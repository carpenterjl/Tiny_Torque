/*
 * test_drivers.c — MAIN's portable driver halves (drivers/) and the pack SoC
 * estimator (core/tt_soc.h). Registered with CTest as `tt_drivers`.
 *
 * Each driver turns bytes or register values into a TtMeas field with the
 * twin's units and conventions; these checks pin both the decoding (vectors
 * built by hand from the formats in each header) and the convention (the
 * value the twin would report for the same physical situation).
 */
#include "tt_rc.h"
#include "tt_icm42688.h"
#include "tt_vl53l5.h"
#include "tt_paa5100.h"
#include "tt_uwb.h"
#include "tt_soc.h"
#include "tt_safety.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;

static void check(const char *name, int ok, const char *fmt, ...)
{
    va_list ap;
    printf(ok ? "PASS  %-56s " : "FAIL  %-56s ", name);
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    printf("\n");
    if (!ok)
        g_fail++;
}

static int near(double a, double b, double tol) { return fabs(a - b) <= tol; }

static uint32_t g_rng = 12345u;
static uint32_t rnd(void) { g_rng = g_rng * 1664525u + 1013904223u; return g_rng >> 8; }
static double urand(void) { return (double)rnd() / 16777216.0; }      /* 0..1 */

/* ---- RC ------------------------------------------------------------------------ */

static void test_rc(void)
{
    uint16_t ch[16], i;
    uint8_t fr[26], st[200], sb[TT_SBUS_FRAME];
    TtCrsfRx cr;
    TtSbusRx sr;
    TtRcCfg cfg;
    TtRcIn rin;
    TtRc rc;
    size_t n, pos, used;
    int got, frames, ok;

    check("CRSF crc8 (DVB-S2) check value", tt_crsf_crc8((const uint8_t *)"123456789", 9) == 0xBCu,
          "0x%02X", tt_crsf_crc8((const uint8_t *)"123456789", 9));

    /* 11-bit packing, least significant bit first. */
    memset(ch, 0, sizeof ch);
    ch[0] = 0x7FFu;
    ch[1] = 0x7FFu;
    tt_sbus_pack(ch, 0u, sb);
    check("11-bit packing: ch1, ch2 all ones", sb[1] == 0xFFu && sb[2] == 0xFFu && sb[3] == 0x3Fu &&
          sb[4] == 0x00u, "%02X %02X %02X %02X", sb[1], sb[2], sb[3], sb[4]);

    for (i = 0; i < 16; i++) ch[i] = (uint16_t)(172u + i * 100u);
    ch[4] = 1811u;            /* arm */
    ch[5] = 172u;             /* kill off */
    tt_crsf_pack_channels(ch, fr);

    /* A stream: junk, a link-statistics frame, the channel frame, a channel
     * frame with a bad CRC; fed in random chunks. */
    {
        static const uint8_t junk[5] = { 0x00, 0x13, 0xC8, 0x01, 0x55 };
        uint8_t ls[14] = { 0xC8, 12, TT_CRSF_T_LINK_STATS, 70, 72, 100, (uint8_t)-5, 1, 4, 3, 80, 98, 7, 0 };
        uint8_t bad[26];
        ls[13] = tt_crsf_crc8(ls + 2, 11);
        memcpy(bad, fr, 26);
        bad[10] ^= 0x40u;
        n = 0;
        memcpy(st + n, junk, 5); n += 5;
        memcpy(st + n, ls, 14); n += 14;
        memcpy(st + n, fr, 26); n += 26;
        memcpy(st + n, bad, 26); n += 26;
    }
    tt_crsf_init(&cr);
    pos = 0;
    frames = 0;
    ok = 1;
    while (pos < n) {
        size_t chunk = 1u + rnd() % 7u, k = 0;
        if (chunk > n - pos) chunk = n - pos;
        while (k < chunk) {
            used = tt_crsf_feed(&cr, st + pos + k, chunk - k, &got);
            k += used;
            if (got & TT_CRSF_GOT_CHANNELS) {
                frames++;
                if (memcmp(cr.ch, ch, sizeof ch) != 0) ok = 0;
            }
        }
        pos += chunk;
    }
    check("CRSF: channels through junk and any chunking", frames == 1 && ok, "%d frames", frames);
    check("CRSF: link statistics decoded", cr.link.lq == 100u && cr.link.snr_db == -5 &&
          cr.link.rssi1_dbm_neg == 70u && cr.link.d_snr_db == 7, "lq %u", cr.link.lq);
    check("CRSF: a damaged frame is counted, not used", cr.crc_errors == 1u && cr.frames == 2u,
          "crc errors %u", (unsigned)cr.crc_errors);

    /* SBUS: two stray header bytes ahead of a frame with the lost flag. */
    tt_sbus_init(&sr);
    tt_sbus_pack(ch, TT_SBUS_FRAME_LOST, sb);
    st[0] = 0x0Fu; st[1] = 0x22u; st[2] = 0x0Fu;
    memcpy(st + 3, sb, TT_SBUS_FRAME);
    pos = 0;
    frames = 0;
    while (pos < 3u + TT_SBUS_FRAME) {
        pos += tt_sbus_feed(&sr, st + pos, 3u + TT_SBUS_FRAME - pos, &got);
        frames += got;
    }
    check("SBUS: resynchronises, decodes channels and flags", frames == 1 &&
          memcmp(sr.ch, ch, sizeof ch) == 0 && sr.flags == TT_SBUS_FRAME_LOST, "errors %u", (unsigned)sr.errors);

    /* Normalisation and the twin's semantics. */
    check("ticks: 172 -> -1, 992 -> 0, 1811 -> +1", tt_rc_norm(172) == -1.0f && fabsf(tt_rc_norm(992)) < 1e-3f &&
          tt_rc_norm(1811) == 1.0f && tt_rc_norm(2000) == 1.0f, "");
    check("an ELRS two-position switch reads +-0.98 (past 0.5)", near(tt_rc_norm(1792), 0.977, 0.002) &&
          near(tt_rc_norm(191), -0.977, 0.002), "%.3f", (double)tt_rc_norm(1792));

    tt_rc_default_cfg(&cfg);
    tt_rc_init(&rin, &cfg);
    tt_rc_read(&rin, 1000u, &rc);
    check("no frame yet: failsafe", rc.st.valid && (rc.failsafe & TT_RC_FAILSAFE) &&
          (rc.failsafe & TT_RC_FRAME_LOST), "");
    tt_rc_frame(&rin, ch, 0, cr.link.lq == 0u, 100000u);
    tt_rc_read(&rin, 110000u, &rc);
    check("a frame: arm on (ch5 > 0.5), kill off, no flags", rc.ch[4] == 1.0f && rc.ch[5] == -1.0f &&
          rc.failsafe == 0u && rc.st.seq == 1u && rc.st.t_us == 100000u, "");
    tt_rc_read(&rin, 140000u, &rc);
    check("40 ms without a frame: FRAME_LOST, channels held", rc.failsafe == TT_RC_FRAME_LOST &&
          rc.ch[4] == 1.0f, "");
    tt_rc_read(&rin, 350000u, &rc);
    check("250 ms: FAILSAFE (the safety layer's RC_LOST)", (rc.failsafe & TT_RC_FAILSAFE) != 0, "");
    tt_rc_frame(&rin, ch, 0, 1, 360000u);
    tt_rc_read(&rin, 361000u, &rc);
    check("the receiver's own failsafe flag passes through", (rc.failsafe & TT_RC_FAILSAFE) && rc.st.seq == 2u, "");
}

