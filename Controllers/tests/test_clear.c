/*
 * test_clear.c — the clearance from a multizone ToF (NAV-05, tt_clear.h),
 * on synthetic frames of the twin's 8x8, 45 deg ranger on the nose: the
 * floor, a box across the path, a cone beside it, a box on a curve.
 * Registered with CTest as `tt_clear_unit`.
 */
#include "tt_clear.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;
#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); g_fail++; } \
                               else printf("ok   %s\n", name); } while (0)

/* An axis-aligned face at x = X: y in [y0, y1], z in [z0, z1] (body frame). */
typedef struct { float x, y0, y1, z0, z1; } Face;

/* One frame: each zone's nearest hit among the faces and the floor, the
 * body pitched `pitch` (nose down +: the world tilts the other way). */
static void frame(const TtClearCfg *c, float pitch, const Face *f, int nf, TtTof *tof)
{
    int side = c->zones_side, k;
    float cb = cosf(pitch), sb = sinf(pitch);
    memset(tof, 0, sizeof(*tof));
    tof->st.valid = 1;
    tof->zones = (uint8_t)(side * side);
    for (k = 0; k < side * side; k++) {
        float d[3], best = c->max_range_m, t;
        int i;
        tt_clear_zone_dir(c, k / side, k % side, d);
        /* The floor at z = floor_z in the WORLD: in the body frame it is
         * the plane z cb - x sb = floor_z. The ray o + t d meets it at
         * t = (floor_z - (oz cb - ox sb)) / (dz cb - dx sb). */
        {
            float den = d[2] * cb - d[0] * sb;
            float num = c->floor_z_m - (c->pos_m[2] * cb - c->pos_m[0] * sb);
            if (den < -1e-6f) { t = num / den; if (t > 0.0f && t < best) best = t; }
        }
        for (i = 0; i < nf; i++) {
            float py, pz;
            if (d[0] <= 1e-6f) continue;
            t = (f[i].x - c->pos_m[0]) / d[0];
            if (t <= 0.0f || t >= best) continue;
            py = c->pos_m[1] + t * d[1];
            pz = c->pos_m[2] + t * d[2];
            if (py >= f[i].y0 && py <= f[i].y1 && pz >= f[i].z0 && pz <= f[i].z1) best = t;
        }
        if (best < c->max_range_m) { tof->range_m[k] = best; tof->status[k] = 0; }
        else                       { tof->range_m[k] = c->max_range_m; tof->status[k] = 1; }
    }
}

static int near(float a, float b, float tol) { return fabsf(a - b) <= tol; }

