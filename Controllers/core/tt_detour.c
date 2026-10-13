/*
 * tt_detour.c — a detour beside the route (NAV-08). See tt_detour.h.
 */
#include "tt_detour.h"

#include <math.h>
#include <string.h>

#define DT_PI 3.14159265358979323846f

/* A raised cosine from 0 to 1 over u in [0, 1], with its first and second
 * derivatives in u. */
static float ease(float u, float *du, float *ddu)
{
    float su = sinf(DT_PI * u), cu = cosf(DT_PI * u);
    if (du)  *du  = 0.5f * DT_PI * su;
    if (ddu) *ddu = 0.5f * DT_PI * DT_PI * cu;
    return 0.5f * (1.0f - cu);
}

float tt_detour_at(const TtDetour *d, float s, float *dd, float *ddd)
{
    float v = 0.0f, dv = 0.0f, ddv = 0.0f;
    if (!d || !d->active) {
        /* nothing */
    } else if (s <= d->s0) {
        v = d->d0;
    } else if (s < d->s1) {
        float L = d->s1 - d->s0, du, ddu, f = ease((s - d->s0) / L, &du, &ddu);
        v = d->d0 + (d->d1 - d->d0) * f;
        dv = (d->d1 - d->d0) * du / L;
        ddv = (d->d1 - d->d0) * ddu / (L * L);
    } else if (s <= d->s2) {
        v = d->d1;
    } else if (s < d->s3) {
        float L = d->s3 - d->s2, du, ddu, f = ease((s - d->s2) / L, &du, &ddu);
        v = d->d1 * (1.0f - f);
        dv = -d->d1 * du / L;
        ddv = -d->d1 * ddu / (L * L);
    }
    if (dd) *dd = dv;
    if (ddd) *ddd = ddv;
    return v;
}

void tt_detour_point(const TtRoute *r, const TtDetour *d, float s, uint32_t *hint,
                     float *x, float *y, float *psi, float *kappa)
{
    float rx, ry, rpsi, rk, off, dd, ddd, den;
    tt_route_at(r, s, hint, &rx, &ry, &rpsi, &rk);
    off = tt_detour_at(d, s, &dd, &ddd);
    if (x) *x = rx - off * sinf(rpsi);
    if (y) *y = ry + off * cosf(rpsi);
    if (psi) *psi = rpsi + atanf(dd);
    if (kappa) {
        den = 1.0f - off * rk;
        if (den < 0.2f) den = 0.2f;     /* an offset inside a tight bend: bounded */
        *kappa = (rk + ddd) / den;
    }
}

void tt_detour_default_cfg(TtDetourCfg *c)
{
    memset(c, 0, sizeof(*c));
    c->half_width_m = 0.23f;     /* the twin's 0.10 half width, the corridor's 0.08, a cell */
    c->unknown_w_m = 0.26f;      /* with the disc, 0.6 m from the centroid: a box
                                    0.6 m wide centred on the route gets 0.6 */
    c->min_cells = 2;            /* one cell is a return, not a map; three
                                    cost a 3 m/s car the room it needs     */
    c->lead_m = 0.3f;
    c->lead_s = 0.2f;
    c->ramp_m = 2.5f;
    c->ramp_min_m = 1.0f;
    c->ramp_out_m = 1.6f;        /* 0.6 m back in 1.6 m: 1.16/m, the car slows to 1.5 m/s for it */
    c->reach_m = 0.45f;          /* the nose and the corridor's margin */
    c->past_m = 0.4f;            /* the car's tail and a margin */
    c->look_m = 4.0f;
    c->step_m = 0.1f;
    c->kappa_max = 1.2f;         /* under the twin's 1.6 lock, with the route's own to add */
    c->offset_m[0] = 0.45f;
    c->offset_m[1] = 0.6f;
    c->offset_m[2] = 0.8f;
    c->offset_m[3] = 1.0f;
    c->n_offsets = 4;
}

float tt_detour_blocked(const TtDetourCfg *c, const TtGrid *g, const TtRoute *r,
                        const TtDetour *d, float s, float look, float *y_block)
{
    float s_end = r->pt[r->n - 1u].s, sk;
    uint32_t hint = 0u;
    if (y_block) *y_block = 0.0f;
    if (s + look > s_end) look = s_end - s;
    for (sk = s; sk <= s + look + 1e-4f; sk += c->step_m) {
        float x, y, psi, cx, cy;
        tt_detour_point(r, d, sk, &hint, &x, &y, &psi, 0);
        if (tt_grid_occupied(g, x, y, c->half_width_m)) {
            if (y_block) {
                /* The centroid of what stands there, as far as the lattice
                 * reaches, as an offset from the ROUTE: the side with more
                 * room is the one away from it. */
                float rx, ry, rpsi, reach = c->half_width_m + c->unknown_w_m + 0.5f;
                tt_route_at(r, sk + c->half_width_m, &hint, &rx, &ry, &rpsi, 0);
                if (tt_grid_centroid(g, rx, ry, reach, &cx, &cy))
                    *y_block = -(cx - rx) * sinf(rpsi) + (cy - ry) * cosf(rpsi);
            }
            return sk;
        }
    }
    return -1.0f;
}

