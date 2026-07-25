/*
 * sim_main.c — sim target: implements the controller ABI (controller_api.h)
 * on top of the portable diffdrive_control logic.
 *
 * This is the thin adapter layer. It owns controller state, translates the
 * host's CtrlInputs into the controller's native call, and packs results plus
 * debug channels back into CtrlOutputs. A real-hardware target would replace
 * this file (reading peripherals instead of CtrlInputs) while reusing the
 * exact same diffdrive_control.c.
 */
#include "controller_api.h"
#include "diffdrive_control.h"

#include <string.h>

/* Setpoint channel assignment for this vehicle. */
#define SP_LINEAR  0   /* m/s   */
#define SP_YAW     1   /* rad/s */

/* Wheel index assignment. */
#define WHEEL_LEFT  0
#define WHEEL_RIGHT 1

/* debug[] layout — keep in sync with ctrl_get_debug_names(). */
enum {
    DBG_TARGET_WL = 0,
    DBG_TARGET_WR,
    DBG_ERR_L,
    DBG_ERR_R,
    DBG_P_L,
    DBG_I_L,
    DBG_D_L,
    DBG_COUNT
};

static DiffDriveController g_ctrl;
static int g_ready = 0;

CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    (void)control_rate_hz; /* fixed-rate loop; kept for API symmetry */

    DiffDriveConfig cfg;
    cfg.wheel_radius_m = 0.05f;   /* 100 mm wheels                       */
    cfg.track_width_m  = 0.30f;   /* 300 mm between wheels               */
    cfg.kp             = 0.08f;   /* starting gains — tune live in sim   */
    cfg.ki             = 0.30f;
    cfg.kd             = 0.0015f;

    diffdrive_init(&g_ctrl, &cfg);
    g_ready = 1;
    return 0; /* 0 = success */
}

CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) {
        return;
    }

    float cmd[2];
    diffdrive_update(&g_ctrl,
                     in->setpoint[SP_LINEAR],
                     in->setpoint[SP_YAW],
                     in->wheel_vel[WHEEL_LEFT],
                     in->wheel_vel[WHEEL_RIGHT],
                     in->dt_s,
                     cmd);

    out->actuator[WHEEL_LEFT]  = cmd[0];
    out->actuator[WHEEL_RIGHT] = cmd[1];

    out->debug[DBG_TARGET_WL] = g_ctrl.target_wl;
    out->debug[DBG_TARGET_WR] = g_ctrl.target_wr;
    out->debug[DBG_ERR_L]     = g_ctrl.target_wl - in->wheel_vel[WHEEL_LEFT];
    out->debug[DBG_ERR_R]     = g_ctrl.target_wr - in->wheel_vel[WHEEL_RIGHT];
    out->debug[DBG_P_L]       = g_ctrl.pid_left.p_term;
    out->debug[DBG_I_L]       = g_ctrl.pid_left.i_term;
    out->debug[DBG_D_L]       = g_ctrl.pid_left.d_term;
}

CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "target_wl,target_wr,err_l,err_r,p_l,i_l,d_l";
}
