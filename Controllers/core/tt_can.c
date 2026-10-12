/*
 * tt_can.c — the drive bus frames. See tt_can.h.
 */
#include "tt_can.h"

#include <string.h>

/* ------------------------------------------------------ byte helpers --- */

static void put16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void put32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); p[2] = (uint8_t)(v >> 16); p[3] = (uint8_t)(v >> 24);
}
static uint16_t get16(const uint8_t *p) { return (uint16_t)(p[0] | (uint16_t)(p[1] << 8)); }
static uint32_t get32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}
static void putf(uint8_t *p, float f) { uint32_t u; memcpy(&u, &f, 4); put32(p, u); }
static float getf(const uint8_t *p) { uint32_t u = get32(p); float f; memcpy(&f, &u, 4); return f; }

static float nanf_(void) { uint32_t u = 0x7FC00000u; float f; memcpy(&f, &u, 4); return f; }

int16_t tt_can_q16(float x, float scale)
{
    float v = x * scale;
    if (v != v) return 0;
    if (v >= 32767.0f) return 32767;
    if (v <= -32768.0f) return -32768;
    return (int16_t)(int32_t)(v >= 0.0f ? v + 0.5f : v - 0.5f);
}

static uint16_t qu16(float x, float scale)
{
    float v = x * scale;
    if (!(v > 0.0f)) return 0;          /* also NaN */
    if (v >= 65535.0f) return 65535u;
    return (uint16_t)(v + 0.5f);
}

static int16_t qtemp(float t_c)
{
    int16_t q;
    if (t_c != t_c) return (int16_t)TT_CAN_TEMP_NONE;
    q = tt_can_q16(t_c, 10.0f);
    return q == (int16_t)TT_CAN_TEMP_NONE ? (int16_t)(TT_CAN_TEMP_NONE + 1) : q;
}

static float dtemp(int16_t q) { return q == (int16_t)TT_CAN_TEMP_NONE ? nanf_() : (float)q * 0.1f; }

static void frame_begin(TtCanFrame *f, uint32_t id, uint8_t len)
{
    f->id = id;
    f->len = len;
    f->brs = 1u;
    f->pad_[0] = f->pad_[1] = 0u;
    memset(f->data, 0, sizeof f->data);
}

/* ------------------------------------------------------------- kinds --- */

int tt_can_kind(uint32_t id, uint8_t *node)
{
    static const struct { uint32_t base; int kind; } per[] = {
        { TT_CAN_ID_STATUS, TT_CAN_K_STATUS }, { TT_CAN_ID_CFG_REQ, TT_CAN_K_CFG_REQ },
        { TT_CAN_ID_CFG_RSP, TT_CAN_K_CFG_RSP }, { TT_CAN_ID_DEBUG, TT_CAN_K_DEBUG },
        { TT_CAN_ID_BOOT, TT_CAN_K_BOOT }
    };
    size_t i;
    if (node) *node = 0;
    if (id == TT_CAN_ID_ESTOP) return TT_CAN_K_ESTOP;
    if (id == TT_CAN_ID_CMD) return TT_CAN_K_CMD;
    for (i = 0; i < sizeof per / sizeof per[0]; i++)
        if (id >= per[i].base && id < per[i].base + TT_CAN_NODES) {
            if (node) *node = (uint8_t)(id - per[i].base);
            return per[i].kind;
        }
    return TT_CAN_K_UNKNOWN;
}

/* ------------------------------------------------------------- ESTOP --- */

void tt_can_pack_estop(TtCanFrame *f, const TtCanEstop *e)
{
    frame_begin(f, TT_CAN_ID_ESTOP, TT_CAN_LEN_ESTOP);
    put32(f->data, e->reason);
}

int tt_can_get_estop(const TtCanFrame *f, TtCanEstop *e)
{
    if (f->id != TT_CAN_ID_ESTOP || f->len != TT_CAN_LEN_ESTOP) return -1;
    e->reason = get32(f->data);
    return 0;
}

/* --------------------------------------------------------------- CMD --- */
/*  0 u16 seq | 2 u8 enable | 3 u8 flags | 4 i16 iq_ma[4] | 12 u32 t_us   */

void tt_can_pack_cmd(TtCanFrame *f, const TtCanCmd *c)
{
    int n;
    frame_begin(f, TT_CAN_ID_CMD, TT_CAN_LEN_CMD);
    put16(f->data, c->seq);
    f->data[2] = c->enable;
    f->data[3] = c->flags;
    for (n = 0; n < TT_CAN_NODES; n++)
        put16(f->data + 4 + 2 * n, (uint16_t)tt_can_q16(c->iq_a[n], 1000.0f));
    put32(f->data + 12, c->t_us);
}

