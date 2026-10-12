/*
 * tt_vplan.h — the speed plan along a route, layer L5 (NAV-03).
 *
 * For each route point, the fastest the car may be going there:
 *
 *   v_max(s) = min(the route's cap, v_max, sqrt(a_lat / |kappa(s)|))
 *
 * then a backward pass limited by a_brake (it can always slow down for what
 * is coming) and a forward pass limited by a_drive (it cannot be faster
 * than it could have accelerated to). The same two-pass idea as the sim's
 * RaceLineSolver, in C over the route table. The start of the route is not
 * pinned to zero: the launch ramps up in time and meets the plan; and the
 * stop at the route's end is the mission's distance-parameterised stop, not
 * part of this plan (tt_vplan_build's v_end only limits the last point).
 *
 * Rebuilt when the friction estimate moves (FW-16): a_lat and a_brake scale
 * with mu_est / mu_nominal.
 *
 * Portable C11, float32, no allocation: one float per route point.
 */
#ifndef TT_VPLAN_H
#define TT_VPLAN_H

#include <stdint.h>
#include "tt_route.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    float    v[TT_ROUTE_MAX_PTS];  /* m/s at each route point             */
    uint32_t n;
    float    v_max, a_lat, a_drive, a_brake;   /* what it was built with  */
} TtVPlan;

/* Build for route `r` with these limits (each <= 0 takes the route's own).
 * v_end < 0 leaves the last point free. */
void tt_vplan_build(TtVPlan *p, const TtRoute *r, float v_max, float a_lat,
                    float a_drive, float a_brake, float v_end);

/* The plan at arc length s (interpolated in v^2, which is linear in s under
 * constant acceleration). `hint` as tt_route_at. *dv2ds (may be NULL) is the
 * slope of v^2: half of it is the acceleration the plan asks for there. */
float tt_vplan_at(const TtVPlan *p, const TtRoute *r, float s, uint32_t *hint,
                  float *dv2ds);

/* The lowest the plan goes over [s, s + d]: what the car must be able to
 * slow to within d. */
float tt_vplan_min(const TtVPlan *p, const TtRoute *r, float s, float d, uint32_t *hint);

#ifdef __cplusplus
}
#endif

#endif /* TT_VPLAN_H */
