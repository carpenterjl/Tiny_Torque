/*
 * test_mission.c — offline bench for the Opus Vector mission controller.
 *
 * Not part of any DLL. It runs opus_mission.c against a simplified plant so the
 * phase machine, the braking profile and the turn can be validated in
 * milliseconds instead of by repeatedly launching the simulator.
 *
 * The plant deliberately mirrors the simulator's structure where it matters:
 *   - torque-commanded drive wheels with a current limit (an ideal FOC drive:
 *     the core asks for wheel torque, the plant delivers it),
 *   - the same coast-down drag polynomial,
 *   - a bicycle-model yaw response,
 *   - and, critically, an encoder that integrates with the SAME left-endpoint
 *     sum the simulator uses, so the integration-bias correction is exercised.
 *
 * It is NOT a substitute for the simulator: there are no tyres, no slip, no
 * weight transfer, no ESC lag and no suspension. Passing here means the logic is
 * right; it says nothing about whether the odometer scale is calibrated.
 *
 * The mission runs twice, at 100 Hz and at 500 Hz, and must pass at both: the
 * core takes dt from timestamps and states every threshold as a time, so its
 * loop rate is the scheduler's business (FW-05, TIM-06).
 *
 * It then runs at 100 Hz with every command acting one and two periods late
 * (a timer-latched output, FW-13), with the delay in the parameters, and
 * every leg must land within TOL_DELAY_MM of the run without it: the core
 * predicts over the commands in flight, so a known delay costs nothing.
 *
 * And with the commands two periods late while the parameters say they are
 * not, the drives reporting their current (FW-17): the mission must measure
 * the delay at the launch and land within TOL_DELAY_MM of the undelayed run.
 *
 * Then the car is not what its parameters say (FW-15, FW-16): the motors
 * give 10 % less torque, and the gyro reads 0.5 deg/s off with the steering
 * 2 deg off centre. The mission must still pass, having learned the force
 * gain, the gyro bias and the steering trim.
 *
 * And with the odometry wheels' reports lost on the bus in bursts, one wheel
 * at a time and both together (FW-24): a report held over brings no travel
 * and the next brings it all, and every leg must land within TOL_DELAY_MM of
 * the run without the losses.
 *
 * Last, routes (NAV-04): the oval and the figure-8 at 2 and 3 m/s and the
 * Opus manoeuvre's route, followed through the same arming, launch and stop,
 * steered by the tracker from the plant's own pose (the navigation filter is
 * tt_nav_unit's business). Each must finish, hold the line within
 * TOL_RT_XTE_MM and stop within TOL_RT_STOP_MM of the route's end.
 *
 * Build:
 *   gcc -std=c11 -O2 -I opus_mission -I common -I core -o test_mission \
 *       opus_mission/test_mission.c opus_mission/opus_mission.c common/pid.c \
 *       core/tt_params.c core/params_opus_vector.c -lm
 * or through CMake, where it is registered as the `opus_mission_bench` test:
 *   cmake -S . -B build && cmake --build build --target test_mission
 *   ctest --test-dir build --output-on-failure
 *
 * Exit code 0 = mission completed and every leg is inside its tolerance below,
 * at both rates. The tolerances are regression bounds for THIS plant, not the
 * in-simulator acceptance numbers: the bench has no tyres or ESC lag, so it
 * settles a few centimetres away from the simulator's figures by design.
 */
#include "opus_mission.h"
#include "mission_cfg.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

/* Regression tolerances (see the header). */
#define TOL_LEG_MM        25.0   /* constant-velocity and post-turn legs */
#define TOL_BRAKE_MM      75.0   /* braking distance                     */
#define TOL_TURN_DEG       0.5
/* The stop and delay bounds were set while the bench's parameters still
 * claimed rotating inertia its plant does not have, so the feed-forward ran
 * 35 % heavy. With the two agreeing (FW-16) the 500 Hz run settles 9 mm
 * long by its own odometer and a two-period delay moves the stop 2.7 mm;
 * the controller before FW-15/16 does exactly the same on this bench. */
#define TOL_STOP_ERR_MM   10.0   /* the controller's own stop estimate   */
#define TOL_ODO_DRIFT_MM  60.0   /* odometer vs true path, whole mission */
#define PLANT_I_MAX_A     30.0   /* per motor, the ESC's limit           */
#define TOL_DELAY_MM       3.0   /* a delayed run vs the undelayed one (FW-13) */
#define TOL_DELAY_DEG      0.15  /* the bench's gyro already lags a tick */

