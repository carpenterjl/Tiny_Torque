/*
 * controller_api.h — ABI contract between the Unity host and a controller DLL.
 *
 * This is the ONLY header the host (Unity) and the sim target agree on.
 * Layout is fixed and plain-C so it is stable across compilers and marshals
 * cleanly to C# blittable structs. No allocation crosses this boundary.
 *
 * Field counts here must stay in sync with ControllerInterop.cs on the C# side.
 *
 * ABI v2 appends a generic, manifest-described sensor block to CtrlInputs and
 * adds one OPTIONAL export, ctrl_configure(). Everything from v1 keeps its
 * original offset, so a v1 controller DLL (which never reads the new fields and
 * doesn't export ctrl_configure) still loads and runs.
 *
 * ABI v4 changes NO layout. It only pins down a convention that was previously
 * unstated: cam_pixels is TOP-DOWN (row 0 = top of the image). It used to arrive
 * bottom-up by accident of Unity's texture space. The version bump exists purely
 * so a controller written against v3 that assumed the old order has something to
 * notice — nothing about the struct moved, and any controller that treats the
 * frame symmetrically (left/right sums, whole-frame brightness) is unaffected
 * either way.
 *
 * ABI v5 changes no layout either. It adds one OPTIONAL export,
 * ctrl_get_vehicle(), which lets a controller name the car it wants to be
 * loaded into. A DLL that does not export it is driven exactly as in v4.
 *
 * ABI v7 (this file) is append-only and opt-in. A controller becomes a v7
 * controller by exporting ctrl_abi_version() (use CTRL_DEFINE_ABI_VERSION()
 * below). Only then does the host:
 *   - check the struct sizes it was compiled with, and refuse on a mismatch;
 *   - fill the v7 tail of CtrlInputs (tick, time_us, per-sensor stamps);
 *   - hand over the extended manifest through ctrl_configure2(), if exported;
 *   - deliver every vector in the FLU body frame (see "Frames" below) and take
 *     the steering command in radians, positive LEFT;
 *   - call ctrl_reset() on a respawn instead of shutdown + init, if exported;
 *   - honour ctrl_get_control_rate(), if exported.
 * A v6 or older DLL sees none of this: same frame, same signs, same calls.
 *
 * Frames (v7). SI units throughout. Body frame FLU: x forward, y left, z up
 * (ISO 8855 / ROS REP-103), right-handed, so positive yaw rate and positive
 * steer are both to the LEFT. This applies to gyro[], accel[], the
 * SENSOR_IMU slices and the RF bearings. Older controllers keep the
 * simulator's native frame (x right, y up, z forward, left-handed: a left
 * turn reads as negative gyro[1]).
 */
#ifndef CONTROLLER_API_H
#define CONTROLLER_API_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define CTRL_EXPORT __declspec(dllexport)
#else
#define CTRL_EXPORT __attribute__((visibility("default")))
#endif

/* v6 changes no layout: it appends sensor types 8-12 (colour, RF, compass,
 * bump, LED) and the LED two-slot actuator convention. A v5 DLL loads and
 * drives unchanged; it only ever sees the new tags if the car carries them. */
#define CTRL_ABI_VERSION 7

/*
 * Sensor type tags. A vehicle in the sim is assembled from these parts; the
 * host describes the loadout to the controller once via ctrl_configure().
 */
