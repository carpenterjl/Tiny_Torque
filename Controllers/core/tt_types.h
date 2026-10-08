/*
 * tt_types.h — vehicle-level measurement and command types (FW-01).
 *
 * The portable firmware core sees the car only through these two structs:
 * TtMeas in, TtCmd out. Each target fills TtMeas from its own hardware (the
 * sim adapter from the Unity ABI, the MCU from its drivers) and turns TtCmd
 * into actuator writes, so the core itself never changes between them.
 *
 * Conventions, everywhere in this file:
 *   - SI units.
 *   - Body frame FLU: x forward, y left, z up (right-handed, ISO 8855 /
 *     ROS REP-103). Positive yaw rate and positive steer are to the LEFT.
 *   - Every measurement carries a TtStamp. `valid` says whether the source
 *     exists at all; `seq` changes only when a FRESH sample arrives, so a
 *     slow sensor read inside a fast loop is recognisable as old.
 *   - Times are tt_us_t, a free-running microsecond counter that wraps every
 *     ~71 minutes: compare times by unsigned difference, never by value.
 *
 * Plain C11, fixed-width types, no pointers: safe to memcpy, log, and send
 * over a wire between a 64-bit host and a 32-bit MCU (unlike CtrlInputs,
 * which carries pointers and so changes layout between them).
 */
#ifndef TT_TYPES_H
#define TT_TYPES_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef uint32_t tt_us_t;          /* monotonic microseconds; use differences */

enum { TT_FL = 0, TT_FR = 1, TT_RL = 2, TT_RR = 3, TT_MAX_WHEELS = 4 };

typedef struct {
    tt_us_t  t_us;                 /* when the sample was taken            */
    uint32_t seq;                  /* bumps once per fresh sample          */
    uint8_t  valid;                /* 0 = the source does not exist        */
    uint8_t  pad_[3];
} TtStamp;

/* Encoder: CUMULATIVE count of its shaft since power-up. The target unwraps
 * the hardware register, so the core only ever differences two int32s. Which
 * shaft (wheel or motor) and how many counts per turn are parameters. */
typedef struct { TtStamp st; int32_t count; } TtEnc;

/* Raw 6-axis IMU at its mount, FLU: rad/s and m/s^2 (specific force, so a
 * level car at rest reads accel = (0, 0, +9.81)). */
typedef struct { TtStamp st; float gyro[3]; float accel[3]; } TtImu;

/* Drive (motor driver) feedback for one wheel. */
typedef struct {
    TtStamp  st;
    float    iq_a;                 /* measured torque-producing current     */
    float    omega_m;              /* motor shaft speed, rad/s (0 if none)  */
    float    vbus_v;
    float    temp_c;
    uint16_t fault;                /* driver fault bits (driver-defined)    */
    uint8_t  pad_[2];
} TtDriveFb;

/* Time-of-flight ranger, single- or multizone (zones = 1 for a single beam).
 * Zone ranges in metres; status 0 = valid, anything else = no target. */
#define TT_TOF_MAX_ZONES 64
typedef struct {
    TtStamp  st;
    uint8_t  zones;
    uint8_t  status[TT_TOF_MAX_ZONES];
    uint8_t  pad_[3];
    float    range_m[TT_TOF_MAX_ZONES];
} TtTof;

typedef struct { TtStamp st; float flow_x, flow_y, quality, height_m; } TtFlow;

#define TT_MAX_UWB 4
typedef struct { TtStamp st; uint8_t anchor; uint8_t pad_[3]; float range_m, quality; } TtUwb;

typedef struct { TtStamp st; float v, i; } TtBatt;                 /* pack V, A */
typedef struct { TtStamp st; float ch[8]; uint8_t failsafe; uint8_t pad_[3]; } TtRc;
typedef struct { TtStamp st; float rad; } TtSteerFb;              /* + = left */

typedef struct {
    tt_us_t   now_us;              /* when this frame was assembled         */
    float     dt_s;                /* MEASURED time since the previous one  */
    TtEnc     enc[TT_MAX_WHEELS];
    TtDriveFb drv[TT_MAX_WHEELS];
    TtImu     imu;
    TtTof     tof;
    TtFlow    flow;
    TtUwb     uwb[TT_MAX_UWB];
    uint8_t   uwb_n;
    uint8_t   pad_[3];
    TtBatt    batt;
    TtRc      rc;
    TtSteerFb steer_fb;
} TtMeas;

typedef struct {
    tt_us_t  t_us;
    uint32_t seq;
    uint8_t  arm;                  /* 0 = drives must produce no torque     */
    uint8_t  pad_[3];
    float    wheel_torque_nm[TT_MAX_WHEELS]; /* at the wheel; + = forward,
                                                - = braking/regen           */
    float    steer_rad;            /* front road-wheel angle, + = left      */
    float    brake_01;             /* friction brake, 0..1 (0 if none)      */
} TtCmd;

/* What the navigation/control layers ask the torque allocator for (FW-06). */
typedef struct { float fx_n, mz_nm, steer_rad; } TtVehReq;

#ifdef __cplusplus
}
#endif

#endif /* TT_TYPES_H */
