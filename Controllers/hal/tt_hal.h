/*
 * tt_hal.h — the board interface of the portable core (FW-02).
 *
 * Push-style: the per-target SCHEDULER calls these, gathers a TtMeas, calls
 * the core, and writes the TtCmd back. Core logic never calls the HAL; it is
 * a pure function of (state, TtMeas) -> TtCmd, which is what lets the same
 * code run in the simulator, in host replay, and on the MCU.
 *
 *     for (;;) {                                   (MCU: a timer ISR sets a
 *         wait_for_tick();                          flag the main loop polls)
 *         tt_hal_read(&meas);
 *         core_step(&core, &meas, &cmd, &log);
 *         tt_hal_write(&cmd);
 *         tt_hal_watchdog_kick();
 *         tt_hal_log(&log);
 *     }
 *
 * Per target:
 *   targets/sim    — no HAL: the Unity host is the scheduler. ctrl_step() is
 *                    the loop body, and the adapter (opus_main.c) plays the
 *                    part of read/write against the ABI v7 structs.
 *   targets/stm32  — BSP drivers behind these functions (Phase 4+).
 *   targets/hil    — read/write against USB frames from the sim (Phase 4).
 *
 * This header replaces the old hal.h, which nothing ever included.
 */
#ifndef TT_HAL_H
#define TT_HAL_H

#include "tt_types.h"
#include "tt_params.h"
#include "tt_log.h"

#ifdef __cplusplus
extern "C" {
#endif

tt_us_t tt_hal_now_us(void);

/* Snapshot the latest stamped sample of every sensor. 0 = ok. */
int  tt_hal_read(TtMeas *m);

/* Apply a command: wheel torque -> Iq through tt_torque_to_iq() (so every
 * target converts identically), then to the drive's mailbox or CAN frame;
 * steer radians -> servo pulse. 0 = ok. */
int  tt_hal_write(const TtCmd *c);

void tt_hal_watchdog_kick(void);

/* Parameter store: flash copy with version + tt_params_hash(). 0 = ok. */
int  tt_hal_params_load(TtParams *p);
int  tt_hal_params_store(const TtParams *p);

/* One binary log frame per tick; the host decoder turns it into CSV with
 * the same column names the sim writes (FW-09). */
void tt_hal_log(const TtLogFrame *f);

#ifdef __cplusplus
}
#endif

#endif /* TT_HAL_H */
