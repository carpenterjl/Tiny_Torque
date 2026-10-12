/*
 * tt_vl53l5.c — VL53L5CX results mapping. See tt_vl53l5.h.
 */
#include "tt_vl53l5.h"

void tt_vl53_default_cfg(TtVl53Cfg *c)
{
    c->width = 8u;
    c->rot = 0u;
    c->mirror = 1u;
    c->weak_ok = 0u;
    c->range_max_m = 4.0f;
}

void tt_vl53_to_tof(const int16_t *distance_mm, const uint8_t *target_status,
                    const uint8_t *nb_target, const TtVl53Cfg *c, TtTof *out)
{
    int w = c->width == 4u ? 4 : 8, r, k;
    out->zones = (uint8_t)(w * w);
    for (r = 0; r < w; r++) {
        for (k = 0; k < w; k++) {
            int src = r * w + k;
            int rr = r, cc = c->mirror ? w - 1 - k : k, q, t;
            uint8_t st = target_status[src], status;
            for (q = 0; q < (int)(c->rot & 3u); q++) {   /* clockwise quarter turn */
                t = rr;
                rr = cc;
                cc = w - 1 - t;
            }
            if (nb_target[src] == 0u) status = 1u;
            else if (st == 5u || st == 9u) status = 0u;
            else if (st == 6u || st == 10u || st == 12u) status = c->weak_ok ? 0u : 2u;
            else status = 3u;
            if (status == 0u && distance_mm[src] < 0) status = 3u;
            out->status[rr * w + cc] = status;
            out->range_m[rr * w + cc] = status == 0u ? (float)distance_mm[src] * 0.001f
                                                     : c->range_max_m;
        }
    }
}
