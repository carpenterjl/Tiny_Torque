/*
 * test_grid.c — the local map and the detour planner (NAV-08, tt_grid.h and
 * tt_detour.h):
 *
 *   - rays into the grid: a return raises the cell it ends in, a ray
 *     through a cell lowers it, the window rolls with the car and forgets
 *     what it leaves behind, and a return outside the height band is not
 *     a hit the planner sees;
 *   - the offset profile: continuous, zero slope at its knots, the held
 *     offset and the way back, its peak curvature as the formula says, and
 *     the offset path lying d(s) left of the route;
 *   - the planner on the oval's first straight: a barrier across the route
 *     is passed on the side the hint says, on the nearest offset that
 *     clears it, with the ease-out done before the block and the ease-back
 *     ending past it; a wider block pushes the offset out; a wall across
 *     the whole lattice yields no plan.
 *
 * Registered with CTest as `tt_grid_unit`.
 */
#include "tt_grid.h"
#include "tt_detour.h"
#include "tt_route.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0, g_pass = 0;
#define CHECK(name, cond) do { if (cond) { printf("PASS %s\n", name); g_pass++; } \
                               else { printf("FAIL %s\n", name); g_fail++; } } while (0)

static TtGrid g_grid;   /* 9 KB: not on the stack */

static int near(float a, float b, float tol) { return fabsf(a - b) <= tol; }

/* A wall of returns across the route at arc length s, from lat0 to lat1
 * (left +), confirmed `times` frames: each return's ray starts half a
 * metre short of it along the route. */
static void wall(TtGrid *g, const TtRoute *r, float s, float lat0, float lat1, int times)
{
    float x, y, psi, lat;
    int k;
    uint32_t h = 0u;
    tt_route_at(r, s, &h, &x, &y, &psi, 0);
    for (k = 0; k < times; k++)
        for (lat = lat0; lat <= lat1 + 1e-4f; lat += 0.05f) {
            float ex = x - lat * sinf(psi), ey = y + lat * cosf(psi);
            tt_grid_ray(g, ex - 0.5f * cosf(psi), ey - 0.5f * sinf(psi), 0.05f, ex, ey, 0.05f,
                        -0.06f, 0.15f, 1);
        }
}

