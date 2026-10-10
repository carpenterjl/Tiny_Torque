/*
 * test_hil.c — the lockstep HIL wire protocol and the firmware's end of the
 * link (HIL-02, HIL-03), without Unity or a board.
 *
 *   1. CRC-16/CCITT-FALSE and COBS against published vectors.
 *   2. Frames: random headers and payloads survive any chunking; damaged
 *      frames (bit flips, garbage, overruns) are rejected, never delivered
 *      wrong, and the receiver resynchronises at the next delimiter.
 *   3. Payloads: TtMeas / TtAct / TtCarDesc / INFO round-trip bit for bit
 *      (NaN, -0, every ToF zone), and malformed payloads are refused.
 *   4. The device loop: ordering rules, PING, faults.
 *   5. Lockstep: the Opus firmware (FOC twin parameters) drives a synthetic
 *      car twice — called directly, and through host framing -> wire bytes
 *      in random chunks -> hil_dev -> reply bytes -> host — and every tick's
 *      actuator writes and log frame must be bit-identical.
 *
 * With an argument, the lockstep run's wire stream (both directions) is
 * written there: CTest hands it to tt_replay (VAL-03) as a capture.
 * Registered with CTest as `hil_wire`.
 */
#include "tt_wire.h"
#include "hil_dev.h"
#include "opus_hil.h"
#include "mission_cfg.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_fail = 0;
#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); g_fail++; } \
                               else printf("ok   %s\n", name); } while (0)

static unsigned long long g_rng = 0x9E3779B97F4A7C15ull;
static uint32_t rnd(void)
{
    g_rng ^= g_rng << 13; g_rng ^= g_rng >> 7; g_rng ^= g_rng << 17;
    return (uint32_t)(g_rng >> 16);
}
static float rndf(float lo, float hi) { return lo + (hi - lo) * (float)(rnd() % 1000000u) / 1e6f; }

/* ------------------------------------------------------------ 1. vectors */

static int cobs_case(const uint8_t *in, size_t n, const uint8_t *want, size_t wn)
{
    uint8_t enc[600], dec[600];
    size_t k = tt_cobs_encode(in, n, enc);
    int d;
    if (k != wn || memcmp(enc, want, wn) != 0) return 0;
    d = tt_cobs_decode(enc, k, dec, sizeof(dec));
    return d == (int)n && memcmp(dec, in, n) == 0;
}

static void test_vectors(void)
{
    static const uint8_t z1[] = { 0x00 }, w1[] = { 0x01, 0x01 };
    static const uint8_t z2[] = { 0x00, 0x00 }, w2[] = { 0x01, 0x01, 0x01 };
    static const uint8_t a3[] = { 0x00, 0x11, 0x00 }, w3[] = { 0x01, 0x02, 0x11, 0x01 };
    static const uint8_t a4[] = { 0x11, 0x22, 0x00, 0x33 }, w4[] = { 0x03, 0x11, 0x22, 0x02, 0x33 };
    static const uint8_t a5[] = { 0x11, 0x22, 0x33, 0x44 }, w5[] = { 0x05, 0x11, 0x22, 0x33, 0x44 };
    static const uint8_t a6[] = { 0x11, 0x00, 0x00, 0x00 }, w6[] = { 0x02, 0x11, 0x01, 0x01, 0x01 };
    uint8_t big[300], want[300];
    int i, ok;

    CHECK("crc16 CCITT-FALSE(\"123456789\") = 0x29B1",
          tt_crc16((const uint8_t *)"123456789", 9, 0xFFFFu) == 0x29B1u);
    ok = cobs_case(0, 0, (const uint8_t *)"\x01", 1) &&
         cobs_case(z1, 1, w1, 2) && cobs_case(z2, 2, w2, 3) && cobs_case(a3, 3, w3, 4) &&
         cobs_case(a4, 4, w4, 5) && cobs_case(a5, 4, w5, 5) && cobs_case(a6, 4, w6, 5);
    CHECK("cobs: short published vectors", ok);

    /* 01..FE (254 bytes) -> FF 01..FE */
    for (i = 0; i < 254; i++) big[i] = (uint8_t)(i + 1);
    want[0] = 0xFF;
    memcpy(want + 1, big, 254);
    ok = cobs_case(big, 254, want, 255);
    /* 00 01..FE -> 01 FF 01..FE */
    {
        uint8_t in2[255], w[256];
        in2[0] = 0;
        memcpy(in2 + 1, big, 254);
        w[0] = 0x01; w[1] = 0xFF;
        memcpy(w + 2, big, 254);
        ok = ok && cobs_case(in2, 255, w, 256);
    }
    /* 01..FF (255 bytes) -> FF 01..FE 02 FF */
    {
        uint8_t in3[255], w[258];
        for (i = 0; i < 255; i++) in3[i] = (uint8_t)(i + 1);
        w[0] = 0xFF;
        memcpy(w + 1, in3, 254);
        w[255] = 0x02; w[256] = 0xFF;
        ok = ok && cobs_case(in3, 255, w, 257);
    }
    CHECK("cobs: 254-byte block boundaries (canonical)", ok);
    CHECK("cobs: a zero inside a block is refused",
          tt_cobs_decode((const uint8_t *)"\x03\x11\x00", 3, big, sizeof(big)) < 0);
}