/* What one run measured, for comparing runs. */
typedef struct { double leg_a, turn, leg_b, brake, total; } Legs;

/* How the bench car differs from its parameters (FW-15, FW-16). */
typedef struct {
    const char *label;
    double torque_scale;            /* torque delivered / torque commanded */
    double gyro_bias;               /* rad/s added to the gyro             */
    double steer_offset;            /* rad the wheels sit off the command  */
    double accel_bias;              /* m/s^2 added to the forward accelerometer
                                       (a die pitched in the body, FW-22) */
} CarErr;
static CarErr g_err = { 0, 1.0, 0.0, 0.0, 0.0 };
/* FW-17: the parameters claim no delay, and the drives report the current
 * of the torque acting; the mission must measure the delay itself. */
static int g_measure_delay = 0;
/* FW-24: the odometry wheels' reports lost on the bus, in bursts: a held
 * report keeps its count, sequence and stamp. */
static int g_bus_loss = 0;
static int bus_held(uint32_t tick, int right)
{
    if (tick % 41u < 2u) return 1;                       /* both, two ticks */
    return right ? tick % 29u - 11u < 2u : tick % 17u - 5u < 3u;
}
#define TOL_GAIN          0.02   /* learned force gain vs the torque scale */
#define TOL_RT_XTE_MM     40.0   /* route: max cross-track error (regression bound; the
                                    sim judges NAV-02: 50 mm RMS, 150 mm max) */
#define TOL_RT_RMS_MM     20.0   /* route: RMS cross-track error            */
#define TOL_RT_STOP_MM    25.0   /* route: stop along the route vs its end  */
#define TOL_BIAS          1e-4   /* rad/s, learned gyro bias               */
#define TOL_ABIAS         0.02   /* m/s^2, learned accelerometer bias (FW-22) */

typedef struct {
    double x, z, psi;               /* world pose, psi positive = left */
    double v;                       /* m/s */
    double acc[TT_MAX_WHEELS];      /* integrated wheel angle, rad */
    double path;                    /* true ground path length */
} Plant;

static int32_t counts(const TtParams *p, int w, double accum)
{
    return (int32_t)floor(accum / (2.0 * OPUS_PI / (p->enc_cpr[w] * p->enc_ratio[w])));
}

/* One plant step under command c: a kinematic bicycle referenced midway
 * between the axles (the twin's chassis origin, where the firmware's pose
 * is: it moves at the sideslip atan(tan(delta) / 2) to its heading), an
 * ideal current drive, the drag polynomial, the friction brake. */
static void plant_step(Plant *p, const TtParams *pp, const TtCmd *c, double dt,
                       double *psi_dot_out, double *a_out)
{
    const double r = pp->wheel_radius_m;
    double psi_dot, f_motor = 0.0, f_brake, f_drag, a, v_l, v_r;
    int w;
    double td = tan((double)c->steer_rad + g_err.steer_offset), beta = atan(0.5 * td);
    psi_dot = (fabs(p->v) > 0.05) ? p->v * cos(beta) / pp->wheelbase_m * td : 0.0;

    /* An ideal current-controlled drive: the commanded wheel torque,
     * limited by the drive's current. */
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        double t_max = PLANT_I_MAX_A * pp->gear[w] * pp->kt[w] * pp->eta_drive;
        double tw = c->arm ? (double)c->wheel_torque_nm[w] : 0.0;
        if (tw >  t_max) tw =  t_max;
        if (tw < -t_max) tw = -t_max;
        f_motor += tw * g_err.torque_scale / r;
    }

    f_drag = pp->drag_c0 + pp->drag_c1 * fabs(p->v) + pp->drag_c2 * p->v * p->v;
    if (p->v < 0.0) f_drag = -f_drag;
    if (fabs(p->v) < 0.01 && fabs(f_motor) < pp->drag_c0) f_drag = f_motor;  /* stiction */

    f_brake = (double)c->brake_01 * 4.0 * pp->brake_max_nm / r;
    if (p->v > 0.0) f_brake = -f_brake; else if (p->v < 0.0) f_brake = -f_brake * -1.0;
    if (fabs(p->v) < 0.01) f_brake = 0.0;

    a = (f_motor - f_drag + f_brake) / pp->mass_kg;

    /* Encoders integrate the CURRENT speed over the step — the same
     * left-endpoint sum WheelEncoderSensor.Sample uses. */
    v_l = p->v - psi_dot * pp->track_front_m * 0.5;
    v_r = p->v + psi_dot * pp->track_front_m * 0.5;
    p->acc[TT_FL] += (v_l / r) * dt;
    p->acc[TT_FR] += (v_r / r) * dt;
    p->acc[TT_RL] += (v_l / r) * dt;
    p->acc[TT_RR] += (v_r / r) * dt;

    p->x += p->v * cos(p->psi + beta) * dt;
    p->z += p->v * sin(p->psi + beta) * dt;
    p->psi += psi_dot * dt;

    /* Ground truth must NOT use the same left-endpoint sum the encoder does,
     * or it inherits the very bias the controller is correcting for and the
     * comparison becomes circular. Trapezoid it. */
    {
        double v_next = p->v + a * dt;
        p->path += 0.5 * (fabs(p->v) + fabs(v_next)) * dt;
    }
    p->v += a * dt;
    /* Static friction: below a few mm/s, a drive force smaller than the
     * Coulomb breakaway cannot keep the car moving. Without this the plant
     * glides forever at millimetres per second and nothing ever stops. */
    if (fabs(p->v) < 0.02 && fabs(f_motor) < pp->drag_c0) p->v = 0.0;
    if (p->v < 0.0 && f_motor >= 0.0) p->v = 0.0;
    *psi_dot_out = psi_dot;
    *a_out = a;
}

