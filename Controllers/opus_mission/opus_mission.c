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
 *      gyro. It is geometric, so it cannot drift. The gyro (FLU, so a left turn
 *      is positive by definition) supplies the high-rate content.
 *   3. Every distance target is an ABSOLUTE odometer value and nothing is reset
 *      at a phase boundary, so a phase trigger that fires one tick late shifts
 *      where the car is, never how far it has gone.
 *
 * Firmware hygiene (FW-04/05): float32 only, every literal suffixed, every
 * threshold a time or a rate, dt measured from timestamps.
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

/* First-order low-pass blend for a time constant, so a filter means the same
 * thing at any loop rate. */
static float lp_alpha(float dt, float tau)
{
    return tau > 0.0f ? 1.0f - expf(-dt / tau) : 1.0f;
}

static int popcount4(uint32_t m)
{
    return (int)((m & 1u) + ((m >> 1) & 1u) + ((m >> 2) & 1u) + ((m >> 3) & 1u));
}

/* Coast-down drag at speed v. Feed-forward only — error here costs accuracy,
 * not stability, because the speed loop trims what is left. */
static float drag_n(const TtParams *p, float v)
{
    float a = v < 0.0f ? -v : v;
    float f = p->drag_c0 + p->drag_c1 * a + p->drag_c2 * a * a;
    return v >= 0.0f ? f : -f;
}

/* ------------------------------------------------------------- odometry ----
 * Encoder counts arrive cumulative (the target unwraps the hardware register),
 * so a delta is one int32 subtraction. A jump faster than SF_GLITCH_CPS
 * cannot be real motion and is dropped rather than believed.
 */
static int32_t enc_delta(OpusState *st, const TtMeas *m, int w, float dt, int *glitch)
{
    int32_t d;
    if (!m->enc[w].st.valid) return 0;
    if (!st->enc_has_prev[w]) {
        st->enc_prev[w] = m->enc[w].count;
        st->enc_has_prev[w] = 1;
        return 0;
    }
    d = (int32_t)((uint32_t)m->enc[w].count - (uint32_t)st->enc_prev[w]);
    st->enc_prev[w] = m->enc[w].count;
    if ((float)(d < 0 ? -d : d) > SF_GLITCH_CPS * dt) { if (glitch) *glitch = 1; return 0; }
    return d;
}

float opus_odo_m(const OpusState *st)
{
    const TtParams *p = st->p;
    return 0.5f * ((float)st->odo_cnt_l * tt_m_per_count(p, st->odo_l) +
                   (float)st->odo_cnt_r * tt_m_per_count(p, st->odo_r)) + st->odo_corr_m;
}

/* The odometer the mission measures legs with. (Until SEN-02 this added a
 * correction for the sim encoder's integration bias — 22.5 mm over the
 * braking leg. The sim now counts the angle the physics integrated, as a
 * real encoder does, so there is nothing left to correct.) */
static float odo_effective(const OpusState *st)
{
    return opus_odo_m(st);
}

/* -------------------------------------------------------------- lifecycle --*/

void opus_init(OpusState *st, const TtParams *p, float rate_hz)
{
    memset(st, 0, sizeof(*st));
    st->p = p;
    st->rate_hz = rate_hz;
    /* Odometry and the heading baseline: the front pair if both are odometry
     * wheels (the Opus case), else the rear pair. */
    if ((p->odo_wheel_mask & 0x3u) == 0x3u) { st->odo_l = TT_FL; st->odo_r = TT_FR; }
    else                                    { st->odo_l = TT_RL; st->odo_r = TT_RR; }
    opus_reset(st);
}

void opus_reset(OpusState *st)
{
    const TtParams *p = st->p;
    float rate = st->rate_hz;
    int l = st->odo_l, r = st->odo_r;

    memset(st, 0, sizeof(*st));
    st->p = p;
    st->rate_hz = rate;
    st->odo_l = l;
    st->odo_r = r;
    st->phase = OPUS_BOOT;
    st->drive_ok = 1;

    pid_init(&st->spd_pid, GA_SPD_KP, GA_SPD_KI, 0.0f, -GA_SPD_TRIM, GA_SPD_TRIM);
    pid_init(&st->yaw_pid, GA_YAW_KP, GA_YAW_KI, 0.0f, -GA_YAW_TRIM, GA_YAW_TRIM);
    tt_alloc_reset(&st->alloc);
}

