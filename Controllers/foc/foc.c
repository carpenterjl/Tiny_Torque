/*
 * foc.c — field-oriented current control (FW-10). See foc.h for the
 * conventions and the PWM timing model the gains assume.
 */
#include "foc.h"

#include <math.h>

static float clampf(float v, float lo, float hi)
{
    if (v < lo) return lo;
    if (v > hi) return hi;
    return v;
}

float foc_wrap_pi(float x)
{
    return x - FOC_2PI_F * floorf((x + FOC_PI_F) * (1.0f / FOC_2PI_F));
}

/* ---- building blocks ------------------------------------------------------ */

void foc_pi_gains(float r_ohm, float l_h, float bw_hz, float *kp, float *ki)
{
    float w = FOC_2PI_F * bw_hz;
    *kp = l_h * w;
    *ki = r_ohm * w;
}

int foc_limit_vdq(float *vd, float *vq, float v_max)
{
    float d = clampf(*vd, -v_max, v_max);
    float q_room = sqrtf(fmaxf(v_max * v_max - d * d, 0.0f));
    float q = clampf(*vq, -q_room, q_room);
    int cut = (d != *vd) || (q != *vq);
    *vd = d;
    *vq = q;
    return cut;
}

void foc_svpwm(float v_alpha, float v_beta, float vbus, float duty[3])
{
    float va, vb, vc, vmax, vmin, v0, inv;
    if (!(vbus > 0.0f)) {                 /* also catches NaN */
        duty[0] = duty[1] = duty[2] = 0.5f;
        return;
    }
    foc_inv_clarke(v_alpha, v_beta, &va, &vb, &vc);
    vmax = fmaxf(va, fmaxf(vb, vc));
    vmin = fminf(va, fminf(vb, vc));
    /* Centre the three phases in the bus: the zero-sequence shift that makes
     * sine PWM reach Vbus/sqrt3 instead of Vbus/2 (same result as sector
     * SVPWM with equal zero vectors). */
    v0  = -0.5f * (vmax + vmin);
    inv = 1.0f / vbus;
    duty[0] = clampf(0.5f + (va + v0) * inv, 0.0f, 1.0f);
    duty[1] = clampf(0.5f + (vb + v0) * inv, 0.0f, 1.0f);
    duty[2] = clampf(0.5f + (vc + v0) * inv, 0.0f, 1.0f);
}

void foc_pll_init(FocPll *pll, float bw_hz, float ts)
{
    /* Critically damped (zeta = 1): no overshoot on a speed step. */
    float wn = FOC_2PI_F * bw_hz;
    pll->kp = 2.0f * wn;
    pll->ki = wn * wn;
    pll->ts = ts;
    foc_pll_reset(pll, 0.0f);
}

void foc_pll_reset(FocPll *pll, float theta)
{
    pll->theta = foc_wrap_pi(theta);
    pll->omega = 0.0f;
}

void foc_pll_step(FocPll *pll, float theta_meas)
{
    /* The wrapped difference, not sin(): exact for any error below pi, so the
     * loop stays linear when it has to catch up after a speed step. */
    float err = foc_wrap_pi(theta_meas - pll->theta);
    pll->omega += pll->ki * pll->ts * err;
    pll->theta  = foc_wrap_pi(pll->theta + (pll->omega + pll->kp * err) * pll->ts);
}

float foc_mech_to_elec(float theta_m, uint32_t pole_pairs, float offset_e)
{
    /* Wrap first: p * (a large unwrapped angle) loses float resolution. */
    return foc_wrap_pi((float)pole_pairs * foc_wrap_pi(theta_m) + offset_e);
}

float foc_align_offset(float theta_m_at_align, uint32_t pole_pairs, float theta_e_forced)
{
    return foc_wrap_pi(theta_e_forced - (float)pole_pairs * foc_wrap_pi(theta_m_at_align));
}

/* ---- the controller ------------------------------------------------------- */

void foc_reset(FocState *s)
{
    s->pi_d.integ = 0.0f;
    s->pi_q.integ = 0.0f;
    foc_pll_reset(&s->pll, 0.0f);
    s->pll_seeded = 0u;
}

int foc_init(FocState *s, const FocParams *p)
{
    float kp, ki;
    if (!(p->r_ohm > 0.0f) || !(p->ld_h > 0.0f) || !(p->lq_h > 0.0f) ||
        !(p->f_pwm_hz > 0.0f) || !(p->bw_current_hz > 0.0f) ||
        !(p->bw_pll_hz > 0.0f) || p->pole_pairs == 0u ||
        !(p->mod_max > 0.0f) || !(p->mod_max <= 1.0f) || !(p->i_max_a > 0.0f))
        return -1;

    s->p  = *p;
    s->ts = 1.0f / p->f_pwm_hz;
    foc_pi_gains(p->r_ohm, p->ld_h, p->bw_current_hz, &kp, &ki);
    s->pi_d.kp = kp;
    s->pi_d.ki_ts = ki * s->ts;
    foc_pi_gains(p->r_ohm, p->lq_h, p->bw_current_hz, &kp, &ki);
    s->pi_q.kp = kp;
    s->pi_q.ki_ts = ki * s->ts;
    foc_pll_init(&s->pll, p->bw_pll_hz, s->ts);
    foc_reset(s);
    return 0;
}

