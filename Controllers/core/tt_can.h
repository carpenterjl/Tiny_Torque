/*
 * tt_can.h — the drive bus: CAN-FD between the vehicle MCU (MAIN) and the four
 * wheel-motor nodes (SYS-03; Hardware/HW_00_SYSTEM.html §7, HW_03_NODE.html).
 *
 * One header for both ends, as tt_wire.h is for the HIL link: the MAIN
 * firmware (core/tt_drvbus.h), the node firmware (node/tt_node.h) and the
 * host tests all pack and unpack these frames with the same code.
 *
 * THE BUS. 11-bit identifiers, FD frames. Bring-up runs FD frames at
 * 1 Mbit/s without bit-rate switching; the target is 1 Mbit/s arbitration
 * and 5 Mbit/s data (BRS). tt_can_frame_bits() gives the worst-case length
 * of a frame, so a schedule's bus load can be checked before the bus exists
 * (tests/test_can.c checks the planned ones).
 *
 * NODES. Node n drives wheel n (TT_FL, TT_FR, TT_RL, TT_RR): the node reads
 * n from its ID jumpers, and the daisy-chain order does not matter.
 *
 *     id          frame    from -> to     rate        payload bytes
 *     0x010       ESTOP    MAIN -> all    event       4
 *     0x020       CMD      MAIN -> all    1 kHz       16
 *     0x100 + n   STATUS   node -> MAIN   1 kHz / div 24
 *     0x200 + n   CFG_REQ  MAIN -> node   on demand   8
 *     0x280 + n   CFG_RSP  node -> MAIN   reply       8
 *     0x300 + n   DEBUG    node -> MAIN   on demand   64
 *     0x700 + n   BOOT     node -> MAIN   at boot, on request  16
 *
 * A lower identifier wins arbitration, so an ESTOP is never queued behind a
 * command and a command never behind a status.
 *
 * WHEEL FRAME. Every current, speed and count on the bus is in the wheel's
 * frame: + Iq is torque that drives the car FORWARD, + speed and rising
 * counts are forward rolling. The left and right motors are mirror images,
 * so the node applies its configured direction (TtNodeCfg.dir) on both
 * sides of the bus and nothing upstream needs to know which way a motor is
 * wound.
 *
 * TIME. The nodes and MAIN do not share a clock. A STATUS says which
 * command it last obeyed (cmd_seq) and how long after that command arrived
 * its measurements were taken (age_us); MAIN knows when it sent that
 * command, so it can place the measurement on its own clock to within the
 * frame's transmission time (tt_drvbus.h).
 *
 * PAYLOADS are packed field by field, little-endian. Fixed-point fields
 * saturate; a non-finite value packs as 0.
 *
 * Portable C11, no allocation, no platform calls.
 */
#ifndef TT_CAN_H
#define TT_CAN_H

#include <stddef.h>
#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_CAN_PROTO_VERSION 1u

#define TT_CAN_NODES      TT_MAX_WHEELS
#define TT_CAN_MAX_DATA   64u

/* Identifiers. */
#define TT_CAN_ID_ESTOP     0x010u
#define TT_CAN_ID_CMD       0x020u
#define TT_CAN_ID_STATUS    0x100u   /* + node */
#define TT_CAN_ID_CFG_REQ   0x200u   /* + node */
#define TT_CAN_ID_CFG_RSP   0x280u   /* + node */
#define TT_CAN_ID_DEBUG     0x300u   /* + node */
#define TT_CAN_ID_BOOT      0x700u   /* + node */

/* Payload lengths. Each is a valid CAN-FD data length, so the DLC is exact. */
#define TT_CAN_LEN_ESTOP    4u
#define TT_CAN_LEN_CMD      16u
#define TT_CAN_LEN_STATUS   24u
#define TT_CAN_LEN_CFG      8u
#define TT_CAN_LEN_DEBUG    64u
#define TT_CAN_LEN_BOOT     16u

/* A frame as the FDCAN driver sees it. */
typedef struct {
    uint32_t id;                    /* 11-bit identifier                     */
    uint8_t  len;                   /* data bytes, a valid CAN-FD length     */
    uint8_t  brs;                   /* 1 = data phase at the data bit rate   */
    uint8_t  pad_[2];
    uint8_t  data[TT_CAN_MAX_DATA];
} TtCanFrame;

