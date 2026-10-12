/*
 * test_route.c — routes, the path tracker and the speed plan (NAV-01..03).
 *
 *   - every built-in route validates, and its CRC recomputed in C equals
 *     the one Tools/gen_route.js computed in JavaScript (the two agree on
 *     every float bit);
 *   - a damaged table is caught;
 *   - projection: a car on the route reads zero cross-track error and its
 *     own arc length, left of it reads +, and at the figure-8's crossing it
 *     stays on its own strand;
 *   - the speed plan keeps v^2 |kappa| <= a_lat everywhere, and no more than
 *     a_drive / a_brake between points;
 *   - both tracking laws on a kinematic car with the twin's delays (one
 *     command period, the steering's lag) on the oval and the figure-8 at 2
 *     and 3 m/s: cross-track and end-heading errors.
 *
 * The kinematic car is not the sim: no tyres, an exact pose. It catches a
 * tracker that cannot work even there; the Unity runs (NAV-06) judge it.
 */
#include "tt_route.h"
#include "tt_track.h"
#include "tt_vplan.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_fail = 0, g_pass = 0;
#define CHECK(name, cond) do { if (cond) { printf("PASS %s\n", name); g_pass++; } \
                               else { printf("FAIL %s\n", name); g_fail++; } } while (0)

#define PI 3.14159265358979

/* ---- the routes themselves ------------------------------------------------ */

static void routes(void)
{
    int i, n = 0;
    for (i = 0; tt_routes[i] != 0; i++) {
        const TtRoute *r = tt_routes[i];
        char name[96];
        int e = tt_route_validate(r, 1.6f);
        snprintf(name, sizeof(name), "route %s: valid, C CRC %08X = generator's (%u pts, %.2f m)",
                 r->name, (unsigned)tt_route_crc(r), (unsigned)r->n, (double)r->pt[r->n - 1u].s);
        CHECK(name, e == TT_RTE_OK);
        CHECK("  ...found by name", tt_route_find(r->name) == r);
        n++;
    }
    CHECK("five routes built in", n == 5);
    CHECK("an unknown name finds nothing", tt_route_find("nope") == 0);

    /* a damaged copy */
    {
        static TtRoutePt pts[TT_ROUTE_MAX_PTS];
        const TtRoute *o = tt_route_find("oval");
        TtRoute r = *o;
        memcpy(pts, o->pt, sizeof(pts[0]) * o->n);
        r.pt = pts;
        pts[100].x += 0.001f;
        CHECK("a point moved 1 mm: CRC mismatch", tt_route_validate(&r, 1.6f) == TT_RTE_CRC);
        pts[100].x += 0.5f;
        CHECK("a point moved 0.5 m: geometry", tt_route_validate(&r, 1.6f) == TT_RTE_GEOM);
        pts[100].x = o->pt[100].x;
        pts[200].s = pts[199].s;
        CHECK("s not increasing", tt_route_validate(&r, 1.6f) == TT_RTE_S);
        pts[200].s = o->pt[200].s;
        CHECK("curvature beyond the car's lock", tt_route_validate(&r, 0.2f) == TT_RTE_KAPPA);
    }
}

/* ---- projection ----------------------------------------------------------- */

static void projection(void)
{
    const TtRoute *r = tt_route_find("figure8");
    TtRouteProj pr;
    uint32_t h = 0, i, from = 0, jumps = 0;
    float x, y, psi, k, s;

    tt_route_at(r, 12.34f, &h, &x, &y, &psi, &k);
    tt_route_project(r, x, y, h, 10u, 40u, &pr);
    CHECK("on the route: ey 0, s its own",
          fabsf(pr.ey) < 1e-3f && fabsf(pr.s - 12.34f) < 2e-3f);
    tt_route_project(r, x - 0.2f * sinf(psi), y + 0.2f * cosf(psi), h, 10u, 40u, &pr);
    CHECK("0.2 m to the left reads ey +0.2", fabsf(pr.ey - 0.2f) < 2e-3f);

    /* Walk the whole figure-8 0.3 m off to the right, projecting from the
     * last place each time: s must advance smoothly through the crossing. */
    for (s = 0.0f; s < r->pt[r->n - 1u].s - 0.1f; s += 0.045f) {
        uint32_t hh = 0;
        float prev = -1.0f;
        tt_route_at(r, s, &hh, &x, &y, &psi, &k);
        if (from > 0u) prev = r->pt[from].s;
        tt_route_project(r, x + 0.3f * sinf(psi), y - 0.3f * cosf(psi), from, 10u, 40u, &pr);
        if (prev >= 0.0f && (pr.s < prev - 0.2f || pr.s > s + 0.5f || pr.s < s - 0.5f)) jumps++;
        from = pr.i;
    }
    CHECK("figure-8 walked 0.3 m off the line: never jumps strands", jumps == 0u);
    (void)i;
}