/* PI output for one axis. The integrator update is only a candidate until the
 * voltage limit has been applied (pi_commit). */
static float pi_out(const FocPi *pi, float err, float ff, float *integ_next)
{
    *integ_next = pi->integ + pi->ki_ts * err;
    return ff + pi->kp * err + *integ_next;
}

/* Conditional integration: accept the new integral unless this axis was cut
 * by the limit AND the error points further into the cut. Clamped to the
 * voltage ceiling either way, so it can never wind past what is reachable. */
static void pi_commit(FocPi *pi, float err, float integ_next, float v_unlim,
                      float v_lim, float v_max)
{
    int cut = (v_lim != v_unlim);
    if (!cut || err * v_unlim < 0.0f)
        pi->integ = integ_next;
    pi->integ = clampf(pi->integ, -v_max, v_max);
}

void foc_isr_step(FocState *s, const FocIn *in, FocOut *out)
{
    const FocParams *p = &s->p;
    float th_enc, th, sn, cs, ia, ib, id, iq;
    float id_ref, iq_ref, iq_room, ed, eq, vd_ff = 0.0f, vq_ff = 0.0f;
    float vd_u, vq_u, vd, vq, nd, nq, v_max, th_v, va, vb, w_e;

    /* Angle and speed. The PLL always follows the encoder, even while the
     * angle is forced, so speed is known when closed loop takes over. */
    th_enc = foc_mech_to_elec(in->theta_m, p->pole_pairs, p->enc_offset_e);
    if (!s->pll_seeded) {
        foc_pll_reset(&s->pll, th_enc);
        s->pll_seeded = 1u;
    }
    foc_pll_step(&s->pll, th_enc);
    w_e = s->pll.omega;
    th  = in->force_angle ? foc_wrap_pi(in->theta_e_force) : th_enc;

    /* Measure. */
    sn = sinf(th);
    cs = cosf(th);
    foc_clarke(in->i_abc[0], in->i_abc[1], in->i_abc[2], &ia, &ib);
    foc_park(ia, ib, sn, cs, &id, &iq);

    out->id = id;
    out->iq = iq;
    out->theta_e = th;
    out->omega_e = w_e;
    out->omega_m = w_e / (float)p->pole_pairs;
    out->v_sat = 0u;

    v_max = p->mod_max * in->vbus_v * (1.0f / FOC_SQRT3_F);
    if (!in->enable || !(v_max > 0.0f)) {
        s->pi_d.integ = 0.0f;
        s->pi_q.integ = 0.0f;
        out->vd = out->vq = 0.0f;
        out->duty[0] = out->duty[1] = out->duty[2] = 0.5f;  /* HAL also gates off */
        return;
    }

    /* References inside the current circle, d first (field weakening and
     * alignment ask for Id; torque gets the rest). */
    id_ref  = clampf(in->id_ref, -p->i_max_a, p->i_max_a);
    iq_room = sqrtf(fmaxf(p->i_max_a * p->i_max_a - id_ref * id_ref, 0.0f));
    iq_ref  = clampf(in->iq_ref, -iq_room, iq_room);
    ed = id_ref - id;
    eq = iq_ref - iq;

    /* Feed-forward: cancel the speed-dependent cross-coupling and back-EMF so
     * the PI only sees R + sL. Off when the angle is forced (it is not the
     * rotor's angle then). */
    if (p->ff_enable && !in->force_angle) {
        vd_ff = -w_e * p->lq_h * iq;
        vq_ff =  w_e * (p->ld_h * id + p->flux_wb);
    }

    vd_u = pi_out(&s->pi_d, ed, vd_ff, &nd);
    vq_u = pi_out(&s->pi_q, eq, vq_ff, &nq);
    vd = vd_u;
    vq = vq_u;
    out->v_sat = (uint8_t)foc_limit_vdq(&vd, &vq, v_max);
    pi_commit(&s->pi_d, ed, nd, vd_u, vd, v_max);
    pi_commit(&s->pi_q, eq, nq, vq_u, vq, v_max);

    /* Back to the stator, at the angle the rotor will have when the voltage
     * is (on average) applied. */
    th_v = in->force_angle ? th : th + w_e * p->delay_periods * s->ts;
    if (th_v != th) {
        sn = sinf(th_v);
        cs = cosf(th_v);
    }
    foc_inv_park(vd, vq, sn, cs, &va, &vb);
    foc_svpwm(va, vb, in->vbus_v, out->duty);

    out->vd = vd;
    out->vq = vq;
}