/* ------------------------------------------------------------- 2. frames */

static int frames_equal(const TtWireHdr *a, const TtWireHdr *b)
{
    return a->type == b->type && a->flags == b->flags && a->seq == b->seq &&
           a->tick == b->tick && a->time_us == b->time_us;
}

static void test_frames(void)
{
    static uint8_t pl[TT_WIRE_MAX_PAYLOAD], frame[TT_WIRE_MAX_FRAME];
    static TtWireRx rx;
    int trial, ok = 1, wrong = 0, delivered = 0, flips = 0;

    tt_wire_rx_init(&rx);
    for (trial = 0; trial < 2000 && ok; trial++) {
        TtWireHdr h, g;
        size_t n = rnd() % (TT_WIRE_MAX_PAYLOAD + 1u), len, off = 0, i;
        const uint8_t *p = 0;
        size_t plen = 0;
        int got = 0;
        h.type = (uint8_t)rnd(); h.flags = (uint8_t)rnd(); h.seq = (uint16_t)trial;
        h.tick = rnd(); h.time_us = ((uint64_t)rnd() << 32) | rnd();
        for (i = 0; i < n; i++) pl[i] = (uint8_t)(rnd() % 4u == 0 ? 0 : rnd());
        len = tt_wire_frame(&h, pl, n, frame, sizeof(frame));
        if (len == 0 || frame[len - 1] != 0) { ok = 0; break; }
        for (i = 0; i + 1 < len; i++) if (frame[i] == 0) ok = 0;
        while (off < len && !got) {                 /* random chunks */
            size_t c = 1u + rnd() % 97u;
            if (c > len - off) c = len - off;
            off += tt_wire_rx_feed(&rx, frame + off, c, &got, &g, &p, &plen);
        }
        if (!got || off != len || !frames_equal(&h, &g) || plen != n || memcmp(p, pl, n) != 0) ok = 0;
    }
    CHECK("frames: 2000 random frames survive random chunking", ok);
    CHECK("frames: no sequence gaps on a clean stream", rx.seq_gaps == 0);
    {
        TtWireHdr h;
        memset(&h, 0, sizeof(h));
        CHECK("frames: an oversized payload is refused",
              tt_wire_frame(&h, pl, TT_WIRE_MAX_PAYLOAD + 1u, frame, sizeof(frame)) == 0);
    }

    /* One flipped bit anywhere in a frame body: never delivered as anything
     * but the original (a flip that turns a byte into 0x00 splits the frame;
     * both halves must die). */
    for (trial = 0; trial < 20000; trial++) {
        TtWireHdr h, g;
        size_t n = 1u + rnd() % 200u, len, i, off = 0;
        const uint8_t *p;
        size_t plen;
        int got;
        h.type = TT_WIRE_MEAS; h.flags = 0; h.seq = (uint16_t)trial; h.tick = rnd(); h.time_us = rnd();
        for (i = 0; i < n; i++) pl[i] = (uint8_t)rnd();
        len = tt_wire_frame(&h, pl, n, frame, sizeof(frame));
        frame[rnd() % (len - 1u)] ^= (uint8_t)(1u << (rnd() % 8u));
        flips++;
        tt_wire_rx_init(&rx);
        while (off < len) {
            off += tt_wire_rx_feed(&rx, frame + off, len - off, &got, &g, &p, &plen);
            if (got) {
                delivered++;
                if (!frames_equal(&h, &g) || plen != n || memcmp(p, pl, n) != 0) wrong++;
            }
        }
    }
    printf("     %d single-bit flips: %d frames delivered, %d of them wrong\n", flips, delivered, wrong);
    CHECK("frames: a flipped bit is never delivered as wrong data", wrong == 0);

    /* Garbage, an overrun, and a frame glued onto garbage. */
    {
        static uint8_t stream[3 * TT_WIRE_MAX_FRAME + 4096];
        TtWireHdr h, g;
        size_t s = 0, i, len, off = 0;
        const uint8_t *p;
        size_t plen;
        int got, n_got = 0, last_ok = 0;
        tt_wire_rx_init(&rx);
        for (i = 0; i < 300; i++) stream[s++] = (uint8_t)(1u + rnd() % 255u);   /* no delimiter */
        stream[s++] = 0;
        h.type = TT_WIRE_PING; h.flags = 0; h.seq = 1; h.tick = 7; h.time_us = 9;
        len = tt_wire_frame(&h, pl, 4, frame, sizeof(frame));
        memcpy(stream + s, frame, len); s += len;                      /* clean: delivered */
        for (i = 0; i < 40; i++) stream[s++] = (uint8_t)(1u + rnd() % 255u);
        memcpy(stream + s, frame, len); s += len;                      /* glued: lost */
        for (i = 0; i < TT_WIRE_MAX_FRAME + 10u; i++) stream[s++] = 0x55; /* overrun */
        stream[s++] = 0;
        h.seq = 2; h.tick = 8;
        len = tt_wire_frame(&h, pl, 4, frame, sizeof(frame));
        memcpy(stream + s, frame, len); s += len;                      /* delivered */
        while (off < s) {
            off += tt_wire_rx_feed(&rx, stream + off, s - off, &got, &g, &p, &plen);
            if (got) { n_got++; last_ok = g.tick == 8; }
        }
        CHECK("resync: garbage, a glued frame and an overrun cost exactly one frame",
              n_got == 2 && last_ok && rx.overflows == 1 && rx.crc_errors + rx.cobs_errors >= 2);
    }
}