int main(void)
{
    TtClearCfg c;
    TtTof tof;
    TtClearOut o;
    TtClearPt straight[2] = { { 0.0f, 0.0f }, { 6.0f, 0.0f } };
    TtClearPt curve[40];
    const float floor_z = -0.055f;
    int i;

    memset(&c, 0, sizeof(c));
    c.pos_m[0] = 0.205f; c.pos_m[2] = 0.035f;    /* the twin's nose ranger */
    c.fov_rad = 45.0f * 3.14159265f / 180.0f;
    c.zones_side = 8;
    c.half_width_m = 0.20f;
    c.floor_z_m = floor_z;
    c.floor_margin_m = 0.03f;
    c.max_range_m = 4.0f;

    /* A left arc of radius 3 m from the origin, 0.1 m apart. */
    for (i = 0; i < 40; i++) {
        float s = 0.1f * (float)i;
        curve[i].x = 3.0f * sinf(s / 3.0f);
        curve[i].y = 3.0f * (1.0f - cosf(s / 3.0f));
    }

    /* Nothing but the floor. */
    frame(&c, 0.0f, 0, 0, &tof);
    tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
    CHECK("the floor alone is seen", o.n_valid > 10);
    CHECK("...and dropped", o.n_floor == o.n_valid && o.n_in == 0);
    CHECK("...so the way is clear", o.clear_m == TT_CLEAR_NONE);

    /* A barrier across the path at 1.5 m (0.6 wide, 0.25 high, on the floor). */
    {
        Face box = { 1.5f, -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("a barrier at 1.5 m reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
        CHECK("...inside the corridor", near(o.y_m, 0.0f, c.half_width_m));
        CHECK("...from several zones", o.n_in >= 4);

        /* Braking: 2 deg nose down. The barrier stays; the floor still goes. */
        frame(&c, 0.035f, &box, 1, &tof);
        tt_clear_step(&c, &tof, 0.035f, straight, 2, &o);
        CHECK("nose down 2 deg: the barrier still reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
        /* ...and the pitch forgotten: the floor, now in the lower zones at a
         * metre or so, would read as a wall. */
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("...which needs the pitch: without it the floor is in the way", o.clear_m < 1.4f);
    }

    /* A cone beside the path (0.45 m to the left, 0.14 across, 0.18 high),
     * 1.6 m ahead: inside the 45 deg field of view, outside the corridor. */
    {
        Face cone = { 1.6f, 0.38f, 0.52f, floor_z, floor_z + 0.18f };
        frame(&c, 0.0f, &cone, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("a cone 0.45 m beside the path is seen", o.n_valid > o.n_floor);
        CHECK("...and not in the way", o.clear_m == TT_CLEAR_NONE && o.n_in == 0);
    }

    /* A box on a left curve: in the way of the curve, not of a straight path. */
    {
        Face box = { 1.86f, 0.44f, 0.84f, floor_z, floor_z + 0.25f };   /* (1.86, 0.64): the arc at s = 2 */
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, curve, 40, &o);
        printf("curve: clear %.3f m, y %+.3f, %d in, %d floor, %d valid\n",
               (double)o.clear_m, (double)o.y_m, o.n_in, o.n_floor, o.n_valid);
        CHECK("a box on the curve ahead is in the way", o.clear_m < 2.3f && o.n_in > 0);
        CHECK("...at about its arc length (its face spans the curve)", near(o.clear_m, 1.9f, 0.3f));
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("...and clear of a straight path", o.clear_m == TT_CLEAR_NONE);
    }

    /* The same barrier against a path of many short segments (a route's
     * points every 0.25 m): a return just past an inner segment's end
     * belongs to the next segment, not to that end (it read 0.2 m near on
     * the first box run: the hold stood 0.25 m off and judged 0.04). */
    {
        Face box = { 1.5f, -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        TtClearPt dense[24];
        for (i = 0; i < 24; i++) { dense[i].x = 0.25f * (float)i; dense[i].y = 0.0f; }
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, dense, 24, &o);
        CHECK("a barrier at 1.5 m reads 1.5 m against a dense path too", near(o.clear_m, 1.5f, 0.02f));
        /* ...and right in front of the nose, the end of the path cap still
         * counts: a wall 0.3 m from the sensor. */
        box.x = 0.5f;
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, dense, 24, &o);
        CHECK("a barrier at 0.5 m reads 0.5 m", near(o.clear_m, 0.5f, 0.02f));
    }

    /* A banner across the path 0.5 m up (the proving ground's finish): the
     * car passes under it. */
    {
        Face banner = { 2.0f, -0.8f, 0.8f, 0.45f, 0.6f };
        c.roof_z_m = 0.15f;
        frame(&c, 0.0f, &banner, 1, &tof);
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("a banner 0.5 m up is seen", o.n_valid > o.n_floor - 8);
        CHECK("...and passed under", o.clear_m == TT_CLEAR_NONE);
        c.roof_z_m = 0.0f;
        tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
        CHECK("...which the ceiling decides", o.clear_m < 2.1f);
    }

    /* A short frame (a single-beam ranger) is not judged. */
    tof.zones = 1;
    tt_clear_step(&c, &tof, 0.0f, straight, 2, &o);
    CHECK("a single-zone frame is not judged", o.clear_m == TT_CLEAR_NONE && o.n_valid == 0);

    printf("%d check(s) failed\n", g_fail);
    return g_fail ? 1 : 0;
}
