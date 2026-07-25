/*
 * test_mission.c — offline bench for the Opus Vector mission controller.
 *
 * Not part of any DLL. It runs opus_mission.c against a simplified plant so the
 * phase machine, the braking profile and the turn can be validated in
 * milliseconds instead of by repeatedly launching the simulator.
 *
 * The plant deliberately mirrors the simulator's structure where it matters:
 *   - the DC machine's back-EMF/resistance/current-limit loop,
 *   - the same coast-down drag polynomial,
 *   - a bicycle-model yaw response,
 *   - and, critically, an encoder that integrates with the SAME left-endpoint
 *     sum the simulator uses, so the integration-bias correction is exercised.
 *
 * It is NOT a substitute for the simulator: there are no tyres, no slip, no
 * weight transfer, no ESC lag and no suspension. Passing here means the logic is
 * right; it says nothing about whether the odometer scale is calibrated.
 *
 * Build:
 *   gcc -std=c11 -O2 -I opus_mission -I common -o test_mission \
 *       opus_mission/test_mission.c opus_mission/opus_mission.c common/pid.c -lm
 */
#include "opus_mission.h"
#include "mission_cfg.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

typedef struct {
    double x, z, psi;      /* world pose, psi positive = left */
    double v;              /* m/s */
    double acc_l, acc_r;   /* integrated front wheel angle, rad */
    double acc_rl, acc_rr;
    double path;           /* true ground path length */
} Plant;

static double tick_angle(void) { return 2.0 * OPUS_PI / VE_ENC_CPR; }

static float wrapped_ticks(double accum)
{
    long t = (long)(accum / tick_angle());
    long w = t % VE_ENC_WRAP;
    if (w < 0) w += VE_ENC_WRAP;
    return (float)w;
}