enum {
    SENSOR_TOF        = 1,  /* directional time-of-flight range finder */
    SENSOR_ENCODER    = 2,  /* wheel encoder (angular velocity + ticks) */
    SENSOR_MOTOR      = 3,  /* motor electrical feedback (V, I, torque)  */
    SENSOR_IMU        = 4,  /* strap-down IMU (also mirrored in gyro/accel) */
    SENSOR_CAMERA     = 5,  /* grayscale camera (pixels via cam_pixels)  */
    SENSOR_SUSPENSION = 6,  /* per-wheel strut: spring force, compression, tilt */
    SENSOR_BATTERY    = 7,  /* pack: terminal voltage, total current, SoC (2026-07 append) */
    SENSOR_COLOR      = 8,  /* surface colour ahead of the aim (v6 append)      */
    SENSOR_RF         = 9,  /* RF antenna: strongest-3 beacon pings (v6 append) */
    SENSOR_MAG        = 10, /* magnetometer / compass heading (v6 append)       */
    SENSOR_BUMP       = 11, /* contact switch at the mount point (v6 append)    */
    SENSOR_LED        = 12, /* actuator part: firmware-driven LED (v6 append)   */
    /* v7 tags. Reserved now so the numbers never move; the sim emits each one
     * once the matching part exists (SENSOR_STEER_FB and SENSOR_FOC_FB are
     * emitted already). */
    SENSOR_IMU6       = 13, /* raw 6-axis IMU part with a mount pose           */
    SENSOR_TOF_MZ     = 14, /* multizone ToF                                   */
    SENSOR_FLOW       = 15, /* optical flow                                    */
    SENSOR_UWB        = 16, /* UWB ranging                                     */
    SENSOR_FOC_FB     = 17, /* an FOC-driven motor: [Iq A, Id A, w_m rad/s (driver
                               PLL), Vbus V, T_winding C, fault bits]; its slot
                               carries Iq (CTRL_UNITS_AMPS_IQ). Faults: 1 over-
                               voltage, 2 over-temp derate, 4 current-limited,
                               8 voltage-limited                              */
    SENSOR_STEER_FB   = 18  /* the steering servo: describes actuator[6]       */
};

/*
 * Cars the controller may ask to be loaded into (ABI v5), for ctrl_get_vehicle()
 * below. These are the built-in designs the game's own picker offers, by name —
 * the numbers here are the stable identifier, and the host maps each one back to
 * the picker entry it names.
 *
 * Cars only. The debug VW Tiguan (a full-scale physics reference, not a playable
 * car) and the aircraft are deliberately absent: neither is something a car
 * controller can drive, so there is no value in being able to name them.
 *
 * A design you saved yourself in the garage has no number here and never will —
 * it did not exist when this header was compiled. Pick those in the menu; the
 * pick is what CTRL_VEHICLE_MENU keeps.
 */
enum {
    CTRL_VEHICLE_MENU        = 0,  /* no override — whatever the menu picked   */
    CTRL_VEHICLE_STOCK       = 1,  /* "Stock Default", the plain 1/10 chassis  */
    CTRL_VEHICLE_REAL_TWIN   = 2,  /* Real Twin 1/10 — every realism feature on */
    CTRL_VEHICLE_TT_COUPE    = 3,
    CTRL_VEHICLE_TT_BAJA     = 4,
    CTRL_VEHICLE_TT_PATROL   = 5,
    CTRL_VEHICLE_TT_RATTLETRAP = 6,
    CTRL_VEHICLE_TT_REDLINE  = 7,
    CTRL_VEHICLE_TT_HIGHWING = 8,
    CTRL_VEHICLE_TT_AUTOPIA  = 9,
    CTRL_VEHICLE_OPUS_VECTOR = 10  /* the F1TENTH-class research platform      */
};

