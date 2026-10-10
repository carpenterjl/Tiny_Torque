/*
 * test_core.c — unit checks for the portable core's shared pieces:
 * the parameter store (FW-07), the torque -> Iq helper, the log schema
 * (FW-09), plus the torque allocator (FW-06). Registered with CTest as
 * `tt_core_unit`.
 */
#include "tt_types.h"
#include "tt_params.h"
#include "tt_log.h"
#include "tt_alloc.h"
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
        CHECK("the log fits a TtLogFrame (16 in CtrlOutputs.debug, the rest via ctrl_get_debug_ext)",
              n <= TT_LOG_MAX);
        CHECK("row 0 is `state` (MissionHud matches it)", strcmp(names[0], "state") == 0);
        memset(&lg, 0, sizeof(lg));
        lg.state = 7.0f;
        ((float *)&lg)[OPUS_LOG_N - 1] = -1.5f;
        tt_log_pack(&f, &lg, OPUS_LOG_N, 42u, 123456u, tt_params_hash(p));
        CHECK("frame carries magic, seq and count",
              f.magic == TT_LOG_MAGIC && f.seq == 42u && f.n == OPUS_LOG_N);
        CHECK("frame values are in table order",
              f.v[0] == 7.0f && f.v[OPUS_LOG_N - 1] == -1.5f);
    }

    /* ---- torque allocation (FW-06) ------------------------------------- */
    {
        const TtParams *fp = &tt_params_opus_vector_foc;
        const float r = fp->wheel_radius_m;
        TtAlloc a;
        TtAllocIn in;
        TtVehReq req;
        TtCmd c;
        int w, k;

        CHECK("FOC twin parameters validate", tt_params_validate(fp) == 0);

        memset(&in, 0, sizeof(in));
        memset(&req, 0, sizeof(req));
        in.v_mps = 2.0f;
        for (w = 0; w < TT_MAX_WHEELS; w++) { in.wheel_omega[w] = 2.0f / r; in.omega_valid[w] = 1; }

        /* Rear-drive car: equal halves, nothing to the fronts. */
        tt_alloc_reset(&a);
        req.fx_n = 4.0f;
        tt_alloc_step(&a, p, &req, &in, 0.01f, &c);
        CHECK("2WD: equal rear halves (open differential)",
              near(c.wheel_torque_nm[TT_RL], 2.0f * r, 1e-6) && near(c.wheel_torque_nm[TT_RR], 2.0f * r, 1e-6)
              && c.wheel_torque_nm[TT_FL] == 0.0f && c.wheel_torque_nm[TT_FR] == 0.0f);

        /* 4WD at rest load: half front, half rear; accelerating moves load back. */
        tt_alloc_reset(&a);
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("4WD: static split follows front_weight_frac",
              near(c.wheel_torque_nm[TT_FL] + c.wheel_torque_nm[TT_FR], 4.0f * fp->front_weight_frac * r, 1e-5));
        in.ax_mps2 = 5.0f;
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("4WD: accelerating shifts drive to the rear",
              c.wheel_torque_nm[TT_RL] > c.wheel_torque_nm[TT_FL]);
        CHECK("4WD: total force is preserved",
              near((c.wheel_torque_nm[0] + c.wheel_torque_nm[1] + c.wheel_torque_nm[2] + c.wheel_torque_nm[3]) / r, 4.0, 1e-4));
        in.ax_mps2 = 0.0f;

        /* A left yaw moment pushes the right wheels forward. */
        req.mz_nm = 0.2f;
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("yaw moment: right > left, couple = Mz",
              c.wheel_torque_nm[TT_RR] > c.wheel_torque_nm[TT_RL] &&
              near(((c.wheel_torque_nm[TT_FR] - c.wheel_torque_nm[TT_FL]) * fp->track_front_m +
                    (c.wheel_torque_nm[TT_RR] - c.wheel_torque_nm[TT_RL]) * fp->track_rear_m) / (2.0f * r),
                   0.2, 1e-4));
        req.mz_nm = 0.0f;

        /* Per-wheel limit. */
        req.fx_n = 200.0f;
        tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("per-wheel drive limit holds",
              near(c.wheel_torque_nm[TT_RL], fp->wheel_drive_max_nm, 1e-6));

        /* Traction control: a spinning rear-left wheel is cut, the others not. */
        tt_alloc_reset(&a);
        req.fx_n = 8.0f;
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        in.wheel_omega[TT_RL] = 2.6f / r;           /* 30 % slip */
        tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("TC cuts the spinning wheel",
              c.wheel_torque_nm[TT_RL] < 0.5f * c.wheel_torque_nm[TT_RR] && (a.limiting & (1u << TT_RL)));
        in.wheel_omega[TT_RL] = 2.0f / r;
        for (k = 0; k < 50; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("TC recovers once the slip is gone",
              near(c.wheel_torque_nm[TT_RL], c.wheel_torque_nm[TT_RR], 1e-3));

        /* ABS: a locking wheel under regen is released. */
        req.fx_n = -8.0f;
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        in.wheel_omega[TT_FL] = 1.0f / r;           /* -50 % slip */
        tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
        CHECK("ABS releases the locking wheel",
              c.wheel_torque_nm[TT_FL] > 0.5f * c.wheel_torque_nm[TT_FR]);
        in.wheel_omega[TT_FL] = 2.0f / r;

        /* Lash crossing: drive -> regen walks through the band, then jumps. */
        tt_alloc_reset(&a);
        req.fx_n = 8.0f;
        for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.0025f, &c);
        req.fx_n = -8.0f;
        tt_alloc_step(&a, fp, &req, &in, 0.0025f, &c);
        CHECK("lash: first step after a reversal stays inside the band",
              fabsf(c.wheel_torque_nm[TT_RL]) <= TT_ALLOC_LASH_BAND_NM + 1e-6f);
        for (k = 0; k < 10; k++) tt_alloc_step(&a, fp, &req, &in, 0.0025f, &c);
        CHECK("lash: the reversal completes within ~10 ms",
              near(c.wheel_torque_nm[TT_RL], -2.0f * r * 0.5f, 1e-3) ||
              c.wheel_torque_nm[TT_RL] < -TT_ALLOC_LASH_BAND_NM);
    }

    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
