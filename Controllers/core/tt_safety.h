/*
 * tt_safety.h — the vehicle safety layer (FW-08).
 *
 * A supervisor that sits between the control core and the actuators. Every
 * tick it sees what the car measured (TtMeas) and what the core asked for
 * (TtCmd), and passes the command on, replaces it with a stop, or zeroes it:
 *
 *     core_step(&core, &meas, &cmd);
 *     tt_safety_step(&safe, &meas, &cmd, &pose, &out);
 *     tt_hal_write(&out);
 *
 * It shares nothing with the core but the measurement frame, so a core that
 * misbehaves (hangs, emits NaN, keeps driving) is still caught. It runs
 * identically in the sim, in host tests and on the MCU; the faults it guards
 * against are injected by VAL-11 (tests/tt_fault.h in CTest, the sim's
 * FaultInjector end to end).
 *
 * States:
 *   DISARMED  no torque. Arms when the core asks (cmd.arm) and every arming
 *             condition holds: no fault, the car at rest, the RC arm switch
 *             on and its kill switch off (when a receiver is fitted), the
 *             pack above its arming voltage, every required sensor fresh.
 *   ARMED     the core's command passes through unchanged.
 *   DRIVING   the same, while the car moves or torque is asked for.
 *   FAULT     a fault tripped. The response depends on the fault:
 *             category 0 (the drive or the core cannot be trusted): zero
 *             torque at once, steering held at the last good command;
 *             category 1 (the drives are fine): a controlled stop on motor
 *             torque at `stop_decel`, then zero torque once at rest.
 *             FAULT goes back to DISARMED only when no fault is active, the
 *             latched ones have been cleared, and the core has dropped its
 *             arm request — re-arming always needs a fresh request.
 *
 * Latched faults (kill switch, watchdog, overrun, NaN command, driver trip,
 * tip-over, encoder failure) clear only through tt_safety_reset() — an
 * operator reset, which on the car is a power cycle — or by cycling the RC
 * arm switch off and on once the condition has gone.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_SAFETY_H
#define TT_SAFETY_H

#include <stdint.h>
#include "tt_types.h"
#include "tt_params.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    TT_SAFE_DISARMED = 0,
    TT_SAFE_ARMED    = 1,
    TT_SAFE_DRIVING  = 2,
    TT_SAFE_FAULT    = 3
} TtSafeState;

/* Fault bits. Published as log channel `safe_fault`, so append only. */
#define TT_SF_KILL        0x0001u  /* RC kill switch                    0, latched */
#define TT_SF_RC_LOST     0x0002u  /* receiver failsafe, frames lost or stale   1 */
#define TT_SF_RC_DISARM   0x0004u  /* RC arm switch turned off while armed      1 */
#define TT_SF_WATCHDOG    0x0008u  /* the core stopped producing commands  0, latched */
#define TT_SF_OVERRUN     0x0010u  /* a tick came far too late             0, latched */
#define TT_SF_NAN         0x0020u  /* the core commanded a non-finite value 0, latched */
#define TT_SF_DRV_TRIP    0x0040u  /* a driver reports a trip (OV, timeout, ...) 0, latched */
#define TT_SF_DRV_HOT     0x0080u  /* a winding above its temperature limit     1 */
#define TT_SF_DRV_STALE   0x0100u  /* a driver's feedback stopped               0 */
#define TT_SF_ENC         0x0200u  /* odometry encoder stale or glitching  1, latched */
#define TT_SF_IMU         0x0400u  /* IMU stale or non-finite                   1 */
#define TT_SF_BATT        0x0800u  /* pack sense stale                          1 */
#define TT_SF_UNDERVOLT   0x1000u  /* pack below the cutoff, debounced          1 */
#define TT_SF_TIPOVER     0x2000u  /* the car is on its side or roof   0, latched */
#define TT_SF_GEOFENCE    0x4000u  /* position outside the fence                1 */
#define TT_SF_POS_LOST    0x8000u  /* position fix lost after it was held       1 */

/* Category 0: zero torque at once. Everything else stops on the motors. */
#define TT_SF_CAT0   (TT_SF_KILL | TT_SF_WATCHDOG | TT_SF_OVERRUN | TT_SF_NAN | \
                      TT_SF_DRV_TRIP | TT_SF_DRV_STALE | TT_SF_TIPOVER)
#define TT_SF_LATCHED (TT_SF_KILL | TT_SF_WATCHDOG | TT_SF_OVERRUN | TT_SF_NAN | \
                       TT_SF_DRV_TRIP | TT_SF_TIPOVER | TT_SF_ENC)

/* TtDriveFb.fault bits the driver reports (SENSOR_FOC_FB field 5). */
#define TT_DRV_OVERVOLT    0x01u   /* bus over-voltage: bridge off          */
#define TT_DRV_OVERTEMP    0x02u   /* derating for temperature              */
#define TT_DRV_ILIMIT      0x04u   /* current-limited (normal operation)    */
#define TT_DRV_VLIMIT      0x08u   /* voltage-limited (normal operation)    */
#define TT_DRV_CMD_TIMEOUT 0x10u   /* no command frame in time: bridge off  */
#define TT_DRV_SENSOR      0x20u   /* rotor position sensor lost            */
#define TT_DRV_OVERCURRENT 0x40u   /* phase over-current trip               */

/* TtRc.failsafe bits. */
#define TT_RC_FAILSAFE     0x01u   /* the receiver declared failsafe        */
#define TT_RC_FRAME_LOST   0x02u   /* this frame was not received           */

