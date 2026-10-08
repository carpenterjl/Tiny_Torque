/*
 * tt_params.c — parameter validation and identity (FW-07).
 */
#include "tt_params.h"

#include <stddef.h>

static int bad_pos(float v)  { return !(v > 0.0f) || !(v < 1e9f); }   /* also catches NaN */
static int bad_frac(float v) { return !(v > 0.0f) || v > 1.0f; }

int tt_params_validate(const TtParams *p)
{
    int bad = 0, w;

    if (p == NULL) return 1;
    if (p->version != TT_PARAMS_VERSION) bad++;

    bad += bad_pos(p->wheel_radius_m) + bad_pos(p->wheelbase_m) + bad_pos(p->track_front_m);
    bad += bad_pos(p->mass_kg);
    if (!(p->mass_eff_kg >= p->mass_kg)) bad++;

    if ((p->odo_wheel_mask & 0xFu) == 0u || (p->odo_wheel_mask & ~0xFu) != 0u) bad++;
    if ((p->driven_mask & 0xFu) == 0u || (p->driven_mask & ~0xFu) != 0u) bad++;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if (p->odo_wheel_mask & (1u << w))
            bad += bad_pos(p->enc_cpr[w]) + bad_pos(p->enc_ratio[w]);
        if (p->driven_mask & (1u << w))
            bad += bad_pos(p->gear[w]) + bad_pos(p->kt[w]);
    }

    bad += bad_frac(p->eta_drive);
    /* eta_back may exceed 1 only to mirror a known sim defect (BUG-05); it
     * must still be positive and not absurd. */
    if (!(p->eta_back > 0.0f && p->eta_back < 2.0f)) bad++;
    if (p->regen_n_per_ms < 0.0f || p->regen_max_n < 0.0f || p->regen_grip_cap_n < 0.0f) bad++;
    bad += bad_frac(p->traction_eff) + bad_pos(p->v_rail_nom);

    bad += bad_pos(p->max_steer_rad) + bad_pos(p->servo_slew_rad_s);
    if (!(p->max_steer_rad < 1.2f)) bad++;
    if (p->brake_max_nm < 0.0f) bad++;
    if (p->drag_c0 < 0.0f || p->drag_c1 < 0.0f || p->drag_c2 < 0.0f) bad++;

    if (!(p->cal_scale > -0.5f && p->cal_scale < 0.5f)) bad++;
    if (!(p->cal_brake >= 0.0f && p->cal_brake < 10.0f)) bad++;
    if (p->odo_lead_comp > 1u) bad++;

    return bad;
}

uint32_t tt_params_hash(const TtParams *p)
{
    /* Bitwise CRC-32/IEEE: tiny, table-free, and the same on every target
     * because the struct is all 32-bit fields with no padding (and every
     * target so far is little-endian). */
    const uint8_t *b = (const uint8_t *)p;
    uint32_t crc = 0xFFFFFFFFu;
    size_t i;
    int k;

    if (p == NULL) return 0u;
    for (i = 0; i < sizeof(*p); i++) {
        crc ^= b[i];
        for (k = 0; k < 8; k++)
            crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
    }
    return ~crc;
}
