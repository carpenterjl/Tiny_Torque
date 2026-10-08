/*
 * test_foc_pmsm.c — the FOC current loop (foc/foc.c) against a dq PMSM plant
 * (plant/pmsm_model.c), FW-10. Registered with CTest as `foc_pmsm`.
 *
 * Motor: a small outrunner for a ~2 kg 1/10 car, one per wheel. R = 0.1 ohm
 * per phase, L = 25 uH, 7 pole pairs, Kv = 1000 rpm/V on a 2S pack (7.4 V),
 * 20 kHz PWM, 2 kHz current-loop bandwidth, rotor J = 3e-6 kg m^2.
 *
 * Timing as on the MCU (foc.h): currents sampled at the PWM centre, new duties
 * loaded half a period later and held for one period.
 */
#include "foc.h"
#include "pmsm_model.h"

#include <math.h>
#include <stdarg.h>
#include <stdio.h>

#define PI 3.14159265358979323846

static int g_fail = 0;

static void check(const char *name, int ok, const char *fmt, ...)
{
    va_list ap;
    printf(ok ? "PASS  %-52s " : "FAIL  %-52s ", name);
    va_start(ap, fmt);
    vprintf(fmt, ap);
    va_end(ap);
    printf("\n");
    if (!ok)
        g_fail++;
}

/* ---- the motor ------------------------------------------------------------- */

#define R_OHM   0.1
#define L_H     25e-6
#define PP      7
#define KV_RPM  1000.0
#define VBUS    7.4
#define F_PWM   20000.0
#define TS      (1.0 / F_PWM)
#define BW_I    2000.0
#define J_ROT   3e-6

/* Kv -> lambda: the line-line peak back-EMF is sqrt3 * p * lambda * w_m, and
 * Kv is (rad/s) per (line-line peak) volt. */
static double flux_wb(void) { return 60.0 / (2.0 * PI * KV_RPM) / (sqrt(3.0) * PP); }

static FocParams foc_params(void)
{
    FocParams p;
    p.r_ohm = (float)R_OHM;
    p.ld_h = p.lq_h = (float)L_H;
    p.flux_wb = (float)flux_wb();
    p.pole_pairs = PP;
    p.f_pwm_hz = (float)F_PWM;
    p.bw_current_hz = (float)BW_I;
    p.bw_pll_hz = 300.0f;
    p.i_max_a = 30.0f;
    p.mod_max = 0.95f;
    p.delay_periods = 1.0f;
    p.ff_enable = 1u;
    p.enc_offset_e = 0.0f;
    return p;
}

static void plant_params(PmsmModel *m)
{
    m->r_ohm = R_OHM;
    m->ld_h = m->lq_h = L_H;
    m->flux_wb = flux_wb();
    m->pole_pairs = PP;
    m->j_kgm2 = J_ROT;
    m->b_nms = 5e-5;
    m->t_load_nm = 0.0;
    m->vbus_v = VBUS;
    m->f_pwm_hz = F_PWM;
    m->dead_time_s = 200e-9;   /* ~0.03 V of pole-voltage error */
    m->h_max_s = 1e-6;
    m->fixed_speed = 0;
    pmsm_init(m);
}

/* ---- the rig: plant + FOC + PWM timing --------------------------------------- */

typedef struct {
    PmsmModel m;
    FocState  f;
    FocIn     in;
    FocOut    out;
    float     duty_prev[3];
    double    mount;          /* encoder zero vs rotor d-axis, mechanical rad */
    double    t;
} Rig;

static void rig_init(Rig *r, const FocParams *fp, double mount)
{
    plant_params(&r->m);
    foc_init(&r->f, fp);
    r->in.vbus_v = (float)VBUS;
    r->in.id_ref = r->in.iq_ref = 0.0f;
    r->in.enable = 1u;
    r->in.force_angle = 0u;
    r->in.theta_e_force = 0.0f;
    r->duty_prev[0] = r->duty_prev[1] = r->duty_prev[2] = 0.5f;
    r->mount = mount;
    r->t = 0.0;
}

