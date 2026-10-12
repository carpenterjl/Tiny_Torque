/*
 * tt_drvbus.h — MAIN's end of the drive bus (SYS-03; frames in tt_can.h).
 *
 * The vehicle firmware writes Iq per wheel (TtAct.drive on an FOC car) and
 * reads TtDriveFb + TtEnc per wheel (TtMeas). On the car those cross the
 * CAN-FD bus to the four wheel nodes; this module is the translation, so
 * the board code only moves frames between it and the FDCAN peripheral:
 *
 *     every 1 ms:   tt_drvbus_cmd(&bus, iq, enable, flags, now, &f);  send f
 *     on receive:   tt_drvbus_rx(&bus, &f, rx_time, &meas);
 *     before arm:   tt_drvbus_not_ready(&bus, mask, now) == 0
 *
 * TIMESTAMPS. A STATUS names the command it last obeyed and how long after
 * that command's arrival it was sampled. tt_drvbus remembers when it queued
 * each command, so the sample lands on MAIN's clock at
 *
 *     t_sent(cmd_seq) + tx_latency_us + age_us
 *
 * tx_latency_us is the CMD frame's own time on the bus (the node timestamps
 * its reception at the end of the frame). If the FDCAN driver reports when
 * a command actually left (its TX event FIFO), pass that through
 * tt_drvbus_sent() and arbitration delays drop out too. A STATUS whose
 * command is too old to remember is stamped with its arrival time instead.
 *
 * Portable C11, no allocation.
 */
#ifndef TT_DRVBUS_H
#define TT_DRVBUS_H

#include <stdint.h>
#include "tt_can.h"
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_DRVBUS_HIST 32u          /* commands remembered for timestamps    */

typedef struct {
    uint8_t     booted;             /* a BOOT has arrived                    */
    uint8_t     have_status;
    uint8_t     last_seq;           /* the node's STATUS counter             */
    uint8_t     cfg_new;            /* a CFG reply arrived; cleared by reader */
    TtCanBoot   boot;
    TtCanStatus status;             /* the last one, decoded                 */
    TtCanCfg    cfg_rsp;            /* the last CFG reply                    */
    tt_us_t     last_rx_us;
    uint32_t    n_status, n_boot, seq_gaps;
} TtDrvNode;

typedef struct {
    uint16_t  seq;                  /* of the next CMD                       */
    uint16_t  hist_seq[TT_DRVBUS_HIST];
    tt_us_t   hist_us[TT_DRVBUS_HIST];
    uint8_t   hist_ok[TT_DRVBUS_HIST];
    float     tx_latency_us;
    uint32_t  expect_cfg_hash[TT_CAN_NODES]; /* 0 = any                     */
    TtDrvNode node[TT_CAN_NODES];
    uint32_t  rx_bad, rx_other;
} TtDrvBus;

/* tx_latency_us: the CMD frame's time on the bus at the running bit rates
 * (tt_can_frame_us(TT_CAN_LEN_CMD, ...)). */
void tt_drvbus_init(TtDrvBus *b, float tx_latency_us);

/* The next command frame. iq_a in the wheel frame (+ = forward), as
 * TtAct.drive holds it for a TT_DRV_IQ wheel; enable bit n lets node n
 * drive; flags TT_CAN_CMD_*. */
void tt_drvbus_cmd(TtDrvBus *b, const float iq_a[TT_CAN_NODES], uint8_t enable,
                   uint8_t flags, tt_us_t now_us, TtCanFrame *f);

/* The FDCAN driver's transmit time for command `seq`, if it has one. */
void tt_drvbus_sent(TtDrvBus *b, uint16_t seq, tt_us_t tx_us);

void tt_drvbus_estop(uint32_t reason, TtCanFrame *f);

/* One received frame. A STATUS fills m->drv[n] and m->enc[n] with fresh
 * stamps (m may be NULL to only track the node). Returns the frame's kind
 * (TT_CAN_K_*), or -1 if it was malformed. */
int  tt_drvbus_rx(TtDrvBus *b, const TtCanFrame *f, tt_us_t rx_us, TtMeas *m);

/* The nodes in `mask` that cannot drive yet: no BOOT, another protocol
 * version, an unexpected configuration hash, encoder not calibrated, or no
 * STATUS within stale_us. Returns a bit per such node; 0 = ready to arm. */
uint8_t tt_drvbus_not_ready(const TtDrvBus *b, uint8_t mask, tt_us_t now_us,
                            uint32_t stale_us);

#ifdef __cplusplus
}
#endif

#endif /* TT_DRVBUS_H */