/* glitch: from 1 s into the first measured leg, the left odometry encoder
 * reads 5000 counts ahead (one miscount that stays in the counter, VAL-11).
 * The mission must reject it without losing distance or heading.
 * delay: each command acts this many ticks after the one it was computed on,
 * and the parameters say so (FW-13). */
static int run(double rate_hz, int glitch, int delay, Legs *out_legs)
{
    TtParams bench = tt_params_opus_vector;
    const TtParams *pp = &bench;
    OpusState st;
    Plant p;
    const double dt = 1.0 / rate_hz;
    const double r = pp->wheel_radius_m;
    double t = 0.0;
    double mark_leg_a = -1, mark_turn_entry = -1, mark_turn_exit = -1;
    double mark_leg_b = -1, mark_stop = -1, psi_at_entry = 0, psi_at_exit = 0;
    int prev_phase = -99, steps = 0, w;
    double psi_dot_prev = 0.0;      /* last step's yaw rate, for the gyro */
    uint32_t tick = 0;
    int32_t glitch_off = 0;
    double a_prev = 0.0;            /* last step's acceleration, for the accelerometer */
    TtCmd  fly[16];                 /* commands in flight, oldest first */
    int    n_fly = 0;
    double iq_meas[TT_MAX_WHEELS] = { 0 };   /* the drives' current, sampled next tick */
    TtEnc  enc_last[TT_MAX_WHEELS];          /* the last report sent (FW-24) */
    (void)r;

    /* Calibration is measured against a plant, and this plant has no tyre
     * slip: the sim's measured brake-slip term would make the odometer
     * over-read here by exactly the slip the bench does not model. */
    bench.cal_scale = 0.0f;
    bench.cal_brake = 0.0f;
    /* Nor rotating inertia: the plant accelerates the bare mass, so the
     * parameters say so too (else the car answers 30 % more acceleration
     * per newton than they promise, and the force gain (FW-16) learns it). */
    bench.mass_eff_kg = bench.mass_kg;
    bench.cmd_delay_s = g_measure_delay ? 0.0f : (float)(delay / rate_hz);

    memset(&p, 0, sizeof(p));
    opus_init(&st, pp, (float)rate_hz);
    if (g_measure_delay) opus_set_drive_feedback(&st, 1);

    for (steps = 0; steps < (int)(60.0 * rate_hz); steps++) {    /* 60 s ceiling */
        TtMeas m;
        TtCmd  c;
        double psi_dot, a;

        /* --- sample the plant EXACTLY as the host does: before stepping it,
         *     using the current speed (left-endpoint integration). --- */
        memset(&m, 0, sizeof(m));
        m.now_us = (tt_us_t)llround(t * 1e6);
        m.dt_s = (float)dt;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            m.enc[w].count = counts(pp, w, p.acc[w]) + (w == st.odo_l ? glitch_off : 0);
            m.enc[w].st.valid = 1;
            m.enc[w].st.seq = tick;
            m.enc[w].st.t_us = m.now_us;
            if (g_bus_loss && tick > 0u && (w == st.odo_l || w == st.odo_r) &&
                bus_held(tick, w == st.odo_r))
                m.enc[w] = enc_last[w];
            enc_last[w] = m.enc[w];
            if (pp->driven_mask & (1u << w)) {
                m.drv[w].st.valid = 1;
                m.drv[w].vbus_v = 7.4f;
                m.drv[w].iq_a = (float)iq_meas[w];
            }
        }
        /* Yaw rate of the previous step feeds the gyro. FLU: a left turn is
         * positive, no sign to learn. */
        m.imu.st.valid = 1;
        m.imu.gyro[2] = (float)(psi_dot_prev + g_err.gyro_bias);
        m.imu.accel[0] = (float)(a_prev + g_err.accel_bias);
        m.imu.accel[2] = 9.81f;
        m.batt.st.valid = 1;
        m.batt.v = 7.4f;
        /* no ToF: m.tof.st.valid stays 0 */

        opus_step(&st, &m, &c);
        tick++;
        /* The plant gets the command from `delay` ticks ago (zeros until the
         * first one arrives); the marks below still follow the firmware. */
        fly[n_fly++] = c;
        if (n_fly > delay) {
            c = fly[0];
            memmove(fly, fly + 1, sizeof(fly[0]) * (size_t)(--n_fly));
        } else {
            memset(&c, 0, sizeof(c));
        }
        if (glitch && glitch_off == 0 && st.phase == OPUS_CRUISE_A && st.phase_t > 1.0f)
            glitch_off = 5000;

        /* Mark phase transitions BEFORE advancing the plant. Taking them after
         * would fold one whole tick of travel (45 mm at cruise) into every
         * start-of-leg datum and none into the stationary end one, biasing every
         * measured leg by a different amount. */
        if (st.phase != prev_phase) {
            if (st.phase == OPUS_CRUISE_A) mark_leg_a = p.path;
            if (st.phase == OPUS_TURN)   { mark_turn_entry = p.path; psi_at_entry = p.psi; }
            if (st.phase == OPUS_CRUISE_B) { mark_turn_exit = p.path; psi_at_exit = p.psi; }
            if (st.phase == OPUS_BRAKE)  mark_leg_b = p.path;
            if (st.phase == OPUS_DONE)   mark_stop = p.path;
            prev_phase = st.phase;
        }

        /* --- plant --- */
        plant_step(&p, pp, &c, dt, &psi_dot, &a);
        psi_dot_prev = psi_dot;
        a_prev = a;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            double k = (double)pp->kt[w] * pp->gear[w] * pp->eta_drive;
            iq_meas[w] = (c.arm && k > 0.0) ? (double)c.wheel_torque_nm[w] / k : 0.0;
        }

        t += dt;