/* What the encoder reads: the rotor angle plus its mounting offset, 0..2pi. */
static float rig_encoder(const Rig *r)
{
    double a = fmod(r->m.theta_m + r->mount, 2.0 * PI);
    return (float)(a < 0.0 ? a + 2.0 * PI : a);
}

/* One PWM period. If t_cross is given and still negative, the first time the
 * plant's iq reaches `thresh` is recorded at 1/20-period resolution. */
static void rig_step(Rig *r, double thresh, double *t_cross)
{
    int half, k;
    const int chunks = 10;
    pmsm_phase_currents(&r->m, r->in.i_abc);
    r->in.theta_m = rig_encoder(r);
    foc_isr_step(&r->f, &r->in, &r->out);
    for (half = 0; half < 2; half++) {
        const float *d = half ? r->out.duty : r->duty_prev;
        for (k = 0; k < chunks; k++) {
            pmsm_step(&r->m, d, 0.5 * TS / chunks);
            r->t += 0.5 * TS / chunks;
            if (t_cross && *t_cross < 0.0 && r->m.iq >= thresh)
                *t_cross = r->t;
        }
    }
    for (k = 0; k < 3; k++)
        r->duty_prev[k] = r->out.duty[k];
}

static double wrap_pi(double x) { return x - 2.0 * PI * floor((x + PI) / (2.0 * PI)); }

/* Deterministic pseudo-random in [lo, hi). */
static unsigned g_seed = 12345u;
static double rnd(double lo, double hi)
{
    g_seed = g_seed * 1103515245u + 12345u;
    return lo + (hi - lo) * (double)((g_seed >> 8) & 0xFFFFu) / 65536.0;
}

/* ---- tests --------------------------------------------------------------------- */

static void test_transforms(void)
{
    double e_clarke = 0.0, e_park = 0.0;
    int n;
    for (n = 0; n < 1000; n++) {
        float amp = (float)rnd(0.0, 50.0), ph = (float)rnd(-PI, PI);
        float a = amp * cosf(ph);
        float b = amp * cosf(ph - 2.0943951f);
        float c = -a - b;                         /* balanced, as the motor's */
        float al, be, a2, b2, c2, d, q, al2, be2;
        float th = (float)rnd(-PI, PI), s = sinf(th), co = cosf(th);
        foc_clarke(a, b, c, &al, &be);
        foc_inv_clarke(al, be, &a2, &b2, &c2);
        e_clarke = fmax(e_clarke, fabs((double)a2 - a) / (amp + 1.0));
        e_clarke = fmax(e_clarke, fabs((double)b2 - b) / (amp + 1.0));
        e_clarke = fmax(e_clarke, fabs((double)c2 - c) / (amp + 1.0));
        foc_park(al, be, s, co, &d, &q);
        foc_inv_park(d, q, s, co, &al2, &be2);
        e_park = fmax(e_park, fabs((double)al2 - al) / (amp + 1.0));
        e_park = fmax(e_park, fabs((double)be2 - be) / (amp + 1.0));
    }
    check("Clarke -> inverse Clarke round trip", e_clarke < 1e-5, "(max rel err %.2e)", e_clarke);
    check("Park -> inverse Park round trip", e_park < 1e-5, "(max rel err %.2e)", e_park);
    {
        /* An amplitude-I sine set is a vector of length I (amplitude-invariant). */
        float al, be;
        foc_clarke(10.0f, -5.0f, -5.0f, &al, &be);
        check("Clarke is amplitude-invariant", fabs(al - 10.0) < 1e-5 && fabs(be) < 1e-5,
              "(alpha %.6f beta %.6f)", (double)al, (double)be);
    }
}