/* What kind of frame an identifier is, and for which node. Returns one of
 * TT_CAN_K_*, with *node set for the per-node kinds (else 0). */
enum {
    TT_CAN_K_UNKNOWN = 0,
    TT_CAN_K_ESTOP, TT_CAN_K_CMD, TT_CAN_K_STATUS, TT_CAN_K_CFG_REQ,
    TT_CAN_K_CFG_RSP, TT_CAN_K_DEBUG, TT_CAN_K_BOOT
};
int tt_can_kind(uint32_t id, uint8_t *node);

/* ---- ESTOP: every node switches its bridge off and latches it --------- */
/* Cleared only by a CMD with TT_CAN_CMD_CLEAR set and that node's enable
 * bit clear, so clearing a stop can never start a motor in the same frame. */
typedef struct {
    uint32_t reason;                /* the TT_SF_* bits that caused it        */
} TtCanEstop;

/* ---- CMD: the torque command, one frame for all four nodes ------------- */
#define TT_CAN_CMD_CLEAR  0x01u     /* clear latched faults and a latched
                                       ESTOP on the nodes whose enable bit
                                       is 0 (if their cause has gone)        */
typedef struct {
    uint16_t seq;                   /* +1 every frame                         */
    uint8_t  enable;                /* bit n: node n may drive (bridge on)    */
    uint8_t  flags;                 /* TT_CAN_CMD_*                           */
    float    iq_a[TT_CAN_NODES];    /* wheel frame; sent as int16 mA          */
    uint32_t t_us;                  /* MAIN's clock when it was queued        */
} TtCanCmd;

/* ---- STATUS: one node's measurements ----------------------------------- */
/* Node states. */
enum {
    TT_NODE_INIT  = 0,              /* configuration unusable: never drives   */
    TT_NODE_IDLE  = 1,              /* bridge off, ready                      */
    TT_NODE_ALIGN = 2,              /* finding the encoder offset             */
    TT_NODE_RUN   = 3,              /* enabled, following Iq                  */
    TT_NODE_FAULT = 4,              /* a latched trip; needs CMD CLEAR        */
    TT_NODE_ESTOP = 5               /* stopped by ESTOP; needs CMD CLEAR      */
};

#define TT_CAN_TEMP_NONE  (-32768)  /* the raw value of an absent sensor      */

typedef struct {
    uint16_t cmd_seq;               /* the last CMD this node accepted        */
    uint16_t age_us;                /* that CMD's arrival -> this sample;
                                       saturates at 65535                     */
    uint8_t  seq;                   /* the node's own counter, +1 per STATUS  */
    uint8_t  state;                 /* TT_NODE_*                              */
    uint16_t fault;                 /* TT_DRV_* bits (tt_safety.h)            */
    float    iq_a, id_a;            /* measured; int16 mA on the wire          */
    int32_t  enc_count;             /* cumulative, wheel frame                 */
    float    omega_m;               /* motor shaft rad/s, wheel frame; 0.1    */
    float    vbus_v;                /* uint16 mV                              */
    float    t_fet_c, t_wind_c;     /* int16 0.1 degC; NaN = no sensor        */
} TtCanStatus;

/* ---- CFG: read and write a node's configuration, run its procedures ---- */
enum {
    TT_CFG_GET      = 1,            /* value <- key                           */
    TT_CFG_SET      = 2,            /* key <- value (bridge off only)         */
    TT_CFG_SAVE     = 3,            /* write the configuration to flash       */
    TT_CFG_ALIGN    = 4,            /* run the encoder alignment (IDLE only)  */
    TT_CFG_HELLO    = 5,            /* send BOOT again                        */
    TT_CFG_DEFAULTS = 6             /* back to the built-in defaults          */
};
#define TT_CFG_ERR  0x80u           /* set in a reply's op: value is a TT_CFGE_* */
enum {
    TT_CFGE_OP = 1,                 /* unknown op                             */
    TT_CFGE_KEY = 2,                /* unknown key                            */
    TT_CFGE_RANGE = 3,              /* value out of range; nothing changed    */
    TT_CFGE_BUSY = 4,               /* not while the bridge may be on         */
    TT_CFGE_READONLY = 5,
    TT_CFGE_FLASH = 6,              /* the save failed                        */
    TT_CFGE_ALIGN = 7               /* the rotor did not follow the alignment:
                                       stuck, no encoder, or wrong pole pairs */
};
typedef struct {
    uint8_t  op;                    /* TT_CFG_*; a reply sets TT_CFG_ERR on error */
    uint8_t  tag;                   /* echoed, to match replies to requests   */
    uint16_t key;                   /* a TT_NCFG_* key (node/tt_node.h)       */
    uint32_t value;                 /* raw 32 bits: a float's bits or an int  */
} TtCanCfg;