#ifdef OPUS_TRACE
        if (steps % (int)(rate_hz / 10.0) == 0)
            printf("t=%6.2f ph=%d odo=%8.3f v=%5.2f psi=%7.2f psid=%+7.3f "
                   "steer=%+6.3f T=%+6.3f br=%4.2f turn_t=%5.2f\n",
                   t, st.phase, opus_odo_m(&st), st.v_meas, st.psi * OPUS_RAD2DEG,
                   st.psi_dot, c.steer_rad, c.wheel_torque_nm[TT_RL], c.brake_01, st.turn_t);
#endif
        if (st.phase == OPUS_DONE || st.phase == OPUS_FAULT) break;
    }

    printf("=== Opus mission bench @ %.0f Hz%s%s%s%s ===\n", rate_hz,
           delay ? (delay == 1 ? ", commands one period late" : ", commands two periods late") : "",
           g_bus_loss ? ", odometry frames lost in bursts" : "",
           g_err.label ? ", " : "", g_err.label ? g_err.label : "");
    printf("terminated in phase %d after %.2f s, fault=0x%04X\n\n", st.phase, t, st.fault);

    if (st.phase != OPUS_DONE) {
        printf("MISSION DID NOT COMPLETE\n");
        printf("  odo=%.3f v=%.3f psi=%.1f deg\n\n", opus_odo_m(&st), st.v_meas, st.psi * OPUS_RAD2DEG);
        return 1;
    }

    {
        double leg_a  = (mark_turn_entry - mark_leg_a - MI_LEG_A_M) * 1000.0;
        double turn   = (psi_at_exit - psi_at_entry) * OPUS_RAD2DEG - 45.0;
        double leg_b  = (mark_leg_b - mark_turn_exit - MI_LEG_B_M) * 1000.0;
        double brake  = (mark_stop - mark_leg_b - MI_BRAKE_M) * 1000.0;
        double total  = (mark_stop - mark_turn_exit - MI_STOP_FROM_EXIT) * 1000.0;
        double drift  = (opus_odo_m(&st) - p.path) * 1000.0;
        int failures = 0;
        if (out_legs) {
            out_legs->leg_a = leg_a; out_legs->turn = turn; out_legs->leg_b = leg_b;
            out_legs->brake = brake; out_legs->total = total;
        }

#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); failures++; } } while (0)

        printf("%.0f Hz%s\n", rate_hz, glitch ? ", one encoder glitch in leg A" : "");
        printf("%-26s %10s %10s %9s\n", "leg", "target", "actual", "error");
        printf("%-26s %10.3f %10.3f %+9.1f mm\n", "constant-velocity leg",
               (double)MI_LEG_A_M, mark_turn_entry - mark_leg_a, leg_a);
        printf("%-26s %10.2f %10.2f %+9.2f deg\n", "turn",
               45.0, (psi_at_exit - psi_at_entry) * OPUS_RAD2DEG, turn);
        printf("%-26s %10.3f %10.3f %+9.1f mm\n", "post-turn leg",
               (double)MI_LEG_B_M, mark_leg_b - mark_turn_exit, leg_b);
        printf("%-26s %10.3f %10.3f %+9.1f mm\n", "braking distance",
               (double)MI_BRAKE_M, mark_stop - mark_leg_b, brake);
        printf("%-26s %10.3f %10.3f %+9.1f mm\n", "total from turn exit",
               (double)MI_STOP_FROM_EXIT, mark_stop - mark_turn_exit, total);
        printf("\ncontroller's own stop error: %+.2f mm\n", (double)st.stop_err_mm);
        printf("learned: force gain %.4f, steering trim %+.4f rad, gyro bias %+.5f rad/s, "
               "accel bias %+.3f m/s^2, command delay %.1f ms%s\n",
               (double)st.f_gain, (double)st.steer_trim, (double)st.gyro_bias, (double)st.ax_bias,
               (double)st.dly_s * 1000.0, st.dly_state == 2 ? " (measured)" : "");
        printf("odometer %.4f m vs true path %.4f m (drift %+.1f mm)\n\n",
               (double)opus_odo_m(&st), p.path, drift);

        CHECK("every phase marker was crossed",
              mark_leg_a >= 0 && mark_turn_entry >= 0 && mark_turn_exit >= 0 &&
              mark_leg_b >= 0 && mark_stop >= 0);
        CHECK("constant-velocity leg", fabs(leg_a) <= TOL_LEG_MM);
        CHECK("turn angle",            fabs(turn)  <= TOL_TURN_DEG);
        /* A delay the parameters did not promise starts the brake early by
         * its travel at cruise, a tick more than measured (the measurement
         * rounds down, FW-19): leg B short by it, the stop long. */
        {
            double early = g_measure_delay ? (double)MI_V_CRUISE * (delay + 1) / rate_hz * 1000.0 : 0.0;
            CHECK("post-turn leg",     fabs(leg_b + early) <= TOL_LEG_MM);
            CHECK("braking distance",  fabs(brake - early) <= TOL_BRAKE_MM);
        }
        CHECK("controller stop error", fabs(st.stop_err_mm) <= TOL_STOP_ERR_MM);
        CHECK("odometer drift",        fabs(drift) <= TOL_ODO_DRIFT_MM);
        CHECK("force gain learned",    fabs(st.f_gain - g_err.torque_scale) <= TOL_GAIN);
        CHECK("gyro bias learned",     fabs(st.gyro_bias - g_err.gyro_bias) <= TOL_BIAS);
        CHECK("accel bias learned",    fabs(st.ax_bias - g_err.accel_bias) <= TOL_ABIAS);
        /* The trim takes what lies beyond the servo's play; the yaw loop's
         * integral holds the rest. */
        CHECK("steering trim learned", fabs(st.steer_trim - g_err.steer_offset) <= GA_TRIM_DEADZONE + 0.002);
        if (g_measure_delay)
            CHECK("command delay measured", st.dly_state == 2 && fabs(st.dly_s - delay / rate_hz) < 1e-4);

#undef CHECK
        printf(failures ? "%d check(s) FAILED\n\n" : "all checks passed\n\n", failures);
        return failures ? 1 : 0;
    }
}

