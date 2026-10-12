/*
 * test_node.c — a wheel node (node/tt_node.c) on the PMSM plant
 * (plant/pmsm_model.c), driven over the drive bus by MAIN's end
 * (core/tt_drvbus.c). Registered with CTest as `tt_node_plant`.
 *
 * The node is the front-right one with the motor mirrored (dir = -1) and an
 * encoder that counts backwards from a zero near a billion, so the wheel
 * frame, the encoder direction found by the alignment, and the angle's
 * precision at large counts are all exercised.
 *
 * The motor is the "Opus Vector FOC" twin's (1400 Kv, 7 pole pairs,
 * 0.12 ohm, 30 uH) with a quarter of the car's inertia reflected through the
 * 5:1 gear and a viscous load. Timing as on the boards: the current loop
 * every 50 us PWM period; every millisecond MAIN sends a CMD (on the node
 * 72 us later), the node ticks 100 us after MAIN and sends its STATUS, which
 * MAIN receives 300 us after its own tick. The limits, derates and trips
 * checked are the twin's driver model's (MotorPart.StepFoc), same numbers.
 */
#include "tt_node.h"
#include "tt_drvbus.h"
#include "tt_safety.h"
#include "pmsm_model.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#define PI      3.14159265358979323846
#define F_PWM   20000.0
#define TS      (1.0 / F_PWM)
#define NODE    TT_FR

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

/* ---- the rig ----------------------------------------------------------------- */

typedef struct {
    PmsmModel pm;
    TtNode    node;
    TtDrvBus  bus;
    TtMeas    meas;
    uint32_t  now_us;          /* MAIN's clock at its last tick               */
    int       periods;
    /* the world */
    float     vbus, t_fet, t_wind;
    uint8_t   hw_enable, oc, enc_fault, cmd_on;
    int32_t   enc_zero;
    int       enc_sign;        /* how the encoder counts against + rotation   */
    /* MAIN */
    float     iq[4];
    uint8_t   enable, flags;
    TtCanFrame pend[4];        /* MAIN -> node frames for the next tick       */
    int       n_pend;
    uint32_t  n_status;
    float     duty[3];
    int       bridge;
} Rig;

static int32_t enc_raw(const Rig *r)
{
    double c = floor(r->pm.theta_m * (double)r->node.cfg.enc_cpr / (2.0 * PI));
    return r->enc_zero + (int32_t)(r->enc_sign * (int32_t)c);
}

/* An open bridge: the windings carry no current (back-EMF below the bus),
 * so apply exactly the back-EMF and the plant coasts. */
static void coast(Rig *r)
{
    float th = (float)fmod(pmsm_theta_e(&r->pm), 2.0 * PI);
    float vq = (float)(r->pm.omega_m * r->pm.pole_pairs * r->pm.flux_wb);
    float va, vb;
    foc_inv_park(0.0f, vq, sinf(th), cosf(th), &va, &vb);
    foc_svpwm(va, vb, r->vbus, r->duty);
    r->pm.id = r->pm.iq = 0.0;
}

static void tick(Rig *r)
{
    TtCanFrame f;
    TtNodeMeas m;
    int i;

    r->now_us += 1000u;
    if (r->cmd_on) {
        tt_drvbus_cmd(&r->bus, r->iq, r->enable, r->flags, r->now_us, &f);
        r->flags = 0u;
        tt_node_rx(&r->node, &f, r->now_us + 72u);
    }
    for (i = 0; i < r->n_pend; i++) tt_node_rx(&r->node, &r->pend[i], r->now_us + 80u);
    r->n_pend = 0;

    memset(&m, 0, sizeof m);
    m.now_us = r->now_us + 100u;
    m.vbus_v = r->vbus;
    m.t_fet_c = r->t_fet;
    m.t_wind_c = r->t_wind;
    m.enc_raw = enc_raw(r);
    m.hw_enable = r->hw_enable;
    m.oc_trip = r->oc;
    m.enc_fault = r->enc_fault;
    r->oc = 0u;
    tt_node_tick(&r->node, &m);

    while (tt_node_tx(&r->node, &f)) {
        if (tt_drvbus_rx(&r->bus, &f, r->now_us + 300u, &r->meas) == TT_CAN_K_STATUS) r->n_status++;
    }
}

