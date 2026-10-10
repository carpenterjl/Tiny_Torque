/*
 * tt_board.h — what a target tells the firmware about the car, and what the
 * firmware writes to the car's peripherals (HIL-02/03).
 *
 * TtMeas (tt_types.h) is what the firmware reads each tick. These are the
 * other two things that cross the target boundary:
 *
 *   TtCarDesc  once, at configure time: which wheels have an encoder and a
 *              drive, what kind of drive, and the geometry the target can
 *              state. The firmware compares it with its own parameters and
 *              refuses to arm if they disagree (FA_PARAMS), instead of driving
 *              on numbers that are wrong for the car it is in.
 *   TtAct      every tick: the actuator writes, in the units each drive
 *              takes — the end of the firmware's own tt_hal_write(), after
 *              its torque -> Iq conversion.
 *
 * On the car the board config fills TtCarDesc and TtAct goes to the drives'
 * mailboxes and the servo timer. In the sim the adapter fills TtCarDesc from
 * the ABI v7 manifest (targets/sim/sim_hal.c) and turns TtAct into actuator
 * slots. In lockstep HIL both cross the wire (core/tt_wire.h), so the board
 * running the firmware sees exactly what the DLL sees.
 *
 * Plain C11, fixed-width types, no pointers.
 */
#ifndef TT_BOARD_H
#define TT_BOARD_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

/* What a wheel's drive takes. */
enum {
    TT_DRV_NONE  = 0,
    TT_DRV_VOLTS = 1,   /* a brushed motor behind a hobby ESC: volts          */
    TT_DRV_IQ    = 2    /* an FOC driver: torque-producing current, amps      */
};

typedef struct {
    uint8_t present;                 /* 0 = the target could not describe it */
    uint8_t enc_mask;                /* wheels with an encoder               */
    uint8_t drv_mask;                /* wheels with a drive                  */
    uint8_t has_steer_fb;
    uint8_t has_uwb;
    uint8_t pad_[3];
    uint8_t drv_kind[TT_MAX_WHEELS]; /* TT_DRV_*                             */
    float   enc_radius_m[TT_MAX_WHEELS];
    float   enc_counts_per_rev[TT_MAX_WHEELS]; /* of the WHEEL: cpr x gear   */
    float   drv_kt[TT_MAX_WHEELS];   /* the motor's true constants, which a  */
    float   drv_r_ohm[TT_MAX_WHEELS];/* current loop needs for a VOLTS drive */
    float   drv_gear[TT_MAX_WHEELS];
    float   drv_vmax[TT_MAX_WHEELS];
    float   steer_max_rad;           /* full lock as the servo states it     */
    float   uwb_lever_m[3];          /* the UWB tag from the body origin, FLU */
} TtCarDesc;

typedef struct {
    float drive[TT_MAX_WHEELS];      /* per wheel, in its drive's unit       */
    float steer_rad;                 /* front road-wheel angle, + = left     */
    float brake_01;                  /* friction brake, 0..1                 */
} TtAct;

#ifdef __cplusplus
}
#endif

#endif /* TT_BOARD_H */