/* ----------------------------------------------------------- 3. payloads */

static void rnd_stamp(TtStamp *s)
{
    s->t_us = rnd();
    s->seq = rnd();
    s->valid = (uint8_t)(rnd() % 4u != 0);
}

static void rnd_meas(TtMeas *m)
{
    int k;
    memset(m, 0, sizeof(*m));
    m->now_us = rnd();
    m->dt_s = rndf(0.0f, 0.02f);
    for (k = 0; k < TT_MAX_WHEELS; k++) {
        rnd_stamp(&m->enc[k].st); m->enc[k].count = (int32_t)rnd();
        rnd_stamp(&m->drv[k].st);
        m->drv[k].iq_a = rndf(-30, 30); m->drv[k].omega_m = rndf(-3000, 3000);
        m->drv[k].vbus_v = rndf(6, 9); m->drv[k].temp_c = rndf(20, 90); m->drv[k].fault = (uint16_t)rnd();
    }
    rnd_stamp(&m->imu.st);
    for (k = 0; k < 3; k++) { m->imu.gyro[k] = rndf(-5, 5); m->imu.accel[k] = rndf(-20, 20); }
    m->imu.gyro[1] = NAN;                 /* NaN and -0 must survive */
    m->imu.accel[0] = -0.0f;
    rnd_stamp(&m->tof.st);
    m->tof.zones = (uint8_t)(rnd() % (TT_TOF_MAX_ZONES + 1u));
    for (k = 0; k < m->tof.zones; k++) { m->tof.status[k] = (uint8_t)rnd(); m->tof.range_m[k] = rndf(0, 4); }
    rnd_stamp(&m->flow.st);
    m->flow.flow_x = rndf(-1, 1); m->flow.flow_y = rndf(-1, 1);
    m->flow.quality = rndf(0, 255); m->flow.height_m = rndf(0, 0.1f);
    m->uwb_n = (uint8_t)(rnd() % (TT_MAX_UWB + 1u));
    for (k = 0; k < m->uwb_n; k++) {
        rnd_stamp(&m->uwb[k].st);
        m->uwb[k].anchor = (uint8_t)rnd();
        m->uwb[k].range_m = rndf(0, 40); m->uwb[k].quality = rndf(0, 10);
        m->uwb[k].pos_m[0] = rndf(-20, 20); m->uwb[k].pos_m[1] = rndf(-10, 10); m->uwb[k].pos_m[2] = 1.5f;
    }
    rnd_stamp(&m->batt.st); m->batt.v = rndf(6, 9); m->batt.i = rndf(-5, 30);
    rnd_stamp(&m->rc.st);
    for (k = 0; k < 8; k++) m->rc.ch[k] = rndf(-1, 1);
    m->rc.failsafe = (uint8_t)rnd();
    rnd_stamp(&m->steer_fb.st); m->steer_fb.rad = rndf(-0.4f, 0.4f);
}

