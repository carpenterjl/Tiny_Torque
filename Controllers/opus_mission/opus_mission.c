/*
 * opus_mission.c — closed-loop mission controller for the Opus Vector.
 *
 * The manoeuvre: arm, accelerate to 4.5 m/s, hold it for exactly 14.5 m, turn
 * 45 degrees left without lifting, run exactly 7.5 m more, then stop in exactly
 * 1.5 m. No operator input at any point.
 *
 * Three design decisions carry most of the accuracy, and each is defended at
 * its site below:
 *
 *   1. Odometry comes from the UNPOWERED FRONT wheels' tick COUNTERS. Drive
 *      wheels lie under acceleration and the velocity channel is the noisy one.
 *   2. Heading comes from the gyro, less the bias it read while the car stood
 *      still to arm (FLU, so a left turn is positive by definition). The
 *      front-encoder differential stands in only while the IMU is out: a
 *      left/right wheel-size mismatch reads as yaw (FW-15).
 *   3. Every distance target is an ABSOLUTE odometer value and nothing is reset
 *      at a phase boundary, so a phase trigger that fires one tick late shifts
 *      where the car is, never how far it has gone.
 *
 * Firmware hygiene (FW-04/05): float32 only, every literal suffixed, every
 * threshold a time or a rate, dt measured from timestamps.
 *
 * ROUTES (NAV-04). Given a route (opus_set_route), the same arming, launch,
 * safety and stop drive a different middle: FOLLOW, where the path tracker
 * (core/tt_track, L4) steers from the navigation filter's pose carried over
 * the commands in flight, and the speed plan (core/tt_vplan, L5) sets the
 * speed. Near the route's end the stop takes over exactly as it does after
 * leg B: the distance left on the route becomes an odometer target, flown
 * by BRAKE and CREEP. Without a route nothing here changes.
 */
#include "opus_mission.h"
#include "mission_cfg.h"
#include "tt_nav.h"

#include <math.h>
#include <string.h>

/* ------------------------------------------------------------------ utils --*/

static float clampf(float v, float lo, float hi)
{
    return v < lo ? lo : (v > hi ? hi : v);
}

static int finitef_(float v)
{
    /* Deliberately not isfinite(): freestanding MCU libcs vary. A value is
     * finite iff it compares equal to itself and is within a sane magnitude. */
    return (v == v) && (v < 1e18f) && (v > -1e18f);
}

/* First-order low-pass blend for a time constant, so a filter means the same
 * thing at any loop rate. */
static float lp_alpha(float dt, float tau)
{
    return tau > 0.0f ? 1.0f - expf(-dt / tau) : 1.0f;
}

/* Phases that command the motors. FOLLOW (11) sits outside the manoeuvre's
 * range of numbers (they are append-only), so ranges go through this. */
static int driving(OpusPhase ph)
{
    return (ph >= OPUS_LAUNCH && ph <= OPUS_CREEP) || ph == OPUS_FOLLOW;
}

/* Where the car is meant to be going straight: the manoeuvre's launch and
 * measured legs, or a straight stretch of a route. The steering trim and the
 * odometry pair's left/right scale are learned only there (FW-15). */
static int straight(const OpusState *st)
{
    if (st->route == 0)
        return st->phase == OPUS_LAUNCH || st->phase == OPUS_CRUISE_A || st->phase == OPUS_CRUISE_B;
    return (st->phase == OPUS_LAUNCH || st->phase == OPUS_FOLLOW) &&
           fabsf(st->trk.kappa_route) < RT_STRAIGHT_K;
}

static int popcount4(uint32_t m)
{
    return (int)((m & 1u) + ((m >> 1) & 1u) + ((m >> 2) & 1u) + ((m >> 3) & 1u));
}

/* Coast-down drag at speed v. Feed-forward only — error here costs accuracy,
 * not stability, because the speed loop trims what is left. */
static float drag_n(const TtParams *p, float v)
{
    float a = v < 0.0f ? -v : v;
    float f = p->drag_c0 + p->drag_c1 * a + p->drag_c2 * a * a;
    return v >= 0.0f ? f : -f;
}

/* ------------------------------------------------------------- odometry ----
 * Encoder counts arrive cumulative (the target unwraps the hardware register),
 * so a delta is one int32 subtraction. A jump faster than SF_GLITCH_MPS
 * cannot be real motion and is dropped rather than believed.
 */
static int32_t enc_delta(OpusState *st, const TtMeas *m, int w, float dt, int *glitch)
{
    int32_t d;
    float mpc = tt_m_per_count(st->p, w);
    if (!m->enc[w].st.valid) return 0;
    if (!st->enc_has_prev[w]) {
        st->enc_prev[w] = m->enc[w].count;
        st->enc_has_prev[w] = 1;
        return 0;
    }
    d = (int32_t)((uint32_t)m->enc[w].count - (uint32_t)st->enc_prev[w]);
    st->enc_prev[w] = m->enc[w].count;
    if (mpc > 0.0f && (float)(d < 0 ? -d : d) * mpc > SF_GLITCH_MPS * dt) {
        if (glitch) *glitch = 1;
        return 0;
    }
    return d;
}

float opus_odo_m(const OpusState *st)
{
    const TtParams *p = st->p;
    return 0.5f * ((float)st->odo_cnt_l * tt_m_per_count(p, st->odo_l) +
                   (float)st->odo_cnt_r * tt_m_per_count(p, st->odo_r)) + st->odo_corr_m;
}

/* The odometer the mission measures legs with. (Until SEN-02 this added a
 * correction for the sim encoder's integration bias — 22.5 mm over the
 * braking leg. The sim now counts the angle the physics integrated, as a
 * real encoder does, so there is nothing left to correct.) */
static float odo_effective(const OpusState *st)
{
    return opus_odo_m(st);
}

/* True metres covered since odometer value `from`, on the scale the
 * navigation filter has found (FW-15). */
static float leg_m(const OpusState *st, float odo, float from)
{
    return st->odo_scale * (odo - from);
}

void opus_set_odo_scale(OpusState *st, float k)
{
    /* Up to the brake: the stop is flown on one scale, the one it started
     * with, so the target cannot move under the braking profile. */
    if ((st->phase < OPUS_LAUNCH || st->phase > OPUS_CRUISE_B) && st->phase != OPUS_FOLLOW) return;
    if (!(k >= NV_KW_MIN && k <= NV_KW_MAX)) return;
    st->odo_scale = k;
    if (st->phase == OPUS_CRUISE_B)
        st->stop_target_m = st->leg_start_m + MI_STOP_FROM_EXIT / k;
}

/* ----------------------------------------------------- commands in flight --
 * FW-13. A command acts cmd_delay_s after its tick (a timer-latched output
 * holds each one for exactly a period), so when a measurement arrives the
 * newest few commands have not reached the car yet. The estimators need the
 * one that is acting; the loops need to know what the rest will do. */
static int delay_ticks(const OpusState *st, float dt)
{
    int n = dt > 0.0f ? (int)(st->dly_s / dt + 0.5f) : 0;
    return n < 0 ? 0 : (n > OPUS_PRED_SLOTS - 1 ? OPUS_PRED_SLOTS - 1 : n);
}

/* The command acting on the car this tick (none yet: zeros). */
static OpusSent acting(const OpusState *st, int n)
{
    OpusSent z;
    int i = st->sent_n - 1 - n;
    if (i >= 0) return st->sent[i];
    memset(&z, 0, sizeof(z));
    return z;
}

/* Carry the estimates over the n commands still in flight: the speed the
 * car will have, and the distance and the heading it will cover, by the
 * time this tick's command lands. Steering sets a yaw rate almost at once,
 * so the heading still to come is the rates already asked for. The axle
 * loads too (FW-19): they follow the car's acceleration through the body's
 * pitch (EN_LOAD_TAU_S), and the acceleration follows each command as it
 * lands - the force it sent, at the gain the launch measured, less the
 * drag. A budget drawn on the loads the car HAD a delay ago asks the rear
 * axle for its standing share while the brake has already lifted it: at
 * 30 ms the rears ran 8.5 % slip against the fronts' 4 and the limiter
 * cut them. (A friction brake's force is not in the sent force; no car
 * with a grip budget has one, ACT-08.) */
static void predict(OpusState *st, int n, float dt)
{
    const TtParams *p = st->p;
    float v = st->v_est, s = 0.0f, h = 0.0f, l = st->ax_load;
    int i = st->sent_n - n;
    for (i = i < 0 ? 0 : i; i < st->sent_n; i++) {
        float v1 = v + st->sent[i].a * dt;
        float ax = p->mass_eff_kg > 0.0f
                 ? (st->sent[i].f * st->f_gain - drag_n(p, v)) / p->mass_eff_kg
                 : st->sent[i].a;
        s += 0.5f * (v + v1) * dt;
        v = v1;
        h += st->sent[i].r * dt;
        l += (ax - l) * lp_alpha(dt, EN_LOAD_TAU_S);
    }
    st->v_pred = v;
    st->s_pred = s;
    st->psi_fly = h;
    st->ax_land = l;
}

static void remember(OpusState *st, const TtCmd *out)
{
    OpusSent *e;
    if (st->sent_n == OPUS_PRED_SLOTS) {
        memmove(st->sent, st->sent + 1, sizeof(st->sent[0]) * (OPUS_PRED_SLOTS - 1));
        st->sent_n--;
    }
    e = &st->sent[st->sent_n++];
    e->a = st->a_cmd;
    e->r = st->r_cmd;
    e->steer = st->wheel_est;   /* where the wheels go, not the horn */
    {
        float f = 0.0f;
        int w;
        for (w = 0; w < TT_MAX_WHEELS; w++) f += out->wheel_torque_nm[w];
        e->f = st->p->wheel_radius_m > 0.0f ? f / st->p->wheel_radius_m : 0.0f;
        /* Slipping near the limit the way its torque pushes (FW-16). */
        e->lim = 0;
        st->drive_slip = 0.0f;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            float s = out->wheel_torque_nm[w] >= 0.0f ? st->alloc.slip[w] : -st->alloc.slip[w];
            if (!(st->p->driven_mask & (1u << w))) continue;
            if (s > st->drive_slip) st->drive_slip = s;
            if (s > GA_FG_SLIP_FRAC * st->p->slip_max) e->lim |= 1 << w;
        }
    }
    e->t_odo[0] = out->wheel_torque_nm[st->odo_l];
    e->t_odo[1] = out->wheel_torque_nm[st->odo_r];
}

