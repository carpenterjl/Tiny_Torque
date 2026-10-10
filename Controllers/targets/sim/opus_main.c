/*
 * opus_main.c — ABI v7 adapter for the Opus Vector mission controller.
 *
 * This is the ONLY file in the mission firmware that exports the controller
 * ABI. It is three calls deep and nothing else:
 *
 *     sim_hal_read    the host's manifest-described sensor block -> TtMeas
 *     opus_app_step   the whole firmware (opus_app.h: mission, navigation,
 *                     safety, then torque -> Iq and the drives' own units)
 *     sim_hal_write   TtAct -> actuator slots
 *
 * The adapter (targets/sim/sim_hal.c) binds by TYPE and WHEEL INDEX from the
 * extended manifest (ABI-02) and describes the car to the firmware, which
 * refuses to arm with FA_PARAMS if the car does not match what its parameters
 * say (an odometry wheel without an encoder, a different wheel radius or
 * CPR, a different steering lock).
 *
 * The lockstep bridge (tools/tt_bridge.c, HIL-01/04) runs the same adapter on
 * the host and sends the TtMeas over the wire (core/tt_wire.h) to a board or
 * a host-built firmware image running opus_app_step; only the wire is in
 * between, so a run through the bridge reproduces this DLL's run exactly.
 *
 * One source, two cars: OPUS_PARAMS picks the parameter set at build time
 * (opus_controller.dll = the brushed Opus Vector, opus_foc_controller.dll =
 * the four-motor FOC twin), as a board config would on the MCU.
 */
#include "controller_api.h"
#include "sim_hal.h"
#include "opus_app.h"

#include <string.h>

#ifndef OPUS_PARAMS
#define OPUS_PARAMS tt_params_opus_vector
#endif

static OpusApp g_app;
static SimHal  g_hal;
static int     g_ready = 0;
static int     g_hal_up = 0;

/* ------------------------------------------------------------- lifecycle --*/

CTRL_DEFINE_ABI_VERSION()

CTRL_EXPORT int ctrl_init(float control_rate_hz)
{
    if (!g_hal_up) { sim_hal_init(&g_hal); g_hal_up = 1; }
    opus_app_init(&g_app, &OPUS_PARAMS, control_rate_hz);
    sim_hal_restart_clock(&g_hal);
    g_ready = 1;
    return 0;
}

CTRL_EXPORT void ctrl_reset(void)
{
    opus_app_reset(&g_app);
    sim_hal_restart_clock(&g_hal);
}

CTRL_EXPORT void ctrl_shutdown(void)
{
    g_ready = 0;
}

/* The mission is tuned at 100 Hz but rate-independent; take the host's. */
CTRL_EXPORT float ctrl_get_control_rate(void)
{
    return 0.0f;
}

CTRL_EXPORT const char *ctrl_get_debug_names(void)
{
    return opus_app_log_names();
}

CTRL_EXPORT void ctrl_configure2(const SensorInfo2 *sensors, int count)
{
    TtCarDesc car;
    sim_hal_configure(&g_hal, sensors, count, &car);
    opus_app_configure(&g_app, &car);
}

CTRL_EXPORT void ctrl_step(const CtrlInputs *in, CtrlOutputs *out)
{
    TtMeas meas;
    TtAct  act;
    const float *log;
    uint32_t i, n;

    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    sim_hal_read(&g_hal, in, &meas);
    opus_app_step(&g_app, &meas, &act);
    sim_hal_write(&g_hal, &act, out);

    log = opus_app_log(&g_app, &n);
    for (i = 0; i < n && i < 16u; i++) out->debug[i] = log[i];
}

/* The log channels after the first 16 (the safety layer and the EKF). */
CTRL_EXPORT int ctrl_get_debug_ext(float *dst, int max)
{
    uint32_t n;
    const float *log = opus_app_log(&g_app, &n);
    int i, k = (int)n - 16;
    if (dst == 0 || k <= 0) return 0;
    if (k > max) k = max;
    for (i = 0; i < k; i++) dst[i] = log[16 + i];
    return k;
}
