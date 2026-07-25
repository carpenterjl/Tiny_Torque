/*
 * car_main.c — sim target for the 4-wheel car: closes a speed loop and drives
 * the wheels by VOLTAGE (ABI v3).
 *
 * Reuses the portable car_control PID (speed -> normalized effort), then scales
 * that effort by each motor's advertised max voltage to produce the per-motor
 * voltage command written to that motor's actuator slot. Steering is passed
 * through to the steer-servo actuator.
 *
 * Setpoint convention (from CarInput on the Unity side):
 *   setpoint[0] = target forward speed (m/s)
 *   setpoint[1] = steer command [-1, 1]
 * Wheel order: FL, FR, RL, RR (rad/s).
 */
#include "controller_api.h"
#include "car_control.h"

#include <string.h>

#define MAX_MOTORS 8
#define SP_SPEED 0
#define SP_STEER 1

/* debug[] layout — keep in sync with ctrl_get_debug_names(). */
enum {
    DBG_TARGET_SPEED = 0,
    DBG_SPEED,
    DBG_EFFORT,
    DBG_MOTOR_V,
    DBG_COUNT
};

static CarController g_car;
static int g_ready = 0;

/* Drive motors discovered from the manifest. */
static int   g_motor_actuator[MAX_MOTORS];
static float g_motor_vmax[MAX_MOTORS];
static int   g_motor_count = 0;

static float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    (void)control_rate_hz;

    CarConfig cfg;
    cfg.wheel_radius_m = 0.35f; /* matches Unity CarVehicle.wheelRadius */
    cfg.kp = 0.40f;
    cfg.ki = 1.00f;
    cfg.kd = 0.00f;

    car_init(&g_car, &cfg);
    g_motor_count = 0;
    g_ready = 1;
    return 0;
}

/* v3: learn which actuator slots are motors and their voltage limits. */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    g_motor_count = 0;
    for (int i = 0; i < count; i++) {
        if (sensors[i].type == SENSOR_MOTOR && g_motor_count < MAX_MOTORS) {
            g_motor_actuator[g_motor_count] = sensors[i].actuator_index;
            g_motor_vmax[g_motor_count] = sensors[i].range_max; /* +maxVoltage */
            g_motor_count++;
        }
    }
}

CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) {
        return;
    }

    float wheel_avg = 0.25f * (in->wheel_vel[0] + in->wheel_vel[1] +
                               in->wheel_vel[2] + in->wheel_vel[3]);

    float cmd[2];
    car_update(&g_car,
               in->setpoint[SP_SPEED],
               in->setpoint[SP_STEER],
               wheel_avg,
               in->dt_s,
               cmd);

    /* cmd[0] is a normalized effort in [-1,1]; turn it into a voltage per motor. */
    float effort = clampf(cmd[0], -1.0f, 1.0f);
    float applied_v = 0.0f;
    for (int m = 0; m < g_motor_count; m++) {
        int idx = g_motor_actuator[m];
        if (idx < 0 || idx >= 8) continue;
        float vmax = g_motor_vmax[m] > 0.1f ? g_motor_vmax[m] : 24.0f;
        applied_v = effort * vmax;
        out->actuator[idx] = applied_v;
    }

    out->actuator[CTRL_STEER_ACTUATOR] = clampf(cmd[1], -1.0f, 1.0f);
    /* brake left at 0: this controller uses motor voltage (incl. reverse) only. */

    out->debug[DBG_TARGET_SPEED] = in->setpoint[SP_SPEED];
    out->debug[DBG_SPEED]        = g_car.measured_speed;
    out->debug[DBG_EFFORT]       = effort;
    out->debug[DBG_MOTOR_V]      = applied_v;
}

CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "target_speed,speed,effort,motor_v";
}
