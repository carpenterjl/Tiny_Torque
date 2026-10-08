/*
 * foc.h — field-oriented current control for one PMSM/BLDC wheel motor (FW-10).
 *
 * The car has one motor per wheel, each on its own FOC driver: the vehicle
 * firmware asks for an Iq (tt_torque_to_iq) and this module turns it into
 * three PWM duties. Unity simulates only the CLOSED current loop, so this
 * code never runs in the sim; it is unit-tested on the host against a dq
 * motor plant (plant/pmsm_model.h, tests/test_foc_pmsm.c) and cross-compiled
 * for the MCU with the rest of the portable core.
 *
 * Portable C11, float32 only, no malloc, no stdio. foc_isr_step() is meant
 * for the ADC-injected-conversion ISR at the PWM rate (20–40 kHz).
 *
 * Conventions:
 *   - Clarke is amplitude-invariant: a phase current of amplitude I is a
 *     space vector of length I, so Te = 1.5 p (lambda iq + (Ld - Lq) id iq)
 *     and kt = 1.5 p lambda (N*m per A of Iq).
 *   - theta_e = 0 puts the rotor d-axis (magnet north) on phase A. Positive
 *     rotation is A -> B -> C. Angles in rad, speeds in rad/s.
 *   - Duties are 0..1 of the high-side on-time, centre-aligned PWM.
 *   - Timing model (what the gains and delay compensation assume): the ADC
 *     samples at the PWM centre, the ISR computes, and the new duties load
 *     at the next counter update, half a period later; they then hold for one
 *     period. The voltage is therefore centred one period after the sample
 *     (FocParams.delay_periods = 1).
 */
#ifndef FOC_H
#define FOC_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define FOC_PI_F     3.14159265f
#define FOC_2PI_F    6.28318531f
#define FOC_SQRT3_F  1.73205081f

/* The motor and loop configuration. All fields 32-bit, like TtParams, so it
 * can live in the same flash store and hash the same on every target. */
typedef struct {
    float    r_ohm;          /* phase resistance (line-line / 2)           */
    float    ld_h, lq_h;     /* d/q inductance per phase                    */
    float    flux_wb;        /* lambda: PM flux linkage, per-phase peak     */
    uint32_t pole_pairs;
    float    f_pwm_hz;       /* = ISR rate                                  */
    float    bw_current_hz;  /* current-loop bandwidth: Kp = L w, Ki = R w  */
    float    bw_pll_hz;      /* speed/angle PLL natural frequency           */
    float    i_max_a;        /* |Idq| limit applied to the references       */
    float    mod_max;        /* fraction of the linear SVPWM limit Vbus/sqrt3
                                actually used (< 1 leaves room for sampling) */
    float    delay_periods;  /* sample -> centre of applied voltage, in PWM
                                periods; inverse Park leads by w_e * this   */
    uint32_t ff_enable;      /* 1 = dq decoupling + back-EMF feed-forward   */
    float    enc_offset_e;   /* theta_e = p * theta_enc + this (foc_align_offset) */
} FocParams;

/* One axis of the current controller: PI with clamped, conditional
 * integration (the integrator freezes while its output is being cut by the
 * voltage limit and the error would push it further in). */
typedef struct {
    float kp, ki_ts;         /* V/A, V/A per sample (= Ki * Ts)             */
    float integ;             /* V                                           */
} FocPi;

/* Type-2 PLL on the electrical angle: angle and speed with zero steady-state
 * error at constant speed. */
typedef struct {
    float theta;             /* estimated theta_e, wrapped to [-pi, pi)     */
    float omega;             /* estimated omega_e, rad/s                    */
    float kp, ki;            /* 2 zeta wn, wn^2                             */
    float ts;
} FocPll;

typedef struct {
    FocParams p;
    float     ts;            /* 1 / f_pwm                                   */
    FocPi     pi_d, pi_q;
    FocPll    pll;
    uint8_t   pll_seeded;
    uint8_t   pad_[3];
} FocState;