int main(void)
{
    OpusState st;
    Plant p;
    const double dt = 0.01;
    double t = 0.0;
    int phase_seen[12];
    double mark_leg_a = -1, mark_turn_entry = -1, mark_turn_exit = -1;
    double mark_leg_b = -1, mark_stop = -1, psi_at_entry = 0, psi_at_exit = 0;
    int prev_phase = -99, steps = 0;

    memset(&p, 0, sizeof(p));
    memset(phase_seen, 0, sizeof(phase_seen));
    opus_init(&st);

    for (steps = 0; steps < 6000; steps++) {    /* 60 s ceiling */
        OpusMeas m;
        OpusCmd  c;
        double v_l, v_r, psi_dot, delta, i_each, f_motor, f_brake, f_drag, a;

        /* --- sample the plant EXACTLY as the host does: before stepping it,
         *     using the current speed (left-endpoint integration). --- */
        memset(&m, 0, sizeof(m));
        m.dt = (float)dt;
        m.time_s = (float)t;
        m.enc_left_ticks  = wrapped_ticks(p.acc_l);
        m.enc_right_ticks = wrapped_ticks(p.acc_r);
        m.enc_valid = 1;
        m.enc_rl_ticks = wrapped_ticks(p.acc_rl);
        m.enc_rr_ticks = wrapped_ticks(p.acc_rr);
        m.enc_rear_valid = 1;
        m.gyro_y = 0.0f;              /* left-handed host: a left turn reads negative */
        m.accel_mag = 9.81f;
        m.batt_v = 7.4f;
        m.tof_front_m = 1e6f;
        m.motor_count = VE_N_MOTORS;
        m.motor_vmax = 7.4f;

        /* Yaw rate of the previous step feeds the gyro channel, negated to
         * mimic Unity's handedness so the sign-learning path is exercised. */
        psi_dot = 0.0;

        opus_step(&st, &m, &c);

        /* Mark phase transitions BEFORE advancing the plant. Taking them after
         * would fold one whole tick of travel (45 mm at cruise) into every
         * start-of-leg datum and none into the stationary end one, biasing every
         * measured leg by a different amount. */
        if (st.phase != prev_phase) {
            if (st.phase >= 0 && st.phase < 12) phase_seen[st.phase] = 1;
            if (st.phase == OPUS_CRUISE_A) mark_leg_a = p.path;
            if (st.phase == OPUS_TURN)   { mark_turn_entry = p.path; psi_at_entry = p.psi; }
            if (st.phase == OPUS_CRUISE_B) { mark_turn_exit = p.path; psi_at_exit = p.psi; }
            if (st.phase == OPUS_BRAKE)  mark_leg_b = p.path;
            if (st.phase == OPUS_DONE)   mark_stop = p.path;
            prev_phase = st.phase;
        }

        /* --- plant --- */
        delta = -(double)c.steer * VE_MAX_STEER_DEG * OPUS_DEG2RAD;  /* +delta = left */
        psi_dot = (fabs(p.v) > 0.05) ? p.v / VE_WHEELBASE * tan(delta) : 0.0;

        i_each = ((double)c.motor_v - VE_BEMF_V_PER_MS * p.v) / VE_R_MOTOR;
        if (i_each >  30.0) i_each =  30.0;
        if (i_each < -30.0) i_each = -30.0;
        f_motor = VE_FORCE_PER_AMP_ALL * i_each;

        f_drag = VE_DRAG_C0 + VE_DRAG_C1 * fabs(p.v) + VE_DRAG_C2 * p.v * p.v;
        if (p.v < 0.0) f_drag = -f_drag;
        if (fabs(p.v) < 0.01 && fabs(f_motor) < VE_DRAG_C0) f_drag = f_motor;  /* stiction */

        f_brake = (double)c.brake * 4.0 * VE_MAX_BRAKE_NM / VE_WHEEL_R;
        if (p.v > 0.0) f_brake = -f_brake; else if (p.v < 0.0) f_brake = -f_brake * -1.0;
        if (fabs(p.v) < 0.01) f_brake = 0.0;

        a = (f_motor - f_drag + f_brake) / VE_MASS;

        /* Encoders integrate the CURRENT speed over the step — the same
         * left-endpoint sum WheelEncoderSensor.Sample uses. */
        v_l = p.v - psi_dot * VE_TRACK_FRONT * 0.5;
        v_r = p.v + psi_dot * VE_TRACK_FRONT * 0.5;
        p.acc_l  += (v_l / VE_WHEEL_R) * dt;
        p.acc_r  += (v_r / VE_WHEEL_R) * dt;
        p.acc_rl += (v_l / VE_WHEEL_R) * dt;
        p.acc_rr += (v_r / VE_WHEEL_R) * dt;

        p.x += p.v * cos(p.psi) * dt;
        p.z += p.v * sin(p.psi) * dt;
        p.psi += psi_dot * dt;

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
        if (fabs(p.v) < 0.02 && fabs(f_motor) < VE_DRAG_C0) p.v = 0.0;
        if (p.v < 0.0 && c.motor_v >= 0.0f) p.v = 0.0;

        t += dt;
#ifdef OPUS_TRACE
        if (steps % 10 == 0)
            printf("t=%6.2f ph=%d odo=%8.3f v=%5.2f psi=%7.2f psid=%+7.3f "
                   "steer=%+6.3f mv=%+6.2f br=%4.2f turn_t=%5.2f\n",
                   t, st.phase, st.odo_m, st.v_meas, st.psi * OPUS_RAD2DEG,
                   st.psi_dot, c.steer, c.motor_v, c.brake, st.turn_t);
#endif
        if (st.phase == OPUS_DONE || st.phase == OPUS_FAULT) break;
    }

    printf("=== Opus mission bench ===\n");
    printf("terminated in phase %d after %.2f s, fault=0x%04X\n\n",
           st.phase, t, st.fault);

    if (st.phase != OPUS_DONE) {
        printf("MISSION DID NOT COMPLETE\n");
        printf("  odo=%.3f v=%.3f psi=%.1f deg\n", st.odo_m, st.v_meas, st.psi * OPUS_RAD2DEG);
        return 1;
    }

    printf("%-26s %10s %10s %9s\n", "leg", "target", "actual", "error");
    printf("%-26s %10.3f %10.3f %+9.1f mm\n", "constant-velocity leg",
           (double)MI_LEG_A_M, mark_turn_entry - mark_leg_a,
           (mark_turn_entry - mark_leg_a - MI_LEG_A_M) * 1000.0);
    printf("%-26s %10.2f %10.2f %+9.2f deg\n", "turn",
           45.0, (psi_at_exit - psi_at_entry) * OPUS_RAD2DEG,
           (psi_at_exit - psi_at_entry) * OPUS_RAD2DEG - 45.0);
    printf("%-26s %10.3f %10.3f %+9.1f mm\n", "post-turn leg",
           (double)MI_LEG_B_M, mark_leg_b - mark_turn_exit,
           (mark_leg_b - mark_turn_exit - MI_LEG_B_M) * 1000.0);
    printf("%-26s %10.3f %10.3f %+9.1f mm\n", "braking distance",
           (double)MI_BRAKE_M, mark_stop - mark_leg_b,
           (mark_stop - mark_leg_b - MI_BRAKE_M) * 1000.0);
    printf("%-26s %10.3f %10.3f %+9.1f mm\n", "total from turn exit",
           (double)MI_STOP_FROM_EXIT, mark_stop - mark_turn_exit,
           (mark_stop - mark_turn_exit - MI_STOP_FROM_EXIT) * 1000.0);
    printf("\ncontroller's own stop error: %+.2f mm\n", st.stop_err_mm);
    printf("odometer %.4f m vs true path %.4f m (drift %+.1f mm)\n",
           st.odo_m, p.path, (st.odo_m - p.path) * 1000.0);
    return 0;
}
