/*
 * opus_app.h — the whole Opus firmware, as any target runs it.
 *
 * opus_fw.h is the loop body (mission, navigation, safety: TtMeas in, TtCmd
 * out). This wraps it in the two board-facing steps every target needs:
 *
 *   configure   compare the car the target describes (TtCarDesc) with the
 *               firmware's own parameters; a disagreement refuses to arm
 *               (FA_PARAMS), a missing encoder or drive too;
 *   step        run the loop body, then do what the firmware's tt_hal_write()
 *               does on the car: wheel torque -> Iq through tt_torque_to_iq(),
 *               and for a brushed drive behind a hobby ESC, Iq -> the ESC's
 *               volts (a virtual current loop on the motor's true constants).
 *
 * The result is a TtAct — actuator writes in each drive's own unit. Every
 * target calls exactly this:
 *
 *   the sim DLL      targets/sim/opus_main.c   (adapter: targets/sim/sim_hal.c)
 *   lockstep HIL     targets/hil/opus_hil.c    (TtMeas / TtAct over the wire)
 *   host replay      tools/tt_replay.c         (a recorded wire capture)
 *
 * so a board in the loop and the DLL run the same code on the same inputs.
 */
#ifndef OPUS_APP_H
#define OPUS_APP_H

#include "opus_fw.h"
#include "tt_board.h"

/* The UWB tag's height above the floor. The manifest gives the mount relative
 * to the body origin, whose own height it does not state; this is the twin's
 * tag (0.095 m above an origin 0.095 m up), as the replay used. */
#define OPUS_UWB_TAG_Z_M  0.19f

typedef struct {
    OpusFw    fw;
    TtParams  params;      /* the firmware's own copy; fw points into it */
    TtCarDesc car;
    int       cfg_fault;   /* FA_* from configure                         */
    int       ready;
} OpusApp;

/* Power-on with a parameter set (copied) at a nominal tick rate. */
void opus_app_init(OpusApp *a, const TtParams *p, float rate_hz);

/* The car this firmware was put in. NULL = no description (FA_NO_MANIFEST). */
void opus_app_configure(OpusApp *a, const TtCarDesc *car);

/* The car was put back at the start (ctrl_reset). */
void opus_app_reset(OpusApp *a);

/* One tick: TtMeas in, actuator writes out. */
void opus_app_step(OpusApp *a, const TtMeas *m, TtAct *act);

/* The log frame of the last tick (opus_log.def order). */
const float *opus_app_log(const OpusApp *a, uint32_t *n);

/* The log channel names, comma-separated (ctrl_get_debug_names). */
const char *opus_app_log_names(void);

#endif /* OPUS_APP_H */