/* What the receiver should see: invalid sources zeroed. */
static void expected_meas(const TtMeas *m, TtMeas *e)
{
    int k;
    *e = *m;
#define DROP(x) do { if (!(x).st.valid) memset(&(x), 0, sizeof(x)); } while (0)
    for (k = 0; k < TT_MAX_WHEELS; k++) { DROP(e->enc[k]); DROP(e->drv[k]); }
    DROP(e->imu); DROP(e->tof); DROP(e->flow); DROP(e->batt); DROP(e->rc); DROP(e->steer_fb);
    for (k = 0; k < TT_MAX_UWB; k++) DROP(e->uwb[k]);
#undef DROP
}

static void test_payloads(void)
{
    static uint8_t pl[TT_WIRE_MAX_PAYLOAD];
    int trial, ok = 1, maxlen = 0, bad_refused = 1;

    for (trial = 0; trial < 3000; trial++) {
        TtMeas m, e, g;
        size_t n;
        rnd_meas(&m);
        expected_meas(&m, &e);
        n = tt_wire_put_meas(pl, sizeof(pl), &m);
        if ((int)n > maxlen) maxlen = (int)n;
        if (n == 0 || tt_wire_get_meas(pl, n, &g) != 0 || memcmp(&e, &g, sizeof(g)) != 0) { ok = 0; break; }
        if (tt_wire_get_meas(pl, n - 1u, &g) == 0) bad_refused = 0;          /* truncated */
        pl[n] = 0;
        if (tt_wire_get_meas(pl, n + 1u, &g) == 0) bad_refused = 0;          /* trailing */
    }
    printf("     largest MEAS payload: %d bytes\n", maxlen);
    CHECK("payloads: 3000 random TtMeas round-trip bit for bit (NaN, -0, zones)", ok);
    CHECK("payloads: truncated or over-long MEAS refused", bad_refused);

    {
        TtAct a, b;
        float log[40], log2[40];
        uint32_t nl = 0;
        size_t n;
        int k;
        for (k = 0; k < 4; k++) a.drive[k] = rndf(-30, 30);
        a.steer_rad = -0.0f; a.brake_01 = NAN;
        for (k = 0; k < 23; k++) log[k] = rndf(-100, 100);
        n = tt_wire_put_act(pl, sizeof(pl), &a, log, 23);
        ok = n > 0 && tt_wire_get_act(pl, n, &b, log2, 40, &nl) == 0 && nl == 23 &&
             memcmp(&a, &b, sizeof(a)) == 0 && memcmp(log, log2, 23 * sizeof(float)) == 0;
        CHECK("payloads: TtAct + 23 log floats round-trip", ok);
    }
    {
        TtCarDesc c, d;
        size_t n;
        int k;
        memset(&c, 0, sizeof(c));
        c.present = 1; c.enc_mask = 3; c.drv_mask = 15; c.has_steer_fb = 1; c.has_uwb = 1;
        for (k = 0; k < 4; k++) {
            c.drv_kind[k] = TT_DRV_IQ; c.enc_radius_m[k] = 0.05f; c.enc_counts_per_rev[k] = 4096;
            c.drv_kt[k] = 0.01f; c.drv_r_ohm[k] = 0.1f; c.drv_gear[k] = 5; c.drv_vmax[k] = 8;
        }
        c.steer_max_rad = 0.4f; c.uwb_lever_m[0] = -0.05f; c.uwb_lever_m[2] = 0.095f;
        n = tt_wire_put_car(pl, sizeof(pl), &c);
        CHECK("payloads: TtCarDesc round-trips",
              n > 0 && tt_wire_get_car(pl, n, &d) == 0 && memcmp(&c, &d, sizeof(c)) == 0);
    }
    {
        TtWireInfo i, j;
        size_t n;
        memset(&i, 0, sizeof(i));
        i.version = TT_WIRE_VERSION; i.n_log = 23; i.rate_hz = 0.0f; i.params_hash = 0xDEADBEEFu;
        strcpy(i.name, "opus_foc");
        strcpy(i.log_names, opus_app_log_names());
        n = tt_wire_put_info(pl, sizeof(pl), &i);
        CHECK("payloads: INFO round-trips with the Opus log names",
              n > 0 && tt_wire_get_info(pl, n, &j) == 0 && memcmp(&i, &j, sizeof(i)) == 0);
    }
}