/* ---- IMU ----------------------------------------------------------------------- */

static void put_be16(uint8_t *p, int v) { p[0] = (uint8_t)((uint16_t)v >> 8); p[1] = (uint8_t)v; }

static void packet3(uint8_t *p, const int a[3], const int g[3], int temp, unsigned ts)
{
    int k;
    p[0] = TT_ICM_HDR_ACCEL | TT_ICM_HDR_GYRO | TT_ICM_HDR_TS_ODR;
    for (k = 0; k < 3; k++) { put_be16(p + 1 + 2 * k, a[k]); put_be16(p + 7 + 2 * k, g[k]); }
    p[13] = (uint8_t)(int8_t)temp;
    put_be16(p + 14, (int)ts);
}

static void test_imu(void)
{
    uint8_t f[64];
    TtIcmCfg c;
    TtIcmSample s[4];
    TtImu imu;
    size_t used;
    int a1[3] = { 0, 0, 2048 }, g1[3] = { 164, 0, -164 };
    int a2[3] = { 1024, -2048, 0 }, g2[3] = { 0, 1640, 0 };
    int n;

    tt_icm_default_cfg(&c);
    packet3(f, a1, g1, 41, 0x1234u);
    memset(f + 16, 0, 8);
    f[16] = TT_ICM_HDR_ACCEL;                       /* an accel-only packet */
    packet3(f + 24, a2, g2, 0, 0x1239u);
    f[40] = 0x68u; f[41] = 1; f[42] = 2; f[43] = 3; f[44] = 4;   /* partial */
    n = tt_icm_fifo_decode(f, 45, &c, s, 4, &used);
    check("FIFO: two packet-3 samples, an 8-byte packet skipped, partial kept", n == 2 && used == 40u,
          "%d samples, %u bytes", n, (unsigned)used);
    check("level at rest reads (0, 0, +9.81) m/s^2", near(s[0].accel[0], 0, 1e-6) && near(s[0].accel[2], 9.80665, 1e-4),
          "%.4f", (double)s[0].accel[2]);
    check("164 LSB at +-2000 dps = 10 deg/s", near(s[0].gyro[0], 0.174533, 1e-4) &&
          near(s[0].gyro[2], -0.174533, 1e-4), "%.5f", (double)s[0].gyro[0]);
    check("temperature raw / 2.07 + 25, timestamp", near(s[0].temp_c, 44.807, 0.01) && s[0].ts == 0x1234u &&
          s[0].ok, "%.2f C", (double)s[0].temp_c);

    c.axis[0] = -2; c.axis[1] = 1; c.axis[2] = 3;   /* chip turned 90 deg on the board */
    n = tt_icm_fifo_decode(f + 24, 16, &c, s, 4, &used);
    check("axis map: body x = -chip y, body y = chip x", n == 1 && near(s[0].accel[0], 9.80665, 1e-4) &&
          near(s[0].accel[1], 4.903325, 1e-4) && near(s[0].gyro[0], -1.74533, 1e-4), "");
    tt_icm_to_imu(&s[0], &imu);
    check("sample -> TtImu", imu.accel[0] == s[0].accel[0] && imu.gyro[0] == s[0].gyro[0], "");

    f[1] = 0x80; f[2] = 0x00;                       /* accel X = -32768 */
    tt_icm_default_cfg(&c);
    n = tt_icm_fifo_decode(f, 16, &c, s, 4, &used);
    check("an invalid axis marks the sample", n == 1 && !s[0].ok, "");
    f[0] = 0x80u;
    n = tt_icm_fifo_decode(f, 45, &c, s, 4, &used);
    check("an empty-FIFO header ends the read", n == 0 && used == 45u, "");
    check("sample time across the 16-bit wrap", tt_icm_sample_time(50000u, 0x0005u, 0xFFF0u, &c) == 50000u - 21u, "");
}

