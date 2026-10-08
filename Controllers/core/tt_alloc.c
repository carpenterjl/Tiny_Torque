/*
 * tt_alloc.c — electronic differential and torque allocation (FW-06).
 * See tt_alloc.h for the pipeline; equations in SIM_TO_REAL_PLAN §6.6.
 */
#include "tt_alloc.h"

#include <math.h>
#include <string.h>

#define G_MPS2 9.80665f

static float clampf(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

void tt_alloc_reset(TtAlloc *a)
{
    int w;
    memset(a, 0, sizeof(*a));
    for (w = 0; w < TT_MAX_WHEELS; w++) a->cut[w] = 1.0f;
}

static int driven(const TtParams *p, int w) { return (p->driven_mask & (1u << w)) != 0u; }

void tt_alloc_step(TtAlloc *a, const TtParams *p, const TtVehReq *req,
                   const TtAllocIn *in, float dt, TtCmd *out)
{
    const float r = p->wheel_radius_m;
    float force[TT_MAX_WHEELS] = { 0.0f, 0.0f, 0.0f, 0.0f };
    int nf = driven(p, TT_FL) + driven(p, TT_FR);
    int nr = driven(p, TT_RL) + driven(p, TT_RR);
    float share_f, fx_f, fx_r, mz_f, mz_r;
    int w;

    /* 1. Front/rear: by axle load when both are driven. Under acceleration
     * the rear gains h/L of m*a; braking moves it forward. */
    if (nf > 0 && nr > 0) {
        float transfer = p->wheelbase_m > 0.0f ? p->cg_height_m / p->wheelbase_m : 0.0f;
        share_f = clampf(p->front_weight_frac - transfer * in->ax_mps2 / G_MPS2, 0.1f, 0.9f);
    } else {
        share_f = nf > 0 ? 1.0f : 0.0f;
    }
    fx_f = req->fx_n * share_f;
    fx_r = req->fx_n - fx_f;
    /* The yaw moment goes to the driven axles in the same proportion. */
    mz_f = req->mz_nm * share_f;
    mz_r = req->mz_nm - mz_f;

    /* 2. Left/right: half each, plus the couple. In FLU a positive (left)
     * yaw moment pushes the RIGHT wheel forward. A single driven wheel on an
     * axle takes the axle's whole force and no couple. */
    if (nf == 2) {
        float d = p->track_front_m > 0.0f ? mz_f / p->track_front_m : 0.0f;
        force[TT_FL] = 0.5f * fx_f - d;
        force[TT_FR] = 0.5f * fx_f + d;
    } else if (nf == 1) {
        force[driven(p, TT_FL) ? TT_FL : TT_FR] = fx_f;
    }
    if (nr == 2) {
        float d = p->track_rear_m > 0.0f ? mz_r / p->track_rear_m : 0.0f;
        force[TT_RL] = 0.5f * fx_r - d;
        force[TT_RR] = 0.5f * fx_r + d;
    } else if (nr == 1) {
        force[driven(p, TT_RL) ? TT_RL : TT_RR] = fx_r;
    }

    a->limiting = 0;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float t, side, v_ref, slip, lim;
        if (!driven(p, w)) { out->wheel_torque_nm[w] = 0.0f; a->t_prev[w] = 0.0f; continue; }

        t = force[w] * r;

        /* 3. Per-wheel limits: motoring and regen separately. Regen is torque
         * against the direction of travel. */
        {
            int regen = (in->v_mps >= 0.0f) ? (t < 0.0f) : (t > 0.0f);
            lim = regen ? p->wheel_regen_max_nm : p->wheel_drive_max_nm;
            if (lim > 0.0f) t = clampf(t, -lim, lim);
        }

        /* 4. Slip limiter. The wheel's speed reference is the vehicle speed
         * at its corner (left wheels are on the inside of a left turn). */
        side = (w == TT_FL || w == TT_RL) ? 1.0f : -1.0f;
        v_ref = in->v_mps - side * in->yaw_rate *
                0.5f * ((w < 2) ? p->track_front_m : p->track_rear_m);
        slip = 0.0f;
        if (p->slip_max > 0.0f && in->omega_valid[w]) {
            float den = fabsf(v_ref) > p->slip_v_min ? fabsf(v_ref) : p->slip_v_min;
            float excess = 0.0f, target;
            slip = (in->wheel_omega[w] * r - v_ref) / den;
            /* Driving forward spins the wheel faster (+slip); braking locks it
             * (-slip). The excess is in the direction the torque pushes. */
            if (t > 0.0f && slip > p->slip_max)  excess = slip - p->slip_max;
            if (t < 0.0f && slip < -p->slip_max) excess = -slip - p->slip_max;
            target = clampf(1.0f - p->slip_gain * excess, 0.0f, 1.0f);
            if (target < a->cut[w]) {
                a->cut[w] = target;                 /* cut at once */
                a->limiting |= (uint8_t)(1u << w);
            } else {
                a->cut[w] += (target - a->cut[w]) * (1.0f - expf(-dt / p->slip_recover_s));
            }
            t *= a->cut[w];
        }
        a->slip[w] = slip;

        /* 5. Gear-lash crossing: near zero torque, slew rather than step, so
         * the teeth meet gently. Only for a car with per-wheel drives (one
         * with wheel limits stated); a single motor behind a diff has its
         * lash upstream of anything this could shape. */
        if (p->wheel_drive_max_nm > 0.0f && dt > 0.0f) {
            const float band = TT_ALLOC_LASH_BAND_NM;
            float prev = a->t_prev[w];
            if (fabsf(prev) <= band || (prev > 0.0f) != (t > 0.0f)) {
                /* Walk from where we are (or the band edge on our side)
                 * toward where the request meets the band; once there, the
                 * request is free again. */
                float from = clampf(prev, -band, band);
                float to = clampf(t, -band, band);
                float step = TT_ALLOC_LASH_SLEW_NMS * dt;
                float x = from + clampf(to - from, -step, step);
                t = (x == to) ? t : x;
            }
        }

        out->wheel_torque_nm[w] = t;
        a->t_prev[w] = t;
    }
}
