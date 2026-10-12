/*
 * tt_soc.c — pack state of charge. See tt_soc.h.
 */
#include "tt_soc.h"

#include <math.h>
#include <string.h>

/* The twin's curve: (soc, volts per cell), piecewise linear. */
static const float SOC_K[5] = { 0.00f, 0.05f, 0.20f, 0.90f, 1.00f };
static const float OCV_K[5] = { 3.00f, 3.40f, 3.65f, 4.05f, 4.20f };

void tt_soc_default_cfg(TtSocCfg *c)
{
    c->capacity_ah = 5.2f;
    c->cells = 2u;
    c->r_int_ohm = 0.020f;
    c->i_rest_a = 1.5f;
    c->di_rest_a = 0.2f;
    c->t_rest_s = 2.0f;
    c->tau_s = 30.0f;
}

void tt_soc_init(TtSoc *s, const TtSocCfg *c)
{
    memset(s, 0, sizeof *s);
    s->cfg = *c;
}

float tt_soc_cell_ocv(float soc)
{
    int k;
    if (soc <= 0.0f) return OCV_K[0];
    if (soc >= 1.0f) return OCV_K[4];
    for (k = 1; k < 4 && soc > SOC_K[k]; k++) {}
    return OCV_K[k - 1] + (OCV_K[k] - OCV_K[k - 1]) * (soc - SOC_K[k - 1]) / (SOC_K[k] - SOC_K[k - 1]);
}

float tt_soc_from_cell_ocv(float v)
{
    int k;
    if (v <= OCV_K[0]) return 0.0f;
    if (v >= OCV_K[4]) return 1.0f;
    for (k = 1; k < 4 && v > OCV_K[k]; k++) {}
    return SOC_K[k - 1] + (SOC_K[k] - SOC_K[k - 1]) * (v - OCV_K[k - 1]) / (OCV_K[k] - OCV_K[k - 1]);
}

float tt_soc_update(TtSoc *s, const TtBatt *b)
{
    const TtSocCfg *c = &s->cfg;
    float dt, cells = (float)(c->cells ? c->cells : 1u);
    if (!b->st.valid || !(b->v > 0.0f) || b->i != b->i) return s->soc;
    if (s->started && b->st.seq == s->seq) return s->soc;

    s->soc_ocv = tt_soc_from_cell_ocv((b->v + b->i * c->r_int_ohm) / cells);
    if (!s->started) {
        /* Power-up: the pack has been sitting; its voltage is the best start. */
        s->started = 1u;
        s->soc = s->soc_ocv;
        s->seq = b->st.seq;
        s->t_last = b->st.t_us;
        s->i_ref = b->i;
        return s->soc;
    }
    dt = (float)(uint32_t)(b->st.t_us - s->t_last) * 1e-6f;
    if (dt > 1.0f) dt = 1.0f;               /* a gap: do not count a guess */
    s->seq = b->st.seq;
    s->t_last = b->st.t_us;

    s->soc -= b->i * dt / (3600.0f * c->capacity_ah);

    /* Rest: small and steady for t_rest_s. */
    if (fabsf(b->i) < c->i_rest_a && fabsf(b->i - s->i_ref) < c->di_rest_a) {
        s->rest_s += dt;
    } else {
        s->rest_s = 0.0f;
        s->i_ref = b->i;
    }
    s->resting = (uint8_t)(s->rest_s >= c->t_rest_s);
    if (s->resting && c->tau_s > 0.0f)
        s->soc += (s->soc_ocv - s->soc) * (dt / (c->tau_s + dt));

    if (s->soc < 0.0f) s->soc = 0.0f;
    if (s->soc > 1.0f) s->soc = 1.0f;
    return s->soc;
}

float tt_soc_remaining_ah(const TtSoc *s, float soc_floor)
{
    float left = s->soc - soc_floor;
    return left > 0.0f ? left * s->cfg.capacity_ah : 0.0f;
}
