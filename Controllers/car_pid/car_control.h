/*
 * car_control.h — portable closed-loop speed controller for the 4-wheel car.
 *
 * Firmware-grade logic: includes only pid.h and the C standard library. A PID
 * holds the commanded forward speed by modulating throttle; steering is passed
 * through from the operator setpoint. Knows nothing about Unity or the sim ABI.
 *
 * Command convention (setpoints): target forward speed (m/s) and steer [-1, 1].
 */
#ifndef CAR_CONTROL_H
#define CAR_CONTROL_H

#include "pid.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CarConfig {
    float wheel_radius_m;  /* to convert wheel rad/s -> ground speed; keep in
                              sync with the Unity CarVehicle wheelRadius */
    float kp, ki, kd;      /* speed-hold PID gains */
} CarConfig;

typedef struct CarController {
    CarConfig cfg;
    Pid speed_pid;

    /* Telemetry snapshot from the last update(). */
    float measured_speed;  /* m/s */
    float throttle;        /* [-1, 1] */
} CarController;

void car_init(CarController* c, const CarConfig* cfg);

/*
 * Run one control step.
 *   target_speed_mps : commanded forward speed (m/s)
 *   steer_cmd        : operator steering [-1, 1] (passed through)
 *   wheel_vel_avg    : mean wheel angular velocity (rad/s)
 *   dt_s             : control period (s)
 *   out_cmd          : [2] -> {throttle, steer}, each in [-1, 1]
 */
void car_update(CarController* c,
                float target_speed_mps, float steer_cmd,
                float wheel_vel_avg, float dt_s,
                float out_cmd[2]);

#ifdef __cplusplus
}
#endif

#endif /* CAR_CONTROL_H */
