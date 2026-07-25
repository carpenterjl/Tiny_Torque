/*
 * opus_main.c — ABI adapter for the Opus Vector mission controller.
 *
 * This is the ONLY file in the mission firmware that includes controller_api.h.
 * Its whole job is to translate the host's manifest-described sensor block into
 * the plain measurement struct opus_mission.c expects, and to translate the
 * returned commands back into actuator slots. Everything else — estimation,
 * sequencing, control — is host-agnostic, so a real MCU needs a different
 * adapter and nothing else.
 *
 * Sensor binding is BY NAME, because the ABI's SensorInfo carries no wheel
 * index and actuator_index is documented as -1 for non-actuators, so there is
 * nothing to overload. The Opus Vector preset therefore guarantees the names
 * enc_fl / enc_fr (front, unpowered — the odometry source), enc_rl / enc_rr
 * (rear, driven — slip diagnostics only), tof_front, and one battery. If a
 * different vehicle loads this DLL the binding fails cleanly and the controller
 * reports a fault instead of driving off blind.
 */
#include "controller_api.h"
#include "opus_mission.h"
#include "mission_cfg.h"

#include <math.h>
#include <string.h>

static OpusState g_st;
static int   g_ready = 0;

/* Manifest bindings, resolved once in ctrl_configure. -1 = not present. */
static int g_off_enc_fl = -1, g_off_enc_fr = -1;
static int g_off_enc_rl = -1, g_off_enc_rr = -1;
static int g_off_tof    = -1;
static int g_off_batt   = -1;
static int g_motor_slot[8];
static int g_motor_count = 0;
static float g_motor_vmax = 0.0f;

enum {
    DBG_STATE = 0, DBG_FAULT, DBG_ODO, DBG_LEG_REM, DBG_V_MEAS,
    DBG_TARGET_SPEED, DBG_V_ERR, DBG_YAW_DEG, DBG_YAW_RATE, DBG_STEER,
    DBG_MOTOR_V, DBG_I_CMD, DBG_BRAKE, DBG_BATT_V, DBG_SLIP, DBG_STOP_ERR
};

/* Slot 5 is deliberately called "target_speed": SimulationRunner.SaveTelemetry
 * falls back to StepMetrics.Compute(hub, "dbg/target_speed", "veh/speed") when
 * sp/linear is flat, which it is here because this controller ignores operator
 * setpoints entirely. That name buys rise/overshoot/settling metrics in the CSV
 * sidecar for free. */
static const char *DEBUG_NAMES =
    "state,fault,odo_m,leg_rem_m,v_meas,target_speed,v_err,yaw_deg,"
    "yaw_rate,steer_cmd,motor_v,i_cmd,brake_cmd,batt_v,slip_pct,stop_err_mm";

CTRL_EXPORT int ctrl_init(float control_rate_hz)
{
    (void)control_rate_hz;   /* the mission integrates on the host's dt, not a nominal rate */
    opus_init(&g_st);
    g_ready = 1;
    return 0;
}

CTRL_EXPORT void ctrl_shutdown(void)
{
    g_ready = 0;
}

CTRL_EXPORT const char *ctrl_get_debug_names(void)
{
    return DEBUG_NAMES;
}

static int name_is(const SensorInfo *s, const char *want)
{
    return strncmp(s->name, want, 31) == 0;
}

CTRL_EXPORT void ctrl_configure(const SensorInfo *sensors, int count)
{
    int i;

    g_off_enc_fl = g_off_enc_fr = g_off_enc_rl = g_off_enc_rr = -1;
    g_off_tof = g_off_batt = -1;
    g_motor_count = 0;
    g_motor_vmax = 0.0f;

    if (sensors == 0 || count <= 0) return;

    for (i = 0; i < count; i++) {
        const SensorInfo *s = &sensors[i];
        switch (s->type) {

        case SENSOR_ENCODER:
            /* slice is [ang_vel, ticks]; we want the tick counter, which is the
             * only channel the host leaves un-noised. */
            if (s->data_count >= 2) {
                int off = s->data_offset + 1;
                if      (name_is(s, "enc_fl")) g_off_enc_fl = off;
                else if (name_is(s, "enc_fr")) g_off_enc_fr = off;
                else if (name_is(s, "enc_rl")) g_off_enc_rl = off;
                else if (name_is(s, "enc_rr")) g_off_enc_rr = off;
            }
            break;

        case SENSOR_MOTOR:
            if (s->actuator_index >= 0 && s->actuator_index < 6 && g_motor_count < 8) {
                g_motor_slot[g_motor_count++] = s->actuator_index;
                if (s->range_max > g_motor_vmax) g_motor_vmax = s->range_max;
            }
            break;

        case SENSOR_TOF:
            if (name_is(s, "tof_front") && s->data_count >= 1) g_off_tof = s->data_offset;
            break;

        case SENSOR_BATTERY:
            if (s->data_count >= 1 && g_off_batt < 0) g_off_batt = s->data_offset;
            break;

        default:
            break;
        }
    }
}

