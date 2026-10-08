/*
 * tt_nav.c — planar UWB/odometry/gyro EKF; see tt_nav.h.
 */
#include "tt_nav.h"

#include <math.h>
#include <string.h>

#define N TT_NAV_N
#define PI_F 3.14159265f

float tt_nav_wrap(float a)
{
    while (a > PI_F) a -= 2.0f * PI_F;
    while (a <= -PI_F) a += 2.0f * PI_F;
    return a;
}

void tt_nav_default_cfg(TtNavCfg *c)
{
    memset(c, 0, sizeof(*c));
    c->q_accel = 0.5f;
    c->q_gyro = 0.002f;
    c->q_bias = 0.0005f;
    c->q_scale = 0.002f;
    c->r_speed = 0.05f;
    c->r_flow = 0.05f;
    c->r_range = 0.08f;
    c->r_gyro_rest = 0.005f;
    c->nlos_db = 6.0f;
    c->nlos_scale = 4.0f;
    c->gate = 9.0f;
    c->tag_z = 0.0f;
    c->init_travel_m = 3.0f;
    c->rest_speed = 0.02f;
}

void tt_nav_reset(TtNav *n, const TtNavCfg *c)
{
    memset(n, 0, sizeof(*n));
    n->cfg = *c;
    n->stage = TT_NAV_WAIT_INIT;
}

/* ---- scalar EKF update -------------------------------------------------- */

/* z - h with Jacobian H and variance r; returns 1 when accepted. gate <= 0
 * accepts everything. */
static int update(TtNav *n, float innov, const float H[N], float r, float gate)
{
    float pht[N], k[N], s = r;
    int i, j;
    for (i = 0; i < N; i++) {
        float a = 0.0f;
        for (j = 0; j < N; j++) a += n->P[i * N + j] * H[j];
        pht[i] = a;
        s += H[i] * a;
    }
    if (!(s > 0.0f)) return 0;
    if (gate > 0.0f && innov * innov > gate * s) { n->n_gated++; return 0; }
    for (i = 0; i < N; i++) k[i] = pht[i] / s;
    for (i = 0; i < N; i++) n->s[i] += k[i] * innov;
    for (i = 0; i < N; i++)
        for (j = 0; j < N; j++) n->P[i * N + j] -= k[i] * pht[j];
    /* keep P symmetric with a positive diagonal in float */
    for (i = 0; i < N; i++) {
        for (j = i + 1; j < N; j++) {
            float m = 0.5f * (n->P[i * N + j] + n->P[j * N + i]);
            n->P[i * N + j] = n->P[j * N + i] = m;
        }
        if (n->P[i * N + i] < 1e-9f) n->P[i * N + i] = 1e-9f;
    }
    n->s[TT_NAV_PSI] = tt_nav_wrap(n->s[TT_NAV_PSI]);
    return 1;
}

static void predict(TtNav *n, float dt, float gyro_z, float accel_x)
{
    float *s = n->s, F[N * N], FP[N * N];
    float c = cosf(s[TT_NAV_PSI]), sn = sinf(s[TT_NAV_PSI]), v = s[TT_NAV_V];
    const TtNavCfg *g = &n->cfg;
    int i, j, k;

    s[TT_NAV_X] += v * c * dt;
    s[TT_NAV_Y] += v * sn * dt;
    s[TT_NAV_PSI] = tt_nav_wrap(s[TT_NAV_PSI] + (gyro_z - s[TT_NAV_BG]) * dt);
    s[TT_NAV_V] += accel_x * dt;

    memset(F, 0, sizeof(F));
    for (i = 0; i < N; i++) F[i * N + i] = 1.0f;
    F[TT_NAV_X * N + TT_NAV_PSI] = -v * sn * dt;
    F[TT_NAV_X * N + TT_NAV_V] = c * dt;
    F[TT_NAV_Y * N + TT_NAV_PSI] = v * c * dt;
    F[TT_NAV_Y * N + TT_NAV_V] = sn * dt;
    F[TT_NAV_PSI * N + TT_NAV_BG] = -dt;

    for (i = 0; i < N; i++)
        for (j = 0; j < N; j++) {
            float a = 0.0f;
            for (k = 0; k < N; k++) a += F[i * N + k] * n->P[k * N + j];
            FP[i * N + j] = a;
        }
    for (i = 0; i < N; i++)
        for (j = 0; j < N; j++) {
            float a = 0.0f;
            for (k = 0; k < N; k++) a += FP[i * N + k] * F[j * N + k];
            n->P[i * N + j] = a;
        }
    n->P[TT_NAV_X * N + TT_NAV_X] += 1e-6f * dt;
    n->P[TT_NAV_Y * N + TT_NAV_Y] += 1e-6f * dt;
    n->P[TT_NAV_PSI * N + TT_NAV_PSI] += g->q_gyro * g->q_gyro * dt;
    n->P[TT_NAV_V * N + TT_NAV_V] += g->q_accel * g->q_accel * dt;
    n->P[TT_NAV_BG * N + TT_NAV_BG] += g->q_bias * g->q_bias * dt;
    n->P[TT_NAV_KW * N + TT_NAV_KW] += g->q_scale * g->q_scale * dt;
}

