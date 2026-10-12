/*
 * tt_paa5100.c — PAA5100JE motion burst decoding. See tt_paa5100.h.
 */
#include "tt_paa5100.h"

void tt_paa_default_cfg(TtPaaCfg *c)
{
    c->axis[0] = 1;
    c->axis[1] = 2;
    c->squal_min = 25u;
    c->pad_ = 0u;
    c->shutter_max = 0x1F00u;
    c->pad2_ = 0u;
    c->counts_per_rad = 47.7f;
    c->height_m = 0.025f;
}

void tt_paa_burst_decode(const uint8_t b[TT_PAA_BURST], TtPaaBurst *o)
{
    o->motion = b[0];
    o->dx = (int16_t)(uint16_t)(b[2] | (uint16_t)(b[3] << 8));
    o->dy = (int16_t)(uint16_t)(b[4] | (uint16_t)(b[5] << 8));
    o->squal = b[6];
    o->raw_sum = b[7];
    o->raw_max = b[8];
    o->raw_min = b[9];
    o->shutter = (uint16_t)((uint16_t)(b[10] << 8) | b[11]);
    if (!(o->motion & 0x80u)) o->dx = o->dy = 0;       /* nothing moved */
}

static float pick(int8_t a, const float chip[2])
{
    int k = a < 0 ? -a : a;
    float v = (k == 1 || k == 2) ? chip[k - 1] : 0.0f;
    return a < 0 ? -v : v;
}

void tt_paa_to_flow(const TtPaaBurst *b, float dt_s, const TtPaaCfg *c, TtFlow *f)
{
    float chip[2];
    int tracking = b->squal >= c->squal_min && !(b->shutter > c->shutter_max) &&
                   dt_s > 0.0f && c->counts_per_rad > 0.0f;
    chip[0] = (float)b->dx;
    chip[1] = (float)b->dy;
    f->height_m = c->height_m;
    if (!tracking) {
        f->flow_x = f->flow_y = 0.0f;
        f->quality = 0.0f;
        return;
    }
    f->flow_x = pick(c->axis[0], chip) / (c->counts_per_rad * dt_s);
    f->flow_y = pick(c->axis[1], chip) / (c->counts_per_rad * dt_s);
    f->quality = (float)b->squal * (1.0f / 255.0f);
}
