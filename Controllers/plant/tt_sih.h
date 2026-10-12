/*
 * tt_sih.h — a reduced car the firmware can run against on its own board
 * (HIL-06, PX4's "simulation in hardware" pattern).
 *
 * The lockstep HIL validates the numbers, but the sim is the master clock
 * there, so it says nothing about time. Built into the MCU image behind a
 * build flag, this plant replaces the drivers: each tick the scheduler
 * takes the plant's TtMeas, runs the firmware at its real rate with its
 * real interrupts, and gives the plant the actuator writes. What that
 * measures is the firmware's CPU time per tick, its worst case and its
 * headroom (targets/sih/tt_sih_run.h writes the report).
 *
 * The car: a kinematic bicycle about the chassis origin midway between the
 * axles (so the origin moves at the sideslip atan(tan(delta)/2) to the
 * heading), the parameters' mass and drag, ideal FOC drives (Iq through
 * kt * gear * eta, at most the parameters' torque), each command acting one
 * period late (the timer-latched output), the steering through
 * steer_lag_s. Sensors: encoders on the motor shafts as the parameters
 * count them, the drives' current and speed, a gyro and accelerometer with
 * no error, the pack (a fixed OCV less 20 mOhm), the UWB tag ranging the
 * anchors round-robin at 40 Hz, the steering angle, and an RC receiver
 * with the arm switch on. No tyres and no noise: this is a load, not a
 * model of the car.
 *
 * Portable C11, float32, no allocation: it builds into the MCU image.
 */
#ifndef TT_SIH_H
#define TT_SIH_H

#include <stdint.h>
#include "tt_types.h"
#include "tt_params.h"
#include "tt_board.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_SIH_ANCHORS 4

typedef struct {
    const TtParams *p;
    /* state */
    float    x, y, psi;              /* body origin, site frame          */
    float    v;                      /* origin speed, m/s                */
    float    r;                      /* yaw rate, rad/s                  */
    float    a;                      /* acceleration, m/s^2              */
    float    delta;                  /* road-wheel angle, rad            */
    float    wheel_ang[TT_MAX_WHEELS];
    float    wheel_w[TT_MAX_WHEELS]; /* rad/s                            */
    float    pack_i;                 /* A, + = discharge                 */
    float    t;                      /* s                                */
    TtAct    acting, pending;        /* one period late                  */
    /* sensors */
    float    anchor[TT_SIH_ANCHORS][3];
    float    tag_lever[2], tag_z;
    float    uwb_next, rc_next;
    int      uwb_slot;
    uint32_t seq, uwb_seq, rc_seq;
    TtUwb    uwb_last;
    float    ocv_v, r_int;
} TtSih;

/* Start at (x, y, psi) in the site frame with the proving ground's four
 * anchors (the corners of a 39 x 19 m field, 1.52 m up). */
void tt_sih_init(TtSih *s, const TtParams *p, float x, float y, float psi);

/* The car, as a target describes it to the firmware (opus_app_configure). */
void tt_sih_describe(const TtSih *s, TtCarDesc *car);

/* The sensors at the present state; dt the tick period. */
void tt_sih_meas(TtSih *s, float dt, TtMeas *m);

/* This tick's actuator writes (they act from the next tick), then dt. */
void tt_sih_step(TtSih *s, const TtAct *act, float dt);

#ifdef __cplusplus
}
#endif

#endif /* TT_SIH_H */