static void period(Rig *r)
{
    float i_abc[3];
    r->pm.vbus_v = r->vbus;
    pmsm_phase_currents(&r->pm, i_abc);
    r->bridge = tt_node_isr(&r->node, i_abc, r->vbus, enc_raw(r), r->duty);
    if (!r->bridge) coast(r);
    pmsm_step(&r->pm, r->duty, TS);
    if (++r->periods % 20 == 0) tick(r);
}

static void run(Rig *r, double s)
{
    long n = (long)(s * F_PWM + 0.5), i;
    for (i = 0; i < n; i++) period(r);
}

static void send(Rig *r, const TtCanFrame *f)
{
    if (r->n_pend < 4) r->pend[r->n_pend++] = *f;
}

static void cfg(Rig *r, uint8_t op, uint16_t key, uint32_t value, uint8_t tag)
{
    TtCanCfg q;
    TtCanFrame f;
    q.op = op; q.tag = tag; q.key = key; q.value = value;
    tt_can_pack_cfg(&f, TT_CAN_ID_CFG_REQ, NODE, &q);
    r->bus.node[NODE].cfg_new = 0u;
    send(r, &f);
}

static uint32_t fbits(float x) { uint32_t u; memcpy(&u, &x, 4); return u; }
static float bitsf(uint32_t u) { float x; memcpy(&x, &u, 4); return x; }

static void rig_init(Rig *r, const TtNodeCfg *c, int locked)
{
    double kt = 0.006820926;
    memset(r, 0, sizeof *r);
    r->pm.r_ohm = 0.12;
    r->pm.ld_h = r->pm.lq_h = 30e-6;
    r->pm.pole_pairs = 7;
    r->pm.flux_wb = kt / (1.5 * 7.0);
    /* rotor 2e-6 + a quarter of 2.3 kg at r = 33 mm through 5:1 */
    r->pm.j_kgm2 = 2e-6 + 2.315 * 0.033 * 0.033 / (4.0 * 25.0);
    /* Damped enough to settle on the alignment field (zeta ~0.4, as a
     * geared drivetrain's friction would); the drive tests then drop it to
     * a light viscous load. */
    r->pm.b_nms = 2e-3;
    r->pm.vbus_v = 7.4;
    r->pm.f_pwm_hz = F_PWM;
    r->pm.dead_time_s = 0.0;
    pmsm_init(&r->pm);
    r->pm.theta_m = 0.37;
    if (locked) { r->pm.fixed_speed = 1; r->pm.omega_m = 0.0; }

    r->vbus = 7.4f;
    r->t_fet = 40.0f;
    r->t_wind = nanf("");
    r->hw_enable = 1u;
    r->cmd_on = 1u;
    r->enc_zero = 1000000123;
    r->enc_sign = -1;
    tt_drvbus_init(&r->bus, tt_can_frame_us(TT_CAN_LEN_CMD, 1, 1e6f, 5e6f));
    tt_node_init(&r->node, c, NODE, 0x0C0FFEE0u, 1u, 0u);
}

static TtNodeCfg node_cfg(void)
{
    TtNodeCfg c;
    tt_node_cfg_default(&c);
    c.dir = -1;                /* the right-hand motor is mirrored */
    return c;
}

static int saved_ok;
static TtNodeCfg saved;
static int save_hook(const TtNodeCfg *c, void *user)
{
    (void)user;
    saved = *c;
    saved_ok = 1;
    return 0;
}

#define DRV   (r->meas.drv[NODE])
#define CFGR  (r->bus.node[NODE].cfg_rsp)
#define BMASK (1u << NODE)

/* ---- the scenario ------------------------------------------------------------ */