/* ---- ToF ----------------------------------------------------------------------- */

static void test_tof(void)
{
    int16_t d[64];
    uint8_t st[64], nb[64];
    TtVl53Cfg c;
    TtTof t;
    int i;

    for (i = 0; i < 64; i++) { d[i] = (int16_t)(100 + i * 10); st[i] = 5u; nb[i] = 1u; }
    tt_vl53_default_cfg(&c);
    c.width = 4u;
    c.mirror = 0u;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("4x4 as delivered: 16 zones, metres, status 0", t.zones == 16u && near(t.range_m[5], 0.150, 1e-6) &&
          t.status[5] == 0u, "");
    c.mirror = 1u;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("mirror: zone (0,0) gets the ULD's (0,3)", near(t.range_m[0], 0.130, 1e-6) &&
          near(t.range_m[3], 0.100, 1e-6), "%.3f", (double)t.range_m[0]);
    c.mirror = 0u;
    c.rot = 1u;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("a quarter turn clockwise: top-left goes top-right", near(t.range_m[3], 0.100, 1e-6) &&
          near(t.range_m[15], 0.130, 1e-6), "");

    c.rot = 0u;
    nb[0] = 0u; st[1] = 6u; st[2] = 255u; st[3] = 9u; d[4] = -3;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("no target: status 1 at range_max; 6 weak; 255 other; 9 valid",
          t.status[0] == 1u && near(t.range_m[0], 4.0, 1e-6) && t.status[1] == 2u && t.status[2] == 3u &&
          t.status[3] == 0u && t.status[4] == 3u, "");
    c.weak_ok = 1u;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("weak_ok accepts status 6", t.status[1] == 0u && near(t.range_m[1], 0.110, 1e-6), "");
    c.width = 8u;
    tt_vl53_to_tof(d, st, nb, &c, &t);
    check("8x8: 64 zones", t.zones == 64u, "");
}

/* ---- flow ---------------------------------------------------------------------- */

