/*
 * tt_node.c — the wheel-motor node's firmware. See tt_node.h.
 */
#include "tt_node.h"
#include "tt_safety.h"     /* TT_DRV_* fault bits */

#include <math.h>
#include <string.h>

#define NODE_PI   3.14159265f
#define NODE_2PI  6.28318531f

/* Every field is 32 bits, so a key is a word index. */
typedef char tt_node_cfg_is_words[(sizeof(TtNodeCfg) == 4u * TT_NCFG_COUNT) ? 1 : -1];

/* ---------------------------------------------------------- the config --- */

void tt_node_cfg_default(TtNodeCfg *c)
{
    /* The "Opus Vector FOC" twin's motor and driver (VehiclePresets.cs,
     * OpusVectorFoc): 1400 Kv 12N14P, 0.12 ohm, 30 uH, 15 A / 12 A,
     * 2 kHz current loop, 8.5/8.7 V, 100/130 degC, 50 ms watchdog. */
    memset(c, 0, sizeof *c);
    c->version = TT_NODE_CFG_VERSION;
    c->dir = 1;
    c->enc_dir = 1;
    c->pole_pairs = 7u;
    c->r_ohm = 0.12f;
    c->ld_h = 30e-6f;
    c->lq_h = 30e-6f;
    c->flux_wb = 0.006820926f / (1.5f * 7.0f);   /* kt = 60 / (2 pi 1400) */
    c->f_pwm_hz = 20000.0f;
    c->bw_current_hz = 2000.0f;
    c->bw_pll_hz = 200.0f;
    c->mod_max = 0.95f;
    c->delay_periods = 1.0f;
    c->ff_enable = 1u;
    c->enc_cpr = 4096u;
    c->enc_offset_e = 0.0f;
    c->cal_valid = 0u;
    c->i_drive_max_a = 15.0f;
    c->i_regen_max_a = 12.0f;
    c->i_trip_a = 25.0f;
    c->ov_trip_v = 8.7f;
    c->ov_derate_v = 8.5f;
    c->uv_off_v = 5.5f;
    c->uv_on_v = 5.8f;
    c->temp_derate_c = 100.0f;
    c->temp_limit_c = 130.0f;
    c->cmd_timeout_s = 0.050f;
    c->status_div = 1u;
    c->align_i_a = 5.0f;
    c->align_s = 1.5f;
    c->vbus_filter_s = 0.005f;
}

enum { KT_U32 = 0, KT_I32 = 1, KT_F32 = 2 };
typedef struct { uint8_t type, ro, nonzero, pad_; float lo, hi; } KeyDef;

