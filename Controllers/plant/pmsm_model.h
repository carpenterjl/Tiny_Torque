/*
 * pmsm_model.h — dq model of one PMSM wheel motor and its inverter (FW-10).
 *
 * HOST ONLY: the plant foc/foc.c is unit-tested against
 * (tests/test_foc_pmsm.c). Double precision throughout; never built for the
 * MCU.
 *
 * Model, in the rotor frame (amplitude-invariant, same conventions as foc.h):
 *   Ld did/dt = vd - R id + w_e Lq iq
 *   Lq diq/dt = vq - R iq - w_e (Ld id + lambda)
 *   Te        = 1.5 p (lambda iq + (Ld - Lq) id iq)
 *   J dw_m/dt = Te - b w_m - T_load
 * w_e = p w_m. The inverter is an average-voltage model: each pole sits at
 * duty * Vbus over a PWM period, minus an optional dead-time error of
 * sign(i) * Vbus * t_dead * f_pwm; the motor neutral floats, so the common
 * mode drops out. PWM ripple is not modelled.
 */
#ifndef PMSM_MODEL_H
#define PMSM_MODEL_H

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    /* motor */
    double r_ohm, ld_h, lq_h, flux_wb;
    int    pole_pairs;
    double j_kgm2;           /* rotor (+ reflected load) inertia            */
    double b_nms;            /* viscous friction, N*m*s/rad                 */
    double t_load_nm;        /* constant load torque, opposes + rotation    */

    /* inverter */
    double vbus_v;
    double f_pwm_hz;         /* only used by the dead-time error            */
    double dead_time_s;      /* 0 = ideal inverter                          */

    /* integration */
    double h_max_s;          /* largest internal step (default 1 us)        */

    /* speed imposed by a dynamometer: w_m stays at omega_m, Te is ignored.
     * A locked rotor is fixed_speed = 1 with omega_m = 0. */
    int    fixed_speed;

    /* state */
    double id, iq;           /* A                                           */
    double omega_m;          /* rad/s                                       */
    double theta_m;          /* rad, unwrapped                              */
} PmsmModel;

/* Zero state (at rest, theta_m = 0), h_max 1 us if unset. Fill the motor
 * and inverter fields first; set omega_m / theta_m afterwards if wanted. */
void   pmsm_init(PmsmModel *m);

/* Advance dt seconds with the three duties held (0..1). */
void   pmsm_step(PmsmModel *m, const float duty[3], double dt);

/* What the drive's sensors see. */
void   pmsm_phase_currents(const PmsmModel *m, float i_abc[3]);
double pmsm_theta_e(const PmsmModel *m);      /* unwrapped p * theta_m     */
double pmsm_torque(const PmsmModel *m);       /* electromagnetic Te, N*m   */
double pmsm_kt(const PmsmModel *m);           /* 1.5 p lambda              */

#ifdef __cplusplus
}
#endif

#endif /* PMSM_MODEL_H */
