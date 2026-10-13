/*
 * tt_track.c — path tracker (NAV-02). See tt_track.h.
 */
#include "tt_track.h"

#include <math.h>
#include <string.h>

static float clampf_(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

void tt_track_default_cfg(TtTrackCfg *c)
{
    memset(c, 0, sizeof(*c));
    /* FRENET: on a kinematic car with the twin's delays (one period and
     * the steering's lag) it stays within 12 mm on every test route, where
     * pure pursuit, which aims at the route ahead but has no idea how late
     * the yaw rate arrives, gives 30-40 mm at each curvature transition
     * (test_route --sweep). */
    c->law = TT_TRK_FRENET;
    c->k_v = 0.45f;
    c->ld_min = 0.6f;
    c->ld_max = 2.5f;
    c->zeta = 0.9f;
    c->t_ff = 0.04f;     /* the steering lag left after the in-flight prediction */
    c->kappa_max = 1.6f;     /* the twin's full lock: tan(28 deg) / 0.30 m  */
    c->back = 10u;           /* 1 m at 0.1 m spacing                        */
    c->ahead = 40u;          /* 4 m: a tick at 4.5 m/s is 45 mm              */
    c->lost_m = 1.0f;
}

void tt_track_reset(TtTrack *t, const TtTrackCfg *c, const TtRoute *r)
{
    /* Everything, the detour pointer included: a reset tracker follows the
     * route until tt_track_set_detour says otherwise. (It once kept the
     * pointer across the reset, which read whatever an uninitialised
     * TtTrack held — fine on one machine's stack, a crash on CI's.) */
    memset(t, 0, sizeof(*t));
    t->cfg = *c;
    t->r = r;
}

void tt_track_set_detour(TtTrack *t, const TtDetour *d)
{
    t->det = d;
}

float tt_track_step(TtTrack *t, float x, float y, float psi, float v)
{
    const TtRoute *r = t->r;
    const TtTrackCfg *c = &t->cfg;
    TtRouteProj pr;
    float va = v < 0.0f ? -v : v, k, s_end, off, doff, ey, epsi;

    if (r == 0 || r->n < 2u) { t->kappa = 0.0f; return 0.0f; }

    tt_route_project(r, x, y, t->i, c->back, c->ahead, &pr);
    t->i = pr.i;
    t->s = pr.s;
    t->ey = pr.ey;
    t->epsi = tt_route_wrap(psi - pr.psi);
    t->kappa_route = pr.kappa;
    /* The errors against the offset path (NAV-08): the offset here and
     * the heading its slope turns the path by. */
    off = tt_detour_at(t->det, pr.s, &doff, 0);
    t->d_off = off;
    ey = pr.ey - off;
    epsi = tt_route_wrap(t->epsi - atanf(doff));
    t->lost = (uint8_t)(ey > c->lost_m || ey < -c->lost_m);
    s_end = r->pt[r->n - 1u].s;
    t->at_end = (uint8_t)(pr.s >= s_end - 1e-3f);
    t->ld = clampf_(c->k_v * va, c->ld_min, c->ld_max);

    if (c->law == TT_TRK_FRENET) {
        float kff, ky = 1.0f / (t->ld * t->ld), kp = 2.0f * c->zeta / t->ld;
        uint32_t h = t->i;
        tt_detour_point(r, t->det, pr.s + va * c->t_ff, &h, 0, 0, 0, &kff);
        t->i_look = h;
        k = kff - ky * ey - kp * sinf(epsi);
    } else {
        /* The lookahead point along the route; past the end, the route's
         * last heading carries it on so the car still lines up. */
        float lx, ly, lpsi, dx, dy, d, ca, sa, lat, sl = pr.s + t->ld;
        uint32_t h = t->i;
        tt_detour_point(r, t->det, sl, &h, &lx, &ly, &lpsi, 0);
        if (sl > s_end) {
            lx += (sl - s_end) * cosf(lpsi);
            ly += (sl - s_end) * sinf(lpsi);
        }
        t->i_look = h;
        dx = lx - x;
        dy = ly - y;
        d = sqrtf(dx * dx + dy * dy);
        ca = cosf(psi);
        sa = sinf(psi);
        lat = -sa * dx + ca * dy;          /* + = point to the left       */
        /* 2 sin(a) / d = 2 lat / d^2 */
        k = d > 1e-3f ? 2.0f * lat / (d * d) : 0.0f;
    }
    t->kappa = clampf_(k, -c->kappa_max, c->kappa_max);
    return t->kappa;
}
