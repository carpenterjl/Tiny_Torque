/*
 * test_nav.c — the navigation EKF (tt_nav) against a synthetic drive of the
 * Opus mission's shape: rest, accelerate, 14.5 m straight, 45 deg left on a
 * 5 m radius, 7.5 m straight, stop. Sensors carry the errors the sim gives
 * them: a biased noisy gyro, a 2 % odometer scale error, and 40 Hz UWB
 * ranges round-robin over four corner anchors with an antenna bias, 5 cm
 * noise, NLOS tails and outliers. Registered with CTest as `tt_nav_unit`.
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

int main(void)
{
    const double dt = 0.01;
    const double anchors[4][3] = { {0, 0, 1.52}, {37, 0, 1.52}, {37, 18, 1.52}, {0, 18, 1.52} };
    const double bias_g = 0.01, odo_scale = 1.02, uwb_bias = 0.03, tag_z = 0.19;
    const double lever_x = -0.06;
    TtNavCfg cfg;
    TtNav nav;
    double x = 5.0, y = 4.0, psi = 0.3, v = 0.0, t = 0.0, s_path = 0.0;
    double next_uwb = 0.0, se = 0.0, spsi = 0.0, emax = 0.0, t_init = -1.0, travel_init = 0.0;
    int slot = 0, n = 0, phase = 0;

    tt_nav_default_cfg(&cfg);
    cfg.lever[0] = (float)lever_x;
    cfg.tag_z = (float)tag_z;
    tt_nav_reset(&nav, &cfg);

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

    printf("init at t = %.2f s after %.2f m (fit rms %.3f m); %u ranges used, %u gated\n",
           t_init, travel_init, nav.init_rms, nav.n_used, nav.n_gated);
    CHECK("initialises from the UWB ranges within 5 m of travel", t_init > 0.0 && travel_init < 5.0);
    if (n > 0) {
        double rmse = sqrt(se / n), hd = sqrt(spsi / n) * 57.29578;
        printf("position RMSE %.3f m (max %.3f), heading RMSE %.2f deg, k_w %.4f, b_g %.4f rad/s\n",
               rmse, emax, hd, nav.s[TT_NAV_KW], nav.s[TT_NAV_BG]);
        CHECK("position RMSE below 0.10 m", rmse < 0.10);
        CHECK("position error never above 0.30 m", emax < 0.30);
        CHECK("heading RMSE below 2 deg", hd < 2.0);
        CHECK("odometer scale estimated within 1 %", fabs(nav.s[TT_NAV_KW] - odo_scale) < 0.01);
        CHECK("gyro bias estimated within 0.003 rad/s", fabs(nav.s[TT_NAV_BG] - bias_g) < 0.003);
        CHECK("NLOS/outlier ranges are gated out", nav.n_gated > 0);
    }
    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
