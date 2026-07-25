/*
 * hal.h — hardware abstraction layer.
 *
 * Portable controllers are written against this interface, never against a
 * specific board or the sim. Each *target* provides an implementation:
 *
 *   targets/sim      -> backed by CtrlInputs/CtrlOutputs from the Unity host
 *   targets/arduino  -> backed by real ADC/PWM/encoders (added later)
 *
 * The sim bootstrap wires controllers directly for simplicity, but keeping
 * this seam documented is what makes "write once, run in sim or on hardware"
 * a real property rather than an aspiration.
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
