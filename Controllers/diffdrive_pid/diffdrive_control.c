#include "diffdrive_control.h"

void diffdrive_init(DiffDriveController* c, const DiffDriveConfig* cfg) {
    c->cfg = *cfg;
    /* Wheel commands are normalized to [-1, 1]. */
    pid_init(&c->pid_left,  cfg->kp, cfg->ki, cfg->kd, -1.0f, 1.0f);
    pid_init(&c->pid_right, cfg->kp, cfg->ki, cfg->kd, -1.0f, 1.0f);
    c->target_wl = c->target_wr = 0.0f;
    c->cmd_left = c->cmd_right = 0.0f;
}

void diffdrive_update(DiffDriveController* c,
                      float lin_mps, float yaw_rps,
                      float meas_wl, float meas_wr,
                      float dt_s,
                      float out_cmd[2]) {
    const float r = c->cfg.wheel_radius_m;
    const float half_track = 0.5f * c->cfg.track_width_m;

    /* Mix body velocity command into per-wheel ground speeds, then to rad/s. */
    const float v_left  = lin_mps - yaw_rps * half_track;
    const float v_right = lin_mps + yaw_rps * half_track;
    c->target_wl = (r > 1e-6f) ? (v_left  / r) : 0.0f;
    c->target_wr = (r > 1e-6f) ? (v_right / r) : 0.0f;

    c->cmd_left  = pid_update(&c->pid_left,  c->target_wl, meas_wl, dt_s);
    c->cmd_right = pid_update(&c->pid_right, c->target_wr, meas_wr, dt_s);

    out_cmd[0] = c->cmd_left;
    out_cmd[1] = c->cmd_right;
}