/* Tag position in the world and the range Jacobian. */
static float range_h(const TtNav *n, const float a[3], float H[N])
{
    const float *s = n->s;
    float c = cosf(s[TT_NAV_PSI]), sn = sinf(s[TT_NAV_PSI]);
    float lx = n->cfg.lever[0], ly = n->cfg.lever[1];
    float tx = s[TT_NAV_X] + c * lx - sn * ly;
    float ty = s[TT_NAV_Y] + sn * lx + c * ly;
    float dx = tx - a[0], dy = ty - a[1], dz = n->cfg.tag_z - a[2];
    float rho = sqrtf(dx * dx + dy * dy + dz * dz);
    int i;
    if (rho < 1e-3f) rho = 1e-3f;
    for (i = 0; i < N; i++) H[i] = 0.0f;
    H[TT_NAV_X] = dx / rho;
    H[TT_NAV_Y] = dy / rho;
    H[TT_NAV_PSI] = (dx * (-sn * lx - c * ly) + dy * (c * lx - sn * ly)) / rho;
    return rho;
}

/* ---- initialisation: heading search over the recorded track --------------- */

/* Fit the start position for heading th; returns the robust cost. */
static float fit_start(const TtNav *n, float th, float *x0, float *y0)
{
    float c = cosf(th), sn = sinf(th), cost = 0.0f;
    int it, i;
    for (it = 0; it < 10; it++) {
        /* Plain least squares first (from a far guess every residual is
         * large), then cap outliers once the fit is close. */
        float cap = it < 5 ? 1e6f : 0.5f;
        float a11 = 0.0f, a12 = 0.0f, a22 = 0.0f, b1 = 0.0f, b2 = 0.0f, det, d1, d2;
        cost = 0.0f;
        for (i = 0; i < n->rec_n; i++) {
            const float *t = n->rec_t[i], *a = n->rec_a[i];
            float wx = *x0 + c * t[0] - sn * t[1] - a[0];
            float wy = *y0 + sn * t[0] + c * t[1] - a[1];
            float dz = n->cfg.tag_z - a[2];
            float rho = sqrtf(wx * wx + wy * wy + dz * dz), e, jx, jy;
            if (rho < 1e-3f) rho = 1e-3f;
            e = rho - n->rec_r[i];
            if (fabsf(e) > cap) { cost += cap * cap; continue; }   /* outlier: capped */
            cost += e * e;
            jx = wx / rho; jy = wy / rho;
            a11 += jx * jx; a12 += jx * jy; a22 += jy * jy;
            b1 += jx * e; b2 += jy * e;
        }
        det = a11 * a22 - a12 * a12;
        if (fabsf(det) < 1e-9f) break;
        d1 = (a22 * b1 - a12 * b2) / det;
        d2 = (a11 * b2 - a12 * b1) / det;
        *x0 -= d1; *y0 -= d2;
        if (it >= 5 && fabsf(d1) + fabsf(d2) < 1e-4f) break;
    }
    return cost;
}

static int try_init(TtNav *n, float v_odo, float gyro_bias)
{
    float best = 1e30f, bth = 0.0f, bx = 0.0f, by = 0.0f, cx = 0.0f, cy = 0.0f;
    int i, k;
    if (n->rec_n < 12) return 0;
    for (i = 0; i < n->rec_n; i++) { cx += n->rec_a[i][0]; cy += n->rec_a[i][1]; }
    cx /= (float)n->rec_n; cy /= (float)n->rec_n;
    for (k = 0; k < 72; k++) {
        float th = -PI_F + (float)k * (2.0f * PI_F / 72.0f), x0 = cx, y0 = cy;
        float cst = fit_start(n, th, &x0, &y0);
        if (cst < best) { best = cst; bth = th; bx = x0; by = y0; }
    }
    /* refine the heading around the best coarse cell */
    {
        float lo = bth - 2.0f * PI_F / 72.0f, hi = bth + 2.0f * PI_F / 72.0f;
        for (k = 0; k < 20; k++) {
            float m1 = lo + (hi - lo) * 0.382f, m2 = lo + (hi - lo) * 0.618f;
            float x1 = bx, y1 = by, x2 = bx, y2 = by;
            float c1 = fit_start(n, m1, &x1, &y1), c2 = fit_start(n, m2, &x2, &y2);
            if (c1 < c2) { hi = m2; if (c1 < best) { best = c1; bth = m1; bx = x1; by = y1; } }
            else         { lo = m1; if (c2 < best) { best = c2; bth = m2; bx = x2; by = y2; } }
        }
    }
    n->init_rms = sqrtf(best / (float)n->rec_n);
    if (n->init_rms > 0.25f) return 0;      /* not explained yet: keep driving */

    {
        float c = cosf(bth), sn = sinf(bth), *s = n->s, *P = n->P;
        s[TT_NAV_X] = bx + c * n->lx - sn * n->ly;
        s[TT_NAV_Y] = by + sn * n->lx + c * n->ly;
        s[TT_NAV_PSI] = tt_nav_wrap(bth + n->lpsi);
        s[TT_NAV_V] = v_odo;
        s[TT_NAV_BG] = gyro_bias;
        s[TT_NAV_KW] = 1.0f;
        memset(P, 0, sizeof(n->P));
        P[TT_NAV_X * N + TT_NAV_X] = 0.05f * 0.05f;
        P[TT_NAV_Y * N + TT_NAV_Y] = 0.05f * 0.05f;
        P[TT_NAV_PSI * N + TT_NAV_PSI] = 0.03f * 0.03f;
        P[TT_NAV_V * N + TT_NAV_V] = 0.1f * 0.1f;
        P[TT_NAV_BG * N + TT_NAV_BG] = 0.005f * 0.005f;
        P[TT_NAV_KW * N + TT_NAV_KW] = 0.03f * 0.03f;
    }
    n->stage = TT_NAV_RUN;
    return 1;
}

