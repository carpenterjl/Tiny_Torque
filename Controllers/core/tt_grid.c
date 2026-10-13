/*
 * tt_grid.c — a rolling local occupancy grid (NAV-08). See tt_grid.h.
 */
#include "tt_grid.h"

#include <math.h>
#include <string.h>

#define N ((int32_t)TT_GRID_N)

static int32_t cell_of(float v, float cell_m)
{
    return (int32_t)floorf(v / cell_m);
}

/* Storage slot of a site cell index: a ring in each axis. */
static int32_t wrap(int32_t i)
{
    int32_t m = i % N;
    return m < 0 ? m + N : m;
}

static int inside(const TtGrid *g, int32_t ix, int32_t iy)
{
    return g->placed && ix >= g->wx0 && ix < g->wx0 + N && iy >= g->wy0 && iy < g->wy0 + N;
}

static uint8_t *slot(TtGrid *g, int32_t ix, int32_t iy)
{
    return &g->occ[(size_t)wrap(iy) * TT_GRID_N + (size_t)wrap(ix)];
}

void tt_grid_init(TtGrid *g, float cell_m)
{
    memset(g, 0, sizeof(*g));
    g->cell_m = cell_m > 0.0f ? cell_m : 0.05f;
    g->hit_inc = 2;
    g->free_dec = 1;
    g->occ_thr = 3;
    g->occ_max = 6;
}

void tt_grid_clear(TtGrid *g)
{
    memset(g->occ, 0, sizeof(g->occ));
}

/* Clear the columns ix in [a, b) (every row), or the rows iy in [a, b). */
static void clear_cols(TtGrid *g, int32_t a, int32_t b)
{
    int32_t i, j;
    if (b - a >= N) { tt_grid_clear(g); return; }
    for (i = a; i < b; i++) {
        int32_t sx = wrap(i);
        for (j = 0; j < N; j++) g->occ[(size_t)j * TT_GRID_N + (size_t)sx] = 0;
    }
}

static void clear_rows(TtGrid *g, int32_t a, int32_t b)
{
    int32_t j;
    if (b - a >= N) { tt_grid_clear(g); return; }
    for (j = a; j < b; j++) memset(&g->occ[(size_t)wrap(j) * TT_GRID_N], 0, TT_GRID_N);
}

void tt_grid_centre(TtGrid *g, float x, float y)
{
    int32_t nx0 = cell_of(x, g->cell_m) - N / 2, ny0 = cell_of(y, g->cell_m) - N / 2;
    if (!g->placed) {
        tt_grid_clear(g);
        g->placed = 1;
    } else {
        /* The columns that leave: the window moved +x by d, so the d
         * lowest columns are gone (and the d new ones at the top reuse
         * their slots); -x the other way round. */
        if (nx0 > g->wx0)      clear_cols(g, g->wx0, nx0);
        else if (nx0 < g->wx0) clear_cols(g, nx0 + N, g->wx0 + N);
        if (ny0 > g->wy0)      clear_rows(g, g->wy0, ny0);
        else if (ny0 < g->wy0) clear_rows(g, ny0 + N, g->wy0 + N);
    }
    g->wx0 = nx0;
    g->wy0 = ny0;
}

void tt_grid_ray(TtGrid *g, float x0, float y0, float z0, float x1, float y1, float z1,
                 float z_lo, float z_hi, int hit)
{
    float dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
    float len = sqrtf(dx * dx + dy * dy), step, t;
    int32_t ex = cell_of(x1, g->cell_m), ey = cell_of(y1, g->cell_m);
    int32_t lx = cell_of(x0, g->cell_m) - 1, ly = 0;
    int n, k;
    if (!g->placed) return;
    /* Half a cell per step never skips a cell the ray crosses for long. */
    step = 0.5f * g->cell_m;
    n = (int)ceilf(len / step);
    if (n < 1) n = 1;
    for (k = 0; k < n; k++) {
        int32_t ix, iy;
        float z;
        t = (float)k / (float)n;
        ix = cell_of(x0 + t * dx, g->cell_m);
        iy = cell_of(y0 + t * dy, g->cell_m);
        if (ix == lx && iy == ly) continue;
        lx = ix; ly = iy;
        if (hit && ix == ex && iy == ey) break;      /* the end cell is the hit's */
        z = z0 + t * dz;
        if (z < z_lo || z > z_hi) continue;          /* over or under what the car meets */
        if (!inside(g, ix, iy)) continue;
        {
            uint8_t *c = slot(g, ix, iy);
            *c = (uint8_t)(*c > g->free_dec ? *c - g->free_dec : 0);
        }
    }
    if (hit && inside(g, ex, ey)) {
        uint8_t *c = slot(g, ex, ey);
        int v = (int)*c + (int)g->hit_inc;
        *c = (uint8_t)(v > (int)g->occ_max ? (int)g->occ_max : v);
    }
}

