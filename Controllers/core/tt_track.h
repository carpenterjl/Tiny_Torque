/*
 * tt_track.h — path tracker, layer L4 of the control stack (NAV-02).
 *
 * Given the car's pose in the site frame (the navigation filter's, carried
 * over the commands still in flight by the caller), its speed and a route,
 * it finds the car's place on the route and returns the curvature to drive.
 * The caller turns that into a yaw rate, r_ref = v * kappa, for the yaw-rate
 * loop it already has (L2), so the tyres' slip angles stay that loop's
 * business.
 *
 * Two laws, chosen in the config:
 *
 *   TT_TRK_RPP     pure pursuit on a lookahead point L_d = clamp(k_v * v,
 *                  ld_min, ld_max) further along the route: kappa = 2 sin(a)
 *                  / d, a the bearing of that point off the heading, d its
 *                  distance. On a circle the car rides on, this is exactly
 *                  1/R. (The "regulated" half of Regulated Pure Pursuit,
 *                  slowing for curvature and for the route's end, is the
 *                  speed plan's: tt_vplan.h.)
 *   TT_TRK_FRENET  curvature feed-forward plus feedback on the errors in the
 *                  route's own frame: kappa = kappa_route(s + v * t_ff)
 *                  - k_y * e_y - k_psi * sin(e_psi), with k_y = 1/L^2 and
 *                  k_psi = 2 zeta / L, L the same lookahead. In distance
 *                  travelled that is a damped second-order settling with a
 *                  wavelength set by L, at any speed. It takes the place of
 *                  the plan's Stanley variant: it commands curvature, which
 *                  the yaw-rate loop wants, rather than a steering angle.
 *
 * The car's place is searched from its last one, a window back and ahead,
 * so on a figure-8 it never jumps to the other strand at the crossing.
 *
 * With a detour set (tt_track_set_detour, NAV-08) the tracker follows the
 * offset path instead: the route's point moved d(s) along its normal, the
 * heading and curvature that go with it (tt_detour_point). The errors it
 * reports stay the route's (ey, epsi); d_off is the offset it is holding.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_TRACK_H
#define TT_TRACK_H

#include <stdint.h>
#include "tt_route.h"
#include "tt_detour.h"

#ifdef __cplusplus
extern "C" {
#endif

enum { TT_TRK_RPP = 0, TT_TRK_FRENET = 1 };   /* default: FRENET */

typedef struct {
    int      law;          /* TT_TRK_*                                   */
    float    k_v;          /* lookahead per unit speed, s                */
    float    ld_min, ld_max;   /* lookahead bounds, m                    */
    float    zeta;         /* FRENET damping                             */
    float    t_ff;         /* FRENET: feed-forward this far ahead, s     */
    float    kappa_max;    /* clamp on the command, 1/m                  */
    uint32_t back, ahead;  /* projection window, points                  */
    float    lost_m;       /* cross-track beyond this: off the route     */
} TtTrackCfg;

typedef struct {
    TtTrackCfg     cfg;
    const TtRoute *r;
    uint32_t       i;          /* the car's segment on the route          */
    uint32_t       i_look;     /* the lookahead point's                   */
    float          s;          /* arc length of the car's place, m        */
    float          ey;         /* cross-track error, m, + = car left      */
    float          epsi;       /* heading error, rad, + = car left of it  */
    float          kappa_route;/* the route's curvature at the car        */
    float          kappa;      /* the command, 1/m                        */
    float          ld;         /* lookahead used, m                       */
    const TtDetour *det;       /* the detour followed; NULL = the route    */
    float          d_off;      /* its offset at the car's place, m         */
    uint8_t        lost;       /* |ey - d_off| > lost_m                   */
    uint8_t        at_end;     /* the car's place is the route's last pt  */
    uint8_t        pad_[2];
} TtTrack;

void tt_track_default_cfg(TtTrackCfg *c);

/* Start on `r` at its first point. */
void tt_track_reset(TtTrack *t, const TtTrackCfg *c, const TtRoute *r);

/* Follow the route offset by `d` (NAV-08); NULL, or one that is not
 * active, is the route itself. `d` must outlive the tracker. */
void tt_track_set_detour(TtTrack *t, const TtDetour *d);

/* One tick: the pose to steer from (x, y in the site frame, and psi the
 * COURSE: the direction the car moves, its heading plus its sideslip; all
 * as they will be when this tick's command lands, i.e. carried over the
 * commands still in flight) and the speed (m/s). Returns the curvature
 * command (also t->kappa). */
float tt_track_step(TtTrack *t, float x, float y, float psi, float v);

#ifdef __cplusplus
}
#endif

#endif /* TT_TRACK_H */
