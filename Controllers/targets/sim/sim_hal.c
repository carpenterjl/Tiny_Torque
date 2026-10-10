/*
 * sim_hal.c — the Unity host's ABI v7 frames as a board. See sim_hal.h.
 */
#include "sim_hal.h"
#include "tt_safety.h"     /* TT_RC_* receiver flags */

#include <math.h>
#include <string.h>

#define SIM_PI 3.14159265358979323846f

static void unbind(SimHal *h)
{
    memset(h->enc, 0, sizeof(h->enc));
    memset(h->mot, 0, sizeof(h->mot));
    h->tof_idx = h->tof_off = h->batt_idx = h->batt_off = -1;
    h->rc_idx = h->rc_off = h->uwb_idx = h->uwb_off = -1;
    h->tof_max = 0.0f;
}

void sim_hal_init(SimHal *h)
{
    memset(h, 0, sizeof(*h));
    unbind(h);
}

void sim_hal_restart_clock(SimHal *h)
{
    h->has_prev_us = 0;
}

void sim_hal_configure(SimHal *h, const SensorInfo2 *sensors, int count, TtCarDesc *car)
{
    int i, w;

    unbind(h);
    memset(car, 0, sizeof(*car));
    if (sensors == 0 || count <= 0) return;      /* car->present = 0 */
    car->present = 1;

    for (i = 0; i < count; i++) {
        const SensorInfo2 *s = &sensors[i];
        w = s->wheel_index;
        switch (s->base.type) {

        case SENSOR_ENCODER:
            /* slice is [ang_vel, ticks]; we want the tick counter, which is the
             * only channel the host leaves un-noised. */
            if (w >= 0 && w < TT_MAX_WHEELS && s->base.data_count >= 2 && !h->enc[w].bound) {
                float c = s->cpr * (s->gear_ratio > 0.0f ? s->gear_ratio : 1.0f);
                h->enc[w].bound = 1;
                h->enc[w].idx = i;
                h->enc[w].off = s->base.data_offset + 1;
                h->enc[w].wrap = s->wrap;
                h->enc[w].rad_per_count = c > 0.0f ? 2.0f * SIM_PI / c : 0.0f;
                car->enc_mask |= (uint8_t)(1u << w);
                car->enc_radius_m[w] = s->wheel_radius_m;
                car->enc_counts_per_rev[w] = c;
            }
            break;

        case SENSOR_MOTOR:
        case SENSOR_FOC_FB:
            if (w >= 0 && w < TT_MAX_WHEELS && s->base.actuator_index >= 0 &&
                s->base.actuator_index < CTRL_STEER_ACTUATOR && !h->mot[w].bound) {
                h->mot[w].bound = 1;
                h->mot[w].idx = i;
                h->mot[w].off = s->base.data_offset;
                h->mot[w].foc = s->base.type == SENSOR_FOC_FB;
                h->mot[w].slot = s->base.actuator_index;
                h->mot[w].gear = s->gear_ratio;
                car->drv_mask |= (uint8_t)(1u << w);
                car->drv_kind[w] = (uint8_t)(s->units == CTRL_UNITS_AMPS_IQ ? TT_DRV_IQ : TT_DRV_VOLTS);
                car->drv_kt[w] = s->kt;
                car->drv_r_ohm[w] = s->resistance_ohm;
                car->drv_gear[w] = s->gear_ratio;
                car->drv_vmax[w] = s->base.range_max;
            }
            break;

        case SENSOR_TOF:
            /* The forward-looking one: aimed within 20 deg of x, in yaw and
             * in pitch (a downward line sensor also has yaw 0). */
            if (h->tof_idx < 0 && s->base.data_count >= 1 &&
                fabsf(s->rpy_rad[2]) < 0.35f && fabsf(s->rpy_rad[1]) < 0.35f) {
                h->tof_idx = i;
                h->tof_off = s->base.data_offset;
                h->tof_max = s->base.range_max;
            }
            break;

        case SENSOR_BATTERY:
            if (h->batt_idx < 0 && s->base.data_count >= 2) {
                h->batt_idx = i;
                h->batt_off = s->base.data_offset;
            }
            break;

        case SENSOR_RC:
            if (h->rc_idx < 0 && s->base.data_count >= 10) {
                h->rc_idx = i;
                h->rc_off = s->base.data_offset;
            }
            break;

        case SENSOR_UWB:
            if (h->uwb_idx < 0 && s->base.data_count >= 6) {
                h->uwb_idx = i;
                h->uwb_off = s->base.data_offset;
                car->has_uwb = 1;
                car->uwb_lever_m[0] = s->pos_m[0];
                car->uwb_lever_m[1] = s->pos_m[1];
                car->uwb_lever_m[2] = s->pos_m[2];
            }
            break;

        case SENSOR_STEER_FB:
            if (!car->has_steer_fb) {
                car->has_steer_fb = 1;
                car->steer_max_rad = s->base.range_max;
            }
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

void sim_hal_read(SimHal *h, const CtrlInputs *in, TtMeas *m)
{
    int w, k;
    tt_us_t now = (tt_us_t)in->time_us;

    memset(m, 0, sizeof(*m));
    m->now_us = now;
    /* dt from the timestamps, not the host's nominal period (FW-05). */
    m->dt_s = h->has_prev_us ? (float)(uint32_t)(now - h->prev_us) * 1e-6f : in->dt_s;
    h->prev_us = now;
    h->has_prev_us = 1;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        SimEncBind *e = &h->enc[w];
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

    if (h->batt_off >= 0) {
        m->batt.v = slice(in, h->batt_off, 0.0f);
        m->batt.i = slice(in, h->batt_off + 1, 0.0f);
        stamp(&m->batt.st, in, h->batt_idx);
    }

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        const SimMotBind *mt = &h->mot[w];
        if (!mt->bound) continue;
        if (mt->foc) {
            /* What an FOC driver reports, as it reports it. */
            m->drv[w].iq_a    = slice(in, mt->off + 0, 0.0f);
            m->drv[w].omega_m = slice(in, mt->off + 2, 0.0f);
            m->drv[w].vbus_v  = slice(in, mt->off + 3, m->batt.v);
            m->drv[w].temp_c  = slice(in, mt->off + 4, 0.0f);
            m->drv[w].fault   = (uint16_t)slice(in, mt->off + 5, 0.0f);
        } else {
            m->drv[w].iq_a = slice(in, mt->off + 1, 0.0f);
            m->drv[w].vbus_v = m->batt.v;
            m->drv[w].omega_m = h->enc[w].bound ? h->enc[w].omega_wheel * mt->gear : 0.0f;
        }
        stamp(&m->drv[w].st, in, mt->idx);
    }

    if (h->rc_off >= 0) {
        for (k = 0; k < 8; k++) m->rc.ch[k] = slice(in, h->rc_off + k, 0.0f);
        m->rc.failsafe = (uint8_t)((slice(in, h->rc_off + 8, 0.0f) > 0.5f ? TT_RC_FRAME_LOST : 0) |
                                   (slice(in, h->rc_off + 9, 0.0f) > 0.5f ? TT_RC_FAILSAFE : 0));
        stamp(&m->rc.st, in, h->rc_idx);
    }

    if (h->uwb_off >= 0) {
        float id = slice(in, h->uwb_off, -1.0f);
        TtUwb *u = &m->uwb[0];
        stamp(&u->st, in, h->uwb_idx);
        u->anchor = id >= 0.0f && id < 255.0f ? (uint8_t)lroundf(id) : 255u;
        u->range_m = slice(in, h->uwb_off + 1, 0.0f);
        u->quality = slice(in, h->uwb_off + 2, 0.0f);
        for (k = 0; k < 3; k++) u->pos_m[k] = slice(in, h->uwb_off + 3 + k, 0.0f);
        m->uwb_n = 1;
    }

    if (h->tof_off >= 0) {
        float r = slice(in, h->tof_off, 0.0f);
        m->tof.zones = 1;
        m->tof.range_m[0] = r;
        m->tof.status[0] = (h->tof_max > 0.0f && r >= h->tof_max - 1e-3f) ? 1 : 0; /* no target */
        stamp(&m->tof.st, in, h->tof_idx);
    }
}

void sim_hal_write(const SimHal *h, const TtAct *a, CtrlOutputs *out)
{
    int w;
    for (w = 0; w < TT_MAX_WHEELS; w++)
        if (h->mot[w].bound) out->actuator[h->mot[w].slot] = a->drive[w];
    out->actuator[CTRL_STEER_ACTUATOR] = a->steer_rad;    /* v7: radians, + = left */
    out->actuator[CTRL_BRAKE_ACTUATOR] = a->brake_01;
}
