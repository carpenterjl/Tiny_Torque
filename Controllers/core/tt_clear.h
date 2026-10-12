/*
 * tt_clear.h — the clear distance ahead, from a multizone ToF (NAV-05).
 *
 * A multizone ranger (VL53L5CX class) returns one distance per zone of a
 * square field of view. This turns a frame of it into one number the speed
 * loop can use: how far along the car's path the way is clear.
 *
 *   1. Each valid zone's return becomes a point in the body frame (FLU: x
 *      forward, y left, z up from the body origin): the zone's direction in
 *      the sensor's frame, the sensor's mount pose, the range.
 *   2. Points on the floor are dropped: the body's pitch (nose down +,
 *      from the gyro) tilts them, and whatever then lies within
 *      `floor_margin_m` of the floor is the floor. A nose-down car under
 *      braking sees the floor in its lower zones at a metre; this is why
 *      the old single-beam abort had to be debounced. So are points above
 *      `roof_z_m`: a banner the car passes under.
 *   3. The rest are measured against the path ahead, a polyline in the body
 *      frame from the car's origin: a point within `half_width_m` of it
 *      (the car's half width plus a margin) is in the way, and the shortest
 *      along-path distance of such a point is the clearance.
 *
 * Portable C11, float32, no allocation; a few thousand flops per frame.
 */
#ifndef TT_CLEAR_H
#define TT_CLEAR_H

#include "tt_types.h"

#ifdef __cplusplus
extern "C" {
#endif

#define TT_CLEAR_MAX_PATH 64u    /* path points the corridor is built on */
#define TT_CLEAR_NONE     1.0e6f /* clearance when nothing is in the way   */

typedef struct {
    float pos_m[3];        /* the sensor from the body origin, FLU        */
    float rpy_rad[3];      /* its mount: roll, pitch, yaw (Z-Y-X)         */
    float fov_rad;         /* square field of view                        */
    int   zones_side;      /* zones per side (N: N*N returns per frame)   */
    float half_width_m;    /* corridor half width about the path          */
    float floor_z_m;       /* the floor below the body origin at rest (-) */
    float floor_margin_m;  /* a return this close to the floor is the floor */
    float roof_z_m;        /* above this (body z) the car passes under it;
                              0 = no ceiling                               */
    float max_range_m;     /* beyond this a return is the sensor's "none" */
} TtClearCfg;

typedef struct { float x, y; } TtClearPt;   /* body frame, m */

typedef struct {
    float clear_m;         /* along the path to the nearest point in the
                              way, from the path's first point (put it at
                              the body origin); TT_CLEAR_NONE = nothing  */
    float y_m;             /* that point's offset from the path, + = left */
    int   n_in;            /* returns in the corridor                     */
    int   n_floor;         /* returns dropped as the floor                */
    int   n_valid;         /* returns with a target at all                */
} TtClearOut;

/* The zone's direction in the SENSOR frame (x = aim, y left, z up): row 0
 * is the top, col 0 the left, looking along the aim. */
void tt_clear_zone_dir(const TtClearCfg *c, int row, int col, float dir[3]);

/* One frame. `path` runs from the car (its first point at the body
 * origin) forward; 2 <= n <= TT_CLEAR_MAX_PATH. pitch_rad is the body's,
 * nose down +. Returns out->clear_m. */
float tt_clear_step(const TtClearCfg *c, const TtTof *tof, float pitch_rad,
                    const TtClearPt *path, int n, TtClearOut *out);

#ifdef __cplusplus
}
#endif

#endif /* TT_CLEAR_H */
