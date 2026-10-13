/*
 * tt_grid.h — a rolling local occupancy grid (NAV-08).
 *
 * A square window of cells in the site frame, TT_GRID_N a side, that
 * follows the car: tt_grid_centre() moves the window and keeps what is
 * still inside it (the storage is a ring in both axes, so cells never move;
 * the ones that leave are cleared). Each cell counts how often a ranger's
 * return has landed in it, less how often a ray has passed through it:
 * rays from the multizone ToF (core/tt_clear.h gives them in the body
 * frame) raise the cell they end on when they end on something in the way,
 * and lower every cell they cross while they are within the band of
 * heights the car cannot pass (the floor's margin to the roof it passes
 * under). A cell at or above `occ_thr` is occupied; a return that stops
 * coming (the box was taken away) fades in a few frames.
 *
 * Cells outside the window are unknown, and the planner treats unknown as
 * free: the window reaches as far as the ranger does.
 *
 * Portable C11, float32, no allocation: TT_GRID_N^2 bytes.
 */
#ifndef TT_GRID_H
#define TT_GRID_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TT_GRID_N 96u    /* cells a side: 4.8 m at 5 cm                     */

typedef struct {
    float    cell_m;
    int32_t  wx0, wy0;    /* site cell index of the window's low corner      */
    uint8_t  placed;      /* centred at least once                           */
    uint8_t  hit_inc;     /* a return in the cell adds this                  */
    uint8_t  free_dec;    /* a ray through the cell takes this               */
    uint8_t  occ_thr;     /* at or above: occupied                           */
    uint8_t  occ_max;     /* the count saturates here                        */
    uint8_t  pad_[3];
    uint8_t  occ[TT_GRID_N * TT_GRID_N];
} TtGrid;

/* Empty, with this cell size and the default counts (2 per hit, 1 per
 * pass, occupied at 3, saturating at 6). */
void tt_grid_init(TtGrid *g, float cell_m);

/* Forget every cell; the window stays. */
void tt_grid_clear(TtGrid *g);

/* Move the window so that (x, y) is its centre. Cells that stay inside keep
 * their counts; the rest are cleared. */
void tt_grid_centre(TtGrid *g, float x, float y);

/* One ray from (x0, y0, z0) to (x1, y1, z1), site frame: every cell it
 * crosses while z is within [z_lo, z_hi] is lowered, and when `hit` the cell
 * it ends in is raised. z is the height over the body origin's floor
 * level, pitch already taken out. */
void tt_grid_ray(TtGrid *g, float x0, float y0, float z0, float x1, float y1, float z1,
                 float z_lo, float z_hi, int hit);

/* The count of the cell holding (x, y); -1 outside the window. */
int tt_grid_at(const TtGrid *g, float x, float y);

/* 1 if any occupied cell's centre lies within r of (x, y). */
int tt_grid_occupied(const TtGrid *g, float x, float y, float r);

/* The occupied cells within r of (x, y), and their centroid: returns
 * their number (0 = none, and cx, cy untouched). */
int tt_grid_centroid(const TtGrid *g, float x, float y, float r, float *cx, float *cy);

/* Occupied cells in the window. */
uint32_t tt_grid_count(const TtGrid *g);

#ifdef __cplusplus
}
#endif

#endif /* TT_GRID_H */
