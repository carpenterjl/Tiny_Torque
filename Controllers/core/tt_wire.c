/*
 * tt_wire.c — the lockstep HIL wire protocol. See tt_wire.h.
 */
#include "tt_wire.h"

#include <string.h>

/* ---------------------------------------------------------------- CRC --- */

uint16_t tt_crc16(const uint8_t *p, size_t n, uint16_t crc)
{
    size_t i;
    int b;
    for (i = 0; i < n; i++) {
        crc ^= (uint16_t)((uint16_t)p[i] << 8);
        for (b = 0; b < 8; b++)
            crc = (uint16_t)((crc & 0x8000u) ? (uint16_t)(crc << 1) ^ 0x1021u : (uint16_t)(crc << 1));
    }
    return crc;
}

/* --------------------------------------------------------------- COBS --- */

/* A streaming encoder, so a frame never needs a second full-size buffer. */
typedef struct {
    uint8_t *out;
    size_t   cap, w, code_i;
    uint8_t  code;
    int      err;
    int      after_full;   /* the last block was a full 254-byte one */
} Cobs;

static void cobs_begin(Cobs *c, uint8_t *out, size_t cap)
{
    c->out = out;
    c->cap = cap;
    c->code_i = 0;
    c->w = 1;
    c->code = 1;
    c->err = cap < 1;
    c->after_full = 0;
}

static void cobs_close_block(Cobs *c)
{
    if (c->code_i < c->cap) c->out[c->code_i] = c->code; else c->err = 1;
    c->code_i = c->w++;
    c->code = 1;
}

static void cobs_put(Cobs *c, uint8_t b)
{
    if (c->err) return;
    c->after_full = 0;
    if (b == 0) { cobs_close_block(c); return; }
    if (c->w >= c->cap) { c->err = 1; return; }
    c->out[c->w++] = b;
    if (++c->code == 0xFF) { cobs_close_block(c); c->after_full = 1; }
}

static size_t cobs_end(Cobs *c)
{
    if (c->err) return 0;
    /* Input that ends with a full block needs no empty block after it. */
    if (c->after_full) return c->code_i;
    if (c->code_i >= c->cap) return 0;
    c->out[c->code_i] = c->code;
    return c->w;
}

size_t tt_cobs_encode(const uint8_t *in, size_t n, uint8_t *out)
{
    Cobs c;
    size_t i;
    cobs_begin(&c, out, n + n / 254u + 1u);
    for (i = 0; i < n; i++) cobs_put(&c, in[i]);
    return cobs_end(&c);
}

int tt_cobs_decode(const uint8_t *in, size_t n, uint8_t *out, size_t cap)
{
    size_t i = 0, o = 0;
    while (i < n) {
        uint8_t code = in[i++], k;
        if (code == 0) return -1;
        for (k = 1; k < code; k++) {
            if (i >= n || o >= cap || in[i] == 0) return -1;
            out[o++] = in[i++];
        }
        if (code < 0xFF && i < n) {
            if (o >= cap) return -1;
            out[o++] = 0;
        }
    }
    return (int)o;
}

/* ------------------------------------------------------------- frames --- */

static void hdr_bytes(const TtWireHdr *h, uint8_t b[TT_WIRE_HDR])
{
    int k;
    b[0] = h->type;
    b[1] = h->flags;
    b[2] = (uint8_t)(h->seq & 0xFFu);
    b[3] = (uint8_t)(h->seq >> 8);
    for (k = 0; k < 4; k++) b[4 + k] = (uint8_t)(h->tick >> (8 * k));
    for (k = 0; k < 8; k++) b[8 + k] = (uint8_t)(h->time_us >> (8 * k));
}

size_t tt_wire_frame(const TtWireHdr *h, const uint8_t *payload, size_t n,
                     uint8_t *out, size_t cap)
{
    uint8_t hb[TT_WIRE_HDR];
    uint16_t crc;
    Cobs c;
    size_t i, len;

    if (n > TT_WIRE_MAX_PAYLOAD || cap < 2) return 0;
    hdr_bytes(h, hb);
    crc = tt_crc16(hb, TT_WIRE_HDR, 0xFFFFu);
    if (n) crc = tt_crc16(payload, n, crc);

    cobs_begin(&c, out, cap - 1u);               /* room for the delimiter */
    for (i = 0; i < TT_WIRE_HDR; i++) cobs_put(&c, hb[i]);
    for (i = 0; i < n; i++) cobs_put(&c, payload[i]);
    cobs_put(&c, (uint8_t)(crc & 0xFFu));
    cobs_put(&c, (uint8_t)(crc >> 8));
    len = cobs_end(&c);
    if (len == 0) return 0;
    out[len++] = 0x00;
    return len;
}

