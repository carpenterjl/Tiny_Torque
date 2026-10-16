/*
 * tt_clear.c — the clear distance ahead, from a multizone ToF (NAV-05).
 * See tt_clear.h.
 */
#include "tt_clear.h"

#include <math.h>
#include <string.h>

#define TT_CLEAR_DIP_MAX    0.10f   /* rad: the fit's reach either side of the centre */
#define TT_CLEAR_FLOOR_MOVE 0.04f   /* m: ...and of the cfg's floor                    */

/* Rotate a vector by Z-Y-X Euler angles (yaw about z, then pitch about y,
 * then roll about x), the convention SensorInfo2.rpy_rad uses. */
static void rotate_rpy(const float rpy[3], const float v[3], float out[3])
{
    float cr = cosf(rpy[0]), sr = sinf(rpy[0]);
    float cp = cosf(rpy[1]), sp = sinf(rpy[1]);
    float cy = cosf(rpy[2]), sy = sinf(rpy[2]);
    /* R = Rz(yaw) Ry(pitch) Rx(roll) */
    float r00 = cy * cp,  r01 = cy * sp * sr - sy * cr, r02 = cy * sp * cr + sy * sr;
    float r10 = sy * cp,  r11 = sy * sp * sr + cy * cr, r12 = sy * sp * cr - cy * sr;
    float r20 = -sp,      r21 = cp * sr,                r22 = cp * cr;
    out[0] = r00 * v[0] + r01 * v[1] + r02 * v[2];
    out[1] = r10 * v[0] + r11 * v[1] + r12 * v[2];
    out[2] = r20 * v[0] + r21 * v[1] + r22 * v[2];
}

/* The direction at (rowf, colf) in zone units: row + 0.5 is a zone's
 * centre, row + 1 its lower edge. */
static void zone_dir_at(const TtClearCfg *c, float rowf, float colf, float dir[3])
{
    int n = c->zones_side > 0 ? c->zones_side : 1;
    float zone = c->fov_rad / (float)n;
    float yaw   = -0.5f * c->fov_rad + colf * zone;   /* + = right */
    float pitch = -0.5f * c->fov_rad + rowf * zone;   /* + = down  */
    float cp = cosf(pitch);
    dir[0] = cp * cosf(yaw);
    dir[1] = -cp * sinf(yaw);     /* FLU: left is +y, so a zone to the right is -y */
    dir[2] = -sinf(pitch);        /* and a zone below the aim is -z             */
}

void tt_clear_zone_dir(const TtClearCfg *c, int row, int col, float dir[3])
{
    zone_dir_at(c, (float)row + 0.5f, (float)col + 0.5f, dir);
}

/* The height (pitch taken out) of the zone's lower edge at range d: what
 * the roof is judged on. */
static float lower_edge_z(const TtClearCfg *c, int row, int col, float d, float cb, float sb)
{
    float ds[3], db[3], px, pz;
    zone_dir_at(c, (float)row + 1.0f, (float)col + 0.5f, ds);
    rotate_rpy(c->rpy_rad, ds, db);
    px = c->pos_m[0] + d * db[0];
    pz = c->pos_m[2] + d * db[2];
    return pz * cb - px * sb;
}

/* A return's height over the world's level (pitch taken out) as the floor
 * would put it: on the ray `dip` below the zone's centre direction `db`
 * (body frame), at range d. *g is how much lower it goes per radian more
 * dip, for the fit. With no dip it is the centre's point exactly. */
static float floor_zw(const TtClearCfg *c, const float db[3], float dip, float d,
                      float cb, float sb, float *g)
{
    float h = sqrtf(db[0] * db[0] + db[1] * db[1]), hx, z, px, pz;
    if (dip == 0.0f || !(h > 1e-6f)) {
        hx = db[0];
        z = db[2];
        if (g) *g = d * (h * cb + (h > 1e-6f ? db[0] / h : 0.0f) * z * sb);
    } else {
        float cd = cosf(dip), sd = sinf(dip), hd = h * cd + db[2] * sd;
        z = db[2] * cd - h * sd;            /* the ray tipped down by dip */
        hx = db[0] / h * hd;
        if (g) *g = d * (hd * cb + db[0] / h * z * sb);
    }
    px = c->pos_m[0] + d * hx;
    pz = c->pos_m[2] + d * z;
    return pz * cb - px * sb;
}

static int is_none(const TtClearCfg *c, const TtTof *tof, int k)
{
    float d = tof->range_m[k];
    return tof->status[k] != 0 || !(d > 0.0f) || (c->max_range_m > 0.0f && d >= c->max_range_m - 1e-3f);
}

/* The prior's dip: a quarter zone, the middle of a zone's nearer half. */
static float dip_prior(const TtClearCfg *c)
{
    return 0.25f * c->fov_rad / (float)(c->zones_side > 0 ? c->zones_side : 1);
}

