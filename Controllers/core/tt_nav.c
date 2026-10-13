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
    /* The odometer's scale is the tyres' rolling radius: it moves with wear
     * and load, not within a run. 0.002 let it wander 0.6 % in 10 s, and the
     * mission measures its legs on it (FW-15): 0.0003 averages over the run. */
    c->q_scale = 0.0003f;
    c->r_speed = 0.05f;
    c->r_flow = 0.05f;
    c->kf_sigma0 = 0.10f;
    c->r_range = 0.08f;
    /* An uncalibrated antenna delay is a few cm on every range. Left out, it
     * reads as an odometer scale error of ~0.3 % as the geometry turns
     * under the car (FW-15). */
    c->r_bias0 = 0.10f;
    c->r_gyro_rest = 0.005f;
    c->nlos_db = 6.0f;
    c->nlos_scale = 4.0f;
    c->gate = 9.0f;
    c->tag_z = 0.0f;
    c->init_travel_m = 3.0f;
    c->init_rms_max = 0.25f;
    c->init_inlier_min = 0.6f;
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

float tt_nav_sideslip(float rear_axle_x, float corner_k, float v, float rate)
{
    float b = 0.0f, va = fabsf(v);
    if (va < 0.3f) return 0.0f;           /* the curvature is noise down here */
    if (rear_axle_x < 0.0f) {
        float k = rate / v;
        if (k > 2.0f) k = 2.0f;
        if (k < -2.0f) k = -2.0f;
        b = atanf(-rear_axle_x * k);
    }
    if (corner_k > 0.0f) b -= v * rate / (9.80665f * corner_k);
    return b;
}

static void predict(TtNav *n, float dt, float gyro_z, float accel_x)
{
    float *s = n->s, F[N * N], FP[N * N];
    const TtNavCfg *g = &n->cfg;
    float v = s[TT_NAV_V];
    float course = s[TT_NAV_PSI] + tt_nav_sideslip(g->rear_axle_x, g->corner_k, v, gyro_z - s[TT_NAV_BG]);
    float c = cosf(course), sn = sinf(course);
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
    /* b_r: a constant (the antenna delay drifts with temperature, slowly);
     * k_f too (the ride height moves with the load, not within a run) */
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
    H[TT_NAV_BR] = 1.0f;
    return rho + s[TT_NAV_BR];
}

/* ---- initialisation: heading search over the recorded track --------------- */

/* Fit the start position for heading th; returns the robust cost (every
 * capped outlier counts cap^2), and the ranges the fit explains: how many
 * lie under the cap and their rms. */