void tt_wire_rx_init(TtWireRx *rx)
{
    memset(rx, 0, sizeof(*rx));
}

static int rx_finish(TtWireRx *rx, TtWireHdr *h, const uint8_t **payload, size_t *plen)
{
    int n = tt_cobs_decode(rx->buf, rx->len, rx->body, sizeof(rx->body));
    const uint8_t *b = rx->body;
    uint16_t crc;
    int k;

    if (n < 0) { rx->cobs_errors++; return 0; }
    if (n < (int)(TT_WIRE_HDR + 2u)) { rx->cobs_errors++; return 0; }
    crc = tt_crc16(b, (size_t)n - 2u, 0xFFFFu);
    if ((uint8_t)(crc & 0xFFu) != b[n - 2] || (uint8_t)(crc >> 8) != b[n - 1]) {
        rx->crc_errors++;
        return 0;
    }
    h->type = b[0];
    h->flags = b[1];
    h->seq = (uint16_t)(b[2] | (b[3] << 8));
    h->tick = 0;
    for (k = 3; k >= 0; k--) h->tick = (h->tick << 8) | b[4 + k];
    h->time_us = 0;
    for (k = 7; k >= 0; k--) h->time_us = (h->time_us << 8) | b[8 + k];
    *payload = b + TT_WIRE_HDR;
    *plen = (size_t)n - TT_WIRE_HDR - 2u;

    if (rx->have_seq && h->seq != rx->next_seq) rx->seq_gaps++;
    rx->next_seq = (uint16_t)(h->seq + 1u);
    rx->have_seq = 1;
    rx->frames++;
    return 1;
}

size_t tt_wire_rx_feed(TtWireRx *rx, const uint8_t *p, size_t n, int *got,
                       TtWireHdr *h, const uint8_t **payload, size_t *plen)
{
    size_t i;
    *got = 0;
    for (i = 0; i < n; i++) {
        uint8_t b = p[i];
        if (b != 0) {
            if (rx->len < sizeof(rx->buf)) rx->buf[rx->len++] = b;
            else rx->overflow = 1;
            continue;
        }
        /* end of a frame */
        if (rx->overflow) {
            rx->overflows++;
        } else if (rx->len > 0 && rx_finish(rx, h, payload, plen)) {
            rx->len = 0;
            *got = 1;
            return i + 1;
        }
        rx->len = 0;
        rx->overflow = 0;
    }
    return n;
}

/* ------------------------------------------------------- byte packing --- */

typedef struct { uint8_t *p; size_t cap, n; int err; } W;
typedef struct { const uint8_t *p; size_t n, i; int err; } R;

static void w_u8(W *w, uint8_t v)
{
    if (w->n >= w->cap) { w->err = 1; return; }
    w->p[w->n++] = v;
}
static void w_u16(W *w, uint16_t v) { w_u8(w, (uint8_t)v); w_u8(w, (uint8_t)(v >> 8)); }
static void w_u32(W *w, uint32_t v)
{
    w_u8(w, (uint8_t)v); w_u8(w, (uint8_t)(v >> 8));
    w_u8(w, (uint8_t)(v >> 16)); w_u8(w, (uint8_t)(v >> 24));
}
static void w_f32(W *w, float f) { uint32_t u; memcpy(&u, &f, 4); w_u32(w, u); }
static void w_f32n(W *w, const float *f, int n) { int k; for (k = 0; k < n; k++) w_f32(w, f[k]); }
static void w_stamp(W *w, const TtStamp *s) { w_u32(w, s->t_us); w_u32(w, s->seq); }

