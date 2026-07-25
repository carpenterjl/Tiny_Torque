#include "pid.h"

static float clampf(float v, float lo, float hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

void pid_init(Pid* pid, float kp, float ki, float kd, float out_min, float out_max) {
    pid->kp = kp;
    pid->ki = ki;
    pid->kd = kd;
    pid->out_min = out_min;
    pid->out_max = out_max;
    pid_reset(pid);
}

void pid_reset(Pid* pid) {
    pid->integrator = 0.0f;
    pid->prev_measurement = 0.0f;
    pid->initialized = 0;
    pid->p_term = pid->i_term = pid->d_term = 0.0f;
}

float pid_update(Pid* pid, float setpoint, float measurement, float dt) {
    if (dt <= 0.0f) {
        return clampf(pid->p_term + pid->i_term + pid->d_term, pid->out_min, pid->out_max);
    }

    const float error = setpoint - measurement;

    /* On the first step, seed the derivative history so it doesn't spike. */
    if (!pid->initialized) {
        pid->prev_measurement = measurement;
        pid->initialized = 1;
    }

    pid->p_term = pid->kp * error;

    /* Derivative on measurement: -kd * d(measurement)/dt */
    const float dmeas = (measurement - pid->prev_measurement) / dt;
    pid->d_term = -pid->kd * dmeas;
    pid->prev_measurement = measurement;

    /* Tentative integrator update. */
    const float integrator_next = pid->integrator + pid->ki * error * dt;
    pid->i_term = integrator_next;

    float output = pid->p_term + pid->i_term + pid->d_term;

    /* Conditional-integration anti-windup: only commit the integrator step if
     * we are not saturated, or if it moves the output back into range. */
    const float output_clamped = clampf(output, pid->out_min, pid->out_max);
    const int saturated = (output != output_clamped);
    const int integrating_into_saturation =
        (output > pid->out_max && error > 0.0f) ||
        (output < pid->out_min && error < 0.0f);

    if (!(saturated && integrating_into_saturation)) {
        pid->integrator = integrator_next;
    } else {
        pid->i_term = pid->integrator; /* keep the frozen value for telemetry */
        output = pid->p_term + pid->i_term + pid->d_term;
    }

    return clampf(output, pid->out_min, pid->out_max);
}