typedef struct {
    float   i_abc[3];        /* phase currents, A, + = into the motor       */
    float   vbus_v;
    float   theta_m;         /* encoder mechanical angle, rad (any wrap)    */
    float   id_ref, iq_ref;  /* A                                           */
    uint8_t enable;          /* 0 = zero voltage, integrators cleared       */
    uint8_t force_angle;     /* 1 = use theta_e_force instead of the encoder
                                (alignment, open-loop start)                */
    uint8_t pad_[2];
    float   theta_e_force;
} FocIn;

typedef struct {
    float   duty[3];         /* 0..1, phases A B C                          */
    float   id, iq;          /* measured, A                                 */
    float   vd, vq;          /* applied (after limiting), V                 */
    float   theta_e;         /* angle used for Park, rad                    */
    float   omega_e;         /* PLL electrical speed, rad/s                 */
    float   omega_m;         /* PLL mechanical speed, rad/s                 */
    uint8_t v_sat;           /* 1 = the voltage vector hit the limit        */
    uint8_t pad_[3];
} FocOut;

/* ---- transforms --------------------------------------------------------- */

/* Amplitude-invariant Clarke from three measured phases (the common mode,
 * e.g. an offset shared by all three sensors, drops out). */
static inline void foc_clarke(float a, float b, float c, float *alpha, float *beta)
{
    *alpha = (2.0f * a - b - c) * (1.0f / 3.0f);
    *beta  = (b - c) * (1.0f / FOC_SQRT3_F);
}

static inline void foc_inv_clarke(float alpha, float beta, float *a, float *b, float *c)
{
    *a = alpha;
    *b = -0.5f * alpha + 0.5f * FOC_SQRT3_F * beta;
    *c = -0.5f * alpha - 0.5f * FOC_SQRT3_F * beta;
}

/* Park with a precomputed sin/cos (the ISR evaluates them once). */
static inline void foc_park(float alpha, float beta, float s, float c, float *d, float *q)
{
    *d =  c * alpha + s * beta;
    *q = -s * alpha + c * beta;
}

static inline void foc_inv_park(float d, float q, float s, float c, float *alpha, float *beta)
{
    *alpha = c * d - s * q;
    *beta  = s * d + c * q;
}

/* Wrap an angle to [-pi, pi). */
float foc_wrap_pi(float x);

/* ---- building blocks ------------------------------------------------------ */

/* Kp = L * 2 pi bw, Ki = R * 2 pi bw: the PI zero cancels the R/L pole, so
 * the closed loop is first order with the requested bandwidth. */
void  foc_pi_gains(float r_ohm, float l_h, float bw_hz, float *kp, float *ki);

/* Limit (vd, vq) to a circle of radius v_max, d-axis first: d keeps what it
 * asked for (up to v_max), q gets what is left. Returns 1 if anything was cut. */
int   foc_limit_vdq(float *vd, float *vq, float v_max);

/* Min/max zero-sequence SVPWM: duties 0..1 for an alpha-beta voltage. Linear
 * up to |V| = vbus / sqrt3; beyond that the duties are clamped. */
void  foc_svpwm(float v_alpha, float v_beta, float vbus, float duty[3]);

void  foc_pll_init(FocPll *pll, float bw_hz, float ts);
void  foc_pll_reset(FocPll *pll, float theta);
void  foc_pll_step(FocPll *pll, float theta_meas);

/* Electrical angle from a mechanical one, wrapped to [-pi, pi). */
float foc_mech_to_elec(float theta_m, uint32_t pole_pairs, float offset_e);

/* Encoder offset from a forced alignment: drive Id along theta_e_forced
 * (normally 0) until the rotor settles, read the encoder, and pass both here.
 * The result goes in FocParams.enc_offset_e. */
float foc_align_offset(float theta_m_at_align, uint32_t pole_pairs, float theta_e_forced);

/* ---- the controller ------------------------------------------------------- */

/* Copies the parameters and derives gains. Returns 0, or -1 if a parameter
 * is unusable (non-positive R/L/f_pwm/bandwidth, zero pole pairs). */
int   foc_init(FocState *s, const FocParams *p);
void  foc_reset(FocState *s);

/* One current-loop step: measurements and references in, duties out. */
void  foc_isr_step(FocState *s, const FocIn *in, FocOut *out);

#ifdef __cplusplus
}
#endif

#endif /* FOC_H */
