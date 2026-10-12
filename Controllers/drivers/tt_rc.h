/*
 * tt_rc.h — RC receiver protocols (CRSF, SBUS) and their TtRc (HW_04_MAST).
 *
 * The mast carries an ExpressLRS receiver on a UART (W05 MAST-LINK RC_TX/RX)
 * speaking CRSF; SBUS is here for a receiver that only has that. Each parser
 * takes bytes as the UART delivers them, in any chunks, and hands decoded
 * channel frames to a TtRcIn, which produces the TtRc the safety layer reads
 * with the twin's semantics (hal/controller_api.h, SENSOR_RC):
 *
 *   - channels normalised to -1..1 (988 us = -1, 1500 = 0, 2012 = +1);
 *   - a frame that does not come repeats the last channels and sets
 *     TT_RC_FRAME_LOST; after the hold time TT_RC_FAILSAFE is declared;
 *   - ch5 (index 4) arms, ch6 (index 5) kills (tt_safety: > 0.5).
 *
 * Set the ELRS receiver's failsafe to "no pulses": it then stops sending
 * channel frames when the link goes, and the hold time here decides, rather
 * than the receiver replaying stick positions that look like a live link.
 *
 * Protocol constants are from the CRSF and SBUS descriptions [D]; check them
 * against the receiver at bring-up (tests/test_drivers.c has the vectors).
 *
 * Portable C11, no allocation.
 */
#ifndef TT_RC_H
#define TT_RC_H

#include <stddef.h>
#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_RC_CHANNELS 16

/* Channel ticks (CRSF and SBUS share them: 172 = 988 us, 992 = 1500 us,
 * 1811 = 2012 us) -> -1..1, clamped. */
float tt_rc_norm(uint16_t ticks);

/* ---- CRSF (ExpressLRS, Crossfire): 420 kbaud 8N1 ------------------------
 * frame: [addr 0xC8|0xEA|0xEE] [len = type..crc] [type] [payload] [crc8]
 * crc8: polynomial 0xD5 over type and payload. */
#define TT_CRSF_MAX_FRAME      64u
#define TT_CRSF_ADDR_FC        0xC8u
#define TT_CRSF_T_LINK_STATS   0x14u
#define TT_CRSF_T_RC_CHANNELS  0x16u

typedef struct {
    uint8_t rssi1_dbm_neg, rssi2_dbm_neg;   /* uplink RSSI, -dBm            */
    uint8_t lq;                             /* uplink link quality, %       */
    int8_t  snr_db;
    uint8_t antenna, rf_mode, tx_power;
    uint8_t d_rssi_dbm_neg, d_lq;
    int8_t  d_snr_db;
} TtCrsfLink;

typedef struct {
    uint8_t    buf[TT_CRSF_MAX_FRAME];
    uint8_t    n;
    uint16_t   ch[TT_RC_CHANNELS];          /* the last channel frame        */
    TtCrsfLink link;                        /* the last link statistics      */
    uint32_t   frames, crc_errors, other;
} TtCrsfRx;

#define TT_CRSF_GOT_CHANNELS 0x01
#define TT_CRSF_GOT_LINK     0x02

uint8_t tt_crsf_crc8(const uint8_t *p, size_t n);
void    tt_crsf_init(TtCrsfRx *r);
/* Consumes bytes up to and including the end of the first complete frame;
 * *got gets TT_CRSF_GOT_* for it (0 if none). Returns the bytes consumed:
 * call again with the rest. */
size_t  tt_crsf_feed(TtCrsfRx *r, const uint8_t *p, size_t n, int *got);
/* A channel frame (26 bytes), for tests and HIL injection. */
size_t  tt_crsf_pack_channels(const uint16_t ch[TT_RC_CHANNELS], uint8_t out[26]);

/* ---- SBUS: 100 kbaud 8E2, inverted ---------------------------------------
 * frame: 0x0F, 22 bytes of 16 x 11-bit channels, flags, footer. */
#define TT_SBUS_FRAME       25u
#define TT_SBUS_FRAME_LOST  0x04u
#define TT_SBUS_FAILSAFE    0x08u

typedef struct {
    uint8_t  buf[TT_SBUS_FRAME];
    uint8_t  n;
    uint8_t  flags;                         /* of the last frame             */
    uint16_t ch[TT_RC_CHANNELS];
    uint32_t frames, errors;
} TtSbusRx;

void   tt_sbus_init(TtSbusRx *r);
/* As tt_crsf_feed; *got = 1 when a frame completed. */
size_t tt_sbus_feed(TtSbusRx *r, const uint8_t *p, size_t n, int *got);
void   tt_sbus_pack(const uint16_t ch[TT_RC_CHANNELS], uint8_t flags, uint8_t out[TT_SBUS_FRAME]);

/* ---- decoded frames -> TtRc --------------------------------------------- */
typedef struct {
    float   lost_s;        /* no channel frame this long: FRAME_LOST          */
    float   failsafe_s;    /* ... this long: FAILSAFE (the receiver's hold)   */
    uint8_t map[8];        /* TtRc.ch[k] = receiver channel map[k] (0-based)  */
} TtRcCfg;

typedef struct {
    TtRcCfg  cfg;
    uint16_t ch[TT_RC_CHANNELS];
    uint8_t  have;         /* a channel frame has arrived                     */
    uint8_t  rx_lost, rx_failsafe, pad_;
    tt_us_t  t_frame;
    uint32_t seq;
} TtRcIn;

/* lost after 30 ms (1.5 frames at the twin's 50 Hz), failsafe after 200 ms,
 * channels 1..8 in order. */
void tt_rc_default_cfg(TtRcCfg *c);
void tt_rc_init(TtRcIn *r, const TtRcCfg *c);
/* A decoded channel frame arrived at t. lost / failsafe: what the receiver
 * itself flags (SBUS flags; CRSF: link quality 0 -> failsafe). */
void tt_rc_frame(TtRcIn *r, const uint16_t ch[TT_RC_CHANNELS], int lost, int failsafe, tt_us_t t);
/* The TtRc for a control tick at `now`. */
void tt_rc_read(const TtRcIn *r, tt_us_t now, TtRc *out);

#ifdef __cplusplus
}
#endif

#endif /* TT_RC_H */
