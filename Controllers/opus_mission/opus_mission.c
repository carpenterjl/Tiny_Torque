/*
 * opus_mission.c — closed-loop mission controller for the Opus Vector.
 *
 * The manoeuvre: arm, accelerate to 4.5 m/s, hold it for exactly 14.5 m, turn
 * 45 degrees left without lifting, run exactly 7.5 m more, then stop in exactly
 * 1.5 m. No operator input at any point.
 *
 * Three design decisions carry most of the accuracy, and each is defended at
 * its site below:
 *
 *   1. Odometry comes from the UNPOWERED FRONT wheels' tick COUNTERS. Drive
 *      wheels lie under acceleration and the velocity channel is the noisy one.
 *   2. Heading comes primarily from the front-encoder DIFFERENTIAL, not the
 *      gyro. It is geometric, so it cannot drift, and the gyro's sign is learned
 *      at runtime rather than assumed.
 *   3. Every distance target is an ABSOLUTE odometer value and nothing is reset
 *      at a phase boundary, so a phase trigger that fires one tick late shifts
 *      where the car is, never how far it has gone.
 */
#include "opus_mission.h"
#include "mission_cfg.h"

#include <math.h>
#include <string.h>

/* ------------------------------------------------------------------ utils --*/

static float clampf(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

static int finitef_(float v)
{
    /* Deliberately not isfinite(): freestanding MCU libcs vary. A value is
     * finite iff it compares equal to itself and is within a sane magnitude. */
    return (v == v) && (v < 1e18f) && (v > -1e18f);
}

/* Coast-down drag at speed v. Feed-forward only — error here costs accuracy,
 * not stability, because the speed loop trims what is left. */
static float drag_n(float v)
{
    float a = v < 0.0f ? -v : v;
    float f = VE_DRAG_C0 + VE_DRAG_C1 * a + VE_DRAG_C2 * a * a;
    return v >= 0.0f ? f : -f;
}

/* ------------------------------------------------------------- odometry ----
 * The tick register wraps at 65536. At cruise the car covers 889 counts per
 * 10 ms tick against a 32768 half-wrap, a 37x margin, so resolving the wrap by
 * sign is unambiguous. A jump larger than SF_TICK_GLITCH cannot be real motion
 * and is dropped rather than believed.
 */
static float enc_delta_m(OpusEncoder *e, float raw_ticks, int *glitch)
{
    int t = (int)(raw_ticks + (raw_ticks >= 0.0f ? 0.5f : -0.5f));
    int d;

    if (!e->has_prev) { e->prev_ticks = t; e->has_prev = 1; return 0.0f; }

    d = t - e->prev_ticks;
    if (d > VE_ENC_WRAP / 2)       d -= VE_ENC_WRAP;
    else if (d < -VE_ENC_WRAP / 2) d += VE_ENC_WRAP;
    e->prev_ticks = t;

    if (d > SF_TICK_GLITCH || d < -SF_TICK_GLITCH) { if (glitch) *glitch = 1; return 0.0f; }

    /* 2*pi*r/CPR = 0.0506 mm per count at 4096 CPR on a 33 mm wheel. */
    return (float)d * (2.0f * OPUS_PI * VE_WHEEL_R / VE_ENC_CPR);
}

/* Effective odometer: the raw integral plus the integration-bias correction.
 *
 * The encoder integrates with a left-endpoint sum (angle += omega*dt sampled
 * before the step), so over a leg it over-reads by (dt/2)*(v_start - v_now).
 * On the constant-speed legs that is sub-millimetre; across the 4.5 -> 0
 * braking leg it is 22.5 mm, all of it in the direction of stopping SHORT.
 * Correcting it continuously also covers profiles that are not clean ramps.
 */
static double odo_effective(const OpusState *st, float dt)
{
    return st->odo_m + 0.5 * (double)dt * (double)(st->v_meas - st->v_leg_start);
}

/* -------------------------------------------------------------- lifecycle --*/

void opus_init(OpusState *st)
{
    memset(st, 0, sizeof(*st));
    st->phase = OPUS_BOOT;
    st->gyro_sign = 0.0f;          /* unknown until the correlation resolves it */
    st->stop_err_mm = 0.0f;
    st->prev_time_s = -1.0f;

    pid_init(&st->spd_pid, GA_SPD_KP, GA_SPD_KI, 0.0f, -GA_SPD_TRIM, GA_SPD_TRIM);
    pid_init(&st->yaw_pid, GA_YAW_KP, GA_YAW_KI, 0.0f, -GA_YAW_TRIM, GA_YAW_TRIM);
}

static void enter(OpusState *st, OpusPhase p)
{
    st->phase = p;
    st->phase_t = 0.0f;
    /* A leg's integral must not leak into the next one. */
    pid_reset(&st->spd_pid);
}

/* Latch the datum for a newly-started measured leg.
 *
 * The start is the RAW odometer, not the corrected one. The integration-bias
 * correction is defined relative to this leg's own starting speed, so at the
 * boundary itself it is zero by construction; taking odo_effective() here would
 * apply the PREVIOUS leg's correction to the new leg's datum and bake a fixed
 * offset into every subsequent measurement (worth 22 mm at a 0 -> 4.5 m/s
 * boundary, which is most of a leg's error budget). */
static void begin_leg(OpusState *st, float dt)
{
    (void)dt;
    st->leg_start_m = st->odo_m;
    st->v_leg_start = st->v_meas;
}

/* ---------------------------------------------------------------- arming ---
 * A stationary car cannot prove its encoders work: a counter that never moves
 * is indistinguishable from a dead one. So the standing check verifies
 * everything that CAN be checked at rest, and encoder liveness is proven over
 * the first half-metre of the launch instead.
 */
static void arm_checks(OpusState *st, const OpusMeas *m)
{
    int f = 0;

    if (m->motor_count <= 0)                      f |= FA_NO_MOTORS;
    if (!m->enc_valid)                            f |= FA_NO_ENCODERS;
    if (m->motor_vmax > 0.0f && m->motor_vmax < 6.0f) f |= FA_NO_MOTORS;

    /* Battery: present and near full, drawing nothing while stopped. */
    if (m->batt_v > 0.0f && m->batt_v < 0.85f * VE_V_RAIL) f |= FA_BATTERY;

    /* The host is supposed to tick us at a steady 100 Hz. */
    if (!(m->dt > 0.008f && m->dt < 0.012f))      f |= FA_DT;

    /* Specific force at rest is -g, i.e. magnitude ~9.81. */
    if (m->accel_mag > 0.0f && (m->accel_mag < 9.3f || m->accel_mag > 10.3f)) f |= FA_IMU;

    if (!finitef_(m->gyro_y) || !finitef_(m->enc_left_ticks) ||
        !finitef_(m->enc_right_ticks))            f |= FA_NAN;

    st->fault |= f;
}

/* ----------------------------------------------------------- longitudinal --
 * Force-based, not voltage-based. A voltage PID has no handle on tyre force and
 * spends its authority fighting back-EMF; here the loop produces an
 * acceleration, which becomes a force, a current, and only then a voltage
 * through the inverse machine model. At cruise 94 % of the command is
 * feed-forward, so the PID trims rather than drives.
 */
static void longitudinal(OpusState *st, const OpusMeas *m,
                         float v_ref, float a_ff, int allow_brake, OpusCmd *out)
{
    float a_trim, a_cmd, f_req, f_motor, f_fric, i_each, v_cmd, v_lim, rail;
    float brake_duty = -1.0f;   /* < 0 = driving */

    a_trim = pid_update(&st->spd_pid, v_ref, st->v_meas, m->dt);
    a_cmd  = clampf(a_ff + a_trim, -LI_A_MAX, LI_A_MAX);

    /* Total longitudinal force the car needs, coast drag included. Note the
     * EFFECTIVE mass: a fifth of what has to be accelerated is spinning rotor,
     * not translating car, and ignoring it makes every force command small. */
    f_req = VE_MASS_EFF * a_cmd + drag_n(st->v_meas);

    /* Stay inside the pack's live terminal voltage so the host's own sag clamp
     * never silently truncates the command and breaks the inverse model. */
    rail  = (m->batt_v > 1.0f) ? m->batt_v : VE_V_RAIL;
    v_lim = 0.95f * rail;
    if (m->motor_vmax > 0.1f && m->motor_vmax < v_lim) v_lim = m->motor_vmax;

    if (f_req >= 0.0f) {
        /* Driving. The driven tyres run at slip, so slightly more force is
         * commanded at the wheel than reaches the road (a scale on force, not
         * speed — it must NOT hide inside the drag polynomial). */
        f_motor = f_req / VE_TRACTION_EFF;
        f_fric  = 0.0f;
    } else {
        /* Braking. The ESC brakes by shorting the winding: force available is
         * proportional to duty AND speed (back-EMF drives the current), fading
         * to nothing at rest — so the friction brake takes the growing surplus
         * as the car slows. A negative command IS the duty request: the host's
         * ESC state machine reads |v_cmd|/V_rail as brake duty while rolling. */
        float need  = -f_req;
        float v_eff = st->v_meas > 0.05f ? st->v_meas : 0.05f;
        float f_esc_max = VE_ESC_BRAKE_N_PER_MS * v_eff;
        float f_esc;
        if (f_esc_max > VE_ESC_BRAKE_MAX_N) f_esc_max = VE_ESC_BRAKE_MAX_N;
        /* Rear-grip ceiling: the ESC brakes the rear axle only, and that axle
         * unloads under braking. Past ~9 N the rears just slide (measured). */
        if (f_esc_max > EN_ESC_BRAKE_CAP_N) f_esc_max = EN_ESC_BRAKE_CAP_N;
        f_esc = need < f_esc_max ? need : f_esc_max;
        brake_duty = clampf(f_esc / (VE_ESC_BRAKE_N_PER_MS * v_eff), 0.0f, 1.0f);
        f_motor = -f_esc;
        f_fric  = allow_brake ? (need - f_esc) : 0.0f;
    }

    i_each = f_motor / VE_FORCE_PER_AMP_ALL;

    if (brake_duty < 0.0f) {
        /* Drive: inverse machine model, back-EMF feed-forward + IR term. */
        v_cmd = VE_BEMF_V_PER_MS * st->v_meas + VE_R_MOTOR * i_each;
        /* Push past the ESC deadband when force is genuinely wanted, and snap
         * to zero when it is not — small commands must not vanish silently. */
        if (v_cmd > 0.0f && v_cmd < VE_ESC_DEADBAND_V)
            v_cmd = (f_motor > 0.05f) ? VE_ESC_DEADBAND_V : 0.0f;
        else if (v_cmd < 0.0f)
            v_cmd = 0.0f;   /* drive branch never commands reverse at speed */
    } else {
        /* Brake: duty maps linearly onto the negative command range. The ESC
         * normalises duty against its NOMINAL rail (the host divides by the
         * motor's rated voltage), not the live pack voltage — using the live
         * rail here over-brakes by the fresh-pack surcharge. */
        v_cmd = -brake_duty * VE_V_RAIL;
        if (v_cmd < 0.0f && v_cmd > -VE_ESC_DEADBAND_V)
            v_cmd = (f_motor < -0.05f) ? -VE_ESC_DEADBAND_V : 0.0f;
    }

    out->motor_v = clampf(v_cmd, -v_lim, v_lim);
    out->brake   = clampf(f_fric * VE_WHEEL_R / (4.0f * VE_MAX_BRAKE_NM), 0.0f, 1.0f);

    st->i_cmd = i_each;
    st->v_ref = v_ref;
}

/* ---------------------------------------------------------------- lateral --
 * Never open-loop the steer angle. The kinematic angle for this corner is
 * 3.4 degrees, but at 4.5 m/s the tyres need another 3 or so of slip that
 * cannot be known in advance, so feed-forward sets the ballpark and a yaw-rate
 * loop finds the rest. Sign convention here: positive is LEFT throughout, and
 * only the final line flips into the host's positive-is-right command.
 */
static void lateral(OpusState *st, const OpusMeas *m, float psi_dot_ref, OpusCmd *out)
{
    float v_eff = st->v_meas > 0.5f ? st->v_meas : 0.5f;
    float ff_deg = atan2f(VE_WHEELBASE * psi_dot_ref, v_eff) * OPUS_RAD2DEG;
    float trim   = pid_update(&st->yaw_pid, psi_dot_ref, st->psi_dot_f, m->dt);
    float deg    = ff_deg + trim;

    /* Servo observer — the ABI gives no steer feedback, so model it. Used for
     * reporting, not for control. */
    {
        float step = VE_SERVO_SLEW_DPS * m->dt;
        float err  = clampf(deg, -VE_MAX_STEER_DEG, VE_MAX_STEER_DEG) - st->steer_obs_deg;
        st->steer_obs_deg += clampf(err, -step, step);
    }

    out->steer = -clampf(deg / VE_MAX_STEER_DEG, -1.0f, 1.0f);
}

/* Trapezoidal yaw-rate reference. Ramping in and out keeps the inner front
 * wheel loaded, and because the profile is integrated as COMMANDED the turn is
 * exactly 45 degrees by construction — accuracy then depends on how well the
 * yaw loop tracks, not on integrating a noisy measurement up to a threshold. */
static float turn_profile(float t, float *out_rate)
{
    const float rate = LI_A_LAT / MI_V_CRUISE;             /* 0.889 rad/s */
    const float ramp = LI_TURN_RAMP_S;
    const float ramp_area = rate * ramp;                   /* both ramps together */
    const float hold = (MI_TURN_RAD - ramp_area) / rate;

    float r;
    if (t < ramp)                 r = rate * (t / ramp);
    else if (t < ramp + hold)     r = rate;
    else if (t < ramp + hold + ramp) r = rate * (1.0f - (t - ramp - hold) / ramp);
    else                          r = 0.0f;

    *out_rate = r;
    return ramp + hold + ramp;   /* total duration */
}

/* -------------------------------------------------------------------- step --*/

void opus_step(OpusState *st, const OpusMeas *m, OpusCmd *out)
{
    float dt = m->dt;
    float ds_l, ds_r, ds, psi_dot_enc, psi_dot_ref = 0.0f;
    float v_ref = 0.0f, a_ff = 0.0f;
    int   allow_brake = 1, glitch = 0;
    double odo_eff;

    out->motor_v = 0.0f;
    out->steer   = 0.0f;
    out->brake   = 0.0f;

    if (!(dt > 1e-5f) || !finitef_(dt)) { st->cmd = *out; return; }

    /* A host clock that went backwards means the run was restarted underneath
     * us; start over rather than integrating across the discontinuity. */
    if (st->prev_time_s >= 0.0f && m->time_s < st->prev_time_s - 1e-3f) {
        opus_init(st);
    }
    st->prev_time_s = m->time_s;

    /* ---- estimators ---------------------------------------------------- */

    ds_l = enc_delta_m(&st->enc_l, m->enc_left_ticks,  &glitch);
    ds_r = enc_delta_m(&st->enc_r, m->enc_right_ticks, &glitch);
    if (glitch) st->fault |= FA_TICK_GLITCH;

    /* Averaging the two front wheels cancels the track-width term exactly, so
     * no steering-angle compensation is needed on the measured legs. */
    ds = 0.5f * (ds_l + ds_r);
    /* Brake slip is MULTIPLICATIVE on the rolled distance (a braked wheel runs
     * at negative slip proportional to road speed), never additive — an
     * additive term manufactures phantom metres while the car sits at rest
     * with the brake held, which is exactly the ARM rolling check's job to
     * catch (it did — fault 0x40, run R3). */
    ds = ds * (1.0f + CAL_SCALE + CAL_BRAKE * st->cmd.brake);

    st->odo_m += (double)ds;
    st->v_meas = ds / dt;
    st->v_filt += (st->v_meas - st->v_filt) * 0.35f;

    if (m->enc_rear_valid) {
        int g2 = 0;
        float rl = enc_delta_m(&st->enc_rl, m->enc_rl_ticks, &g2);
        float rr = enc_delta_m(&st->enc_rr, m->enc_rr_ticks, &g2);
        st->v_rear = 0.5f * (rl + rr) / dt;
        st->slip_pct = (st->v_meas > 0.3f)
            ? (st->v_rear - st->v_meas) / st->v_meas * 100.0f : 0.0f;
    }

    /* Heading. The differential of two wheels on a known baseline is a purely
     * geometric yaw measurement: no bias, no drift, and one tick of resolution
     * is 0.02 degrees over this turn. The gyro is fused for its high-rate
     * content only, once its sign has been established. */
    psi_dot_enc = (ds_r - ds_l) / (VE_TRACK_FRONT * dt);

    st->gyro_corr += m->gyro_y * psi_dot_enc * dt;
    if (st->gyro_sign == 0.0f && (st->gyro_corr > 0.05f || st->gyro_corr < -0.05f))
        st->gyro_sign = st->gyro_corr > 0.0f ? 1.0f : -1.0f;

    if (st->gyro_sign != 0.0f) {
        float gy = st->gyro_sign * m->gyro_y;
        st->psi_dot = GA_GYRO_ALPHA * gy + (1.0f - GA_GYRO_ALPHA) * psi_dot_enc;
    } else {
        st->psi_dot = psi_dot_enc;   /* adequate on its own; just noisier */
    }
    st->psi += st->psi_dot * dt;
    /* One tick of encoder differential is 0.03 rad/s, so the raw estimate is
     * coarse. Integrate the raw value (quantisation averages out) but close the
     * loops on the filtered one. */
    st->psi_dot_f += (st->psi_dot - st->psi_dot_f) * GA_YAW_FILT;

    odo_eff = odo_effective(st, dt);

    /* ---- standing faults ----------------------------------------------- */

    if (m->accel_mag > SF_ACCEL_ABORT) {
        if (++st->accel_hits >= SF_ACCEL_TICKS) st->fault |= FA_IMPACT;
    } else {
        st->accel_hits = 0;
    }
    if (st->phase >= OPUS_LAUNCH && st->phase <= OPUS_BRAKE &&
        m->tof_front_m > 0.0f && m->tof_front_m < SF_TOF_ABORT_M) {
        if (++st->tof_hits >= SF_TOF_TICKS) st->fault |= FA_OBSTACLE;
    } else {
        st->tof_hits = 0;
    }

    if (st->fault & (FA_IMPACT | FA_OBSTACLE | FA_ENC_DEAD | FA_NO_ENCODERS |
                     FA_NO_MOTORS | FA_NAN))
        st->phase = OPUS_FAULT;

    st->phase_t += dt;

    /* ---- phase machine -------------------------------------------------- */

    switch (st->phase) {

    case OPUS_BOOT:
        enter(st, OPUS_ARM_STATIC);
        break;

    case OPUS_ARM_STATIC:
        /* Brake held first so the car is definitely still, then released so a
         * silent roll-away or a sloped pad shows up as counter movement. */
        out->brake = (st->phase_t < SQ_ARM_BRAKE_S) ? 1.0f : 0.0f;
        if (st->phase_t > SQ_ARM_BRAKE_S) {
            if (ds > 0.002f || ds < -0.002f) st->fault |= FA_ROLLING;
        }
        if (st->phase_t > SQ_ARM_BRAKE_S + SQ_ARM_ROLL_S) {
            arm_checks(st, m);
            if (st->fault) { st->phase = OPUS_FAULT; break; }
            /* Zero the estimators so the mission's datum is the arm point. */
            st->odo_m = 0.0; st->psi = 0.0f; st->psi_ref = 0.0f;
            enter(st, OPUS_ARMED);
        }
        break;

    case OPUS_ARMED:
        out->brake = 1.0f;
        if (st->phase_t > SQ_ARM_DWELL_S) {
            st->v_ref = 0.0f;
            begin_leg(st, dt);
            enter(st, OPUS_LAUNCH);
        }
        break;

    case OPUS_LAUNCH:
        /* Ramp the reference rather than the command: the speed loop then has a
         * feasible target at every instant instead of a step it must saturate
         * against. */
        v_ref = st->v_ref + LI_A_LAUNCH * dt;
        if (v_ref > MI_V_CRUISE) v_ref = MI_V_CRUISE;
        a_ff = (v_ref < MI_V_CRUISE) ? LI_A_LAUNCH : 0.0f;
        psi_dot_ref = clampf(GA_HEAD_KP * (st->psi_ref - st->psi),
                             -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);

        /* The only real encoder-liveness test: both counters must advance once
         * the car is definitely moving. Judged on ACCUMULATED distance over the
         * whole window, not per tick — a per-tick comparison fails on the first
         * bit of steering correction, since at a 0.172 m track a 1 deg/s yaw
         * already separates the two wheels by more than a tenth of their travel.
         * The band is deliberately loose: this detects a dead or unplugged
         * counter, it is not a calibration check. */
        st->live_l += ds_l;
        st->live_r += ds_r;
        if (!st->live_checked && odo_eff > SQ_LIVENESS_M) {
            st->live_checked = 1;
            float lo = st->live_l < st->live_r ? st->live_l : st->live_r;
            float hi = st->live_l < st->live_r ? st->live_r : st->live_l;
            if (lo <= 0.01f || lo < 0.60f * hi) st->fault |= FA_ENC_DEAD;
        }

        if (st->v_meas > MI_V_CRUISE - 0.05f) st->settle_t += dt; else st->settle_t = 0.0f;
        if (st->settle_t > SQ_SETTLE_S) {
            begin_leg(st, dt);
            enter(st, OPUS_CRUISE_A);
        }
        break;

    case OPUS_CRUISE_A:
        v_ref = MI_V_CRUISE;
        psi_dot_ref = clampf(GA_HEAD_KP * (st->psi_ref - st->psi),
                             -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);
        /* Half-tick lead. A leg boundary can only ever land on a control tick,
         * and at 4.5 m/s a tick is 45 mm — so testing the bare threshold always
         * overshoots, by 22 mm on average. Testing the midpoint of the coming
         * tick instead centres the quantisation on zero. */
        if (odo_eff - st->leg_start_m + 0.5 * st->v_meas * dt >= MI_LEG_A_M) {
            st->leg_a_actual = (float)(odo_eff - st->leg_start_m);
            st->turn_t = 0.0f;
            st->turn_cmd_rad = 0.0f;
            enter(st, OPUS_TURN);
        }
        break;

    case OPUS_TURN: {
        float total = turn_profile(st->turn_t, &psi_dot_ref);
        v_ref = MI_V_CRUISE;                 /* the brief is explicit: do not slow down */
        st->turn_t += dt;
        st->turn_cmd_rad += psi_dot_ref * dt;
        /* Close the heading loop around the profile. Without this the turn is
         * open-loop in heading and keeps whatever the yaw-rate loop failed to
         * deliver — measured as 3.6 deg short on the first full run. The
         * feed-forward still does the work; this only repays the shortfall, and
         * because it is driven by turn_cmd_rad (the INTEGRAL of the commanded
         * profile, not a step to the final angle) it never asks for more rate
         * than the lateral-acceleration budget allows. */
        {
            float herr = st->turn_cmd_rad - st->psi;
            psi_dot_ref += clampf(GA_TURN_KP * herr,
                                  -GA_TURN_MAX_TRIM, GA_TURN_MAX_TRIM);
        }
        /* The settle test needs the heading to have ARRIVED as well as the rate
         * to have died — a loose rate band on its own is satisfied by simply
         * stopping the turn early. The timeout is the honest escape hatch: exit
         * anyway and let the reported turn_actual_deg carry the bad news. */
        if ((st->turn_t >= total + 0.15f &&
             st->psi_dot_f < 0.05f && st->psi_dot_f > -0.05f &&
             fabsf(st->turn_cmd_rad - st->psi) < GA_TURN_CLOSE_RAD) ||
            st->turn_t >= total + GA_TURN_MAX_EXTRA) {
            /* Straightened out. This instant DEFINES the datum for the next
             * leg, so the 7.5 m is exact by construction; the residual heading
             * error is reported, not propagated. */
            st->turn_actual_deg = st->psi * OPUS_RAD2DEG;
            st->psi_ref = st->psi;
            begin_leg(st, dt);
            st->stop_target_m = st->leg_start_m + MI_STOP_FROM_EXIT;
            enter(st, OPUS_CRUISE_B);
        }
        break;
    }

    case OPUS_CRUISE_B:
        v_ref = MI_V_CRUISE;
        psi_dot_ref = clampf(GA_HEAD_KP * (st->psi_ref - st->psi),
                             -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);
        if (odo_eff - st->leg_start_m + 0.5 * st->v_meas * dt >= MI_LEG_B_M) {
            st->leg_b_actual = (float)(odo_eff - st->leg_start_m);
            st->v_leg_start = st->v_meas;    /* datum for the trapezoid correction */
            enter(st, OPUS_BRAKE);
        }
        break;

    case OPUS_BRAKE: {
        /* Parameterise the speed profile by REMAINING DISTANCE, not by time:
         * that makes it self-correcting against modelling error. The lead term
         * removes the transient lag of the loop's own dead time — 20 ms at
         * 4.5 m/s is 90 mm of prediction. */
        float s_rem = (float)(st->stop_target_m - odo_eff) - st->v_meas * EN_DEAD_TIME_S;
        if (s_rem < 0.0f) s_rem = 0.0f;
        v_ref = sqrtf(2.0f * LI_A_BRAKE * s_rem);
        if (v_ref > MI_V_CRUISE) v_ref = MI_V_CRUISE;
        a_ff = -LI_A_BRAKE;
        psi_dot_ref = clampf(GA_HEAD_KP * (st->psi_ref - st->psi),
                             -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);

        /* The friction brake acts on ALL FOUR wheels, so it slips the very
         * wheels the odometer reads. Give it up for the last 40 mm and coast
         * the rest on motor torque alone, where the front slip falls to
         * essentially nothing. */
        /* Speed gate as well as distance. CREEP releases the friction brake and
         * closes on position with a 0.3 m/s ceiling, which is right for the last
         * 40 mm and catastrophic at 3 m/s — the first completed run ran out of
         * distance while still doing 2.9 m/s, handed over to CREEP, and coasted
         * 1.3 m past the mark. If the distance is gone but the speed is not,
         * stay in BRAKE: s_rem clamps to zero, v_ref goes to zero, and the loop
         * asks for everything it has. */
        if ((float)(st->stop_target_m - odo_eff) < EN_CREEP_M &&
            st->v_filt < EN_CREEP_V * 2.0f) {
            st->brake_actual = (float)(odo_eff - (st->stop_target_m - MI_BRAKE_M));
            enter(st, OPUS_CREEP);
        }
        break;
    }

    case OPUS_CREEP: {
        /* Below 40 mm the sqrt profile's gain runs away (dv/ds = -a/v), so hand
         * over to a linear position loop. Position resolves to 0.05 mm here;
         * velocity resolves to only 5 mm/s, which is why this closes on
         * distance and not on speed. */
        float s_rem = (float)(st->stop_target_m - odo_eff);
        allow_brake = 0;
        v_ref = clampf(EN_CREEP_KV * s_rem, -EN_CREEP_V, EN_CREEP_V);
        if (s_rem < EN_DONE_M && st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) {
            st->hold_t = 0.0f;
            enter(st, OPUS_HOLD);
        }
        break;
    }

    case OPUS_HOLD:
        out->brake = 1.0f;
        /* Test the filtered speed, not the raw tick delta: at a few mm/s the
         * counter alternates between one and two ticks per period, so an
         * exact-zero-ticks test can never be satisfied for a whole second. */
        if (st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) st->hold_t += dt;
        else st->hold_t = 0.0f;
        if (st->hold_t > EN_HOLD_S) {
            st->stop_err_mm = (float)((odo_eff - st->stop_target_m) * 1000.0);
            enter(st, OPUS_DONE);
        }
        break;

    case OPUS_DONE:
        out->brake = 1.0f;
        break;

    case OPUS_FAULT:
    default:
        out->brake = 1.0f;
        break;
    }

    /* Distance still owed on whichever leg is being measured. */
    switch (st->phase) {
    case OPUS_CRUISE_A: st->leg_rem = MI_LEG_A_M - (float)(odo_eff - st->leg_start_m); break;
    case OPUS_CRUISE_B: st->leg_rem = MI_LEG_B_M - (float)(odo_eff - st->leg_start_m); break;
    case OPUS_BRAKE:
    case OPUS_CREEP:    st->leg_rem = (float)(st->stop_target_m - odo_eff); break;
    default:            st->leg_rem = 0.0f; break;
    }

    /* ---- actuate --------------------------------------------------------- */

    if (st->phase >= OPUS_LAUNCH && st->phase <= OPUS_CREEP) {
        longitudinal(st, m, v_ref, a_ff, allow_brake, out);
        lateral(st, m, psi_dot_ref, out);
    } else {
        st->v_ref = 0.0f;
        st->i_cmd = 0.0f;
        pid_reset(&st->spd_pid);
        pid_reset(&st->yaw_pid);
    }

    /* Live stop error while it still means something. */
    if (st->phase == OPUS_BRAKE || st->phase == OPUS_CREEP)
        st->stop_err_mm = (float)((odo_eff - st->stop_target_m) * 1000.0);

    st->cmd = *out;
}