/* -------------------------------------------------------------- lifecycle --*/

void opus_init(OpusState *st, const TtParams *p, float rate_hz)
{
    memset(st, 0, sizeof(*st));
    st->p = p;
    st->rate_hz = rate_hz;
    /* Odometry and the heading baseline: the front pair if both are odometry
     * wheels (the Opus case), else the rear pair. */
    if ((p->odo_wheel_mask & 0x3u) == 0x3u) { st->odo_l = TT_FL; st->odo_r = TT_FR; }
    else                                    { st->odo_l = TT_RL; st->odo_r = TT_RR; }
    opus_reset(st);
}

void opus_reset(OpusState *st)
{
    const TtParams *p = st->p;
    const TtRoute *route = st->route;
    float rate = st->rate_hz, vmax = st->route_vmax;
    int l = st->odo_l, r = st->odo_r, iq = st->drive_iq, bad = st->route_bad;

    memset(st, 0, sizeof(*st));
    st->p = p;
    st->rate_hz = rate;
    st->odo_l = l;
    st->odo_r = r;
    st->route = route;
    st->route_vmax = vmax;
    st->route_bad = bad;
    st->drive_iq = iq;
    st->dly_s = p->cmd_delay_s;
    st->phase = OPUS_BOOT;
    st->drive_ok = 1;
    st->odo_scale = 1.0f;
    st->f_gain = 1.0f;
    st->mu_nom = p->tyre_mu > 0.0f ? p->tyre_mu : 1.0f;
    st->mu_est = st->mu_nom;
    st->a_brake_nom = route ? route->a_brake : LI_A_BRAKE;
    st->a_brake = st->a_brake_nom;

    pid_init(&st->spd_pid, GA_SPD_KP, GA_SPD_KI, 0.0f, -GA_SPD_TRIM, GA_SPD_TRIM);
    pid_init(&st->yaw_pid, GA_YAW_KP, GA_YAW_KI, 0.0f, -GA_YAW_TRIM, GA_YAW_TRIM);
    tt_alloc_reset(&st->alloc);

    if (route) {
        TtTrackCfg tc;
        tt_track_default_cfg(&tc);
        if (p->wheelbase_m > 0.0f && p->max_steer_rad > 0.0f)
            tc.kappa_max = tanf(p->max_steer_rad) / p->wheelbase_m;
        tt_track_reset(&st->trk, &tc, route);
        tt_vplan_build(&st->plan, route, vmax, RT_LAT_USE * route->a_lat_max, 0.0f, 0.0f, -1.0f);
        st->plan_mu = st->mu_est;
    }
}

void opus_set_route(OpusState *st, const TtRoute *r, float v_max)
{
    st->route = r;
    st->route_vmax = v_max > 0.0f ? v_max : 0.0f;
    st->route_bad = 0;
    opus_reset(st);
    /* Checked here, once, not while arming: walking the route is the
     * longest thing the firmware ever does in one call (HIL-06 timed it at
     * the arming tick). */
    st->route_bad = r && (!r->has_start ||
                          tt_route_validate(r, st->trk.cfg.kappa_max) != TT_RTE_OK);
}

void opus_set_drive_feedback(OpusState *st, int iq)
{
    st->drive_iq = iq;
}

/* The force the driven wheels are producing, from their measured current
 * (FW-17); valid only while every one reports a fresh sample. */
static int drive_force(const OpusState *st, const TtMeas *m, float *f)
{
    const TtParams *p = st->p;
    int w;
    *f = 0.0f;
    if (!(p->wheel_radius_m > 0.0f)) return 0;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if (!(p->driven_mask & (1u << w))) continue;
        if (!m->drv[w].st.valid) return 0;
        *f += m->drv[w].iq_a * p->kt[w] * p->gear[w] * p->eta_drive / p->wheel_radius_m;
    }
    return 1;
}

/* FW-17: time the launch's torque step through the drives. Called at the
 * start of a tick, with that tick's measurements; the step was commanded on
 * an earlier tick (dly_k0). */
static void time_delay(OpusState *st, const TtMeas *m)
{
    uint32_t k = st->cmd.seq + 1u;           /* this tick's number */
    float f;
    if (st->dly_state != 1) return;
    if (!drive_force(st, m, &f)) { st->dly_state = 3; return; }
    if (f >= GA_DLY_FRAC * st->dly_f0) {
        /* sampled at tick k, it acted after tick k - 1's sample */
        int n = (int)(k - st->dly_k0) - 1;
        if (n >= 0 && n < OPUS_PRED_SLOTS) {
            st->dly_s = (float)n * m->dt_s;
            st->dly_state = 2;
        } else {
            st->dly_state = 3;
        }
    } else if (k - st->dly_k0 > (uint32_t)OPUS_PRED_SLOTS) {
        st->dly_state = 3;          /* never arrived: keep the parameters' */
    }
}

/* ------------------------------------------------- friction probe (FW-18) --
 * See mission_cfg.h. The car needs both axles driven, each drive's current
 * (the force each wheel really carries) and its speed; not on a route, whose
 * speeds are its own plan's. */
static int probe_can(const OpusState *st)
{
    const TtParams *p = st->p;
    return st->drive_iq && !st->route &&
           (p->driven_mask & 0x3u) != 0u && (p->driven_mask & 0xCu) != 0u &&
           p->slip_max > 0.0f && p->mass_kg > 0.0f && p->wheel_radius_m > 0.0f &&
           p->eta_drive > 0.0f && p->eta_back > 0.0f &&
           p->front_weight_frac > 0.05f && p->front_weight_frac < 0.95f;
}

/* The car's speed while probing: the mean of the driven wheels' speeds (the
 * drives' own), each less its offset at cruise (FW-16's, taken against the
 * odometry pair's: the radius it really has against the pair's), on the
 * odometer's scale. 0 if a drive is not reporting. */
static int probe_speed(const OpusState *st, const TtMeas *m, float *v)
{
    const TtParams *p = st->p;
    float s = 0.0f, ref;
    int w, n = 0;
    if (st->mu_cn <= 0) return 0;
    ref = 0.5f * (st->mu_cs[st->odo_l] + st->mu_cs[st->odo_r]) / (float)st->mu_cn;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float off;
        if (!(p->driven_mask & (1u << w))) continue;
        if (!m->drv[w].st.valid || !(p->gear[w] > 0.0f) || !finitef_(m->drv[w].omega_m)) return 0;
        off = st->mu_cs[w] / (float)st->mu_cn - ref;
        s += m->drv[w].omega_m / p->gear[w] * p->wheel_radius_m / (1.0f + off);
        n++;
    }
    if (n == 0) return 0;
    *v = s / (float)n * (1.0f + p->cal_scale);
    return 1;
}

/* A brush tyre's slip times its stiffness C/Fz at a = force / load: odd in a. */
static float pr_g(float a, float mu)
{
    float r = (a < 0.0f ? -a : a) / mu, s;
    if (r > 0.999f) r = 0.999f;
    s = 2.0f * mu * (1.0f - sqrtf(1.0f - r));
    return a < 0.0f ? -s : s;
}

/* For each mu the model is a straight line in g, so least squares gives the
 * offset and 1/C at once; the mu with the smallest residual wins, searched
 * coarse and then fine about the best (a few thousand square roots). */
static float probe_grid(const float *af, const float *ar, const float *d, int n,
                        float *slope_out, float *c_out)
{
    float best = -1.0f, best_mu = 0.0f, lo = 0.0f, step = 0.05f, from, to;
    int i, k, pass;
    if (n < 3 || n > OPUS_PR_SAMPLES) return 0.0f;
    for (i = 0; i < n; i++) {
        float a = fabsf(af[i]) > fabsf(ar[i]) ? fabsf(af[i]) : fabsf(ar[i]);
        if (a > lo) lo = a;
    }
    lo /= 0.98f;                     /* the peak lies above every force seen */
    if (lo < GA_PR_MU_MIN) lo = GA_PR_MU_MIN;
    from = lo;
    to = GA_PR_MU_MAX;
    for (pass = 0; pass < 2; pass++) {
        for (k = 0; ; k++) {
            float x[OPUS_PR_SAMPLES], mx = 0.0f, md = 0.0f, sxx = 0.0f, sxd = 0.0f, sdd = 0.0f;
            float mu = from + (float)k * step, slope, e;
            if (mu > to + 1e-4f) break;
            for (i = 0; i < n; i++) {
                x[i] = pr_g(ar[i], mu) + pr_g(af[i], mu);
                mx += x[i];
                md += d[i];
            }
            mx /= (float)n;
            md /= (float)n;
            for (i = 0; i < n; i++) {
                float dx = x[i] - mx, dd = d[i] - md;
                sxx += dx * dx;
                sxd += dx * dd;
                sdd += dd * dd;
            }
            if (!(sxx > 0.0f)) continue;
            slope = sxd / sxx;           /* 1 / C: a tyre's slip grows with force */
            if (!(slope > 0.0f)) continue;
            e = sdd - slope * sxd;
            if (best < 0.0f || e < best) {
                best = e;
                best_mu = mu;
                *slope_out = slope;
                *c_out = md - slope * mx;
            }
        }
        if (best < 0.0f) return 0.0f;
        from = best_mu - step > lo ? best_mu - step : lo;
        to = best_mu + step < GA_PR_MU_MAX ? best_mu + step : GA_PR_MU_MAX;
        step = 0.005f;
    }
    return best_mu;
}

