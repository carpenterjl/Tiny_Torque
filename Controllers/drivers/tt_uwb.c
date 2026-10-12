/*
 * tt_uwb.c — UWB two-way ranging maths. See tt_uwb.h.
 */
#include "tt_uwb.h"

#include <math.h>
#include <string.h>

static float nan_(void) { uint32_t u = 0x7FC00000u; float f; memcpy(&f, &u, 4); return f; }

uint64_t tt_dw_ts_diff(uint64_t later, uint64_t earlier)
{
    return (later - earlier) & TT_DW_TS_MASK;
}

float tt_dw_dstwr_ticks(uint64_t round1, uint64_t reply1, uint64_t round2, uint64_t reply2)
{
    int64_t num, den, q, r;
    if (round1 > TT_DW_MAX_INTERVAL || reply1 > TT_DW_MAX_INTERVAL ||
        round2 > TT_DW_MAX_INTERVAL || reply2 > TT_DW_MAX_INTERVAL) return nan_();
    num = (int64_t)(round1 * round2) - (int64_t)(reply1 * reply2);
    den = (int64_t)(round1 + round2 + reply1 + reply2);
    if (den == 0) return nan_();
    q = num / den;
    r = num % den;
    return (float)q + (float)r / (float)den;
}

float tt_dw_sstwr_ticks(uint64_t round, uint64_t reply, float clock_offset)
{
    if (round > TT_DW_MAX_INTERVAL || reply > TT_DW_MAX_INTERVAL) return nan_();
    /* The exact integer difference first; the correction is small. */
    return 0.5f * ((float)((int64_t)round - (int64_t)reply) + (float)reply * clock_offset);
}

float tt_dw_range_m(float tof_ticks, float bias_m)
{
    return tof_ticks * TT_DW_M_PER_TICK - bias_m;
}

float tt_dw_fp_gap_db(float cir_pwr, float f1, float f2, float f3)
{
    float fp = f1 * f1 + f2 * f2 + f3 * f3;
    if (!(fp > 0.0f) || !(cir_pwr > 0.0f)) return 99.0f;
    /* RX = 10 log10(C 2^21 / N^2) - A, FP = 10 log10(fp / N^2) - A. */
    return 10.0f * log10f(cir_pwr * 2097152.0f / fp);
}

void tt_uwb_fill(TtUwb *u, uint8_t anchor, float range_m, float gap_db,
                 const float anchor_pos_m[3])
{
    u->anchor = anchor;
    u->pad_[0] = u->pad_[1] = u->pad_[2] = 0u;
    u->range_m = range_m;
    u->quality = gap_db;
    u->pos_m[0] = anchor_pos_m[0];
    u->pos_m[1] = anchor_pos_m[1];
    u->pos_m[2] = anchor_pos_m[2];
}
