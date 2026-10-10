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
#define TOL_STOP_ERR_MM    5.0   /* the controller's own stop estimate   */
#define TOL_ODO_DRIFT_MM  60.0   /* odometer vs true path, whole mission */
#define PLANT_I_MAX_A     30.0   /* per motor, the ESC's limit           */

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

/* glitch: from 1 s into the first measured leg, the left odometry encoder
 * reads 5000 counts ahead (one miscount that stays in the counter, VAL-11).
 * The mission must reject it without losing distance or heading. */
static int run(double rate_hz, int glitch)
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

    /* Calibration is measured against a plant, and this plant has no tyre
     * slip: the sim's measured brake-slip term would make the odometer
     * over-read here by exactly the slip the bench does not model. */
    bench.cal_scale = 0.0f;
    bench.cal_brake = 0.0f;

    memset(&p, 0, sizeof(p));
    opus_init(&st, pp, (float)rate_hz);

    for (steps = 0; steps < (int)(60.0 * rate_hz); steps++) {    /* 60 s ceiling */
        TtMeas m;
        TtCmd  c;
        double v_l, v_r, psi_dot, f_motor, f_brake, f_drag, a;

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
            if (pp->driven_mask & (1u << w)) { m.drv[w].st.valid = 1; m.drv[w].vbus_v = 7.4f; }
        }
        /* Yaw rate of the previous step feeds the gyro. FLU: a left turn is
         * positive, no sign to learn. */
        m.imu.st.valid = 1;
        m.imu.gyro[2] = (float)psi_dot_prev;
        m.imu.accel[2] = 9.81f;
        m.batt.st.valid = 1;
        m.batt.v = 7.4f;
        /* no ToF: m.tof.st.valid stays 0 */

        opus_step(&st, &m, &c);
        tick++;
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
        psi_dot = (fabs(p.v) > 0.05) ? p.v / pp->wheelbase_m * tan((double)c.steer_rad) : 0.0;

        /* An ideal current-controlled drive: the commanded wheel torque,
         * limited by the drive's current. */
        f_motor = 0.0;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            double t_max = PLANT_I_MAX_A * pp->gear[w] * pp->kt[w] * pp->eta_drive;
            double tw = c.arm ? (double)c.wheel_torque_nm[w] : 0.0;
            if (tw >  t_max) tw =  t_max;
            if (tw < -t_max) tw = -t_max;
            f_motor += tw / r;
        }

        f_drag = pp->drag_c0 + pp->drag_c1 * fabs(p.v) + pp->drag_c2 * p.v * p.v;
        if (p.v < 0.0) f_drag = -f_drag;
        if (fabs(p.v) < 0.01 && fabs(f_motor) < pp->drag_c0) f_drag = f_motor;  /* stiction */

        f_brake = (double)c.brake_01 * 4.0 * pp->brake_max_nm / r;
        if (p.v > 0.0) f_brake = -f_brake; else if (p.v < 0.0) f_brake = -f_brake * -1.0;
        if (fabs(p.v) < 0.01) f_brake = 0.0;

        a = (f_motor - f_drag + f_brake) / pp->mass_kg;

        /* Encoders integrate the CURRENT speed over the step — the same
         * left-endpoint sum WheelEncoderSensor.Sample uses. */
        v_l = p.v - psi_dot * pp->track_front_m * 0.5;
        v_r = p.v + psi_dot * pp->track_front_m * 0.5;
        p.acc[TT_FL] += (v_l / r) * dt;
        p.acc[TT_FR] += (v_r / r) * dt;
        p.acc[TT_RL] += (v_l / r) * dt;
        p.acc[TT_RR] += (v_r / r) * dt;

        p.x += p.v * cos(p.psi) * dt;
        p.z += p.v * sin(p.psi) * dt;
        p.psi += psi_dot * dt;
        psi_dot_prev = psi_dot;

        /* Ground truth must NOT use the same left-endpoint sum the encoder does,
         * or it inherits the very bias the controller is correcting for and the
         * comparison becomes circular. Trapezoid it. */
        {
            double v_next = p.v + a * dt;
            p.path += 0.5 * (fabs(p.v) + fabs(v_next)) * dt;
        }
        p.v += a * dt;
        /* Static friction: below a few mm/s, a drive force smaller than the
         * Coulomb breakaway cannot keep the car moving. Without this the plant
         * glides forever at millimetres per second and nothing ever stops. */
        if (fabs(p.v) < 0.02 && fabs(f_motor) < pp->drag_c0) p.v = 0.0;
        if (p.v < 0.0 && f_motor >= 0.0) p.v = 0.0;

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

    printf("=== Opus mission bench @ %.0f Hz ===\n", rate_hz);
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
        printf("odometer %.4f m vs true path %.4f m (drift %+.1f mm)\n\n",
               (double)opus_odo_m(&st), p.path, drift);

        CHECK("every phase marker was crossed",
              mark_leg_a >= 0 && mark_turn_entry >= 0 && mark_turn_exit >= 0 &&
              mark_leg_b >= 0 && mark_stop >= 0);
        CHECK("constant-velocity leg", fabs(leg_a) <= TOL_LEG_MM);
        CHECK("turn angle",            fabs(turn)  <= TOL_TURN_DEG);
        CHECK("post-turn leg",         fabs(leg_b) <= TOL_LEG_MM);
        CHECK("braking distance",      fabs(brake) <= TOL_BRAKE_MM);
        CHECK("controller stop error", fabs(st.stop_err_mm) <= TOL_STOP_ERR_MM);
        CHECK("odometer drift",        fabs(drift) <= TOL_ODO_DRIFT_MM);

#undef CHECK
        printf(failures ? "%d check(s) FAILED\n\n" : "all checks passed\n\n", failures);
        return failures ? 1 : 0;
    }
}

int main(void)
{
    int fails = 0;
    fails += run(100.0, 0);
    fails += run(500.0, 0);
    fails += run(100.0, 1);
    return fails ? 1 : 0;
}