/* NAV-04: a route through the same mission, one command period late as the
 * twin's are, steered from the plant's pose as the navigation filter would
 * give it (the pose after the last step). */
static int run_route(const char *name, float v_max)
{
    TtParams bench = tt_params_opus_vector;
    const TtParams *pp = &bench;
    const TtRoute *rt = tt_route_find(name);
    const double rate_hz = 100.0, dt = 1.0 / rate_hz;
    OpusState st;
    Plant p;
    TtCmd fly[2];
    int n_fly = 0, steps, w, failures = 0;
    double psi_dot_prev = 0.0, a_prev = 0.0, t = 0.0, sum2 = 0.0, mx = 0.0, prev[3];
    uint32_t tick = 0, i_proj = 0, n = 0;

    bench.cal_scale = 0.0f;
    bench.cal_brake = 0.0f;
    bench.mass_eff_kg = bench.mass_kg;
    bench.cmd_delay_s = (float)dt;
    bench.tyre_c_alpha = 0.0f;      /* no tyres here: only the kinematic sideslip */
    memset(&p, 0, sizeof(p));
    p.x = rt->start[0];
    p.z = rt->start[1];
    p.psi = rt->start[2];
    prev[0] = p.x; prev[1] = p.z; prev[2] = p.psi;
    opus_init(&st, pp, (float)rate_hz);
    opus_set_route(&st, rt, v_max);

    for (steps = 0; steps < (int)(90.0 * rate_hz); steps++) {
        TtMeas m;
        TtCmd c;
        double psi_dot, a;
        memset(&m, 0, sizeof(m));
        m.now_us = (tt_us_t)llround(t * 1e6);
        m.dt_s = (float)dt;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            m.enc[w].count = counts(pp, w, p.acc[w]);
            m.enc[w].st.valid = 1;
            m.enc[w].st.seq = tick;
            m.enc[w].st.t_us = m.now_us;
            if (pp->driven_mask & (1u << w)) { m.drv[w].st.valid = 1; m.drv[w].vbus_v = 7.4f; }
        }
        m.imu.st.valid = 1;
        m.imu.gyro[2] = (float)psi_dot_prev;
        m.imu.accel[0] = (float)a_prev;
        m.imu.accel[2] = 9.81f;
        m.batt.st.valid = 1;
        m.batt.v = 7.4f;

        opus_set_pose(&st, (float)prev[0], (float)prev[1], (float)prev[2], 1);
        prev[0] = p.x; prev[1] = p.z; prev[2] = p.psi;
        opus_step(&st, &m, &c);
        tick++;
        fly[n_fly++] = c;
        if (n_fly > 1) { c = fly[0]; fly[0] = fly[1]; n_fly = 1; } else memset(&c, 0, sizeof(c));

        plant_step(&p, pp, &c, dt, &psi_dot, &a);
        psi_dot_prev = psi_dot;
        a_prev = a;
        t += dt;
        if (st.phase == OPUS_FOLLOW || st.phase == OPUS_BRAKE || st.phase == OPUS_CREEP) {
            TtRouteProj pr;
            tt_route_project(rt, (float)p.x, (float)p.z, i_proj, 10u, 40u, &pr);
            i_proj = pr.i;
            sum2 += (double)pr.ey * pr.ey;
            if (fabs(pr.ey) > mx) mx = fabs(pr.ey);
            n++;
        }
        if (st.phase == OPUS_DONE || st.phase == OPUS_FAULT) break;
    }
    {
        TtRouteProj pr;
        double stop_mm, rms = n ? sqrt(sum2 / n) * 1e3 : 1e9, end_deg;
        tt_route_project(rt, (float)p.x, (float)p.z, i_proj, 10u, 40u, &pr);
        stop_mm = ((double)pr.s - rt->pt[rt->n - 1u].s) * 1e3;
        end_deg = tt_route_wrap((float)p.psi - rt->pt[rt->n - 1u].psi) * OPUS_RAD2DEG;
        printf("=== route %s%s%.1f m/s: phase %d after %.2f s, fault 0x%X ===\n", name,
               v_max > 0.0f ? " @ " : " at its own ", v_max > 0.0f ? (double)v_max : (double)rt->v_max,
               st.phase, t, st.fault);
        printf("cross-track rms %.1f mm, max %.1f mm; stop %+.1f mm along, %+.1f mm across; "
               "heading at the end %+.2f deg\n\n", rms, mx * 1e3, stop_mm, (double)pr.ey * 1e3, end_deg);
#define CHECK(nm, cond) do { if (!(cond)) { printf("FAIL: %s\n", nm); failures++; } } while (0)
        CHECK("route completed", st.phase == OPUS_DONE);
        CHECK("route cross-track rms", rms <= TOL_RT_RMS_MM);
        CHECK("route cross-track max", mx * 1e3 <= TOL_RT_XTE_MM);
        CHECK("route stop", fabs(stop_mm) <= TOL_RT_STOP_MM);
#undef CHECK
    }
    return failures;
}

