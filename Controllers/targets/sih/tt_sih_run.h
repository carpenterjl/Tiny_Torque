/*
 * tt_sih_run.h — the firmware against the on-target plant, timed (HIL-06).
 *
 * The loop a board's scheduler runs with the plant compiled in instead of
 * the drivers: each tick, the plant's TtMeas, the whole firmware
 * (opus_app_step: mission, EKF, safety, torque -> Iq), the actuator writes
 * back into the plant. Every firmware tick is timed with the target's own
 * cycle counter (DWT->CYCCNT on a Cortex-M; a performance counter on a PC),
 * the plant's ticks too, and the report says what the firmware costs:
 * mean, p50, p99 and worst tick, the CPU load at the control rate, the
 * headroom to the period, and where the mission ended.
 *
 * On the board the tick comes from a timer interrupt and the loop's own
 * jitter is the time from that interrupt to the firmware starting:
 * `tick_start` (if set) is called first thing each tick and its lateness
 * is reported too. On a PC the loop simply runs as fast as it can.
 *
 * Portable C11, no allocation: it builds into the MCU image.
 */
#ifndef TT_SIH_RUN_H
#define TT_SIH_RUN_H

#include <stdint.h>
#include "tt_params.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_SIH_HIST_US 4096u     /* the histogram's 1 us bins             */

typedef struct {
    const TtParams *params;
    float     rate_hz;           /* control rate                          */
    float     max_s;             /* stop after this long (sim time)       */
    const char *route;           /* opus_app_set_route spec, NULL = none  */
    /* the target's clock */
    uint32_t (*cycles)(void);    /* a free-running counter                */
    float     cycles_per_us;
    /* on a board: wait for the timer tick, return how late it started
     * (cycles); NULL on a PC */
    uint32_t (*tick_start)(void);
} TtSihCfg;

typedef struct {
    uint32_t ticks;
    float    fw_mean_us, fw_p50_us, fw_p99_us, fw_max_us;
    uint32_t fw_max_tick;         /* the worst tick: its number             */
    int      fw_max_phase;        /* and the mission phase after it         */
    float    plant_mean_us;
    float    load_pct;            /* firmware mean / period               */
    float    headroom_pct;        /* 1 - worst tick / period              */
    float    late_max_us;         /* tick start lateness (board only)     */
    uint32_t over_period;         /* ticks longer than the period         */
    int      phase, fault;        /* where the mission ended              */
    float    x, y, psi, sim_s;    /* the plant at the end                 */
    uint32_t hist[TT_SIH_HIST_US];/* firmware tick time, 1 us bins        */
} TtSihReport;

/* Run to the mission's end (DONE or FAULT) or max_s. 0 if it ran. */
int tt_sih_run(const TtSihCfg *cfg, TtSihReport *rep);

/* The report as text, one line at a time through `out`. */
void tt_sih_print(const TtSihCfg *cfg, const TtSihReport *rep,
                  void (*out)(const char *line, void *ctx), void *ctx);

#ifdef __cplusplus
}
#endif

#endif /* TT_SIH_RUN_H */