static float slice(const CtrlInputs *in, int off, float fallback)
{
    if (off < 0 || in->sensor_data == 0 || off >= in->sensor_data_len) return fallback;
    return in->sensor_data[off];
}

CTRL_EXPORT void ctrl_step(const CtrlInputs *in, CtrlOutputs *out)
{
    OpusMeas meas;
    OpusCmd  cmd;
    int i;

    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    memset(&meas, 0, sizeof(meas));
    meas.dt     = in->dt_s;
    meas.time_s = in->time_s;

    meas.enc_left_ticks  = slice(in, g_off_enc_fl, 0.0f);
    meas.enc_right_ticks = slice(in, g_off_enc_fr, 0.0f);
    meas.enc_valid       = (g_off_enc_fl >= 0 && g_off_enc_fr >= 0);

    meas.enc_rl_ticks    = slice(in, g_off_enc_rl, 0.0f);
    meas.enc_rr_ticks    = slice(in, g_off_enc_rr, 0.0f);
    meas.enc_rear_valid  = (g_off_enc_rl >= 0 && g_off_enc_rr >= 0);

    /* Body-frame gyro, Y is up: index 1 is the yaw rate. Its SIGN is not
     * assumed — opus_mission.c learns it by correlating against the
     * encoder-differential yaw rate, so a host with the opposite handedness
     * still works. */
    meas.gyro_y = in->gyro[1];
    meas.accel_mag = sqrtf(in->accel[0] * in->accel[0] +
                           in->accel[1] * in->accel[1] +
                           in->accel[2] * in->accel[2]);

    meas.batt_v      = slice(in, g_off_batt, -1.0f);
    meas.tof_front_m = slice(in, g_off_tof, 1e6f);   /* huge = "nothing ahead" */
    meas.motor_count = g_motor_count;
    meas.motor_vmax  = g_motor_vmax;

    opus_step(&g_st, &meas, &cmd);

    /* Same voltage to every motor: the two simulated motors stand in for one
     * real motor through a differential, so they are never commanded apart. */
    for (i = 0; i < g_motor_count; i++)
        out->actuator[g_motor_slot[i]] = cmd.motor_v;
    out->actuator[CTRL_STEER_ACTUATOR] = cmd.steer;
    out->actuator[CTRL_BRAKE_ACTUATOR] = cmd.brake;

    out->debug[DBG_STATE]        = (float)g_st.phase;
    out->debug[DBG_FAULT]        = (float)g_st.fault;
    out->debug[DBG_ODO]          = (float)g_st.odo_m;
    out->debug[DBG_LEG_REM]      = g_st.leg_rem;
    out->debug[DBG_V_MEAS]       = g_st.v_meas;
    out->debug[DBG_TARGET_SPEED] = g_st.v_ref;
    out->debug[DBG_V_ERR]        = g_st.v_ref - g_st.v_meas;
    out->debug[DBG_YAW_DEG]      = g_st.psi * OPUS_RAD2DEG;
    out->debug[DBG_YAW_RATE]     = g_st.psi_dot;
    out->debug[DBG_STEER]        = cmd.steer;
    out->debug[DBG_MOTOR_V]      = cmd.motor_v;
    out->debug[DBG_I_CMD]        = g_st.i_cmd;
    out->debug[DBG_BRAKE]        = cmd.brake;
    out->debug[DBG_BATT_V]       = meas.batt_v;
    out->debug[DBG_SLIP]         = g_st.slip_pct;
    out->debug[DBG_STOP_ERR]     = g_st.stop_err_mm;
}
