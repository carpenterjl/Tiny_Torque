/*
 * test_can.c — the CAN-FD drive bus (core/tt_can.h) and MAIN's end of it
 * (core/tt_drvbus.h), SYS-03. Registered with CTest as `tt_can_bus`.
 *
 *   1. Byte vectors that pin the CMD and STATUS layouts, round trips of
 *      every frame, fixed-point saturation and NaN.
 *   2. Identifiers, DLC lengths, worst-case frame lengths.
 *   3. The bus load of the planned schedules (HW_00_SYSTEM.html §7): the
 *      target must stay under 50 %, and the table is printed for the doc.
 *   4. tt_drvbus: commands remembered, STATUS -> TtMeas with timestamps on
 *      MAIN's clock, sequence gaps, the arming check.
 */
#include "tt_can.h"
#include "tt_drvbus.h"
#include "tt_safety.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;

static void check(const char *name, int ok, const char *fmt, ...)
{
    va_list ap;
    printf(ok ? "PASS  %-52s " : "FAIL  %-52s ", name);
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    printf("\n");
    if (!ok)
        g_fail++;
}

static int near(float a, float b, float tol) { return fabsf(a - b) <= tol; }

/* ---- 1. layouts and round trips ------------------------------------------ */

static void test_frames(void)
{
    TtCanFrame f;
    TtCanCmd c, c2;
    TtCanStatus s, s2;
    TtCanCfg q, q2;
    TtCanBoot b, b2;
    TtCanDebug d, d2;
    TtCanEstop e, e2;
    int i, ok;

    /* CMD: seq 0x1234, enable 0b0101, CLEAR, Iq +1.5, -2.25, 40 (saturates),
     * NaN (-> 0), t = 0xA1B2C3D4. */
    static const uint8_t cmd_vec[16] = {
        0x34, 0x12, 0x05, 0x01, 0xDC, 0x05, 0x36, 0xF7,
        0xFF, 0x7F, 0x00, 0x00, 0xD4, 0xC3, 0xB2, 0xA1 };
    memset(&c, 0, sizeof c);
    c.seq = 0x1234u;
    c.enable = 0x05u;
    c.flags = TT_CAN_CMD_CLEAR;
    c.iq_a[0] = 1.5f;
    c.iq_a[1] = -2.25f;
    c.iq_a[2] = 40.0f;
    c.iq_a[3] = nanf("");
    c.t_us = 0xA1B2C3D4u;
    tt_can_pack_cmd(&f, &c);
    check("CMD id, length, BRS", f.id == 0x020u && f.len == 16u && f.brs == 1u, "id 0x%03X len %u",
          (unsigned)f.id, f.len);
    check("CMD byte layout", memcmp(f.data, cmd_vec, 16) == 0,
          "%02X %02X %02X %02X %02X %02X ...", f.data[0], f.data[1], f.data[2], f.data[3],
          f.data[4], f.data[5]);
    ok = tt_can_get_cmd(&f, &c2) == 0 && c2.seq == c.seq && c2.enable == 5u &&
         c2.flags == TT_CAN_CMD_CLEAR && c2.t_us == c.t_us &&
         near(c2.iq_a[0], 1.5f, 1e-6f) && near(c2.iq_a[1], -2.25f, 1e-6f) &&
         near(c2.iq_a[2], 32.767f, 1e-4f) && c2.iq_a[3] == 0.0f;
    check("CMD round trip (saturation, NaN -> 0)", ok, "iq %.3f %.3f %.3f %.3f",
          (double)c2.iq_a[0], (double)c2.iq_a[1], (double)c2.iq_a[2], (double)c2.iq_a[3]);
    f.len = 12u;
    check("CMD with the wrong length is refused", tt_can_get_cmd(&f, &c2) == -1, "");

    /* STATUS from node 2 (RL). */
    {
        static const uint8_t st_vec[24] = {
            0x34, 0x12, 0x2C, 0x01, 0x07, 0x03, 0x14, 0x00,
            0x88, 0x13, 0x0C, 0xFE, 0xFE, 0xFF, 0xFF, 0xFF,
            0xD2, 0x04, 0xD0, 0x1C, 0x2B, 0x03, 0x00, 0x80 };
        memset(&s, 0, sizeof s);
        s.cmd_seq = 0x1234u;
        s.age_us = 300u;
        s.seq = 7u;
        s.state = TT_NODE_RUN;
        s.fault = TT_DRV_ILIMIT | TT_DRV_CMD_TIMEOUT;
        s.iq_a = 5.0f;
        s.id_a = -0.5f;
        s.enc_count = -2;
        s.omega_m = 123.4f;
        s.vbus_v = 7.376f;
        s.t_fet_c = 81.1f;
        s.t_wind_c = nanf("");
        tt_can_pack_status(&f, TT_RL, &s);
        check("STATUS id, length", f.id == 0x102u && f.len == 24u, "id 0x%03X len %u",
              (unsigned)f.id, f.len);
        check("STATUS byte layout", memcmp(f.data, st_vec, 24) == 0,
              "%02X %02X %02X %02X | %02X %02X %02X %02X ...", f.data[0], f.data[1],
              f.data[2], f.data[3], f.data[8], f.data[9], f.data[10], f.data[11]);
        ok = tt_can_get_status(&f, &s2) == 0 && s2.cmd_seq == s.cmd_seq && s2.age_us == 300u &&
             s2.seq == 7u && s2.state == TT_NODE_RUN && s2.fault == s.fault &&
             near(s2.iq_a, 5.0f, 1e-6f) && near(s2.id_a, -0.5f, 1e-6f) && s2.enc_count == -2 &&
             near(s2.omega_m, 123.4f, 0.051f) && near(s2.vbus_v, 7.376f, 1e-4f) &&
             near(s2.t_fet_c, 81.1f, 0.051f) && s2.t_wind_c != s2.t_wind_c;
        check("STATUS round trip (absent sensor = NaN)", ok, "omega %.1f vbus %.3f fet %.1f",
              (double)s2.omega_m, (double)s2.vbus_v, (double)s2.t_fet_c);
    }
    /* Temperatures: -3276.8 would read as the absent marker; it must not. */
    s.t_fet_c = -5000.0f;
    tt_can_pack_status(&f, 0, &s);
    tt_can_get_status(&f, &s2);
    check("a real temperature never packs as 'no sensor'", s2.t_fet_c == s2.t_fet_c,
          "%.1f", (double)s2.t_fet_c);

    q.op = TT_CFG_SET; q.tag = 9u; q.key = 17u; q.value = 0x41200000u;
    tt_can_pack_cfg(&f, TT_CAN_ID_CFG_REQ, TT_FR, &q);
    ok = f.id == 0x201u && tt_can_get_cfg(&f, &q2) == 0 && q2.op == q.op && q2.tag == 9u &&
         q2.key == 17u && q2.value == q.value;
    check("CFG request round trip", ok, "id 0x%03X", (unsigned)f.id);

    b.proto = TT_CAN_PROTO_VERSION; b.node = 3u; b.hw_rev = 2u; b.reset_cause = 4u;
    b.fw_id = 0xDEADBEEFu; b.cfg_hash = 0x01020304u; b.state = TT_NODE_IDLE;
    b.cal_valid = 1u; b.enc_cpr = 4096u;
    tt_can_pack_boot(&f, 3u, &b);
    ok = f.id == 0x703u && tt_can_get_boot(&f, &b2) == 0 && memcmp(&b, &b2, sizeof b) == 0;
    check("BOOT round trip", ok, "id 0x%03X", (unsigned)f.id);

    memset(&d, 0, sizeof d);
    d.seq = 77u; d.set = 1u; d.n = 15u;
    for (i = 0; i < 15; i++) d.v[i] = (float)i * -0.25f;
    d.v[3] = nanf("");
    tt_can_pack_debug(&f, 0u, &d);
    ok = tt_can_get_debug(&f, &d2) == 0 && d2.n == 15u && d2.v[14] == d.v[14] && d2.v[3] != d2.v[3];
    check("DEBUG round trip (floats bit for bit)", ok, "");

    e.reason = TT_SF_KILL | TT_SF_TIPOVER;
    tt_can_pack_estop(&f, &e);
    ok = f.id == 0x010u && tt_can_get_estop(&f, &e2) == 0 && e2.reason == e.reason;
    check("ESTOP round trip", ok, "");

    check("q16 rounds to nearest, saturates", tt_can_q16(0.0016f, 1000.0f) == 2 &&
          tt_can_q16(-0.0016f, 1000.0f) == -2 && tt_can_q16(-50.0f, 1000.0f) == -32768, "");
}

