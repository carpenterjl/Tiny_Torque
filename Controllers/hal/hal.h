/*
 * hal.h — hardware abstraction layer.
 *
 * Portable controllers are written against this interface, never against a
 * specific board or the sim. Each *target* provides an implementation:
 *
 *   targets/sim      -> backed by CtrlInputs/CtrlOutputs from the Unity host
 *   targets/arduino  -> backed by real ADC/PWM/encoders (added later)
 *
 * STATUS: aspirational. No controller includes this header or calls these
 * functions today; every sim target wires CtrlInputs/CtrlOutputs straight into
 * its portable core (see targets/sim/opus_main.c). The replacement is the
 * push-style tt_hal.h described in SIM_TO_REAL_PLAN.md (FW-02) — until it
 * lands, treat this file as a sketch, not a contract.
 */
#ifndef HAL_H
#define HAL_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Monotonic time source (seconds). */
float hal_time_s(void);

/* Read a wheel encoder's angular velocity in rad/s. */
float hal_encoder_vel(int index);

/* Read the IMU. Fills 3-element rate (rad/s) and accel (m/s^2) buffers. */
void hal_imu_read(float gyro_out[3], float accel_out[3]);

/* Command a motor with a normalized value in [-1, 1]. */
void hal_motor_write(int index, float command);

/* Emit a named debug value for telemetry/graphing. */
void hal_debug(const char* name, float value);

#ifdef __cplusplus
}
#endif

#endif /* HAL_H */
