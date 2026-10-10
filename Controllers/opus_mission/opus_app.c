/*
 * opus_app.c — the whole Opus firmware, as any target runs it. See opus_app.h.
 */
#include "opus_app.h"
#include "mission_cfg.h"

#include <math.h>
#include <string.h>

/* ESC input deadband: below it the hobby ESC outputs nothing, so the driver
 * pushes small genuine requests up to it rather than letting them vanish. */
#define ESC_DEADBAND_V   0.10f
#define DRIVE_IQ_EPS_A   0.035f   /* ~0.05 N of road force on this car */

static char g_names[1024];

const char *opus_app_log_names(void)
{
    static const char *names[] = {
#define TT_LOG(name, unit, desc) #name,
#include "opus_log.def"
#undef TT_LOG
    };
    size_t i, n = sizeof(names) / sizeof(names[0]), len = 0;
    if (g_names[0] != '\0') return g_names;
    for (i = 0; i < n; i++) {
        size_t k = strlen(names[i]);
        if (len + k + 2 >= sizeof(g_names)) break;
        if (i > 0) g_names[len++] = ',';
        memcpy(g_names + len, names[i], k);
        len += k;
        g_names[len] = '\0';
    }
    return g_names;
}

void opus_app_init(OpusApp *a, const TtParams *p, float rate_hz)
{
    TtNavCfg nav;
    memset(a, 0, sizeof(*a));
    a->params = *p;
    tt_nav_default_cfg(&nav);
    nav.tag_z = OPUS_UWB_TAG_Z_M;
    opus_fw_init(&a->fw, &a->params, rate_hz, &nav, 0);
    a->ready = 1;
}

static int differs(float x, float ref, float tol)
{
    float d = x - ref;
    float m = fabsf(ref) > 1e-9f ? fabsf(ref) : 1.0f;
    return fabsf(d) > tol * m;
}

void opus_app_configure(OpusApp *a, const TtCarDesc *car)
{
    const TtParams *p = &a->params;
    int w;

    a->cfg_fault = 0;
    if (car == 0 || !car->present) {
        memset(&a->car, 0, sizeof(a->car));
        a->cfg_fault = FA_NO_MANIFEST;
        return;
    }
    a->car = *car;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        unsigned bit = 1u << w;
        /* The firmware's own beliefs must match the car it was put in. */
        if ((p->odo_wheel_mask & bit) && (car->enc_mask & bit) &&
            (differs(car->enc_radius_m[w], p->wheel_radius_m, 0.01f) ||
             differs(car->enc_counts_per_rev[w], p->enc_cpr[w] * p->enc_ratio[w], 0.001f)))
            a->cfg_fault |= FA_PARAMS;
        if ((p->odo_wheel_mask & bit) && !(car->enc_mask & bit)) a->cfg_fault |= FA_NO_ENCODERS;
        if ((p->driven_mask & bit) && !(car->drv_mask & bit))    a->cfg_fault |= FA_NO_MOTORS;
    }
    /* Full lock as the car states it; the firmware's own belief is
     * max_steer_rad, and the two must agree. */
    if (car->has_steer_fb && differs(car->steer_max_rad, p->max_steer_rad, 0.02f))
        a->cfg_fault |= FA_PARAMS;

    /* The tag's lever arm from the body origin, for the EKF. */
    if (car->has_uwb) {
        a->fw.nav.cfg.lever[0] = car->uwb_lever_m[0];
        a->fw.nav.cfg.lever[1] = car->uwb_lever_m[1];
    }
}

void opus_app_reset(OpusApp *a)
{
    opus_fw_reset(&a->fw);
}

/* The virtual driver for a VOLTS drive: brushed motor behind a hobby ESC.
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
 * FW-06 (traction control); this keeps Phase 1 a pure interface change. */
static float esc_volts(const OpusApp *a, int w, float iq, float omega_wheel,
                       float batt_v, float w_floor)
{
    const TtCarDesc *c = &a->car;
    float kt = c->drv_kt[w], r = c->drv_r_ohm[w], gear = c->drv_gear[w], vmax = c->drv_vmax[w];
    float w_m = gear * omega_wheel, v, lim;

    if (iq >= 0.0f) {
        v = kt * w_m + r * iq;
        if (v > 0.0f && v < ESC_DEADBAND_V) v = (iq > DRIVE_IQ_EPS_A) ? ESC_DEADBAND_V : 0.0f;
        else if (v < 0.0f) v = 0.0f;   /* never command reverse at speed */
    } else {
        float w_eff = w_m > gear * w_floor ? w_m : gear * w_floor;
        float duty = (kt > 0.0f && w_eff > 0.0f) ? (-iq * r) / (kt * w_eff) : 1.0f;
        if (duty > 1.0f) duty = 1.0f;
        v = -duty * vmax;
        if (v < 0.0f && v > -ESC_DEADBAND_V) v = (iq < -DRIVE_IQ_EPS_A) ? -ESC_DEADBAND_V : 0.0f;
    }

    /* Stay inside the pack's live voltage so the host's sag clamp never
     * silently truncates the command and breaks the model above. */
    lim = 0.95f * (batt_v > 1.0f ? batt_v : a->params.v_rail_nom);
    if (vmax > 0.1f && vmax < lim) lim = vmax;
    return v < -lim ? -lim : (v > lim ? lim : v);
}

void opus_app_step(OpusApp *a, const TtMeas *m, TtAct *act)
{
    OpusState *st = &a->fw.mission;
    TtCmd c;
    int w, first = 1;
    const float r = a->params.wheel_radius_m;
    /* Wheel speed implied by the vehicle speed (see esc_volts), floored at
     * 5 cm/s, below which it is too coarse to size a brake duty on. */
    float w_veh, w_floor;

    memset(act, 0, sizeof(*act));
    if (!a->ready) return;

    st->fault |= a->cfg_fault;
    opus_fw_step(&a->fw, m, &c);

    w_veh = r > 0.0f ? st->v_meas / r : 0.0f;
    w_floor = r > 0.0f ? 0.05f / r : 0.0f;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float iq, out;
        if (!(a->car.drv_mask & (1u << w))) continue;
        iq = c.arm ? tt_torque_to_iq(&a->params, w, c.wheel_torque_nm[w]) : 0.0f;
        if (a->car.drv_kind[w] == TT_DRV_IQ) out = iq;
        else out = c.arm ? esc_volts(a, w, iq, w_veh, m->batt.v, w_floor) : 0.0f;
        act->drive[w] = out;
        if (first) {
            st->log.motor_v = a->car.drv_kind[w] == TT_DRV_IQ ? 0.0f : out;
            st->log.i_cmd = iq;
            first = 0;
        }
    }
    act->steer_rad = c.steer_rad;
    act->brake_01 = c.brake_01;
}

const float *opus_app_log(const OpusApp *a, uint32_t *n)
{
    if (n) *n = OPUS_LOG_N;
    return (const float *)&a->fw.mission.log;
}
