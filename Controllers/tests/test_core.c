/*
 * test_core.c — unit checks for the portable core's shared pieces:
 * the parameter store (FW-07), the torque -> Iq helper, the log schema
 * (FW-09), plus the torque allocator (FW-06). Registered with CTest as
 * `tt_core_unit`.
 */
#include "tt_types.h"
#include "tt_params.h"
#include "tt_pstore.h"
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

    /* ---- the parameter image (FW-07, tt_pstore.h) ----------------------- */
    {
        static uint8_t a[TT_PSTORE_SIZE], b[TT_PSTORE_SIZE];
        TtParams r;
        uint32_t seq = 0;
        size_t n = tt_pstore_write(a, sizeof(a), p, 7u);
        CHECK("image: written at its size", n == TT_PSTORE_SIZE);
        CHECK("image: a small buffer is refused", tt_pstore_write(b, 10, p, 1u) == 0);
        memset(&r, 0, sizeof(r));
        CHECK("image: reads back", tt_pstore_read(a, n, &r, &seq) == TT_PSTORE_OK);
        CHECK("image: the set and its hash come back", seq == 7u && tt_params_hash(&r) == tt_params_hash(p));
        CHECK("image: a short read is refused", tt_pstore_read(a, n - 1u, &r, 0) == TT_PSTORE_E_SHORT);
        memcpy(b, a, n);
        b[0] ^= 0xFFu;
        CHECK("image: blank or foreign bytes are not an image", tt_pstore_read(b, n, &r, 0) == TT_PSTORE_E_MAGIC);
        memcpy(b, a, n);
        b[TT_PSTORE_HDR + 20u] ^= 0x01u;
        CHECK("image: one flipped bit is damage", tt_pstore_read(b, n, &r, 0) == TT_PSTORE_E_CRC);
        memcpy(b, a, n);
        b[4] = (uint8_t)(TT_PARAMS_VERSION + 1u);
        b[TT_PSTORE_HDR + sizeof(TtParams)] = 0u;   /* its CRC no longer holds either */
        CHECK("image: a damaged version word reads as damage", tt_pstore_read(b, n, &r, 0) == TT_PSTORE_E_CRC);
        /* An older/newer layout: written whole with its own version. */
        memcpy(b, a, n);
        b[4] = (uint8_t)(TT_PARAMS_VERSION + 1u);
        {
            uint32_t c = tt_crc32(b, TT_PSTORE_HDR + sizeof(TtParams));
            uint8_t *t = b + TT_PSTORE_HDR + sizeof(TtParams);
            t[0] = (uint8_t)c; t[1] = (uint8_t)(c >> 8); t[2] = (uint8_t)(c >> 16); t[3] = (uint8_t)(c >> 24);
        }
        CHECK("image: another layout's version is refused", tt_pstore_read(b, n, &r, 0) == TT_PSTORE_E_VERSION);
        q = *p;
        q.wheel_radius_m = 0.0f;
        CHECK("image: a set that does not validate is not written", tt_pstore_write(b, sizeof(b), &q, 1u) == 0);
        /* Two slots: the newer valid one wins, a damaged newer one loses. */
        tt_pstore_write(b, sizeof(b), p, 9u);
        CHECK("slots: the higher write number is taken", tt_pstore_pick(a, n, b, n, &r, &seq) == 2 && seq == 9u);
        CHECK("slots: ...whichever side it is on", tt_pstore_pick(b, n, a, n, &r, &seq) == 1 && seq == 9u);
        b[TT_PSTORE_HDR + 8u] ^= 0x10u;
        CHECK("slots: a damaged newer slot falls back to the older",
              tt_pstore_pick(a, n, b, n, &r, &seq) == 1 && seq == 7u);
        a[3] ^= 0x01u;
        CHECK("slots: neither usable = none", tt_pstore_pick(a, n, b, n, &r, &seq) == 0);
    }
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

        /* Per-wheel limit (grip budget off: the motor's limit is what binds). */
        {
            TtParams q = *fp;
            q.tyre_mu = 0.0f;
            req.fx_n = 200.0f;
            tt_alloc_step(&a, &q, &req, &in, 0.01f, &c);
            CHECK("per-wheel drive limit holds",
                  near(c.wheel_torque_nm[TT_RL], fp->wheel_drive_max_nm, 1e-6));
        }

        /* Grip budget (FW-13). Braking hard before the load has moved (the
         * accelerometer still reads ~0): the fronts' load-transfer share is
         * more than their static load can carry, so the excess goes rear. */
        {
            const float g = 9.80665f;
            float fz_f = 0.5f * fp->mass_kg * g * fp->front_weight_frac;
            float fz_r = 0.5f * fp->mass_kg * g * (1.0f - fp->front_weight_frac);
            float cap_f = TT_ALLOC_GRIP_USE * fp->tyre_mu * fz_f;
            float cap_r = TT_ALLOC_GRIP_USE * fp->tyre_mu * fz_r;
            float sum;
            CHECK("grip budget on for the FOC twin", fp->tyre_mu > 0.0f);
            tt_alloc_reset(&a);
            in.ax_mps2 = -6.0f;
            in.ax_meas_mps2 = 0.0f;
            req.fx_n = -15.0f;
            for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
            sum = 0.0f;
            for (w = 0; w < TT_MAX_WHEELS; w++) sum += c.wheel_torque_nm[w] / r;
            CHECK("grip budget: a front wheel stays inside its cap",
                  fabsf(c.wheel_torque_nm[TT_FL]) / r <= cap_f + 1e-4f &&
                  near(fabsf(c.wheel_torque_nm[TT_FL]) / r, cap_f, 1e-3));
            CHECK("grip budget: what the fronts cannot carry goes rear",
                  near(sum, -15.0, 1e-3) && fabsf(c.wheel_torque_nm[TT_RL]) / r <= cap_r + 1e-4f);
            /* Once the load has moved, the fronts carry more. */
            in.ax_meas_mps2 = -6.0f;
            for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
            CHECK("grip budget: transferred load raises the front cap",
                  fabsf(c.wheel_torque_nm[TT_FL]) / r > cap_f + 0.1f);
            /* Asking for the impossible gets every wheel at its cap, no more. */
            in.ax_meas_mps2 = 0.0f;
            req.fx_n = -40.0f;
            for (k = 0; k < 20; k++) tt_alloc_step(&a, fp, &req, &in, 0.01f, &c);
            CHECK("grip budget: never past any wheel's cap",
                  fabsf(c.wheel_torque_nm[TT_FL]) / r <= cap_f + 1e-4f &&
                  fabsf(c.wheel_torque_nm[TT_RR]) / r <= cap_r + 1e-4f &&
                  near(fabsf(c.wheel_torque_nm[TT_RR]) / r, cap_r, 1e-3));
            in.ax_mps2 = 0.0f;
        }

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
