/*
 * tt_vplan.c — the speed plan along a route (NAV-03). See tt_vplan.h.
 */
#include "tt_vplan.h"

#include <math.h>
#include <string.h>

void tt_vplan_build(TtVPlan *p, const TtRoute *r, float v_max, float a_lat,
                    float a_drive, float a_brake, float v_end)
{
    uint32_t i, n = r->n < TT_ROUTE_MAX_PTS ? r->n : TT_ROUTE_MAX_PTS;
    memset(p, 0, sizeof(*p));
    p->n = n;
    p->v_max = v_max > 0.0f ? v_max : r->v_max;
    p->a_lat = a_lat > 0.0f ? a_lat : r->a_lat_max;
    p->a_drive = a_drive > 0.0f ? a_drive : r->a_drive;
    p->a_brake = a_brake > 0.0f ? a_brake : r->a_brake;
    if (n == 0u) return;

    for (i = 0; i < n; i++) {
        const TtRoutePt *q = &r->pt[i];
        float v = p->v_max, k = fabsf(q->kappa);
        if (q->v_cap > 0.0f && q->v_cap < v) v = q->v_cap;
        if (k > 1e-6f) {
            float vk = sqrtf(p->a_lat / k);
            if (vk < v) v = vk;
        }
        p->v[i] = v;
    }
    if (v_end >= 0.0f && v_end < p->v[n - 1u]) p->v[n - 1u] = v_end;
    /* backward: slow down in time for what is coming */
    for (i = n - 1u; i > 0u; i--) {
        float ds = r->pt[i].s - r->pt[i - 1u].s;
        float vb = sqrtf(p->v[i] * p->v[i] + 2.0f * p->a_brake * ds);
        if (vb < p->v[i - 1u]) p->v[i - 1u] = vb;
    }
    /* forward: no faster than the car can accelerate to (from the plan's
     * own first point; the launch ramps up to it in time) */
    for (i = 1u; i < n; i++) {
        float ds = r->pt[i].s - r->pt[i - 1u].s;
        float vf = sqrtf(p->v[i - 1u] * p->v[i - 1u] + 2.0f * p->a_drive * ds);
        if (vf < p->v[i]) p->v[i] = vf;
    }
}

float tt_vplan_at(const TtVPlan *p, const TtRoute *r, float s, uint32_t *hint,
                  float *dv2ds)
{
    uint32_t i;
    float t, a2, b2, ds;
    if (p->n < 2u) { if (dv2ds) *dv2ds = 0.0f; return p->n ? p->v[0] : 0.0f; }
    i = tt_route_at(r, s, hint, 0, 0, 0, 0);
    if (i > p->n - 2u) i = p->n - 2u;
    ds = r->pt[i + 1u].s - r->pt[i].s;
    if (s < r->pt[0].s) s = r->pt[0].s;
    if (s > r->pt[p->n - 1u].s) s = r->pt[p->n - 1u].s;
    t = ds > 0.0f ? (s - r->pt[i].s) / ds : 0.0f;
    a2 = p->v[i] * p->v[i];
    b2 = p->v[i + 1u] * p->v[i + 1u];
    if (dv2ds) *dv2ds = ds > 0.0f ? (b2 - a2) / ds : 0.0f;
    return sqrtf(a2 + t * (b2 - a2));
}

float tt_vplan_min(const TtVPlan *p, const TtRoute *r, float s, float d, uint32_t *hint)
{
    uint32_t i, h = hint ? *hint : 0u;
    float v = tt_vplan_at(p, r, s, &h, 0), se = s + d;
    for (i = h + 1u; i < p->n && r->pt[i].s <= se; i++)
        if (p->v[i] < v) v = p->v[i];
    if (hint) *hint = h;
    return v;
}
