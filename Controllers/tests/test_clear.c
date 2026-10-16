/*
 * test_clear.c — the clearance from a multizone ToF (NAV-05, tt_clear.h),
 * on synthetic frames of the twin's 8x8, 45 deg ranger on the nose: the
 * floor, a box across the path, a cone beside it, a box on a curve. And
 * the floor learned (FW-25) on frames as the twin makes them: each zone the
 * nearest of 2x2 sub-rays, the ranger's mount a degree off what the
 * firmware was told.
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

/* As the twin's ranger makes a frame (FW-25): each zone the nearest return
 * of sub x sub rays across its patch, the sensor pitched `mount` (down +)
 * on top of what c->rpy_rad says, the body pitched `pitch` and riding
 * `dive` lower. A return too faint is none, as the twin has it:
 * cos(incidence) (range_max / d)^2 < 1 (the floor at a grazing angle).
 * `wall_y`, if not 0, is a wall along the path at that y, 0.3 m high. */
static void frame_sub(const TtClearCfg *c, float mount, int sub, float pitch, float dive,
                      const Face *f, int nf, float wall_y, TtTof *tof)
{
    int side = c->zones_side, k, a, b, i;
    float cb = cosf(pitch), sb = sinf(pitch), zone = c->fov_rad / (float)side;
    float floor_z = c->floor_z_m + dive;
    float m = c->rpy_rad[1] + mount, cm = cosf(m), sm = sinf(m);
    memset(tof, 0, sizeof(*tof));
    tof->st.valid = 1;
    tof->zones = (uint8_t)(side * side);
    for (k = 0; k < side * side; k++) {
        float best = c->max_range_m;
        for (a = 0; a < sub; a++)
            for (b = 0; b < sub; b++) {
                float rowf = (float)(k / side) + ((float)a + 0.5f) / (float)sub;
                float colf = (float)(k % side) + ((float)b + 0.5f) / (float)sub;
                float yaw = -0.5f * c->fov_rad + colf * zone, el = -0.5f * c->fov_rad + rowf * zone;
                float s0[3], d[3], t;
                s0[0] = cosf(el) * cosf(yaw); s0[1] = -cosf(el) * sinf(yaw); s0[2] = -sinf(el);
                d[0] = cm * s0[0] + sm * s0[2]; d[1] = s0[1]; d[2] = -sm * s0[0] + cm * s0[2];
                {
                    float den = d[2] * cb - d[0] * sb;
                    float num = floor_z - (c->pos_m[2] * cb - c->pos_m[0] * sb);
                    if (den < -1e-6f) {
                        t = num / den;
                        if (t > 0.0f && t < best &&
                            -den * (c->max_range_m / t) * (c->max_range_m / t) >= 1.0f) best = t;
                    }
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
                if (wall_y != 0.0f && d[1] * wall_y > 1e-6f) {
                    float pz;
                    t = (wall_y - c->pos_m[1]) / d[1];
                    pz = c->pos_m[2] + t * d[2];
                    if (t > 0.0f && t < best && pz >= floor_z && pz <= floor_z + 0.3f) best = t;
                }
            }
        if (best < c->max_range_m) { tof->range_m[k] = best; tof->status[k] = 0; }
        else                       { tof->range_m[k] = c->max_range_m; tof->status[k] = 1; }
    }
}

static int near(float a, float b, float tol) { return fabsf(a - b) <= tol; }

/* The floor learned and judged on the twin's frames (FW-25). */
static void floor_learned(void)
{
    TtClearCfg c;
    TtClearFloor fl;
    TtTof tof;
    TtClearOut o;
    TtClearPt straight[2] = { { 0.0f, 0.0f }, { 6.0f, 0.0f } };
    const float floor_z = -0.093f, quarter = 0.25f * 45.0f / 8.0f * 3.14159265f / 180.0f;
    const float nose_dn = 0.040f, dive = 0.008f;   /* a hard brake: 2.3 deg, 8 mm lower */
    const float mount = 0.026f;                    /* 1.5 deg further down than told    */
    int i;

    memset(&c, 0, sizeof(c));
    c.pos_m[0] = 0.205f; c.pos_m[2] = 0.035f;    /* the twin's nose ranger, 0.128 m up */
    c.fov_rad = 45.0f * 3.14159265f / 180.0f;
    c.zones_side = 8;
    c.half_width_m = 0.20f;
    c.floor_z_m = floor_z;
    c.floor_margin_m = 0.03f;
    c.roof_z_m = 0.15f;
    c.max_range_m = 4.0f;
    tt_clear_floor_init(&fl, &c);

    /* The zones' centres (no floor model) on the twin's frames: the floor's
     * returns lie a quarter zone below the centres they are put on, 18 mm
     * high at 0.75 m. Level, the margin holds; the mount a degree and a half
     * down, or the brake, and the floor is in the way. */
    frame_sub(&c, 0.0f, 2, 0.0f, 0.0f, 0, 0, 0.0f, &tof);
    tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
    CHECK("twin frame, level, on the centres: the floor is the floor", o.clear_m == TT_CLEAR_NONE && o.n_valid > 10);
    frame_sub(&c, mount, 2, 0.0f, 0.0f, 0, 0, 0.0f, &tof);
    tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
    CHECK("...the mount 1.5 deg down: the floor is in the way", o.clear_m < 1.0f);
    frame_sub(&c, 0.0f, 2, nose_dn, dive, 0, 0, 0.0f, &tof);
    tt_clear_step(&c, 0, &tof, nose_dn, straight, 2, &o);
    CHECK("...a hard brake, its pitch known: the floor is in the way", o.clear_m < 1.5f);
    /* The model's prior, before it has learned anything: a quarter zone. */
    CHECK("the prior is a quarter zone and the cfg's floor", fl.dip_rad == quarter && fl.floor_z_m == floor_z);
    tt_clear_step(&c, &fl, &tof, nose_dn, straight, 2, &o);
    CHECK("...with which the brake's floor is the floor", o.clear_m == TT_CLEAR_NONE);

    /* Learned at cruise, level, the mount as told: the quarter zone and the
     * floor where it is. */
    for (i = 0; i < 30; i++) {
        frame_sub(&c, 0.0f, 2, 0.0f, 0.0f, 0, 0, 0.0f, &tof);
        tt_clear_floor_learn(&fl, &c, &tof, 0.0f, 0.9f);
    }
    printf("learned, mount as told: dip %.3f deg (a quarter zone %.3f), floor %+.4f m, %.0f returns\n",
           (double)(fl.dip_rad * 57.29578f), (double)(quarter * 57.29578f), (double)fl.floor_z_m, (double)fl.w);
    CHECK("the dip learned is a quarter zone", near(fl.dip_rad, quarter, 0.001f));
    CHECK("...and the floor where it is", near(fl.floor_z_m, floor_z, 0.001f));
    frame_sub(&c, 0.0f, 2, nose_dn, dive, 0, 0, 0.0f, &tof);
    tt_clear_step(&c, &fl, &tof, nose_dn, straight, 2, &o);
    CHECK("a hard brake: the floor in the lower zones is the floor",
          o.clear_m == TT_CLEAR_NONE && o.n_floor == o.n_valid);

    /* Something standing up is still in the way, at its distance. */
    {
        Face bar = { 1.5f, -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        Face low = { 0.9f, -0.3f, 0.3f, floor_z, floor_z + 0.08f };
        frame_sub(&c, 0.0f, 2, 0.0f, 0.0f, &bar, 1, 0.0f, &tof);
        tt_clear_step(&c, &fl, &tof, 0.0f, straight, 2, &o);
        CHECK("learned: a barrier at 1.5 m reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
        frame_sub(&c, 0.0f, 2, 0.0f, 0.0f, &low, 1, 0.0f, &tof);
        tt_clear_step(&c, &fl, &tof, 0.0f, straight, 2, &o);
        CHECK("...a box 8 cm high at 0.9 m reads 0.9 m", near(o.clear_m, 0.9f, 0.02f));
        frame_sub(&c, 0.0f, 2, nose_dn, dive, &low, 1, 0.0f, &tof);
        tt_clear_step(&c, &fl, &tof, nose_dn, straight, 2, &o);
        CHECK("...and under the brake", near(o.clear_m, 0.9f, 0.02f));
    }

    /* The mount 1.5 deg down: learned again, with a barrier closing on the
     * car through the frames, 0.3 m a frame from 3 m, and a wall along the
     * path 0.35 m to the left: neither is in the fit. */
    tt_clear_floor_init(&fl, &c);
    for (i = 0; i < 30; i++) {
        Face bar = { 3.0f - 0.3f * (float)(i % 8), -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        frame_sub(&c, mount, 2, 0.0f, 0.0f, &bar, 1, 0.35f, &tof);
        tt_clear_floor_learn(&fl, &c, &tof, 0.0f, 0.9f);
    }
    printf("learned, mount 1.5 deg down, a barrier closing, a wall beside: dip %.3f deg, floor %+.4f m\n",
           (double)(fl.dip_rad * 57.29578f), (double)fl.floor_z_m);
    CHECK("the mount's 1.5 deg is in the dip", near(fl.dip_rad, quarter + mount, 0.002f));
    CHECK("...the floor where it is", near(fl.floor_z_m, floor_z, 0.002f));
    frame_sub(&c, mount, 2, nose_dn, dive, 0, 0, 0.0f, &tof);
    tt_clear_step(&c, &fl, &tof, nose_dn, straight, 2, &o);
    CHECK("...the floor under the brake is the floor", o.clear_m == TT_CLEAR_NONE);
    {
        Face bar = { 1.5f, -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        frame_sub(&c, mount, 2, 0.0f, 0.0f, &bar, 1, 0.0f, &tof);
        tt_clear_step(&c, &fl, &tof, 0.0f, straight, 2, &o);
        CHECK("...a barrier at 1.5 m reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
    }
    /* The map's rays agree, and a floor's ray ends under the band of
     * heights the map frees: on the zone's centre it would end above it,
     * at the foot of whatever stands there. */
    {
        float org[3], end[3], cb = cosf(nose_dn), sb = sinf(nose_dn);
        int hit, hits = 0, high = 0, k;
        frame_sub(&c, mount, 2, nose_dn, dive, 0, 0, 0.0f, &tof);
        for (k = 0; k < 64; k++)
            if (tt_clear_ray(&c, &fl, &tof, nose_dn, k, org, end, &hit)) {
                hits += hit;
                if (!hit && tof.status[k] == 0 &&
                    end[2] * cb - end[0] * sb >= fl.floor_z_m + c.floor_margin_m) high++;
            }
        CHECK("...the map's rays find no floor in the way either", hits == 0);
        CHECK("...and end under the band the map frees", high == 0);
    }
}

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
    tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
    CHECK("the floor alone is seen", o.n_valid > 10);
    CHECK("...and dropped", o.n_floor == o.n_valid && o.n_in == 0);
    CHECK("...so the way is clear", o.clear_m == TT_CLEAR_NONE);

    /* A barrier across the path at 1.5 m (0.6 wide, 0.25 high, on the floor). */
    {
        Face box = { 1.5f, -0.3f, 0.3f, floor_z, floor_z + 0.25f };
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
        CHECK("a barrier at 1.5 m reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
        CHECK("...inside the corridor", near(o.y_m, 0.0f, c.half_width_m));
        CHECK("...from several zones", o.n_in >= 4);

        /* Braking: 2 deg nose down. The barrier stays; the floor still goes. */
        frame(&c, 0.035f, &box, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.035f, straight, 2, &o);
        CHECK("nose down 2 deg: the barrier still reads 1.5 m", near(o.clear_m, 1.5f, 0.02f));
        /* ...and the pitch forgotten: the floor, now in the lower zones at a
         * metre or so, would read as a wall. */
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
        CHECK("...which needs the pitch: without it the floor is in the way", o.clear_m < 1.4f);
    }

    /* A cone beside the path (0.45 m to the left, 0.14 across, 0.18 high),
     * 1.6 m ahead: inside the 45 deg field of view, outside the corridor. */
    {
        Face cone = { 1.6f, 0.38f, 0.52f, floor_z, floor_z + 0.18f };
        frame(&c, 0.0f, &cone, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
        CHECK("a cone 0.45 m beside the path is seen", o.n_valid > o.n_floor);
        CHECK("...and not in the way", o.clear_m == TT_CLEAR_NONE && o.n_in == 0);
    }

    /* A box on a left curve: in the way of the curve, not of a straight path. */
    {
        Face box = { 1.86f, 0.44f, 0.84f, floor_z, floor_z + 0.25f };   /* (1.86, 0.64): the arc at s = 2 */
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.0f, curve, 40, &o);
        printf("curve: clear %.3f m, y %+.3f, %d in, %d floor, %d valid\n",
               (double)o.clear_m, (double)o.y_m, o.n_in, o.n_floor, o.n_valid);
        CHECK("a box on the curve ahead is in the way", o.clear_m < 2.3f && o.n_in > 0);
        CHECK("...at about its arc length (its face spans the curve)", near(o.clear_m, 1.9f, 0.3f));
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
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
        tt_clear_step(&c, 0, &tof, 0.0f, dense, 24, &o);
        CHECK("a barrier at 1.5 m reads 1.5 m against a dense path too", near(o.clear_m, 1.5f, 0.02f));
        /* ...and right in front of the nose, the end of the path cap still
         * counts: a wall 0.3 m from the sensor. */
        box.x = 0.5f;
        frame(&c, 0.0f, &box, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.0f, dense, 24, &o);
        CHECK("a barrier at 0.5 m reads 0.5 m", near(o.clear_m, 0.5f, 0.02f));
    }

    /* A banner across the path 0.5 m up (the proving ground's finish): the
     * car passes under it. */
    {
        Face banner = { 2.0f, -0.8f, 0.8f, 0.45f, 0.6f };
        c.roof_z_m = 0.15f;
        frame(&c, 0.0f, &banner, 1, &tof);
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
        CHECK("a banner 0.5 m up is seen", o.n_valid > o.n_floor - 8);
        CHECK("...and passed under", o.clear_m == TT_CLEAR_NONE);
        c.roof_z_m = 0.0f;
        tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
        CHECK("...which the ceiling decides", o.clear_m < 2.1f);
    }

    floor_learned();

    /* A short frame (a single-beam ranger) is not judged. */
    tof.zones = 1;
    tt_clear_step(&c, 0, &tof, 0.0f, straight, 2, &o);
    CHECK("a single-zone frame is not judged", o.clear_m == TT_CLEAR_NONE && o.n_valid == 0);

    printf("%d check(s) failed\n", g_fail);
    return g_fail ? 1 : 0;
}
