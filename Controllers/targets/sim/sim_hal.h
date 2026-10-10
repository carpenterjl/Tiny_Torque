/*
 * sim_hal.h — the Unity host's ABI v7 frames as a board (FW-02, HIL-01).
 *
 * The part of the sim target that plays the HAL: it binds the manifest's
 * sensors and drives by TYPE and WHEEL INDEX (ABI-02), turns each ctrl_step
 * input block into a TtMeas, and puts a TtAct back into actuator slots. It
 * knows nothing of any firmware — no parameters, no mission — so the same
 * code serves
 *
 *   - a controller DLL, which runs the firmware in-process
 *     (targets/sim/opus_main.c), and
 *   - the lockstep bridge (tools/tt_bridge.c), which sends the TtMeas over
 *     the wire to a board or a host-built firmware image and turns the TtAct
 *     that comes back into the same actuator slots.
 *
 * What the firmware needs to know about the car (TtCarDesc) comes out of
 * configure; the firmware decides whether it matches.
 */
#ifndef SIM_HAL_H
#define SIM_HAL_H

#include "controller_api.h"
#include "tt_types.h"
#include "tt_board.h"

typedef struct {
    int     bound;
    int     idx;            /* manifest index, for the stamp */
    int     off;            /* sensor_data offset of the tick register */
    float   wrap;
    float   rad_per_count;  /* true geometry, for the brushed drive's speed */
    int32_t cum;            /* unwrapped count */
    int     prev_raw, has_prev;
    float   omega_wheel;    /* rad/s from the last count delta */
} SimEncBind;

typedef struct {
    int   bound;
    int   idx, off;         /* SENSOR_MOTOR [V, I, torque] or
                               SENSOR_FOC_FB [Iq, Id, w_m, Vbus, T, faults] */
    int   foc;
    int   slot;             /* actuator index */
    float gear;
} SimMotBind;

typedef struct {
    SimEncBind enc[TT_MAX_WHEELS];
    SimMotBind mot[TT_MAX_WHEELS];
    int   tof_idx, tof_off;
    float tof_max;
    int   batt_idx, batt_off;
    int   rc_idx, rc_off;
    int   uwb_idx, uwb_off;
    uint32_t prev_us;
    int   has_prev_us;
} SimHal;

/* Power-on: nothing bound. */
void sim_hal_init(SimHal *h);

/* Restart the dt clock (ctrl_init, ctrl_reset): the next frame's dt is the
 * host's nominal one. */
void sim_hal_restart_clock(SimHal *h);

/* Bind the manifest and describe the car to the firmware. */
void sim_hal_configure(SimHal *h, const SensorInfo2 *sensors, int count, TtCarDesc *car);

/* One input block -> TtMeas. */
void sim_hal_read(SimHal *h, const CtrlInputs *in, TtMeas *m);

/* TtAct -> actuator slots. The caller zeroes `out` first. */
void sim_hal_write(const SimHal *h, const TtAct *a, CtrlOutputs *out);

#endif /* SIM_HAL_H */