/* The first s at or beyond s_from where the bare route is free for three
 * steps running; -1 if not within `within`. */
static float route_free_from(const TtDetourCfg *c, const TtGrid *g, const TtRoute *r,
                             float s_from, float within)
{
    float s_end = r->pt[r->n - 1u].s, sk;
    uint32_t hint = 0u;
    int run = 0;
    for (sk = s_from; sk <= s_from + within && sk <= s_end; sk += c->step_m) {
        float x, y;
        tt_route_at(r, sk, &hint, &x, &y, 0, 0);
        if (tt_grid_occupied(g, x, y, c->half_width_m)) run = 0;
        else if (++run >= 3) return sk - 2.0f * c->step_m;
    }
    return -1.0f;
}

static float peak_kappa(float height, float len)
{
    return 0.5f * DT_PI * DT_PI * (height < 0.0f ? -height : height) / (len * len);
}

int tt_detour_plan(const TtDetourCfg *c, const TtGrid *g, const TtRoute *r,
                   float s, float v, float d_now, float s_block, float y_block,
                   TtDetour *out)
{
    const float s_end = r->pt[r->n - 1u].s;
    float s0 = s + c->lead_m + (v > 0.0f ? v : 0.0f) * c->lead_s, L_in, s_free, s2;
    /* The side: the one the car is already on, else away from the block
     * (a block on the route's centre: the left). */
    float first = d_now > 0.05f ? 1.0f : d_now < -0.05f ? -1.0f : (y_block > 0.05f ? -1.0f : 1.0f);
    int i, side;

    /* Enough of a map to plan on: the first return of a block 3 m out is a
     * cell or two, and what it belongs to is not yet known. */
    {
        float rx, ry, cx, cy;
        uint32_t h = 0u;
        tt_route_at(r, s_block + c->half_width_m, &h, &rx, &ry, 0, 0);
        if (tt_grid_centroid(g, rx, ry, c->half_width_m + c->unknown_w_m + 0.5f, &cx, &cy) < c->min_cells)
            return 0;
    }

    /* The room to ease out: up to ramp_m, but done reach_m before the block. */
    L_in = c->ramp_m;
    if (s0 + L_in > s_block - c->reach_m) L_in = s_block - c->reach_m - s0;
    if (L_in < c->ramp_min_m) return 0;

    /* Where the route is free again, and so where the hold ends. */
    s_free = route_free_from(c, g, r, s_block, 3.0f);
    if (s_free < 0.0f) return 0;
    s2 = s_free + c->past_m;

    for (i = 0; i < c->n_offsets; i++) {
        for (side = 0; side < 2; side++) {
            TtDetour cand;
            float d1 = (side == 0 ? first : -first) * c->offset_m[i];
            /* The ease-back as long as its curvature needs, at least ramp_out_m. */
            float L_out = sqrtf(0.5f * DT_PI * DT_PI * c->offset_m[i] / (0.95f * c->kappa_max)), s3;
            float k_in = peak_kappa(d1 - d_now, L_in), k_out;
            float room = d1 - y_block;
            if (L_out < c->ramp_out_m) L_out = c->ramp_out_m;
            k_out = peak_kappa(d1, L_out);
            s3 = s2 + L_out;
            if (s3 > s_end) continue;
            if (room < 0.0f) room = -room;
            if (room < c->half_width_m + c->unknown_w_m) continue;
            if (k_in > c->kappa_max || k_out > c->kappa_max) continue;
            memset(&cand, 0, sizeof(cand));
            cand.active = 1;
            cand.s0 = s0;
            cand.s1 = s0 + L_in;
            cand.s2 = s2;
            cand.s3 = s3;
            cand.d0 = d_now;
            cand.d1 = d1;
            cand.kappa_peak = k_in > k_out ? k_in : k_out;
            if (tt_detour_blocked(c, g, r, &cand, s, s3 + 0.5f - s, 0) < 0.0f) {
                *out = cand;
                return 1;
            }
        }
    }
    return 0;
}
