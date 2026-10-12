/*
 * tt_soc.h — pack state of charge from TtBatt (SEN-09, firmware side).
 *
 * The power board's INA228 gives pack voltage and current (+ = discharge,
 * as the twin's battery sensor). Two estimates, combined:
 *
 *   - coulomb counting: exact over minutes, but it starts from a guess and
 *     drifts with the current sensor's offset;
 *   - the open-circuit voltage: V + I R through the cell's OCV(SoC) curve.
 *     Right only once the pack has rested (the electronics' ~0.7 A never
 *     stops, so "rest" is a small, steady current), and coarse on the
 *     curve's flat plateau.
 *
 * At power-up the OCV sets the start; afterwards the count runs and, while
 * the pack rests, is pulled toward the OCV estimate with time constant
 * tau_s. The OCV curve is the twin's (CarVehicle.CellOcv: 3.00 / 3.40 /
 * 3.65 / 4.05 / 4.20 V at 0 / 5 / 20 / 90 / 100 %) [E]: replace both with a
 * measured curve of the real pack.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_SOC_H
#define TT_SOC_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    float    capacity_ah;      /* 5.2                                         */
    uint32_t cells;            /* 2                                           */
    float    r_int_ohm;        /* whole pack, 0.020                           */
    float    i_rest_a;         /* |I| below this counts as resting ...        */
    float    di_rest_a;        /* ... if it also moves less than this ...     */
    float    t_rest_s;         /* ... for this long                           */
    float    tau_s;            /* pull toward the OCV estimate while resting  */
} TtSocCfg;

typedef struct {
    TtSocCfg cfg;
    float    soc;              /* 0..1                                        */
    float    soc_ocv;          /* the OCV estimate this tick                  */
    float    rest_s;           /* how long the pack has rested                */
    float    i_ref;            /* the current the rest started at             */
    uint32_t seq;              /* last TtBatt sample used                     */
    uint8_t  started, resting, pad_[2];
    tt_us_t  t_last;
} TtSoc;

/* The twin's pack: 2S 5200 mAh, 20 mOhm; rest = under 1.5 A, within 0.2 A,
 * for 2 s; tau 30 s. */
void  tt_soc_default_cfg(TtSocCfg *c);
void  tt_soc_init(TtSoc *s, const TtSocCfg *c);

/* Per-cell open-circuit voltage at a state of charge, and its inverse. */
float tt_soc_cell_ocv(float soc);
float tt_soc_from_cell_ocv(float v_cell);

/* One battery sample (only fresh ones are used). Returns the SoC. */
float tt_soc_update(TtSoc *s, const TtBatt *b);

/* Amp-hours left above `soc_floor` (e.g. the firmware's cut-off). */
float tt_soc_remaining_ah(const TtSoc *s, float soc_floor);

#ifdef __cplusplus
}
#endif

#endif /* TT_SOC_H */
