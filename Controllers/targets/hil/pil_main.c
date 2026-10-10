/*
 * pil_main.c — the Opus firmware as a host process in external-tick mode
 * (HIL-03 without a board).
 *
 * The same code an MCU image runs in lockstep HIL (hil_dev + opus_hil +
 * opus_app), with USB CDC replaced by stdin / stdout. The lockstep bridge
 * (tools/tt_bridge.c) starts it as a child process and talks the wire
 * protocol to it exactly as it would to a board on a serial port, so
 *
 *     Unity  <-pipe->  tt_bridge  <-wire over pipes->  opus_foc_pil.exe
 *
 * proves the whole link — framing, packing, ordering, the bridge, Unity's
 * lockstep wait — before there is a board, and must reproduce the DLL's run
 * exactly. Built twice, like the DLLs: opus_pil (brushed Opus Vector) and
 * opus_foc_pil (the FOC twin), picked by OPUS_PARAMS.
 *
 * Exits when stdin closes.
 *
 * Test hook: TT_PIL_STALL_EVERY=N and TT_PIL_STALL_MS=M in the environment
 * make every Nth tick take M ms longer — a firmware that misses deadlines,
 * for exercising the bridge's and the sim's handling of late answers.
 */
#include "opus_hil.h"

#include <stdio.h>
#include <stdlib.h>

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#define SLEEP_MS(ms) Sleep((DWORD)(ms))
#define READ  _read
#define WRITE _write
#else
#include <unistd.h>
#define SLEEP_MS(ms) usleep((useconds_t)(ms) * 1000u)
#define READ  read
#define WRITE write
#endif

#ifndef OPUS_PARAMS
#define OPUS_PARAMS tt_params_opus_vector
#endif
#ifndef OPUS_PIL_NAME
#define OPUS_PIL_NAME "opus_pil"
#endif

static HilApp g_app;
static long   g_stall_every, g_stall_ms, g_ticks;

static void stall_step(void *ctx, const TtMeas *m, TtAct *act)
{
    g_app.step(ctx, m, act);
    if (g_stall_every > 0 && ++g_ticks % g_stall_every == 0) SLEEP_MS(g_stall_ms);
}

static void write_out(void *wctx, const uint8_t *p, size_t n)
{
    (void)wctx;
    while (n > 0) {
        int k = (int)WRITE(1, p, (unsigned)n);
        if (k <= 0) return;
        p += k;
        n -= (size_t)k;
    }
}

int main(void)
{
    static OpusApp app;
    static OpusHilCtx ctx;
    static HilApp ha;
    static HilDev dev;
    static uint8_t buf[4096];

#ifdef _WIN32
    _setmode(0, _O_BINARY);
    _setmode(1, _O_BINARY);
#endif
    opus_hil_app(&ctx, &app, &OPUS_PARAMS, OPUS_PIL_NAME, &ha);
    if (getenv("TT_PIL_STALL_EVERY") && getenv("TT_PIL_STALL_MS")) {
        g_stall_every = atol(getenv("TT_PIL_STALL_EVERY"));
        g_stall_ms = atol(getenv("TT_PIL_STALL_MS"));
        g_app = ha;
        ha.step = stall_step;
        fprintf(stderr, "%s: test hook: every %ld ticks, %ld ms late\n",
                OPUS_PIL_NAME, g_stall_every, g_stall_ms);
    }
    hil_dev_init(&dev, &ha, write_out, 0);

    for (;;) {
        int n = (int)READ(0, buf, (unsigned)sizeof(buf));
        if (n <= 0) break;
        hil_dev_feed(&dev, buf, (size_t)n);
    }
    fprintf(stderr, "%s: %u ticks, %u not ready, %u bad, rx %u frames, %u crc, %u cobs, %u gaps\n",
            OPUS_PIL_NAME, (unsigned)dev.steps, (unsigned)dev.not_ready, (unsigned)dev.bad,
            (unsigned)dev.rx.frames, (unsigned)dev.rx.crc_errors, (unsigned)dev.rx.cobs_errors,
            (unsigned)dev.rx.seq_gaps);
    return 0;
}