static void test_flow(void)
{
    uint8_t b[TT_PAA_BURST] = { 0x80, 0, 0, 0, 0, 0, 100, 50, 80, 20, 0x01, 0x00 };
    TtPaaBurst o;
    TtPaaCfg c;
    TtFlow f;
    double v = 0.5, h = 0.025, dt = 0.1;
    int counts = (int)lround(47.7 * v / h * dt);    /* what the twin's counts would be */

    b[2] = (uint8_t)counts; b[3] = 0;
    b[4] = (uint8_t)(-3); b[5] = 0xFF;
    tt_paa_burst_decode(b, &o);
    check("burst: deltas little-endian, squal, shutter", o.dx == counts && o.dy == -3 && o.squal == 100u &&
          o.shutter == 0x0100u, "dx %d dy %d", o.dx, o.dy);
    tt_paa_default_cfg(&c);
    tt_paa_to_flow(&o, (float)dt, &c, &f);
    check("0.5 m/s at 25 mm: v = h * flow, as the twin", near(f.flow_x * f.height_m, v, 0.01) &&
          near(f.quality, 100.0 / 255.0, 1e-6), "flow %.2f rad/s -> %.3f m/s", (double)f.flow_x,
          (double)(f.flow_x * f.height_m));
    c.axis[0] = 2; c.axis[1] = -1;
    tt_paa_to_flow(&o, (float)dt, &c, &f);
    check("axis map: x <- chip Y, y <- -chip X", near(f.flow_x, -3.0 / (47.7 * dt), 1e-3) &&
          near(f.flow_y, -counts / (47.7 * dt), 1e-3), "");
    b[0] = 0x00;
    tt_paa_burst_decode(b, &o);
    check("no motion bit: no motion", o.dx == 0 && o.dy == 0, "");
    b[0] = 0x80; b[6] = 10;
    tt_paa_burst_decode(b, &o);
    tt_paa_to_flow(&o, (float)dt, &c, &f);
    check("low squal: not tracking (quality 0)", f.quality == 0.0f && f.flow_x == 0.0f, "");
}

/* ---- UWB ----------------------------------------------------------------------- */

static void test_uwb(void)
{
    const double tick = 1.0 / (499.2e6 * 128.0);
    const double tof = 100e-9;                       /* 29.98 m */
    const double ppm_i = 20e-6, ppm_r = -15e-6;      /* the two crystals */
    const double reply1 = 500e-6, reply2 = 600e-6;  /* true times */
    uint64_t r1, d1, r2, d2;
    float t_ds, t_ss, t_ss0, gap, fp_amp;
    float pos[3] = { 1.0f, 2.0f, 2.5f };
    TtUwb u;

    check("40-bit timestamps wrap", tt_dw_ts_diff(5u, TT_DW_TS_MASK - 2u) == 8u, "");

    /* Intervals as each device's clock counts them. */
    r1 = (uint64_t)llround((2.0 * tof + reply1) * (1.0 + ppm_i) / tick);
    d1 = (uint64_t)llround(reply1 * (1.0 + ppm_r) / tick);
    r2 = (uint64_t)llround((2.0 * tof + reply2) * (1.0 + ppm_r) / tick);
    d2 = (uint64_t)llround(reply2 * (1.0 + ppm_i) / tick);
    t_ds = tt_dw_dstwr_ticks(r1, d1, r2, d2);
    check("DS-TWR with 35 ppm between the clocks: within 2 cm",
          near(tt_dw_range_m(t_ds, 0.0f), tof * 299702547.0, 0.02), "%.4f m (true %.4f)",
          (double)tt_dw_range_m(t_ds, 0.0f), tof * 299702547.0);
    t_ss0 = tt_dw_sstwr_ticks(r1, d1, 0.0f);
    t_ss = tt_dw_sstwr_ticks(r1, d1, (float)((ppm_r - ppm_i) / (1.0 + ppm_i)));
    check("SS-TWR: metres off uncorrected, centimetres with the clock offset",
          fabs(tt_dw_range_m(t_ss0, 0.0f) - tof * 299702547.0) > 1.0 &&
          near(tt_dw_range_m(t_ss, 0.0f), tof * 299702547.0, 0.03), "%.3f -> %.4f m",
          (double)tt_dw_range_m(t_ss0, 0.0f), (double)tt_dw_range_m(t_ss, 0.0f));
    check("an interval over ~34 ms is refused", tt_dw_dstwr_ticks(0x80000000ull, 1, 1, 1) !=
          tt_dw_dstwr_ticks(0x80000000ull, 1, 1, 1), "");

    fp_amp = sqrtf(1000.0f * 2097152.0f / powf(10.0f, 0.3f) / 3.0f);
    gap = tt_dw_fp_gap_db(1000.0f, fp_amp, fp_amp, fp_amp);
    check("first-path gap from the diagnostics", near(gap, 3.0, 1e-3), "%.4f dB", (double)gap);
    check("no first path: 99 dB", tt_dw_fp_gap_db(1000.0f, 0, 0, 0) == 99.0f, "");
    tt_uwb_fill(&u, 2u, 29.98f, gap, pos);
    check("-> TtUwb", u.anchor == 2u && u.range_m == 29.98f && u.quality == gap && u.pos_m[2] == 2.5f, "");
}

