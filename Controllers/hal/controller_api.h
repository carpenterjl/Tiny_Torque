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
 * ABI v5 (this file) changes no layout either. It adds one OPTIONAL export,
 * ctrl_get_vehicle(), which lets a controller name the car it wants to be
 * loaded into. A DLL that does not export it is driven exactly as in v4.
 */
#ifndef CONTROLLER_API_H
#define CONTROLLER_API_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define CTRL_EXPORT __declspec(dllexport)
#else
#define CTRL_EXPORT __attribute__((visibility("default")))
#endif

#define CTRL_ABI_VERSION 5

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
    SENSOR_BATTERY    = 7   /* pack: terminal voltage, total current, SoC (2026-07 append) */
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
 *   SENSOR_BATTERY -> [terminal_V, total_current_A, soc_01]  (soc fixed 1.0 for now)
 */
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
} CtrlInputs;

/*
 * Controller -> host, once per control tick.
 *
 * actuator[] layout (ABI v3, car vehicle):
 *   actuator[SensorInfo.actuator_index] = that MOTOR's applied VOLTAGE (signed;
 *       sign chooses direction). The host clamps to the motor's ±maxVoltage
 *       (advertised as range_min/range_max in the manifest).
 *   actuator[6] = steering command in [-1, 1] (front-wheel servo angle).
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

#ifdef __cplusplus
}
#endif

#endif /* CONTROLLER_API_H */
