/*
 * pmsm_model.c — dq PMSM + average-model inverter, host only (FW-10).
 * See pmsm_model.h for the equations.
 */
#include "pmsm_model.h"

#include <math.h>

#define SQRT3 1.7320508075688772

void pmsm_init(PmsmModel *m)
{
    m->id = m->iq = 0.0;
    m->theta_m = 0.0;
    m->omega_m = 0.0;
    if (!(m->h_max_s > 0.0))
        m->h_max_s = 1e-6;
}

double pmsm_theta_e(const PmsmModel *m) { return (double)m->pole_pairs * m->theta_m; }
double pmsm_kt(const PmsmModel *m)      { return 1.5 * (double)m->pole_pairs * m->flux_wb; }

double pmsm_torque(const PmsmModel *m)
{
    return 1.5 * (double)m->pole_pairs *
           (m->flux_wb * m->iq + (m->ld_h - m->lq_h) * m->id * m->iq);
}

/* Stator-frame phase currents for a dq state at electrical angle th. */
static void dq_to_abc(double id, double iq, double th, double i[3])
{
    double s = sin(th), c = cos(th);
    double ia = c * id - s * iq;          /* alpha */
    double ib = s * id + c * iq;          /* beta  */
    i[0] = ia;
    i[1] = -0.5 * ia + 0.5 * SQRT3 * ib;
    i[2] = -0.5 * ia - 0.5 * SQRT3 * ib;
}

void pmsm_phase_currents(const PmsmModel *m, float i_abc[3])
{
    double i[3];
    dq_to_abc(m->id, m->iq, pmsm_theta_e(m), i);
    i_abc[0] = (float)i[0];
    i_abc[1] = (float)i[1];
    i_abc[2] = (float)i[2];
}

/* Smooth sign for the dead-time error: a hard sign() would chatter the
 * model at the current zero crossing. */
static double soft_sign(double i) { return i / (fabs(i) + 0.05); }

/* Time derivatives of (id, iq, w_m) at a state, for the given pole
 * voltages. theta_m enters through the angle only. */
static void deriv(const PmsmModel *m, const double vpole[3], double id, double iq,
                  double w_m, double th_m, double *did, double *diq, double *dw)
{
    double th = (double)m->pole_pairs * th_m, w_e = (double)m->pole_pairs * w_m;
    double v[3], i[3], va, vb, vd, vq, s, c, te;
    double dt_err = m->vbus_v * m->dead_time_s * m->f_pwm_hz;
    int k;

    dq_to_abc(id, iq, th, i);
    for (k = 0; k < 3; k++)
        v[k] = vpole[k] - dt_err * soft_sign(i[k]);
    /* Clarke of the pole voltages: the common mode (and so the floating
     * neutral) drops out. */
    va = (2.0 * v[0] - v[1] - v[2]) / 3.0;
    vb = (v[1] - v[2]) / SQRT3;
    s = sin(th);
    c = cos(th);
    vd =  c * va + s * vb;
    vq = -s * va + c * vb;

    *did = (vd - m->r_ohm * id + w_e * m->lq_h * iq) / m->ld_h;
    *diq = (vq - m->r_ohm * iq - w_e * (m->ld_h * id + m->flux_wb)) / m->lq_h;
    te = 1.5 * (double)m->pole_pairs * (m->flux_wb * iq + (m->ld_h - m->lq_h) * id * iq);
    *dw = m->fixed_speed ? 0.0 : (te - m->b_nms * w_m - m->t_load_nm) / m->j_kgm2;
}

void pmsm_step(PmsmModel *m, const float duty[3], double dt)
{
    double vpole[3];
    int n, k;
    double h;

    if (!(dt > 0.0))
        return;
    for (k = 0; k < 3; k++)
        vpole[k] = (double)duty[k] * m->vbus_v;
    n = (int)ceil(dt / m->h_max_s);
    h = dt / (double)n;

    /* Midpoint (RK2): plenty at h <= 1 us against an L/R of hundreds of us. */
    for (k = 0; k < n; k++) {
        double d1, q1, w1, d2, q2, w2;
        deriv(m, vpole, m->id, m->iq, m->omega_m, m->theta_m, &d1, &q1, &w1);
        deriv(m, vpole, m->id + 0.5 * h * d1, m->iq + 0.5 * h * q1,
              m->omega_m + 0.5 * h * w1, m->theta_m + 0.5 * h * m->omega_m,
              &d2, &q2, &w2);
        m->theta_m += h * (m->omega_m + 0.5 * h * w1);
        m->id      += h * d2;
        m->iq      += h * q2;
        m->omega_m += h * w2;
    }
}