/* ---- DEBUG: up to 15 floats of a node trace (the V2 motor bench) ------- */
typedef struct {
    uint16_t seq;
    uint8_t  set;                   /* which trace (node-defined)             */
    uint8_t  n;                     /* floats that follow, <= 15              */
    float    v[15];
} TtCanDebug;

/* ---- BOOT: who a node is; at power-up and on TT_CFG_HELLO -------------- */
typedef struct {
    uint8_t  proto;                 /* TT_CAN_PROTO_VERSION it speaks         */
    uint8_t  node;                  /* from its jumpers                       */
    uint8_t  hw_rev;
    uint8_t  reset_cause;           /* MCU-defined                            */
    uint32_t fw_id;                 /* build identity (e.g. a git hash)       */
    uint32_t cfg_hash;              /* tt_node_cfg_hash() of its config       */
    uint8_t  state;                 /* TT_NODE_*                              */
    uint8_t  cal_valid;             /* encoder offset known                   */
    uint16_t enc_cpr;
} TtCanBoot;

/* ---- pack / unpack -------------------------------------------------------
 * pack: fills f->id, f->len and f->data, and sets f->brs = 1 (a bring-up
 * HAL running without bit-rate switching ignores it).
 * unpack: 0 on success, -1 if the identifier or length is wrong. */
void tt_can_pack_estop (TtCanFrame *f, const TtCanEstop *e);
int  tt_can_get_estop  (const TtCanFrame *f, TtCanEstop *e);
void tt_can_pack_cmd   (TtCanFrame *f, const TtCanCmd *c);
int  tt_can_get_cmd    (const TtCanFrame *f, TtCanCmd *c);
void tt_can_pack_status(TtCanFrame *f, uint8_t node, const TtCanStatus *s);
int  tt_can_get_status (const TtCanFrame *f, TtCanStatus *s);
void tt_can_pack_cfg   (TtCanFrame *f, uint32_t base_id, uint8_t node, const TtCanCfg *c);
int  tt_can_get_cfg    (const TtCanFrame *f, TtCanCfg *c);
void tt_can_pack_debug (TtCanFrame *f, uint8_t node, const TtCanDebug *d);
int  tt_can_get_debug  (const TtCanFrame *f, TtCanDebug *d);
void tt_can_pack_boot  (TtCanFrame *f, uint8_t node, const TtCanBoot *b);
int  tt_can_get_boot   (const TtCanFrame *f, TtCanBoot *b);

/* Fixed point: x * scale rounded to the nearest integer and clamped to
 * the int16 range; NaN -> 0. */
int16_t tt_can_q16(float x, float scale);

/* ---- CAN-FD lengths and bus timing ------------------------------------- */

/* The data length a DLC code means (0..15), and the smallest valid CAN-FD
 * length that holds n bytes (n > 64 -> 0). */
uint8_t tt_can_dlc_to_len(uint8_t dlc);
uint8_t tt_can_len_round(size_t n);

/* Worst-case length of one FD frame with an 11-bit identifier and `len`
 * data bytes: the bits sent at the nominal (arbitration) rate and at the
 * data rate. Worst-case dynamic stuffing (one bit per four after the
 * first), the fixed stuff bits of the FD CRC field, and 3 bits of
 * interframe space. Without BRS every bit is at the nominal rate: add the
 * two. */
void  tt_can_frame_bits(uint8_t len, uint32_t *nominal_bits, uint32_t *data_bits);

/* Worst-case time on the bus of one frame, microseconds. data_bps is
 * ignored when brs = 0. */
float tt_can_frame_us(uint8_t len, int brs, float nominal_bps, float data_bps);

#ifdef __cplusplus
}
#endif

#endif /* TT_CAN_H */