int main(void)
{
    TtGrid *g = &g_grid;
    const TtRoute *oval = tt_route_find("oval");
    TtDetourCfg cfg;
    TtDetour d;
    int i;

    /* ---- the grid ------------------------------------------------------ */
    tt_grid_init(g, 0.05f);
    tt_grid_centre(g, 0.0f, 0.0f);
    CHECK("an empty grid has nothing occupied", tt_grid_count(g) == 0u && !tt_grid_occupied(g, 1.0f, 0.0f, 0.3f));
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    CHECK("one return: the end cell counts 2, not yet occupied",
          tt_grid_at(g, 1.0f, 0.0f) == 2 && !tt_grid_occupied(g, 1.0f, 0.0f, 0.1f));
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    CHECK("two returns: occupied, and the cells the rays crossed are not",
          tt_grid_occupied(g, 1.0f, 0.0f, 0.1f) && tt_grid_at(g, 0.5f, 0.0f) == 0 && tt_grid_count(g) == 1u);
    CHECK("a disc that does not reach the cell is clear", !tt_grid_occupied(g, 0.6f, 0.0f, 0.3f));
    CHECK("...and one that does is not", tt_grid_occupied(g, 0.8f, 0.0f, 0.3f));
    for (i = 0; i < 4; i++) tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 2.0f, 0.0f, 0.0f, -0.06f, 0.15f, 0);
    CHECK("four rays through it and the return has faded", tt_grid_at(g, 1.0f, 0.0f) == 0 && !tt_grid_occupied(g, 1.0f, 0.0f, 0.1f));
    /* a ray that climbs above the roof stops clearing there */
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    for (i = 0; i < 4; i++) tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 2.0f, 0.0f, 1.0f, -0.06f, 0.15f, 0);
    CHECK("a ray over the roof does not clear what is under it", tt_grid_at(g, 1.0f, 0.0f) == 4);
    tt_grid_centre(g, 1.0f, 0.0f);
    CHECK("the window rolls a metre and keeps the cell", tt_grid_at(g, 1.0f, 0.0f) == 4);
    tt_grid_centre(g, 6.0f, 0.0f);
    CHECK("...rolls past it and it is outside", tt_grid_at(g, 1.0f, 0.0f) == -1);
    tt_grid_centre(g, 1.0f, 0.0f);
    CHECK("...and back, and the cell was forgotten", tt_grid_at(g, 1.0f, 0.0f) == 0 && tt_grid_count(g) == 0u);
    tt_grid_centre(g, 0.0f, 0.0f);
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    tt_grid_ray(g, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, -0.06f, 0.15f, 1);
    tt_grid_centre(g, 0.3f, 0.2f);
    tt_grid_centre(g, -0.4f, -0.1f);
    CHECK("small moves either way keep the map", tt_grid_at(g, 1.0f, 0.0f) == 4);

    /* ---- the profile --------------------------------------------------- */
    memset(&d, 0, sizeof(d));
    d.active = 1;
    d.s0 = 2.0f; d.s1 = 4.5f; d.s2 = 7.0f; d.s3 = 9.5f;
    d.d0 = 0.0f; d.d1 = 0.6f;
    {
        float dd, ddd, kmax = 0.0f, s, prev = tt_detour_at(&d, 1.0f, 0, 0), jump = 0.0f;
        CHECK("before the ease-out the offset is d0", near(tt_detour_at(&d, 1.0f, 0, 0), 0.0f, 1e-6f));
        CHECK("held between s1 and s2", near(tt_detour_at(&d, 5.0f, &dd, &ddd), 0.6f, 1e-6f) && near(dd, 0.0f, 1e-6f));
        CHECK("back on the route after s3", near(tt_detour_at(&d, 10.0f, 0, 0), 0.0f, 1e-6f));
        CHECK("zero slope at the knots",
              near(0.0f, (tt_detour_at(&d, 2.0f, &dd, 0), dd), 1e-5f) && near(0.0f, (tt_detour_at(&d, 4.5f, &dd, 0), dd), 1e-5f) &&
              near(0.0f, (tt_detour_at(&d, 7.0f, &dd, 0), dd), 1e-5f) && near(0.0f, (tt_detour_at(&d, 9.5f, &dd, 0), dd), 1e-5f));
        for (s = 1.0f; s < 11.0f; s += 0.01f) {
            float v = tt_detour_at(&d, s, 0, &ddd);
            if (fabsf(v - prev) > jump) jump = fabsf(v - prev);
            if (fabsf(ddd) > kmax) kmax = fabsf(ddd);
            prev = v;
        }
        CHECK("continuous (no step over 5 mm per cm)", jump < 0.005f);
        CHECK("peak curvature pi^2 D / (2 L^2)", near(kmax, 0.5f * 3.14159265f * 3.14159265f * 0.6f / (2.5f * 2.5f), 0.01f));
        CHECK("a detour that is not active is the route", near(tt_detour_at(0, 5.0f, 0, 0), 0.0f, 1e-9f));
    }
    CHECK("the oval is built in", oval != 0);
    if (oval) {
        float x, y, psi, kap;
        uint32_t h = 0u;
        TtRouteProj pr;
        tt_route_at(oval, 10.0f, &h, &x, &y, &psi, &kap);
        CHECK("the oval's first straight is straight at s = 10", near(kap, 0.0f, 1e-4f));
        tt_detour_point(oval, &d, 5.5f, &h, &x, &y, &psi, &kap);
        tt_route_project(oval, x, y, 0u, 10u, 100u, &pr);
        CHECK("the offset path lies d1 left of the route while held", near(pr.ey, 0.6f, 2e-3f) && near(pr.s, 5.5f, 2e-2f) && near(kap, 0.0f, 1e-4f));
        tt_detour_point(oval, &d, 3.25f, &h, &x, &y, &psi, &kap);
        tt_route_project(oval, x, y, 0u, 10u, 100u, &pr);
        CHECK("...and half way out in the ease-out, pointing outward",
              near(pr.ey, 0.3f, 5e-3f) && psi - pr.psi > 0.1f && near(kap, 0.0f, 1e-4f));
    }

    /* ---- the planner --------------------------------------------------- */
    if (oval) {
        TtDetour plan;
        float sb, dd, yb;
        tt_detour_default_cfg(&cfg);
        tt_grid_init(g, 0.05f);
        {
            float x, y, psi;
            uint32_t h = 0u;
            tt_route_at(oval, 7.8f, &h, &x, &y, &psi, 0);
            tt_grid_centre(g, x, y);
        }
        CHECK("nothing on the route: nothing blocks it", tt_detour_blocked(&cfg, g, oval, 0, 6.0f, 4.0f, 0) < 0.0f);
        wall(g, oval, 10.0f, -0.1f, 0.4f, 2);          /* a box mostly left of the route */
        sb = tt_detour_blocked(&cfg, g, oval, 0, 6.0f, 4.0f, &yb);
        CHECK("a box at s = 10 blocks the route just short of it, its centroid left",
              sb > 9.6f && sb < 10.0f && yb > 0.1f && yb < 0.2f);
        CHECK("the plan passes it on the right, the side with more room",
              tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, 0.0f, sb, yb, &plan) && plan.active && plan.d1 < 0.0f);
        CHECK("...on the nearest offset that clears it and its unknown width (0.45 m, 0.6 from the centroid)",
              near(fabsf(plan.d1), 0.45f, 1e-6f));
        CHECK("...eased out before the block, from the car's offset",
              plan.s0 > 6.0f && plan.s1 <= sb - cfg.reach_m + 1e-4f && near(plan.d0, 0.0f, 1e-9f));
        CHECK("...held past it and eased back after", plan.s2 > 10.0f + cfg.past_m - 0.2f && near(plan.s3, plan.s2 + cfg.ramp_out_m, 1e-4f));
        CHECK("...with a path the map agrees is clear", tt_detour_blocked(&cfg, g, oval, &plan, 6.0f, plan.s3 - 6.0f + 0.5f, 0) < 0.0f);
        CHECK("...with its peak curvature stated", plan.kappa_peak > 0.3f && plan.kappa_peak <= cfg.kappa_max);
        CHECK("and on the left for a box to the right",
              tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, 0.0f, sb, -0.15f, &plan) && plan.d1 > 0.0f);
        wall(g, oval, 10.0f, -0.75f, 0.4f, 2);         /* wider to the right: the right needs 1.0 */
        sb = tt_detour_blocked(&cfg, g, oval, 0, 6.0f, 4.0f, &yb);
        CHECK("a wider block pushes the offset out on that side",
              tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, 0.0f, sb, -0.15f, &plan) && near(fabsf(plan.d1), 0.8f, 1e-6f) && plan.d1 > 0.0f);
        wall(g, oval, 10.0f, -1.3f, 1.3f, 2);          /* a wall across the whole lattice */
        sb = tt_detour_blocked(&cfg, g, oval, 0, 6.0f, 4.0f, &yb);
        CHECK("a wall across the lattice: no plan, the car holds",
              !tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, 0.0f, sb, yb, &plan));
        CHECK("too close to ease out: no plan", !tt_detour_plan(&cfg, g, oval, 9.0f, 2.0f, 0.0f, sb, yb, &plan));
        /* replanning from a held offset: the ease-out starts from it */
        tt_grid_init(g, 0.05f);
        {
            float x, y, psi;
            uint32_t h = 0u;
            tt_route_at(oval, 7.8f, &h, &x, &y, &psi, 0);
            tt_grid_centre(g, x, y);
        }
        wall(g, oval, 10.0f, -0.3f, 0.3f, 2);
        sb = tt_detour_blocked(&cfg, g, oval, 0, 6.0f, 4.0f, &yb);
        CHECK("a box centred on the route: the plan goes left (its centroid is not right of it)",
              near(yb, 0.0f, 0.05f) && tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, 0.0f, sb, yb, &plan) && plan.d1 > 0.0f);
        CHECK("from an offset of -0.6 the plan keeps the side and starts there",
              tt_detour_plan(&cfg, g, oval, 6.0f, 2.0f, -0.6f, sb, yb, &plan) && near(plan.d0, -0.6f, 1e-9f) &&
              near(plan.d1, -0.6f, 1e-6f) && near(tt_detour_at(&plan, 6.5f, &dd, 0), -0.6f, 1e-6f));
    }

    printf("%d passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
