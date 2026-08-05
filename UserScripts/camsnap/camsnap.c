/*
 * camsnap.c — save a camera frame to disk, so you can look at what your
 * controller is actually seeing.
 *
 * A controller that steers from an image is debugging blind: the graphs show
 * you what it decided, never what it was looking at. This writes one frame out
 * as a file you can open, and as a text array you can paste into a spreadsheet
 * or a Python script.
 *
 * It drives nothing. Build it, pick it in the Controller row, press
 * Run controller ▶, and after a second or so two files appear:
 *
 *     camsnap.pgm   the image. Binary PGM (P5) — GIMP, IrfanView, ImageMagick,
 *                   Photoshop and Python's Pillow all open it directly.
 *     camsnap.txt   the same pixels as numbers: one header line, then one row
 *                   per line, 0..255, left to right, TOP row first.
 *
 * ─── where the files land ─────────────────────────────────────────────────
 *
 * The paths are relative, so they land in the game's working directory: the
 * Unity project folder (UnitySim/) in the editor, and next to the executable
 * in a build. If you would rather choose, put an absolute path in OUT_DIR
 * below — "C:/Users/you/Desktop/" — and keep the trailing slash.
 *
 * ─── what it demonstrates ─────────────────────────────────────────────────
 *
 * The frame is taken from a kept TtFrame, not from in->cam_pixels. That is the
 * point of TtCamera: the live pointer is the game's own buffer and it is dead
 * the moment ctrl_step returns, so anything that wants to hold a picture — to
 * write it out, to compare it with the next one, to scan it over several ticks
 * — has to copy it first. What lands in the file is f->px[y][x], byte for byte
 * what your control law would be reading.
 */

#include "tt_controller.h"
#include <stdio.h>

/* ═══════════════════════════════ TUNING ═══════════════════════════════════ */

/* Which frame to save. 1 is the first one the camera ever produced, which on
 * some maps is still mid-fade-in; a dozen frames is about a second at 10 Hz and
 * is past anything transient. Raise it to catch a later moment. */
static const unsigned long SNAP_ON_FRAME = 12;

/* Trailing slash, or "" for the working directory. */
#define OUT_DIR ""

/* ═══════════════════════════════ STATE ════════════════════════════════════ */

static TtCamera g_cam;      /* the kept frames — ~64 KB of static */
static int      g_saved;    /* 0 until the files are written */

enum { DBG_SEQ = 0, DBG_AGE, DBG_NEW, DBG_SAVED, DBG_COUNT };

/* ═══════════════════════════════ SETUP ════════════════════════════════════ */

CTRL_EXPORT int ctrl_get_vehicle(void) { return CTRL_VEHICLE_MENU; }

CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    TT_UNUSED(control_rate_hz);
    tt_cam_init(&g_cam);
    g_saved = 0;
    return 0;
}

/* The image does not arrive through the manifest, so there is nothing to
 * unpack here. A controller that also reads sensors would call
 * tt_car_configure; see my_controller.c. */
CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    TT_UNUSED(sensors); TT_UNUSED(count);
}

CTRL_EXPORT void ctrl_shutdown(void) { }

CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    return "frame_seq,frame_age_s,is_new,saved";
}

/* ═══════════════════════════════ SAVING ═══════════════════════════════════ */

/*
 * Binary PGM: the simplest image format that is still a real one. Three lines
 * of ASCII header, then width*height raw bytes, one per pixel, top row first —
 * which is exactly how a TtFrame already stores it, so each row is one fwrite
 * with nothing to convert.
 */
static void write_pgm(const TtFrame* f, const char* path) {
    FILE* fp = fopen(path, "wb");        /* "wb": the pixels are not text */
    if (fp == 0) return;                 /* no such folder, no permission */
    fprintf(fp, "P5\n%d %d\n255\n", f->width, f->height);
    for (int y = 0; y < f->height; y++)
        fwrite(f->px[y], 1, (size_t)f->width, fp);
    fclose(fp);
}

/* The same pixels as numbers, for when you want to look at values rather than
 * at a picture. One header line so a reader knows the shape it is getting. */
static void write_txt(const TtFrame* f, const char* path) {
    FILE* fp = fopen(path, "w");
    if (fp == 0) return;
    fprintf(fp, "width %d height %d seq %lu time_s %.3f\n",
            f->width, f->height, f->seq, (double)f->time_s);
    for (int y = 0; y < f->height; y++) {
        for (int x = 0; x < f->width; x++)
            fprintf(fp, "%s%d", x ? " " : "", (int)f->px[y][x]);
        fputc('\n', fp);
    }
    fclose(fp);
}

/* ═════════════════════════════ THE LOOP ═══════════════════════════════════ */

CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    memset(out, 0, sizeof(*out));
    if (in == 0) return;

    /* True only when the picture actually changed. The camera captures at
     * ~10 Hz and this runs at 100, so without the test everything below would
     * happen ten times per frame — including, once, writing the file. */
    int fresh = tt_cam_update(&g_cam, in);
    const TtFrame* f = tt_cam_newest(&g_cam);

    if (fresh && !g_saved && f != 0 && f->seq >= SNAP_ON_FRAME) {
        write_pgm(f, OUT_DIR "camsnap.pgm");
        write_txt(f, OUT_DIR "camsnap.txt");
        g_saved = 1;                     /* once per load, not once per frame */
    }

    /* Nothing is written to actuator[], so the car sits still. Add your own
     * control law here — the frames are already being kept for it. */

    out->debug[DBG_SEQ]   = f ? (float)f->seq : -1.0f;
    out->debug[DBG_AGE]   = tt_cam_age(&g_cam, in);
    out->debug[DBG_NEW]   = (float)fresh;
    out->debug[DBG_SAVED] = (float)g_saved;
}