/* FW-13: the same mission with a known command delay must land where the
 * undelayed one does. A delay the parameters did not promise starts the
 * brake early by its travel at cruise (FW-19): leg B reads short by
 * `early_mm` and the stop long by it, the total as before. */
static int compare(const char *what, const Legs *a, const Legs *b, double early_mm)
{
    int f = 0;
#define NEAR(name, x, y, tol) do { \
        double d_ = (x) - (y); \
        if (d_ > (tol) || d_ < -(tol)) { printf("FAIL: %s, %s %+.2f vs %+.2f\n", what, name, x, y); f++; } \
    } while (0)
    NEAR("constant-velocity leg mm", a->leg_a, b->leg_a, TOL_DELAY_MM);
    NEAR("turn deg",                 a->turn,  b->turn,  TOL_DELAY_DEG);
    NEAR("post-turn leg mm",         a->leg_b, b->leg_b - early_mm, TOL_DELAY_MM);
    NEAR("braking distance mm",      a->brake, b->brake + early_mm, TOL_DELAY_MM);
    NEAR("total from turn exit mm",  a->total, b->total, TOL_DELAY_MM);
#undef NEAR
    printf("%s: %s\n\n", what, f ? "differs" : "within tolerance of the undelayed run");
    return f;
}

/* FW-18: the friction probe's fit, on the samples a tyre would give (the
 * bench has no tyres to run the probe on). Two tyres: a brush tyre (its
 * stiffness fixed, its peak slip moving with mu) and the sim's (its peak
 * slip fixed at 10 %, so its stiffness moves with mu). The same force on
 * both axles, the front the lighter; the probe's ramp stopped short of the
 * peak; a little measurement noise, and one sample thrown off as the front
 * motors cross their gear lash. */