/* ---- 2. identifiers, lengths, frame bits ---------------------------------- */

static void test_ids_and_lengths(void)
{
    uint8_t node;
    uint32_t nb, db;
    int ok;

    ok = tt_can_kind(0x010u, &node) == TT_CAN_K_ESTOP && tt_can_kind(0x020u, &node) == TT_CAN_K_CMD &&
         tt_can_kind(0x103u, &node) == TT_CAN_K_STATUS && node == 3u &&
         tt_can_kind(0x281u, &node) == TT_CAN_K_CFG_RSP && node == 1u &&
         tt_can_kind(0x702u, &node) == TT_CAN_K_BOOT && node == 2u &&
         tt_can_kind(0x104u, &node) == TT_CAN_K_UNKNOWN && tt_can_kind(0x7FFu, NULL) == TT_CAN_K_UNKNOWN;
    check("identifiers decode to kind and node", ok, "");
    check("ESTOP < CMD < STATUS < CFG < DEBUG < BOOT in priority",
          TT_CAN_ID_ESTOP < TT_CAN_ID_CMD && TT_CAN_ID_CMD < TT_CAN_ID_STATUS &&
          TT_CAN_ID_STATUS + 3u < TT_CAN_ID_CFG_REQ && TT_CAN_ID_DEBUG < TT_CAN_ID_BOOT, "");

    ok = tt_can_dlc_to_len(8) == 8 && tt_can_dlc_to_len(9) == 12 && tt_can_dlc_to_len(15) == 64 &&
         tt_can_len_round(13) == 16 && tt_can_len_round(24) == 24 && tt_can_len_round(65) == 0 &&
         tt_can_len_round(TT_CAN_LEN_CMD) == TT_CAN_LEN_CMD &&
         tt_can_len_round(TT_CAN_LEN_STATUS) == TT_CAN_LEN_STATUS &&
         tt_can_len_round(TT_CAN_LEN_BOOT) == TT_CAN_LEN_BOOT;
    check("DLC lengths; every payload is a valid FD length", ok, "");

    /* Hand count for 16 bytes: nominal 17 + 4 stuff + 12 tail = 33; data
     * 5 + 128 + 33 stuff + CRC field 27 + delimiter = 194. */
    tt_can_frame_bits(16, &nb, &db);
    check("CMD frame: 33 nominal + 194 data bits worst case", nb == 33u && db == 194u,
          "%u + %u", nb, db);
    tt_can_frame_bits(24, &nb, &db);
    check("STATUS frame: CRC-21 above 16 bytes", nb == 33u && db == 279u, "%u + %u", nb, db);
}