static const KeyDef KEYS[TT_NCFG_COUNT] = {
    { KT_U32, 1, 0, 0, 0.0f, 0.0f },          /*  0 version                 */
    { KT_I32, 0, 1, 0, -1.0f, 1.0f },         /*  1 dir                     */
    { KT_I32, 0, 1, 0, -1.0f, 1.0f },         /*  2 enc_dir                 */
    { KT_U32, 0, 0, 0, 1.0f, 30.0f },         /*  3 pole_pairs              */
    { KT_F32, 0, 0, 0, 1e-3f, 10.0f },        /*  4 r_ohm                   */
    { KT_F32, 0, 0, 0, 1e-7f, 1e-2f },        /*  5 ld_h                    */
    { KT_F32, 0, 0, 0, 1e-7f, 1e-2f },        /*  6 lq_h                    */
    { KT_F32, 0, 0, 0, 1e-5f, 0.1f },         /*  7 flux_wb                 */
    { KT_F32, 0, 0, 0, 5000.0f, 100000.0f },  /*  8 f_pwm_hz                */
    { KT_F32, 0, 0, 0, 50.0f, 10000.0f },     /*  9 bw_current_hz           */
    { KT_F32, 0, 0, 0, 10.0f, 2000.0f },      /* 10 bw_pll_hz               */
    { KT_F32, 0, 0, 0, 0.5f, 1.0f },          /* 11 mod_max                 */
    { KT_F32, 0, 0, 0, 0.0f, 3.0f },          /* 12 delay_periods           */
    { KT_U32, 0, 0, 0, 0.0f, 1.0f },          /* 13 ff_enable               */
    { KT_U32, 0, 0, 0, 16.0f, 65535.0f },     /* 14 enc_cpr                 */
    { KT_F32, 0, 0, 0, -NODE_PI, NODE_PI },   /* 15 enc_offset_e            */
    { KT_U32, 0, 0, 0, 0.0f, 1.0f },          /* 16 cal_valid               */
    { KT_F32, 0, 0, 0, 0.0f, 60.0f },         /* 17 i_drive_max_a           */
    { KT_F32, 0, 0, 0, 0.0f, 60.0f },         /* 18 i_regen_max_a           */
    { KT_F32, 0, 0, 0, 1.0f, 100.0f },        /* 19 i_trip_a                */
    { KT_F32, 0, 0, 0, 1.0f, 60.0f },         /* 20 ov_trip_v               */
    { KT_F32, 0, 0, 0, 1.0f, 60.0f },         /* 21 ov_derate_v             */
    { KT_F32, 0, 0, 0, 0.0f, 60.0f },         /* 22 uv_off_v                */
    { KT_F32, 0, 0, 0, 0.0f, 60.0f },         /* 23 uv_on_v                 */
    { KT_F32, 0, 0, 0, 0.0f, 200.0f },        /* 24 temp_derate_c           */
    { KT_F32, 0, 0, 0, 1.0f, 200.0f },        /* 25 temp_limit_c            */
    { KT_F32, 0, 0, 0, 0.002f, 1.0f },        /* 26 cmd_timeout_s           */
    { KT_U32, 0, 0, 0, 1.0f, 100.0f },        /* 27 status_div              */
    { KT_F32, 0, 0, 0, 0.1f, 30.0f },         /* 28 align_i_a               */
    { KT_F32, 0, 0, 0, 0.1f, 10.0f },         /* 29 align_s                 */
    { KT_F32, 0, 0, 0, 1e-4f, 1.0f },         /* 30 vbus_filter_s           */
};

static uint32_t word(const TtNodeCfg *c, uint16_t key)
{
    uint32_t v;
    memcpy(&v, (const uint8_t *)c + 4u * key, 4);
    return v;
}

/* The field's own range. */
static int key_ok(uint16_t key, uint32_t v)
{
    const KeyDef *k = &KEYS[key];
    float x;
    if (k->type == KT_F32) {
        memcpy(&x, &v, 4);
        if (x != x) return 0;
    } else if (k->type == KT_I32) {
        int32_t i = (int32_t)v;
        if (k->nonzero && i == 0) return 0;
        x = (float)i;
    } else {
        x = (float)v;
    }
    return x >= k->lo && x <= k->hi;
}

int tt_node_cfg_validate(const TtNodeCfg *c)
{
    int bad = 0;
    uint16_t key;
    if (c->version != TT_NODE_CFG_VERSION) bad++;
    for (key = 1; key < TT_NCFG_COUNT; key++)
        if (!key_ok(key, word(c, key))) bad++;
    if (!(c->ov_derate_v < c->ov_trip_v)) bad++;
    if (!(c->uv_off_v < c->uv_on_v && c->uv_on_v < c->ov_derate_v)) bad++;
    if (!(c->temp_derate_c < c->temp_limit_c)) bad++;
    if (!(c->bw_current_hz * 5.0f <= c->f_pwm_hz)) bad++;
    if (!(c->i_drive_max_a < c->i_trip_a && c->i_regen_max_a < c->i_trip_a &&
          c->align_i_a < c->i_trip_a)) bad++;
    return bad;
}