static float fit_start(const TtNav *n, float th, float *x0, float *y0,
                       int *n_in, float *rms_in)
{
    float c = cosf(th), sn = sinf(th), cost = 0.0f, sq_in = 0.0f;
    int it, i, k_in = 0;
    for (it = 0; it < 10; it++) {
        /* Plain least squares first (from a far guess every residual is
         * large), then cap outliers once the fit is close. */
        float cap = it < 5 ? 1e6f : 0.5f;
        float a11 = 0.0f, a12 = 0.0f, a22 = 0.0f, b1 = 0.0f, b2 = 0.0f, det, d1, d2;
        cost = 0.0f; sq_in = 0.0f; k_in = 0;
        for (i = 0; i < n->rec_n; i++) {
            const float *t = n->rec_t[i], *a = n->rec_a[i];
            float wx = *x0 + c * t[0] - sn * t[1] - a[0];
            float wy = *y0 + sn * t[0] + c * t[1] - a[1];
            float dz = n->cfg.tag_z - a[2];
            float rho = sqrtf(wx * wx + wy * wy + dz * dz), e, jx, jy;
            if (rho < 1e-3f) rho = 1e-3f;
            e = rho - n->rec_r[i];
            if (fabsf(e) > cap) { cost += cap * cap; continue; }   /* outlier: capped */
            cost += e * e; sq_in += e * e; k_in++;
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
    if (n_in) *n_in = k_in;
    if (rms_in) *rms_in = k_in > 0 ? sqrtf(sq_in / (float)k_in) : 1e30f;
    return cost;
}

/* The heading search over the recorded track: the transform (th, x0, y0)
 * from the local frame to the site frame that best explains the ranges.
 * Returns 0 while the ranges are not explained yet. */
static int search(TtNav *n, float *th_out, float *x_out, float *y_out)
{
    float best = 1e30f, bth = 0.0f, bx = 0.0f, by = 0.0f, cx = 0.0f, cy = 0.0f, rms = 1e30f;
    int i, k, n_in = 0;
    if (n->rec_n < 12) return 0;
    for (i = 0; i < n->rec_n; i++) { cx += n->rec_a[i][0]; cy += n->rec_a[i][1]; }
    cx /= (float)n->rec_n; cy /= (float)n->rec_n;
    for (k = 0; k < 72; k++) {
        float th = -PI_F + (float)k * (2.0f * PI_F / 72.0f), x0 = cx, y0 = cy, r1;
        int k1;
        float cst = fit_start(n, th, &x0, &y0, &k1, &r1);
        if (cst < best) { best = cst; bth = th; bx = x0; by = y0; n_in = k1; rms = r1; }
    }
    /* refine the heading around the best coarse cell */
    {
        float lo = bth - 2.0f * PI_F / 72.0f, hi = bth + 2.0f * PI_F / 72.0f;
        for (k = 0; k < 20; k++) {
            float m1 = lo + (hi - lo) * 0.382f, m2 = lo + (hi - lo) * 0.618f;
            float x1 = bx, y1 = by, x2 = bx, y2 = by, r1, r2;
            int k1, k2;
            float c1 = fit_start(n, m1, &x1, &y1, &k1, &r1), c2 = fit_start(n, m2, &x2, &y2, &k2, &r2);
            if (c1 < c2) { hi = m2; if (c1 < best) { best = c1; bth = m1; bx = x1; by = y1; n_in = k1; rms = r1; } }
            else         { lo = m1; if (c2 < best) { best = c2; bth = m2; bx = x2; by = y2; n_in = k2; rms = r2; } }
        }
    }
    /* Explained when the ranges the fit keeps agree with it and are most of
     * what was recorded. Judged on those, not on the capped cost: a site
     * where a third of the ranges are multipath (every one long by 0.5 m or
     * more) can never bring the whole set's rms under the bar, however right
     * the heading is, and the filter would then run on no scale at all
     * (FW-20: leg A 110-160 mm off, on the raw radius). */
    n->init_rms = rms;
    n->init_inlier = (float)n_in / (float)n->rec_n;
    if (n_in < 12 || n->init_inlier < n->cfg.init_inlier_min || rms > n->cfg.init_rms_max)
        return 0;                           /* not explained yet: keep driving */
    *th_out = bth;
    *x_out = bx;
    *y_out = by;
    return 1;
}

static int try_init(TtNav *n, float v_odo, float gyro_bias)
{
    float bth, bx, by;
    if (!search(n, &bth, &bx, &by)) return 0;
    {
        float c = cosf(bth), sn = sinf(bth), *s = n->s, *P = n->P;
        s[TT_NAV_X] = bx + c * n->lx - sn * n->ly;
        s[TT_NAV_Y] = by + sn * n->lx + c * n->ly;
        s[TT_NAV_PSI] = tt_nav_wrap(bth + n->lpsi);
        s[TT_NAV_V] = v_odo;
        s[TT_NAV_BG] = gyro_bias;
        s[TT_NAV_KW] = 1.0f;
        s[TT_NAV_BR] = 0.0f;
        s[TT_NAV_KF] = 1.0f;
        memset(P, 0, sizeof(n->P));
        P[TT_NAV_X * N + TT_NAV_X] = 0.05f * 0.05f;
        P[TT_NAV_Y * N + TT_NAV_Y] = 0.05f * 0.05f;
        P[TT_NAV_PSI * N + TT_NAV_PSI] = 0.03f * 0.03f;
        P[TT_NAV_V * N + TT_NAV_V] = 0.1f * 0.1f;
        P[TT_NAV_BG * N + TT_NAV_BG] = 0.005f * 0.005f;
        P[TT_NAV_KW * N + TT_NAV_KW] = 0.03f * 0.03f;
        P[TT_NAV_BR * N + TT_NAV_BR] = n->cfg.r_bias0 * n->cfg.r_bias0;
        P[TT_NAV_KF * N + TT_NAV_KF] = n->cfg.kf_sigma0 * n->cfg.kf_sigma0;
    }
    n->stage = TT_NAV_RUN;
    return 1;
}

/* How much faster the odometry axle moves than the origin in a turn: both
 * turn about a point on the rear axle's line (see tt_nav.h). */
static float odo_axle_gain(const TtNavCfg *g, float v, float rate)
{
    float k, d, l;
    if (!(g->rear_axle_x < 0.0f) || fabsf(v) < 0.3f) return 1.0f;
    k = rate / v;
    if (k > 2.0f) k = 2.0f;
    if (k < -2.0f) k = -2.0f;
    d = g->odo_axle_x - g->rear_axle_x;
    l = -g->rear_axle_x;
    return sqrtf(1.0f + (d * d - l * l) * k * k);
}

/* The dead-reckoned local track (start = origin, heading 0) and the ranges
 * along it, for the heading search. */
static void record(TtNav *n, float dt, float rate, float v_odo,
                   const TtNavRange *ranges, int n_ranges)
{
    const TtNavCfg *g = &n->cfg;
    int i;
    n->lpsi = tt_nav_wrap(n->lpsi + rate * dt);
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
}

void tt_nav_seed(TtNav *n, float x, float y, float psi, float sig_pos, float sig_psi)
{
    TtNavCfg c = n->cfg;
    float *s, *P;
    tt_nav_reset(n, &c);
    s = n->s;
    P = n->P;
    s[TT_NAV_X] = x;
    s[TT_NAV_Y] = y;
    s[TT_NAV_PSI] = tt_nav_wrap(psi);
    s[TT_NAV_V] = 0.0f;
    s[TT_NAV_BG] = 0.0f;
    s[TT_NAV_KW] = 1.0f;
    s[TT_NAV_BR] = 0.0f;
    s[TT_NAV_KF] = 1.0f;
    P[TT_NAV_X * N + TT_NAV_X] = sig_pos * sig_pos;
    P[TT_NAV_Y * N + TT_NAV_Y] = sig_pos * sig_pos;
    P[TT_NAV_PSI * N + TT_NAV_PSI] = sig_psi * sig_psi;
    P[TT_NAV_V * N + TT_NAV_V] = 0.1f * 0.1f;
    P[TT_NAV_BG * N + TT_NAV_BG] = 0.005f * 0.005f;
    P[TT_NAV_KW * N + TT_NAV_KW] = 0.03f * 0.03f;
    P[TT_NAV_BR * N + TT_NAV_BR] = c.r_bias0 * c.r_bias0;
    P[TT_NAV_KF * N + TT_NAV_KF] = c.kf_sigma0 * c.kf_sigma0;
    n->stage = TT_NAV_RUN;
    n->check = TT_NAV_CHECK_WAIT;
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
        record(n, dt, gyro_z - n->s[TT_NAV_BG], v_odo, ranges, n_ranges);
        if (n->travel >= g->init_travel_m) try_init(n, v_odo, n->s[TT_NAV_BG]);
        return;
    }

    /* A seeded start: the same search, once, as a check on the seed. */
    if (n->check == TT_NAV_CHECK_WAIT) {
        float th, x0, y0;
        record(n, dt, gyro_z - n->s[TT_NAV_BG], v_odo, ranges, n_ranges);
        if (n->travel >= g->init_travel_m && search(n, &th, &x0, &y0)) {
            float c = cosf(th), sn = sinf(th);
            float px = x0 + c * n->lx - sn * n->ly, py = y0 + sn * n->lx + c * n->ly;
            float dx = px - n->s[TT_NAV_X], dy = py - n->s[TT_NAV_Y];
            n->check_dpsi = tt_nav_wrap(th + n->lpsi - n->s[TT_NAV_PSI]);
            n->check_dpos = sqrtf(dx * dx + dy * dy);
            n->check = (fabsf(n->check_dpsi) > TT_NAV_CHECK_PSI_RAD ||
                        n->check_dpos > TT_NAV_CHECK_POS_M) ? TT_NAV_CHECK_BAD : TT_NAV_CHECK_OK;
        } else if (n->travel >= 3.0f * g->init_travel_m) {
            /* the ranges never explained a track: nothing to check against */
            n->check = TT_NAV_CHECK_NONE;
        }
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
            float ax = odo_axle_gain(g, n->s[TT_NAV_V], gyro_z - n->s[TT_NAV_BG]);
            H[TT_NAV_V] = ax / kw;
            H[TT_NAV_KW] = -ax * n->s[TT_NAV_V] / (kw * kw);
            update(n, v_odo - ax * n->s[TT_NAV_V] / kw, H, g->r_speed * g->r_speed, 0.0f);
        }
        if (flow_ok) {
            /* z = v k_f: the sensor's own scale, so a wrong height cannot
             * read as a wrong wheel radius (FW-14). */
            float kf = n->s[TT_NAV_KF];
            for (j = 0; j < N; j++) H[j] = 0.0f;
            H[TT_NAV_V] = kf;
            H[TT_NAV_KF] = n->s[TT_NAV_V];
            if (update(n, v_flow - kf * n->s[TT_NAV_V], H, g->r_flow * g->r_flow, g->gate)) n->n_flow++;
        }
        for (i = 0; i < n_ranges; i++) {
            float rho = range_h(n, ranges[i].anchor, H);
            float sig = g->r_range * (ranges[i].nlos_db > g->nlos_db ? g->nlos_scale : 1.0f);
            if (update(n, ranges[i].range_m - rho, H, sig * sig, g->gate)) n->n_used++;
        }
    }
}