/*
 * One manifest entry per configured sensor, in the same order its data appears
 * in CtrlInputs.sensor_data. Per-type data_count / layout of the slice
 * [data_offset .. data_offset+data_count) of sensor_data:
 *
 *   SENSOR_TOF     -> [distance_m]              (range_max on no hit)
 *   SENSOR_ENCODER -> [ang_vel_rad_s, ticks]    (ticks is a wrapped counter)
 *   SENSOR_MOTOR   -> [voltage_V, current_A, torque_Nm]
 *   SENSOR_IMU     -> [gx,gy,gz, ax,ay,az]      (mirror of gyro[]/accel[])
 *   SENSOR_CAMERA  -> (no floats; frame arrives via cam_pixels/cam_width/height)
 *   SENSOR_SUSPENSION -> [spring_force_N, compression_01, angle_deg]
 *   SENSOR_BATTERY -> [terminal_V, total_current_A, soc_01]  (soc stays 1.0 on an
 *                     infinite pack, capacitymAh 0; coulomb-counted otherwise)
 *   SENSOR_COLOR   -> [r, g, b, reflect]  all 0..1; black when nothing in range;
 *                     reflect is Rec.709 luminance (aim it down = line follower)
 *   SENSOR_RF      -> [count, id0, rssi0_dbm, bearing0_deg,
 *                             id1, rssi1_dbm, bearing1_deg,
 *                             id2, rssi2_dbm, bearing2_deg]   (10 floats)
 *                     strongest three pings; empty slot: id=-1, rssi=-100, brg=0;
 *                     bearing is signed yaw from the antenna's aim (+ = right)
 *   SENSOR_MAG     -> [heading_deg]  0..360, 0 = world +Z, clockwise
 *   SENSOR_BUMP    -> [contact_01, force_N]
 *   SENSOR_LED     -> actuator part; readback [r, g, b, lit] (post blink gate).
 *                     actuator_index is the FIRST of TWO consecutive slots:
 *                       actuator[i]   = RGB24 packed ((r<<16)|(g<<8)|b) as an
 *                                       integer-valued float (exact: <= 2^24)
 *                       actuator[i+1] = blink rate in Hz (0 = solid)
 *                     LEDs are slotted after the motors, never into 6/7 (the
 *                     reserved steer/brake slots). actuator_index -1 = no free
 *                     slot pair, display-only.
 */
/* SENSOR_STEER_FB (v7, extended manifest only): a manifest entry for the
 * steering actuator itself. actuator_index = 6, units = CTRL_UNITS_RAD,
 * range_min/max = -/+ the road-wheel angle at full lock, in radians. No data
 * (data_count 0): a hobby servo reports no position. */
typedef struct SensorInfo {
    char  name[32];        /* user-chosen sensor name (NUL-terminated)      */
    int   type;            /* SENSOR_* tag                                  */
    int   data_offset;     /* start index into CtrlInputs.sensor_data       */
    int   data_count;      /* number of floats this sensor contributes      */
    float range_min;       /* configured output range (min)                 */
    float range_max;       /* configured output range (max)                 */
    int   actuator_index;  /* v3: for MOTOR, the actuator[] slot it reads   */
                           /*     (and range_min/max = -/+ maxVoltage);     */
                           /*     -1 for non-actuator sensors               */
} SensorInfo;

/* v7: units of an actuator slot (SensorInfo2.units). */
enum {
    CTRL_UNITS_NONE    = 0,  /* not an actuator                              */
    CTRL_UNITS_VOLTS   = 1,  /* motor slot carries signed volts (brushed/ESC) */
    CTRL_UNITS_AMPS_IQ = 2,  /* motor slot carries Iq in amps (FOC drive)     */
    CTRL_UNITS_RAD     = 3   /* steering slot: road-wheel angle, + = left     */
};

/*
 * v7 extended manifest entry, handed over by ctrl_configure2(). The first
 * member is the v6 entry verbatim, so code that only needs the v6 fields can
 * read sensors[i].base. Every geometric value is FLU, metres and radians.
 */
typedef struct SensorInfo2 {
    SensorInfo base;
    int32_t wheel_index;     /* 0=FL 1=FR 2=RL 3=RR for a wheel-bound part; -1 otherwise */
    int32_t units;           /* CTRL_UNITS_* when base.actuator_index >= 0          */
    float   pos_m[3];        /* mount position from the vehicle origin               */
    float   rpy_rad[3];      /* mount orientation: roll, pitch, yaw (Z-Y-X)          */
    float   rate_hz;         /* sensor update rate; 0 = fresh every control tick     */
    float   latency_s;       /* reported values are this old                         */
    float   cpr;             /* encoder: counts per rev of its shaft; 0 otherwise    */
    float   wrap;            /* encoder: tick counter wraps at this; 0 = never       */
    float   gear_ratio;      /* motor: motor:wheel. encoder: shaft:wheel. else 0     */
    float   kt;              /* motor torque constant, N*m/A, motor side; 0 if n/a   */
    float   resistance_ohm;  /* motor winding resistance; 0 if n/a                   */
    float   efficiency;      /* motor gearbox efficiency 0..1; 0 if n/a              */
    float   wheel_radius_m;  /* radius of the bound wheel; 0 if not wheel-bound      */
    int32_t truth_only;      /* 1 = simulator ground truth with no real counterpart  */
    float   reserved[8];     /* zero; room to grow without changing the stride       */
} SensorInfo2;

