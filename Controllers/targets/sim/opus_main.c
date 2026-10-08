/*
 * opus_main.c — ABI v7 adapter for the Opus Vector mission controller.
 *
 * This is the ONLY file in the mission firmware that includes controller_api.h.
 * It plays the part the HAL plays on the car (hal/tt_hal.h): it turns the
 * host's manifest-described sensor block into a TtMeas, runs the portable
 * core, and turns the TtCmd back into actuator slots. Everything else —
 * estimation, sequencing, control — is host-agnostic.
 *
 * Binding is by TYPE and WHEEL INDEX from the extended manifest (ABI-02), not
 * by name: encoders and motors by wheel_index, the ToF by being the one that
 * looks forward, the steering range from the SENSOR_STEER_FB entry. If the
 * car does not match what the parameters say (an odometry wheel without an
 * encoder, a different wheel radius or CPR), the controller refuses to arm
 * with FA_PARAMS instead of driving on numbers that disagree.
 *
 * The motor slots of this car carry VOLTS (a brushed motor behind a hobby
 * ESC). The core asks for wheel torque; tt_torque_to_iq() turns that into the
 * current the firmware believes it needs — the same conversion an FOC car
 * runs — and the virtual driver below turns current into the ESC's volts
 * using the motor's TRUE constants from the manifest, as a real current loop
 * would. When the sim gains an Iq drive (ACT-01) the slot reports
 * CTRL_UNITS_AMPS_IQ and the current goes through unchanged.
 */
#include "controller_api.h"
#include "opus_mission.h"
#include "mission_cfg.h"

#include <math.h>
#include <string.h>

static OpusState g_st;
static TtParams  g_params;
static int       g_ready = 0;
static float     g_rate_hz = 0.0f;

/* ESC input deadband: below it the hobby ESC outputs nothing, so the driver
 * pushes small genuine requests up to it rather than letting them vanish. */
#define ESC_DEADBAND_V   0.10f
#define DRIVE_IQ_EPS_A   0.035f   /* ~0.05 N of road force on this car */

typedef struct {
    int   bound;
    int   idx;            /* manifest index, for the stamp */
    int   off;            /* sensor_data offset of the tick register */
    float wrap;
    float rad_per_count;  /* true geometry, for the driver's speed estimate */
    int32_t cum;          /* unwrapped count */
    int   prev_raw, has_prev;
    float omega_wheel;    /* rad/s from the last count delta */
} EncBind;

typedef struct {
    int   bound;
    int   idx, off;       /* [V, I, torque] */
    int   slot;           /* actuator index */
    int   units;
    float kt, r, gear, vmax;
} MotBind;

static EncBind g_enc[TT_MAX_WHEELS];
static MotBind g_mot[TT_MAX_WHEELS];
static int   g_tof_idx = -1, g_tof_off = -1;
static float g_tof_max = 0.0f;
static int   g_batt_idx = -1, g_batt_off = -1;
static int   g_cfg_fault = 0;
static uint32_t g_prev_us = 0;
static int   g_has_prev_us = 0;
static char  g_debug_names[512];

/* ------------------------------------------------------------- lifecycle --*/

CTRL_DEFINE_ABI_VERSION()

static void build_debug_names(void)
{
    static const char *names[] = {
#define TT_LOG(name, unit, desc) #name,
#include "opus_log.def"
#undef TT_LOG
    };
    size_t i, n = sizeof(names) / sizeof(names[0]), len = 0;
    g_debug_names[0] = '\0';
    for (i = 0; i < n && i < 16; i++) {
        size_t k = strlen(names[i]);
        if (len + k + 2 >= sizeof(g_debug_names)) break;
        if (i > 0) g_debug_names[len++] = ',';
        memcpy(g_debug_names + len, names[i], k);
        len += k;
        g_debug_names[len] = '\0';
    }
}

CTRL_EXPORT int ctrl_init(float control_rate_hz)
{
    g_params = tt_params_opus_vector;
    g_rate_hz = control_rate_hz;
    opus_init(&g_st, &g_params, control_rate_hz);
    build_debug_names();
    g_has_prev_us = 0;
    g_ready = 1;
    return 0;
}