/* The fit, then again without the samples it misses by far (GA_PR_OUTLIER). */
float opus_probe_mu(const float *af, const float *ar, const float *d, int n)
{
    float slope = 0.0f, c = 0.0f, res[OPUS_PR_SAMPLES], srt[OPUS_PR_SAMPLES], med, lim;
    float af2[OPUS_PR_SAMPLES], ar2[OPUS_PR_SAMPLES], d2[OPUS_PR_SAMPLES];
    float mu = probe_grid(af, ar, d, n, &slope, &c);
    int i, j, n2 = 0;
    if (!(mu > 0.0f)) return 0.0f;
    for (i = 0; i < n; i++) {
        float r = d[i] - c - slope * (pr_g(ar[i], mu) + pr_g(af[i], mu));
        res[i] = r < 0.0f ? -r : r;
        /* insertion into the sorted copy, for the median */
        for (j = i; j > 0 && srt[j - 1] > res[i]; j--) srt[j] = srt[j - 1];
        srt[j] = res[i];
    }
    med = srt[n / 2];
    lim = GA_PR_OUTLIER * (med > 1e-5f ? med : 1e-5f);
    for (i = 0; i < n; i++) {
        if (res[i] > lim) continue;
        af2[n2] = af[i];
        ar2[n2] = ar[i];
        d2[n2] = d[i];
        n2++;
    }
    if (n2 < n && n2 >= GA_PR_MIN_N) {
        float mu2 = probe_grid(af2, ar2, d2, n2, &slope, &c);
        if (mu2 > 0.0f) mu = mu2;
    }
    return mu;
}

/* One sample: each axle's tyre force over its static load (no transfer at
 * a steady speed), from the drives' measured current (a braking wheel takes
 * 1/eta_back of the motor's torque, as tt_torque_to_iq has it) less the
 * wheel's own losses (the c0 + c1 v of the drag model, a quarter each, as
 * the odometer's slip model has it); and the rear wheels' slip over the
 * front ones', side by side, where the car's own speed cancels. Also watches
 * for a wheel slipping toward its limit, past its cruise offset (FW-16). */
static void probe_sample(OpusState *st, const TtMeas *m)
{
    const TtParams *p = st->p;
    const float g = 9.80665f;
    float v = st->v_est, own, af = 0.0f, ar = 0.0f, d = 0.0f;
    int w, nf = 0, nr = 0, ns = 0;
    if (st->pr_cnt >= OPUS_PR_SAMPLES || st->phase_t - st->pr_last_t < GA_PR_DT_S) return;
    if (!(v > GA_MU_MIN_V)) { st->pr_bad = 1; return; }
    own = (p->drag_c0 + p->drag_c1 * v) / (float)TT_MAX_WHEELS;
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float t, slip, off, fz, a;
        if (!(p->driven_mask & (1u << w))) continue;
        if (!m->drv[w].st.valid || !st->wheel_omega_ok[w] || !finitef_(m->drv[w].iq_a)) { st->pr_bad = 1; return; }
        slip = st->wheel_omega[w] * p->wheel_radius_m / v - 1.0f;
        if (slip > GA_PR_BAD_SLIP || slip < -GA_PR_BAD_SLIP) { st->pr_bad = 1; return; }
        off = st->mu_cn > 0 ? st->mu_cs[w] / (float)st->mu_cn : 0.0f;
        t = m->drv[w].iq_a * p->kt[w] * p->gear[w];
        t = t >= 0.0f ? t * p->eta_drive : t / p->eta_back;
        fz = 0.5f * p->mass_kg * g * (w < 2 ? p->front_weight_frac : 1.0f - p->front_weight_frac);
        /* over the force gain the launch measured (FW-16): a heavy car or a
         * weak motor makes less acceleration of a newton of command, and
         * this is the force the tyre really carries per newton of its load */
        a = st->f_gain * (t / p->wheel_radius_m - own) / fz;
        if (w < 2) {
            af -= a;
            nf++;
            if (off - slip > GA_PR_ABORT_FRAC * p->slip_max) st->pr_abort = 1;
        } else {
            ar += a;
            nr++;
            if (slip - off > GA_PR_ABORT_FRAC * p->slip_max) st->pr_abort = 1;
        }
    }
    if ((p->driven_mask & 0x5u) == 0x5u) { d += st->wheel_omega[TT_RL] - st->wheel_omega[TT_FL]; ns++; }
    if ((p->driven_mask & 0xAu) == 0xAu) { d += st->wheel_omega[TT_RR] - st->wheel_omega[TT_FR]; ns++; }
    if (nf == 0 || nr == 0 || ns == 0) { st->pr_bad = 1; return; }
    af /= (float)nf;
    ar /= (float)nr;
    st->pr_af[st->pr_cnt] = af;
    st->pr_ar[st->pr_cnt] = ar;
    st->pr_d[st->pr_cnt] = d / (float)ns * p->wheel_radius_m / v;
    st->pr_cnt++;
    st->pr_last_t = st->phase_t;
    if (af > st->pr_amax) st->pr_amax = af;
}

/* The probe's force pair this tick, on leg A: GA_PR_PRE_S of samples with
 * none (the fit's zero), then the ramp, ended at its top or by a wheel
 * slipping toward its limit; then the fit. The stop is planned on what it
 * found (FW-16's machinery); if it could not judge, on the launch's verdict. */
static void probe_run(OpusState *st)
{
    const TtParams *p = st->p;
    float t = st->phase_t - GA_PR_T0;
    if (st->pr_state == 0 && t >= -GA_PR_PRE_S) {
        st->pr_state = probe_can(st) ? 1 : 3;
        st->pr_last_t = -1.0f;
    }
    if (st->pr_state == 1 && (st->pr_bad || st->pr_abort || t >= GA_PR_RAMP_S)) {
        float mu = (!st->pr_bad && st->pr_cnt >= GA_PR_MIN_N)
                 ? opus_probe_mu(st->pr_af, st->pr_ar, st->pr_d, st->pr_cnt) : 0.0f;
        st->pr_hold_t = GA_PR_SETTLE_S + st->dly_s;
        if (mu > 0.0f) {
            /* the stop and the budget take GA_PR_USE of it, not GA_MU_USE */
            float mu_use = mu * GA_PR_USE / GA_MU_USE;
            st->pr_mu = mu;
            st->pr_state = 2;
            if (mu_use < st->mu_est) st->mu_est = mu_use;
        } else {
            st->pr_state = 3;
        }
    }
    if (st->pr_state == 3 && st->mu_launch > 0.0f) {
        if (st->mu_launch < st->mu_est) st->mu_est = st->mu_launch;
        st->mu_launch = 0.0f;
    }
    st->pr_n = (st->pr_state == 1 && t > 0.0f)
             ? GA_PR_A_MAX * p->mass_kg * 9.80665f * p->front_weight_frac * t / GA_PR_RAMP_S : 0.0f;
}

void opus_set_pose(OpusState *st, float x, float y, float psi, int ok)
{
    st->pose[0] = x;
    st->pose[1] = y;
    st->pose[2] = psi;
    st->pose_ok = ok;
}

static void enter(OpusState *st, OpusPhase ph)
{
    st->phase = ph;
    st->phase_t = 0.0f;
    /* A leg's integral must not leak into the next one. */
    pid_reset(&st->spd_pid);
}

/* Latch the datum for a newly-started measured leg. */
static void begin_leg(OpusState *st)
{
    st->leg_start_m = odo_effective(st);
}

/* ---------------------------------------------------------------- arming ---
 * A stationary car cannot prove its encoders work: a counter that never moves
 * is indistinguishable from a dead one. So the standing check verifies
 * everything that CAN be checked at rest, and encoder liveness is proven over
 * the first half-metre of the launch instead.
 */
static void arm_checks(OpusState *st, const TtMeas *m)
{
    const TtParams *p = st->p;
    int f = 0, w;

    if (tt_params_validate(p) != 0) f |= FA_PARAMS;
    /* A route must have a surveyed start, and the car must be able to
     * drive it: its tightest curve within the steering lock. */
    if (st->route && st->route_bad)
        f |= FA_PARAMS;

    for (w = 0; w < TT_MAX_WHEELS; w++) {
        if ((p->driven_mask & (1u << w)) && !m->drv[w].st.valid)    f |= FA_NO_MOTORS;
        if ((p->odo_wheel_mask & (1u << w)) && !m->enc[w].st.valid) f |= FA_NO_ENCODERS;
    }

    /* Battery: present and near full, drawing nothing while stopped. */
    if (m->batt.st.valid && m->batt.v > 0.0f && m->batt.v < 0.85f * p->v_rail_nom) f |= FA_BATTERY;

    /* The scheduler must deliver the rate it promised at init. */
    if (st->rate_hz > 0.0f) {
        float nom = 1.0f / st->rate_hz;
        if (!(m->dt_s > nom * (1.0f - SF_DT_TOL) && m->dt_s < nom * (1.0f + SF_DT_TOL))) f |= FA_DT;
    }

    /* Specific force at rest is +g up, i.e. magnitude ~9.81. */
    if (m->imu.st.valid) {
        const float *a = m->imu.accel;
        float mag = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (mag < 9.3f || mag > 10.3f) f |= FA_IMU;
        if (!finitef_(m->imu.gyro[2])) f |= FA_NAN;
    }

    st->fault |= f;
}

/* ----------------------------------------------------------- longitudinal --
 * Force-based. The loop produces an acceleration, which becomes a road force
 * and then a torque per driven wheel. Turning torque into the drive's own
 * units (Iq for an FOC drive, volts for the sim's brushed ESC) is the
 * target's job, so nothing here knows what kind of motor is fitted. At cruise
 * 94 % of the command is feed-forward, so the PID trims rather than drives.
 */