static void enter(OpusState *st, OpusPhase ph)
{
    st->phase = ph;
    st->phase_t = 0.0f;
    /* A leg's integral must not leak into the next one. */
    pid_reset(&st->spd_pid);
}

/* Latch the datum for a newly-started measured leg. */
static void begin_leg(OpusState *st)
{
    st->leg_start_m = odo_effective(st);
}

/* ---------------------------------------------------------------- arming ---
 * A stationary car cannot prove its encoders work: a counter that never moves
 * is indistinguishable from a dead one. So the standing check verifies
 * everything that CAN be checked at rest, and encoder liveness is proven over
 * the first half-metre of the launch instead.
 */
static void arm_checks(OpusState *st, const TtMeas *m)
{
    const TtParams *p = st->p;
    int f = 0, w;

    if (tt_params_validate(p) != 0) f |= FA_PARAMS;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if ((p->driven_mask & (1u << w)) && !m->drv[w].st.valid)    f |= FA_NO_MOTORS;
        if ((p->odo_wheel_mask & (1u << w)) && !m->enc[w].st.valid) f |= FA_NO_ENCODERS;
    }

    /* Battery: present and near full, drawing nothing while stopped. */
    if (m->batt.st.valid && m->batt.v > 0.0f && m->batt.v < 0.85f * p->v_rail_nom) f |= FA_BATTERY;

    /* The scheduler must deliver the rate it promised at init. */
    if (st->rate_hz > 0.0f) {
        float nom = 1.0f / st->rate_hz;
        if (!(m->dt_s > nom * (1.0f - SF_DT_TOL) && m->dt_s < nom * (1.0f + SF_DT_TOL))) f |= FA_DT;
    }

    /* Specific force at rest is +g up, i.e. magnitude ~9.81. */
    if (m->imu.st.valid) {
        const float *a = m->imu.accel;
        float mag = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (mag < 9.3f || mag > 10.3f) f |= FA_IMU;
        if (!finitef_(m->imu.gyro[2])) f |= FA_NAN;
    }

    st->fault |= f;
}

/* ----------------------------------------------------------- longitudinal --
 * Force-based. The loop produces an acceleration, which becomes a road force
 * and then a torque per driven wheel. Turning torque into the drive's own
 * units (Iq for an FOC drive, volts for the sim's brushed ESC) is the
 * target's job, so nothing here knows what kind of motor is fitted. At cruise
 * 94 % of the command is feed-forward, so the PID trims rather than drives.
 */