/* ------------------------------------------------- a host and a device --- */

/* The host side of a link, as tt_bridge has it: frames out, frames in. */
typedef struct {
    HilDev   dev;
    TtWireRx rx;              /* host receiver */
    uint16_t seq;
    uint8_t  out[64 * 1024];  /* device -> host bytes not yet read */
    size_t   out_n;
    FILE    *rec;
    uint8_t  frame[TT_WIRE_MAX_FRAME];
} Link;

static void dev_write(void *wctx, const uint8_t *p, size_t n)
{
    Link *l = (Link *)wctx;
    if (l->out_n + n <= sizeof(l->out)) { memcpy(l->out + l->out_n, p, n); l->out_n += n; }
}

static void link_init(Link *l, const HilApp *app, FILE *rec)
{
    memset(l, 0, sizeof(*l));
    hil_dev_init(&l->dev, app, dev_write, l);
    tt_wire_rx_init(&l->rx);
    l->rec = rec;
}

/* Send a request, feeding the device in random chunks. */
static void link_send(Link *l, uint8_t type, uint32_t tick, const uint8_t *p, size_t n)
{
    TtWireHdr h;
    size_t len, off = 0;
    h.type = type; h.flags = 0; h.seq = l->seq++; h.tick = tick; h.time_us = (uint64_t)tick * 10000u;
    len = tt_wire_frame(&h, p, n, l->frame, sizeof(l->frame));
    if (l->rec) fwrite(l->frame, 1, len, l->rec);
    while (off < len) {
        size_t c = 1u + rnd() % 61u;
        if (c > len - off) c = len - off;
        hil_dev_feed(&l->dev, l->frame + off, c);
        off += c;
    }
}

/* Read the next reply frame (it is already there: the device is synchronous). */
static int link_recv(Link *l, TtWireHdr *h, uint8_t *pl, size_t *pn)
{
    size_t off = 0;
    int got = 0;
    const uint8_t *p = 0;
    while (off < l->out_n && !got)
        off += tt_wire_rx_feed(&l->rx, l->out + off, l->out_n - off, &got, h, &p, pn);
    if (got) {
        memcpy(pl, p, *pn);
        if (l->rec) {
            size_t len = tt_wire_frame(h, pl, *pn, l->frame, sizeof(l->frame));
            fwrite(l->frame, 1, len, l->rec);
        }
    }
    memmove(l->out, l->out + off, l->out_n - off);
    l->out_n -= off;
    return got;
}

/* --------------------------------------------------- 4. device behaviour */