static void longitudinal(OpusState *st, const TtMeas *m,
                         float v_ref, float a_ff, int allow_brake, TtCmd *out)
{
    const TtParams *p = st->p;
    int   n_driven = popcount4(p->driven_mask), w;
    float a_trim, a_cmd, f_req, f_motor, f_fric, t_each, g;

    /* On the speed the car will have when this command lands (FW-13). */
    a_trim = pid_update(&st->spd_pid, v_ref, st->v_pred, m->dt_s);
    a_cmd  = clampf(a_ff + a_trim, -LI_A_MAX, LI_A_MAX);

    /* Total longitudinal force the car needs, coast drag included. Note the
     * EFFECTIVE mass: a fifth of what has to be accelerated is spinning rotor,
     * not translating car, and ignoring it makes every force command small.
     * Over the force gain the car has shown (FW-16), from the end of the
     * launch it is measured on: a gain stepping in mid-launch would move
     * where the launch ends. */
    g = (st->phase > OPUS_LAUNCH && !(st->mu_est < st->mu_nom)) ? st->f_gain : 1.0f;
    f_req = (p->mass_eff_kg * a_cmd + drag_n(p, st->v_pred)) / g;

    if (f_req >= 0.0f) {
        /* Driving. The driven tyres run at slip, so slightly more force is
         * commanded at the wheel than reaches the road (a scale on force, not
         * speed — it must NOT hide inside the drag polynomial). */
        f_motor = f_req / p->traction_eff;
        f_fric  = 0.0f;
    } else {
        /* Braking. The motors take what they can and the friction brake takes
         * the rest. What they can is the car's regen capability: for this car's
         * hobby ESC (shorted winding) proportional to speed, fading to nothing
         * at rest — so the friction brake takes the growing surplus as the car
         * slows — and capped by the grip of the driven axle, which unloads
         * under braking. An FOC car states a flat current limit instead. */
        float need  = -f_req;
        float v_eff = st->v_pred > 0.05f ? st->v_pred : 0.05f;
        float cap   = p->regen_n_per_ms > 0.0f ? p->regen_n_per_ms * v_eff : need;
        if (p->regen_max_n > 0.0f && cap > p->regen_max_n) cap = p->regen_max_n;
        if (p->regen_grip_cap_n > 0.0f && cap > p->regen_grip_cap_n) cap = p->regen_grip_cap_n;
        f_motor = -(need < cap ? need : cap);
        f_fric  = allow_brake ? (need + f_motor) : 0.0f;
    }

    /* The allocator turns the force into wheel torques: front/rear by load,
     * left/right as an open differential, per-wheel limits, and traction
     * control / ABS where the parameters enable them (FW-06). */
    {
        TtVehReq req;
        TtAllocIn ain;
        req.fx_n = f_motor;
        req.mz_nm = 0.0f;
        req.steer_rad = 0.0f;
        /* The car's speed, not the odometry pair's: those wheels are
         * driven too, and a slip reference made of the wheels it judges
         * never sees them lock (FW-13). */
        ain.v_mps = st->v_est;
        ain.yaw_rate = st->psi_dot_f;
        ain.ax_mps2 = a_cmd;
        /* The loads when this command lands, and a lead later (the delay
         * and a tick: until a cut for it can land), the body's pitch
         * carrying them toward what is asked now (FW-19). */
        ain.ax_meas_mps2 = st->ax_land;
        ain.lead_s = st->dly_s + m->dt_s;
        ain.ax_next_mps2 = st->ax_land + (a_cmd - st->ax_land) * lp_alpha(ain.lead_s, EN_LOAD_TAU_S);
        ain.ay_meas_mps2 = st->v_est * st->psi_dot_f;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            ain.wheel_omega[w] = st->wheel_omega[w];
            ain.omega_valid[w] = st->wheel_omega_ok[w];
        }
        /* The grip budget on the friction measured (FW-16), in the units the
         * allocator caps: commanded force, which buys f_gain of the road
         * force the parameters promise. Low kt or a heavy car alike, a
         * wheel's real limit is then mu * Fz / f_gain of command (heavier
         * means loaded harder). On a floor measured slippery, also keep the
         * slip below where the tyre peaks. */
        ain.mu = p->tyre_mu > 0.0f ? st->mu_est / g : 0.0f;
        ain.slip_max = 0.0f;
        ain.probe_n = st->phase == OPUS_CRUISE_A ? st->pr_n : 0.0f;
        if (st->mu_est < st->mu_nom && p->slip_stiffness > 0.0f)
            ain.slip_max = GA_SLIP_PEAK_FRAC * 2.0f * st->mu_est / p->slip_stiffness;
        tt_alloc_step(&st->alloc, p, &req, &ain, m->dt_s, out);
    }
    t_each = n_driven > 0 ? f_motor * p->wheel_radius_m / (float)n_driven : 0.0f;

    /* The friction brake acts on all four wheels. */
    out->brake_01 = p->brake_max_nm > 0.0f
        ? clampf(f_fric * p->wheel_radius_m / (4.0f * p->brake_max_nm), 0.0f, 1.0f) : 0.0f;

    st->t_cmd_nm = t_each;
    st->a_cmd = a_cmd;
    st->v_ref = v_ref;
}

/* Steering backlash inverse (FW-15). The wheels follow the horn through
 * steer_backlash_rad of play; to put them at `d`, push the horn half the play
 * past it on the side they must move to, and leave it where it is while the
 * wheels are already within GA_LASH_HYST of `d`. */
static float lash_inverse(OpusState *st, float d)
{
    float hb = 0.5f * st->p->steer_backlash_rad, u;
    if (!(hb > 0.0f)) { st->horn = d; st->wheel_est = d; return d; }
    if (d > st->wheel_est + GA_LASH_HYST)      u = d + hb;
    else if (d < st->wheel_est - GA_LASH_HYST) u = d - hb;
    else                                       u = st->horn;
    u = clampf(u, -st->p->max_steer_rad, st->p->max_steer_rad);
    st->horn = u;
    st->wheel_est = clampf(st->wheel_est, u - hb, u + hb);
    return u;
}

/* ---------------------------------------------------------------- lateral --
 * Never open-loop the steer angle. The kinematic angle for this corner is
 * 3.4 degrees, but at 4.5 m/s the tyres need another 3 or so of slip that
 * cannot be known in advance, so feed-forward sets the ballpark and a yaw-rate
 * loop finds the rest. Positive is LEFT throughout, command included.
 */
static void lateral(OpusState *st, const TtMeas *m, float psi_dot_ref, TtCmd *out)
{
    const TtParams *p = st->p;
    float v_eff  = st->v_meas > 0.5f ? st->v_meas : 0.5f;
    float ff_deg = atan2f(p->wheelbase_m * psi_dot_ref, v_eff) * OPUS_RAD2DEG;
    float trim   = pid_update(&st->yaw_pid, psi_dot_ref, st->psi_dot_f, m->dt_s);
    float rad    = clampf((ff_deg + trim) * OPUS_DEG2RAD - st->steer_trim,
                          -p->max_steer_rad, p->max_steer_rad);

    /* Servo observer — a hobby servo gives no position, so model it. Used for
     * reporting, not for control. */
    {
        float step = p->servo_slew_rad_s * m->dt_s;
        st->steer_obs_rad += clampf(rad - st->steer_obs_rad, -step, step);
    }

    out->steer_rad = lash_inverse(st, rad);
    st->r_cmd = psi_dot_ref;
}

/* Trapezoidal yaw-rate reference. Ramping in and out keeps the inner front
 * wheel loaded, and because the profile is integrated as COMMANDED the turn is
 * exactly 45 degrees by construction — accuracy then depends on how well the
 * yaw loop tracks, not on integrating a noisy measurement up to a threshold. */
static float turn_profile(float t, float *out_rate)
{
    const float rate = LI_A_LAT / MI_V_CRUISE;             /* 0.889 rad/s */
    const float ramp = LI_TURN_RAMP_S;
    const float ramp_area = rate * ramp;                   /* both ramps together */
    const float hold = (MI_TURN_RAD - ramp_area) / rate;

    float r;
    if (t < ramp)                 r = rate * (t / ramp);
    else if (t < ramp + hold)     r = rate;
    else if (t < ramp + hold + ramp) r = rate * (1.0f - (t - ramp - hold) / ramp);
    else                          r = 0.0f;

    *out_rate = r;
    return ramp + hold + ramp;   /* total duration */
}

/* The heading the car will settle on if nothing more is asked: what it has,
 * the yaw-rate commands still in flight, and the rate it is turning at now
 * run out through the steering's lag (FW-13). Closing a heading loop on the
 * bare measurement reads that lag as a shortfall and pays it back as
 * overshoot: 3 deg at the end of the turn. */
static float psi_pred(const OpusState *st)
{
    return st->psi + st->psi_fly + st->psi_dot_f * st->p->steer_lag_s;
}

static float heading_hold(const OpusState *st)
{
    return clampf(GA_HEAD_KP * (st->psi_ref - psi_pred(st)),
                  -GA_HEAD_MAX_RATE, GA_HEAD_MAX_RATE);
}

/* L4 (NAV-02): the yaw rate the route asks for. The filter's pose is from
 * its last step: carry it over this tick's motion (the gyro, the odometer on
 * the filter's scale), then over the commands still in flight, which is
 * where this tick's command lands; steer from there. Steer the COURSE, the
 * heading plus the body origin's sideslip: on a curve the car rightly
 * points outside the tangent (1.4 deg on the oval at 3 m/s), and a tracker
 * holding the heading to the tangent settles 5-6 cm to the inside.
 *
 * The yaw rate that drives a curvature is then v * kappa less the rate the
 * sideslip itself changes at: on a spiral the sideslip grows with the
 * curvature, and the course turns faster than the heading (0.11 rad/s more
 * on the oval's 1 m spirals at 3 m/s), so r = v * kappa alone turns in
 * early and comes out late. That rate is taken from the route ahead, where
 * the command lands.
 *
 * Also where the car is on the route now, and the speed plan where the
 * command lands. */