CTRL_EXPORT void ctrl_reset(void)
{
    opus_reset(&g_st);
    g_has_prev_us = 0;
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
    if (g_debug_names[0] == '\0') build_debug_names();
    return g_debug_names;
}

static int differs(float a, float b, float tol)
{
    float d = a - b;
    float m = fabsf(b) > 1e-9f ? fabsf(b) : 1.0f;
    return fabsf(d) > tol * m;
}

CTRL_EXPORT void ctrl_configure2(const SensorInfo2 *sensors, int count)
{
    int i, w;

    memset(g_enc, 0, sizeof(g_enc));
    memset(g_mot, 0, sizeof(g_mot));
    g_tof_idx = g_tof_off = g_batt_idx = g_batt_off = -1;
    g_tof_max = 0.0f;
    g_cfg_fault = 0;

    if (sensors == 0 || count <= 0) { g_cfg_fault = FA_NO_MANIFEST; return; }

    for (i = 0; i < count; i++) {
        const SensorInfo2 *s = &sensors[i];
        w = s->wheel_index;
        switch (s->base.type) {

        case SENSOR_ENCODER:
            /* slice is [ang_vel, ticks]; we want the tick counter, which is the
             * only channel the host leaves un-noised. */
            if (w >= 0 && w < TT_MAX_WHEELS && s->base.data_count >= 2 && !g_enc[w].bound) {
                float c = s->cpr * (s->gear_ratio > 0.0f ? s->gear_ratio : 1.0f);
                g_enc[w].bound = 1;
                g_enc[w].idx = i;
                g_enc[w].off = s->base.data_offset + 1;
                g_enc[w].wrap = s->wrap;
                g_enc[w].rad_per_count = c > 0.0f ? 2.0f * OPUS_PI / c : 0.0f;
                /* The firmware's own beliefs must match the car it was put in. */
                if ((g_params.odo_wheel_mask & (1u << w)) &&
                    (differs(s->wheel_radius_m, g_params.wheel_radius_m, 0.01f) ||
                     differs(c, g_params.enc_cpr[w] * g_params.enc_ratio[w], 0.001f)))
                    g_cfg_fault |= FA_PARAMS;
            }
            break;

        case SENSOR_MOTOR:
            if (w >= 0 && w < TT_MAX_WHEELS && s->base.actuator_index >= 0 &&
                s->base.actuator_index < CTRL_STEER_ACTUATOR && !g_mot[w].bound) {
                g_mot[w].bound = 1;
                g_mot[w].idx = i;
                g_mot[w].off = s->base.data_offset;
                g_mot[w].slot = s->base.actuator_index;
                g_mot[w].units = s->units;
                g_mot[w].kt = s->kt;
                g_mot[w].r = s->resistance_ohm;
                g_mot[w].gear = s->gear_ratio;
                g_mot[w].vmax = s->base.range_max;
            }
            break;

        case SENSOR_TOF:
            /* The forward-looking one: aimed within 20 deg of x, in yaw and
             * in pitch (a downward line sensor also has yaw 0). */
            if (g_tof_idx < 0 && s->base.data_count >= 1 &&
                fabsf(s->rpy_rad[2]) < 0.35f && fabsf(s->rpy_rad[1]) < 0.35f) {
                g_tof_idx = i;
                g_tof_off = s->base.data_offset;
                g_tof_max = s->base.range_max;
            }
            break;

        case SENSOR_BATTERY:
            if (g_batt_idx < 0 && s->base.data_count >= 2) {
                g_batt_idx = i;
                g_batt_off = s->base.data_offset;
            }
            break;

        case SENSOR_STEER_FB:
            /* Full lock as the car states it; the firmware's own belief is
             * max_steer_rad, and the two must agree. */
            if (differs(s->base.range_max, g_params.max_steer_rad, 0.02f))
                g_cfg_fault |= FA_PARAMS;
            break;

        default:
            break;
        }
    }

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if ((g_params.odo_wheel_mask & (1u << w)) && !g_enc[w].bound) g_cfg_fault |= FA_NO_ENCODERS;
        if ((g_params.driven_mask & (1u << w)) && !g_mot[w].bound)    g_cfg_fault |= FA_NO_MOTORS;
    }
}