static void test_device(void)
{
    static OpusApp app;
    static OpusHilCtx ctx;
    static HilApp ha;
    static Link l;
    static uint8_t pl[TT_WIRE_MAX_PAYLOAD];
    TtWireHdr h;
    size_t pn = 0;
    TtMeas m;
    uint8_t rate[4] = { 0, 0, 0, 0 };
    float hz = 100.0f;
    uint32_t u;

    opus_hil_app(&ctx, &app, &tt_params_opus_vector_foc, "opus_foc", &ha);
    link_init(&l, &ha, 0);

    link_send(&l, TT_WIRE_PING, 5, (const uint8_t *)"abcd", 4);
    CHECK("device: PING -> PONG with the nonce",
          link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_PONG && h.tick == 5 && pn == 4 &&
          memcmp(pl, "abcd", 4) == 0);

    memset(&m, 0, sizeof(m));
    pn = tt_wire_put_meas(pl, sizeof(pl), &m);
    link_send(&l, TT_WIRE_MEAS, 1, pl, pn);
    CHECK("device: MEAS before INIT -> ACT flagged not ready",
          link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_ACT && h.tick == 1 && (h.flags & TT_WF_NOT_READY));

    link_send(&l, TT_WIRE_INIT, 2, rate, 4);
    {
        TtWireInfo info;
        int ok = link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_INFO &&
                 tt_wire_get_info(pl, pn, &info) == 0 && info.version == TT_WIRE_VERSION &&
                 info.n_log == OPUS_LOG_N &&
                 info.params_hash == tt_params_hash(&tt_params_opus_vector_foc) &&
                 strcmp(info.log_names, opus_app_log_names()) == 0 && (h.flags & TT_WF_NOT_READY);
        CHECK("device: INIT rate 0 identifies (version, log, params hash) without initialising", ok);
    }
    memcpy(&u, &hz, 4);
    rate[0] = (uint8_t)u; rate[1] = (uint8_t)(u >> 8); rate[2] = (uint8_t)(u >> 16); rate[3] = (uint8_t)(u >> 24);
    link_send(&l, TT_WIRE_INIT, 3, rate, 4);
    CHECK("device: INIT 100 Hz -> INFO, ready",
          link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_INFO && !(h.flags & TT_WF_NOT_READY));

    link_send(&l, 0x77, 4, 0, 0);
    CHECK("device: an unknown type -> FAULT",
          link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_FAULT && pl[0] == TT_WFAULT_UNKNOWN_TYPE);

    {
        uint8_t junk[3] = { 0x05, 0x11, 0x00 };   /* a broken frame */
        hil_dev_feed(&l.dev, junk, 3);
        CHECK("device: a damaged frame -> FAULT rx errors",
              link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_FAULT && pl[0] == TT_WFAULT_RX_ERRORS);
    }
}

/* -------------------------------------------------------- 5. lockstep --- */

#define LS_DT 0.01

static void foc_car(TtCarDesc *c, const TtParams *p)
{
    int w;
    memset(c, 0, sizeof(*c));
    c->present = 1;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if (p->odo_wheel_mask & (1u << w)) {
            c->enc_mask |= (uint8_t)(1u << w);
            c->enc_radius_m[w] = p->wheel_radius_m;
            c->enc_counts_per_rev[w] = p->enc_cpr[w] * p->enc_ratio[w];
        }
        if (p->driven_mask & (1u << w)) {
            c->drv_mask |= (uint8_t)(1u << w);
            c->drv_kind[w] = TT_DRV_IQ;
            c->drv_kt[w] = p->kt[w];
            c->drv_gear[w] = p->gear[w];
            c->drv_r_ohm[w] = 0.1f;
            c->drv_vmax[w] = 8.4f;
        }
    }
    c->has_steer_fb = 1;
    c->steer_max_rad = p->max_steer_rad;
    c->has_uwb = 1;
    c->uwb_lever_m[0] = -0.03f;
    c->uwb_lever_m[2] = 0.095f;
}

