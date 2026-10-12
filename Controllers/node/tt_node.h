/*
 * tt_node.h — the wheel-motor node's firmware, minus its HAL (HW_03_NODE).
 *
 * One node per wheel: a G431-class MCU, a three-phase bridge, three phase
 * shunts, a motor-shaft encoder and a CAN-FD port (frames in core/tt_can.h).
 * This module is everything on the node that is not register-level:
 *
 *   - the current loop (foc/foc.h), in tt_node_isr() at the PWM rate;
 *   - the supervisor, in tt_node_tick() at 1 kHz: command watchdog, bus
 *     over/under-voltage, regen derate near the over-voltage trip, thermal
 *     derate, motoring and regen current limits, latched trips, ESTOP;
 *   - the bus protocol: CMD, ESTOP, CFG in (tt_node_rx), STATUS, BOOT and
 *     CFG replies out (tt_node_tx);
 *   - the encoder alignment, which also finds the encoder's direction and
 *     checks the pole-pair count.
 *
 * The supervisor's rules are the sim's FOC driver model's
 * (UnitySim/Assets/Scripts/Sensors/MotorPart.cs, StepFoc), with the same
 * numbers by default (the "Opus Vector FOC" preset): the twin already drives
 * on the limits and trips this node will have. Change one and change the
 * other.
 *
 * THE HAL (on the MCU, written against the dev board first, NODE-06):
 *   PWM ISR (ADC injected end of conversion, 20 kHz):
 *       on = tt_node_isr(&node, i_abc, vbus, timer_count, duty);
 *       write duty; outputs enabled only while `on`
 *   1 kHz:      fill TtNodeMeas, tt_node_tick(&node, &meas);
 *               while (tt_node_tx(&node, &f)) queue f on FDCAN
 *   FDCAN RX:   tt_node_rx(&node, &f, rx_timestamp)
 *   Over-current comparator -> TIM1 BKIN (the bridge goes off in hardware);
 *   report it in TtNodeMeas.oc_trip. DRV_EN -> BKIN2 and the gate driver's
 *   enable; report its level in TtNodeMeas.hw_enable.
 * tt_node_tick, tt_node_rx and tt_node_tx run in one context (the main loop
 * or one interrupt priority); tt_node_isr preempts them. What they share is
 * single 32-bit words, and the FOC is re-initialised only while the bridge
 * is off.
 *
 * FRAMES. Motor frame: positive is A -> B -> C rotation and positive Iq.
 * Wheel frame (the bus): positive drives the car forward. cfg.dir maps one
 * to the other; cfg.enc_dir makes the encoder count up with positive
 * rotation (found by the alignment).
 *
 * Portable C11, float32, no allocation. Host-tested on the PMSM plant with
 * MAIN's end of the bus (tests/test_node.c).
 */
#ifndef TT_NODE_H
#define TT_NODE_H

#include <stdint.h>
#include "foc.h"
#include "tt_can.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_NODE_CFG_VERSION 1u

/* The node's configuration: flash, and the bus's CFG keys. All fields are
 * 32-bit and the key of a field is its index, so append only. */
typedef struct {
    uint32_t version;        /*  0 TT_NODE_CFG_VERSION (read-only)           */
    int32_t  dir;            /*  1 +1/-1: motor rotation that drives forward */
    int32_t  enc_dir;        /*  2 +1/-1: encoder counts up with + rotation  */
    uint32_t pole_pairs;     /*  3                                           */
    float    r_ohm;          /*  4 phase resistance                          */
    float    ld_h;           /*  5                                           */
    float    lq_h;           /*  6                                           */
    float    flux_wb;        /*  7 = kt / (1.5 p)                            */
    float    f_pwm_hz;       /*  8 = the ISR rate                            */
    float    bw_current_hz;  /*  9                                           */
    float    bw_pll_hz;      /* 10                                           */
    float    mod_max;        /* 11                                           */
    float    delay_periods;  /* 12                                           */
    uint32_t ff_enable;      /* 13                                           */
    uint32_t enc_cpr;        /* 14 counts per motor turn                     */
    float    enc_offset_e;   /* 15 from the alignment                        */
    uint32_t cal_valid;      /* 16 enc_dir and enc_offset_e are measured     */
    float    i_drive_max_a;  /* 17 motoring Iq limit                         */
    float    i_regen_max_a;  /* 18 braking Iq limit                          */
    float    i_trip_a;       /* 19 the hardware comparator (the HAL sets it) */
    float    ov_trip_v;      /* 20 bridge off at or above                    */
    float    ov_derate_v;    /* 21 regen fades from here; the trip clears below */
    float    uv_off_v;       /* 22 bridge off below                          */
    float    uv_on_v;        /* 23 ... until back above                      */
    float    temp_derate_c;  /* 24 limits derate linearly from here ...      */
    float    temp_limit_c;   /* 25 ... to zero here                          */
    float    cmd_timeout_s;  /* 26 no CMD this long: bridge off, setpoint gone */
    uint32_t status_div;     /* 27 a STATUS every this many ticks            */
    float    align_i_a;      /* 28 alignment current                         */
    float    align_s;        /* 29 alignment duration                        */
    float    vbus_filter_s;  /* 30 bus filter for the regen derate           */
} TtNodeCfg;