static void longitudinal(OpusState *st, const TtMeas *m,
                         float v_ref, float a_ff, int allow_brake, TtCmd *out)
{
    const TtParams *p = st->p;
    int   n_driven = popcount4(p->driven_mask), w;
    float a_trim, a_cmd, f_req, f_motor, f_fric, t_each;

    a_trim = pid_update(&st->spd_pid, v_ref, st->v_meas, m->dt_s);
    a_cmd  = clampf(a_ff + a_trim, -LI_A_MAX, LI_A_MAX);

    /* Total longitudinal force the car needs, coast drag included. Note the
     * EFFECTIVE mass: a fifth of what has to be accelerated is spinning rotor,
     * not translating car, and ignoring it makes every force command small. */
    f_req = p->mass_eff_kg * a_cmd + drag_n(p, st->v_meas);

    if (f_req >= 0.0f) {
        /* Driving. The driven tyres run at slip, so slightly more force is
         * commanded at the wheel than reaches the road (a scale on force, not
         * speed — it must NOT hide inside the drag polynomial). */
        f_motor = f_req / p->traction_eff;
        f_fric  = 0.0f;
    } else {
        /* Braking. The motors take what they can and the friction brake takes
         * the rest. What they can is the car's regen capability: for this car's
         * hobby ESC (shorted winding) proportional to speed, fading to nothing
         * at rest — so the friction brake takes the growing surplus as the car
         * slows — and capped by the grip of the driven axle, which unloads
         * under braking. An FOC car states a flat current limit instead. */
        float need  = -f_req;
        float v_eff = st->v_meas > 0.05f ? st->v_meas : 0.05f;
        float cap   = p->regen_n_per_ms > 0.0f ? p->regen_n_per_ms * v_eff : need;
        if (p->regen_max_n > 0.0f && cap > p->regen_max_n) cap = p->regen_max_n;
        if (p->regen_grip_cap_n > 0.0f && cap > p->regen_grip_cap_n) cap = p->regen_grip_cap_n;
        f_motor = -(need < cap ? need : cap);
        f_fric  = allow_brake ? (need + f_motor) : 0.0f;
    }

    /* The allocator turns the force into wheel torques: front/rear by load,
     * left/right as an open differential, per-wheel limits, and traction
     * control / ABS where the parameters enable them (FW-06). */
    {
        TtVehReq req;
        TtAllocIn ain;
        req.fx_n = f_motor;
        req.mz_nm = 0.0f;
        req.steer_rad = 0.0f;
        ain.v_mps = st->v_meas;
        ain.yaw_rate = st->psi_dot_f;
        ain.ax_mps2 = a_cmd;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            ain.wheel_omega[w] = st->wheel_omega[w];
            ain.omega_valid[w] = st->wheel_omega_ok[w];
        }
        tt_alloc_step(&st->alloc, p, &req, &ain, m->dt_s, out);
    }
    t_each = n_driven > 0 ? f_motor * p->wheel_radius_m / (float)n_driven : 0.0f;

    /* The friction brake acts on all four wheels. */
    out->brake_01 = p->brake_max_nm > 0.0f
        ? clampf(f_fric * p->wheel_radius_m / (4.0f * p->brake_max_nm), 0.0f, 1.0f) : 0.0f;

    st->t_cmd_nm = t_each;
    st->a_cmd = a_cmd;
    st->v_ref = v_ref;
}

/* ---------------------------------------------------------------- lateral --
 * Never open-loop the steer angle. The kinematic angle for this corner is
 * 3.4 degrees, but at 4.5 m/s the tyres need another 3 or so of slip that
 * cannot be known in advance, so feed-forward sets the ballpark and a yaw-rate
 * loop finds the rest. Positive is LEFT throughout, command included.
 */
