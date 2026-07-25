/*
 * pid.h — a small, portable PID controller.
 *
 * Pure control math: depends only on the C standard library. This is the kind
 * of code you port verbatim to an MCU. Features:
 *   - derivative on measurement (avoids derivative kick on setpoint changes)
 *   - clamped output with conditional-integration anti-windup
 */
#ifndef PID_H
#define PID_H

#ifdef __cplusplus
extern "C" {
#endif

typedef struct Pid {
    float kp, ki, kd;
    float out_min, out_max;

    float integrator;
    float prev_measurement;
    int   initialized;

    /* Last-computed terms, exposed for telemetry. */
    float p_term, i_term, d_term;
} Pid;

/* Configure gains and output limits; clears internal state. */
void  pid_init(Pid* pid, float kp, float ki, float kd, float out_min, float out_max);

/* Zero the integrator and derivative history (e.g. on re-arm). */
void  pid_reset(Pid* pid);

/* Advance one step. dt in seconds. Returns the clamped control output. */
float pid_update(Pid* pid, float setpoint, float measurement, float dt);

#ifdef __cplusplus
}
#endif

#endif /* PID_H */
