/*
 * tt_nav.h — planar navigation filter: UWB + odometry + gyro (+ flow) EKF.
 *
 * The L3 estimator of the plan's control stack (§7.1): a 6-state EKF
 *
 *     s = [x, y, psi, v, b_g, k_w]
 *
 * in the UWB anchors' world frame (x, y metres, psi = heading of the body x
 * axis, + = left/CCW), with the forward speed v, the gyro's yaw-rate bias
 * b_g and the odometer's scale k_w (true speed = k_w * odometer speed).
 *
 *   predict  x += v cos(psi) dt, y += v sin(psi) dt,
 *            psi += (gyro_z - b_g) dt, v += a_x dt
 *   updates  odometer speed  z = v / k_w
 *            optical flow    z = v           (when the flow sensor tracks)
 *            UWB range       z = |tag - anchor|, tightly coupled, gated
 *                            by its Mahalanobis distance; the tag sits at a
 *                            lever arm from the body origin
 *            zero velocity   at rest: v = 0 and gyro_z = b_g
 *
 * Every update is scalar, so nothing is inverted. Before it can run, the
 * filter needs a pose. It records the odometer/gyro track in a local frame
 * (the start = origin, heading 0) together with every UWB range. Once the
 * car has travelled `init_travel_m`, a search over the heading offset (with
 * a Gauss-Newton fit of the start position at each candidate) finds the
 * transform that best explains the ranges. No start pose is needed, and the
 * car need not stand still first.
 *
 * Portable C11, float32, no allocation.
 */
#ifndef TT_NAV_H
#define TT_NAV_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

enum { TT_NAV_X = 0, TT_NAV_Y, TT_NAV_PSI, TT_NAV_V, TT_NAV_BG, TT_NAV_KW, TT_NAV_N };
enum { TT_NAV_WAIT_INIT = 0, TT_NAV_RUN = 1 };
#define TT_NAV_MAX_REC 96

typedef struct {
    float q_accel;        /* v random walk from accel noise, m/s^2/sqrt(Hz) */
    float q_gyro;         /* heading noise, rad/s/sqrt(Hz)                  */
    float q_bias;         /* gyro bias walk, rad/s/sqrt(s)                  */
    float q_scale;        /* odometer scale walk, 1/sqrt(s)                 */
    float r_speed;        /* odometer speed sigma, m/s                      */
    float r_flow;         /* flow speed sigma, m/s                          */
    float r_range;        /* UWB line-of-sight sigma, m                     */
    float r_gyro_rest;    /* gyro sigma for the at-rest bias update, rad/s  */
    float nlos_db;        /* power gap above which a range is likely NLOS   */
    float nlos_scale;     /* sigma multiplier for a likely-NLOS range       */
    float gate;           /* Mahalanobis gate, innovation^2 / S             */
    float lever[2];       /* UWB tag in the body frame (x fwd, y left), m   */
    float tag_z;          /* tag height above the ground plane, m           */
    float init_travel_m;  /* odometer travel before the heading search      */
    float rest_speed;     /* below this the car is at rest, m/s             */
} TtNavCfg;

/* One UWB range: the anchor's surveyed position and the measurement. */
typedef struct { float anchor[3]; float range_m; float nlos_db; } TtNavRange;

typedef struct {
    TtNavCfg cfg;
    uint8_t  stage;
    uint8_t  pad_[3];
    float    s[TT_NAV_N];
    float    P[TT_NAV_N * TT_NAV_N];
    /* initialisation: local dead-reckoned track and the ranges along it */
    float    lx, ly, lpsi, travel;
    int      rec_n;
    float    rec_t[TT_NAV_MAX_REC][2];     /* tag, local frame             */
    float    rec_a[TT_NAV_MAX_REC][3];
    float    rec_r[TT_NAV_MAX_REC];
    float    init_rms;                     /* range residual of the fit, m */
    uint32_t n_used, n_gated;
} TtNav;

void tt_nav_default_cfg(TtNavCfg *c);
void tt_nav_reset(TtNav *n, const TtNavCfg *c);

/* One control tick. gyro_z (rad/s, + = left) and accel_x (m/s^2) in the body
 * frame; v_odo the odometer speed; v_flow the flow speed when flow_ok. */
void tt_nav_step(TtNav *n, float dt, float gyro_z, float accel_x,
                 float v_odo, float v_flow, int flow_ok,
                 const TtNavRange *ranges, int n_ranges);

/* Heading in (-pi, pi]. */
float tt_nav_wrap(float a);

#ifdef __cplusplus
}
#endif

#endif /* TT_NAV_H */