/* ------------------------------------------------------------ the HAL part --*/

static float slice(const CtrlInputs *in, int off, float fallback)
{
    if (off < 0 || in->sensor_data == 0 || off >= in->sensor_data_len) return fallback;
    return in->sensor_data[off];
}

static void stamp(TtStamp *st, const CtrlInputs *in, int idx)
{
    st->valid = 1;
    if (in->stamps != 0 && idx >= 0 && idx < in->sensor_count) {
        st->seq  = in->stamps[idx].seq;
        st->t_us = in->stamps[idx].t_sample_us;
    } else {
        st->seq  = in->tick;
        st->t_us = (tt_us_t)in->time_us;
    }
}

static void read_meas(const CtrlInputs *in, TtMeas *m)
{
    int w, k;
    tt_us_t now = (tt_us_t)in->time_us;

    memset(m, 0, sizeof(*m));
    m->now_us = now;
    /* dt from the timestamps, not the host's nominal period (FW-05). */
    m->dt_s = g_has_prev_us ? (float)(uint32_t)(now - g_prev_us) * 1e-6f : in->dt_s;
    g_prev_us = now;
    g_has_prev_us = 1;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        EncBind *e = &g_enc[w];
        int raw, d;
        if (!e->bound) continue;
        raw = (int)lroundf(slice(in, e->off, 0.0f));
        if (!e->has_prev) { e->prev_raw = raw; e->has_prev = 1; d = 0; }
        else {
            /* Unwrap the 16-bit register into a cumulative count: the job a
             * real driver does with a timer in encoder mode. */
            d = raw - e->prev_raw;
            if (e->wrap > 0.0f) {
                int half = (int)(e->wrap * 0.5f), full = (int)e->wrap;
                if (d > half) d -= full; else if (d < -half) d += full;
            }
            e->prev_raw = raw;
        }
        e->cum += d;
        e->omega_wheel = m->dt_s > 1e-6f ? (float)d * e->rad_per_count / m->dt_s : 0.0f;
        m->enc[w].count = e->cum;
        stamp(&m->enc[w].st, in, e->idx);
    }

    /* The host delivers the IMU in FLU for a v7 controller (CTRL_IN_FLU). */
    for (k = 0; k < 3; k++) { m->imu.gyro[k] = in->gyro[k]; m->imu.accel[k] = in->accel[k]; }
    m->imu.st.valid = (in->flags & CTRL_IN_FLU) ? 1 : 0;   /* never guess a frame */
    m->imu.st.seq = in->tick;
    m->imu.st.t_us = now;

    if (g_batt_off >= 0) {
        m->batt.v = slice(in, g_batt_off, 0.0f);
        m->batt.i = slice(in, g_batt_off + 1, 0.0f);
        stamp(&m->batt.st, in, g_batt_idx);
    }

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        MotBind *mt = &g_mot[w];
        if (!mt->bound) continue;
        m->drv[w].iq_a = slice(in, mt->off + 1, 0.0f);
        m->drv[w].vbus_v = m->batt.v;
        m->drv[w].omega_m = g_enc[w].bound ? g_enc[w].omega_wheel * mt->gear : 0.0f;
        stamp(&m->drv[w].st, in, mt->idx);
    }

    if (g_tof_off >= 0) {
        float r = slice(in, g_tof_off, 0.0f);
        m->tof.zones = 1;
        m->tof.range_m[0] = r;
        m->tof.status[0] = (g_tof_max > 0.0f && r >= g_tof_max - 1e-3f) ? 1 : 0; /* no target */
        stamp(&m->tof.st, in, g_tof_idx);
    }
}