/* ---- 3. bus load ------------------------------------------------------------ */

typedef struct { const char *name; float nom, data; int brs; float status_hz; } Sched;

static float load_pct(const Sched *s)
{
    /* One CMD per ms, four STATUS frames at status_hz, per second. */
    float cmd = tt_can_frame_us(TT_CAN_LEN_CMD, s->brs, s->nom, s->data) * 1000.0f;
    float st = 4.0f * tt_can_frame_us(TT_CAN_LEN_STATUS, s->brs, s->nom, s->data) * s->status_hz;
    return (cmd + st) * 1e-6f * 100.0f;
}

static void test_bus_load(void)
{
    static const Sched sch[] = {
        { "target 1/5 Mbit/s BRS, status 1 kHz",   1e6f, 5e6f, 1, 1000.0f },
        { "fallback 1/2 Mbit/s BRS, status 1 kHz", 1e6f, 2e6f, 1, 1000.0f },
        { "fallback 1/2 Mbit/s BRS, status 500 Hz", 1e6f, 2e6f, 1, 500.0f },
        { "bring-up 1 Mbit/s no BRS, status 1 kHz", 1e6f, 1e6f, 0, 1000.0f },
        { "bring-up 1 Mbit/s no BRS, status 250 Hz", 1e6f, 1e6f, 0, 250.0f },
        { "bring-up 1 Mbit/s no BRS, status 125 Hz", 1e6f, 1e6f, 0, 125.0f },
    };
    size_t i;
    float l[6];
    printf("      bus load (worst-case stuffing), CMD at 1 kHz + 4 x STATUS:\n");
    printf("        CMD %.0f us, STATUS %.0f us at 1/5 Mbit/s; %.0f / %.0f us without BRS\n",
           (double)tt_can_frame_us(TT_CAN_LEN_CMD, 1, 1e6f, 5e6f),
           (double)tt_can_frame_us(TT_CAN_LEN_STATUS, 1, 1e6f, 5e6f),
           (double)tt_can_frame_us(TT_CAN_LEN_CMD, 0, 1e6f, 1e6f),
           (double)tt_can_frame_us(TT_CAN_LEN_STATUS, 0, 1e6f, 1e6f));
    for (i = 0; i < sizeof sch / sizeof sch[0]; i++) {
        l[i] = load_pct(&sch[i]);
        printf("        %-44s %5.1f %%\n", sch[i].name, (double)l[i]);
    }
    check("target schedule under 50 % bus load", l[0] < 50.0f, "%.1f %%", (double)l[0]);
    check("2 Mbit/s data needs status at 500 Hz", l[1] > 50.0f && l[2] < 50.0f,
          "%.1f / %.1f %%", (double)l[1], (double)l[2]);
    check("bring-up without BRS: status at 125 Hz (status_div 8)", l[3] > 100.0f && l[5] < 50.0f,
          "%.1f / %.1f %%", (double)l[3], (double)l[5]);
}