static void test_svpwm(void)
{
    const float vbus = (float)VBUS, vlim = vbus / FOC_SQRT3_F;
    double e_max = 0.0;
    int n, in_range = 1;
    for (n = 0; n < 2000; n++) {
        double mag = (n == 0) ? (double)vlim : rnd(0.0, 1.0) * vlim;
        double ang = rnd(-PI, PI);
        float va = (float)(mag * cos(ang)), vb = (float)(mag * sin(ang)), duty[3];
        double v[3], ra, rb, err;
        int k;
        foc_svpwm(va, vb, vbus, duty);
        for (k = 0; k < 3; k++) {
            if (!(duty[k] >= 0.0f && duty[k] <= 1.0f)) in_range = 0;
            v[k] = (double)duty[k] * vbus;      /* average pole voltage */
        }
        /* What the motor sees: line-line differences, i.e. the Clarke of the
         * pole voltages with the common mode removed. */
        ra = (2.0 * v[0] - v[1] - v[2]) / 3.0;
        rb = (v[1] - v[2]) / sqrt(3.0);
        err = hypot(ra - va, rb - vb) / fmax(mag, 0.01 * vlim);
        e_max = fmax(e_max, err);
    }
    check("SVPWM duties in [0,1] up to |V| = Vbus/sqrt3", in_range, "(2000 vectors)");
    check("SVPWM reproduces commanded alpha-beta voltage", e_max < 0.01,
          "(max rel err %.2e, limit 1e-2)", e_max);
    {
        float duty[3];
        int k, ok = 1;
        foc_svpwm(2.0f * vlim, 0.3f, vbus, duty);   /* over-modulated */
        for (k = 0; k < 3; k++) if (!(duty[k] >= 0.0f && duty[k] <= 1.0f)) ok = 0;
        check("SVPWM clamps an over-modulated vector", ok, "(%.3f %.3f %.3f)",
              (double)duty[0], (double)duty[1], (double)duty[2]);
    }
    {
        float vd = 3.0f, vq = 5.0f;
        int cut = foc_limit_vdq(&vd, &vq, 4.0f);
        check("voltage limit keeps d, gives q the rest",
              cut && vd == 3.0f && fabs(vq - sqrt(7.0)) < 1e-5,
              "(vd %.3f vq %.4f)", (double)vd, (double)vq);
    }
}

static void test_locked_rotor(void)
{
    FocParams fp = foc_params();
    Rig r;
    const double step = 10.0, tau = 1.0 / (2.0 * PI * BW_I);
    double t63 = -1.0, t_settle = 0.0, id_max = 0.0, iq_peak = 0.0, iq_end = 0.0;
    int k;

    rig_init(&r, &fp, 0.7);
    fp.enc_offset_e = (float)wrap_pi(-PP * r.mount);   /* calibrated encoder */
    foc_init(&r.f, &fp);
    r.m.fixed_speed = 1;
    r.m.omega_m = 0.0;
    r.m.theta_m = 0.3;                 /* arbitrary rotor position */

    r.in.iq_ref = (float)step;
    for (k = 0; k < (int)(5e-3 * F_PWM); k++) {
        rig_step(&r, 0.632 * step, &t63);
        if (fabs(r.m.iq - step) > 0.02 * step) t_settle = r.t;
        id_max = fmax(id_max, fabs(r.m.id));
        iq_peak = fmax(iq_peak, r.m.iq);
        iq_end = r.m.iq;
    }
    check("locked rotor: Iq reaches 63 % in 1/(2 pi bw) +- 30 %",
          t63 > 0.7 * tau && t63 < 1.3 * tau,
          "(t63 %.1f us, target %.1f us)", t63 * 1e6, tau * 1e6);
    check("locked rotor: Iq settles within 2 %",
          t_settle < 1e-3 && fabs(iq_end - step) < 0.02 * step,
          "(settled after %.0f us, final %.3f A, overshoot %.1f %%)",
          t_settle * 1e6, iq_end, (iq_peak / step - 1.0) * 100.0);
    check("locked rotor: Id coupling < 5 % of the step", id_max < 0.05 * step,
          "(max |Id| %.4f A)", id_max);
}