/* ---- speed plan ----------------------------------------------------------- */

static void speed_plan(void)
{
    static TtVPlan p;
    int i;
    for (i = 0; tt_routes[i] != 0; i++) {
        const TtRoute *r = tt_routes[i];
        uint32_t j;
        float worst_lat = 0.0f, worst_acc = 0.0f, worst_dec = 0.0f, vmin = 1e9f;
        char name[128];
        tt_vplan_build(&p, r, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f);
        for (j = 0; j < p.n; j++) {
            float lat = p.v[j] * p.v[j] * fabsf(r->pt[j].kappa);
            if (lat > worst_lat) worst_lat = lat;
            if (p.v[j] < vmin) vmin = p.v[j];
            if (j > 0u) {
                float a = (p.v[j] * p.v[j] - p.v[j - 1u] * p.v[j - 1u]) /
                          (2.0f * (r->pt[j].s - r->pt[j - 1u].s));
                if (a > worst_acc) worst_acc = a;
                if (-a > worst_dec) worst_dec = -a;
            }
        }
        snprintf(name, sizeof(name),
                 "plan %s: lat %.2f <= %.2f, acc %.2f <= %.2f, dec %.2f <= %.2f m/s^2 (min %.2f m/s)",
                 r->name, (double)worst_lat, (double)r->a_lat_max, (double)worst_acc, (double)r->a_drive,
                 (double)worst_dec, (double)r->a_brake, (double)vmin);
        CHECK(name, worst_lat <= r->a_lat_max * 1.001f && worst_acc <= r->a_drive * 1.001f &&
                    worst_dec <= r->a_brake * 1.001f);
    }
    {
        const TtRoute *r = tt_route_find("hairpin");
        uint32_t h = 0;
        float v = tt_vplan_min(&p, r, 0.0f, r->pt[r->n - 1u].s, &h);
        tt_vplan_build(&p, r, 0.0f, 0.0f, 0.0f, 0.0f, -1.0f);
        v = tt_vplan_min(&p, r, 0.0f, r->pt[r->n - 1u].s, &h);
        CHECK("hairpin: slowest = sqrt(a_lat * 1.5 m)", fabsf(v - sqrtf(r->a_lat_max * 1.5f)) < 0.02f);
    }
}

/* ---- tracking on a kinematic car ------------------------------------------ */

#define DT        0.01
#define DELAY_N   1        /* command periods before a command acts       */
#define LAG_S     0.06     /* yaw rate follows its command through this    */

typedef struct { double rms, max, end_psi_deg; int done; } Trk;

static int g_pred = 0;     /* 0: over the delay (what the mission does);
                              1: over delay + lag on the current rate */
static float g_kv = 0.45f, g_tff = 0.04f;
static int g_dbg = 0;

static Trk drive(const char *route, int law, float v_set, double y_off)
{
    const TtRoute *r = tt_route_find(route);
    TtTrackCfg c;
    TtTrack t;
    static TtVPlan p;
    double x = r->start[0], y = r->start[1], psi = r->start[2], v = 0.0, rate = 0.0;
    double sum2 = 0.0, mx = 0.0;
    float sent[8] = { 0 };
    int n = 0, k;
    Trk out;
    memset(&out, 0, sizeof(out));
    tt_track_default_cfg(&c);
    c.law = law;
    c.k_v = g_kv;
    c.t_ff = g_tff;
    tt_track_reset(&t, &c, r);
    tt_vplan_build(&p, r, v_set, 0.0f, 0.0f, 0.0f, -1.0f);
    /* start offset to the left to see it converge */
    x += -sin(psi) * y_off;
    y += cos(psi) * y_off;

    for (k = 0; k < 6000; k++) {
        uint32_t h = t.i;
        float vref, kap, xp, yp, pp, vp = (float)v;
        double fly = 0.0;
        int j;
        /* the pose the command will land on: the rates in flight, then
         * (pred 1) the steering lag at the rate it turns now, position and
         * heading along the same arc */
        for (j = 0; j < DELAY_N; j++) fly += sent[j] * DT;
        {
            double T = DELAY_N * DT, h = fly;
            if (g_pred) { T += LAG_S; h += rate * LAG_S; }
            pp = (float)(psi + h);
            xp = (float)(x + v * cos(psi + 0.5 * h) * T);
            yp = (float)(y + v * sin(psi + 0.5 * h) * T);
        }
        kap = tt_track_step(&t, xp, yp, pp, vp);
        vref = tt_vplan_at(&p, r, t.s, &h, 0);
        if (t.at_end || t.s > r->pt[r->n - 1u].s - 0.05f) { out.done = 1; break; }
        /* the car: speed follows the plan with a launch ramp, the yaw
         * rate follows the command one period late, through the lag */
        if (v < vref) v = v + 2.0 * DT < vref ? v + 2.0 * DT : vref;
        else v = vref;
        if (v < 0.3) v = 0.3;
        for (j = DELAY_N - 1; j > 0; j--) sent[j] = sent[j - 1];
        sent[0] = (float)(v * kap);
        rate += (sent[DELAY_N - 1] - rate) * (1.0 - exp(-DT / LAG_S));
        psi += rate * DT;
        x += v * cos(psi) * DT;
        y += v * sin(psi) * DT;
        if (t.s > 2.0f) {
            TtRouteProj pr;
            tt_route_project(r, (float)x, (float)y, t.i, 10u, 40u, &pr);
            sum2 += (double)pr.ey * pr.ey;
            if (fabs(pr.ey) > mx) mx = fabs(pr.ey);
            n++;
            if (g_dbg && k % 10 == 0)
                printf("s %6.2f ey %7.1f mm  k_route %6.3f k_cmd %6.3f rate %6.3f v %.2f\n", (double)pr.s,
                       (double)pr.ey * 1e3, (double)pr.kappa, (double)kap, rate, v);
        }
    }
    out.rms = n ? sqrt(sum2 / n) : 1e9;
    out.max = mx;
    out.end_psi_deg = fabs(tt_route_wrap((float)psi - r->pt[r->n - 1u].psi)) * 180.0 / PI;
    return out;
}

