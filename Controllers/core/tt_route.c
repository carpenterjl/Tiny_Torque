/*
 * tt_route.c — route tables: lookup, validation, interpolation, projection.
 * See tt_route.h.
 */
#include "tt_route.h"

#include <math.h>
#include <string.h>

#define PI_F 3.14159265f

float tt_route_wrap(float a)
{
    while (a > PI_F) a -= 2.0f * PI_F;
    while (a <= -PI_F) a += 2.0f * PI_F;
    return a;
}

const TtRoute *tt_route_find(const char *name)
{
    int i;
    if (name == 0) return 0;
    for (i = 0; tt_routes[i] != 0; i++)
        if (strcmp(tt_routes[i]->name, name) == 0) return tt_routes[i];
    return 0;
}

/* ---- CRC-32 over the serialised route ----------------------------------- */

static uint32_t crc_byte(uint32_t c, uint8_t b)
{
    int k;
    c ^= b;
    for (k = 0; k < 8; k++) c = (c & 1u) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
    return c;
}

static uint32_t crc_u32(uint32_t c, uint32_t v)
{
    c = crc_byte(c, (uint8_t)(v & 0xFFu));
    c = crc_byte(c, (uint8_t)((v >> 8) & 0xFFu));
    c = crc_byte(c, (uint8_t)((v >> 16) & 0xFFu));
    return crc_byte(c, (uint8_t)((v >> 24) & 0xFFu));
}

static uint32_t crc_f32(uint32_t c, float f)
{
    uint32_t v;
    memcpy(&v, &f, sizeof(v));
    return crc_u32(c, v);
}

uint32_t tt_route_crc(const TtRoute *r)
{
    uint32_t c = 0xFFFFFFFFu, i;
    c = crc_u32(c, r->n);
    for (i = 0; i < r->n; i++) {
        const TtRoutePt *p = &r->pt[i];
        c = crc_f32(c, p->x);
        c = crc_f32(c, p->y);
        c = crc_f32(c, p->s);
        c = crc_f32(c, p->psi);
        c = crc_f32(c, p->kappa);
        c = crc_f32(c, p->v_cap);
    }
    for (i = 0; i < 3u; i++) c = crc_f32(c, r->start[i]);
    c = crc_byte(c, r->has_start);
    c = crc_byte(c, r->stop_at_end);
    c = crc_f32(c, r->v_max);
    c = crc_f32(c, r->a_lat_max);
    c = crc_f32(c, r->a_drive);
    c = crc_f32(c, r->a_brake);
    return c ^ 0xFFFFFFFFu;
}

static int finite_(float v)
{
    return (v == v) && v < 1e18f && v > -1e18f;
}

int tt_route_validate(const TtRoute *r, float kappa_max)
{
    uint32_t i;
    if (r == 0 || r->pt == 0 || r->n < 2u || r->n > TT_ROUTE_MAX_PTS) return TT_RTE_EMPTY;
    if (!(r->v_max > 0.0f && r->a_lat_max > 0.0f && r->a_drive > 0.0f && r->a_brake > 0.0f))
        return TT_RTE_LIMITS;
    if (r->pt[0].s != 0.0f) return TT_RTE_S;
    for (i = 0; i < r->n; i++) {
        const TtRoutePt *p = &r->pt[i];
        if (!finite_(p->x) || !finite_(p->y) || !finite_(p->s) || !finite_(p->psi) ||
            !finite_(p->kappa) || !finite_(p->v_cap)) return TT_RTE_NAN;
        if (kappa_max > 0.0f && fabsf(p->kappa) > kappa_max) return TT_RTE_KAPPA;
        if (i > 0) {
            const TtRoutePt *q = &r->pt[i - 1];
            float ds = p->s - q->s, dx = p->x - q->x, dy = p->y - q->y;
            float d = sqrtf(dx * dx + dy * dy), ch;
            if (!(ds > 0.0f)) return TT_RTE_S;
            if (fabsf(d - ds) > 0.01f * ds + 0.001f) return TT_RTE_GEOM;
            /* the chord runs along the mean of the end headings */
            ch = atan2f(dy, dx);
            if (fabsf(tt_route_wrap(ch - 0.5f * (p->psi + q->psi))) > 0.05f) return TT_RTE_HEADING;
        }
    }
    if (tt_route_crc(r) != r->crc) return TT_RTE_CRC;
    return TT_RTE_OK;
}

/* ---- along the route ------------------------------------------------------ */

uint32_t tt_route_at(const TtRoute *r, float s, uint32_t *hint,
                     float *x, float *y, float *psi, float *kappa)
{
    uint32_t i = hint ? *hint : 0u;
    const TtRoutePt *a, *b;
    float t, ds;
    if (s < 0.0f) s = 0.0f;
    if (s > r->pt[r->n - 1u].s) s = r->pt[r->n - 1u].s;
    if (i > r->n - 2u) i = r->n - 2u;
    while (i > 0u && r->pt[i].s > s) i--;
    while (i < r->n - 2u && r->pt[i + 1u].s < s) i++;
    a = &r->pt[i];
    b = &r->pt[i + 1u];
    ds = b->s - a->s;
    t = ds > 0.0f ? (s - a->s) / ds : 0.0f;
    if (x) *x = a->x + t * (b->x - a->x);
    if (y) *y = a->y + t * (b->y - a->y);
    if (psi) *psi = a->psi + t * (b->psi - a->psi);
    if (kappa) *kappa = a->kappa + t * (b->kappa - a->kappa);
    if (hint) *hint = i;
    return i;
}

void tt_route_project(const TtRoute *r, float x, float y, uint32_t from,
                      uint32_t back, uint32_t ahead, TtRouteProj *out)
{
    uint32_t i0, i1, i, best_i = 0u;
    float best_d2 = 1e30f, best_t = 0.0f;
    if (from > r->n - 2u) from = r->n - 2u;
    i0 = from > back ? from - back : 0u;
    i1 = from + ahead < r->n - 1u ? from + ahead : r->n - 2u;
    for (i = i0; i <= i1; i++) {
        const TtRoutePt *a = &r->pt[i], *b = &r->pt[i + 1u];
        float ex = b->x - a->x, ey = b->y - a->y, l2 = ex * ex + ey * ey;
        float t = l2 > 0.0f ? ((x - a->x) * ex + (y - a->y) * ey) / l2 : 0.0f, dx, dy, d2;
        /* Past either end only on the route's own ends: elsewhere the next
         * segment covers it. */
        if (t < 0.0f && i > 0u) t = 0.0f;
        if (t > 1.0f && i + 2u < r->n) t = 1.0f;
        dx = a->x + t * ex - x;
        dy = a->y + t * ey - y;
        d2 = dx * dx + dy * dy;
        if (d2 < best_d2) { best_d2 = d2; best_i = i; best_t = t; }
    }
    {
        const TtRoutePt *a = &r->pt[best_i], *b = &r->pt[best_i + 1u];
        float tc = best_t < 0.0f ? 0.0f : (best_t > 1.0f ? 1.0f : best_t);
        float ex = b->x - a->x, ey = b->y - a->y, l = sqrtf(ex * ex + ey * ey);
        float px = a->x + best_t * ex, py = a->y + best_t * ey;
        out->i = best_i;
        out->s = a->s + best_t * (b->s - a->s);
        out->psi = a->psi + tc * (b->psi - a->psi);
        out->kappa = a->kappa + tc * (b->kappa - a->kappa);
        /* + = left of the direction of travel */
        out->ey = l > 0.0f ? ((x - px) * (-ey) + (y - py) * ex) / l : 0.0f;
    }
}
