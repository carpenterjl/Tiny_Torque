/*
 * tt_safety.c — the vehicle safety layer (FW-08). See tt_safety.h.
 */
#include "tt_safety.h"

#include <math.h>
#include <string.h>

#define SAFE_PI 3.14159265f

static int finitef_(float x) { return x == x && x - x == 0.0f; }

void tt_safety_default_cfg(TtSafetyCfg *c)
{
    memset(c, 0, sizeof(*c));
    c->tick_max_s = 0.050f;           /* five periods at 100 Hz            */
    c->wd_ticks = 3u;
    c->stale_drive_s = 0.050f;        /* a 1 kHz driver: 50 frames missed  */
    c->stale_enc_s = 0.050f;
    c->stale_imu_s = 0.050f;
    c->stale_batt_s = 0.500f;
    c->stale_rc_s = 0.100f;           /* SBUS/CRSF frames at 50-150 Hz     */
    c->rc_required = 0u;
    c->rc_arm_ch = 4u;                /* AUX1                              */
    c->rc_kill_ch = 5u;               /* AUX2                              */
    c->rc_lost_s = 0.200f;
    c->drv_trip_mask = (uint16_t)(TT_DRV_OVERVOLT | TT_DRV_CMD_TIMEOUT |
                                  TT_DRV_SENSOR | TT_DRV_OVERCURRENT);
    c->drv_temp_max_c = 120.0f;
    c->v_plausible = 15.0f;
    c->enc_glitch_max = 3u;
    c->enc_glitch_win_s = 1.0f;
    c->enc_mismatch_s = 0.2f;
    c->uv_cutoff_v = 0.0f;            /* 0: 3.2 V per cell                 */
    c->uv_arm_v = 0.0f;               /* 0: 3.5 V per cell                 */
    c->uv_debounce_s = 0.5f;
    c->tilt_max_rad = 60.0f * SAFE_PI / 180.0f;
    c->tilt_s = 0.3f;
    c->pos_timeout_s = 1.0f;
    c->stop_decel = 2.5f;
    c->stop_v_taper = 0.3f;
    c->rest_v = 0.03f;
    c->rest_s = 0.3f;
    c->stop_max_s = 4.0f;
}

void tt_safety_reset(TtSafety *s)
{
    TtSafetyCfg c = s->cfg;
    const TtParams *p = s->p;
    memset(s, 0, sizeof(*s));
    s->cfg = c;
    s->p = p;
    s->state = TT_SAFE_DISARMED;
    s->cat = 255u;
}

void tt_safety_init(TtSafety *s, const TtSafetyCfg *c, const TtParams *p)
{
    memset(s, 0, sizeof(*s));
    s->cfg = *c;
    s->p = p;
    tt_safety_reset(s);
}

void tt_safety_fence_from_anchors(TtSafetyCfg *c, const float (*anchor)[3], int n,
                                  float margin_m)
{
    int i;
    float x0, x1, y0, y1;
    if (n < 3) { c->fence[0] = c->fence[1] = c->fence[2] = c->fence[3] = 0.0f; return; }
    x0 = x1 = anchor[0][0];
    y0 = y1 = anchor[0][1];
    for (i = 1; i < n; i++) {
        if (anchor[i][0] < x0) x0 = anchor[i][0];
        if (anchor[i][0] > x1) x1 = anchor[i][0];
        if (anchor[i][1] < y0) y0 = anchor[i][1];
        if (anchor[i][1] > y1) y1 = anchor[i][1];
    }
    c->fence[0] = x0 + margin_m;
    c->fence[1] = x1 - margin_m;
    c->fence[2] = y0 + margin_m;
    c->fence[3] = y1 - margin_m;
}

/* A sample is fresh while its seq keeps changing. Returns 1 when stale. */
static int fresh_step(TtFresh *f, const TtStamp *st, float dt, float limit)
{
    if (!st->valid) return 0;                     /* absence is not staleness */
    if (!f->seen || st->seq != f->seq) {
        f->seen = 1u;
        f->seq = st->seq;
        f->since_s = 0.0f;
        return 0;
    }
    f->since_s += dt;
    return f->since_s > limit;
}

static float cells(const TtParams *p)
{
    float n = p->v_rail_nom > 0.0f ? floorf(p->v_rail_nom / 3.7f + 0.5f) : 0.0f;
    return n < 1.0f ? 1.0f : n;
}

