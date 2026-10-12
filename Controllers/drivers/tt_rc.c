/*
 * tt_rc.c — RC receiver protocols. See tt_rc.h.
 */
#include "tt_rc.h"
#include "tt_safety.h"     /* TT_RC_* flags */

#include <string.h>

float tt_rc_norm(uint16_t ticks)
{
    float x = ((float)ticks - 991.5f) / 819.5f;    /* exact at both ends */
    return x < -1.0f ? -1.0f : (x > 1.0f ? 1.0f : x);
}

/* 16 x 11-bit channels, least significant bit first. */
static void unpack11(const uint8_t *p, uint16_t ch[TT_RC_CHANNELS])
{
    uint32_t acc = 0u;
    int bits = 0, i = 0, k;
    for (k = 0; k < TT_RC_CHANNELS; k++) {
        while (bits < 11) { acc |= (uint32_t)p[i++] << bits; bits += 8; }
        ch[k] = (uint16_t)(acc & 0x7FFu);
        acc >>= 11;
        bits -= 11;
    }
}

static void pack11(const uint16_t ch[TT_RC_CHANNELS], uint8_t *p)
{
    uint32_t acc = 0u;
    int bits = 0, o = 0, k;
    memset(p, 0, 22);
    for (k = 0; k < TT_RC_CHANNELS; k++) {
        acc |= (uint32_t)(ch[k] & 0x7FFu) << bits;
        bits += 11;
        while (bits >= 8) { p[o++] = (uint8_t)acc; acc >>= 8; bits -= 8; }
    }
}

/* ----------------------------------------------------------------- CRSF --- */

uint8_t tt_crsf_crc8(const uint8_t *p, size_t n)
{
    uint8_t crc = 0u;
    size_t i;
    int b;
    for (i = 0; i < n; i++) {
        crc ^= p[i];
        for (b = 0; b < 8; b++) crc = (uint8_t)((crc & 0x80u) ? (uint8_t)(crc << 1) ^ 0xD5u : (uint8_t)(crc << 1));
    }
    return crc;
}

void tt_crsf_init(TtCrsfRx *r) { memset(r, 0, sizeof *r); }

static int crsf_addr(uint8_t b) { return b == 0xC8u || b == 0xEAu || b == 0xEEu; }

static int crsf_frame(TtCrsfRx *r)
{
    uint8_t len = r->buf[1], type = r->buf[2];
    const uint8_t *pl = r->buf + 3;
    if (tt_crsf_crc8(r->buf + 2, (size_t)len - 1u) != r->buf[len + 1u]) { r->crc_errors++; return 0; }
    r->frames++;
    if (type == TT_CRSF_T_RC_CHANNELS && len == 24u) {
        unpack11(pl, r->ch);
        return TT_CRSF_GOT_CHANNELS;
    }
    if (type == TT_CRSF_T_LINK_STATS && len == 12u) {
        TtCrsfLink *l = &r->link;
        l->rssi1_dbm_neg = pl[0]; l->rssi2_dbm_neg = pl[1]; l->lq = pl[2];
        l->snr_db = (int8_t)pl[3]; l->antenna = pl[4]; l->rf_mode = pl[5];
        l->tx_power = pl[6]; l->d_rssi_dbm_neg = pl[7]; l->d_lq = pl[8];
        l->d_snr_db = (int8_t)pl[9];
        return TT_CRSF_GOT_LINK;
    }
    r->other++;
    return 0;
}

size_t tt_crsf_feed(TtCrsfRx *r, const uint8_t *p, size_t n, int *got)
{
    size_t i;
    *got = 0;
    for (i = 0; i < n; i++) {
        uint8_t b = p[i];
        if (r->n == 0u) {
            if (crsf_addr(b)) r->buf[r->n++] = b;
            continue;
        }
        if (r->n == 1u && (b < 2u || b > TT_CRSF_MAX_FRAME - 2u)) {
            /* Not a length: this byte may itself start the next frame. */
            r->n = 0u;
            if (crsf_addr(b)) r->buf[r->n++] = b;
            continue;
        }
        r->buf[r->n++] = b;
        if (r->n >= 2u && r->n == r->buf[1] + 2u) {
            *got = crsf_frame(r);
            r->n = 0u;
            return i + 1u;
        }
    }
    return n;
}

