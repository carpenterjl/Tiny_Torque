/*
 * test_nav.c — the navigation EKF (tt_nav) against a synthetic drive of the
 * Opus mission's shape: rest, accelerate, 14.5 m straight, 45 deg left on a
 * 5 m radius, 7.5 m straight, stop. Sensors carry the errors the sim gives
 * them: a biased noisy gyro, a 2 % odometer scale error, and 40 Hz UWB
 * ranges round-robin over four corner anchors with an antenna bias, 5 cm
 * noise, NLOS tails and outliers. Registered with CTest as `tt_nav_unit`.
 *
 * Run three ways: the filter finding its own pose (the heading search after
 * 3 m), seeded with the true start (a route's surveyed start, NAV-04: the
 * search then only checks it), and seeded 30 deg off (the check must say so).
 */
#include "tt_nav.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;
#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); g_fail++; } \
                               else printf("ok   %s\n", name); } while (0)

static unsigned long long g_rng = 88172645463325252ull;
static double urand(void)
{
    g_rng ^= g_rng << 13; g_rng ^= g_rng >> 7; g_rng ^= g_rng << 17;
    return (double)(g_rng >> 11) * (1.0 / 9007199254740992.0);
}
static double gauss(void)
{
    double u1 = 1.0 - urand(), u2 = urand();
    return sqrt(-2.0 * log(u1)) * cos(6.283185307179586 * u2);
}

typedef struct {
    double t_init, travel_init, rmse, emax, hd;
    int    n;
    TtNav  nav;
} Run;

/* seed: 0 = find the pose; 1 = seeded at the truth; 2 = seeded dpsi off */
static Run drive(int seed, double dpsi)
{
    const double dt = 0.01;
    const double anchors[4][3] = { {0, 0, 1.52}, {37, 0, 1.52}, {37, 18, 1.52}, {0, 18, 1.52} };
    const double bias_g = 0.01, odo_scale = 1.02, uwb_bias = 0.03, tag_z = 0.19;
    const double lever_x = -0.06;
    TtNavCfg cfg;
    Run out;
    TtNav *nav_ = &out.nav;
#define nav (*nav_)
    double x = 5.0, y = 4.0, psi = 0.3, v = 0.0, t = 0.0, s_path = 0.0;
    double next_uwb = 0.0, se = 0.0, spsi = 0.0, emax = 0.0, t_init = -1.0, travel_init = 0.0;
    int slot = 0, n = 0, phase = 0;

    g_rng = 88172645463325252ull;
    memset(&out, 0, sizeof(out));
    tt_nav_default_cfg(&cfg);
    cfg.lever[0] = (float)lever_x;
    cfg.tag_z = (float)tag_z;
    tt_nav_reset(&nav, &cfg);
    if (seed) tt_nav_seed(&nav, (float)x, (float)y, (float)(psi + (seed == 2 ? dpsi : 0.0)), 0.10f, 0.05f);

    while (t < 20.0) {
        double a = 0.0, w = 0.0;
        TtNavRange r[4];
        int nr = 0;
        /* the manoeuvre */
        if (t < 1.0) a = 0.0;
        else if (phase == 0) { a = v < 4.5 ? 3.0 : 0.0; if (s_path >= 14.5) phase = 1; }
        if (phase == 1) { w = v / 5.0; if (s_path >= 14.5 + 5.0 * 0.785398) phase = 2; }
        if (phase == 2 && s_path >= 14.5 + 5.0 * 0.785398 + 7.5) phase = 3;
        if (phase == 3) a = v > 0.0 ? -5.0 : 0.0;
        v += a * dt; if (v < 0.0) v = 0.0;
        psi += w * dt;
        x += v * cos(psi) * dt;
        y += v * sin(psi) * dt;
        s_path += v * dt;
        t += dt;

        /* 40 Hz UWB, one anchor per slot */
        while (next_uwb <= t) {
            const double *an = anchors[slot % 4];
            double tx = x + cos(psi) * lever_x, ty = y + sin(psi) * lever_x;
            double d = sqrt((tx - an[0]) * (tx - an[0]) + (ty - an[1]) * (ty - an[1]) +
                            (tag_z - an[2]) * (tag_z - an[2]));
            double rr = d + uwb_bias + 0.05 * gauss(), db = 2.0 + gauss();
            if (urand() < 0.03) { rr += -0.3 * log(1.0 - urand()); db = 9.0 + 3.0 * gauss(); }
            if (urand() < 0.01) rr += 0.5 + 2.5 * urand();
            if (urand() > 0.02 && nr < 4) {
                r[nr].anchor[0] = (float)an[0]; r[nr].anchor[1] = (float)an[1]; r[nr].anchor[2] = (float)an[2];
                r[nr].range_m = (float)rr; r[nr].nlos_db = (float)db; nr++;
            }
            slot++;
            next_uwb += 0.025;
        }

        tt_nav_step(&nav, (float)dt,
                    (float)(w + bias_g + 0.003 * gauss()), (float)(a + 0.05 * gauss()),
                    (float)(v / odo_scale + (v > 0.0 ? 0.02 * gauss() : 0.0)),
                    0.0f, 0, r, nr);

        if (nav.stage == TT_NAV_RUN) {
            double ex = nav.s[TT_NAV_X] - x, ey = nav.s[TT_NAV_Y] - y;
            double e = sqrt(ex * ex + ey * ey);
            double ep = tt_nav_wrap((float)(nav.s[TT_NAV_PSI] - psi));
            if (t_init < 0.0) { t_init = t; travel_init = s_path; }
            if (t > t_init + 1.0) {             /* after a second to settle */
                se += e * e; spsi += ep * ep; n++;
                if (e > emax) emax = e;
            }
        }
    }

#undef nav
    out.t_init = t_init;
    out.travel_init = travel_init;
    out.n = n;
    out.rmse = n ? sqrt(se / n) : 1e9;
    out.emax = emax;
    out.hd = n ? sqrt(spsi / n) * 57.29578 : 1e9;
    (void)odo_scale;
    return out;
}

