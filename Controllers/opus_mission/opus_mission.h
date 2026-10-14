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
#include "tt_route.h"
#include "tt_track.h"
#include "tt_vplan.h"
#include "tt_clear.h"

/* Longest command delay the prediction carries, in ticks (FW-13). */
#define OPUS_PRED_SLOTS 16

/* Friction probe samples kept (FW-18): its 0.45 s at <= 100 Hz. */
#define OPUS_PR_SAMPLES 48

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
    OPUS_DONE       = 10,
    OPUS_FOLLOW     = 11   /* following a route (NAV-04): the tracker steers, the
                              speed plan sets the speed, BRAKE stops at its end */
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
    tt_us_t enc_prev_us[TT_MAX_WHEELS];    /* the last report's stamp (FW-21) */
    uint32_t enc_prev_seq[TT_MAX_WHEELS];
    int32_t odo_cnt_l, odo_cnt_r;  /* accepted counts since arm */
    float  odo_corr_m;     /* calibration corrections since arm (slip)        */
    float  v_meas;         /* m/s from the tick delta */
    float  v_rear;         /* m/s from the driven wheels — slip diagnostic only */
    float  v_filt;         /* lightly filtered, for the creep loop */
    float  v_est;          /* m/s, car speed: accelerometer + odometer (FW-13) */
    float  ax_prev;        /* last tick's forward acceleration, m/s^2 */
    float  pitch;          /* rad, nose down +, from the gyro (FW-13) */
    float  ax_load;        /* m/s^2, ax as the axle loads have followed it */
    float  ax_land;        /* ...as they will have when this tick's command
                              lands, over the commands in flight (FW-19) */
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
    float  ax_bias;        /* m/s^2, the forward accelerometer's bias: read
                              at rest while arming, kept by the odometer (FW-22) */
    float  ab_sum;         /* ...summed over the standing check */
    int    ab_n;
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
    float  a_brake_nom;    /* ...on a floor as good as the parameters say  */
    float  stop_early_m;   /* m before the mark the brake starts: the delay
                              beyond the parameter's, at speed (FW-19)     */
    float  drive_slip;     /* largest driven-wheel slip, torque's way      */

    /* Route following (NAV-02..04). route NULL = the fixed manoeuvre. */
    const TtRoute *route;
    float  route_vmax;     /* speed the route was selected at (0 = its own) */
    TtTrack trk;           /* L4: the car's place on the route, curvature  */
    TtVPlan plan;          /* L5: speed along the route                    */
    uint32_t plan_hint;
    float  plan_mu;        /* mu_est the plan was built on                 */
    float  pose[3];        /* the navigation filter's x, y, psi (last tick) */
    int    pose_ok;

    /* The command delay, measured on the drives (FW-17). */
    int    drive_iq;       /* every driven wheel reports its current (an FOC
                              drive): the delay can be measured on it      */
    float  dly_s;          /* what the loops predict over: cmd_delay_s until
                              the launch has measured it                   */
    int    dly_state;      /* 0 waiting, 1 timing the launch's step, 2 measured,
                              3 not measurable this run                     */
    uint32_t dly_k0;       /* tick of the launch's torque step              */
    float  dly_f0;         /* the force it asked for, N                     */

    /* The friction probe on leg A (FW-18). */
    int    pr_state;       /* 0 not yet, 1 probing, 2 judged, 3 not this car or run */
    int    pr_abort;       /* a wheel slipped toward its limit: end the ramp */
    int    pr_bad;         /* a drive's current or speed missing or wild     */
    int    pr_cnt;         /* samples taken                                 */
    float  pr_last_t;      /* phase_t of the last sample                    */
    float  pr_n;           /* the force pair asked for, N per axle          */
    float  pr_hold_t;      /* s more the odometer stays on v_est            */
    float  pr_amax;        /* the front tyres' force / load reached          */
    float  pr_mu;          /* the floor's peak friction found; 0 = none      */
    float  mu_launch;      /* the launch's verdict (FW-16), for if the probe cannot judge */
    float  pr_af[OPUS_PR_SAMPLES];   /* front tyres' force / load, + = braking */
    float  pr_ar[OPUS_PR_SAMPLES];   /* rear tyres', + = driving              */
    float  pr_d[OPUS_PR_SAMPLES];    /* rear wheels' slip less the front's    */
    int    route_bad;      /* the route cannot be driven (set with it)      */

    /* The way ahead (NAV-05): a multizone ranger's clearance. */
    TtClearCfg clr;        /* the ranger and the corridor (set with the car) */
    int    clr_on;         /* the car has one                               */
    uint32_t clr_seq;      /* the frame last judged                          */
    uint32_t clr_hint;     /* route index the corridor starts from           */
    float  clear_m;        /* clear distance from the nose; TT_CLEAR_NONE    */
    int    blocked;        /* held short of something in the way             */
    float  block_t;        /* s held                                         */
    int    standing;       /* stopped at the hold: no speed loop, no torque  */
    float  clr_prev_m;     /* the frame before's clearance (closing speed)   */
    uint32_t clr_prev_us;  /* ...and its stamp                               */
    float  clr_dt;         /* s between the last two frames                  */
    float  v_close;        /* m/s what is in the way comes at the car, >= 0  */
    TtClearOut clr_out;    /* the last frame's verdict, for telemetry        */

    /* A detour beside the route (NAV-08): the map the ranger's rays build,
     * the offset profile the tracker follows, the planner's settings. */
    TtGrid grid;
    TtDetour det;
    TtDetourCfg det_cfg;   /* set with the ranger                            */
    int    det_on;         /* the car has a ranger to map with               */
    float  det_t;          /* s since the path ahead was last checked        */
    float  det_d;          /* the detour's offset at the car's place, m      */
    float  det_s;          /* where the path ahead was last found blocked, m; -1 clear */
    int    n_det;          /* detours planned this run                       */
    float  route_s;        /* where the car is on the route now, m         */
    float  v_plan;         /* the plan at the predicted place              */

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

/* Follow `r` instead of flying the fixed manoeuvre (NULL: the manoeuvre),
 * at most at v_max (<= 0: the route's own). Set before arming; it survives
 * opus_reset. A route the car cannot drive (tt_route_validate against its
 * steering lock) refuses to arm with FA_PARAMS. */
void opus_set_route(OpusState *st, const TtRoute *r, float v_max);

/* The drives report their current (every driven wheel an FOC drive): the
 * command delay is then measured at the launch (FW-17). Survives
 * opus_reset; set by the target from the car it was put in. */
void opus_set_drive_feedback(OpusState *st, int iq);

/* The navigation filter's pose after its last step (x, y, psi in the site
 * frame); ok = 0 while it has none. The tracker carries it over this tick's
 * motion and the commands in flight. */
void opus_set_pose(OpusState *st, float x, float y, float psi, int ok);

/* The car's multizone ranger and the corridor to judge its frames against
 * (NAV-05); on = 0 (or c NULL) for a car without one, which keeps the
 * single-beam abort. Survives opus_reset; set by the target. */
void opus_set_clearance(OpusState *st, const TtClearCfg *c, int on);

/* The friction probe's fit (FW-18): the floor's peak mu from n samples of
 * front and rear tyre force / load (af + = braking, ar + = driving) and the
 * rear-less-front slip d, on the brush tyre d = c + (g(ar) + g(af)) / C,
 * g(a) = 2 mu (1 - sqrt(1 - a / mu)). 0 if they do not fit. */
float opus_probe_mu(const float *af, const float *ar, const float *d, int n);

#endif /* OPUS_MISSION_H */