static float pr_slip(int sim_tyre, float a, float mu)
{
    float s = 1.0f - sqrtf(1.0f - a / mu);
    return sim_tyre ? 0.1f * s : 2.0f * mu / 19.0f * s;
}

static int probe_fit_checks(void)
{
    static const float mus[] = { 0.60f, 0.75f, 0.90f, 1.15f, 1.60f };
    int failures = 0, kind, i, j;
    printf("=== friction probe fit (FW-18) ===\n");
    for (kind = 0; kind < 2; kind++) {
        for (i = 0; i < (int)(sizeof(mus) / sizeof(mus[0])); i++) {
            float af[OPUS_PR_SAMPLES], ar[OPUS_PR_SAMPLES], d[OPUS_PR_SAMPLES];
            float mu = mus[i], est, slope = 0.0f, worst = 0.0f;
            uint32_t seed = 12345u + (uint32_t)i;
            int n = 0, ok, k;
            for (j = 0; j < 45; j++) {
                float a_f = j < 5 ? 0.0f : GA_PR_A_MAX * (float)(j - 5) / 40.0f;
                float a_r = a_f * 0.484f / 0.516f;
                float noise;
                if (a_f > 0.85f * mu) break;
                seed = seed * 1664525u + 1013904223u;
                noise = ((float)(seed >> 8) / 16777216.0f - 0.5f) * 0.0008f;   /* +-0.04 % */
                af[n] = a_f;
                ar[n] = a_r;
                d[n] = pr_slip(kind, a_r, mu) + pr_slip(kind, a_f, mu) + 0.004f + noise;
                if (j == 8) d[n] += 0.008f;     /* the gear lash crossed: a tick off */
                n++;
            }
            est = opus_probe_mu(af, ar, d, n, &slope);
            /* a floor well above what the probe asks reads as good, no more */
            ok = mu > 1.5f ? est > 1.2f : fabsf(est - mu) < 0.05f * mu;
            /* FW-26: the curve the fit kept (slope * g(a, est)) is the slip the
             * tyre really has at the forces the probe reached, within a tenth
             * of it, whether or not mu itself was pinned (the 1.6 floor): the
             * odometry pair's brake slip is read off it. */
            for (k = 1; k <= 4 && est > 0.0f; k++) {
                float a = af[n - 1] * 0.2f * (float)k;
                float fit = slope * 2.0f * est * (1.0f - sqrtf(1.0f - (a / est < 0.999f ? a / est : 0.999f)));
                float tru = pr_slip(kind, a, mu);
                float e = fabsf(fit - tru) / tru;
                if (e > worst) worst = e;
            }
            if (!(worst < 0.10f)) ok = 0;
            printf("  %-28s mu %.2f: the probe finds %.3f, C/Fz %.1f, from %d samples; its curve within %.1f %% of the tyre's slip%s\n",
                   kind ? "sim tyre (peak slip 10 %)," : "brush tyre (C/Fz 19),", mu, est,
                   slope > 0.0f ? 1.0f / slope : 0.0f, n, worst * 100.0f, ok ? "" : "  <-- FAIL");
            if (!ok) failures++;
        }
    }
    printf(failures ? "%d check(s) FAILED\n\n" : "all checks passed\n\n", failures);
    return failures;
}

