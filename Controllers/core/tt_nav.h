/*
 * tt_nav.h — planar navigation filter: UWB + odometry + gyro (+ flow) EKF.
 *
 * The L3 estimator of the plan's control stack (§7.1): a 7-state EKF
 *
 *     s = [x, y, psi, v, b_g, k_w, b_r]
 *
 * in the UWB anchors' world frame (x, y metres, psi = heading of the body x
 * axis, + = left/CCW), with the forward speed v, the gyro's yaw-rate bias
 * b_g, the odometer's scale k_w (true speed = k_w * odometer speed) and the
 * tag's range bias b_r (its antenna delay: every range reads b_r long).
 *
 *   predict  x += v cos(psi + beta) dt, y += v sin(psi + beta) dt,
 *            psi += (gyro_z - b_g) dt, v += a_x dt
 *            beta, the sideslip of the body origin: in a turn it does not
 *            move along the heading. Kinematically it moves as a point
 *            -rear_axle_x ahead of the rear axle, atan(-rear_axle_x * kappa)
 *            (2 deg at 0.15 m on a 4 m circle); the rear tyres' slip angle,
 *            a_y / (g * corner_k), points it outward. Left out, the ranges see
 *            the course, the gyro the heading, and the filter reconciles them
 *            by moving its gyro bias through every turn (NAV-06: 2 deg of
 *            heading and 15 cm of position after each half-lap).
 *   updates  odometer speed  z = v * g / k_w, g the odometry axle's speed
 *                            over the origin's: in a turn the front axle runs
 *                            on a wider circle, sqrt(1 + (d^2 - l^2) kappa^2)
 *                            with d and l the odometry axle's and the origin's
 *                            distances ahead of the rear axle (1.5 % on a
 *                            1.5 m hairpin)
 *            optical flow    z = v           (when the flow sensor tracks)
 *            UWB range       z = |tag - anchor| + b_r, tightly coupled, gated
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

enum { TT_NAV_X = 0, TT_NAV_Y, TT_NAV_PSI, TT_NAV_V, TT_NAV_BG, TT_NAV_KW, TT_NAV_BR, TT_NAV_N };
enum { TT_NAV_WAIT_INIT = 0, TT_NAV_RUN = 1 };
/* The seeded start's cross-check (tt_nav_seed). */
enum { TT_NAV_CHECK_NONE = 0, TT_NAV_CHECK_WAIT = 1, TT_NAV_CHECK_OK = 2, TT_NAV_CHECK_BAD = 3 };
#define TT_NAV_MAX_REC 96

typedef struct {
    float q_accel;        /* v random walk from accel noise, m/s^2/sqrt(Hz) */
    float q_gyro;         /* heading noise, rad/s/sqrt(Hz)                  */
    float q_bias;         /* gyro bias walk, rad/s/sqrt(s)                  */
    float q_scale;        /* odometer scale walk, 1/sqrt(s)                 */
    float r_speed;        /* odometer speed sigma, m/s                      */
    float r_flow;         /* flow speed sigma, m/s                          */
    float r_range;        /* UWB line-of-sight sigma, m                     */
    float r_bias0;        /* UWB range bias sigma before it is learned, m   */
    float r_gyro_rest;    /* gyro sigma for the at-rest bias update, rad/s  */
    float nlos_db;        /* power gap above which a range is likely NLOS   */
    float nlos_scale;     /* sigma multiplier for a likely-NLOS range       */
    float gate;           /* Mahalanobis gate, innovation^2 / S             */
    float lever[2];       /* UWB tag in the body frame (x fwd, y left), m   */
    float tag_z;          /* tag height above the ground plane, m           */
    float init_travel_m;  /* odometer travel before the heading search      */
    float init_rms_max;   /* the search accepts a fit whose kept ranges (the
                             ones within 0.5 m of it) have this rms, m ...  */
    float init_inlier_min;/* ... and are this share of all recorded (FW-20) */
    float rest_speed;     /* below this the car is at rest, m/s             */
    float rear_axle_x;    /* rear axle in the body frame (x fwd), m; 0 = the
                             origin is on it (no kinematic sideslip)        */
    float corner_k;       /* rear tyres' cornering stiffness per load,
                             C_alpha / F_z, 1/rad; 0 = no tyre sideslip     */
    float odo_axle_x;     /* the odometry wheels' axle in the body frame
                             (x fwd), m; only with rear_axle_x              */
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
    float    init_rms;                     /* range residual of the fit over the
                                              ranges it keeps (under 0.5 m), m */
    float    init_inlier;                  /* the share of ranges it keeps  */
    uint32_t n_used, n_gated;
    /* seeded start (NAV-04): the heading search still runs once over the
     * first init_travel_m, as a check on where the car was said to be */
    uint8_t  check;                        /* TT_NAV_CHECK_*               */
    uint8_t  pad2_[3];
    float    check_dpsi, check_dpos;       /* search - filter, rad and m   */
} TtNav;

void tt_nav_default_cfg(TtNavCfg *c);
void tt_nav_reset(TtNav *n, const TtNavCfg *c);

/* One control tick. gyro_z (rad/s, + = left) and accel_x (m/s^2) in the body
 * frame; v_odo the odometer speed; v_flow the flow speed when flow_ok. */
void tt_nav_step(TtNav *n, float dt, float gyro_z, float accel_x,
                 float v_odo, float v_flow, int flow_ok,
                 const TtNavRange *ranges, int n_ranges);

/* Start running at a known pose (a route's surveyed start: NAV-04) instead
 * of searching for one: x, y, psi in the site frame, with these standard
 * deviations; the speed and the gyro bias start at 0 and are learned (the car
 * stands still). The local track is still recorded, and once the car has
 * travelled init_travel_m the heading search runs on it: if the pose it finds
 * disagrees with the filter's by more than check_psi_rad or check_pos_m, the
 * seed was wrong and n->check reads TT_NAV_CHECK_BAD (the caller decides). */
void tt_nav_seed(TtNav *n, float x, float y, float psi, float sig_pos, float sig_psi);
#define TT_NAV_CHECK_PSI_RAD 0.175f   /* 10 deg */
#define TT_NAV_CHECK_POS_M   0.50f

/* The body origin's sideslip (rad, + = moving left of the heading) at
 * speed v and yaw rate `rate`, for an origin -rear_axle_x ahead of the rear
 * axle on tyres of corner_k = C_alpha / F_z (see the predict step above).
 * The path tracker steers this course too (NAV-02): on a curve the right
 * heading is not the route's tangent. */
float tt_nav_sideslip(float rear_axle_x, float corner_k, float v, float rate);

/* Heading in (-pi, pi]. */
float tt_nav_wrap(float a);

#ifdef __cplusplus
}
#endif

#endif /* TT_NAV_H */
