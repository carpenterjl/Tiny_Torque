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
 *      the old single-beam abort had to be debounced. Where the floor is,
 *      and the angle its returns come in at, can be learned as the car runs
 *      (TtClearFloor, below); without it, the floor is `floor_z_m` and a
 *      return lies on its zone's centre. So are points above
 *      `roof_z_m`: a banner the car passes under. The roof is judged on the
 *      zone's LOWER edge, not its centre: a return anywhere in the zone is
 *      reported at the centre, and a barrier 0.25 m tall at 3 m lies in
 *      the row whose centre points over the roof (NAV-08 needs it seen
 *      there; the floor's returns lie on a ray of their own, below).
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

/* The floor as the ranger sees it (FW-25). A zone reports one range for
 * a patch of the field of view: for something standing up, the part of it
 * nearest the car; for the floor, the patch's lower part, which is nearer
 * and brighter than its centre. Put on the zone's centre, the floor reads
 * high, by the range times that angle (the twin's 8x8 at 45 deg: 18 mm at
 * 0.75 m, 24 at a metre), and a mount a degree off is 13 mm more. So the
 * floor's returns are put on a ray `dip_rad` below their zone's centre, and
 * the floor at `floor_z_m`: at first a quarter zone (the middle of the
 * zone's nearer half) and the cfg's floor, then both fitted to the returns
 * while the car runs
 * steadily: a least-squares fit forgetting as it goes, of the returns that
 * already lie within the floor margin, within TT_CLEAR_FLOOR_NEAR_M of the
 * sensor (where the floor comes back steeply, and a return far off would
 * outweigh the rest), and at the range the zone gave the frame before.
 * The same two numbers hold in every row, so the zones that see the floor
 * only when the nose dips under the brake are judged with what the cruise
 * taught. Something standing up is not in the fit: its returns lie above
 * the margin, or close on the car frame by frame where the floor's stay
 * put. */
#define TT_CLEAR_FLOOR_NEAR_M  1.0f   /* m: farther returns are not learned from */
#define TT_CLEAR_FLOOR_STILL_M 0.04f  /* m: a return that moved more since the
                                         frame before is not the floor (so the
                                         car must move more than this a frame) */
typedef struct {
    float floor_z_m;       /* the floor below the body origin (-)         */
    float dip_rad;         /* a floor return's ray below its zone's centre */
    float w;               /* the returns behind them (forgetting); 0 = the
                              prior: the cfg's floor, a quarter zone       */
    float s1, sg, sgg, sy, sgy;   /* the fit's sums                       */
    float d_prev[TT_TOF_MAX_ZONES];  /* each zone's range the frame before (0: none) */
} TtClearFloor;

/* The prior: the cfg's floor, a quarter zone below the zones' centres. */
void tt_clear_floor_init(TtClearFloor *f, const TtClearCfg *c);

/* One frame into the fit, taken while the car runs steadily (the caller
 * knows when: no braking, no turning, and moving more than
 * TT_CLEAR_FLOOR_STILL_M a frame). `keep` is what the returns already in it
 * keep of their weight (exp(-frame/tau)). Returns the returns it took. */
int tt_clear_floor_learn(TtClearFloor *f, const TtClearCfg *c, const TtTof *tof,
                         float pitch_rad, float keep);

/* The zone's direction in the SENSOR frame (x = aim, y left, z up): row 0
 * is the top, col 0 the left, looking along the aim. */
void tt_clear_zone_dir(const TtClearCfg *c, int row, int col, float dir[3]);

/* One frame. `path` runs from the car (its first point at the body
 * origin) forward; 2 <= n <= TT_CLEAR_MAX_PATH. pitch_rad is the body's,
 * nose down +. `fl` is the floor learned, or 0 for the prior. Returns
 * out->clear_m. */
float tt_clear_step(const TtClearCfg *c, const TtClearFloor *fl, const TtTof *tof,
                    float pitch_rad, const TtClearPt *path, int n, TtClearOut *out);

/* One zone's return as a ray for a map (NAV-08, tt_grid.h), in the body
 * frame: from the sensor (`org`) to where the return is (`end`), or to the
 * sensor's range when there is none. *hit = 1 when the return is something
 * in the way (not the floor, not above the roof). Returns 0 when the zone
 * has nothing to tell (no frame, or no range to reach). */
int tt_clear_ray(const TtClearCfg *c, const TtClearFloor *fl, const TtTof *tof,
                 float pitch_rad, int zone, float org[3], float end[3], int *hit);

#ifdef __cplusplus
}
#endif

#endif /* TT_CLEAR_H */
