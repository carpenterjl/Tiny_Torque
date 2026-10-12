/*
 * tt_route.h — routes the car can follow (NAV-01).
 *
 * A route is a polyline in the site frame, the UWB anchors' world frame the
 * navigation filter works in (x, y metres; psi = heading of travel, + = left
 * / CCW). Each point carries its arc length s from the first point, the
 * heading and the curvature there (+ = turning left), and a speed cap. The
 * curvature is the design's own, not differenced from the points: a route is
 * built from lines, arcs and clothoid spirals.
 *
 * Source of truth: Controllers/routes/<name>.json. Tools/gen_route.js builds
 * every route into core/routes_gen.c (the tables below, const, in flash) and
 * into UnitySim/Assets/Resources/Routes/<name>.json for the sim's scorer, so
 * the firmware and the scorer use the same points. The route's CRC covers
 * the table and its header; tt_route_validate() recomputes it.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_ROUTE_H
#define TT_ROUTE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TT_ROUTE_MAX_PTS 1024u

typedef struct {
    float x, y;            /* site frame, m                                */
    float s;               /* arc length from the first point, m           */
    float psi;             /* heading of travel, rad, continuous (unwrapped) */
    float kappa;           /* curvature, 1/m, + = left                     */
    float v_cap;           /* speed cap here, m/s; 0 = none                */
} TtRoutePt;

typedef struct {
    const char      *name;
    uint32_t         id;          /* stable number, logged                 */
    uint32_t         crc;         /* CRC-32 of the table and header (gen)  */
    uint32_t         n;           /* points                                */
    const TtRoutePt *pt;
    float            start[3];    /* where the car stands: x, y, psi       */
    uint8_t          has_start;   /* start[] is surveyed; seed the filter  */
    uint8_t          stop_at_end; /* stop on the last point                */
    uint8_t          pad_[2];
    float            v_max;       /* m/s, everywhere                       */
    float            a_lat_max;   /* m/s^2, the speed plan's lateral limit */
    float            a_drive;     /* m/s^2, its acceleration limit         */
    float            a_brake;     /* m/s^2, its braking limit              */
} TtRoute;

/* Every route built into this firmware, NULL-terminated (routes_gen.c). */
extern const TtRoute *const tt_routes[];

/* By name ("oval"); NULL if there is none. */
const TtRoute *tt_route_find(const char *name);

/* CRC-32 (IEEE, reflected, 0xEDB88320) of the route as gen_route.js
 * serialises it: n, then each point's six floats, then start[3], has_start,
 * stop_at_end, v_max, a_lat_max, a_drive, a_brake — little-endian, floats
 * bit for bit. */
uint32_t tt_route_crc(const TtRoute *r);

/* 0 if the route is usable: at least two points and at most
 * TT_ROUTE_MAX_PTS; s starting at 0 and strictly increasing; consecutive
 * points as far apart as their s says (within 1 % and 1 mm); each chord
 * along the headings at its ends (within 0.05 rad); |kappa| <= kappa_max
 * (the car's tightest turn; 0 = no check); positive limits; every number
 * finite; and the stored CRC matching. Otherwise a TT_RTE_* code. */
enum {
    TT_RTE_OK = 0, TT_RTE_EMPTY, TT_RTE_S, TT_RTE_GEOM, TT_RTE_HEADING,
    TT_RTE_KAPPA, TT_RTE_NAN, TT_RTE_CRC, TT_RTE_LIMITS
};
int tt_route_validate(const TtRoute *r, float kappa_max);

/* Where the route is at arc length s (clamped to the route): position,
 * heading and curvature, interpolated between the two points around s.
 * `hint` (may be NULL) is a point index to start looking from; it is
 * updated. Returns the index i with pt[i].s <= s <= pt[i + 1].s. */
uint32_t tt_route_at(const TtRoute *r, float s, uint32_t *hint,
                     float *x, float *y, float *psi, float *kappa);

/* The closest point of the route to (x, y), searched from point `from`
 * (back `back` points and forward `ahead`), so a crossing figure-8 never
 * jumps to the other strand. */
typedef struct {
    uint32_t i;            /* segment start index: pt[i] .. pt[i + 1]     */
    float    s;            /* arc length of the projection                */
    float    ey;           /* cross-track error, m, + = left of the route */
    float    psi;          /* route heading there (continuous)            */
    float    kappa;        /* route curvature there                       */
} TtRouteProj;
void tt_route_project(const TtRoute *r, float x, float y, uint32_t from,
                      uint32_t back, uint32_t ahead, TtRouteProj *out);

/* Heading difference a - b wrapped to (-pi, pi]. */
float tt_route_wrap(float a);

#ifdef __cplusplus
}
#endif

#endif /* TT_ROUTE_H */