/* ---- the tick ------------------------------------------------------------- */

void tt_nav_step(TtNav *n, float dt, float gyro_z, float accel_x,
                 float v_odo, float v_flow, int flow_ok,
                 const TtNavRange *ranges, int n_ranges)
{
    const TtNavCfg *g = &n->cfg;
    int i;
    if (!(dt > 0.0f)) return;

    if (n->stage == TT_NAV_WAIT_INIT) {
        /* At rest the gyro reads its own bias: learn it for the local track. */
        if (fabsf(v_odo) < g->rest_speed && n->travel < 0.05f)
            n->s[TT_NAV_BG] += 0.02f * (gyro_z - n->s[TT_NAV_BG]);
        n->lpsi = tt_nav_wrap(n->lpsi + (gyro_z - n->s[TT_NAV_BG]) * dt);
        n->lx += v_odo * cosf(n->lpsi) * dt;
        n->ly += v_odo * sinf(n->lpsi) * dt;
        n->travel += fabsf(v_odo) * dt;
        for (i = 0; i < n_ranges; i++) {
            float c = cosf(n->lpsi), sn = sinf(n->lpsi);
            int k = n->rec_n;
            if (k == TT_NAV_MAX_REC) {      /* full: drop the oldest */
                memmove(&n->rec_t[0], &n->rec_t[1], sizeof(n->rec_t[0]) * (TT_NAV_MAX_REC - 1));
                memmove(&n->rec_a[0], &n->rec_a[1], sizeof(n->rec_a[0]) * (TT_NAV_MAX_REC - 1));
                memmove(&n->rec_r[0], &n->rec_r[1], sizeof(n->rec_r[0]) * (TT_NAV_MAX_REC - 1));
                k = TT_NAV_MAX_REC - 1;
            } else n->rec_n++;
            n->rec_t[k][0] = n->lx + c * g->lever[0] - sn * g->lever[1];
            n->rec_t[k][1] = n->ly + sn * g->lever[0] + c * g->lever[1];
            memcpy(n->rec_a[k], ranges[i].anchor, sizeof(n->rec_a[k]));
            n->rec_r[k] = ranges[i].range_m;
        }
        if (n->travel >= g->init_travel_m) try_init(n, v_odo, n->s[TT_NAV_BG]);
        return;
    }

    predict(n, dt, gyro_z, accel_x);
    {
        float H[N];
        int j;
        for (j = 0; j < N; j++) H[j] = 0.0f;
        if (fabsf(v_odo) < g->rest_speed) {
            /* zero velocity: v = 0, and the gyro reads its bias */
            H[TT_NAV_V] = 1.0f;
            update(n, 0.0f - n->s[TT_NAV_V], H, 0.01f * 0.01f, 0.0f);
            H[TT_NAV_V] = 0.0f; H[TT_NAV_BG] = 1.0f;
            update(n, gyro_z - n->s[TT_NAV_BG], H, g->r_gyro_rest * g->r_gyro_rest, 0.0f);
        } else {
            float kw = n->s[TT_NAV_KW] > 0.5f ? n->s[TT_NAV_KW] : 0.5f;
            H[TT_NAV_V] = 1.0f / kw;
            H[TT_NAV_KW] = -n->s[TT_NAV_V] / (kw * kw);
            update(n, v_odo - n->s[TT_NAV_V] / kw, H, g->r_speed * g->r_speed, 0.0f);
        }
        if (flow_ok) {
            for (j = 0; j < N; j++) H[j] = 0.0f;
            H[TT_NAV_V] = 1.0f;
            update(n, v_flow - n->s[TT_NAV_V], H, g->r_flow * g->r_flow, g->gate);
        }
        for (i = 0; i < n_ranges; i++) {
            float rho = range_h(n, ranges[i].anchor, H);
            float sig = g->r_range * (ranges[i].nlos_db > g->nlos_db ? g->nlos_scale : 1.0f);
            if (update(n, ranges[i].range_m - rho, H, sig * sig, g->gate)) n->n_used++;
        }
    }
}