static void test_main_path(void)
{
    static Rig rig;
    Rig *r = &rig;
    TtNodeCfg c = node_cfg();
    TtCanFrame f;
    uint32_t hash0, nb0;
    int32_t e0, e1;
    double th0, th1, err;
    float off;

    rig_init(r, &c, 0);

    /* Boot. */
    run(r, 0.002);
    check("BOOT reaches MAIN: protocol, node, not calibrated",
          r->bus.node[NODE].booted && r->bus.node[NODE].boot.proto == TT_CAN_PROTO_VERSION &&
          r->bus.node[NODE].boot.node == NODE && !r->bus.node[NODE].boot.cal_valid, "");
    check("an uncalibrated node is IDLE, reports NOT_CAL, blocks arming",
          r->bus.node[NODE].status.state == TT_NODE_IDLE && (DRV.fault & TT_DRV_NOT_CAL) &&
          tt_drvbus_not_ready(&r->bus, BMASK, r->now_us, 20000u) == BMASK, "fault 0x%X", DRV.fault);

    /* Alignment. */
    cfg(r, TT_CFG_ALIGN, 0, 0, 42u);
    run(r, 0.5);
    check("alignment runs in its own state", r->node.state == TT_NODE_ALIGN && r->bridge, "");
    run(r, 1.3);
    off = bitsf(CFGR.value);
    check("alignment replies OK with the offset", r->bus.node[NODE].cfg_new && CFGR.op == TT_CFG_ALIGN &&
          CFGR.tag == 42u && r->node.cfg.cal_valid == 1u, "op 0x%02X offset %.3f rad", CFGR.op, (double)off);
    check("alignment finds the encoder counts backwards", r->node.cfg.enc_dir == -1, "%d",
          (int)r->node.cfg.enc_dir);
    {
        /* The node's electrical angle against the plant's, a count near 1e9. */
        float th_node = foc_mech_to_elec(r->node.cfg.enc_dir *
                        (float)(((enc_raw(r) % 4096) + 4096) % 4096) * (float)(2.0 * PI / 4096.0),
                        7u, r->node.cfg.enc_offset_e);
        err = fmod(pmsm_theta_e(&r->pm) - th_node + 3.0 * PI, 2.0 * PI) - PI;
        check("electrical angle matches the rotor after alignment", fabs(err) < 0.03,
              "%.4f rad (one count = %.4f)", err, 2.0 * PI * 7.0 / 4096.0);
    }
    run(r, 0.002);
    check("after alignment the node is ready to arm",
          tt_drvbus_not_ready(&r->bus, BMASK, r->now_us, 20000u) == 0u &&
          r->bus.node[NODE].boot.cal_valid, "boots %u", (unsigned)r->bus.node[NODE].n_boot);
    r->pm.b_nms = 1.1e-4;

    /* Torque in the wheel frame. */
    r->enable = BMASK;
    r->iq[NODE] = 5.0f;
    run(r, 0.020);
    check("enabled: RUN, the bridge on", r->node.state == TT_NODE_RUN && r->bridge, "");
    check("+5 A forward is -5 A in the mirrored motor", fabs(r->pm.iq + 5.0) < 0.15 && fabs(r->pm.id) < 0.15,
          "plant iq %.3f id %.3f", r->pm.iq, r->pm.id);
    check("MAIN reads +5 A in the wheel frame", fabsf(DRV.iq_a - 5.0f) < 0.15f, "%.3f", (double)DRV.iq_a);
    e0 = r->meas.enc[NODE].count;
    th0 = r->pm.theta_m;
    run(r, 0.200);
    e1 = r->meas.enc[NODE].count;
    th1 = r->pm.theta_m;
    check("the motor turns backwards, the wheel forwards", r->pm.omega_m < -50.0 && DRV.omega_m > 50.0f &&
          fabs(DRV.omega_m + r->pm.omega_m) < 0.02 * fabs(r->pm.omega_m) + 1.0,
          "plant %.1f, MAIN %.1f rad/s", r->pm.omega_m, (double)DRV.omega_m);
    check("MAIN's count rises with the wheel, count for count",
          e1 > e0 && fabs((double)(e1 - e0) + (th1 - th0) * 4096.0 / (2.0 * PI)) < 2.5,
          "%d counts for %.1f", (int)(e1 - e0), -(th1 - th0) * 4096.0 / (2.0 * PI));
    check("STATUS stamp = the node's sample time on MAIN's clock",
          DRV.st.t_us == r->now_us + 100u, "%u vs %u", (unsigned)DRV.st.t_us, (unsigned)(r->now_us + 100u));

    /* Current limits. */
    r->iq[NODE] = 30.0f;
    run(r, 0.005);
    check("motoring limited to 15 A, flagged", fabs(fabs(r->pm.iq) - 15.0) < 0.3 && (DRV.fault & TT_DRV_ILIMIT),
          "%.2f A, fault 0x%X", r->pm.iq, DRV.fault);
    r->iq[NODE] = -30.0f;
    run(r, 0.005);
    check("regen limited to 12 A", fabs(fabs(r->pm.iq) - 12.0) < 0.3 && DRV.omega_m > 0.0f,
          "%.2f A at %.0f rad/s", r->pm.iq, (double)DRV.omega_m);

    /* Regen fades near the over-voltage trip: 12 A x (8.7 - 8.6) / 0.2. */
    r->vbus = 8.6f;
    run(r, 0.030);
    check("regen derated to 6 A at 8.6 V", fabs(fabs(r->pm.iq) - 6.0) < 0.3 && DRV.omega_m > 0.0f,
          "%.2f A", r->pm.iq);

    /* Over-voltage trip with hysteresis. */
    r->iq[NODE] = 5.0f;
    r->vbus = 8.75f;
    run(r, 0.003);
    check("over-voltage: bridge off, flagged", !r->bridge && fabs(r->pm.iq) < 0.01 &&
          (DRV.fault & TT_DRV_OVERVOLT) && r->node.state == TT_NODE_RUN, "fault 0x%X", DRV.fault);
    r->vbus = 8.6f;
    run(r, 0.005);
    check("still off between derate and trip (hysteresis)", !r->bridge, "");
    r->vbus = 8.4f;
    run(r, 0.005);
    check("back below 8.5 V: drives again", r->bridge && fabs(r->pm.iq + 5.0) < 0.2 &&
          !(DRV.fault & TT_DRV_OVERVOLT), "%.2f A", r->pm.iq);

    /* Thermal derate: 15 A x (130 - 115) / 30. */
    r->vbus = 7.4f;
    r->t_wind = 115.0f;
    r->iq[NODE] = 30.0f;
    run(r, 0.005);
    check("winding at 115 C: 7.5 A, flagged", fabs(fabs(r->pm.iq) - 7.5) < 0.3 && (DRV.fault & TT_DRV_OVERTEMP),
          "%.2f A", r->pm.iq);
    check("MAIN's temperature is the winding's", fabsf(DRV.temp_c - 115.0f) < 0.1f, "%.1f", (double)DRV.temp_c);
    r->t_wind = 131.0f;
    run(r, 0.005);
    check("winding at 131 C: no current", fabs(r->pm.iq) < 0.2, "%.2f A", r->pm.iq);
    r->t_wind = nanf("");
    r->iq[NODE] = 5.0f;
    run(r, 0.005);
    check("no winding sensor: FET temperature, no derate", fabs(r->pm.iq + 5.0) < 0.2 &&
          fabsf(DRV.temp_c - 40.0f) < 0.1f && !(DRV.fault & TT_DRV_OVERTEMP), "");

    /* Command watchdog. */
    r->cmd_on = 0u;
    run(r, 0.049);
    check("49 ms without a command: still driving", r->bridge, "");
    run(r, 0.002);
    check("50 ms: bridge off, CMD_TIMEOUT", !r->bridge && (DRV.fault & TT_DRV_CMD_TIMEOUT) &&
          r->node.iq_cmd_a == 0.0f, "fault 0x%X", DRV.fault);
    r->cmd_on = 1u;
    run(r, 0.003);
    check("the next command is obeyed", r->bridge && fabs(r->pm.iq + 5.0) < 0.2 &&
          !(DRV.fault & TT_DRV_CMD_TIMEOUT), "%.2f A", r->pm.iq);

    /* ESTOP: off at once, latched, cleared only by a CLEAR that does not
     * also enable. */
    tt_drvbus_estop(TT_SF_KILL, &f);
    tt_node_rx(&r->node, &f, r->now_us + 500u);
    check("ESTOP: bridge off on reception, before the tick", !r->node.bridge_on, "");
    run(r, 0.005);
    check("ESTOP latches", r->node.state == TT_NODE_ESTOP && !r->bridge && (DRV.fault & TT_DRV_ESTOP) &&
          r->node.estop_reason == TT_SF_KILL, "");
    r->flags = TT_CAN_CMD_CLEAR;
    run(r, 0.003);
    check("CLEAR with the enable bit set is ignored", r->node.state == TT_NODE_ESTOP, "");
    r->enable = 0u;
    r->flags = TT_CAN_CMD_CLEAR;
    run(r, 0.003);
    check("CLEAR with the enable bit clear: IDLE", r->node.state == TT_NODE_IDLE && !r->bridge, "");
    r->enable = BMASK;
    run(r, 0.003);
    check("then enable: RUN", r->node.state == TT_NODE_RUN && r->bridge, "");

    /* Over-current comparator: latched until cleared. */
    r->oc = 1u;
    run(r, 0.005);
    check("over-current trip latches FAULT", r->node.state == TT_NODE_FAULT && !r->bridge &&
          (DRV.fault & TT_DRV_OVERCURRENT), "");
    r->enable = 0u;
    r->flags = TT_CAN_CMD_CLEAR;
    run(r, 0.002);
    r->enable = BMASK;
    run(r, 0.003);
    check("cleared and re-enabled", r->node.state == TT_NODE_RUN && r->bridge, "");

    /* Hardware enable (DRV_EN) low. */
    r->hw_enable = 0u;
    run(r, 0.003);
    check("DRV_EN low: bridge off, HW_OFF", !r->bridge && (DRV.fault & TT_DRV_HW_OFF), "");
    r->hw_enable = 1u;
    run(r, 0.003);
    check("DRV_EN high: drives again", r->bridge, "");

    /* Under-voltage with hysteresis. */
    r->vbus = 5.3f;
    run(r, 0.003);
    check("5.3 V: bridge off, UNDERVOLT", !r->bridge && (DRV.fault & TT_DRV_UNDERVOLT), "");
    r->vbus = 5.7f;
    run(r, 0.003);
    check("5.7 V: still off (hysteresis)", !r->bridge, "");
    r->vbus = 6.0f;
    run(r, 0.003);
    check("6.0 V: drives again", r->bridge, "");
    r->vbus = 7.4f;

    /* Configuration over the bus. */
    cfg(r, TT_CFG_GET, 3u, 0u, 1u);
    run(r, 0.002);
    check("GET pole_pairs", CFGR.op == TT_CFG_GET && CFGR.value == 7u, "op 0x%02X value %u", CFGR.op,
          (unsigned)CFGR.value);
    cfg(r, TT_CFG_SET, 17u, fbits(10.0f), 2u);
    run(r, 0.002);
    check("SET while driving is refused", CFGR.op == (TT_CFG_SET | TT_CFG_ERR) && CFGR.value == TT_CFGE_BUSY,
          "op 0x%02X", CFGR.op);
    r->enable = 0u;
    run(r, 0.002);
    hash0 = r->bus.node[NODE].boot.cfg_hash;
    nb0 = r->bus.node[NODE].n_boot;
    cfg(r, TT_CFG_SET, 17u, fbits(10.0f), 3u);
    run(r, 0.002);
    check("SET i_drive_max = 10 A while idle; BOOT with the new hash",
          CFGR.op == TT_CFG_SET && CFGR.tag == 3u && r->node.cfg.i_drive_max_a == 10.0f &&
          r->bus.node[NODE].n_boot == nb0 + 1u && r->bus.node[NODE].boot.cfg_hash != hash0 &&
          r->bus.node[NODE].boot.cfg_hash == tt_node_cfg_hash(&r->node.cfg), "");
    cfg(r, TT_CFG_SET, 4u, fbits(100.0f), 4u);
    run(r, 0.002);
    check("SET out of range is refused, nothing changes",
          CFGR.op == (TT_CFG_SET | TT_CFG_ERR) && CFGR.value == TT_CFGE_RANGE && r->node.cfg.r_ohm == 0.12f, "");
    cfg(r, TT_CFG_SET, 0u, 2u, 5u);
    run(r, 0.002);
    check("the version is read-only", CFGR.value == TT_CFGE_READONLY, "");
    cfg(r, TT_CFG_GET, 99u, 0u, 6u);
    run(r, 0.002);
    check("an unknown key", CFGR.op == (TT_CFG_GET | TT_CFG_ERR) && CFGR.value == TT_CFGE_KEY, "");
    cfg(r, 99u, 0u, 0u, 7u);
    run(r, 0.002);
    check("an unknown op", CFGR.value == TT_CFGE_OP, "");
    cfg(r, TT_CFG_SAVE, 0u, 0u, 8u);
    run(r, 0.002);
    check("SAVE with no flash hook fails", CFGR.op == (TT_CFG_SAVE | TT_CFG_ERR) && CFGR.value == TT_CFGE_FLASH, "");
    r->node.save = save_hook;
    cfg(r, TT_CFG_SAVE, 0u, 0u, 9u);
    run(r, 0.002);
    check("SAVE hands the configuration to flash", CFGR.op == TT_CFG_SAVE && saved_ok &&
          memcmp(&saved, &r->node.cfg, sizeof saved) == 0, "");
    r->enable = BMASK;
    r->iq[NODE] = 30.0f;
    run(r, 0.005);
    check("the new limit applies", fabs(fabs(r->pm.iq) - 10.0) < 0.3, "%.2f A", r->pm.iq);

    /* STATUS rate. */
    r->enable = 0u;
    run(r, 0.002);
    cfg(r, TT_CFG_SET, 27u, 4u, 10u);
    run(r, 0.002);
    r->n_status = 0u;
    run(r, 0.040);
    check("status_div 4: a STATUS every 4 ms", r->n_status == 10u, "%u in 40 ms", (unsigned)r->n_status);
    check("nothing dropped from the transmit queue", r->node.tx_drops == 0u && r->node.rx_bad == 0u, "");
}