/* v7: when a sensor's latest value was taken. One entry per manifest entry,
 * in manifest order. seq increments once per FRESH sample, so a reading held
 * over from an earlier tick (a 15 Hz ToF inside a 100 Hz loop) keeps its seq;
 * t_sample_us is the sim time of that sample on the time_us clock (low 32
 * bits: compare by difference). Both stay 0 for an entry with no data. */
typedef struct SensorStamp {
    uint32_t seq;
    uint32_t t_sample_us;
} SensorStamp;

/* v7: CtrlInputs.flags bits. */
#define CTRL_IN_FLU 0x1u     /* vectors are in the FLU frame (always set for v7) */

/* Host -> controller, once per control tick. */
typedef struct CtrlInputs {
    float time_s;        /* seconds since sim start                        */
    float dt_s;          /* control period for this tick (s)               */
    float gyro[3];       /* body angular rate x,y,z (rad/s)                */
    float accel[3];      /* body specific force x,y,z (m/s^2)              */
    float wheel_vel[4];  /* measured wheel angular velocity (rad/s)        */
    float setpoint[4];   /* operator commands (meaning is vehicle-defined) */

    /* --- v2: generic configurable-sensor block --- */
    const float* sensor_data;        /* flat array; layout per manifest    */
    int   sensor_count;              /* entries in the manifest            */
    int   sensor_data_len;           /* total floats in sensor_data        */
    /* Grayscale frame, or NULL. Row-major, 1 byte per pixel, and ROW 0 IS THE
     * TOP of the image: pixel (x,y) is cam_pixels[y*cam_width + x] with y
     * counting DOWN from the top edge. (v4; v3 and earlier shipped it bottom-up
     * without ever saying so.) */
    const unsigned char* cam_pixels;
    int   cam_width;                 /* camera frame width  (0 if no cam)  */
    int   cam_height;                /* camera frame height (0 if no cam)  */

    /* --- v7: filled only for a controller that exports ctrl_abi_version() --- */
    uint32_t tick;                   /* control ticks since the run started */
    uint32_t flags;                  /* CTRL_IN_* bits                      */
    uint64_t time_us;                /* sim time, exact integer microseconds */
    const SensorStamp* stamps;       /* sensor_count entries                */
} CtrlInputs;

/*
 * Controller -> host, once per control tick.
 *
 * actuator[] layout (ABI v3, car vehicle):
 *   actuator[SensorInfo.actuator_index] = that MOTOR's applied VOLTAGE (signed;
 *       sign chooses direction). The host clamps to the motor's ±maxVoltage
 *       (advertised as range_min/range_max in the manifest). v7: the slot's
 *       SensorInfo2.units says what it carries (volts today; Iq in amps for
 *       an FOC drive).
 *   actuator[6] = steering command in [-1, 1] (front-wheel servo angle,
 *       positive RIGHT). v7: the road-wheel angle in RADIANS, positive LEFT,
 *       clamped to the SENSOR_STEER_FB entry's range.
 *   actuator[7] = brake in [0, 1].
 * Motor slots occupy indices 0..5 (>= steering/brake never overlap for <= 6
 * motors). A wheel with no motor free-rolls.
 */