static void test_free_rotor(void)
{
    FocParams fp = foc_params();
    Rig r;
    const double iq_ref = 5.0;
    double kt, te_err = 0.0, w_err = 0.0, w_true = 0.0, w_expect;
    int k, n = (int)(0.6 * F_PWM), n_avg = 0;

    rig_init(&r, &fp, -2.1);
    fp.enc_offset_e = (float)wrap_pi(-PP * r.mount);
    foc_init(&r.f, &fp);
    r.m.t_load_nm = 0.02;
    r.m.b_nms = 5e-5;

    r.in.iq_ref = (float)iq_ref;
    kt = 1.5 * fp.pole_pairs * (double)fp.flux_wb;   /* the firmware's kt */
    for (k = 0; k < n; k++) {
        double te = pmsm_torque(&r.m), w = r.m.omega_m;   /* at the sample */
        rig_step(&r, 0.0, NULL);
        if (k >= n - (int)(0.01 * F_PWM)) {       /* last 10 ms */
            te_err = fmax(te_err, fabs(kt * r.out.iq - te) / fabs(te));
            w_err  = fmax(w_err, fabs((double)r.out.omega_m - w) / w);
            w_true += w;
            n_avg++;
        }
    }
    w_true /= n_avg;
    w_expect = (pmsm_kt(&r.m) * iq_ref - r.m.t_load_nm) / r.m.b_nms;
    check("free rotor: kt * Iq matches plant Te within 1 %", te_err < 0.01,
          "(max err %.3f %%, Te %.4f N m)", te_err * 100.0, pmsm_torque(&r.m));
    check("free rotor: Iq holds its reference", fabs(r.m.iq - iq_ref) < 0.01 * iq_ref,
          "(Iq %.4f A)", r.m.iq);
    check("free rotor: PLL speed tracks true speed within 1 %", w_err < 0.01,
          "(max err %.4f %% at %.1f rad/s)", w_err * 100.0, w_true);
    check("free rotor: speed = (kt Iq - T_load) / b", fabs(w_true - w_expect) < 0.01 * w_expect,
          "(%.1f vs %.1f rad/s)", w_true, w_expect);
}

/* Run n periods; return the time (after the start) at which |iq - target|
 * last exceeded tol, i.e. the recovery time. */
static double run_recover(Rig *r, int n, double target, double tol, double w_from,
                          double w_to, int n_ramp)
{
    double t0 = r->t, t_last = 0.0;
    int k;
    for (k = 0; k < n; k++) {
        if (k < n_ramp)
            r->m.omega_m = w_from + (w_to - w_from) * (k + 1) / n_ramp;
        rig_step(r, 0.0, NULL);
        if (fabs(r->m.iq - target) > tol) t_last = r->t - t0;
    }
    return t_last;
}

static void test_voltage_ceiling(void)
{
    FocParams fp = foc_params();
    Rig r;
    const double w_hi = 650.0, w_lo = 200.0;
    double iq_sat = 0.0, integ_max = 0.0, v_max, t_rec;
    int k, sat_seen = 0, n = (int)(20e-3 * F_PWM);

    rig_init(&r, &fp, 0.4);
    fp.enc_offset_e = (float)wrap_pi(-PP * r.mount);
    foc_init(&r.f, &fp);
    r.m.fixed_speed = 1;              /* dynamometer */
    r.m.omega_m = w_hi;
    v_max = fp.mod_max * VBUS / sqrt(3.0);

    /* 20 A at 650 rad/s needs ~|V| 6 V against a 4.06 V ceiling. */
    r.in.iq_ref = 20.0f;
    for (k = 0; k < n; k++) {
        rig_step(&r, 0.0, NULL);
        if (k >= n / 2) {
            iq_sat = fmax(iq_sat, r.m.iq);
            sat_seen |= r.out.v_sat;
            integ_max = fmax(integ_max, fmax(fabs(r.f.pi_d.integ), fabs(r.f.pi_q.integ)));
        }
    }
    check("ceiling: 20 A at 650 rad/s is not reachable (voltage-limited)",
          sat_seen && iq_sat < 0.9 * 20.0, "(Iq %.2f A)", iq_sat);
    check("ceiling: integrators stay inside the voltage limit",
          integ_max <= v_max + 1e-4, "(max |integ| %.3f V, limit %.3f V)", integ_max, v_max);

    /* The reference drops to something reachable. */
    r.in.iq_ref = 2.0f;
    t_rec = run_recover(&r, (int)(10e-3 * F_PWM), 2.0, 0.1, w_hi, w_hi, 0);
    check("ceiling: Iq recovers after the reference drops",
          t_rec < 2e-3, "(within 0.1 A after %.0f us)", t_rec * 1e6);

    /* Saturate again, then the speed drops (5 ms ramp) and 20 A becomes
     * reachable. */
    r.in.iq_ref = 20.0f;
    run_recover(&r, n, 20.0, 0.4, w_hi, w_hi, 0);
    t_rec = run_recover(&r, (int)(20e-3 * F_PWM), 20.0, 0.4, w_hi, w_lo,
                        (int)(5e-3 * F_PWM));
    check("ceiling: Iq recovers after the speed drops",
          t_rec < 5e-3 + 3e-3, "(within 2 %% after %.0f us from ramp start; ramp 5000 us)",
          t_rec * 1e6);
}