/* ---- 4. MAIN's end ---------------------------------------------------------- */

static void status_frame(TtCanFrame *f, uint8_t node, uint16_t cmd_seq, uint16_t age, uint8_t seq,
                         uint8_t state, uint16_t fault, int32_t enc)
{
    TtCanStatus s;
    memset(&s, 0, sizeof s);
    s.cmd_seq = cmd_seq;
    s.age_us = age;
    s.seq = seq;
    s.state = state;
    s.fault = fault;
    s.iq_a = 3.0f;
    s.enc_count = enc;
    s.omega_m = 50.0f;
    s.vbus_v = 7.4f;
    s.t_fet_c = 60.0f;
    s.t_wind_c = nanf("");
    tt_can_pack_status(f, node, &s);
}

static void test_drvbus(void)
{
    TtDrvBus bus;
    TtMeas m;
    TtCanFrame f;
    TtCanCmd c;
    TtCanBoot b;
    float iq[4] = { 1.0f, -1.0f, 2.0f, -2.0f };
    float lat = tt_can_frame_us(TT_CAN_LEN_CMD, 1, 1e6f, 5e6f);
    uint32_t want;
    int i, k;

    tt_drvbus_init(&bus, lat);
    memset(&m, 0, sizeof m);

    for (i = 0; i < 5; i++) tt_drvbus_cmd(&bus, iq, 0x0Fu, 0u, 1000u * (uint32_t)i, &f);
    tt_can_get_cmd(&f, &c);
    check("commands count up and carry MAIN's clock", c.seq == 4u && c.t_us == 4000u &&
          c.enable == 0x0Fu && near(c.iq_a[3], -2.0f, 1e-6f), "seq %u t %u", c.seq, (unsigned)c.t_us);

    /* Node 1 answers command 4, sampled 300 us after it arrived. */
    status_frame(&f, 1u, 4u, 300u, 10u, TT_NODE_RUN, 0u, 12345);
    k = tt_drvbus_rx(&bus, &f, 4900u, &m);
    want = 4000u + (uint32_t)(lat + 0.5f) + 300u;
    check("STATUS stamped on MAIN's clock: sent + frame + age", k == TT_CAN_K_STATUS &&
          m.drv[1].st.t_us == want && m.drv[1].st.valid && m.drv[1].st.seq == 1u &&
          m.enc[1].count == 12345 && m.enc[1].st.t_us == want, "t %u want %u", (unsigned)m.drv[1].st.t_us,
          (unsigned)want);
    check("STATUS fills TtDriveFb (FET temp when no winding sensor)",
          near(m.drv[1].iq_a, 3.0f, 1e-6f) && near(m.drv[1].omega_m, 50.0f, 0.051f) &&
          near(m.drv[1].vbus_v, 7.4f, 1e-3f) && near(m.drv[1].temp_c, 60.0f, 0.051f), "temp %.1f",
          (double)m.drv[1].temp_c);

    /* The same frame again is not fresh; a gap of two is counted. */
    tt_drvbus_rx(&bus, &f, 4950u, &m);
    check("a repeated STATUS is not a fresh sample", m.drv[1].st.seq == 1u, "");
    status_frame(&f, 1u, 4u, 900u, 13u, TT_NODE_RUN, 0u, 12350);
    tt_drvbus_rx(&bus, &f, 5100u, &m);
    check("missing STATUS frames are counted", bus.node[1].seq_gaps == 2u && m.drv[1].st.seq == 2u,
          "gaps %u", (unsigned)bus.node[1].seq_gaps);

    /* An age that would land after arrival is clamped to it; an unknown
     * command or no command at all falls back to the arrival time. */
    status_frame(&f, 1u, 4u, 5000u, 14u, TT_NODE_RUN, 0u, 0);
    tt_drvbus_rx(&bus, &f, 6000u, &m);
    check("a stamp is never later than the arrival", m.drv[1].st.t_us == 6000u, "%u",
          (unsigned)m.drv[1].st.t_us);
    status_frame(&f, 1u, 999u, 100u, 15u, TT_NODE_RUN, 0u, 0);
    tt_drvbus_rx(&bus, &f, 6100u, &m);
    check("an unremembered command -> arrival time", m.drv[1].st.t_us == 6100u, "");
    status_frame(&f, 1u, 0u, 0xFFFFu, 16u, TT_NODE_IDLE, 0u, 0);
    tt_drvbus_rx(&bus, &f, 6200u, &m);
    check("no command yet (age 0xFFFF) -> arrival time", m.drv[1].st.t_us == 6200u, "");

    /* The TX event time replaces the queue time. */
    tt_drvbus_cmd(&bus, iq, 0x0Fu, 0u, 7000u, &f);
    tt_drvbus_sent(&bus, 5u, 7040u);
    status_frame(&f, 2u, 5u, 100u, 0u, TT_NODE_RUN, 0u, 0);
    tt_drvbus_rx(&bus, &f, 7900u, &m);
    want = 7040u + (uint32_t)(lat + 0.5f) + 100u;
    check("the TX event time refines the stamp", m.drv[2].st.t_us == want, "%u want %u",
          (unsigned)m.drv[2].st.t_us, (unsigned)want);

    /* Arming check. */
    tt_drvbus_init(&bus, lat);
    check("no node has booted: none ready", tt_drvbus_not_ready(&bus, 0x0Fu, 0u, 20000u) == 0x0Fu, "");
    memset(&b, 0, sizeof b);
    b.proto = TT_CAN_PROTO_VERSION;
    b.cal_valid = 1u;
    b.enc_cpr = 4096u;
    b.cfg_hash = 0xABCDu;
    for (i = 0; i < 4; i++) {
        b.node = (uint8_t)i;
        tt_can_pack_boot(&f, (uint8_t)i, &b);
        tt_drvbus_rx(&bus, &f, 100u, NULL);
        status_frame(&f, (uint8_t)i, 0u, 0xFFFFu, 0u, TT_NODE_IDLE, i == 2 ? TT_DRV_NOT_CAL : 0u, 0);
        tt_drvbus_rx(&bus, &f, 1000u, NULL);
    }
    check("an uncalibrated node keeps the car from arming",
          tt_drvbus_not_ready(&bus, 0x0Fu, 1500u, 20000u) == 0x04u, "0x%X",
          tt_drvbus_not_ready(&bus, 0x0Fu, 1500u, 20000u));
    check("only the nodes asked about count", tt_drvbus_not_ready(&bus, 0x0Bu, 1500u, 20000u) == 0u, "");
    check("a silent node is not ready", tt_drvbus_not_ready(&bus, 0x0Bu, 30000u, 20000u) == 0x0Bu, "");
    bus.expect_cfg_hash[0] = 0x1234u;
    check("an unexpected configuration hash is not ready",
          tt_drvbus_not_ready(&bus, 0x01u, 1500u, 20000u) == 0x01u, "");
    bus.expect_cfg_hash[0] = 0xABCDu;
    status_frame(&f, 0u, 0u, 0xFFFFu, 1u, TT_NODE_ESTOP, TT_DRV_ESTOP, 0);
    tt_drvbus_rx(&bus, &f, 1600u, NULL);
    check("a node in ESTOP is not ready", tt_drvbus_not_ready(&bus, 0x01u, 1700u, 20000u) == 0x01u, "");
    f.len = 20u;
    check("a malformed STATUS is counted and dropped", tt_drvbus_rx(&bus, &f, 1800u, &m) == -1 &&
          bus.rx_bad == 1u, "");
}

int main(void)
{
    test_frames();
    test_ids_and_lengths();
    test_bus_load();
    test_drvbus();
    printf(g_fail ? "\n%d FAILED\n" : "\nall passed\n", g_fail);
    return g_fail ? 1 : 0;
}