/* The virtual driver for a VOLTS slot: brushed motor behind a hobby ESC.
 * Drive: the volts that push iq through the winding against back-EMF.
 * Brake: the ESC brakes by shorting the winding at a duty, giving
 * I = duty * kt * w_m / R, so the duty for iq follows — strong at speed,
 * nothing at rest. A negative command while rolling forward IS that duty,
 * scaled to the motor's rated voltage (what the host's ESC divides by).
 *
 * The back-EMF is sized from the VEHICLE speed (the core's odometry), not
 * from the driven wheel's own encoder. That keeps this a voltage drive, as
 * the mission was tuned on: when a driven wheel spins up, its back-EMF eats
 * the headroom and the current falls, which limits wheelspin by itself.
 * Sizing it from the wheel's own speed makes it a true current drive (what
 * an FOC car has) — and then the 6 m/s^2 launch asks the rear axle for more
 * than its grip and spins the wheels. That is the car the firmware will
 * really drive, so it is Phase 2's to handle, with ACT-01 (Iq drive) and
 * FW-06 (traction control); this adapter keeps Phase 1 a pure interface
 * change. */
static float esc_volts(const MotBind *mt, float iq, float omega_wheel, float batt_v, float w_floor)
{
    float w_m = mt->gear * omega_wheel, v, lim;

    if (iq >= 0.0f) {
        v = mt->kt * w_m + mt->r * iq;
        if (v > 0.0f && v < ESC_DEADBAND_V) v = (iq > DRIVE_IQ_EPS_A) ? ESC_DEADBAND_V : 0.0f;
        else if (v < 0.0f) v = 0.0f;   /* never command reverse at speed */
    } else {
        float w_eff = w_m > mt->gear * w_floor ? w_m : mt->gear * w_floor;
        float duty = (mt->kt > 0.0f && w_eff > 0.0f) ? (-iq * mt->r) / (mt->kt * w_eff) : 1.0f;
        if (duty > 1.0f) duty = 1.0f;
        v = -duty * mt->vmax;
        if (v < 0.0f && v > -ESC_DEADBAND_V) v = (iq < -DRIVE_IQ_EPS_A) ? -ESC_DEADBAND_V : 0.0f;
    }

    /* Stay inside the pack's live voltage so the host's sag clamp never
     * silently truncates the command and breaks the model above. */
    lim = 0.95f * (batt_v > 1.0f ? batt_v : g_params.v_rail_nom);
    if (mt->vmax > 0.1f && mt->vmax < lim) lim = mt->vmax;
    return v < -lim ? -lim : (v > lim ? lim : v);
}

static void write_cmd(const TtCmd *c, const TtMeas *m, CtrlOutputs *out)
{
    int w, first = 1;
    const float r = g_params.wheel_radius_m;
    /* Wheel speed implied by the vehicle speed (see esc_volts), floored at
     * 5 cm/s, below which it is too coarse to size a brake duty on. */
    const float w_veh = r > 0.0f ? g_st.v_meas / r : 0.0f;
    const float w_floor = r > 0.0f ? 0.05f / r : 0.0f;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        const MotBind *mt = &g_mot[w];
        float iq, a;
        if (!mt->bound) continue;
        iq = c->arm ? tt_torque_to_iq(&g_params, w, c->wheel_torque_nm[w]) : 0.0f;
        if (mt->units == CTRL_UNITS_AMPS_IQ) a = iq;
        else
            a = c->arm ? esc_volts(mt, iq, w_veh, m->batt.v, w_floor) : 0.0f;
        out->actuator[mt->slot] = a;
        if (first) {
            g_st.log.motor_v = mt->units == CTRL_UNITS_AMPS_IQ ? 0.0f : a;
            g_st.log.i_cmd = iq;
            first = 0;
        }
    }
    out->actuator[CTRL_STEER_ACTUATOR] = c->steer_rad;    /* v7: radians, + = left */
    out->actuator[CTRL_BRAKE_ACTUATOR] = c->brake_01;
}

CTRL_EXPORT void ctrl_step(const CtrlInputs *in, CtrlOutputs *out)
{
    TtMeas meas;
    TtCmd  cmd;
    uint32_t i, n;

    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    read_meas(in, &meas);
    g_st.fault |= g_cfg_fault;
    opus_step(&g_st, &meas, &cmd);
    write_cmd(&cmd, &meas, out);

    n = OPUS_LOG_N < 16u ? OPUS_LOG_N : 16u;
    for (i = 0; i < n; i++) out->debug[i] = ((const float *)&g_st.log)[i];
}