void tt_clear_floor_init(TtClearFloor *f, const TtClearCfg *c)
{
    memset(f, 0, sizeof(*f));
    f->floor_z_m = c->floor_z_m;
    f->dip_rad = dip_prior(c);
}

int tt_clear_floor_learn(TtClearFloor *f, const TtClearCfg *c, const TtTof *tof,
                         float pitch_rad, float keep)
{
    /* The prior: a hundredth of a return at a metre on each number, enough
     * to keep the fit solvable on one row. More drags the dip: the floor's
     * rows lie between 0.35 and 0.75 m, where its height and the dip all but
     * trade for each other. */
    const float w_z = 0.01f, w_dip = 0.01f;
    int side = c->zones_side > 0 ? c->zones_side : 1, zones = side * side, k, taken = 0;
    float cb = cosf(pitch_rad), sb = sinf(pitch_rad);
    if (!tof->st.valid || tof->zones < zones || zones > TT_TOF_MAX_ZONES) return 0;
    if (!(keep >= 0.0f && keep <= 1.0f)) keep = 1.0f;
    f->s1 *= keep; f->sg *= keep; f->sgg *= keep; f->sy *= keep; f->sgy *= keep;
    for (k = 0; k < zones; k++) {
        float ds[3], db[3], g, zw, d = tof->range_m[k], d0 = f->d_prev[k];
        if (is_none(c, tof, k)) { f->d_prev[k] = 0.0f; continue; }
        f->d_prev[k] = d;
        if (d > TT_CLEAR_FLOOR_NEAR_M || !(d0 > 0.0f) || fabsf(d - d0) >= TT_CLEAR_FLOOR_STILL_M)
            continue;                                   /* far, or coming at the car */
        tt_clear_zone_dir(c, k / side, k % side, ds);
        rotate_rpy(c->rpy_rad, ds, db);
        if (!(db[2] < 0.0f)) continue;                 /* only a zone looking down meets the floor */
        zw = floor_zw(c, db, f->dip_rad, d, cb, sb, &g);
        if (zw - f->floor_z_m >= c->floor_margin_m || f->floor_z_m - zw >= c->floor_margin_m)
            continue;                                   /* not the floor as it stands */
        /* zw(dip) - g (dip' - dip) = floor': y = floor' + g dip' */
        {
            float y = zw + g * f->dip_rad;
            f->s1 += 1.0f; f->sg += g; f->sgg += g * g; f->sy += y; f->sgy += g * y;
        }
        taken++;
    }
    {
        float a = f->s1 + w_z, b = f->sg, e = f->sgg + w_dip;
        float r0 = f->sy + w_z * c->floor_z_m, r1 = f->sgy + w_dip * dip_prior(c);
        float det = a * e - b * b;
        if (det > 1e-9f) {
            float z = (r0 * e - b * r1) / det, dip = (a * r1 - b * r0) / det;
            if (dip > TT_CLEAR_DIP_MAX) dip = TT_CLEAR_DIP_MAX;
            if (dip < -TT_CLEAR_DIP_MAX) dip = -TT_CLEAR_DIP_MAX;
            if (z > c->floor_z_m + TT_CLEAR_FLOOR_MOVE) z = c->floor_z_m + TT_CLEAR_FLOOR_MOVE;
            if (z < c->floor_z_m - TT_CLEAR_FLOOR_MOVE) z = c->floor_z_m - TT_CLEAR_FLOOR_MOVE;
            f->floor_z_m = z;
            f->dip_rad = dip;
        }
        f->w = f->s1;
    }
    return taken;
}

