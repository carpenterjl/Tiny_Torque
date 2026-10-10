/*
 * tt_wire.h — the lockstep HIL wire protocol (HIL-02).
 *
 * How a host (the sim's bridge, a replay tool) and the firmware (a board in
 * the loop, or the same firmware built for the host) talk over a byte
 * stream: USB CDC, a UART, a pipe. One request, one reply, per control tick:
 *
 *     host                                   firmware
 *     INIT {rate}                      ->
 *                                      <-    INFO {version, log names, ...}
 *     CONFIG {TtCarDesc}               ->
 *                                      <-    ACK
 *     MEAS {TtMeas}       tick N       ->    (runs one tick)
 *                                      <-    ACT {TtAct, log}    tick N
 *     MEAS                tick N+1     ->    ...
 *     RESET / SHUTDOWN                 ->
 *                                      <-    ACK
 *
 * FRAMING. Every message is one frame:
 *
 *     COBS( header | payload | crc16 ) 0x00
 *
 * COBS removes every zero byte from the frame body, so 0x00 only ever marks
 * the end of a frame: a receiver that starts mid-stream, or loses bytes,
 * resynchronises at the next zero. The CRC (CRC-16/CCITT-FALSE: poly 0x1021,
 * init 0xFFFF, over header and payload, stored little-endian) catches what
 * USB's own CRC cannot: a buffer overrun, a dropped USB packet, two frames
 * spliced together.
 *
 * HEADER, 16 bytes, little-endian:
 *     u8  type       TT_WIRE_*
 *     u8  flags      TT_WF_*
 *     u16 seq        per sender, +1 every frame: a gap is a lost frame
 *     u32 tick       the control tick (MEAS); a reply echoes its request's
 *     u64 time_us    the host's sim time (MEAS); a reply echoes it
 *
 * PAYLOADS are packed field by field, little-endian, IEEE-754 floats bit for
 * bit (NaN and -0 survive), never as raw structs: the two ends may be a
 * 64-bit PC and a 32-bit MCU with different padding. A source whose stamp is
 * invalid (st.valid == 0) is not sent: a presence mask says which are there,
 * and the receiver zeroes the rest — what an invalid source holds means
 * nothing. A ToF sends only its `zones` zones, the UWB list only `uwb_n`
 * reads. A MEAS for a car with every source is ~350 bytes; ACT with the
 * Opus log ~150.
 *
 * USB: frames are larger than one 64-byte full-speed packet, so the
 * firmware's CDC layer must end a transfer whose length is a multiple of 64
 * with a zero-length packet, or the host holds the frame's tail until the
 * next one. Use the MCU's native USB, not a USB-UART bridge (latency timers
 * of ~16 ms).
 *
 * Portable C11, no allocation, no platform calls: the same file builds into
 * the MCU image and the host tools.
 */
#ifndef TT_WIRE_H
#define TT_WIRE_H

#include <stddef.h>
#include <stdint.h>
#include "tt_types.h"
#include "tt_board.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_WIRE_VERSION      1u

#define TT_WIRE_HDR          16u
#define TT_WIRE_MAX_PAYLOAD  1024u
#define TT_WIRE_MAX_BODY     (TT_WIRE_HDR + TT_WIRE_MAX_PAYLOAD + 2u)
/* COBS adds one byte per 254, plus one; then the 0x00 delimiter. */
#define TT_WIRE_MAX_FRAME    (TT_WIRE_MAX_BODY + TT_WIRE_MAX_BODY / 254u + 2u)

/* Message types. Requests come from the host; each has one reply. */
enum {
    TT_WIRE_PING     = 0x01,  /* host -> fw: u32 nonce             -> PONG     */
    TT_WIRE_PONG     = 0x02,  /* fw -> host: the nonce back                    */
    TT_WIRE_INIT     = 0x10,  /* host -> fw: f32 rate_hz           -> INFO
                                 (rate 0: identify only, initialise nothing)  */
    TT_WIRE_INFO     = 0x11,  /* fw -> host: who the firmware is, its log      */
    TT_WIRE_CONFIG   = 0x12,  /* host -> fw: TtCarDesc             -> ACK      */
    TT_WIRE_RESET    = 0x13,  /* host -> fw: the car is back at the start      */
    TT_WIRE_SHUTDOWN = 0x14,  /* host -> fw: the run is over                   */
    TT_WIRE_ACK      = 0x1F,  /* fw -> host: u8 type acked, i32 status         */
    TT_WIRE_MEAS     = 0x20,  /* host -> fw: TtMeas for `tick`     -> ACT      */
    TT_WIRE_ACT      = 0x21,  /* fw -> host: TtAct + log for `tick`            */
    TT_WIRE_FAULT    = 0x30   /* fw -> host: u16 code, u32 detail (unsolicited)*/
};