static void tracking(void)
{
    const char *rt[] = { "oval", "figure8", "slalom", "hairpin", "opus" };
    const char *lw[] = { "RPP", "FRENET" };
    int a, b, law;
    for (law = 0; law < 2; law++)
        for (a = 0; a < 5; a++)
            for (b = 0; b < 2; b++) {
                float v = a == 4 ? (b ? 4.5f : 3.0f) : (b ? 3.0f : 2.0f);
                Trk t = drive(rt[a], law, v, 0.0);
                char name[160];
                snprintf(name, sizeof(name), "%-6s %-8s %.1f m/s: xte rms %.1f mm, max %.1f mm, end %.2f deg",
                         lw[law], rt[a], (double)v, t.rms * 1e3, t.max * 1e3, t.end_psi_deg);
                /* FRENET is the default and must hold the line; pure
                 * pursuit is kept for comparison and only has to work. */
                CHECK(name, t.done && t.end_psi_deg < 1.0 &&
                            (law == TT_TRK_FRENET ? (t.rms < 0.010 && t.max < 0.025)
                                                  : (t.rms < 0.060 && t.max < 0.150)));
            }
    {
        Trk t = drive("oval", TT_TRK_FRENET, 3.0f, 0.3);
        CHECK("FRENET from 0.3 m off the line: converges, no overshoot past 0.35 m",
              t.done && t.max < 0.35);
    }
}

int main(int argc, char **argv)
{
    if (argc > 3 && strcmp(argv[1], "--trace") == 0) {
        g_dbg = 1; g_pred = 0; g_kv = (float)atof(argv[3]);
        drive(argv[2], argc > 4 ? atoi(argv[4]) : 0, argc > 5 ? (float)atof(argv[5]) : 3.0f, 0.0);
        return 0;
    }
    if (argc > 1 && strcmp(argv[1], "--sweep") == 0) {
        const char *rt[] = { "oval", "figure8", "slalom", "hairpin", "opus" };
        int law, pr, a, kvi;
        float kvs[] = { 0.3f, 0.45f, 0.6f };
        float tffs[] = { 0.0f, 0.04f, 0.07f };
        for (law = 0; law < 2; law++) for (pr = 0; pr < 2; pr++) for (kvi = 0; kvi < 3; kvi++) {
            int ti;
            for (ti = 0; ti < (law ? 3 : 1); ti++) {
                double worst_rms = 0, worst_max = 0;
                g_pred = pr; g_kv = kvs[kvi]; g_tff = tffs[ti];
                for (a = 0; a < 5; a++) {
                    float v = a == 4 ? 4.5f : 3.0f;
                    Trk t = drive(rt[a], law, v, 0.0);
                    if (t.rms > worst_rms) worst_rms = t.rms;
                    if (t.max > worst_max) worst_max = t.max;
                }
                printf("law %d pred %d kv %.2f tff %.2f: worst rms %.1f mm max %.1f mm\n",
                       law, pr, (double)g_kv, (double)g_tff, worst_rms * 1e3, worst_max * 1e3);
            }
        }
        return 0;
    }
    routes();
    projection();
    speed_plan();
    tracking();
    printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