uint32_t tt_node_cfg_hash(const TtNodeCfg *c)
{
    /* CRC-32/IEEE, as tt_params_hash. */
    const uint8_t *b = (const uint8_t *)c;
    uint32_t crc = 0xFFFFFFFFu;
    size_t i;
    int k;
    for (i = 0; i < sizeof *c; i++) {
        crc ^= b[i];
        for (k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
    }
    return ~crc;
}

int tt_node_cfg_get(const TtNodeCfg *c, uint16_t key, uint32_t *value)
{
    if (key >= TT_NCFG_COUNT) return TT_CFGE_KEY;
    *value = word(c, key);
    return 0;
}

int tt_node_cfg_set(TtNodeCfg *c, uint16_t key, uint32_t value)
{
    if (key >= TT_NCFG_COUNT) return TT_CFGE_KEY;
    if (KEYS[key].ro) return TT_CFGE_READONLY;
    if (!key_ok(key, value)) return TT_CFGE_RANGE;
    memcpy((uint8_t *)c + 4u * key, &value, 4);
    return 0;
}

/* -------------------------------------------------------------- helpers --- */

static float clamp01(float x) { return x < 0.0f ? 0.0f : (x > 1.0f ? 1.0f : x); }
static int finite_(float x) { return x == x && x - x == 0.0f; }

/* The encoder's mechanical angle, motor frame. Only the count within one
 * turn is used, so a count of millions loses no precision as a float. */
static float enc_angle(const TtNodeCfg *c, int32_t raw)
{
    int32_t cpr = (int32_t)c->enc_cpr, r = raw % cpr;
    if (r < 0) r += cpr;
    return (float)c->enc_dir * (float)r * (NODE_2PI / (float)cpr);
}

static int32_t flip(int32_t v, int32_t sign)
{
    return sign < 0 ? (int32_t)(0u - (uint32_t)v) : v;
}

static void txq_push(TtNode *n, const TtCanFrame *f)
{
    if (n->tx_n >= TT_NODE_TXQ) { n->tx_drops++; return; }
    n->txq[(n->tx_head + n->tx_n) % TT_NODE_TXQ] = *f;
    n->tx_n++;
}

int tt_node_tx(TtNode *n, TtCanFrame *f)
{
    if (n->tx_n == 0u) return 0;
    *f = n->txq[n->tx_head];
    n->tx_head = (uint8_t)((n->tx_head + 1u) % TT_NODE_TXQ);
    n->tx_n--;
    return 1;
}

static void queue_boot(TtNode *n)
{
    TtCanBoot b;
    TtCanFrame f;
    b.proto = TT_CAN_PROTO_VERSION;
    b.node = n->id;
    b.hw_rev = n->hw_rev;
    b.reset_cause = n->reset_cause;
    b.fw_id = n->fw_id;
    b.cfg_hash = tt_node_cfg_hash(&n->cfg);
    b.state = n->state;
    b.cal_valid = (uint8_t)(n->cfg.cal_valid != 0u);
    b.enc_cpr = (uint16_t)n->cfg.enc_cpr;
    tt_can_pack_boot(&f, n->id, &b);
    txq_push(n, &f);
}

static void reply(TtNode *n, uint8_t op, uint8_t tag, uint16_t key, int err, uint32_t value)
{
    TtCanCfg c;
    TtCanFrame f;
    c.op = (uint8_t)(err ? (op | TT_CFG_ERR) : op);
    c.tag = tag;
    c.key = key;
    c.value = err ? (uint32_t)err : value;
    tt_can_pack_cfg(&f, TT_CAN_ID_CFG_RSP, n->id, &c);
    txq_push(n, &f);
}

static void bridge_off(TtNode *n)
{
    n->bridge_on = 0u;
    n->iq_ref_a = 0.0f;
    n->id_ref_a = 0.0f;
    n->force_angle = 0u;
}

static int foc_setup(TtNode *n)
{
    const TtNodeCfg *c = &n->cfg;
    FocParams p;
    float imax = c->i_drive_max_a;
    if (c->i_regen_max_a > imax) imax = c->i_regen_max_a;
    if (c->align_i_a > imax) imax = c->align_i_a;
    p.r_ohm = c->r_ohm;
    p.ld_h = c->ld_h;
    p.lq_h = c->lq_h;
    p.flux_wb = c->flux_wb;
    p.pole_pairs = c->pole_pairs;
    p.f_pwm_hz = c->f_pwm_hz;
    p.bw_current_hz = c->bw_current_hz;
    p.bw_pll_hz = c->bw_pll_hz;
    p.i_max_a = imax;
    p.mod_max = c->mod_max;
    p.delay_periods = c->delay_periods;
    p.ff_enable = c->ff_enable;
    p.enc_offset_e = c->enc_offset_e;
    return foc_init(&n->foc, &p);
}

/* After any change to the configuration (bridge off). */
static void reconfigure(TtNode *n)
{
    n->cfg_ok = tt_node_cfg_validate(&n->cfg) == 0;
    n->foc_ok = n->cfg_ok && foc_setup(n) == 0;
    if (n->state == TT_NODE_INIT || n->state == TT_NODE_IDLE)
        n->state = (uint8_t)((n->cfg_ok && n->foc_ok) ? TT_NODE_IDLE : TT_NODE_INIT);
    queue_boot(n);          /* the configuration hash changed */
}

int tt_node_init(TtNode *n, const TtNodeCfg *cfg, uint8_t node_id, uint32_t fw_id,
                 uint8_t hw_rev, uint8_t reset_cause)
{
    memset(n, 0, sizeof *n);
    n->cfg = *cfg;
    n->id = (uint8_t)(node_id % TT_CAN_NODES);
    n->fw_id = fw_id;
    n->hw_rev = hw_rev;
    n->reset_cause = reset_cause;
    n->state = TT_NODE_INIT;
    n->first_tick = 1u;
    reconfigure(n);
    return (n->cfg_ok && n->foc_ok) ? 0 : -1;
}

/* ------------------------------------------------------------ receiving --- */

static void cfg_request(TtNode *n, const TtCanFrame *f)
{
    TtCanCfg q;
    uint32_t v = 0u;
    int err = 0;
    int off = !(n->state == TT_NODE_RUN || n->state == TT_NODE_ALIGN);

    if (tt_can_get_cfg(f, &q) != 0) { n->rx_bad++; return; }
    switch (q.op) {
    case TT_CFG_GET:
        err = tt_node_cfg_get(&n->cfg, q.key, &v);
        break;
    case TT_CFG_SET:
        if (!off) { err = TT_CFGE_BUSY; break; }
        err = tt_node_cfg_set(&n->cfg, q.key, q.value);
        if (!err) { v = q.value; reconfigure(n); }
        break;
    case TT_CFG_SAVE:
        if (!off) err = TT_CFGE_BUSY;
        else if (!n->save || n->save(&n->cfg, n->save_user) != 0) err = TT_CFGE_FLASH;
        break;
    case TT_CFG_ALIGN:
        if (n->state != TT_NODE_IDLE) { err = TT_CFGE_BUSY; break; }
        n->state = TT_NODE_ALIGN;
        n->align_t = 0.0f;
        n->align_still = 0.0f;
        n->align_phase = 0u;
        n->align_tag = q.tag;
        return;                 /* the reply comes when it ends */
    case TT_CFG_HELLO:
        queue_boot(n);
        break;
    case TT_CFG_DEFAULTS:
        if (!off) { err = TT_CFGE_BUSY; break; }
        tt_node_cfg_default(&n->cfg);
        reconfigure(n);
        break;
    default:
        err = TT_CFGE_OP;
    }
    reply(n, q.op, q.tag, q.key, err, v);
}

void tt_node_rx(TtNode *n, const TtCanFrame *f, tt_us_t rx_us)
{
    uint8_t who;
    int kind = tt_can_kind(f->id, &who);

    if (kind == TT_CAN_K_ESTOP) {
        TtCanEstop e;
        if (tt_can_get_estop(f, &e) != 0) { n->rx_bad++; return; }
        n->estop = 1u;
        n->estop_reason = e.reason;
        bridge_off(n);                       /* now, not at the next tick */
    } else if (kind == TT_CAN_K_CMD) {
        TtCanCmd c;
        if (tt_can_get_cmd(f, &c) != 0) { n->rx_bad++; return; }
        n->have_cmd = 1u;
        n->cmd_seq = c.seq;
        n->cmd_rx_us = rx_us;
        n->enable_bit = (uint8_t)((c.enable >> n->id) & 1u);
        n->iq_cmd_a = c.iq_a[n->id];
        /* A clear never enables in the same frame. */
        if ((c.flags & TT_CAN_CMD_CLEAR) && !n->enable_bit) {
            n->estop = 0u;
            n->latched = 0u;
        }
        if (!n->enable_bit && n->state == TT_NODE_RUN) bridge_off(n);
    } else if (kind == TT_CAN_K_CFG_REQ && who == n->id) {
        cfg_request(n, f);
    }
}

/* ------------------------------------------------------------ alignment --- */
/*
 * Three steps, each a ramp (half of align_s / 3) and a hold, with
 * Id = align_i at a forced electrical angle:
 *   0. ramp Id up at theta_e = 0; the rotor's d-axis settles on phase A;
 *   1. turn the field one electrical revolution forward;
 *   2. turn it back to 0.
 * A step's count is read only once the rotor has been still (within 2
 * counts) for 50 ms and the step has lasted align_s / 3. An unloaded rotor
 * rings on the field like a spring; one that has not settled by twice that
 * fails the alignment rather than giving an offset read on the move.
 * The three counts say which way the encoder counts (1 minus 0), whether one
 * electrical turn is cpr / pole_pairs counts as it should be (a stuck rotor,
 * a dead encoder or a wrong pole-pair count fail here), and, at
 * theta_e = 0, the offset.
 * Returns 1 while running, 0 when it has ended (state set, reply sent).
 */
static void align_end(TtNode *n, int err, float off)
{
    uint32_t bits;
    bridge_off(n);
    n->state = TT_NODE_IDLE;
    memcpy(&bits, &off, 4);
    reply(n, TT_CFG_ALIGN, n->align_tag, 0u, err, err ? 0u : bits);
}

static int align_step(TtNode *n, const TtNodeMeas *m, float dt)
{
    TtNodeCfg *c = &n->cfg;
    float third = c->align_s / 3.0f, a;
    float th = 0.0f, id = c->align_i_a;
    int32_t moved = m->enc_raw - n->align_ref;

    if (moved > 2 || moved < -2) {
        n->align_ref = m->enc_raw;
        n->align_still = 0.0f;
    } else {
        n->align_still += dt;
    }

    n->align_t += dt;
    a = clamp01(n->align_t / (0.5f * third));
    if (n->align_phase == 0u) id = c->align_i_a * a;
    else if (n->align_phase == 1u) th = NODE_2PI * a;
    else th = NODE_2PI * (1.0f - a);

    if (n->align_t >= third && n->align_still >= 0.05f) {
        n->align_c[n->align_phase] = m->enc_raw;
        if (n->align_phase < 2u) {
            n->align_phase++;
            n->align_t = 0.0f;
        } else {
            float turn = (float)c->enc_cpr / (float)c->pole_pairs;
            float d = (float)(n->align_c[1] - n->align_c[0]);
            float back = (float)(n->align_c[2] - n->align_c[0]);
            float off;
            if (fabsf(fabsf(d) - turn) > 0.1f * turn || fabsf(back) > 0.05f * turn) {
                align_end(n, TT_CFGE_ALIGN, 0.0f);
                return 0;
            }
            c->enc_dir = d > 0.0f ? 1 : -1;
            off = foc_align_offset(enc_angle(c, n->align_c[2]), c->pole_pairs, 0.0f);
            c->enc_offset_e = off;
            c->cal_valid = 1u;
            align_end(n, 0, off);
            reconfigure(n);       /* new offset in the FOC, new hash in a BOOT */
            return 0;
        }
    } else if (n->align_t >= 2.0f * third) {
        align_end(n, TT_CFGE_ALIGN, 0.0f);     /* never settled */
        return 0;
    }
    n->theta_force = th;
    n->id_ref_a = id;
    n->iq_ref_a = 0.0f;
    n->force_angle = 1u;
    n->bridge_on = 1u;
    return 1;
}

/* ----------------------------------------------------------- supervisor --- */

static void queue_status(TtNode *n, const TtNodeMeas *m)
{
    TtCanStatus s;
    TtCanFrame f;
    FocOut last = n->last;
    float dir = (float)n->cfg.dir;
    s.cmd_seq = n->cmd_seq;
    if (!n->have_cmd) s.age_us = 0xFFFFu;
    else {
        uint32_t age = (uint32_t)(m->now_us - n->cmd_rx_us);
        s.age_us = (uint16_t)(age > 0xFFFEu ? 0xFFFEu : age);
    }
    s.seq = n->status_seq++;
    s.state = n->state;
    s.fault = n->fault;
    s.iq_a = dir * last.iq;
    s.id_a = last.id;
    s.enc_count = flip(flip(m->enc_raw, n->cfg.enc_dir), n->cfg.dir);
    s.omega_m = dir * last.omega_m;
    s.vbus_v = m->vbus_v;
    s.t_fet_c = m->t_fet_c;
    s.t_wind_c = m->t_wind_c;
    tt_can_pack_status(&f, n->id, &s);
    txq_push(n, &f);
}

void tt_node_tick(TtNode *n, const TtNodeMeas *m)
{
    const TtNodeCfg *c = &n->cfg;
    uint16_t fault = 0u;
    float dt, tau, temp, i_mot, i_reg, iq_m;
    int timeout, aligning = n->state == TT_NODE_ALIGN;

    dt = n->first_tick ? 0.001f : (float)(uint32_t)(m->now_us - n->last_tick_us) * 1e-6f;
    if (dt > 0.1f) dt = 0.1f;
    n->first_tick = 0u;
    n->last_tick_us = m->now_us;
    n->ticks++;

    /* The bus: a filtered value for the derate, the instantaneous one for
     * the trips (as the twin's driver does). */
    tau = c->vbus_filter_s > 1e-4f ? c->vbus_filter_s : 1e-4f;
    if (n->vbus_filt <= 0.0f) n->vbus_filt = m->vbus_v;
    else n->vbus_filt += (m->vbus_v - n->vbus_filt) * (1.0f - expf(-dt / tau));
    if (m->vbus_v >= c->ov_trip_v) n->ov = 1u;
    else if (m->vbus_v < c->ov_derate_v) n->ov = 0u;
    if (m->vbus_v < c->uv_off_v) n->uv = 1u;
    else if (m->vbus_v > c->uv_on_v) n->uv = 0u;

    /* Trips that latch until a CMD clears them. */
    if (m->oc_trip) n->latched |= TT_DRV_OVERCURRENT;
    if (m->enc_fault) n->latched |= TT_DRV_SENSOR;

    /* Command watchdog: the bridge goes off and the setpoint is forgotten,
     * so the first frame after a gap is obeyed rather than the last before. */
    timeout = n->have_cmd &&
              (float)(uint32_t)(m->now_us - n->cmd_rx_us) * 1e-6f > c->cmd_timeout_s;
    if (timeout) {
        n->iq_cmd_a = 0.0f;
        n->enable_bit = 0u;
    }

    /* State. */
    if (n->estop) n->state = TT_NODE_ESTOP;
    else if (n->latched) n->state = TT_NODE_FAULT;
    else if (!n->cfg_ok || !n->foc_ok) n->state = TT_NODE_INIT;
    else if (aligning) {
        if (n->ov || n->uv || !m->hw_enable) n->state = TT_NODE_IDLE;
        else if (!align_step(n, m, dt)) aligning = 0;
    } else if (n->enable_bit && c->cal_valid) n->state = TT_NODE_RUN;
    else n->state = TT_NODE_IDLE;
    if (aligning && n->state != TT_NODE_ALIGN) {
        bridge_off(n);
        reply(n, TT_CFG_ALIGN, n->align_tag, 0u, TT_CFGE_BUSY, 0u);
    }

    /* Limits: motoring and regen separately, both derated by temperature;
     * regen also fades as the bus nears the over-voltage trip. */
    i_mot = c->i_drive_max_a;
    i_reg = c->i_regen_max_a;
    temp = finite_(m->t_wind_c) ? m->t_wind_c : m->t_fet_c;
    if (finite_(temp)) {
        float s = clamp01((c->temp_limit_c - temp) / (c->temp_limit_c - c->temp_derate_c));
        if (s < 1.0f) fault |= TT_DRV_OVERTEMP;
        i_mot *= s;
        i_reg *= s;
    }
    if (c->ov_trip_v > c->ov_derate_v)
        i_reg *= clamp01((c->ov_trip_v - n->vbus_filt) / (c->ov_trip_v - c->ov_derate_v));

    if (n->state == TT_NODE_RUN) {
        float lim;
        iq_m = (float)c->dir * n->iq_cmd_a;              /* motor frame */
        lim = (iq_m * n->last.omega_m < 0.0f) ? i_reg : i_mot;
        if (fabsf(iq_m) > lim) {
            iq_m = iq_m > 0.0f ? lim : -lim;
            fault |= TT_DRV_ILIMIT;
        }
        if (!n->ov && !n->uv && m->hw_enable) {
            n->id_ref_a = 0.0f;
            n->iq_ref_a = iq_m;
            n->force_angle = 0u;
            n->bridge_on = 1u;
        } else {
            bridge_off(n);
        }
    } else if (n->state != TT_NODE_ALIGN) {
        bridge_off(n);
    }

    /* What the STATUS says. */
    fault |= n->latched;
    if (n->v_sat) { fault |= TT_DRV_VLIMIT; n->v_sat = 0u; }
    if (n->estop) fault |= TT_DRV_ESTOP;
    if (n->ov) fault |= TT_DRV_OVERVOLT;
    if (n->uv) fault |= TT_DRV_UNDERVOLT;
    if (timeout) fault |= TT_DRV_CMD_TIMEOUT;
    if (!m->hw_enable) fault |= TT_DRV_HW_OFF;
    if (!c->cal_valid) fault |= TT_DRV_NOT_CAL;
    if (!n->cfg_ok || !n->foc_ok) fault |= TT_DRV_CONFIG;
    n->fault = fault;

    if (n->ticks % (c->status_div ? c->status_div : 1u) == 0u) queue_status(n, m);
}

/* ------------------------------------------------------------------ ISR --- */

int tt_node_isr(TtNode *n, const float i_abc[3], float vbus_v, int32_t enc_raw,
                float duty[3])
{
    FocIn in;
    FocOut out;
    if (!n->foc_ok) {
        duty[0] = duty[1] = duty[2] = 0.5f;
        return 0;
    }
    in.i_abc[0] = i_abc[0];
    in.i_abc[1] = i_abc[1];
    in.i_abc[2] = i_abc[2];
    in.vbus_v = vbus_v;
    in.theta_m = enc_angle(&n->cfg, enc_raw);
    in.id_ref = n->id_ref_a;
    in.iq_ref = n->iq_ref_a;
    in.enable = n->bridge_on;
    in.force_angle = n->force_angle;
    in.pad_[0] = in.pad_[1] = 0u;
    in.theta_e_force = n->theta_force;
    foc_isr_step(&n->foc, &in, &out);
    duty[0] = out.duty[0];
    duty[1] = out.duty[1];
    duty[2] = out.duty[2];
    n->last = out;
    if (out.v_sat) n->v_sat = 1u;
    return in.enable;
}
