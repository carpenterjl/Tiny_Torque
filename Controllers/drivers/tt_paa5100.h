/*
 * tt_paa5100.h — PAA5100JE motion burst -> TtFlow (HW_05 SAT-FLOW, SEN-04).
 *
 * MAIN reads the motion burst register (0x16) once per control tick; its 12
 * bytes are, as for the PMW3901 [D]:
 *     0 motion (bit 7: motion since the last read)   1 observation
 *     2..3 delta X (int16 LE)   4..5 delta Y (int16 LE)   6 squal
 *     7 raw data sum   8 max raw   9 min raw   10..11 shutter (upper, lower)
 * The chip's long initialisation sequence (PixArt's register settings) is
 * the HAL's.
 *
 * TtFlow, as the twin's FlowSensor defines the reading (SENSOR_FLOW):
 *     flow_x, flow_y  image motion, rad/s, x forward and y left with the
 *                     part aimed down and its "up" to the nose, with the
 *                     vehicle-motion sign: forward driving reads +x.
 *                     m = v / h + w x aim, so the firmware removes rotation
 *                     with the gyro (v = h (m - w x aim)).
 *     quality         squal / 255; 0 = not tracking
 *     height_m        the configured height above the floor
 * counts_per_rad defaults to the twin's 47.7 [E]; calibrate it (SEN-04)
 * and change both. `axis` maps the chip's delta X/Y to image x/y with sign
 * (+-1 = chip X, +-2 = chip Y), for how the part sits on the board.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_PAA5100_H
#define TT_PAA5100_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_PAA_BURST 12u

typedef struct {
    uint8_t  motion;
    int16_t  dx, dy;           /* counts since the last read, chip axes     */
    uint8_t  squal;
    uint8_t  raw_sum, raw_max, raw_min;
    uint16_t shutter;
} TtPaaBurst;

typedef struct {
    int8_t   axis[2];          /* image x, y <- +-1 (chip X) / +-2 (chip Y)  */
    uint8_t  squal_min;        /* below: not tracking                        */
    uint8_t  pad_;
    uint16_t shutter_max;      /* above: too dark to trust (shutter maxed)   */
    uint16_t pad2_;
    float    counts_per_rad;
    float    height_m;
} TtPaaCfg;

/* Identity axes, squal >= 25, the twin's 47.7 counts/rad at 25 mm. */
void tt_paa_default_cfg(TtPaaCfg *c);
void tt_paa_burst_decode(const uint8_t b[TT_PAA_BURST], TtPaaBurst *o);
/* counts over dt_s -> TtFlow (stamp left to the caller). */
void tt_paa_to_flow(const TtPaaBurst *b, float dt_s, const TtPaaCfg *c, TtFlow *f);

#ifdef __cplusplus
}
#endif

#endif /* TT_PAA5100_H */
