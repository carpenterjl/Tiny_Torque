/*
 * test_core.c — unit checks for the portable core's shared pieces:
 * the parameter store (FW-07), the torque -> Iq helper, and the log schema
 * (FW-09). Registered with CTest as `tt_core_unit`.
 */
#include "tt_types.h"
#include "tt_params.h"
#include "tt_log.h"
#include "opus_mission.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;
#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); g_fail++; } \
                               else printf("ok   %s\n", name); } while (0)

static int near(double a, double b, double tol) { return fabs(a - b) <= tol; }

int main(void)
{
    const TtParams *p = &tt_params_opus_vector;
    TtParams q;

    /* ---- parameters ---------------------------------------------------- */
    CHECK("TtParams has no padding (all 32-bit fields)", sizeof(TtParams) % 4 == 0);
    CHECK("Opus parameters validate", tt_params_validate(p) == 0);
    CHECK("hash is stable", tt_params_hash(p) == tt_params_hash(p));
    q = *p;
    q.kt[TT_RL] *= 1.01f;
    CHECK("hash sees a 1 % kt change", tt_params_hash(&q) != tt_params_hash(p));
    q = *p;
    q.wheel_radius_m = 0.0f;
    CHECK("a zero wheel radius is rejected", tt_params_validate(&q) != 0);
    q = *p;
    q.driven_mask = 0u;
    CHECK("no driven wheel is rejected", tt_params_validate(&q) != 0);
    q = *p;
    q.version = TT_PARAMS_VERSION + 1u;
    CHECK("a different layout version is rejected", tt_params_validate(&q) != 0);

    /* ---- torque -> Iq ---------------------------------------------------- */
    {
        /* The old mission computed per-motor current as
         * f / (kt*gear/r*eta*N); for one wheel's share t = f*r/N that is
         * t / (gear*kt*eta). */
        float t = 0.0165f;
        float iq = tt_torque_to_iq(p, TT_RL, t);
        double expect = t / (11.2 * 0.0025130 * 0.85);
        CHECK("drive: Iq = T / (gear kt eta_drive)", near(iq, expect, 1e-6));
        iq = tt_torque_to_iq(p, TT_RL, -t);
        expect = -t * 1.1764706 / (11.2 * 0.0025130);
        CHECK("brake: Iq = T eta_back / (gear kt)", near(iq, expect, 1e-6));
        CHECK("undriven wheel gives 0 A", tt_torque_to_iq(p, TT_FL, 1.0f) == 0.0f);
    }
    CHECK("metres per count = 2 pi r / cpr",
          near(tt_m_per_count(p, TT_FL), 2.0 * 3.14159265 * 0.033 / 4096.0, 1e-9));

    /* ---- log schema ------------------------------------------------------ */
    {
        static const char *names[] = {
#define TT_LOG(name, unit, desc) #name,
#include "opus_log.def"
#undef TT_LOG
        };
        OpusLog lg;
        TtLogFrame f;
        size_t n = sizeof(names) / sizeof(names[0]);
        CHECK("OpusLog has one float per table row", OPUS_LOG_N == n);
        CHECK("the sim's 16 debug slots hold the whole log", n <= 16);
        CHECK("row 0 is `state` (MissionHud matches it)", strcmp(names[0], "state") == 0);
        memset(&lg, 0, sizeof(lg));
        lg.state = 7.0f;
        lg.stop_err_mm = -1.5f;
        tt_log_pack(&f, &lg, OPUS_LOG_N, 42u, 123456u, tt_params_hash(p));
        CHECK("frame carries magic, seq and count",
              f.magic == TT_LOG_MAGIC && f.seq == 42u && f.n == OPUS_LOG_N);
        CHECK("frame values are in table order",
              f.v[0] == 7.0f && f.v[OPUS_LOG_N - 1] == -1.5f);
    }

    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
