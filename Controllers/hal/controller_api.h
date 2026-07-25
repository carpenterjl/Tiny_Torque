/*
 * controller_api.h — ABI contract between the Unity host and a controller DLL.
 *
 * This is the ONLY header the host (Unity) and the sim target agree on.
 * Layout is fixed and plain-C so it is stable across compilers and marshals
 * cleanly to C# blittable structs. No allocation crosses this boundary.
 *
 * Field counts here must stay in sync with ControllerInterop.cs on the C# side.
 *
 * ABI v2 (this file) appends a generic, manifest-described sensor block to
 * CtrlInputs and adds one OPTIONAL export, ctrl_configure(). Everything from
 * v1 keeps its original offset, so a v1 controller DLL (which never reads the
 * new fields and doesn't export ctrl_configure) still loads and runs.
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

#define CTRL_ABI_VERSION 3

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
    const unsigned char* cam_pixels; /* grayscale, row-major, or NULL      */
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

#ifdef __cplusplus
}
#endif

#endif /* CONTROLLER_API_H */