static uint32_t check(TtSafety *s, const TtMeas *m, const TtCmd *core,
                      const TtSafetyPose *pose, float dt, int *glitch_now)
{
    const TtSafetyCfg *c = &s->cfg;
    const TtParams *p = s->p;
    uint32_t f = 0u;
    int w;
    float vsum = 0.0f, v_drv[TT_MAX_WHEELS];
    int vn = 0, drv_ok[TT_MAX_WHEELS] = {0};

    /* ---- timing and the core ------------------------------------------ */
    if (s->t_s > 0.0f && dt > c->tick_max_s) f |= TT_SF_OVERRUN;

    if (s->cmd_seen && core->seq == s->cmd_seq_prev) {
        if (++s->cmd_same >= c->wd_ticks) f |= TT_SF_WATCHDOG;
    } else {
        s->cmd_same = 0u;
    }
    s->cmd_seen = 1u;
    s->cmd_seq_prev = core->seq;

    if (!finitef_(core->steer_rad) || !finitef_(core->brake_01)) f |= TT_SF_NAN;
    for (w = 0; w < TT_MAX_WHEELS; w++)
        if (!finitef_(core->wheel_torque_nm[w])) f |= TT_SF_NAN;

    /* ---- drivers -------------------------------------------------------- */
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        const TtDriveFb *d = &m->drv[w];
        if (!(p->driven_mask & (1u << w)) || !d->st.valid) continue;
        if (fresh_step(&s->f_drv[w], &d->st, dt, c->stale_drive_s)) { f |= TT_SF_DRV_STALE; continue; }
        if (d->fault & c->drv_trip_mask) f |= TT_SF_DRV_TRIP;
        if (finitef_(d->temp_c) && d->temp_c > c->drv_temp_max_c) f |= TT_SF_DRV_HOT;
        if (p->gear[w] > 0.0f && finitef_(d->omega_m)) {
            v_drv[w] = d->omega_m / p->gear[w] * p->wheel_radius_m;
            drv_ok[w] = 1;
            vsum += v_drv[w];
            vn++;
        }
    }

    /* ---- odometry encoders ---------------------------------------------- */
    {
        float evsum = 0.0f;
        int evn = 0;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            const TtEnc *e = &m->enc[w];
            float mpc;
            int32_t d;
            if (!(p->odo_wheel_mask & (1u << w)) || !e->st.valid) continue;
            if (fresh_step(&s->f_enc[w], &e->st, dt, c->stale_enc_s)) f |= TT_SF_ENC;
            mpc = tt_m_per_count(p, w);
            if (!s->enc_has_prev[w]) {
                s->enc_has_prev[w] = 1u;
                s->enc_prev[w] = e->count;
                continue;
            }
            d = (int32_t)((uint32_t)e->count - (uint32_t)s->enc_prev[w]);
            s->enc_prev[w] = e->count;
            if (mpc > 0.0f && dt > 0.0f) {
                float lim = c->v_plausible * dt / mpc + 2.0f;
                float ad = (float)(d < 0 ? -d : d), ve;
                if (ad > lim) { *glitch_now = 1; continue; }
                ve = (float)d * mpc / dt;
                evsum += ve;
                evn++;
                /* A driven odometry wheel has two speed sensors on the same
                 * shaft: the encoder and the driver's own. Wheel slip moves
                 * both, so a lasting disagreement is a sensor fault. */
                if (c->enc_mismatch_s > 0.0f && drv_ok[w]) {
                    float tol = 0.3f + 0.2f * fabsf(v_drv[w]);
                    s->mismatch_t[w] = fabsf(ve - v_drv[w]) > tol ? s->mismatch_t[w] + dt : 0.0f;
                    if (s->mismatch_t[w] >= c->enc_mismatch_s) f |= TT_SF_ENC;
                }
            }
        }
        /* The drives' own speed when they report it; the encoders otherwise. */
        if (vn == 0 && evn > 0) { vsum = evsum; vn = evn; }
    }
    s->v_ok = (uint8_t)(vn > 0);
    s->v = vn > 0 ? vsum / (float)vn : 0.0f;

    if (*glitch_now) {
        s->glitch_t[s->glitch_n % 4u] = s->t_s;
        s->glitch_n++;
        s->glitches++;
    }
    {
        uint32_t k, n = s->glitch_n < 4u ? s->glitch_n : 4u, recent = 0u;
        for (k = 0; k < n; k++)
            if (s->t_s - s->glitch_t[k] <= c->enc_glitch_win_s) recent++;
        if (c->enc_glitch_max > 0u && recent >= c->enc_glitch_max) f |= TT_SF_ENC;
    }

    /* ---- IMU ------------------------------------------------------------- */
    if (m->imu.st.valid) {
        const float *a = m->imu.accel;
        int k, bad = 0;
        for (k = 0; k < 3; k++)
            if (!finitef_(a[k]) || !finitef_(m->imu.gyro[k])) bad = 1;
        if (bad || fresh_step(&s->f_imu, &m->imu.st, dt, c->stale_imu_s)) {
            f |= TT_SF_IMU;
            s->tilt_t = 0.0f;
        } else {
            /* On its side or roof the specific force (~1 g at rest) points
             * away from body z. Only a quasi-static reading counts, so a
             * bump or a hard corner cannot fake it. */
            float mag = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            if (mag > 4.9f && mag < 14.7f && a[2] < cosf(c->tilt_max_rad) * mag) {
                s->tilt_t += dt;
                if (s->tilt_t >= c->tilt_s) f |= TT_SF_TIPOVER;
            } else {
                s->tilt_t = 0.0f;
            }
        }
    }

    /* ---- pack ------------------------------------------------------------ */
    if (m->batt.st.valid) {
        float cut = c->uv_cutoff_v > 0.0f ? c->uv_cutoff_v : 3.2f * cells(p);
        if (fresh_step(&s->f_batt, &m->batt.st, dt, c->stale_batt_s)) f |= TT_SF_BATT;
        if (finitef_(m->batt.v) && m->batt.v < cut) {
            s->uv_t += dt;
            if (s->uv_t >= c->uv_debounce_s) f |= TT_SF_UNDERVOLT;
        } else {
            s->uv_t = 0.0f;
        }
    }

    /* ---- RC receiver ------------------------------------------------------ */
    s->rc_present = m->rc.st.valid;
    if (s->rc_present) {
        int stale = fresh_step(&s->f_rc, &m->rc.st, dt, c->stale_rc_s);
        int lost = (m->rc.failsafe & TT_RC_FRAME_LOST) != 0;
        s->rc_lost_t = lost ? s->rc_lost_t + dt : 0.0f;
        if (stale || (m->rc.failsafe & TT_RC_FAILSAFE) || s->rc_lost_t >= c->rc_lost_s) {
            f |= TT_SF_RC_LOST;
        } else if (!lost) {
            /* Channels mean something only in a frame that arrived. */
            int arm_sw = m->rc.ch[c->rc_arm_ch & 7u] > 0.5f;
            if (m->rc.ch[c->rc_kill_ch & 7u] > 0.5f) f |= TT_SF_KILL;
            if (!arm_sw && (s->state == TT_SAFE_ARMED || s->state == TT_SAFE_DRIVING))
                f |= TT_SF_RC_DISARM;
            /* Arm switch cycled off -> on: the operator acknowledges. */
            if (arm_sw && !s->arm_sw_prev && s->state == TT_SAFE_FAULT) s->latched = 0u;
            s->arm_sw_prev = (uint8_t)arm_sw;
        }
    }

    /* ---- position --------------------------------------------------------- */
    if (pose && pose->valid) {
        const float *fe = c->fence;
        s->pos_held = 1u;
        if (fe[1] > fe[0] && fe[3] > fe[2] &&
            (pose->x < fe[0] || pose->x > fe[1] || pose->y < fe[2] || pose->y > fe[3]))
            f |= TT_SF_GEOFENCE;
        if (pose->age_s > c->pos_timeout_s) f |= TT_SF_POS_LOST;
    } else if (s->pos_held) {
        f |= TT_SF_POS_LOST;
    }
    return f;
}

