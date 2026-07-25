/*
 * car_sensors.c — sim target demonstrating the ABI v3 configurable-sensor API
 * with voltage-driven motors.
 *
 * It receives the vehicle's sensor + motor manifest once via ctrl_configure(),
 * looks parts up by name/type, and each tick:
 *
 *   - holds a target forward speed with a P controller that outputs a VOLTAGE to
 *     every drive motor (written to that motor's actuator slot),
 *   - brakes (actuator[CTRL_BRAKE_ACTUATOR]) and cuts drive when the FRONT ToF
 *     sees an obstacle inside a threshold distance, and
 *   - steers (actuator[CTRL_STEER_ACTUATOR]) toward the brighter side of the
 *     camera frame — a stand-in for line/track following.
 *
 * Depends only on controller_api.h, so it doubles as a minimal v3 reference.
 */
#include "controller_api.h"

#include <string.h>

#define MAX_SENSORS 32
#define MAX_MOTORS  8
#define SP_SPEED    0   /* setpoint[0] = target forward speed (m/s) */
#define SP_STEER    1   /* setpoint[1] = base steer [-1, 1]         */

/* debug[] layout — keep in sync with ctrl_get_debug_names(). */
enum {
    DBG_TARGET_SPEED = 0,
    DBG_SPEED,
    DBG_FRONT_DIST,
    DBG_MOTOR_V,
    DBG_CAM_STEER,
    DBG_BRAKE,
    DBG_COUNT
};

static SensorInfo g_sensors[MAX_SENSORS];
static int        g_count = 0;
static int        g_tof_front = -1;

/* Drive motors discovered from the manifest. */
static int   g_motor_actuator[MAX_MOTORS];
static float g_motor_vmax[MAX_MOTORS];
static int   g_motor_count = 0;

/* Tunables. */
static const float WHEEL_RADIUS_M = 0.35f; /* matches CarVehicle.wheelRadius */
static const float KP_VOLT        = 6.0f;  /* volts per (m/s) speed error    */
static const float BRAKE_DIST_M   = 6.0f;  /* start braking inside this range */
static const float CAM_STEER_GAIN = 0.6f;

static int g_ready = 0;

static float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    (void)control_rate_hz;
    g_count = 0;
    g_tof_front = -1;
    g_motor_count = 0;
    g_ready = 1;
    return 0;
}

/* Optional v3 export: the host hands us the vehicle's sensor + motor loadout. */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    if (count > MAX_SENSORS) count = MAX_SENSORS;
    g_count = count;
    g_motor_count = 0;
    g_tof_front = -1;

    for (int i = 0; i < count; i++) {
        g_sensors[i] = sensors[i];
        if (sensors[i].type == SENSOR_TOF &&
            strncmp(sensors[i].name, "tof_front", sizeof(sensors[i].name)) == 0) {
            g_tof_front = i;
        }
        if (sensors[i].type == SENSOR_MOTOR && g_motor_count < MAX_MOTORS) {
            g_motor_actuator[g_motor_count] = sensors[i].actuator_index;
            g_motor_vmax[g_motor_count] = sensors[i].range_max; /* +maxVoltage */
            g_motor_count++;
        }
    }
}

/* Mean brightness of the left vs right half of the camera frame. */
static float camera_steer(const CtrlInputs* in) {
    if (in->cam_pixels == 0 || in->cam_width <= 1 || in->cam_height <= 0)
        return 0.0f;

    int w = in->cam_width, h = in->cam_height;
    long left = 0, right = 0;
    int half = w / 2;
    int lc = 0, rc = 0;
    for (int y = 0; y < h; y++) {
        const unsigned char* row = in->cam_pixels + (long)y * w;
        for (int x = 0; x < w; x++) {
            if (x < half) { left += row[x]; lc++; }
            else          { right += row[x]; rc++; }
        }
    }
    if (lc == 0 || rc == 0) return 0.0f;
    float lavg = (float)left / (float)lc;
    float ravg = (float)right / (float)rc;
    return CAM_STEER_GAIN * ((ravg - lavg) / 255.0f);
}

CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    /* --- Speed hold: P control on measured wheel speed -> a drive voltage --- */
    float wheel_avg = 0.25f * (in->wheel_vel[0] + in->wheel_vel[1] +
                               in->wheel_vel[2] + in->wheel_vel[3]);
    float speed = wheel_avg * WHEEL_RADIUS_M;    /* m/s */
    float target = in->setpoint[SP_SPEED];
    float drive_volt = KP_VOLT * (target - speed);

    /* --- Obstacle handling from the front ToF --- */
    float front_dist = -1.0f;
    float brake = 0.0f;
    if (g_tof_front >= 0 && in->sensor_data != 0) {
        int off = g_sensors[g_tof_front].data_offset;
        if (off >= 0 && off < in->sensor_data_len) {
            front_dist = in->sensor_data[off];
            if (front_dist < BRAKE_DIST_M) {
                brake = 1.0f - (front_dist / BRAKE_DIST_M);
                brake = clampf(brake, 0.0f, 1.0f);
                drive_volt = 0.0f;                 /* stop driving into it */
            }
        }
    }

    /* --- Write the drive voltage to each motor's actuator slot --- */
    float applied_v = 0.0f;
    for (int m = 0; m < g_motor_count; m++) {
        int idx = g_motor_actuator[m];
        if (idx < 0 || idx >= 8) continue;
        float vmax = g_motor_vmax[m] > 0.1f ? g_motor_vmax[m] : 24.0f;
        float v = clampf(drive_volt, -vmax, vmax);
        out->actuator[idx] = v;
        applied_v = v;
    }

    /* --- Steering (base command + camera assist) --- */
    float cam_steer = camera_steer(in);
    float steer = clampf(in->setpoint[SP_STEER] + cam_steer, -1.0f, 1.0f);
    out->actuator[CTRL_STEER_ACTUATOR] = steer;
    out->actuator[CTRL_BRAKE_ACTUATOR] = brake;

    out->debug[DBG_TARGET_SPEED] = target;
    out->debug[DBG_SPEED]        = speed;
    out->debug[DBG_FRONT_DIST]   = front_dist;
    out->debug[DBG_MOTOR_V]      = applied_v;
    out->debug[DBG_CAM_STEER]    = cam_steer;
    out->debug[DBG_BRAKE]        = brake;
}

CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "target_speed,speed,front_dist,motor_v,cam_steer,brake";
}