/* Header flags. */
#define TT_WF_NOT_READY  0x01u   /* ACT: the firmware was not initialised; all zero */

/* FAULT codes. */
enum {
    TT_WFAULT_UNKNOWN_TYPE = 1,  /* detail: the type                          */
    TT_WFAULT_BAD_PAYLOAD  = 2,  /* detail: the type                          */
    TT_WFAULT_RX_ERRORS    = 3   /* detail: CRC + COBS errors so far           */
};

typedef struct {
    uint8_t  type;
    uint8_t  flags;
    uint16_t seq;
    uint32_t tick;
    uint64_t time_us;
} TtWireHdr;

/* ---- framing ---------------------------------------------------------- */

uint16_t tt_crc16(const uint8_t *p, size_t n, uint16_t crc);   /* start 0xFFFF */

/* COBS-encode n bytes (no delimiter added); returns the encoded length.
 * `out` needs n + n/254 + 1 bytes. */
size_t tt_cobs_encode(const uint8_t *in, size_t n, uint8_t *out);

/* Decode one COBS block (without its delimiter). Returns the decoded length,
 * or -1 if it is malformed or does not fit in `cap`. */
int tt_cobs_decode(const uint8_t *in, size_t n, uint8_t *out, size_t cap);

/* Build a complete frame (header, payload, CRC, COBS, delimiter) into `out`.
 * Returns its length, 0 if the payload is too long or `cap` too small. */
size_t tt_wire_frame(const TtWireHdr *h, const uint8_t *payload, size_t n,
                     uint8_t *out, size_t cap);

/* Streaming receiver: feed it bytes as they arrive, in any chunks. */
typedef struct {
    uint8_t  buf[TT_WIRE_MAX_FRAME];
    size_t   len;
    int      overflow;
    uint8_t  body[TT_WIRE_MAX_BODY];
    /* counters */
    uint32_t frames, crc_errors, cobs_errors, overflows, seq_gaps;
    uint16_t next_seq;
    int      have_seq;
} TtWireRx;

void tt_wire_rx_init(TtWireRx *rx);

/* Consume bytes from `p` up to and including the end of the first complete,
 * valid frame. Returns how many bytes were consumed; when that ended a valid
 * frame, *got = 1 and *h / *payload / *plen describe it (the payload points
 * into the receiver and stays valid until the next call). Damaged frames are
 * counted and skipped. Call again with the rest of the bytes. */
size_t tt_wire_rx_feed(TtWireRx *rx, const uint8_t *p, size_t n, int *got,
                       TtWireHdr *h, const uint8_t **payload, size_t *plen);

/* ---- payloads --------------------------------------------------------- */
/* put_*: returns the payload length, 0 if it does not fit in `cap`.
 * get_*: returns 0 on success, -1 if the payload is malformed. */

size_t tt_wire_put_meas(uint8_t *p, size_t cap, const TtMeas *m);
int    tt_wire_get_meas(const uint8_t *p, size_t n, TtMeas *m);

size_t tt_wire_put_act(uint8_t *p, size_t cap, const TtAct *a,
                       const float *log, uint32_t n_log);
int    tt_wire_get_act(const uint8_t *p, size_t n, TtAct *a,
                       float *log, uint32_t log_cap, uint32_t *n_log);

size_t tt_wire_put_car(uint8_t *p, size_t cap, const TtCarDesc *c);
int    tt_wire_get_car(const uint8_t *p, size_t n, TtCarDesc *c);

/* INFO: who the firmware is. Strings are NUL-terminated on decode. */
typedef struct {
    uint16_t version;           /* TT_WIRE_VERSION it speaks               */
    uint16_t n_log;             /* log floats in each ACT                  */
    float    rate_hz;           /* the tick it wants; 0 = the host's       */
    uint32_t params_hash;       /* tt_params_hash() of its parameter set   */
    char     name[32];          /* firmware name                           */
    char     log_names[768];    /* comma-separated, opus_log.def order     */
} TtWireInfo;

size_t tt_wire_put_info(uint8_t *p, size_t cap, const TtWireInfo *i);
int    tt_wire_get_info(const uint8_t *p, size_t n, TtWireInfo *i);

#ifdef __cplusplus
}
#endif

#endif /* TT_WIRE_H */