typedef struct CtrlOutputs {
    float actuator[8];   /* motor volts (per manifest) + steer[6] + brake[7] */
    float debug[16];     /* free channels, auto-graphed by name             */
} CtrlOutputs;           /* 24 floats / 96 bytes */

#define CTRL_STEER_ACTUATOR 6
#define CTRL_BRAKE_ACTUATOR 7

/*
 * Lifecycle exports. The sim target implements these; the portable control
 * code in common/ and the per-vehicle controllers never see them directly.
 */
CTRL_EXPORT int         ctrl_init(float control_rate_hz);
CTRL_EXPORT void        ctrl_step(const CtrlInputs* in, CtrlOutputs* out);
CTRL_EXPORT void        ctrl_shutdown(void);
CTRL_EXPORT const char* ctrl_get_debug_names(void); /* comma-separated labels for debug[] */

/*
 * OPTIONAL export (v2). If present, the host calls it exactly once, right after
 * ctrl_init, to hand over the vehicle's sensor manifest. Controllers that don't
 * export it are driven exactly as in v1. The pointer is valid only for the
 * duration of the call — copy anything you need to keep.
 */
CTRL_EXPORT void        ctrl_configure(const SensorInfo* sensors, int count);

/*
 * OPTIONAL export (v5). One of the CTRL_VEHICLE_* values above: the car this
 * controller wants to be loaded into. CTRL_VEHICLE_MENU (0) — and a DLL that
 * does not export this at all — means "whatever the menu picked", which is the
 * behaviour every controller had before v5.
 *
 * Called from the Simulate Controller screen BEFORE the car is built, and
 * therefore before ctrl_init: at that point there is no car to configure and no
 * state to consult, so this must answer from a constant. Anything else is a
 * question asked too early. It is not called again once a session is running —
 * a Build & Reload swaps the code driving the car, never the car.
 *
 * The host is the authority on what it can honour: a number it does not
 * recognise, or a car the player has not unlocked, is reported on the console
 * and the menu's own pick stands.
 */
CTRL_EXPORT int         ctrl_get_vehicle(void);

/*
 * v7 exports. ctrl_abi_version() is what makes a DLL a v7 controller; the
 * others are optional on top of it.
 *
 * ctrl_abi_version: return CTRL_ABI_VERSION and write sizeof(CtrlInputs) and
 *   sizeof(CtrlOutputs) as compiled. The host refuses to drive a DLL whose
 *   sizes differ from its own. CTRL_DEFINE_ABI_VERSION() writes it for you.
 * ctrl_configure2: like ctrl_configure, with the extended manifest. Called
 *   instead of ctrl_configure when both are exported. The pointer is valid
 *   only for the call.
 * ctrl_reset: the car was teleported home (respawn, run restart). Drop
 *   estimator and mission state; keep anything ctrl_init set up once. Without
 *   it the host runs ctrl_shutdown + ctrl_init + configure, as before.
 * ctrl_get_control_rate: the rate this firmware wants to be ticked at, in Hz;
 *   0 = the host's default. Asked before ctrl_init. The host runs the nearest
 *   whole divisor of its physics rate and passes the result to ctrl_init.
 */
CTRL_EXPORT int         ctrl_abi_version(int* sizeof_inputs, int* sizeof_outputs);
CTRL_EXPORT void        ctrl_configure2(const SensorInfo2* sensors, int count);
CTRL_EXPORT void        ctrl_reset(void);
CTRL_EXPORT float       ctrl_get_control_rate(void);

#define CTRL_DEFINE_ABI_VERSION()                                             \
    CTRL_EXPORT int ctrl_abi_version(int* sizeof_inputs, int* sizeof_outputs) \
    {                                                                         \
        if (sizeof_inputs)  *sizeof_inputs  = (int)sizeof(CtrlInputs);        \
        if (sizeof_outputs) *sizeof_outputs = (int)sizeof(CtrlOutputs);       \
        return CTRL_ABI_VERSION;                                              \
    }

#ifdef __cplusplus
}
#endif

#endif /* CONTROLLER_API_H */
