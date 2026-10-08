/*
 * opus_mission.h — the Opus Vector's autonomous mission controller.
 *
 * Portable C11, float32 only. It sees the car through TtMeas / TtCmd
 * (core/tt_types.h) and TtParams (core/tt_params.h), and knows nothing about
 * the simulator, the ABI or Unity: each target's adapter fills a TtMeas and
 * applies the TtCmd (targets/sim/opus_main.c in the sim). That separation is
 * what lets this same source drive a real MCU.
 *
 * The controller is handed measurements and returns commands. It never reads
 * operator input: the mission runs start to finish on sensor feedback alone.
 */
#ifndef OPUS_MISSION_H
#define OPUS_MISSION_H

#include <stdint.h>
#include "pid.h"
#include "tt_types.h"
#include "tt_params.h"
#include "tt_alloc.h"

/* Mission phases. The numeric values are published as log channel `state` and
 * are matched by the game's MissionHud, so they are part of the interface —
 * append only. */
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

/* The log frame: one float per opus_log.def entry, in table order. */
#define TT_LOG(name, unit, desc) float name;
typedef struct {
#include "opus_log.def"
} OpusLog;
#undef TT_LOG
#define OPUS_LOG_N ((uint32_t)(sizeof(OpusLog) / sizeof(float)))

typedef struct {
    const TtParams *p;     /* the car, as the firmware believes it          */
    float  rate_hz;        /* tick rate the scheduler promised (0 = unknown) */
    int    odo_l, odo_r;   /* wheels giving odometry and the heading baseline */

    OpusPhase phase;
    int   fault;           /* FA_* bitmask */

    /* Estimator state. Odometry is kept in whole encoder counts (exact at any
     * distance) plus a small float correction, and turned into metres only
     * where it is used — never a float accumulating millimetres. */
    int32_t enc_prev[TT_MAX_WHEELS];
    uint8_t enc_has_prev[TT_MAX_WHEELS];
    int32_t odo_cnt_l, odo_cnt_r;  /* accepted counts since arm */
    float  odo_corr_m;     /* calibration corrections since arm (slip)        */
    float  v_meas;         /* m/s from the tick delta */
    float  v_rear;         /* m/s from the driven wheels — slip diagnostic only */
    float  v_filt;         /* lightly filtered, for the creep loop */
    float  psi;            /* rad, heading since arm; POSITIVE = LEFT */
    float  psi_dot;        /* rad/s, fused */
    float  psi_dot_f;      /* low-passed — what the loops and exit tests use */
    float  steer_obs_rad;  /* modelled servo position, positive = left */

    /* Sequencing */
    float  phase_t;        /* s in the current phase */
    float  hold_t;
    float  settle_t;
    float  live_l, live_r;  /* accumulated odometry-wheel travel, for liveness */
    int    live_checked;
    float  tof_t;           /* how long the forward return has been short   */
    float  accel_t;         /* how long the acceleration has been excessive */
    float  leg_start_m;    /* odometer at the start of the current measured leg */
    float  stop_target_m;  /* absolute odometer value the car must stop on */
    float  psi_ref;        /* heading the straight-line loops hold */
    float  turn_t;         /* s into the turn profile */
    float  turn_cmd_rad;   /* integral of the COMMANDED yaw rate */
    tt_us_t prev_us;
    int    has_prev_us;

    /* Results, latched for reporting */
    float  leg_a_actual;
    float  turn_actual_deg;
    float  leg_b_actual;
    float  brake_actual;
    float  stop_err_mm;

    /* Loops */
    Pid spd_pid;
    Pid yaw_pid;
    TtAlloc alloc;         /* force -> wheel torques (FW-06) */
    float  wheel_omega[TT_MAX_WHEELS];   /* rad/s at the wheel, from the drives */
    uint8_t wheel_omega_ok[TT_MAX_WHEELS];

    /* Last command, mirrored for telemetry and for the brake-slip correction */
    TtCmd  cmd;
    float  t_cmd_nm;       /* torque asked of each driven wheel */
    float  a_cmd;          /* last commanded acceleration, m/s^2 (load transfer) */
    float  kappa_odo;      /* estimated drive slip of the odometry pair (mean) */
    float  slip_pct;
    float  v_ref;
    float  leg_rem;        /* signed distance left in the current measured leg */
    OpusLog log;
} OpusState;

/* Power-on. `p` must outlive the state; rate_hz is the tick rate the
 * scheduler will run (0 = unknown, which skips the standing dt check). */
void opus_init(OpusState *st, const TtParams *p, float rate_hz);

/* The car was put back at the start: forget the mission, keep the setup. */
void opus_reset(OpusState *st);

/* One control tick. */
void opus_step(OpusState *st, const TtMeas *m, TtCmd *out);

/* Odometer, metres since arm. */
float opus_odo_m(const OpusState *st);

#endif /* OPUS_MISSION_H */