static uint8_t r_u8(R *r)
{
    if (r->i >= r->n) { r->err = 1; return 0; }
    return r->p[r->i++];
}
static uint16_t r_u16(R *r) { uint16_t a = r_u8(r); return (uint16_t)(a | (r_u8(r) << 8)); }
static uint32_t r_u32(R *r)
{
    uint32_t a = r_u8(r), b = r_u8(r), c = r_u8(r), d = r_u8(r);
    return a | (b << 8) | (c << 16) | (d << 24);
}
static float r_f32(R *r) { uint32_t u = r_u32(r); float f; memcpy(&f, &u, 4); return f; }
static void r_f32n(R *r, float *f, int n) { int k; for (k = 0; k < n; k++) f[k] = r_f32(r); }
static void r_stamp(R *r, TtStamp *s) { s->t_us = r_u32(r); s->seq = r_u32(r); s->valid = 1; }

/* ---------------------------------------------------------------- MEAS --- */

/* Presence mask bits. */
#define PM_ENC(w)  (1u << (w))
#define PM_DRV(w)  (1u << (4 + (w)))
#define PM_IMU     (1u << 8)
#define PM_TOF     (1u << 9)
#define PM_FLOW    (1u << 10)
#define PM_BATT    (1u << 11)
#define PM_RC      (1u << 12)
#define PM_STEER   (1u << 13)

size_t tt_wire_put_meas(uint8_t *p, size_t cap, const TtMeas *m)
{
    W w = { p, cap, 0, 0 };
    uint16_t mask = 0;
    int k, i, nu;

    for (k = 0; k < TT_MAX_WHEELS; k++) {
        if (m->enc[k].st.valid) mask |= (uint16_t)PM_ENC(k);
        if (m->drv[k].st.valid) mask |= (uint16_t)PM_DRV(k);
    }
    if (m->imu.st.valid)      mask |= PM_IMU;
    if (m->tof.st.valid)      mask |= PM_TOF;
    if (m->flow.st.valid)     mask |= PM_FLOW;
    if (m->batt.st.valid)     mask |= PM_BATT;
    if (m->rc.st.valid)       mask |= PM_RC;
    if (m->steer_fb.st.valid) mask |= PM_STEER;

    w_u32(&w, m->now_us);
    w_f32(&w, m->dt_s);
    w_u16(&w, mask);

    for (k = 0; k < TT_MAX_WHEELS; k++)
        if (mask & PM_ENC(k)) { w_stamp(&w, &m->enc[k].st); w_u32(&w, (uint32_t)m->enc[k].count); }
    for (k = 0; k < TT_MAX_WHEELS; k++)
        if (mask & PM_DRV(k)) {
            const TtDriveFb *d = &m->drv[k];
            w_stamp(&w, &d->st);
            w_f32(&w, d->iq_a); w_f32(&w, d->omega_m); w_f32(&w, d->vbus_v); w_f32(&w, d->temp_c);
            w_u16(&w, d->fault);
        }
    if (mask & PM_IMU) { w_stamp(&w, &m->imu.st); w_f32n(&w, m->imu.gyro, 3); w_f32n(&w, m->imu.accel, 3); }
    if (mask & PM_TOF) {
        int z = m->tof.zones > TT_TOF_MAX_ZONES ? TT_TOF_MAX_ZONES : m->tof.zones;
        w_stamp(&w, &m->tof.st);
        w_u8(&w, (uint8_t)z);
        for (i = 0; i < z; i++) w_u8(&w, m->tof.status[i]);
        w_f32n(&w, m->tof.range_m, z);
    }
    if (mask & PM_FLOW) {
        w_stamp(&w, &m->flow.st);
        w_f32(&w, m->flow.flow_x); w_f32(&w, m->flow.flow_y);
        w_f32(&w, m->flow.quality); w_f32(&w, m->flow.height_m);
    }
    if (mask & PM_BATT) { w_stamp(&w, &m->batt.st); w_f32(&w, m->batt.v); w_f32(&w, m->batt.i); }
    if (mask & PM_RC) { w_stamp(&w, &m->rc.st); w_f32n(&w, m->rc.ch, 8); w_u8(&w, m->rc.failsafe); }
    if (mask & PM_STEER) { w_stamp(&w, &m->steer_fb.st); w_f32(&w, m->steer_fb.rad); }

    nu = m->uwb_n > TT_MAX_UWB ? TT_MAX_UWB : m->uwb_n;
    w_u8(&w, (uint8_t)nu);
    for (i = 0; i < nu; i++) {
        const TtUwb *u = &m->uwb[i];
        w_u8(&w, u->st.valid ? 1u : 0u);
        if (!u->st.valid) continue;
        w_stamp(&w, &u->st);
        w_u8(&w, u->anchor);
        w_f32(&w, u->range_m); w_f32(&w, u->quality); w_f32n(&w, u->pos_m, 3);
    }
    return w.err ? 0 : w.n;
}