static void test_align_failures(void)
{
    static Rig rig;
    Rig *r = &rig;
    TtNodeCfg c = node_cfg();

    rig_init(r, &c, 1);
    cfg(r, TT_CFG_ALIGN, 0, 0, 1u);
    run(r, 1.7);
    check("a locked rotor fails the alignment", CFGR.op == (TT_CFG_ALIGN | TT_CFG_ERR) &&
          CFGR.value == TT_CFGE_ALIGN && !r->node.cfg.cal_valid, "op 0x%02X", CFGR.op);

    rig_init(r, &c, 0);
    r->pm.b_nms = 1.1e-4;      /* zeta 0.02: rings for seconds */
    cfg(r, TT_CFG_ALIGN, 0, 0, 5u);
    run(r, 1.2);
    check("a rotor still ringing fails rather than calibrate on the move",
          r->bus.node[NODE].cfg_new && CFGR.value == TT_CFGE_ALIGN && !r->node.cfg.cal_valid &&
          r->node.state == TT_NODE_IDLE, "op 0x%02X", CFGR.op);

    c.pole_pairs = 6u;
    rig_init(r, &c, 0);
    cfg(r, TT_CFG_ALIGN, 0, 0, 2u);
    run(r, 1.7);
    check("6 pole pairs configured on a 7-pair motor fails", CFGR.value == TT_CFGE_ALIGN &&
          !r->node.cfg.cal_valid, "op 0x%02X", CFGR.op);
    c.pole_pairs = 8u;
    rig_init(r, &c, 0);
    cfg(r, TT_CFG_ALIGN, 0, 0, 3u);
    run(r, 1.7);
    check("8 pole pairs fails", CFGR.value == TT_CFGE_ALIGN && !r->node.cfg.cal_valid, "");

    c = node_cfg();
    rig_init(r, &c, 0);
    cfg(r, TT_CFG_ALIGN, 0, 0, 4u);
    run(r, 0.4);
    r->hw_enable = 0u;
    run(r, 0.003);
    check("DRV_EN dropping aborts the alignment", CFGR.op == (TT_CFG_ALIGN | TT_CFG_ERR) &&
          CFGR.value == TT_CFGE_BUSY && r->node.state == TT_NODE_IDLE && !r->bridge, "");

    c = node_cfg();
    c.ov_derate_v = 9.0f;      /* above the trip: unusable */
    rig_init(r, &c, 0);
    run(r, 0.003);
    check("an unusable configuration: INIT, CONFIG, never drives",
          r->node.state == TT_NODE_INIT && (r->meas.drv[NODE].fault & TT_DRV_CONFIG) && !r->bridge, "");
    cfg(r, TT_CFG_SET, 21u, fbits(8.5f), 1u);
    run(r, 0.003);
    check("fixed over the bus: IDLE", r->node.state == TT_NODE_IDLE &&
          !(r->meas.drv[NODE].fault & TT_DRV_CONFIG), "");
}

static void test_config_defaults(void)
{
    TtNodeCfg c;
    tt_node_cfg_default(&c);
    check("the default configuration is valid", tt_node_cfg_validate(&c) == 0, "%d", tt_node_cfg_validate(&c));
    check("defaults are the twin's driver: 15/12 A, 8.5/8.7 V, 100/130 C, 50 ms",
          c.i_drive_max_a == 15.0f && c.i_regen_max_a == 12.0f && c.ov_derate_v == 8.5f && c.ov_trip_v == 8.7f &&
          c.temp_derate_c == 100.0f && c.temp_limit_c == 130.0f && c.cmd_timeout_s == 0.050f &&
          c.enc_cpr == 4096u && c.pole_pairs == 7u, "");
}

int main(void)
{
    test_config_defaults();
    test_main_path();
    test_align_failures();
    printf(g_fail ? "\n%d FAILED\n" : "\nall passed\n", g_fail);
    return g_fail ? 1 : 0;
}