static void lateral(OpusState *st, const TtMeas *m, float psi_dot_ref, TtCmd *out)
{
    const TtParams *p = st->p;
    float v_eff  = st->v_meas > 0.5f ? st->v_meas : 0.5f;
    float ff_deg = atan2f(p->wheelbase_m * psi_dot_ref, v_eff) * OPUS_RAD2DEG;
    float trim   = pid_update(&st->yaw_pid, psi_dot_ref, st->psi_dot_f, m->dt_s);
    float rad    = clampf((ff_deg + trim) * OPUS_DEG2RAD, -p->max_steer_rad, p->max_steer_rad);

    /* Servo observer — a hobby servo gives no position, so model it. Used for
     * reporting, not for control. */
    {
        float step = p->servo_slew_rad_s * m->dt_s;
        st->steer_obs_rad += clampf(rad - st->steer_obs_rad, -step, step);
    }

    out->steer_rad = rad;
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

static float heading_hold(const OpusState *st)
{
    return clampf(GA_HEAD_KP * (st->psi_ref - st->psi), -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);
}

static void fill_log(OpusState *st, const TtMeas *m)
{
    OpusLog *g = &st->log;
    g->state        = (float)st->phase;
    g->fault        = (float)st->fault;
    g->odo_m        = opus_odo_m(st);
    g->leg_rem_m    = st->leg_rem;
    g->v_meas       = st->v_meas;
    g->target_speed = st->v_ref;
    g->v_err        = st->v_ref - st->v_meas;
    g->yaw_deg      = st->psi * OPUS_RAD2DEG;
    g->yaw_rate     = st->psi_dot;
    g->steer_cmd    = st->cmd.steer_rad;
    g->motor_v      = 0.0f;   /* the drive's business: filled by the target */
    g->i_cmd        = 0.0f;   /* ditto */
    g->brake_cmd    = st->cmd.brake_01;
    g->batt_v       = m->batt.st.valid ? m->batt.v : -1.0f;
    g->slip_pct     = st->slip_pct;
    g->stop_err_mm  = st->stop_err_mm;
}

/* -------------------------------------------------------------------- step --*/

void opus_step(OpusState *st, const TtMeas *m, TtCmd *out)
{
    const TtParams *p = st->p;
    float dt = m->dt_s;
    float ds_l, ds_r, ds, psi_dot_enc, psi_dot_ref = 0.0f;
    float v_ref = 0.0f, a_ff = 0.0f, odo_eff, mpc_l, mpc_r;
    int32_t dl, dr;
    int   allow_brake = 1, glitch = 0, w;

    memset(out, 0, sizeof(*out));
    out->t_us = m->now_us;

    if (!(dt > 1e-5f) || !finitef_(dt)) { st->cmd = *out; fill_log(st, m); return; }

    /* A clock that went backwards means the run was restarted underneath us
     * without a reset; start over rather than integrating across it. */
    if (st->has_prev_us && (int32_t)(m->now_us - st->prev_us) < -1000)
        opus_reset(st);
    st->prev_us = m->now_us;
    st->has_prev_us = 1;

    /* ---- estimators ---------------------------------------------------- */

    mpc_l = tt_m_per_count(p, st->odo_l);
    mpc_r = tt_m_per_count(p, st->odo_r);
    {
        int gl = 0, gr = 0;
        dl = enc_delta(st, m, st->odo_l, dt, &gl);
        dr = enc_delta(st, m, st->odo_r, dt, &gr);
        glitch = gl || gr;
        /* A rejected delta is a MISSING one, not a zero: a zero beside the
         * other wheel's real travel reads as a sharp yaw (a 4.5 cm step
         * turned the heading 3.6 deg, VAL-11). Take the other wheel's travel
         * for it; the heading below then leans on the gyro for this tick. */
        if (gl && !gr && mpc_l > 0.0f) dl = (int32_t)lroundf((float)dr * mpc_r / mpc_l);
        else if (gr && !gl && mpc_r > 0.0f) dr = (int32_t)lroundf((float)dl * mpc_l / mpc_r);
    }
    if (glitch) st->fault |= FA_TICK_GLITCH;
    ds_l = (float)dl * mpc_l;
    ds_r = (float)dr * mpc_r;

    /* A DRIVEN odometry wheel slips by its own tyre force: kappa = F / (C_k/F_z
     * * F_z), so the encoder reads (1 + kappa) of the ground. Undo it with the
     * torque this firmware commanded last tick and the wheel's load from the
     * weight split plus longitudinal and lateral transfer. The tyre carries
     * the torque LESS what the wheel itself loses (rolling resistance,
     * bearings, motor friction — the c0 + c1*v of the drag model, shared by
     * four wheels); only aero is the body's. At cruise that leaves almost
     * nothing, which is why this matters under braking and launch. (A
     * friction-braked wheel is the separate cal_brake term below.) */
    if (p->slip_stiffness > 0.0f) {
        const float g = 9.80665f;
        float tr = (st->odo_l < 2) ? p->track_front_m : p->track_rear_m;
        float shf = p->front_weight_frac - (p->wheelbase_m > 0.0f ? p->cg_height_m / p->wheelbase_m : 0.0f) * st->a_cmd / g;
        float sh = (st->odo_l < 2) ? shf : 1.0f - shf;
        float ay = st->v_meas * st->psi_dot_f;                 /* + = left */
        float dz = tr > 0.0f ? ay * p->cg_height_m / tr : 0.0f; /* per g-scaled mass */
        float fz_l = p->mass_kg * sh * (0.5f * g - dz);
        float fz_r = p->mass_kg * sh * (0.5f * g + dz);
        float va = st->v_meas < 0.0f ? -st->v_meas : st->v_meas;
        float own = (p->drag_c0 + p->drag_c1 * va) / (float)TT_MAX_WHEELS;
        float k_l = 0.0f, k_r = 0.0f, c_l, c_r;
        if (st->v_meas < 0.0f) own = -own;
        if ((p->driven_mask & (1u << st->odo_l)) && fz_l > 0.1f && va > 0.05f)
            k_l = (st->cmd.wheel_torque_nm[st->odo_l] / p->wheel_radius_m - own) / (p->slip_stiffness * fz_l);
        if ((p->driven_mask & (1u << st->odo_r)) && fz_r > 0.1f && va > 0.05f)
            k_r = (st->cmd.wheel_torque_nm[st->odo_r] / p->wheel_radius_m - own) / (p->slip_stiffness * fz_r);
        k_l = clampf(k_l, -0.3f, 0.3f);
        k_r = clampf(k_r, -0.3f, 0.3f);
        c_l = -ds_l * k_l / (1.0f + k_l);
        c_r = -ds_r * k_r / (1.0f + k_r);
        st->kappa_odo = 0.5f * (k_l + k_r);
        ds_l += c_l;
        ds_r += c_r;
        st->odo_corr_m += 0.5f * (c_l + c_r);
    }

    /* Averaging the two odometry wheels cancels the track-width term exactly,
     * so no steering-angle compensation is needed on the measured legs. */
    ds = 0.5f * (ds_l + ds_r);
    st->odo_cnt_l += dl;
    st->odo_cnt_r += dr;
    /* Brake slip is MULTIPLICATIVE on the rolled distance (a braked wheel runs
     * at negative slip proportional to road speed), never additive — an
     * additive term manufactures phantom metres while the car sits at rest
     * with the brake held, which is exactly the ARM rolling check's job to
     * catch (it did — fault 0x40, run R3). */
    {
        float k = p->cal_scale + p->cal_brake * st->cmd.brake_01;
        st->odo_corr_m += ds * k;
        ds *= 1.0f + k;
    }

    st->v_meas = ds / dt;
    st->v_filt += (st->v_meas - st->v_filt) * lp_alpha(dt, EN_VFILT_TAU_S);

    /* Driven-wheel speed, for the slip diagnostic only. */
    {
        float sum = 0.0f;
        int   n = 0;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            /* An odometry wheel was already differenced this tick; doing it
             * again would read zero. */
            if (w == st->odo_l || w == st->odo_r) continue;
            if ((p->driven_mask & (1u << w)) && m->enc[w].st.valid) {
                int g2 = 0;
                sum += (float)enc_delta(st, m, w, dt, &g2) * tt_m_per_count(p, w);
                n++;
            }
        }
        if (n > 0) {
            st->v_rear = sum / (float)n / dt;
            st->slip_pct = (st->v_meas > 0.3f)
                ? (st->v_rear - st->v_meas) / st->v_meas * 100.0f : 0.0f;
        }
    }

    /* Heading. The differential of two wheels on a known baseline is a purely
     * geometric yaw measurement: no bias, no drift, and one tick of resolution
     * is 0.02 degrees over this turn. The gyro is fused for its high-rate
     * content; in FLU a left turn is positive, so there is no sign to learn. */
    psi_dot_enc = (ds_r - ds_l) / (p->track_front_m * dt);
    if (glitch && m->imu.st.valid && finitef_(m->imu.gyro[2]))
        st->psi_dot = m->imu.gyro[2];   /* no encoder differential this tick */
    else if (m->imu.st.valid && finitef_(m->imu.gyro[2]))
        st->psi_dot = GA_GYRO_ALPHA * m->imu.gyro[2] + (1.0f - GA_GYRO_ALPHA) * psi_dot_enc;
    else
        st->psi_dot = psi_dot_enc;   /* adequate on its own; just noisier */
    st->psi += st->psi_dot * dt;
    /* One tick of encoder differential is 0.03 rad/s, so the raw estimate is
     * coarse. Integrate the raw value (quantisation averages out) but close the
     * loops on the filtered one. */
    st->psi_dot_f += (st->psi_dot - st->psi_dot_f) * lp_alpha(dt, GA_YAW_TAU_S);

    odo_eff = odo_effective(st);

    /* Wheel speeds for the slip limiter, from the drives' own speed
     * estimate (an FOC driver's PLL; the sim adapter's encoder rate). */
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float g = p->gear[w];
        st->wheel_omega_ok[w] = (uint8_t)(m->drv[w].st.valid && g > 0.0f);
        st->wheel_omega[w] = st->wheel_omega_ok[w] ? m->drv[w].omega_m / g : 0.0f;
    }

    /* ---- standing faults ----------------------------------------------- */

    if (m->imu.st.valid) {
        const float *a = m->imu.accel;
        float mag = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (mag > SF_ACCEL_ABORT) {
            st->accel_t += dt;
            if (st->accel_t >= SF_ACCEL_S - 1e-4f) st->fault |= FA_IMPACT;
        } else {
            st->accel_t = 0.0f;
        }
    }
    {
        float tof = (m->tof.st.valid && m->tof.zones > 0 && m->tof.status[0] == 0)
                  ? m->tof.range_m[0] : 1e6f;
        if (st->phase >= OPUS_LAUNCH && st->phase <= OPUS_BRAKE &&
            tof > 0.0f && tof < SF_TOF_ABORT_M) {
            st->tof_t += dt;
            if (st->tof_t >= SF_TOF_S - 1e-4f) st->fault |= FA_OBSTACLE;
        } else {
            st->tof_t = 0.0f;
        }
    }

    if (st->fault & (FA_IMPACT | FA_OBSTACLE | FA_ENC_DEAD | FA_NO_ENCODERS |
                     FA_NO_MOTORS | FA_NAN | FA_PARAMS | FA_SAFETY))
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
        out->brake_01 = (st->phase_t < SQ_ARM_BRAKE_S) ? 1.0f : 0.0f;
        if (st->phase_t > SQ_ARM_BRAKE_S) {
            if (ds > 0.002f || ds < -0.002f) st->fault |= FA_ROLLING;
        }
        if (st->phase_t > SQ_ARM_BRAKE_S + SQ_ARM_ROLL_S) {
            arm_checks(st, m);
            if (st->fault) { st->phase = OPUS_FAULT; break; }
            /* Zero the estimators so the mission's datum is the arm point. */
            st->odo_cnt_l = 0; st->odo_cnt_r = 0; st->odo_corr_m = 0.0f;
            st->psi = 0.0f; st->psi_ref = 0.0f;
            enter(st, OPUS_ARMED);
        }
        break;

    case OPUS_ARMED:
        out->brake_01 = 1.0f;
        /* Launch only once the safety layer has armed the drives. */
        if (st->phase_t > SQ_ARM_DWELL_S && st->drive_ok) {
            st->v_ref = 0.0f;
            begin_leg(st);
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
        psi_dot_ref = heading_hold(st);

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
            float lo = st->live_l < st->live_r ? st->live_l : st->live_r;
            float hi = st->live_l < st->live_r ? st->live_r : st->live_l;
            st->live_checked = 1;
            if (lo <= 0.01f || lo < 0.60f * hi) st->fault |= FA_ENC_DEAD;
        }

        if (st->v_meas > MI_V_CRUISE - 0.05f) st->settle_t += dt; else st->settle_t = 0.0f;
        if (st->settle_t > SQ_SETTLE_S) {
            begin_leg(st);
            enter(st, OPUS_CRUISE_A);
        }
        break;

    case OPUS_CRUISE_A:
        v_ref = MI_V_CRUISE;
        psi_dot_ref = heading_hold(st);
        /* Half-tick lead. A leg boundary can only ever land on a control tick,
         * and at 4.5 m/s a tick is 45 mm — so testing the bare threshold always
         * overshoots, by 22 mm on average. Testing the midpoint of the coming
         * tick instead centres the quantisation on zero. */
        if (odo_eff - st->leg_start_m + 0.5f * st->v_meas * dt >= MI_LEG_A_M) {
            st->leg_a_actual = odo_eff - st->leg_start_m;
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
            psi_dot_ref += clampf(GA_TURN_KP * herr, -GA_TURN_MAX_TRIM, GA_TURN_MAX_TRIM);
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
            begin_leg(st);
            st->stop_target_m = st->leg_start_m + MI_STOP_FROM_EXIT;
            enter(st, OPUS_CRUISE_B);
        }
        break;
    }

    case OPUS_CRUISE_B:
        v_ref = MI_V_CRUISE;
        psi_dot_ref = heading_hold(st);
        if (odo_eff - st->leg_start_m + 0.5f * st->v_meas * dt >= MI_LEG_B_M) {
            st->leg_b_actual = odo_eff - st->leg_start_m;
            enter(st, OPUS_BRAKE);
        }
        break;

    case OPUS_BRAKE: {
        /* Parameterise the speed profile by REMAINING DISTANCE, not by time:
         * that makes it self-correcting against modelling error. The lead term
         * removes the transient lag of the loop's own dead time — 20 ms at
         * 4.5 m/s is 90 mm of prediction. */
        float s_rem = (st->stop_target_m - odo_eff) - st->v_meas * EN_DEAD_TIME_S;
        if (s_rem < 0.0f) s_rem = 0.0f;
        v_ref = sqrtf(2.0f * LI_A_BRAKE * s_rem);
        if (v_ref > MI_V_CRUISE) v_ref = MI_V_CRUISE;
        a_ff = -LI_A_BRAKE;
        psi_dot_ref = heading_hold(st);

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
        if (st->stop_target_m - odo_eff < EN_CREEP_M && st->v_filt < EN_CREEP_V * 2.0f) {
            st->brake_actual = odo_eff - (st->stop_target_m - MI_BRAKE_M);
            enter(st, OPUS_CREEP);
        }
        break;
    }

    case OPUS_CREEP: {
        /* Below 40 mm the sqrt profile's gain runs away (dv/ds = -a/v), so hand
         * over to a linear position loop. Position resolves to 0.05 mm here;
         * velocity resolves to only 5 mm/s, which is why this closes on
         * distance and not on speed. */
        float s_rem = st->stop_target_m - odo_eff;
        allow_brake = 0;
        v_ref = clampf(EN_CREEP_KV * s_rem, -EN_CREEP_V, EN_CREEP_V);
        if (s_rem < EN_DONE_M && st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) {
            st->hold_t = 0.0f;
            enter(st, OPUS_HOLD);
        }
        break;
    }

    case OPUS_HOLD:
        out->brake_01 = 1.0f;
        /* Test the filtered speed, not the raw tick delta: at a few mm/s the
         * counter alternates between one and two ticks per period, so an
         * exact-zero-ticks test can never be satisfied for a whole second. */
        if (st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) st->hold_t += dt;
        else st->hold_t = 0.0f;
        if (st->hold_t > EN_HOLD_S) {
            st->stop_err_mm = (odo_eff - st->stop_target_m) * 1000.0f;
            enter(st, OPUS_DONE);
        }
        break;

    case OPUS_DONE:
        out->brake_01 = 1.0f;
        break;

    case OPUS_FAULT:
    default:
        out->brake_01 = 1.0f;
        break;
    }

    /* Distance still owed on whichever leg is being measured. */
    switch (st->phase) {
    case OPUS_CRUISE_A: st->leg_rem = MI_LEG_A_M - (odo_eff - st->leg_start_m); break;
    case OPUS_CRUISE_B: st->leg_rem = MI_LEG_B_M - (odo_eff - st->leg_start_m); break;
    case OPUS_BRAKE:
    case OPUS_CREEP:    st->leg_rem = st->stop_target_m - odo_eff; break;
    default:            st->leg_rem = 0.0f; break;
    }

    /* ---- actuate --------------------------------------------------------- */

    if (st->phase >= OPUS_LAUNCH && st->phase <= OPUS_CREEP) {
        longitudinal(st, m, v_ref, a_ff, allow_brake, out);
        lateral(st, m, psi_dot_ref, out);
        out->arm = 1;
    } else {
        st->v_ref = 0.0f;
        st->t_cmd_nm = 0.0f;
        st->a_cmd = 0.0f;
        pid_reset(&st->spd_pid);
        pid_reset(&st->yaw_pid);
        out->arm = (st->phase == OPUS_ARMED || st->phase == OPUS_HOLD) ? 1 : 0;
    }

    /* Live stop error while it still means something. */
    if (st->phase == OPUS_BRAKE || st->phase == OPUS_CREEP)
        st->stop_err_mm = (odo_eff - st->stop_target_m) * 1000.0f;

    out->seq = st->cmd.seq + 1u;
    st->cmd = *out;
    fill_log(st, m);
}