float tt_clear_step(const TtClearCfg *c, const TtClearFloor *fl, const TtTof *tof,
                    float pitch_rad, const TtClearPt *path, int n, TtClearOut *out)
{
    float fz = fl ? fl->floor_z_m : c->floor_z_m, dip = fl ? fl->dip_rad : 0.0f;
    int side = c->zones_side > 0 ? c->zones_side : 1;
    int zones = side * side, k;
    float cb = cosf(pitch_rad), sb = sinf(pitch_rad);
    float s_at[TT_CLEAR_MAX_PATH];   /* arc length to each path point */

    out->clear_m = TT_CLEAR_NONE;
    out->y_m = 0.0f;
    out->n_in = out->n_floor = out->n_valid = 0;
    if (!tof->st.valid || tof->zones < zones || zones > TT_TOF_MAX_ZONES || n < 2 ||
        n > (int)TT_CLEAR_MAX_PATH)
        return out->clear_m;
    s_at[0] = 0.0f;
    for (k = 1; k < n; k++) {
        float ex = path[k].x - path[k - 1].x, ey = path[k].y - path[k - 1].y;
        s_at[k] = s_at[k - 1] + sqrtf(ex * ex + ey * ey);
    }

    for (k = 0; k < zones; k++) {
        float d = tof->range_m[k], ds[3], db[3], px, py;
        float best = TT_CLEAR_NONE, best_y = 0.0f;
        int i;
        if (is_none(c, tof, k)) continue;
        out->n_valid++;

        /* The return in the body frame. */
        tt_clear_zone_dir(c, k / side, k % side, ds);
        rotate_rpy(c->rpy_rad, ds, db);
        px = c->pos_m[0] + d * db[0];
        py = c->pos_m[1] + d * db[1];

        /* The floor: a nose-down body (pitch +) lowers what is ahead of it,
         * and a floor return lies on the floor's ray (TtClearFloor). And the
         * ceiling: what the car passes under, judged on the zone's lower
         * edge (see tt_clear.h). */
        {
            float zw = floor_zw(c, db, dip, d, cb, sb, 0);
            if (zw < fz + c->floor_margin_m) { out->n_floor++; continue; }
            if (c->roof_z_m > 0.0f && lower_edge_z(c, k / side, k % side, d, cb, sb) > c->roof_z_m) {
                out->n_floor++;
                continue;
            }
        }

        /* Against the path: the nearest segment, its along distance. */
        for (i = 0; i + 1 < n; i++) {
            float ax = path[i].x, ay = path[i].y;
            float ex = path[i + 1].x - ax, ey = path[i + 1].y - ay;
            float l2 = ex * ex + ey * ey, t, qx, qy, lat, along;
            if (!(l2 > 0.0f)) continue;
            t = ((px - ax) * ex + (py - ay) * ey) / l2;
            /* A point beside this segment, or past the path's end (the end
             * cap), or beside the origin: a point past an inner segment's
             * end belongs to the next segment, not to this one's end (it
             * would read nearer than it is). */
            if (t < 0.0f) { if (i > 0) continue; t = 0.0f; }
            if (t > 1.0f) { if (i + 2 < n) continue; t = 1.0f; }
            qx = ax + t * ex;
            qy = ay + t * ey;
            /* Its distance to the segment, signed by which side of the
             * path it lies. */
            lat = sqrtf((px - qx) * (px - qx) + (py - qy) * (py - qy));
            if (lat > c->half_width_m) continue;
            if ((px - qx) * -ey + (py - qy) * ex < 0.0f) lat = -lat;   /* + = left of the path */
            along = s_at[i] + t * (s_at[i + 1] - s_at[i]);
            if (along < best) { best = along; best_y = lat; }
        }
        if (best < TT_CLEAR_NONE) {
            out->n_in++;
            if (best < out->clear_m) { out->clear_m = best; out->y_m = best_y; }
        }
    }
    return out->clear_m;
}

int tt_clear_ray(const TtClearCfg *c, const TtClearFloor *fl, const TtTof *tof,
                 float pitch_rad, int zone, float org[3], float end[3], int *hit)
{
    int side = c->zones_side > 0 ? c->zones_side : 1, zones = side * side, none, k;
    float d, ds[3], db[3];
    *hit = 0;
    if (!tof->st.valid || tof->zones < zones || zone < 0 || zone >= zones) return 0;
    d = tof->range_m[zone];
    none = is_none(c, tof, zone);
    if (none) {
        if (!(c->max_range_m > 0.0f)) return 0;
        d = c->max_range_m;
    }
    tt_clear_zone_dir(c, zone / side, zone % side, ds);
    rotate_rpy(c->rpy_rad, ds, db);
    for (k = 0; k < 3; k++) {
        org[k] = c->pos_m[k];
        end[k] = c->pos_m[k] + d * db[k];
    }
    if (!none) {
        float cb = cosf(pitch_rad), sb = sinf(pitch_rad), dip = fl ? fl->dip_rad : 0.0f;
        float zw = floor_zw(c, db, dip, d, cb, sb, 0);
        int on_floor = zw < (fl ? fl->floor_z_m : c->floor_z_m) + c->floor_margin_m;
        *hit = !on_floor &&
               !(c->roof_z_m > 0.0f && lower_edge_z(c, zone / side, zone % side, d, cb, sb) > c->roof_z_m);
        /* The floor's return ends on the floor's ray: a map takes what the
         * ray crosses at the car's height as free, and on the zone's centre
         * the end would lie up to a zone's quarter higher than the floor
         * judged it, at the foot of whatever stands there. */
        if (on_floor && dip != 0.0f) {
            float h = sqrtf(db[0] * db[0] + db[1] * db[1]);
            if (h > 1e-6f) {
                float cd = cosf(dip), sd = sinf(dip), hd = h * cd + db[2] * sd;
                end[0] = c->pos_m[0] + d * db[0] / h * hd;
                end[1] = c->pos_m[1] + d * db[1] / h * hd;
                end[2] = c->pos_m[2] + d * (db[2] * cd - h * sd);
            }
        }
    }
    return 1;
}