static float route_rate(OpusState *st, float ds, float dt)
{
    const float l_r = -0.5f * st->p->wheelbase_m, ck = st->p->tyre_c_alpha;
    float psi0 = st->pose[2], dpsi = st->psi_dot * dt, psi1, pp, x, y, xp, yp, v, k, beta, bdot = 0.0f;
    ds *= st->odo_scale;
    v = st->v_pred > 0.0f ? st->v_pred : 0.0f;
    beta = tt_nav_sideslip(l_r, ck, v, st->psi_dot_f);
    psi1 = psi0 + dpsi;
    x = st->pose[0] + ds * cosf(psi0 + 0.5f * dpsi + beta);
    y = st->pose[1] + ds * sinf(psi0 + 0.5f * dpsi + beta);
    pp = psi1 + st->psi_fly + beta;
    xp = x + st->s_pred * cosf(psi1 + 0.5f * st->psi_fly + beta);
    yp = y + st->s_pred * sinf(psi1 + 0.5f * st->psi_fly + beta);
    k = tt_track_step(&st->trk, xp, yp, pp, v);
    if (dt > 0.0f && v > 0.3f) {
        float s0 = st->trk.s + v * st->trk.cfg.t_ff, k0, k1;
        uint32_t h = st->trk.i;
        tt_route_at(st->route, s0, &h, 0, 0, 0, &k0);
        tt_route_at(st->route, s0 + v * dt, &h, 0, 0, 0, &k1);
        bdot = (tt_nav_sideslip(l_r, ck, v, v * k1) - tt_nav_sideslip(l_r, ck, v, v * k0)) / dt;
    }
    st->route_s = st->trk.s - st->s_pred;
    {
        float vp = tt_vplan_at(&st->plan, st->route, st->trk.s, &st->plan_hint, 0);
        st->v_plan = vp > RT_V_MIN ? vp : RT_V_MIN;
    }
    return v * k - bdot;
}

/* The speed plan's own acceleration where the command lands: half the slope
 * of v^2 along the route. */
static float plan_accel(OpusState *st)
{
    float dv2 = 0.0f;
    uint32_t h = st->plan_hint;
    (void)tt_vplan_at(&st->plan, st->route, st->trk.s, &h, &dv2);
    return 0.5f * dv2;
}

static void fill_log(OpusState *st, const TtMeas *m)
{
    OpusLog *g = &st->log;
    g->state        = (float)st->phase;
    g->fault        = (float)st->fault;
    g->odo_m        = opus_odo_m(st);
    g->leg_rem_m    = st->leg_rem;
    g->v_meas       = st->v_meas;
    g->target_speed = st->v_ref;
    g->v_err        = st->v_ref - st->v_meas;
    g->yaw_deg      = st->psi * OPUS_RAD2DEG;
    g->yaw_rate     = st->psi_dot;
    g->steer_cmd    = st->cmd.steer_rad;
    g->motor_v      = 0.0f;   /* the drive's business: filled by the target */
    g->i_cmd        = 0.0f;   /* ditto */
    g->brake_cmd    = st->cmd.brake_01;
    g->batt_v       = m->batt.st.valid ? m->batt.v : -1.0f;
    g->slip_pct     = st->slip_pct;
    g->stop_err_mm  = st->stop_err_mm;
    g->v_est        = st->v_est;
    g->v_pred       = st->v_pred;
    g->gyro_bias    = st->gyro_bias;
    g->steer_trim   = st->steer_trim;
    g->odo_scale    = st->odo_scale;
    g->f_gain       = st->f_gain;
    g->mu_est       = st->mu_est;
    g->drive_slip   = st->drive_slip;
    g->launch_slip  = st->launch_slip;
    g->route        = st->route ? (float)st->route->id : 0.0f;
    g->trk_s        = st->trk.s;
    g->trk_ey       = st->trk.ey;
    g->trk_epsi     = st->trk.epsi;
    g->trk_kappa    = st->trk.kappa;
    g->trk_ld       = st->trk.ld;
    g->v_plan       = st->v_plan;
    g->cmd_delay_ms = st->dly_s * 1000.0f;
    g->probe_mu     = st->pr_mu;
    g->stop_early_m = st->stop_early_m;
}

/* -------------------------------------------------------------------- step --*/