#define TT_NCFG_COUNT 31u

void     tt_node_cfg_default(TtNodeCfg *c);
/* 0 when usable, else the number of fields or relations that are not. */
int      tt_node_cfg_validate(const TtNodeCfg *c);
uint32_t tt_node_cfg_hash(const TtNodeCfg *c);
/* 0, or a TT_CFGE_* code. set checks the field's own range only;
 * relations between fields are tt_node_cfg_validate's. */
int      tt_node_cfg_get(const TtNodeCfg *c, uint16_t key, uint32_t *value);
int      tt_node_cfg_set(TtNodeCfg *c, uint16_t key, uint32_t value);

/* What the HAL measured, for one 1 kHz tick. */
typedef struct {
    tt_us_t now_us;
    float   vbus_v;          /* instantaneous                                */
    float   t_fet_c;         /* NaN = no sensor                              */
    float   t_wind_c;        /* NaN = no sensor (NODE-04)                    */
    int32_t enc_raw;         /* the encoder timer, unwrapped, as it counts   */
    uint8_t hw_enable;       /* DRV_EN level: 0 = hardware holds the bridge off */
    uint8_t oc_trip;         /* the over-current comparator fired since the
                                last tick                                    */
    uint8_t enc_fault;       /* the encoder reports a fault (no magnet, the
                                SPI angle disagrees with the count)          */
    uint8_t pad_;
} TtNodeMeas;

#define TT_NODE_TXQ 8u

typedef struct {
    TtNodeCfg cfg;
    int       cfg_ok;
    uint8_t   id, hw_rev, reset_cause, state;
    uint32_t  fw_id;
    int     (*save)(const TtNodeCfg *c, void *user);   /* flash; NULL = none */
    void     *save_user;

    /* the command */
    uint8_t   have_cmd, enable_bit, estop, pad0_;
    uint16_t  cmd_seq, latched;     /* latched: TT_DRV_OVERCURRENT | SENSOR  */
    tt_us_t   cmd_rx_us;
    float     iq_cmd_a;             /* wheel frame                            */
    uint32_t  estop_reason;

    /* supervisor */
    uint8_t   ov, uv, first_tick, pad1_;
    uint16_t  fault;                /* what the last STATUS said              */
    uint16_t  pad2_;
    float     vbus_filt;
    tt_us_t   last_tick_us;
    uint32_t  ticks;

    /* alignment */
    float     align_t, align_still;
    int32_t   align_c[3], align_ref;
    uint8_t   align_tag, align_phase, pad3_[2];

    /* to and from the ISR */
    FocState  foc;
    int       foc_ok;
    volatile uint8_t bridge_on, force_angle, v_sat, pad4_;
    volatile float   id_ref_a, iq_ref_a, theta_force;
    FocOut    last;                 /* the ISR's latest output (motor frame) */

    /* transmit queue */
    TtCanFrame txq[TT_NODE_TXQ];
    uint8_t   tx_head, tx_n, status_seq, pad5_;
    uint32_t  tx_drops, rx_bad;
} TtNode;

/* Returns 0, or -1 if the configuration is unusable (the node then stays in
 * TT_NODE_INIT, reports TT_DRV_CONFIG, and accepts CFG to fix it). Queues a
 * BOOT frame either way. */
int  tt_node_init(TtNode *n, const TtNodeCfg *cfg, uint8_t node_id, uint32_t fw_id,
                  uint8_t hw_rev, uint8_t reset_cause);

void tt_node_rx(TtNode *n, const TtCanFrame *f, tt_us_t rx_us);
void tt_node_tick(TtNode *n, const TtNodeMeas *m);

/* One PWM period: phase currents (A, + into the motor), bus voltage, the
 * encoder timer. Writes three duties; returns 1 if the bridge's outputs may
 * be on. */
int  tt_node_isr(TtNode *n, const float i_abc[3], float vbus_v, int32_t enc_raw,
                 float duty[3]);

/* The next frame to send; 0 when there is none. */
int  tt_node_tx(TtNode *n, TtCanFrame *f);

#ifdef __cplusplus
}
#endif

#endif /* TT_NODE_H */