int tt_can_get_cmd(const TtCanFrame *f, TtCanCmd *c)
{
    int n;
    if (f->id != TT_CAN_ID_CMD || f->len != TT_CAN_LEN_CMD) return -1;
    c->seq = get16(f->data);
    c->enable = f->data[2];
    c->flags = f->data[3];
    for (n = 0; n < TT_CAN_NODES; n++)
        c->iq_a[n] = (float)(int16_t)get16(f->data + 4 + 2 * n) * 0.001f;
    c->t_us = get32(f->data + 12);
    return 0;
}

/* ------------------------------------------------------------ STATUS --- */
/*  0 u16 cmd_seq | 2 u16 age_us | 4 u8 seq | 5 u8 state | 6 u16 fault
 *  8 i16 iq_ma | 10 i16 id_ma | 12 i32 enc | 16 i16 omega 0.1 rad/s
 * 18 u16 vbus_mv | 20 i16 t_fet 0.1 C | 22 i16 t_wind 0.1 C               */

void tt_can_pack_status(TtCanFrame *f, uint8_t node, const TtCanStatus *s)
{
    frame_begin(f, TT_CAN_ID_STATUS + node, TT_CAN_LEN_STATUS);
    put16(f->data + 0, s->cmd_seq);
    put16(f->data + 2, s->age_us);
    f->data[4] = s->seq;
    f->data[5] = s->state;
    put16(f->data + 6, s->fault);
    put16(f->data + 8, (uint16_t)tt_can_q16(s->iq_a, 1000.0f));
    put16(f->data + 10, (uint16_t)tt_can_q16(s->id_a, 1000.0f));
    put32(f->data + 12, (uint32_t)s->enc_count);
    put16(f->data + 16, (uint16_t)tt_can_q16(s->omega_m, 10.0f));
    put16(f->data + 18, qu16(s->vbus_v, 1000.0f));
    put16(f->data + 20, (uint16_t)qtemp(s->t_fet_c));
    put16(f->data + 22, (uint16_t)qtemp(s->t_wind_c));
}

int tt_can_get_status(const TtCanFrame *f, TtCanStatus *s)
{
    uint8_t node;
    if (tt_can_kind(f->id, &node) != TT_CAN_K_STATUS || f->len != TT_CAN_LEN_STATUS) return -1;
    s->cmd_seq = get16(f->data + 0);
    s->age_us = get16(f->data + 2);
    s->seq = f->data[4];
    s->state = f->data[5];
    s->fault = get16(f->data + 6);
    s->iq_a = (float)(int16_t)get16(f->data + 8) * 0.001f;
    s->id_a = (float)(int16_t)get16(f->data + 10) * 0.001f;
    s->enc_count = (int32_t)get32(f->data + 12);
    s->omega_m = (float)(int16_t)get16(f->data + 16) * 0.1f;
    s->vbus_v = (float)get16(f->data + 18) * 0.001f;
    s->t_fet_c = dtemp((int16_t)get16(f->data + 20));
    s->t_wind_c = dtemp((int16_t)get16(f->data + 22));
    return 0;
}

/* --------------------------------------------------------------- CFG --- */
/*  0 u8 op | 1 u8 tag | 2 u16 key | 4 u32 value                          */

void tt_can_pack_cfg(TtCanFrame *f, uint32_t base_id, uint8_t node, const TtCanCfg *c)
{
    frame_begin(f, base_id + node, TT_CAN_LEN_CFG);
    f->data[0] = c->op;
    f->data[1] = c->tag;
    put16(f->data + 2, c->key);
    put32(f->data + 4, c->value);
}

int tt_can_get_cfg(const TtCanFrame *f, TtCanCfg *c)
{
    int k = tt_can_kind(f->id, NULL);
    if ((k != TT_CAN_K_CFG_REQ && k != TT_CAN_K_CFG_RSP) || f->len != TT_CAN_LEN_CFG) return -1;
    c->op = f->data[0];
    c->tag = f->data[1];
    c->key = get16(f->data + 2);
    c->value = get32(f->data + 4);
    return 0;
}

/* ------------------------------------------------------------- DEBUG --- */
/*  0 u16 seq | 2 u8 set | 3 u8 n | 4 f32 v[15]                            */

void tt_can_pack_debug(TtCanFrame *f, uint8_t node, const TtCanDebug *d)
{
    uint8_t i, n = d->n > 15u ? 15u : d->n;
    frame_begin(f, TT_CAN_ID_DEBUG + node, TT_CAN_LEN_DEBUG);
    put16(f->data, d->seq);
    f->data[2] = d->set;
    f->data[3] = n;
    for (i = 0; i < n; i++) putf(f->data + 4 + 4 * i, d->v[i]);
}

