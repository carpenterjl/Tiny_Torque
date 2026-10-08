/*
 * tt_alloc.h — electronic differential and torque allocation (FW-06).
 *
 * A car with one motor per wheel has no mechanical differential: the
 * firmware decides every wheel's torque. The control layers above ask for
 * what the CAR should do — a longitudinal force and a yaw moment
 * (TtVehReq) — and this turns that into wheel torques:
 *
 *   1. Front/rear split by estimated axle load (static share plus the load
 *      transfer of the current acceleration), when both axles are driven.
 *   2. Left/right split per axle: equal halves (an open differential) plus
 *      the yaw moment as a force couple across the track.
 *   3. Per-wheel limits, motoring and regen separately.
 *   4. A slip limiter (traction control driving, ABS braking) against each
 *      wheel's kinematic speed reference from the vehicle speed and yaw rate.
 *   5. A slew limit through zero torque, so a geared motor crosses its gear
 *      lash gently instead of slamming the teeth.
 *
 * Portable C11, float32, no allocation. State is per car (the slip cuts and
 * the last torques).
 */
#ifndef TT_ALLOC_H
#define TT_ALLOC_H

#include "tt_types.h"
#include "tt_params.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    float cut[TT_MAX_WHEELS];      /* slip-limiter torque scale, 0..1       */
    float t_prev[TT_MAX_WHEELS];   /* last output, for the zero-crossing slew */
    float slip[TT_MAX_WHEELS];     /* last measured slip, for telemetry      */
    uint8_t limiting;              /* bit w set = wheel w was cut this tick  */
    uint8_t pad_[3];
} TtAlloc;

/* What the allocator knows about the car's motion this tick. */
typedef struct {
    float v_mps;                   /* vehicle speed at the rear-axle centre  */
    float yaw_rate;                /* rad/s, FLU (+ = left)                  */
    float ax_mps2;                 /* longitudinal acceleration estimate     */
    float wheel_omega[TT_MAX_WHEELS]; /* rad/s at the wheel, + = forward      */
    uint8_t omega_valid[TT_MAX_WHEELS];
} TtAllocIn;

void tt_alloc_reset(TtAlloc *a);

/* Fill out->wheel_torque_nm from the request. Wheels not in driven_mask get
 * zero. dt is the tick period (s). */
void tt_alloc_step(TtAlloc *a, const TtParams *p, const TtVehReq *req,
                   const TtAllocIn *in, float dt, TtCmd *out);

/* Driven-wheel lash-crossing slew (N·m/s) and the band around zero where it
 * applies (N·m). 0 = off. Constants rather than parameters until a real
 * gearbox has been measured (ACT-07). */
#define TT_ALLOC_LASH_BAND_NM   0.02f
#define TT_ALLOC_LASH_SLEW_NMS  4.0f

#ifdef __cplusplus
}
#endif

#endif /* TT_ALLOC_H */