int main(void)
{
    const double bias_g = 0.01, odo_scale = 1.02;
    Run r = drive(0, 0.0);
    printf("init at t = %.2f s after %.2f m (fit rms %.3f m); %u ranges used, %u gated\n",
           r.t_init, r.travel_init, r.nav.init_rms, r.nav.n_used, r.nav.n_gated);
    CHECK("initialises from the UWB ranges within 5 m of travel", r.t_init > 0.0 && r.travel_init < 5.0);
    if (r.n > 0) {
        printf("position RMSE %.3f m (max %.3f), heading RMSE %.2f deg, k_w %.4f, b_g %.4f rad/s\n",
               r.rmse, r.emax, r.hd, r.nav.s[TT_NAV_KW], r.nav.s[TT_NAV_BG]);
        CHECK("position RMSE below 0.10 m", r.rmse < 0.10);
        CHECK("position error never above 0.30 m", r.emax < 0.30);
        CHECK("heading RMSE below 2 deg", r.hd < 2.0);
        CHECK("odometer scale estimated within 1 %", fabs(r.nav.s[TT_NAV_KW] - odo_scale) < 0.01);
        CHECK("gyro bias estimated within 0.003 rad/s", fabs(r.nav.s[TT_NAV_BG] - bias_g) < 0.003);
        CHECK("NLOS/outlier ranges are gated out", r.nav.n_gated > 0);
    }

    r = drive(1, 0.0);
    printf("seeded at the truth: RMSE %.3f m (max %.3f), heading %.2f deg; check %d (dpsi %.2f deg, dpos %.3f m)\n",
           r.rmse, r.emax, r.hd, r.nav.check, r.nav.check_dpsi * 57.29578, r.nav.check_dpos);
    CHECK("seeded: runs from the first tick", r.t_init > 0.0 && r.t_init < 0.02);
    CHECK("seeded: position RMSE below 0.10 m, never above 0.30", r.rmse < 0.10 && r.emax < 0.30);
    CHECK("seeded: heading RMSE below 2 deg", r.hd < 2.0);
    CHECK("seeded: the heading search agrees with the seed", r.nav.check == TT_NAV_CHECK_OK);

    r = drive(2, 0.5236);
    printf("seeded 30 deg off: check %d (dpsi %.2f deg, dpos %.3f m)\n",
           r.nav.check, r.nav.check_dpsi * 57.29578, r.nav.check_dpos);
    CHECK("seeded 30 deg off: the heading search says the seed was wrong", r.nav.check == TT_NAV_CHECK_BAD);

    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