/* Every sensor the core needs is there and fresh. */
static int sensors_ready(const TtSafety *s, const TtMeas *m)
{
    const TtParams *p = s->p;
    int w;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if ((p->driven_mask & (1u << w)) && !m->drv[w].st.valid) return 0;
        if ((p->odo_wheel_mask & (1u << w)) && !m->enc[w].st.valid) return 0;
    }
    return m->imu.st.valid != 0;
}

void tt_safety_step(TtSafety *s, const TtMeas *m, const TtCmd *core,
                    const TtSafetyPose *pose, TtCmd *out)
{
    const TtSafetyCfg *c = &s->cfg;
    const TtParams *p = s->p;
    TtCmd in = *core;              /* `out` may alias `core` */
    float dt = finitef_(m->dt_s) && m->dt_s > 0.0f ? m->dt_s : 0.0f;
    int glitch = 0, w, moving;
    uint32_t act, trip;

    act = check(s, m, &in, pose, dt, &glitch);
    s->t_s += dt;
    s->latched |= act & TT_SF_LATCHED;
    s->active = act;

    if (s->v_ok && fabsf(s->v) < c->rest_v) s->rest_t += dt;
    else s->rest_t = 0.0f;

    /* Faults that matter in the current state. Disarmed, a non-latched
     * condition only blocks arming; armed, anything trips. */
    trip = s->latched;
    if (s->state == TT_SAFE_ARMED || s->state == TT_SAFE_DRIVING) trip |= act;

    switch (s->state) {
    case TT_SAFE_DISARMED: {
        int ok = in.arm && act == 0u && s->latched == 0u && sensors_ready(s, m) &&
                 s->v_ok && s->rest_t >= c->rest_s;
        if (s->rc_present) ok = ok && s->arm_sw_prev;
        else if (c->rc_required) ok = 0;
        if (m->batt.st.valid) {
            float arm_v = c->uv_arm_v > 0.0f ? c->uv_arm_v : 3.5f * cells(p);
            ok = ok && finitef_(m->batt.v) && m->batt.v >= arm_v;
        }
        if (ok && !trip) s->state = TT_SAFE_ARMED;
        break;
    }
    case TT_SAFE_ARMED:
    case TT_SAFE_DRIVING:
        if (trip) break;
        if (!in.arm) { s->state = TT_SAFE_DISARMED; break; }
        moving = fabsf(s->v) > c->rest_v;
        for (w = 0; w < TT_MAX_WHEELS; w++)
            if (in.wheel_torque_nm[w] != 0.0f) moving = 1;
        s->state = (uint8_t)(moving ? TT_SAFE_DRIVING : TT_SAFE_ARMED);
        break;
    default:
        break;
    }

    if (trip && s->state != TT_SAFE_FAULT) {
        s->state = TT_SAFE_FAULT;
        s->first = trip;
        s->t_fault_s = 0.0f;
        s->cat = 255u;
        s->stopped = 0u;
    }

    if (s->state == TT_SAFE_FAULT) {
        uint32_t now = act | s->latched;
        /* Category 0 if any category-0 fault is present, or a stop on the
         * motors is impossible (no speed); never back down within a fault. */
        if ((now & TT_SF_CAT0) || !s->v_ok) s->cat = 0u;
        else if (s->cat == 255u) s->cat = 1u;
        s->t_fault_s += dt;
        if (s->cat == 1u && (s->rest_t >= c->rest_s || s->t_fault_s >= c->stop_max_s))
            s->stopped = 1u;
        /* Out of FAULT: nothing active, nothing latched, the stop finished,
         * and the core no longer asks to drive. */
        if (now == 0u && (s->cat == 0u || s->stopped) && !in.arm) {
            s->state = TT_SAFE_DISARMED;
            s->cat = 255u;
            s->first = 0u;
        }
    }
    s->faults = act | s->latched;

    /* ---- the command ------------------------------------------------------ */
    *out = in;
    switch (s->state) {
    case TT_SAFE_ARMED:
    case TT_SAFE_DRIVING:
        if (finitef_(in.steer_rad)) s->steer_hold = in.steer_rad;
        break;
    case TT_SAFE_DISARMED:
        out->arm = 0u;
        for (w = 0; w < TT_MAX_WHEELS; w++) out->wheel_torque_nm[w] = 0.0f;
        if (!finitef_(out->steer_rad)) out->steer_rad = s->steer_hold;
        if (!finitef_(out->brake_01)) out->brake_01 = 1.0f;
        break;
    default: {                                    /* FAULT */
        int stop = s->cat == 1u && !s->stopped;
        out->arm = (uint8_t)stop;
        out->brake_01 = 1.0f;
        if (s->cat == 0u || !finitef_(in.steer_rad)) out->steer_rad = s->steer_hold;
        for (w = 0; w < TT_MAX_WHEELS; w++) out->wheel_torque_nm[w] = 0.0f;
        if (stop) {
            /* Brake on the motors: the whole car decelerates at stop_decel,
             * shared over the driven wheels, fading out near rest so it
             * stops instead of reversing. */
            int n = 0;
            float t, k;
            for (w = 0; w < TT_MAX_WHEELS; w++) if (p->driven_mask & (1u << w)) n++;
            t = n > 0 ? p->mass_eff_kg * c->stop_decel * p->wheel_radius_m / (float)n : 0.0f;
            if (p->wheel_regen_max_nm > 0.0f && t > p->wheel_regen_max_nm) t = p->wheel_regen_max_nm;
            k = c->stop_v_taper > 0.0f ? fabsf(s->v) / c->stop_v_taper : 1.0f;
            if (k > 1.0f) k = 1.0f;
            t *= (s->v > 0.0f ? -k : k);
            for (w = 0; w < TT_MAX_WHEELS; w++)
                if (p->driven_mask & (1u << w)) out->wheel_torque_nm[w] = t;
        }
        break;
    }
    }
}