int tt_grid_at(const TtGrid *g, float x, float y)
{
    int32_t ix = cell_of(x, g->cell_m), iy = cell_of(y, g->cell_m);
    if (!inside(g, ix, iy)) return -1;
    return (int)g->occ[(size_t)wrap(iy) * TT_GRID_N + (size_t)wrap(ix)];
}

int tt_grid_centroid(const TtGrid *g, float x, float y, float r, float *cx, float *cy)
{
    int32_t ix0 = cell_of(x - r, g->cell_m), ix1 = cell_of(x + r, g->cell_m);
    int32_t iy0 = cell_of(y - r, g->cell_m), iy1 = cell_of(y + r, g->cell_m);
    int32_t ix, iy;
    float r2 = r * r, sx = 0.0f, sy = 0.0f;
    int n = 0;
    if (!g->placed) return 0;
    if (ix0 < g->wx0) ix0 = g->wx0;
    if (iy0 < g->wy0) iy0 = g->wy0;
    if (ix1 > g->wx0 + N - 1) ix1 = g->wx0 + N - 1;
    if (iy1 > g->wy0 + N - 1) iy1 = g->wy0 + N - 1;
    for (iy = iy0; iy <= iy1; iy++) {
        float cyy = ((float)iy + 0.5f) * g->cell_m;
        for (ix = ix0; ix <= ix1; ix++) {
            float cxx = ((float)ix + 0.5f) * g->cell_m;
            if ((cxx - x) * (cxx - x) + (cyy - y) * (cyy - y) > r2) continue;
            if (g->occ[(size_t)wrap(iy) * TT_GRID_N + (size_t)wrap(ix)] < g->occ_thr) continue;
            sx += cxx; sy += cyy; n++;
        }
    }
    if (n == 0) return 0;
    *cx = sx / (float)n;
    *cy = sy / (float)n;
    return n;
}

int tt_grid_occupied(const TtGrid *g, float x, float y, float r)
{
    int32_t ix0 = cell_of(x - r, g->cell_m), ix1 = cell_of(x + r, g->cell_m);
    int32_t iy0 = cell_of(y - r, g->cell_m), iy1 = cell_of(y + r, g->cell_m);
    int32_t ix, iy;
    float r2 = r * r;
    if (!g->placed) return 0;
    if (ix0 < g->wx0) ix0 = g->wx0;
    if (iy0 < g->wy0) iy0 = g->wy0;
    if (ix1 > g->wx0 + N - 1) ix1 = g->wx0 + N - 1;
    if (iy1 > g->wy0 + N - 1) iy1 = g->wy0 + N - 1;
    for (iy = iy0; iy <= iy1; iy++) {
        float cy = ((float)iy + 0.5f) * g->cell_m - y;
        for (ix = ix0; ix <= ix1; ix++) {
            float cx = ((float)ix + 0.5f) * g->cell_m - x;
            if (cx * cx + cy * cy > r2) continue;
            if (g->occ[(size_t)wrap(iy) * TT_GRID_N + (size_t)wrap(ix)] >= g->occ_thr) return 1;
        }
    }
    return 0;
}

uint32_t tt_grid_count(const TtGrid *g)
{
    uint32_t i, n = 0;
    for (i = 0; i < TT_GRID_N * TT_GRID_N; i++) if (g->occ[i] >= g->occ_thr) n++;
    return n;
}
