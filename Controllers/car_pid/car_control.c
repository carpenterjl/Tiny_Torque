#include "car_control.h"

static float clampf(float v, float lo, float hi) {
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

void car_init(CarController* c, const CarConfig* cfg) {
    c->cfg = *cfg;
    pid_init(&c->speed_pid, cfg->kp, cfg->ki, cfg->kd, -1.0f, 1.0f);
    c->measured_speed = 0.0f;
    c->throttle = 0.0f;
}

void car_update(CarController* c,
                float target_speed_mps, float steer_cmd,
                float wheel_vel_avg, float dt_s,
                float out_cmd[2]) {
    c->measured_speed = wheel_vel_avg * c->cfg.wheel_radius_m;
    c->throttle = pid_update(&c->speed_pid, target_speed_mps, c->measured_speed, dt_s);

    out_cmd[0] = c->throttle;
    out_cmd[1] = clampf(steer_cmd, -1.0f, 1.0f);
}
