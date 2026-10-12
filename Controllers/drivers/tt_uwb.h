/*
 * tt_uwb.h — UWB two-way ranging maths, DW3000 class (HW_04_MAST, SEN-05).
 *
 * The tag on the mast ranges to each anchor in turn (TDMA, 40 ranges/s over
 * four in the twin). The radio exchange and the register access are the
 * HAL's (Qorvo's driver); what turns its timestamps and diagnostics into
 * the twin's TtUwb is here:
 *
 *   - 40-bit device timestamps (15.65 ps, wrapping every ~17.2 s);
 *   - double-sided TWR (three messages, Neirynck's asymmetric form: the
 *     clock offset of both ends cancels to first order) and single-sided
 *     TWR corrected by the measured carrier offset;
 *   - the first-path-to-total power gap the twin reports as quality
 *     (~2 dB in line of sight, > 6 dB suggests NLOS).
 *
 *   DS-TWR, initiator I, responder R:
 *       I: poll ---------> R           round1 = I(rx resp)  - I(tx poll)
 *       I <---------- resp  R          reply1 = R(tx resp)  - R(rx poll)
 *       I: final --------> R           round2 = R(rx final) - R(tx resp)
 *                                      reply2 = I(tx final) - I(rx resp)
 *       tof = (round1 round2 - reply1 reply2) / (round1 + round2 + reply1 + reply2)
 *
 * The products are formed in 64-bit integers, so no tick is lost to float
 * rounding (a float holds only ~24 bits: 4 ticks = 2 cm at 1 ms intervals).
 * Antenna delays belong in the radio's delay registers; bias_m is what is
 * left after the anchor survey. Constants [D]: check against the DW3000
 * user manual.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_UWB_H
#define TT_UWB_H

#include <stdint.h>
#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_DW_TS_MASK      0xFFFFFFFFFFull     /* 40 bits                     */
#define TT_DW_M_PER_TICK   0.0046903559f       /* c_air / (128 * 499.2 MHz)   */
#define TT_DW_MAX_INTERVAL 0x7FFFFFFFull       /* ~34 ms: longer is an error  */

/* later - earlier on the 40-bit clock. */
uint64_t tt_dw_ts_diff(uint64_t later, uint64_t earlier);

/* Time of flight in ticks; NaN if an interval is too long or the sum is 0. */
float tt_dw_dstwr_ticks(uint64_t round1, uint64_t reply1, uint64_t round2, uint64_t reply2);

/* Single-sided: clock_offset is the responder's clock relative to the
 * initiator's ((f_R - f_I) / f_I, + = the responder runs fast), as the
 * initiator measures it from the carrier. */
float tt_dw_sstwr_ticks(uint64_t round, uint64_t reply, float clock_offset);

float tt_dw_range_m(float tof_ticks, float bias_m);

/* The first-path-to-total power gap, dB, from the receive diagnostics:
 * cir_pwr (C), and the first-path amplitudes F1..F3. The preamble count and
 * the receiver constant cancel in the difference. Returns 99 if the first
 * path is missing. */
float tt_dw_fp_gap_db(float cir_pwr, float f1, float f2, float f3);

/* A range -> TtUwb (stamp left to the caller). */
void tt_uwb_fill(TtUwb *u, uint8_t anchor, float range_m, float gap_db,
                 const float anchor_pos_m[3]);

#ifdef __cplusplus
}
#endif

#endif /* TT_UWB_H */