typedef struct {
    /* timing */
    float    tick_max_s;        /* a tick later than this is an overrun          */
    uint32_t wd_ticks;          /* core commands unchanged this many ticks       */
    /* staleness: no fresh sample for this long */
    float    stale_drive_s, stale_enc_s, stale_imu_s, stale_batt_s, stale_rc_s;
    /* RC (used when a receiver is present, required when rc_required) */
    uint8_t  rc_required;
    uint8_t  rc_arm_ch;         /* channel of the arm switch (on = > 0.5)        */
    uint8_t  rc_kill_ch;        /* channel of the kill switch (kill = > 0.5)     */
    uint8_t  pad_;
    float    rc_lost_s;         /* frames lost this long = link lost             */
    /* drivers */
    uint16_t drv_trip_mask;     /* TT_DRV_* bits that are a trip                 */
    uint16_t pad2_;
    float    drv_temp_max_c;
    /* encoders */
    float    v_plausible;       /* faster than this (m/s) is a glitch            */
    uint32_t enc_glitch_max;    /* this many glitches inside enc_glitch_win_s    */
    float    enc_glitch_win_s;
    float    enc_mismatch_s;    /* an encoder disagreeing with its own wheel's
                                   driver by more than 0.3 m/s + 20 % this long
                                   (a stuck or slipping encoder); 0 = off       */
    /* pack */
    float    uv_cutoff_v;       /* below: under-voltage (0 = 3.2 V/cell of v_rail_nom / 4.2) */
    float    uv_arm_v;          /* will not arm below (0 = 3.5 V/cell)           */
    float    uv_debounce_s;
    /* attitude */
    float    tilt_max_rad;      /* specific force this far from body z = tipped  */
    float    tilt_s;
    /* position */
    float    fence[4];          /* x_min, x_max, y_min, y_max; x_max <= x_min = off */
    float    pos_timeout_s;
    /* the stop */
    float    stop_decel;        /* m/s^2 asked of a category-1 stop             */
    float    stop_v_taper;      /* the stop torque fades out below this (m/s)   */
    float    rest_v;            /* at rest below this (m/s) ...                 */
    float    rest_s;            /* ... for this long                            */
    float    stop_max_s;        /* a category-1 stop gives up after this        */
} TtSafetyCfg;

/* Position for the geofence, from the navigation filter. age_s is how long
 * since a measurement last corrected it; valid = 0 before the first fix. */
typedef struct { float x, y, age_s; uint8_t valid; uint8_t pad_[3]; } TtSafetyPose;

typedef struct {
    uint32_t seq;
    float    since_s;           /* time since seq last changed                  */
    uint8_t  seen;
    uint8_t  pad_[3];
} TtFresh;

typedef struct {
    TtSafetyCfg     cfg;
    const TtParams *p;
    uint8_t  state;             /* TtSafeState                                   */
    uint8_t  cat;               /* response in force: 0, 1, or 255 = none        */
    uint8_t  rc_present;
    uint8_t  arm_sw_prev;
    uint32_t active;            /* faults whose condition holds this tick        */
    uint32_t latched;           /* latched faults not yet cleared                */
    uint32_t faults;            /* active | latched: what the log shows          */
    uint32_t first;             /* the first fault of this episode              */
    float    t_s;               /* time since init, s                           */
    float    t_fault_s;         /* time in FAULT                                */
    float    v;                 /* the layer's own speed estimate, m/s          */
    uint8_t  v_ok;
    uint8_t  pad_[3];
    float    rest_t;            /* time spent at rest                           */
    float    uv_t, tilt_t, rc_lost_t;
    uint32_t cmd_seq_prev, cmd_same;
    uint8_t  cmd_seen;
    uint8_t  pos_held;          /* a fix was held at some point                 */
    uint8_t  stopped;           /* the category-1 stop has finished             */
    uint8_t  pad2_;
    float    steer_hold;        /* last good steer command                      */
    int32_t  enc_prev[TT_MAX_WHEELS];
    uint8_t  enc_has_prev[TT_MAX_WHEELS];
    float    glitch_t[4];       /* times of the latest glitches (ring)          */
    float    mismatch_t[TT_MAX_WHEELS];
    uint32_t glitch_n, glitches;
    TtFresh  f_drv[TT_MAX_WHEELS], f_enc[TT_MAX_WHEELS], f_imu, f_batt, f_rc;
} TtSafety;

void tt_safety_default_cfg(TtSafetyCfg *c);

/* Power-on. `p` must outlive the state. */
void tt_safety_init(TtSafety *s, const TtSafetyCfg *c, const TtParams *p);

/* Operator reset (a power cycle, or the car put back at the start): back to
 * DISARMED with every latch cleared. */
void tt_safety_reset(TtSafety *s);

/* One tick. `core` is what the control core asked for; `pose` may be NULL
 * (no geofence). `out` receives the command to apply. `out` may be `core`. */
void tt_safety_step(TtSafety *s, const TtMeas *m, const TtCmd *core,
                    const TtSafetyPose *pose, TtCmd *out);

/* The fence as the box of the given anchors, shrunk by `margin_m` on every
 * side: the UWB geometry is good only inside the anchors, so the space they
 * survey is the space the car may use. Needs at least three anchors. */
void tt_safety_fence_from_anchors(TtSafetyCfg *c, const float (*anchor)[3], int n,
                                  float margin_m);

#ifdef __cplusplus
}
#endif

#endif /* TT_SAFETY_H */