static void test_alignment(void)
{
    FocParams fp = foc_params();
    Rig r;
    float off, th_enc;
    double expect, err, e_track = 0.0;
    int k;

    rig_init(&r, &fp, 1.234);          /* encoder offset unknown to the FOC */
    r.m.b_nms = 5e-4;                   /* damping so the rotor settles */
    r.m.theta_m = 0.2;                  /* theta_e = 1.4 rad: well off the axis */

    /* Forced alignment: 8 A along theta_e = 0 for 0.3 s. */
    r.in.force_angle = 1u;
    r.in.theta_e_force = 0.0f;
    r.in.id_ref = 8.0f;
    for (k = 0; k < (int)(0.3 * F_PWM); k++)
        rig_step(&r, 0.0, NULL);
    th_enc = rig_encoder(&r);
    off = foc_align_offset(th_enc, PP, 0.0f);
    expect = wrap_pi(-PP * r.mount);
    err = fabs(wrap_pi((double)off - expect));
    check("alignment recovers the encoder offset", err < 2e-3,
          "(%.5f vs %.5f rad elec, err %.2e)", (double)off, expect, err);

    /* With it, the encoder gives the true electrical angle anywhere. */
    for (k = 0; k < 50; k++) {
        double th_m = rnd(-20.0, 20.0);
        float th_e;
        r.m.theta_m = th_m;
        th_e = foc_mech_to_elec(rig_encoder(&r), PP, off);
        e_track = fmax(e_track, fabs(wrap_pi((double)th_e - PP * th_m)));
    }
    check("calibrated encoder gives the rotor's electrical angle", e_track < 3e-3,
          "(max err %.2e rad elec)", e_track);
}

static void test_init(void)
{
    FocParams fp = foc_params();
    FocState s;
    check("foc_init accepts the test motor", foc_init(&s, &fp) == 0, "");
    fp.pole_pairs = 0u;
    check("foc_init rejects zero pole pairs", foc_init(&s, &fp) != 0, "");
    {
        float kp, ki;
        foc_pi_gains(0.1f, 25e-6f, 2000.0f, &kp, &ki);
        check("PI gains: Kp = L w, Ki = R w",
              fabs(kp - 25e-6 * 2 * PI * 2000) < 1e-5 && fabs(ki - 0.1 * 2 * PI * 2000) < 1e-2,
              "(Kp %.4f V/A, Ki %.1f V/(A s))", (double)kp, (double)ki);
    }
}

int main(void)
{
    printf("motor: lambda %.4e Wb, kt %.4e N m/A, tau_e %.0f us, no-load %.0f rad/s\n",
           flux_wb(), 1.5 * PP * flux_wb(), L_H / R_OHM * 1e6,
           VBUS / sqrt(3.0) / (PP * flux_wb()));
    test_init();
    test_transforms();
    test_svpwm();
    test_locked_rotor();
    test_free_rotor();
    test_voltage_ceiling();
    test_alignment();
    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
