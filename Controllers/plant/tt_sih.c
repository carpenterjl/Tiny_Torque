/*
 * tt_sih.c — a reduced car for on-target real-time runs (HIL-06). See tt_sih.h.
 */
#include "tt_sih.h"

#include <math.h>
#include <string.h>

#define G_ 9.80665f
#define TWO_PI_ 6.28318531f

static float clampf_(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

void tt_sih_init(TtSih *s, const TtParams *p, float x, float y, float psi)
{
    static const float a[TT_SIH_ANCHORS][3] = {
        { -9.5f,  19.5f, 1.52f }, { -9.5f, -19.5f, 1.52f },
        {  9.5f, -19.5f, 1.52f }, {  9.5f,  19.5f, 1.52f },
    };
    memset(s, 0, sizeof(*s));
    s->p = p;
    s->x = x;
    s->y = y;
    s->psi = psi;
    memcpy(s->anchor, a, sizeof(a));
    s->tag_lever[0] = -0.06f;      /* the twin's tag: 6 cm behind the origin */
    s->tag_z = 0.19f;
    s->ocv_v = 8.2f;
    s->r_int = 0.020f;
}

void tt_sih_describe(const TtSih *s, TtCarDesc *car)
{
    const TtParams *p = s->p;
    int w;
    memset(car, 0, sizeof(*car));
    car->present = 1u;
    car->has_steer_fb = 1u;
    car->has_uwb = 1u;
    car->steer_max_rad = p->max_steer_rad;
    car->uwb_lever_m[0] = s->tag_lever[0];
    car->uwb_lever_m[1] = s->tag_lever[1];
    car->uwb_lever_m[2] = s->tag_z;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        car->enc_mask |= (uint8_t)(1u << w);
        car->enc_radius_m[w] = p->wheel_radius_m;
        car->enc_counts_per_rev[w] = p->enc_cpr[w] * p->enc_ratio[w];
        if (p->driven_mask & (1u << w)) {
            car->drv_mask |= (uint8_t)(1u << w);
            car->drv_kind[w] = TT_DRV_IQ;
            car->drv_kt[w] = p->kt[w];
            car->drv_gear[w] = p->gear[w];
        }
    }
}

static void stamp(TtStamp *st, uint32_t seq, tt_us_t t)
{
    st->valid = 1u;
    st->seq = seq;
    st->t_us = t;
}

void tt_sih_meas(TtSih *s, float dt, TtMeas *m)
{
    const TtParams *p = s->p;
    tt_us_t now = (tt_us_t)(s->t * 1e6f);
    int w;

    memset(m, 0, sizeof(*m));
    m->now_us = now;
    m->dt_s = dt;
    s->seq++;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float cpr = p->enc_cpr[w] * p->enc_ratio[w];
        m->enc[w].count = (int32_t)floorf(s->wheel_ang[w] * cpr / TWO_PI_);
        stamp(&m->enc[w].st, s->seq, now);
        if (p->driven_mask & (1u << w)) {
            m->drv[w].iq_a = s->acting.drive[w];
            m->drv[w].omega_m = s->wheel_w[w] * p->gear[w];
            m->drv[w].vbus_v = s->ocv_v - s->r_int * s->pack_i;
            m->drv[w].temp_c = 30.0f;
            stamp(&m->drv[w].st, s->seq, now);
        }
    }

    m->imu.gyro[2] = s->r;
    m->imu.accel[0] = s->a;
    m->imu.accel[1] = s->v * s->r;
    m->imu.accel[2] = G_;
    stamp(&m->imu.st, s->seq, now);

    m->batt.v = s->ocv_v - s->r_int * s->pack_i;
    m->batt.i = s->pack_i;
    stamp(&m->batt.st, s->seq, now);

    m->steer_fb.rad = s->delta;
    stamp(&m->steer_fb.st, s->seq, now);

    /* RC: a frame every 20 ms, armed, not killed */
    if (s->t >= s->rc_next) {
        s->rc_next += 0.020f;
        s->rc_seq++;
    }
    m->rc.ch[4] = 1.0f;
    m->rc.ch[5] = -1.0f;
    stamp(&m->rc.st, s->rc_seq, now);

    /* UWB: one anchor every 25 ms, round-robin */
    if (s->t >= s->uwb_next) {
        const float *an = s->anchor[s->uwb_slot % TT_SIH_ANCHORS];
        float c = cosf(s->psi), sn = sinf(s->psi);
        float tx = s->x + c * s->tag_lever[0] - sn * s->tag_lever[1];
        float ty = s->y + sn * s->tag_lever[0] + c * s->tag_lever[1];
        float dx = tx - an[0], dy = ty - an[1], dz = s->tag_z - an[2];
        TtUwb *u = &s->uwb_last;
        u->anchor = (uint8_t)(s->uwb_slot % TT_SIH_ANCHORS);
        u->range_m = sqrtf(dx * dx + dy * dy + dz * dz);
        u->quality = 2.0f;
        memcpy(u->pos_m, an, sizeof(u->pos_m));
        s->uwb_seq++;
        stamp(&u->st, s->uwb_seq, now);
        s->uwb_slot++;
        s->uwb_next += 0.025f;
    }
    if (s->uwb_seq > 0u) {
        m->uwb[0] = s->uwb_last;
        m->uwb_n = 1u;
    }
}