int tt_wire_get_meas(const uint8_t *p, size_t n, TtMeas *m)
{
    R r = { p, n, 0, 0 };
    uint16_t mask;
    int k, i, nu;

    memset(m, 0, sizeof(*m));
    m->now_us = r_u32(&r);
    m->dt_s = r_f32(&r);
    mask = r_u16(&r);

    for (k = 0; k < TT_MAX_WHEELS; k++)
        if (mask & PM_ENC(k)) { r_stamp(&r, &m->enc[k].st); m->enc[k].count = (int32_t)r_u32(&r); }
    for (k = 0; k < TT_MAX_WHEELS; k++)
        if (mask & PM_DRV(k)) {
            TtDriveFb *d = &m->drv[k];
            r_stamp(&r, &d->st);
            d->iq_a = r_f32(&r); d->omega_m = r_f32(&r); d->vbus_v = r_f32(&r); d->temp_c = r_f32(&r);
            d->fault = r_u16(&r);
        }
    if (mask & PM_IMU) { r_stamp(&r, &m->imu.st); r_f32n(&r, m->imu.gyro, 3); r_f32n(&r, m->imu.accel, 3); }
    if (mask & PM_TOF) {
        int z;
        r_stamp(&r, &m->tof.st);
        z = r_u8(&r);
        if (z > TT_TOF_MAX_ZONES) return -1;
        m->tof.zones = (uint8_t)z;
        for (i = 0; i < z; i++) m->tof.status[i] = r_u8(&r);
        r_f32n(&r, m->tof.range_m, z);
    }
    if (mask & PM_FLOW) {
        r_stamp(&r, &m->flow.st);
        m->flow.flow_x = r_f32(&r); m->flow.flow_y = r_f32(&r);
        m->flow.quality = r_f32(&r); m->flow.height_m = r_f32(&r);
    }
    if (mask & PM_BATT) { r_stamp(&r, &m->batt.st); m->batt.v = r_f32(&r); m->batt.i = r_f32(&r); }
    if (mask & PM_RC) { r_stamp(&r, &m->rc.st); r_f32n(&r, m->rc.ch, 8); m->rc.failsafe = r_u8(&r); }
    if (mask & PM_STEER) { r_stamp(&r, &m->steer_fb.st); m->steer_fb.rad = r_f32(&r); }

    nu = r_u8(&r);
    if (nu > TT_MAX_UWB) return -1;
    m->uwb_n = (uint8_t)nu;
    for (i = 0; i < nu; i++) {
        TtUwb *u = &m->uwb[i];
        if (!r_u8(&r)) continue;
        r_stamp(&r, &u->st);
        u->anchor = r_u8(&r);
        u->range_m = r_f32(&r); u->quality = r_f32(&r); r_f32n(&r, u->pos_m, 3);
    }
    return (r.err || r.i != r.n) ? -1 : 0;
}

/* ----------------------------------------------------------------- ACT --- */

size_t tt_wire_put_act(uint8_t *p, size_t cap, const TtAct *a, const float *log, uint32_t n_log)
{
    W w = { p, cap, 0, 0 };
    if (n_log > 255u) n_log = 255u;
    w_f32n(&w, a->drive, TT_MAX_WHEELS);
    w_f32(&w, a->steer_rad);
    w_f32(&w, a->brake_01);
    w_u8(&w, (uint8_t)n_log);
    if (log) w_f32n(&w, log, (int)n_log);
    else { uint32_t k; for (k = 0; k < n_log; k++) w_f32(&w, 0.0f); }
    return w.err ? 0 : w.n;
}

int tt_wire_get_act(const uint8_t *p, size_t n, TtAct *a, float *log, uint32_t log_cap, uint32_t *n_log)
{
    R r = { p, n, 0, 0 };
    uint32_t k, nl;
    memset(a, 0, sizeof(*a));
    r_f32n(&r, a->drive, TT_MAX_WHEELS);
    a->steer_rad = r_f32(&r);
    a->brake_01 = r_f32(&r);
    nl = r_u8(&r);
    for (k = 0; k < nl; k++) {
        float v = r_f32(&r);
        if (log && k < log_cap) log[k] = v;
    }
    if (n_log) *n_log = nl < log_cap ? nl : log_cap;
    return (r.err || r.i != r.n) ? -1 : 0;
}