void opus_step(OpusState *st, const TtMeas *m, TtCmd *out)
{
    const TtParams *p = st->p;
    float dt = m->dt_s;
    float ds_l, ds_r, ds, psi_dot_enc, psi_dot_ref = 0.0f;
    float v_ref = 0.0f, a_ff = 0.0f, odo_eff, mpc_l, mpc_r, rt_rate = 0.0f;
    int32_t dl, dr;
    int   allow_brake = 1, glitch = 0, w, n_fly;
    OpusSent now;

    memset(out, 0, sizeof(*out));
    out->t_us = m->now_us;

    if (!(dt > 1e-5f) || !finitef_(dt)) { st->cmd = *out; fill_log(st, m); return; }

    /* A clock that went backwards means the run was restarted underneath us
     * without a reset; start over rather than integrating across it. */
    if (st->has_prev_us && (int32_t)(m->now_us - st->prev_us) < -1000)
        opus_reset(st);
    st->prev_us = m->now_us;
    st->has_prev_us = 1;

    /* ---- estimators ---------------------------------------------------- */

    time_delay(st, m);

    mpc_l = tt_m_per_count(p, st->odo_l);
    mpc_r = tt_m_per_count(p, st->odo_r);
    {
        int gl = 0, gr = 0;
        dl = enc_delta(st, m, st->odo_l, dt, &gl);
        dr = enc_delta(st, m, st->odo_r, dt, &gr);
        glitch = gl || gr;
        /* A rejected delta is a MISSING one, not a zero: a zero beside the
         * other wheel's real travel reads as a sharp yaw (a 4.5 cm step
         * turned the heading 3.6 deg, VAL-11). Take the other wheel's travel
         * for it; the heading below then leans on the gyro for this tick. */
        if (gl && !gr && mpc_l > 0.0f) dl = (int32_t)lroundf((float)dr * mpc_r / mpc_l);
        else if (gr && !gl && mpc_r > 0.0f) dr = (int32_t)lroundf((float)dl * mpc_l / mpc_r);
    }
    if (glitch) st->fault |= FA_TICK_GLITCH;
    ds_l = (float)dl * mpc_l;
    ds_r = (float)dr * mpc_r;
    n_fly = delay_ticks(st, dt);
    now = acting(st, n_fly);

    /* A DRIVEN odometry wheel slips by its own tyre force: kappa = F / (C_k/F_z
     * * F_z), so the encoder reads (1 + kappa) of the ground. Undo it with the
     * torque acting on it (sent cmd_delay_s ago) and the wheel's load from the
     * weight split plus longitudinal and lateral transfer. The tyre carries
     * the torque LESS what the wheel itself loses (rolling resistance,
     * bearings, motor friction — the c0 + c1*v of the drag model, shared by
     * four wheels); only aero is the body's. At cruise that leaves almost
     * nothing, which is why this matters under braking and launch. (A
     * friction-braked wheel is the separate cal_brake term below.) */
    if (p->slip_stiffness > 0.0f) {
        const float g = 9.80665f;
        float tr = (st->odo_l < 2) ? p->track_front_m : p->track_rear_m;
        float shf = p->front_weight_frac - (p->wheelbase_m > 0.0f ? p->cg_height_m / p->wheelbase_m : 0.0f) * now.a / g;
        float sh = (st->odo_l < 2) ? shf : 1.0f - shf;
        float ay = st->v_meas * st->psi_dot_f;                 /* + = left */
        float dz = tr > 0.0f ? ay * p->cg_height_m / tr : 0.0f; /* per g-scaled mass */
        float fz_l = p->mass_kg * sh * (0.5f * g - dz);
        float fz_r = p->mass_kg * sh * (0.5f * g + dz);
        float va = st->v_meas < 0.0f ? -st->v_meas : st->v_meas;
        float own = (p->drag_c0 + p->drag_c1 * va) / (float)TT_MAX_WHEELS;
        float k_l = 0.0f, k_r = 0.0f, c_l, c_r;
        if (st->v_meas < 0.0f) own = -own;
        if ((p->driven_mask & (1u << st->odo_l)) && fz_l > 0.1f && va > 0.05f)
            k_l = (now.t_odo[0] / p->wheel_radius_m - own) / (p->slip_stiffness * fz_l);
        if ((p->driven_mask & (1u << st->odo_r)) && fz_r > 0.1f && va > 0.05f)
            k_r = (now.t_odo[1] / p->wheel_radius_m - own) / (p->slip_stiffness * fz_r);
        k_l = clampf(k_l, -0.3f, 0.3f);
        k_r = clampf(k_r, -0.3f, 0.3f);
        c_l = -ds_l * k_l / (1.0f + k_l);
        c_r = -ds_r * k_r / (1.0f + k_r);
        st->kappa_odo = 0.5f * (k_l + k_r);
        ds_l += c_l;
        ds_r += c_r;
        st->odo_corr_m += 0.5f * (c_l + c_r);
    }

    /* Averaging the two odometry wheels cancels the track-width term exactly,
     * so no steering-angle compensation is needed on the measured legs. */
    ds = 0.5f * (ds_l + ds_r);
    st->odo_cnt_l += dl;
    st->odo_cnt_r += dr;
    /* Brake slip is MULTIPLICATIVE on the rolled distance (a braked wheel runs
     * at negative slip proportional to road speed), never additive — an
     * additive term manufactures phantom metres while the car sits at rest
     * with the brake held, which is exactly the ARM rolling check's job to
     * catch (it did — fault 0x40, run R3). */
    {
        float k = p->cal_scale + p->cal_brake * st->cmd.brake_01;
        st->odo_corr_m += ds * k;
        ds *= 1.0f + k;
    }

    st->v_meas = ds / dt;
    /* While the friction probe slips the odometry pair on purpose (FW-18),
     * the car's speed is all four driven wheels': the front pair brakes and
     * the rear drives about as hard, so their slips nearly cancel. (Not the
     * accelerometer's: its bias, a tenth of a m/s^2 at cruise, is held off
     * only by the odometer it would be standing in for.) */
    if (st->pr_state == 1 || st->pr_hold_t > 0.0f) {
        float v4;
        if (probe_speed(st, m, &v4)) {
            float fix = (v4 - st->v_meas) * dt;
            st->odo_corr_m += fix;
            ds += fix;
            st->v_meas = v4;
        }
    }
    st->v_filt += (st->v_meas - st->v_filt) * lp_alpha(dt, EN_VFILT_TAU_S);

    /* Car speed (FW-13): the accelerometer's integral, pulled toward the
     * odometer, and only slackly while the two disagree by more than tyre
     * slip, when it is the wheels that are wrong (mission_cfg.h). That is a
     * cure for DRIVEN odometry wheels; undriven ones roll with the ground
     * (their brake slip is cal_brake's), and the odometer stays the whole
     * story — as it is outside the driven phases, at rest or nearly. */
    {
        float ax = m->imu.accel[0];
        int ok = m->imu.st.valid && finitef_(ax) && finitef_(m->imu.gyro[1]);
        int slip = 0;
        uint32_t odo_pair = (1u << st->odo_l) | (1u << st->odo_r);
        st->ax_corr = ok ? ax : 0.0f;   /* pitch removed below, where tracked */
        if (!(p->driven_mask & odo_pair) || !driving(st->phase) || !ok) {
            st->v_est = st->v_meas;
            st->pitch = 0.0f;
        } else {
            float e, band;
            /* FLU: +y is left, so a positive pitch rate is nose down, and a
             * nose-down car's accelerometer reads g*sin(pitch) of braking. */
            st->pitch += (m->imu.gyro[1] - st->pitch / EN_PITCH_TAU_S) * dt;
            ax += 9.80665f * st->pitch;
            st->ax_corr = ax;
            st->v_est += 0.5f * (ax + st->ax_prev) * dt;
            e = st->v_meas - st->v_est;
            band = EN_VOBS_BAND_MPS + EN_VOBS_BAND_FRAC * fabsf(st->v_est);
            slip = e > band || e < -band;
            st->v_est += e * lp_alpha(dt, slip ? EN_VOBS_SLIP_TAU_S : EN_VOBS_TAU_S);
        }
        st->ax_prev = ok ? ax : 0.0f;
        st->ax_load += (st->ax_prev - st->ax_load) * lp_alpha(dt, EN_LOAD_TAU_S);
        if (st->pr_hold_t > 0.0f) st->pr_hold_t -= dt;
        /* While the odometry pair slips past what the slip model explains,
         * the odometer counts the car's speed, not theirs: a wheel cycling
         * in and out of lock under a late anti-lock reads metres short. */
        st->odo_slip = slip;
        if (slip) {
            float fix = (st->v_est - st->v_meas) * dt;
            st->odo_corr_m += fix;
            ds += fix;
            st->v_meas = st->v_est;
        }
    }
    predict(st, n_fly, dt);

    /* Force gain and grip (FW-16), on the launch: from the force acting on
     * the car (the command a delay ago, as the allocator shaped it) and the
     * acceleration it is producing. */
    if (st->phase == OPUS_LAUNCH && m->imu.st.valid && finitef_(st->ax_corr) && st->v_est > 0.5f) {
        OpusSent prev = acting(st, n_fly + 1);
        float fa = now.f < 0.0f ? -now.f : now.f;
        float df = now.f - prev.f;
        /* Least squares on steady ticks with no wheel near its limit (and
         * no friction brake, whose force is not in now.f). */
        if (now.lim == 0 && fa > GA_FG_MIN_N && !(st->cmd.brake_01 > 0.0f) &&
            (df < 0.0f ? -df : df) < GA_FG_STEP_FRAC * fa) {
            float got = p->mass_eff_kg * st->ax_corr + drag_n(p, st->v_est);
            st->fg_num += got * now.f * dt;
            st->fg_den += now.f * now.f * dt;
            if (st->fg_den > GA_FG_MIN_W) {
                float g = st->fg_num / st->fg_den;
                st->f_gain = (g >= GA_FG_MIN && g <= GA_FG_MAX) ? g : 1.0f;
            }
        }
        /* The launch's slip, wheel by wheel, the way the torque pushed,
         * while it pushes (not the settle at cruise that ends the phase). */
        if (st->v_est > GA_MU_MIN_V && now.a > 0.5f * LI_A_LAUNCH && p->slip_max > 0.0f) {
            for (w = 0; w < TT_MAX_WHEELS; w++)
                st->mu_ls[w] += st->cmd.wheel_torque_nm[w] >= 0.0f ? st->alloc.slip[w] : -st->alloc.slip[w];
            st->mu_ax += st->ax_corr < 0.0f ? -st->ax_corr : st->ax_corr;
            st->mu_ln++;
        }
    }
    /* Each wheel's slip at cruise is its radius error; then judge the floor
     * once, on the launch's slip less it (FW-16). */
    if ((st->phase == OPUS_CRUISE_A || st->phase == OPUS_FOLLOW) &&
        !st->mu_judged && st->phase_t > GA_MU_CAL_T0) {
        if (st->phase_t < GA_MU_CAL_T1) {
            for (w = 0; w < TT_MAX_WHEELS; w++) st->mu_cs[w] += st->alloc.slip[w];
            st->mu_cn++;
        } else {
            float s = 0.0f;
            int n = 0;
            st->mu_judged = 1;
            if (st->mu_ln > 0 && st->mu_cn > 0) {
                for (w = 0; w < TT_MAX_WHEELS; w++) {
                    if (!(p->driven_mask & (1u << w))) continue;
                    s += st->mu_ls[w] / (float)st->mu_ln - st->mu_cs[w] / (float)st->mu_cn;
                    n++;
                }
            }
            if (n > 0) st->launch_slip = s / (float)n;
            if (n > 0 && st->launch_slip > GA_MU_SLIP_FRAC * p->slip_max && p->mass_kg > 0.0f) {
                float mu = p->mass_eff_kg * (st->mu_ax / (float)st->mu_ln) /
                           (p->mass_kg * 9.80665f) / GA_MU_SAT;
                if (mu < GA_MU_MIN) mu = GA_MU_MIN;
                /* A car that probes the floor on leg A (FW-18) keeps this
                 * for if the probe cannot judge. */
                if (probe_can(st)) st->mu_launch = mu;
                else if (mu < st->mu_est) st->mu_est = mu;
            }
        }
    }
    /* What the stop can ask of this floor. Below LI_A_BRAKE the 1.5 m stop
     * is out of reach: say so now, and start it early enough to stop on the
     * mark anyway. */
    if (st->mu_est < st->mu_nom && p->mass_eff_kg > 0.0f) {
        float ab = GA_MU_USE * st->mu_est * p->mass_kg * 9.80665f / p->mass_eff_kg;
        st->a_brake = ab < st->a_brake_nom ? ab : st->a_brake_nom;
        if (st->a_brake < st->a_brake_nom - 1e-3f) st->fault |= FA_GRIP_LOW;
    }
    /* A route's speeds come from its grip budget: replan on the floor
     * measured (FW-16), lateral and braking limits scaled alike. */
    if (st->route && fabsf(st->mu_est - st->plan_mu) > RT_MU_REPLAN * st->mu_nom) {
        float sc = st->mu_est / st->mu_nom;
        tt_vplan_build(&st->plan, st->route, st->route_vmax, RT_LAT_USE * st->route->a_lat_max * sc,
                       0.0f, st->route->a_brake * sc, -1.0f);
        st->plan_mu = st->mu_est;
    }

    /* Driven-wheel speed, for the slip diagnostic only. */
    {
        float sum = 0.0f;
        int   n = 0;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            /* An odometry wheel was already differenced this tick; doing it
             * again would read zero. */
            if (w == st->odo_l || w == st->odo_r) continue;
            if ((p->driven_mask & (1u << w)) && m->enc[w].st.valid) {
                int g2 = 0;
                sum += (float)enc_delta(st, m, w, dt, &g2) * tt_m_per_count(p, w);
                n++;
            }
        }
        if (n > 0) {
            st->v_rear = sum / (float)n / dt;
            st->slip_pct = (st->v_meas > 0.3f)
                ? (st->v_rear - st->v_meas) / st->v_meas * 100.0f : 0.0f;
        }
    }

    /* Heading (FW-15). The gyro, less the bias it read at rest while arming;
     * in FLU a left turn is positive, so there is no sign to learn. The
     * differential of the two odometry wheels is a yaw measurement too, but
     * only as good as their sizes match: 1 % reads as 15 deg/s at cruise. So
     * it is the fallback while the IMU is out, corrected by the left/right
     * scale learned against the gyro on the straights. */
    {
        int gz_ok = m->imu.st.valid && finitef_(m->imu.gyro[2]);
        float gz = m->imu.gyro[2] - st->gyro_bias;
        float va = st->v_meas < 0.0f ? -st->v_meas : st->v_meas;
        psi_dot_enc = (ds_r - ds_l) / (p->track_front_m * dt);
        if (gz_ok && !glitch && !st->odo_slip && st->pr_state != 1 && va > GA_LR_MIN_V && straight(st)) {
            float e = (psi_dot_enc - gz) * p->track_front_m / st->v_meas;
            st->lr_eps = clampf(st->lr_eps + (e - st->lr_eps) * lp_alpha(dt, GA_LR_TAU_S),
                                -GA_LR_MAX, GA_LR_MAX);
        }
        psi_dot_enc -= st->lr_eps * st->v_meas / p->track_front_m;
        st->psi_dot = gz_ok ? gz : psi_dot_enc;
    }
    st->psi += st->psi_dot * dt;
    /* Close the loops on a lightly filtered rate (the differential, when it
     * is the source, is tick-quantised at 0.03 rad/s). */
    st->psi_dot_f += (st->psi_dot - st->psi_dot_f) * lp_alpha(dt, GA_YAW_TAU_S);

    /* Steering trim (FW-15). On a straight, the steering acting on the car
     * should give the kinematic yaw rate; the angle that would give the rate
     * measured, less the angle acting, is how far off centre it sits. Held
     * outside the straights: in the turn the tyres' slip angles count. */
    {
        float lag = p->steer_lag_s > 0.0f ? p->steer_lag_s : GA_TRIM_LAG_S;
        float ay = st->v_est * st->psi_dot_f;
        st->steer_act_f += (now.steer - st->steer_act_f) * lp_alpha(dt, lag);
        if (straight(st) && st->v_est > GA_TRIM_MIN_V && ay < GA_TRIM_MAX_AY && ay > -GA_TRIM_MAX_AY) {
            /* The plant's offset, measured directly: independent of the trim
             * already applied (it is inside the command acting). */
            float off = atan2f(p->wheelbase_m * st->psi_dot_f, st->v_est) - st->steer_act_f;
            float e = off - st->steer_trim;
            e = e > GA_TRIM_DEADZONE ? e - GA_TRIM_DEADZONE
              : (e < -GA_TRIM_DEADZONE ? e + GA_TRIM_DEADZONE : 0.0f);
            st->steer_trim = clampf(st->steer_trim + e * lp_alpha(dt, GA_TRIM_TAU_S),
                                    -GA_TRIM_MAX_RAD, GA_TRIM_MAX_RAD);
        }
    }

    odo_eff = odo_effective(st);

    /* Wheel speeds for the slip limiter, from the drives' own speed
     * estimate (an FOC driver's PLL; the sim adapter's encoder rate). */
    for (w = 0; w < TT_MAX_WHEELS; w++) {
        float g = p->gear[w];
        st->wheel_omega_ok[w] = (uint8_t)(m->drv[w].st.valid && g > 0.0f);
        st->wheel_omega[w] = st->wheel_omega_ok[w] ? m->drv[w].omega_m / g : 0.0f;
    }
    if (st->pr_state == 1) probe_sample(st, m);

    /* ---- standing faults ----------------------------------------------- */

    if (m->imu.st.valid) {
        const float *a = m->imu.accel;
        float mag = sqrtf(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (mag > SF_ACCEL_ABORT) {
            st->accel_t += dt;
            if (st->accel_t >= SF_ACCEL_S - 1e-4f) st->fault |= FA_IMPACT;
        } else {
            st->accel_t = 0.0f;
        }
    }
    {
        float tof = (m->tof.st.valid && m->tof.zones > 0 && m->tof.status[0] == 0)
                  ? m->tof.range_m[0] : 1e6f;
        if (((st->phase >= OPUS_LAUNCH && st->phase <= OPUS_BRAKE) || st->phase == OPUS_FOLLOW) &&
            tof > 0.0f && tof < SF_TOF_ABORT_M) {
            st->tof_t += dt;
            if (st->tof_t >= SF_TOF_S - 1e-4f) st->fault |= FA_OBSTACLE;
        } else {
            st->tof_t = 0.0f;
        }
    }

    if (st->fault & (FA_IMPACT | FA_OBSTACLE | FA_ENC_DEAD | FA_NO_ENCODERS |
                     FA_NO_MOTORS | FA_NAN | FA_PARAMS | FA_SAFETY | FA_NAV))
        st->phase = OPUS_FAULT;

    st->phase_t += dt;

    /* On a route the tracker steers whatever the phase (L4). */
    if (st->route && st->pose_ok && driving(st->phase))
        rt_rate = route_rate(st, ds, dt);

    /* ---- phase machine -------------------------------------------------- */

    switch (st->phase) {

    case OPUS_BOOT:
        enter(st, OPUS_ARM_STATIC);
        break;

    case OPUS_ARM_STATIC:
        /* Brake held first so the car is definitely still, then released so a
         * silent roll-away or a sloped pad shows up as counter movement. */
        out->brake_01 = (st->phase_t < SQ_ARM_BRAKE_S) ? 1.0f : 0.0f;
        if (st->phase_t > SQ_ARM_BRAKE_S) {
            if (ds > 0.002f || ds < -0.002f) st->fault |= FA_ROLLING;
        }
        /* Standing still: whatever the gyro reads is its bias (FW-15). */
        if (st->phase_t > GA_GB_SETTLE_S && m->imu.st.valid && finitef_(m->imu.gyro[2])) {
            st->gb_sum += m->imu.gyro[2];
            st->gb_n++;
        }
        if (st->phase_t > SQ_ARM_BRAKE_S + SQ_ARM_ROLL_S) {
            arm_checks(st, m);
            if (st->fault) { st->phase = OPUS_FAULT; break; }
            if (st->gb_n > 0) st->gyro_bias = st->gb_sum / (float)st->gb_n;
            /* Zero the estimators so the mission's datum is the arm point. */
            st->odo_cnt_l = 0; st->odo_cnt_r = 0; st->odo_corr_m = 0.0f;
            st->psi = 0.0f; st->psi_ref = 0.0f;
            enter(st, OPUS_ARMED);
        }
        break;

    case OPUS_ARMED:
        out->brake_01 = 1.0f;
        /* A route is flown on the filter's pose: none, no launch. */
        if (st->route && !st->pose_ok) {
            if (st->phase_t > RT_POSE_WAIT_S) st->fault |= FA_NAV;
            break;
        }
        /* Launch only once the safety layer has armed the drives. */
        if (st->phase_t > SQ_ARM_DWELL_S && st->drive_ok) {
            st->v_ref = 0.0f;
            begin_leg(st);
            enter(st, OPUS_LAUNCH);
        }
        break;

    case OPUS_LAUNCH:
        /* Ramp the reference rather than the command: the speed loop then has a
         * feasible target at every instant instead of a step it must saturate
         * against. */
        if (st->route) {
            /* Up to the speed plan, at the route's own acceleration; the
             * tracker steers from the first metre. */
            float a = st->plan.a_drive;
            float vt = st->v_plan;
            v_ref = st->v_ref + a * dt;
            if (v_ref > vt) v_ref = vt;
            a_ff = (v_ref < vt) ? a : 0.0f;
            psi_dot_ref = rt_rate;
        } else {
            v_ref = st->v_ref + LI_A_LAUNCH * dt;
            if (v_ref > MI_V_CRUISE) v_ref = MI_V_CRUISE;
            a_ff = (v_ref < MI_V_CRUISE) ? LI_A_LAUNCH : 0.0f;
            psi_dot_ref = heading_hold(st);
        }

        /* The only real encoder-liveness test: both counters must advance once
         * the car is definitely moving. Judged on ACCUMULATED distance over the
         * whole window, not per tick — a per-tick comparison fails on the first
         * bit of steering correction, since at a 0.172 m track a 1 deg/s yaw
         * already separates the two wheels by more than a tenth of their travel.
         * The band is deliberately loose: this detects a dead or unplugged
         * counter, it is not a calibration check. */
        st->live_l += ds_l;
        st->live_r += ds_r;
        if (!st->live_checked && odo_eff > SQ_LIVENESS_M) {
            float lo = st->live_l < st->live_r ? st->live_l : st->live_r;
            float hi = st->live_l < st->live_r ? st->live_r : st->live_l;
            st->live_checked = 1;
            if (lo <= 0.01f || lo < 0.60f * hi) st->fault |= FA_ENC_DEAD;
        }

        if (st->v_meas > (st->route ? st->v_plan : MI_V_CRUISE) - 0.05f) st->settle_t += dt;
        else st->settle_t = 0.0f;
        if (st->settle_t > SQ_SETTLE_S) {
            begin_leg(st);
            enter(st, st->route ? OPUS_FOLLOW : OPUS_CRUISE_A);
        }
        break;

    case OPUS_FOLLOW: {
        /* The speed plan where this command lands, and its own acceleration
         * as the feed-forward; the tracker steers. */
        const float s_end = st->route->pt[st->route->n - 1u].s;
        v_ref = st->v_plan;
        a_ff = clampf(plan_accel(st), -st->a_brake, st->plan.a_drive);
        psi_dot_ref = rt_rate;
        /* The stop: as after leg B, it starts where it can still be made at
         * a_brake, and the distance left on the route becomes an odometer
         * target, so BRAKE and CREEP fly it on the encoders. */
        if (st->route->stop_at_end &&
            s_end - st->trk.s - st->v_pred * EN_DEAD_TIME_S - 0.5f * st->v_meas * dt
                <= st->v_pred * st->v_pred / (2.0f * st->a_brake)) {
            st->stop_target_m = odo_eff + (s_end - st->route_s) / st->odo_scale;
            enter(st, OPUS_BRAKE);
        }
        break;
    }

    case OPUS_CRUISE_A:
        v_ref = MI_V_CRUISE;
        psi_dot_ref = heading_hold(st);
        probe_run(st);
        /* Half-tick lead. A leg boundary can only ever land on a control tick,
         * and at 4.5 m/s a tick is 45 mm — so testing the bare threshold always
         * overshoots, by 22 mm on average. Testing the midpoint of the coming
         * tick instead centres the quantisation on zero. */
        if (leg_m(st, odo_eff + 0.5f * st->v_meas * dt, st->leg_start_m) >= MI_LEG_A_M) {
            st->leg_a_actual = leg_m(st, odo_eff, st->leg_start_m);
            st->turn_t = 0.0f;
            st->turn_cmd_rad = 0.0f;
            enter(st, OPUS_TURN);
        }
        break;

    case OPUS_TURN: {
        float total = turn_profile(st->turn_t, &psi_dot_ref);
        v_ref = MI_V_CRUISE;                 /* the brief is explicit: do not slow down */
        st->turn_t += dt;
        st->turn_cmd_rad += psi_dot_ref * dt;
        /* Close the heading loop around the profile. Without this the turn is
         * open-loop in heading and keeps whatever the yaw-rate loop failed to
         * deliver — measured as 3.6 deg short on the first full run. The
         * feed-forward still does the work; this only repays the shortfall, and
         * because it is driven by turn_cmd_rad (the INTEGRAL of the commanded
         * profile, not a step to the final angle) it never asks for more rate
         * than the lateral-acceleration budget allows. */
        {
            float herr = st->turn_cmd_rad - psi_pred(st);
            psi_dot_ref += clampf(GA_TURN_KP * herr, -GA_TURN_MAX_TRIM, GA_TURN_MAX_TRIM);
        }
        /* The settle test needs the heading to have ARRIVED as well as the rate
         * to have died — a loose rate band on its own is satisfied by simply
         * stopping the turn early. The timeout is the honest escape hatch: exit
         * anyway and let the reported turn_actual_deg carry the bad news. */
        /* Once the rate has died, a heading a degree or so short only
         * creeps the rest of the way (corrections that small vanish into
         * the servo's backlash), and every second spent waiting moves the
         * rest of the run 4.5 m: after GA_TURN_SETTLE_S take a looser mark. */
        if ((st->turn_t >= total + 0.15f &&
             st->psi_dot_f < 0.05f && st->psi_dot_f > -0.05f &&
             (fabsf(st->turn_cmd_rad - psi_pred(st)) < GA_TURN_CLOSE_RAD ||
              (st->turn_t >= total + GA_TURN_SETTLE_S &&
               fabsf(st->turn_cmd_rad - psi_pred(st)) < GA_TURN_LOOSE_RAD))) ||
            st->turn_t >= total + GA_TURN_MAX_EXTRA) {
            /* Straightened out. This instant DEFINES the datum for the next
             * leg, so the 7.5 m is exact by construction; the residual heading
             * error is reported, not propagated. */
            st->turn_actual_deg = st->psi * OPUS_RAD2DEG;
            st->psi_ref = psi_pred(st);   /* where it will settle */
            begin_leg(st);
            st->stop_target_m = st->leg_start_m + MI_STOP_FROM_EXIT / st->odo_scale;
            enter(st, OPUS_CRUISE_B);
        }
        break;
    }

    case OPUS_CRUISE_B: {
        /* The mark-started stop is laid out for the parameter's delay: the
         * brake lands that far past the mark, and the budget's deceleration
         * covers the rest of MI_BRAKE_M with little to spare (the 30 ms car
         * stops 70 mm long). A longer delay, measured at the launch (FW-17),
         * lands it further past; there is no decelerating harder, so the
         * brake starts that much before the mark instead (FW-19). Leg B
         * then reads short by it and the stop long, the total as before. */
        float extra = st->dly_s - p->cmd_delay_s;
        /* The launch's measurement is whole ticks, rounded down (26 ms
         * reads 20): a delay found longer than the parameter's is somewhere
         * in the tick above what it reads, and the stop is planned on the
         * top of it. Starting early costs the total nothing (the profile
         * is distance-based and takes up the room); starting late does. */
        if (extra > 0.0f && st->dly_state == 2) extra += dt;
        st->stop_early_m = extra > 0.0f ? st->v_pred * extra : 0.0f;
        v_ref = MI_V_CRUISE;
        psi_dot_ref = heading_hold(st);
        /* On a slippery floor (FW-16) the stop starts where it can still
         * be made at a_brake, the same lead the brake profile itself uses. */
        if (leg_m(st, odo_eff + 0.5f * st->v_meas * dt, st->leg_start_m) >= MI_LEG_B_M - st->stop_early_m ||
            (st->a_brake < LI_A_BRAKE &&
             st->stop_target_m - odo_eff - st->s_pred - st->v_pred * EN_DEAD_TIME_S - 0.5f * st->v_meas * dt
                 <= st->v_pred * st->v_pred / (2.0f * st->a_brake))) {
            /* The lead the firing rule asked for, logged for the scorer: leg
             * B is then judged against the mark the firmware meant, and the
             * tick's own quantisation stays visible in it. */
            if (st->a_brake < LI_A_BRAKE) {
                float need = st->s_pred + st->v_pred * EN_DEAD_TIME_S +
                             st->v_pred * st->v_pred / (2.0f * st->a_brake);
                float lead = MI_LEG_B_M - (leg_m(st, st->stop_target_m, st->leg_start_m) - need);
                if (lead > st->stop_early_m) st->stop_early_m = lead;
            }
            if (st->stop_early_m < 0.0f) st->stop_early_m = 0.0f;
            st->leg_b_actual = leg_m(st, odo_eff, st->leg_start_m);
            enter(st, OPUS_BRAKE);
        }
        break;
    }

    case OPUS_BRAKE: {
        /* Parameterise the speed profile by REMAINING DISTANCE, not by time:
         * that makes it self-correcting against modelling error. It is the
         * distance left where this command lands (s_pred, FW-13), less a lead
         * for the loop's own dead time: 20 ms at 4.5 m/s is 90 mm. */
        float s_rem = (st->stop_target_m - odo_eff - st->s_pred) - st->v_pred * EN_DEAD_TIME_S;
        if (s_rem < 0.0f) s_rem = 0.0f;
        v_ref = sqrtf(2.0f * st->a_brake * s_rem);
        if (v_ref > (st->route ? st->v_plan : MI_V_CRUISE)) v_ref = st->route ? st->v_plan : MI_V_CRUISE;
        a_ff = -st->a_brake;
        psi_dot_ref = st->route ? rt_rate : heading_hold(st);

        /* The friction brake acts on ALL FOUR wheels, so it slips the very
         * wheels the odometer reads. Give it up for the last 40 mm and coast
         * the rest on motor torque alone, where the front slip falls to
         * essentially nothing. */
        /* Speed gate as well as distance. CREEP releases the friction brake and
         * closes on position with a 0.3 m/s ceiling, which is right for the last
         * 40 mm and catastrophic at 3 m/s — the first completed run ran out of
         * distance while still doing 2.9 m/s, handed over to CREEP, and coasted
         * 1.3 m past the mark. If the distance is gone but the speed is not,
         * stay in BRAKE: s_rem clamps to zero, v_ref goes to zero, and the loop
         * asks for everything it has. */
        if (st->stop_target_m - odo_eff < EN_CREEP_M && st->v_filt < EN_CREEP_V * 2.0f) {
            st->brake_actual = leg_m(st, odo_eff, st->stop_target_m) + (st->route ? 0.0f : MI_BRAKE_M);
            enter(st, OPUS_CREEP);
        }
        break;
    }

    case OPUS_CREEP: {
        /* Below 40 mm the sqrt profile's gain runs away (dv/ds = -a/v), so hand
         * over to a linear position loop. Position resolves to 0.05 mm here;
         * velocity resolves to only 5 mm/s, which is why this closes on
         * distance and not on speed. */
        float s_rem = st->stop_target_m - odo_eff;
        allow_brake = 0;
        v_ref = clampf(EN_CREEP_KV * (s_rem - st->s_pred), -EN_CREEP_V, EN_CREEP_V);
        if (st->route) psi_dot_ref = rt_rate;
        if (st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V && s_rem < EN_STALL_M) st->stall_t += dt;
        else st->stall_t = 0.0f;
        if ((s_rem < EN_DONE_M || st->stall_t > EN_STALL_S) &&
            st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) {
            st->hold_t = 0.0f;
            enter(st, OPUS_HOLD);
        }
        break;
    }

    case OPUS_HOLD:
        out->brake_01 = 1.0f;
        /* Test the filtered speed, not the raw tick delta: at a few mm/s the
         * counter alternates between one and two ticks per period, so an
         * exact-zero-ticks test can never be satisfied for a whole second. */
        if (st->v_filt < EN_DONE_V && st->v_filt > -EN_DONE_V) st->hold_t += dt;
        else st->hold_t = 0.0f;
        if (st->hold_t > EN_HOLD_S) {
            st->stop_err_mm = leg_m(st, odo_eff, st->stop_target_m) * 1000.0f;
            enter(st, OPUS_DONE);
        }
        break;

    case OPUS_DONE:
        out->brake_01 = 1.0f;
        break;

    case OPUS_FAULT:
    default:
        out->brake_01 = 1.0f;
        break;
    }

    /* Distance still owed on whichever leg is being measured. */
    switch (st->phase) {
    case OPUS_CRUISE_A: st->leg_rem = MI_LEG_A_M - leg_m(st, odo_eff, st->leg_start_m); break;
    case OPUS_CRUISE_B: st->leg_rem = MI_LEG_B_M - leg_m(st, odo_eff, st->leg_start_m); break;
    case OPUS_BRAKE:
    case OPUS_CREEP:    st->leg_rem = -leg_m(st, odo_eff, st->stop_target_m); break;
    case OPUS_FOLLOW:   st->leg_rem = st->route->pt[st->route->n - 1u].s - st->route_s; break;
    default:            st->leg_rem = 0.0f; break;
    }

    /* ---- actuate --------------------------------------------------------- */

    if (driving(st->phase)) {
        longitudinal(st, m, v_ref, a_ff, allow_brake, out);
        lateral(st, m, psi_dot_ref, out);
        out->arm = 1;
    } else {
        st->v_ref = 0.0f;
        st->t_cmd_nm = 0.0f;
        st->a_cmd = 0.0f;
        st->r_cmd = 0.0f;
        pid_reset(&st->spd_pid);
        pid_reset(&st->yaw_pid);
        /* The horn goes to centre; the wheels stay within the play of it. */
        st->horn = 0.0f;
        st->wheel_est = clampf(st->wheel_est, -0.5f * p->steer_backlash_rad,
                               0.5f * p->steer_backlash_rad);
        out->arm = (st->phase == OPUS_ARMED || st->phase == OPUS_HOLD) ? 1 : 0;
    }

    /* Live stop error while it still means something. */
    if (st->phase == OPUS_BRAKE || st->phase == OPUS_CREEP)
        st->stop_err_mm = leg_m(st, odo_eff, st->stop_target_m) * 1000.0f;

    out->seq = st->cmd.seq + 1u;
    /* FW-17: the launch's first real command is the step the delay is
     * timed on, if the drives report current and were idle before it. */
    if (st->dly_state == 0 && st->phase == OPUS_LAUNCH && st->drive_iq) {
        float f = 0.0f, f_idle;
        for (w = 0; w < TT_MAX_WHEELS; w++) f += out->wheel_torque_nm[w];
        f = p->wheel_radius_m > 0.0f ? f / p->wheel_radius_m : 0.0f;
        if (f > GA_DLY_MIN_N) {
            if (drive_force(st, m, &f_idle) && f_idle < GA_DLY_PRE_FRAC * f) {
                st->dly_state = 1;
                st->dly_k0 = out->seq;
                st->dly_f0 = f;
            } else {
                st->dly_state = 3;
            }
        }
    }
    st->cmd = *out;
    remember(st, out);
    fill_log(st, m);
}
