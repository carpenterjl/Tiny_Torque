/*
 * tt_clear.c — the clear distance ahead, from a multizone ToF (NAV-05).
 * See tt_clear.h.
 */
#include "tt_clear.h"

#include <math.h>

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

void tt_clear_zone_dir(const TtClearCfg *c, int row, int col, float dir[3])
{
    int n = c->zones_side > 0 ? c->zones_side : 1;
    float zone = c->fov_rad / (float)n;
    float yaw   = -0.5f * c->fov_rad + ((float)col + 0.5f) * zone;   /* + = right */
    float pitch = -0.5f * c->fov_rad + ((float)row + 0.5f) * zone;   /* + = down  */
    float cp = cosf(pitch);
    dir[0] = cp * cosf(yaw);
    dir[1] = -cp * sinf(yaw);     /* FLU: left is +y, so a zone to the right is -y */
    dir[2] = -sinf(pitch);        /* and a zone below the aim is -z             */
}

float tt_clear_step(const TtClearCfg *c, const TtTof *tof, float pitch_rad,
                    const TtClearPt *path, int n, TtClearOut *out)
{
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
        float d = tof->range_m[k], ds[3], db[3], px, py, pz;
        float best = TT_CLEAR_NONE, best_y = 0.0f;
        int i;
        if (tof->status[k] != 0 || !(d > 0.0f) || (c->max_range_m > 0.0f && d >= c->max_range_m - 1e-3f))
            continue;
        out->n_valid++;

        /* The return in the body frame. */
        tt_clear_zone_dir(c, k / side, k % side, ds);
        rotate_rpy(c->rpy_rad, ds, db);
        px = c->pos_m[0] + d * db[0];
        py = c->pos_m[1] + d * db[1];
        pz = c->pos_m[2] + d * db[2];

        /* The floor: a nose-down body (pitch +) lowers what is ahead of it.
         * And the ceiling: what the car passes under. */
        {
            float zw = pz * cb - px * sb;
            if (zw < c->floor_z_m + c->floor_margin_m) { out->n_floor++; continue; }
            if (c->roof_z_m > 0.0f && zw > c->roof_z_m) { out->n_floor++; continue; }
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