int tt_can_get_debug(const TtCanFrame *f, TtCanDebug *d)
{
    uint8_t i;
    if (tt_can_kind(f->id, NULL) != TT_CAN_K_DEBUG || f->len != TT_CAN_LEN_DEBUG) return -1;
    d->seq = get16(f->data);
    d->set = f->data[2];
    d->n = f->data[3];
    if (d->n > 15u) return -1;
    for (i = 0; i < 15u; i++) d->v[i] = i < d->n ? getf(f->data + 4 + 4 * i) : 0.0f;
    return 0;
}

/* -------------------------------------------------------------- BOOT --- */
/*  0 u8 proto | 1 u8 node | 2 u8 hw_rev | 3 u8 reset_cause | 4 u32 fw_id
 *  8 u32 cfg_hash | 12 u8 state | 13 u8 cal_valid | 14 u16 enc_cpr        */

void tt_can_pack_boot(TtCanFrame *f, uint8_t node, const TtCanBoot *b)
{
    frame_begin(f, TT_CAN_ID_BOOT + node, TT_CAN_LEN_BOOT);
    f->data[0] = b->proto;
    f->data[1] = b->node;
    f->data[2] = b->hw_rev;
    f->data[3] = b->reset_cause;
    put32(f->data + 4, b->fw_id);
    put32(f->data + 8, b->cfg_hash);
    f->data[12] = b->state;
    f->data[13] = b->cal_valid;
    put16(f->data + 14, b->enc_cpr);
}

int tt_can_get_boot(const TtCanFrame *f, TtCanBoot *b)
{
    if (tt_can_kind(f->id, NULL) != TT_CAN_K_BOOT || f->len != TT_CAN_LEN_BOOT) return -1;
    b->proto = f->data[0];
    b->node = f->data[1];
    b->hw_rev = f->data[2];
    b->reset_cause = f->data[3];
    b->fw_id = get32(f->data + 4);
    b->cfg_hash = get32(f->data + 8);
    b->state = f->data[12];
    b->cal_valid = f->data[13];
    b->enc_cpr = get16(f->data + 14);
    return 0;
}

/* ------------------------------------------------- lengths and timing --- */

uint8_t tt_can_dlc_to_len(uint8_t dlc)
{
    static const uint8_t fd[7] = { 12, 16, 20, 24, 32, 48, 64 };
    if (dlc <= 8u) return dlc;
    return dlc <= 15u ? fd[dlc - 9u] : 0u;
}

uint8_t tt_can_len_round(size_t n)
{
    uint8_t d;
    for (d = 0; d <= 15u; d++)
        if (tt_can_dlc_to_len(d) >= n) return tt_can_dlc_to_len(d);
    return 0;
}

void tt_can_frame_bits(uint8_t len, uint32_t *nominal_bits, uint32_t *data_bits)
{
    /* Arbitration phase: SOF, 11-bit ID, RRS, IDE, FDF, res, BRS. */
    const uint32_t arb = 17u;
    /* Data phase before the CRC: ESI, DLC, the data. */
    uint32_t pre = 5u + 8u * len;
    /* Worst-case dynamic stuffing over SOF..data: one per 4 bits after the
     * first; the arbitration phase's share goes to the nominal rate. */
    uint32_t stuff = (arb + pre - 1u) / 4u, stuff_arb = (arb - 1u) / 4u;
    /* CRC field: 4-bit stuff count + CRC-17 (<= 16 bytes) or CRC-21, with a
     * fixed stuff bit before the stuff count and after every 4 bits. */
    uint32_t crc = len <= 16u ? 17u : 21u;
    uint32_t crc_field = 4u + crc + 1u + (4u + crc) / 4u;
    /* CRC delimiter (the rate switches back at its sample point), then ACK
     * slot, ACK delimiter, EOF (7) and interframe space (3). */
    *nominal_bits = arb + stuff_arb + 2u + 7u + 3u;
    *data_bits = pre + (stuff - stuff_arb) + crc_field + 1u;
}

float tt_can_frame_us(uint8_t len, int brs, float nominal_bps, float data_bps)
{
    uint32_t nb, db;
    tt_can_frame_bits(len, &nb, &db);
    if (!brs) return (float)(nb + db) * 1e6f / nominal_bps;
    return (float)nb * 1e6f / nominal_bps + (float)db * 1e6f / data_bps;
}