static void test_lockstep(const char *capture)
{
    static const float anchors[4][3] = { { -6, -12, 1.5f }, { 40, -12, 1.5f }, { 40, 24, 1.5f }, { -6, 24, 1.5f } };
    const TtParams *p = &tt_params_opus_vector_foc;
    static OpusApp direct, remote;
    static OpusHilCtx ctx;
    static HilApp ha;
    static Link l;
    static uint8_t pl[TT_WIRE_MAX_PAYLOAD];
    FILE *rec = capture ? fopen(capture, "wb") : 0;
    TtCarDesc car;
    TtWireHdr h;
    size_t pn;
    double x = 0, y = 0, psi = 0, v = 0, acc[TT_MAX_WHEELS] = { 0, 0, 0, 0 }, psi_dot = 0, ax = 0;
    int k, w, mismatch = 0, steps = 0, max_phase = 0, first_bad = -1, final_phase;
    uint32_t tick;
    float hz = 100.0f;
    uint32_t u;
    uint8_t rate[4];

    opus_hil_app(&ctx, &remote, p, "opus_foc", &ha);
    link_init(&l, &ha, rec);
    foc_car(&car, p);

    /* Both firmwares: init, configure. */
    opus_app_init(&direct, p, hz);
    opus_app_configure(&direct, &car);
    memcpy(&u, &hz, 4);
    rate[0] = (uint8_t)u; rate[1] = (uint8_t)(u >> 8); rate[2] = (uint8_t)(u >> 16); rate[3] = (uint8_t)(u >> 24);
    link_send(&l, TT_WIRE_INIT, 0x80000001u, rate, 4);
    link_recv(&l, &h, pl, &pn);
    pn = tt_wire_put_car(pl, sizeof(pl), &car);
    link_send(&l, TT_WIRE_CONFIG, 0x80000002u, pl, pn);
    CHECK("lockstep: CONFIG acked", link_recv(&l, &h, pl, &pn) && h.type == TT_WIRE_ACK && pl[1] == 0);

    for (tick = 0; tick < 6000u; tick++) {
        TtMeas m;
        TtAct a_d, a_r;
        float log_r[64];
        uint32_t n_d = 0, n_r = 0;
        const float *log_d;
        double r = p->wheel_radius_m, f = 0, t = tick * LS_DT;

        /* The car, as its sensors see it. */
        memset(&m, 0, sizeof(m));
        m.now_us = (tt_us_t)llround(t * 1e6);
        m.dt_s = (float)LS_DT;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            if (p->odo_wheel_mask & (1u << w)) {
                m.enc[w].st.valid = 1; m.enc[w].st.seq = tick; m.enc[w].st.t_us = m.now_us;
                m.enc[w].count = (int32_t)floor(acc[w] / (2.0 * OPUS_PI / (p->enc_cpr[w] * p->enc_ratio[w])));
            }
            if (p->driven_mask & (1u << w)) {
                m.drv[w].st.valid = 1; m.drv[w].st.seq = tick; m.drv[w].st.t_us = m.now_us;
                m.drv[w].omega_m = (float)(v / r * p->gear[w]);
                m.drv[w].vbus_v = 8.0f;
                m.drv[w].temp_c = 35.0f;
            }
        }
        m.imu.st.valid = 1; m.imu.st.seq = tick; m.imu.st.t_us = m.now_us;
        m.imu.gyro[2] = (float)psi_dot;
        m.imu.accel[0] = (float)ax;
        m.imu.accel[1] = (float)(v * psi_dot);
        m.imu.accel[2] = 9.81f;
        m.batt.st.valid = 1; m.batt.st.seq = tick / 10u; m.batt.st.t_us = m.now_us; m.batt.v = 8.0f;
        if (tick % 5u == 0) {                     /* one UWB range per 50 ms */
            const float *an = anchors[(tick / 5u) % 4u];
            double tx = x - 0.03 * cos(psi), ty = y - 0.03 * sin(psi);
            m.uwb_n = 1;
            m.uwb[0].st.valid = 1; m.uwb[0].st.seq = tick / 5u; m.uwb[0].st.t_us = m.now_us;
            m.uwb[0].anchor = (uint8_t)((tick / 5u) % 4u);
            m.uwb[0].range_m = (float)sqrt((an[0] - tx) * (an[0] - tx) + (an[1] - ty) * (an[1] - ty) +
                                           (an[2] - 0.19) * (an[2] - 0.19));
            m.uwb[0].quality = 2.0f;
            memcpy(m.uwb[0].pos_m, an, sizeof(m.uwb[0].pos_m));
        }

        /* Direct. */
        opus_app_step(&direct, &m, &a_d);
        log_d = opus_app_log(&direct, &n_d);

        /* Through the wire. */
        pn = tt_wire_put_meas(pl, sizeof(pl), &m);
        link_send(&l, TT_WIRE_MEAS, tick, pl, pn);
        if (!link_recv(&l, &h, pl, &pn) || h.type != TT_WIRE_ACT || h.tick != tick ||
            tt_wire_get_act(pl, pn, &a_r, log_r, 64, &n_r) != 0 || n_r != n_d ||
            memcmp(&a_d, &a_r, sizeof(a_d)) != 0 || memcmp(log_d, log_r, n_d * sizeof(float)) != 0) {
            if (first_bad < 0) first_bad = (int)tick;
            mismatch++;
        }
        steps++;
        if (direct.fw.mission.phase > max_phase) max_phase = direct.fw.mission.phase;

        /* The plant: torque-controlled wheels, a bicycle yaw. */
        for (w = 0; w < TT_MAX_WHEELS; w++)
            if (p->driven_mask & (1u << w)) {
                double iq = a_d.drive[w], k_t = p->gear[w] * p->kt[w];
                double tq = iq >= 0 ? iq * k_t * p->eta_drive : iq * k_t * p->eta_back;
                f += tq / r;
            }
        f -= (double)a_d.brake_01 * 4.0 * p->brake_max_nm / r * (v > 0.01 ? 1.0 : 0.0);
        if (v > 0.01) f -= p->drag_c0 + p->drag_c1 * v + p->drag_c2 * v * v;
        ax = f / p->mass_kg;
        psi_dot = fabs(v) > 0.05 ? v / p->wheelbase_m * tan((double)a_d.steer_rad) : 0.0;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            double side = (w == TT_FL || w == TT_RL) ? -0.5 : 0.5;
            acc[w] += (v + side * psi_dot * p->track_front_m) / r * LS_DT;
        }
        x += v * cos(psi) * LS_DT;
        y += v * sin(psi) * LS_DT;
        psi += psi_dot * LS_DT;
        v += ax * LS_DT;
        if (v < 0.0) v = 0.0;
        if (direct.fw.mission.phase == OPUS_DONE || direct.fw.mission.phase == OPUS_FAULT) break;
    }
    final_phase = direct.fw.mission.phase;
    printf("     %d ticks, furthest phase %d, final phase %d, fault 0x%04X, safety 0x%04X; "
           "%u device steps; %d mismatches (first at %d)\n",
           steps, max_phase, final_phase, (unsigned)direct.fw.mission.fault,
           (unsigned)direct.fw.safe.faults, (unsigned)l.dev.steps, mismatch, first_bad);
    CHECK("lockstep: the drive got past the turn (a real mission, not an idle car)",
          max_phase >= OPUS_CRUISE_B);
    CHECK("lockstep: every tick's actuator writes and log are bit-identical through the wire",
          mismatch == 0 && steps > 500 && l.dev.steps == (uint32_t)steps);
    CHECK("lockstep: no wire errors either way",
          l.rx.crc_errors + l.rx.cobs_errors + l.rx.seq_gaps + l.dev.rx.crc_errors +
          l.dev.rx.cobs_errors + l.dev.rx.seq_gaps == 0);

    link_send(&l, TT_WIRE_SHUTDOWN, 0x80000003u, 0, 0);
    link_recv(&l, &h, pl, &pn);
    if (rec) fclose(rec);
    (void)k;
}

int main(int argc, char **argv)
{
    test_vectors();
    test_frames();
    test_payloads();
    test_device();
    test_lockstep(argc > 1 ? argv[1] : 0);
    printf("\n%s (%d failed)\n", g_fail ? "FAILED" : "PASSED", g_fail);
    return g_fail ? 1 : 0;
}