int main(void)
{
    int fails = 0;
    Legs base, late1, late2;
    fails += probe_fit_checks();
    fails += run(100.0, 0, 0, &base);
    fails += run(500.0, 0, 0, NULL);
    fails += run(100.0, 1, 0, NULL);
    fails += run(100.0, 0, 1, &late1);
    fails += run(100.0, 0, 2, &late2);
    fails += compare("one period late", &late1, &base, 0.0);
    fails += compare("two periods late", &late2, &base, 0.0);
    {
        Legs meas;
        g_measure_delay = 1;
        fails += run(100.0, 0, 2, &meas);
        g_measure_delay = 0;
        fails += compare("two periods late, the parameters saying none, measured (FW-17)", &meas, &base,
                         (double)MI_V_CRUISE * 3.0 / 100.0 * 1000.0);   /* measured 2 ticks: planned on 3 */
    }
    {
        Legs lost;
        g_bus_loss = 1;
        fails += run(100.0, 0, 0, &lost);
        g_bus_loss = 0;
        fails += compare("odometry frames lost in bursts (FW-24)", &lost, &base, 0.0);
    }
    {
        static const CarErr weak = { "motors give 10 % less torque", 0.90, 0.0, 0.0 };
        static const CarErr skew = { "gyro 0.5 deg/s off, steering 2 deg off centre",
                                     1.0, 0.5 * OPUS_DEG2RAD, 2.0 * OPUS_DEG2RAD };
        /* FW-22: the die pitched 2 deg nose up, gravity in the forward axis. */
        static const CarErr pitched = { "IMU pitched 2 deg in the body", 1.0, 0.0, 0.0, 0.3423 };
        Legs lp;
        g_err = weak; fails += run(100.0, 0, 1, NULL);
        g_err = skew; fails += run(100.0, 0, 1, NULL);
        g_err = pitched; fails += run(100.0, 0, 1, &lp);
        fails += compare("IMU pitched 2 deg in the body (FW-22)", &lp, &late1, 0.0);
    }
    {
        static const CarErr none = { 0, 1.0, 0.0, 0.0, 0.0 };
        g_err = none;
        fails += run_route("oval", 2.0f);
        fails += run_route("oval", 3.0f);
        fails += run_route("figure8", 2.0f);
        fails += run_route("figure8", 3.0f);
        fails += run_route("hairpin", 0.0f);
        fails += run_route("slalom", 0.0f);
        fails += run_route("opus", 0.0f);
    }
    return fails ? 1 : 0;
}