void tt_sih_step(TtSih *s, const TtAct *act, float dt)
{
    const TtParams *p = s->p;
    const int sub = 4;
    float h = dt / (float)sub;
    int k, w;

    s->acting = s->pending;
    s->pending = *act;

    for (k = 0; k < sub; k++) {
        float f = 0.0f, drag, td, beta, cb, vf, lag;
        float va = fabsf(s->v);
        float wpack = 0.0f;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            float ktg = p->kt[w] * p->gear[w] * p->eta_drive, tq;
            if (!(p->driven_mask & (1u << w)) || !(ktg > 0.0f)) continue;
            tq = clampf_(s->acting.drive[w] * ktg, -p->wheel_drive_max_nm, p->wheel_drive_max_nm);
            f += tq / p->wheel_radius_m;
            wpack += s->acting.drive[w] * p->kt[w] * s->wheel_w[w] * p->gear[w];
        }
        drag = p->drag_c0 + p->drag_c1 * va + p->drag_c2 * va * va;
        if (s->v < 0.0f) drag = -drag;
        if (va < 0.01f && fabsf(f) < p->drag_c0) { drag = f; }
        s->a = (f - drag) / p->mass_eff_kg;
        s->v += s->a * h;
        if (fabsf(s->v) < 0.01f && fabsf(f) < p->drag_c0) s->v = 0.0f;

        lag = p->steer_lag_s > 0.0f ? p->steer_lag_s : 0.04f;
        s->delta += (clampf_(s->acting.steer_rad, -p->max_steer_rad, p->max_steer_rad) - s->delta) *
                    (1.0f - expf(-h / lag));
        td = tanf(s->delta);
        beta = atanf(0.5f * td);
        cb = cosf(beta);
        s->r = s->v * cb * td / p->wheelbase_m;
        s->x += s->v * cosf(s->psi + beta) * h;
        s->y += s->v * sinf(s->psi + beta) * h;
        s->psi += s->r * h;

        /* wheels roll: the axles' speeds about the turn, +- the track */
        vf = s->v * cb / cosf(s->delta);
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            float side = (w == TT_FL || w == TT_RL) ? -0.5f : 0.5f;
            float tr = (w < 2) ? p->track_front_m : p->track_rear_m;
            float vw = ((w < 2) ? vf : s->v * cb) + side * s->r * tr;
            s->wheel_w[w] = vw / p->wheel_radius_m;
            s->wheel_ang[w] += s->wheel_w[w] * h;
        }
        s->pack_i = 0.7f + wpack / (s->ocv_v > 1.0f ? s->ocv_v : 1.0f);
    }
    s->t += dt;
}
