/*
 * tt_detour.h — a detour beside the route, and the planner that picks one
 * (NAV-08, L5 advanced).
 *
 * A detour is a lateral offset profile d(s) along the route: from the
 * offset the car has (d0, usually 0) at s0 it eases out to d1 by s1 along
 * a raised cosine, holds d1 to s2, and eases back to the route by s3. The
 * tracker (tt_track.h) follows the offset path — the route's point moved
 * d(s) along its normal, with the heading and curvature that go with it —
 * so the yaw-rate loop below it never knows the car left the route.
 *
 * The planner is a lattice, not a graph search: a few offsets either side
 * of the route, tried from the nearest out, on the side with more room
 * first (away from the centroid of what blocks the route; a replan keeps
 * the side it is on). A candidate passes when every point of its path,
 * sampled every `step_m` from the car to the end of the ramp back, has no
 * occupied cell of the grid (tt_grid.h) within `half_width_m`, and when it
 * keeps `unknown_w_m` more from that centroid: the map holds a few returns
 * when the block is first seen 3 m out, and what they belong to is wider
 * than they are. The hold lasts until the route itself is free again,
 * plus `past_m` for the car's tail; the ease-out is as long as the room
 * before the block allows, the ease-back `ramp_out_m` (the car slows for
 * its curvature: the speed cap is the caller's, from kappa_peak), and a
 * ramp too tight for the steering lock (the peak curvature of a raised
 * cosine of height D over length L is pi^2 D / (2 L^2)) is not offered —
 * the car then holds short of the block as it did before (NAV-05).
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_DETOUR_H
#define TT_DETOUR_H

#include <stdint.h>
#include "tt_route.h"
#include "tt_grid.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint8_t active;
    uint8_t pad_[3];
    float   s0, s1, s2, s3;    /* ease out [s0, s1], hold [s1, s2], ease back [s2, s3] */
    float   d0, d1;            /* offset at s0 and the held offset, m, + = left */
    float   kappa_peak;        /* the most curvature the profile adds, 1/m    */
} TtDetour;

/* d(s); also its slope and curvature along s when asked (may be NULL).
 * Before s0: d0; after s3 (or inactive): 0. */
float tt_detour_at(const TtDetour *d, float s, float *dd, float *ddd);

/* The offset path at s: the route's point moved d along its left normal,
 * the heading turned by the slope, the curvature of the offset curve
 * ((kappa + d'') / (1 - d kappa)). `d` NULL or inactive gives the route.
 * `hint` as tt_route_at. */
void tt_detour_point(const TtRoute *r, const TtDetour *d, float s, uint32_t *hint,
                     float *x, float *y, float *psi, float *kappa);

typedef struct {
    float half_width_m;    /* the disc each path point must keep clear       */
    float unknown_w_m;     /* ...and this much more from the block's centroid */
    int   min_cells;       /* no plan on fewer occupied cells about the block */
    float lead_m, lead_s;  /* the ease-out starts lead_m + v lead_s ahead     */
    float ramp_m;          /* the ease-out's length when there is room       */
    float ramp_min_m;      /* ...and the least it may be shortened to        */
    float ramp_out_m;      /* the ease-back's length                         */
    float reach_m;         /* the ease-out must be done this far before the block */
    float past_m;          /* hold this far past the last blocked route point */
    float look_m;          /* how far ahead the path is checked              */
    float step_m;          /* sampling along a path                          */
    float kappa_max;       /* a ramp needing more is not offered             */
    float offset_m[6];     /* |d1| candidates, tried in this order           */
    int   n_offsets;
} TtDetourCfg;

void tt_detour_default_cfg(TtDetourCfg *c);

/* The route (with the detour, when `d` is active) from s over `look` m:
 * the arc length of the first point with an occupied cell within
 * half_width_m, or -1 when it is free. *y_block (may be NULL) gets the
 * centroid of those cells as an offset from the route, + = left. */
float tt_detour_blocked(const TtDetourCfg *c, const TtGrid *g, const TtRoute *r,
                        const TtDetour *d, float s, float look, float *y_block);

/* Plan a detour for the car at s doing v m/s, now offset d_now from the
 * route, around what blocks the route at s_block with its centroid
 * y_block left of the route. 1 and `out` filled when a candidate passes;
 * 0 when none does (hold short, as NAV-05 does). */
int tt_detour_plan(const TtDetourCfg *c, const TtGrid *g, const TtRoute *r,
                   float s, float v, float d_now, float s_block, float y_block,
                   TtDetour *out);

#ifdef __cplusplus
}
#endif

#endif /* TT_DETOUR_H */