/* ---- pack SoC ------------------------------------------------------------------ */

/* The twin's pack (CarVehicle: coulomb counter, CellOcv, internal R) with
 * INA228-class sensing (0.3 % gain, 2 mA offset, ~1 mV noise). */
typedef struct { double soc, cap_ah, r; int cells; } Pack;

static void sense(const Pack *p, double i, uint32_t seq, tt_us_t t, TtBatt *b)
{
    double v = p->cells * tt_soc_cell_ocv((float)p->soc) - i * p->r;
    b->st.valid = 1u;
    b->st.seq = seq;
    b->st.t_us = t;
    b->v = (float)(v + (urand() - 0.5) * 0.002);
    b->i = (float)(i * 1.003 + 0.002 + (urand() - 0.5) * 0.004);
}

static void test_soc(void)
{
    TtSocCfg c;
    TtSoc s;
    TtBatt b;
    Pack p = { 0.80, 5.2, 0.020, 2 };
    tt_us_t t = 0u;
    uint32_t seq = 0u;
    double worst = 0.0;
    int k, step;

    check("the OCV curve is the twin's and inverts", near(tt_soc_cell_ocv(0.5f), 3.65 + 0.4 * 0.3 / 0.7, 1e-5) &&
          near(tt_soc_from_cell_ocv(tt_soc_cell_ocv(0.33f)), 0.33, 1e-5) &&
          tt_soc_from_cell_ocv(2.5f) == 0.0f && tt_soc_from_cell_ocv(4.3f) == 1.0f, "");

    tt_soc_default_cfg(&c);
    tt_soc_init(&s, &c);
    sense(&p, 0.7, ++seq, t, &b);
    tt_soc_update(&s, &b);
    check("power-up at rest: starts from the voltage", near(s.soc, 0.80, 0.01), "%.3f", (double)s.soc);

    /* 20 minutes of driving at 100 Hz: 15 s at 18 A, 5 s regen at -6 A,
     * 10 s parked on the electronics' 0.7 A. */
    for (step = 0; step < 20 * 60 * 100; step++) {
        double ph = fmod(step * 0.01, 30.0);
        double i = ph < 15.0 ? 18.0 : (ph < 20.0 ? -6.0 : 0.7);
        p.soc -= i * 0.01 / (3600.0 * p.cap_ah);
        t += 10000u;
        sense(&p, i, ++seq, t, &b);
        tt_soc_update(&s, &b);
        if (fabs(s.soc - p.soc) > worst) worst = fabs(s.soc - p.soc);
    }
    check("20 min of drive cycles: within 2 % of the truth throughout", worst < 0.02,
          "worst %.2f %%, end %.3f vs %.3f", worst * 100.0, (double)s.soc, p.soc);
    check("a stale sample is not counted twice", tt_soc_update(&s, &b) == s.soc, "");

    /* A wrong start (say the pack was swapped while powered): the rests
     * pull it back. */
    s.soc = (float)(p.soc + 0.25);
    for (k = 0; k < 150 * 100; k++) {
        t += 10000u;
        sense(&p, 0.7, ++seq, t, &b);
        tt_soc_update(&s, &b);
    }
    check("25 % wrong, 150 s parked: back within 1.5 %", fabs(s.soc - p.soc) < 0.015 && s.resting,
          "%.3f vs %.3f", (double)s.soc, p.soc);
    t += 10000u;
    sense(&p, 10.0, ++seq, t, &b);
    tt_soc_update(&s, &b);
    check("under load it is not resting", !s.resting, "");
    check("amp-hours left above the cut-off", near(tt_soc_remaining_ah(&s, 0.05f), (s.soc - 0.05) * 5.2, 1e-4), "%.2f Ah",
          (double)tt_soc_remaining_ah(&s, 0.05f));
}

int main(void)
{
    test_rc();
    test_imu();
    test_tof();
    test_flow();
    test_uwb();
    test_soc();
    printf(g_fail ? "\n%d FAILED\n" : "\nall passed\n", g_fail);
    return g_fail ? 1 : 0;
}
