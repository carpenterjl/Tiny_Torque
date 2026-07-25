/*
 * opus_mission.h — the Opus Vector's autonomous mission controller.
 *
 * Portable C. Depends only on <math.h>, mission_cfg.h and the shared PID in
 * common/pid.h. It knows nothing about the simulator, the ABI, or Unity: the
 * host adapter (targets/sim/opus_main.c) is the only file that includes
 * controller_api.h. That separation is what lets this same source drive a real
 * MCU with a different adapter.
 *
 * The controller is handed measurements and returns commands. It never reads
 * operator input: the mission runs start to finish on sensor feedback alone.
 */
#ifndef OPUS_MISSION_H
#define OPUS_MISSION_H

#include "pid.h"

/* Mission phases. The numeric values are published on debug[0] and are matched
 * by the game's MissionHud, so they are part of the interface — append only. */
typedef enum {
    OPUS_FAULT      = -1,
    OPUS_BOOT       = 0,
    OPUS_ARM_STATIC = 1,   /* standing self-check                                */
    OPUS_ARMED      = 2,   /* checks passed, about to launch                     */
    OPUS_LAUNCH     = 3,   /* accelerating to cruise; also proves encoder liveness */
    OPUS_CRUISE_A   = 4,   /* the measured 14.5 m                                */
    OPUS_TURN       = 5,   /* 45 deg left, no speed loss                         */
    OPUS_CRUISE_B   = 6,   /* the measured 7.5 m                                 */
    OPUS_BRAKE      = 7,   /* the measured 1.5 m                                 */
    OPUS_CREEP      = 8,   /* final millimetres, friction brake released         */
    OPUS_HOLD       = 9,   /* stationary, confirming                             */
    OPUS_DONE       = 10
} OpusPhase;

/* One front-wheel encoder's tick tracking. */
typedef struct {
    int   prev_ticks;      /* last raw register value, 0..VE_ENC_WRAP-1 */
    int   has_prev;
    double total_m;        /* unwrapped distance since arm */
} OpusEncoder;

/* Everything the host must supply each tick. Filling this from the ABI is the
 * adapter's whole job. */
typedef struct {
    float dt;              /* s */
    float time_s;          /* host clock, used only to detect a restart */
    float enc_left_ticks;  /* front-left raw tick register  */
    float enc_right_ticks; /* front-right raw tick register */
    int   enc_valid;       /* both FRONT encoders were located in the manifest */
    float enc_rl_ticks;    /* rear-left  — diagnostics only (wheelspin/lock) */
    float enc_rr_ticks;    /* rear-right */
    int   enc_rear_valid;
    float gyro_y;          /* rad/s about the vehicle's up axis, host sign */
    float accel_mag;       /* |specific force|, m/s^2 — impact/teleport detector */
    float batt_v;          /* pack terminal volts (<= 0 if unknown) */
    float tof_front_m;     /* forward range, or a large number if unknown */
    int   motor_count;     /* motors found in the manifest */
    float motor_vmax;      /* per-motor voltage limit from the manifest */
} OpusMeas;

/* What the controller wants done. */
typedef struct {
    float motor_v;         /* signed volts, same command to every motor */
    float steer;           /* [-1, 1]; positive steers RIGHT (host convention) */
    float brake;           /* [0, 1] */
} OpusCmd;

typedef struct {
    OpusPhase phase;
    int   fault;           /* FA_* bitmask */

    /* Estimator state */
    OpusEncoder enc_l, enc_r, enc_rl, enc_rr;
    double odo_m;          /* slip-corrected front-axle path length since arm */
    float  v_meas;         /* m/s from the tick delta */
    float  v_rear;         /* m/s from the driven wheels — slip diagnostic only */
    float  v_filt;         /* lightly filtered, for the creep loop */
    float  psi;            /* rad, heading since arm; POSITIVE = LEFT */
    float  psi_dot;        /* rad/s, fused */
    float  psi_dot_f;      /* low-passed — what the loops and exit tests use */
    float  gyro_sign;      /* +-1, learned at runtime; 0 while unknown */
    float  gyro_corr;      /* running correlation used to learn the sign */
    float  steer_obs_deg;  /* modelled servo position, positive = left */

    /* Sequencing */
    float  phase_t;        /* s in the current phase */
    float  hold_t;
    float  settle_t;
    float  live_l, live_r;  /* accumulated front-wheel travel, for the liveness test */
    int    live_checked;
    int    tof_hits;        /* consecutive short forward returns (debounce) */
    int    accel_hits;      /* consecutive over-threshold accelerations       */
    double leg_start_m;    /* odometer at the start of the current measured leg */
    double stop_target_m;  /* absolute odometer value the car must stop on */
    float  psi_ref;        /* heading the straight-line loops hold */
    float  turn_t;         /* s into the turn profile */
    float  turn_cmd_rad;   /* integral of the COMMANDED yaw rate */
    float  v_leg_start;    /* speed at the last leg boundary (trapezoid correction) */
    float  prev_time_s;

    /* Results, latched for reporting */
    float  leg_a_actual;
    float  turn_actual_deg;
    float  leg_b_actual;
    float  brake_actual;
    float  stop_err_mm;

    /* Loops */
    Pid spd_pid;
    Pid yaw_pid;

    /* Last command, mirrored for telemetry */
    OpusCmd cmd;
    float  i_cmd;
    float  slip_pct;
    float  v_ref;
    float  leg_rem;        /* signed distance left in the current measured leg */
} OpusState;

/* Reset to the power-on state. Called from ctrl_init, and again whenever the
 * host reports the vehicle was teleported home. */
void opus_init(OpusState *st);

/* One control tick. */
void opus_step(OpusState *st, const OpusMeas *m, OpusCmd *out);

#endif /* OPUS_MISSION_H */