size_t tt_crsf_pack_channels(const uint16_t ch[TT_RC_CHANNELS], uint8_t out[26])
{
    out[0] = TT_CRSF_ADDR_FC;
    out[1] = 24u;
    out[2] = TT_CRSF_T_RC_CHANNELS;
    pack11(ch, out + 3);
    out[25] = tt_crsf_crc8(out + 2, 23u);
    return 26u;
}

/* ----------------------------------------------------------------- SBUS --- */

void tt_sbus_init(TtSbusRx *r) { memset(r, 0, sizeof *r); }

/* SBUS ends in 0x00. S.Bus2's telemetry footers (0x04, 0x14, ...) are not
 * accepted: a flags byte of 0x04 (frame lost) would then pass for a footer
 * one byte out of step, and SBUS has no other sync but the gap. */
static int sbus_footer(uint8_t b) { return b == 0x00u; }

size_t tt_sbus_feed(TtSbusRx *r, const uint8_t *p, size_t n, int *got)
{
    size_t i;
    *got = 0;
    for (i = 0; i < n; i++) {
        uint8_t b = p[i];
        if (r->n == 0u && b != 0x0Fu) continue;
        r->buf[r->n++] = b;
        if (r->n < TT_SBUS_FRAME) continue;
        if (sbus_footer(r->buf[TT_SBUS_FRAME - 1u])) {
            unpack11(r->buf + 1, r->ch);
            r->flags = r->buf[23];
            r->frames++;
            r->n = 0u;
            *got = 1;
            return i + 1u;
        }
        /* Misaligned: restart at the next header byte inside what we hold. */
        {
            uint8_t k, m = 0u;
            r->errors++;
            for (k = 1u; k < TT_SBUS_FRAME; k++)
                if (r->buf[k] == 0x0Fu) break;
            for (; k < TT_SBUS_FRAME; k++) r->buf[m++] = r->buf[k];
            r->n = m;
        }
    }
    return n;
}

void tt_sbus_pack(const uint16_t ch[TT_RC_CHANNELS], uint8_t flags, uint8_t out[TT_SBUS_FRAME])
{
    out[0] = 0x0Fu;
    pack11(ch, out + 1);
    out[23] = flags;
    out[24] = 0x00u;
}

/* ------------------------------------------------------------- to TtRc --- */

void tt_rc_default_cfg(TtRcCfg *c)
{
    uint8_t k;
    c->lost_s = 0.030f;
    c->failsafe_s = 0.200f;
    for (k = 0; k < 8u; k++) c->map[k] = k;
}

void tt_rc_init(TtRcIn *r, const TtRcCfg *c)
{
    memset(r, 0, sizeof *r);
    r->cfg = *c;
}

void tt_rc_frame(TtRcIn *r, const uint16_t ch[TT_RC_CHANNELS], int lost, int failsafe, tt_us_t t)
{
    memcpy(r->ch, ch, sizeof r->ch);
    r->have = 1u;
    r->rx_lost = (uint8_t)(lost != 0);
    r->rx_failsafe = (uint8_t)(failsafe != 0);
    r->t_frame = t;
    r->seq++;
}

void tt_rc_read(const TtRcIn *r, tt_us_t now, TtRc *out)
{
    float age;
    int k;
    memset(out, 0, sizeof *out);
    out->st.valid = 1u;          /* a receiver is fitted */
    if (!r->have) {
        out->failsafe = TT_RC_FAILSAFE | TT_RC_FRAME_LOST;
        return;
    }
    out->st.t_us = r->t_frame;
    out->st.seq = r->seq;
    for (k = 0; k < 8; k++) {
        uint8_t c = r->cfg.map[k];
        out->ch[k] = c < TT_RC_CHANNELS ? tt_rc_norm(r->ch[c]) : 0.0f;
    }
    age = (float)(int32_t)(now - r->t_frame) * 1e-6f;
    if (r->rx_lost || age > r->cfg.lost_s) out->failsafe |= TT_RC_FRAME_LOST;
    if (r->rx_failsafe || age > r->cfg.failsafe_s) out->failsafe |= TT_RC_FAILSAFE;
}