/* ----------------------------------------------------------------- CAR --- */

size_t tt_wire_put_car(uint8_t *p, size_t cap, const TtCarDesc *c)
{
    W w = { p, cap, 0, 0 };
    int k;
    w_u8(&w, c->present); w_u8(&w, c->enc_mask); w_u8(&w, c->drv_mask);
    w_u8(&w, c->has_steer_fb); w_u8(&w, c->has_uwb);
    for (k = 0; k < TT_MAX_WHEELS; k++) w_u8(&w, c->drv_kind[k]);
    w_f32n(&w, c->enc_radius_m, TT_MAX_WHEELS);
    w_f32n(&w, c->enc_counts_per_rev, TT_MAX_WHEELS);
    w_f32n(&w, c->drv_kt, TT_MAX_WHEELS);
    w_f32n(&w, c->drv_r_ohm, TT_MAX_WHEELS);
    w_f32n(&w, c->drv_gear, TT_MAX_WHEELS);
    w_f32n(&w, c->drv_vmax, TT_MAX_WHEELS);
    w_f32(&w, c->steer_max_rad);
    w_f32n(&w, c->uwb_lever_m, 3);
    return w.err ? 0 : w.n;
}

int tt_wire_get_car(const uint8_t *p, size_t n, TtCarDesc *c)
{
    R r = { p, n, 0, 0 };
    int k;
    memset(c, 0, sizeof(*c));
    c->present = r_u8(&r); c->enc_mask = r_u8(&r); c->drv_mask = r_u8(&r);
    c->has_steer_fb = r_u8(&r); c->has_uwb = r_u8(&r);
    for (k = 0; k < TT_MAX_WHEELS; k++) c->drv_kind[k] = r_u8(&r);
    r_f32n(&r, c->enc_radius_m, TT_MAX_WHEELS);
    r_f32n(&r, c->enc_counts_per_rev, TT_MAX_WHEELS);
    r_f32n(&r, c->drv_kt, TT_MAX_WHEELS);
    r_f32n(&r, c->drv_r_ohm, TT_MAX_WHEELS);
    r_f32n(&r, c->drv_gear, TT_MAX_WHEELS);
    r_f32n(&r, c->drv_vmax, TT_MAX_WHEELS);
    c->steer_max_rad = r_f32(&r);
    r_f32n(&r, c->uwb_lever_m, 3);
    return (r.err || r.i != r.n) ? -1 : 0;
}

/* ---------------------------------------------------------------- INFO --- */

size_t tt_wire_put_info(uint8_t *p, size_t cap, const TtWireInfo *i)
{
    W w = { p, cap, 0, 0 };
    size_t k, nn = strlen(i->name), nl = strlen(i->log_names);
    if (nn > sizeof(i->name) - 1u) nn = sizeof(i->name) - 1u;
    if (nl > sizeof(i->log_names) - 1u) nl = sizeof(i->log_names) - 1u;
    w_u16(&w, i->version);
    w_u16(&w, i->n_log);
    w_f32(&w, i->rate_hz);
    w_u32(&w, i->params_hash);
    w_u8(&w, (uint8_t)nn);
    for (k = 0; k < nn; k++) w_u8(&w, (uint8_t)i->name[k]);
    w_u16(&w, (uint16_t)nl);
    for (k = 0; k < nl; k++) w_u8(&w, (uint8_t)i->log_names[k]);
    return w.err ? 0 : w.n;
}

int tt_wire_get_info(const uint8_t *p, size_t n, TtWireInfo *i)
{
    R r = { p, n, 0, 0 };
    size_t k, nn, nl;
    memset(i, 0, sizeof(*i));
    i->version = r_u16(&r);
    i->n_log = r_u16(&r);
    i->rate_hz = r_f32(&r);
    i->params_hash = r_u32(&r);
    nn = r_u8(&r);
    for (k = 0; k < nn; k++) {
        char ch = (char)r_u8(&r);
        if (k < sizeof(i->name) - 1u) i->name[k] = ch;
    }
    nl = r_u16(&r);
    for (k = 0; k < nl; k++) {
        char ch = (char)r_u8(&r);
        if (k < sizeof(i->log_names) - 1u) i->log_names[k] = ch;
    }
    return (r.err || r.i != r.n) ? -1 : 0;
}
