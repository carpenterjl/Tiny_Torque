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

/* Longest command delay the prediction carries, in ticks (FW-13). */
#define OPUS_PRED_SLOTS 16

/* One tick's command, as far as the estimators need it back. */
typedef struct {
    float a;               /* acceleration asked for, m/s^2 */
    float r;               /* yaw rate asked for, rad/s, + = left */
    float steer;           /* road-wheel angle asked for, rad, + = left */
    float t_odo[2];        /* torque on the odometry pair (left, right), N*m */
    float f;               /* wheel force the allocator commanded, N    */
    int   lim;             /* wheels slipping near the limit (bit mask) */
} OpusSent;

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
    int   drive_ok;        /* the safety layer has armed the drives (1 when
                              there is none); ARMED waits for it to launch */

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
    float  v_est;          /* m/s, car speed: accelerometer + odometer (FW-13) */
    float  ax_prev;        /* last tick's forward acceleration, m/s^2 */
    float  pitch;          /* rad, nose down +, from the gyro (FW-13) */
    float  ax_load;        /* m/s^2, ax as the axle loads have followed it */
    int    odo_slip;       /* the odometry pair disagrees with v_est this tick */
    float  v_pred;         /* v_est carried over the commands in flight */
    float  s_pred;         /* metres the car covers while they land */
    float  psi_fly;        /* rad the car turns while they land */
    float  r_cmd;          /* yaw rate this tick asked for */
    OpusSent sent[OPUS_PRED_SLOTS];  /* what was asked for, newest last */
    int    sent_n;         /* ticks of it held */
    float  psi;            /* rad, heading since arm; POSITIVE = LEFT */
    float  psi_dot;        /* rad/s: the gyro less its bias (encoders without one) */
    float  psi_dot_f;      /* low-passed — what the loops and exit tests use */
    float  steer_obs_rad;  /* modelled servo position, positive = left */

    /* The car's own errors, learned on the way (FW-15). */
    float  gyro_bias;      /* rad/s, the gyro's reading at rest while arming */
    float  gb_sum;         /* ...summed over the standing check */
    int    gb_n;
    float  lr_eps;         /* right-minus-left odometry wheel scale: the
                              encoder differential's yaw is off by eps*v/track */
    float  steer_act_f;    /* steering acting on the car, through its lag */
    float  steer_trim;     /* rad the steering is off centre: taken off every
                              command */
    float  horn;           /* rad, the servo command last sent (the horn)   */
    float  wheel_est;      /* rad, where the road wheels sit in the play    */
    float  odo_scale;      /* true metres per odometer metre (the EKF's k_w),
                              applied to the measured legs; 1 until known */

    /* Force gain and grip (FW-16). */
    float  ax_corr;        /* m/s^2, forward acceleration, pitch removed  */
    float  fg_num, fg_den; /* least-squares sums for the force gain       */
    float  f_gain;         /* acceleration got / promised per newton; 1 until known */
    float  mu_nom;         /* tyre_mu, or 1 for a car that states none    */
    float  mu_est;         /* floor friction; mu_nom until measured lower */
    float  mu_ls[TT_MAX_WHEELS];   /* launch slip, summed (torque's way)  */
    float  mu_cs[TT_MAX_WHEELS];   /* cruise slip, summed: radius offset   */
    float  mu_ax;          /* launch |ax|, summed                         */
    int    mu_ln, mu_cn;   /* ticks in each sum                           */
    int    mu_judged;      /* the floor has been judged this run          */
    float  launch_slip;    /* what it was judged on                       */
    float  a_brake;        /* m/s^2 the stop is planned on                */
    float  drive_slip;     /* largest driven-wheel slip, torque's way      */

    /* Sequencing */
    float  phase_t;        /* s in the current phase */
    float  hold_t;
    float  stall_t;        /* s the creep has been at rest short of the mark */
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

/* The navigation filter's odometer scale (true / odometer). Taken until the
 * car brakes; the stop is flown on the scale it had then. */
void opus_set_odo_scale(OpusState *st, float k);

#endif /* OPUS_MISSION_H */
