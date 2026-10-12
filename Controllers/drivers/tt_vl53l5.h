/*
 * tt_vl53l5.h — VL53L5CX results -> TtTof (HW_05 SAT-TOF, SEN-03).
 *
 * The VL53L5CX is driven by ST's ULD driver (firmware upload, ranging
 * start/stop, the results frame): that part stays ST's. What has to match
 * the twin is the frame it produces, which this does:
 *
 *   - zones in the twin's order (SENSOR_TOF_MZ): zone = row * N + col, row 0
 *     at the top, col 0 at the left, looking along the aim;
 *   - TtTof's status convention: 0 = a valid range, anything else = no
 *     usable target (1 none, 2 a low-confidence range, 3 other), with
 *     range_m = range_max when there is no target, as the twin reports.
 *
 * The ULD's zone order relative to the field of view depends on how the
 * part sits on the board; `rot` (quarter turns clockwise) and `mirror`
 * (left-right, applied first) map it. The default (mirror = 1) is the
 * reading of ST's examples [D]; confirm at bring-up by covering one corner
 * of the field of view (SAT-TOF test list).
 *
 * Target status, from ST's ULD documentation [D]: 5 = valid; 9 = valid
 * with a large pulse (merged targets); 6, 10, 12 = range with ~50 %
 * confidence; anything else, or no target detected, is not a range.
 *
 * Portable C11, no allocation.
 */
#ifndef TT_VL53L5_H
#define TT_VL53L5_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint8_t width;          /* 4 (16 zones) or 8 (64 zones)                   */
    uint8_t rot;            /* quarter turns clockwise, 0..3                  */
    uint8_t mirror;         /* left-right, before the rotation                */
    uint8_t weak_ok;        /* 1 = report status 6/10/12 ranges as valid      */
    float   range_max_m;    /* reported when there is no target               */
} TtVl53Cfg;

/* 8 x 8, mirrored, strict, 4.0 m. */
void tt_vl53_default_cfg(TtVl53Cfg *c);

/* One results frame (the ULD's distance_mm, target_status and
 * nb_target_detected arrays, first target per zone) -> TtTof. Leaves the
 * stamp to the caller. */
void tt_vl53_to_tof(const int16_t *distance_mm, const uint8_t *target_status,
                    const uint8_t *nb_target, const TtVl53Cfg *c, TtTof *out);

#ifdef __cplusplus
}
#endif

#endif /* TT_VL53L5_H */
