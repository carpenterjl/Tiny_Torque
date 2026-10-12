/*
 * sih_main.c — the on-target plant run on a PC (HIL-06).
 *
 *   opus_sih [--route <name>[@m/s]] [--rate 100] [--max 60] [--expect-done]
 *
 * The same loop a board runs (tt_sih_run), timed with the PC's performance
 * counter and as fast as it can go: a check that the firmware drives the
 * plant to the end of its mission, and the report's format. The numbers
 * that matter come from the board, where DWT->CYCCNT times the same loop
 * at the control rate (see the plan's HIL-06).
 */
#define _CRT_SECURE_NO_WARNINGS
#include "tt_sih_run.h"
#include "opus_app.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
static uint32_t cycles(void)
{
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (uint32_t)c.QuadPart;
}
static float cycles_per_us(void)
{
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    return (float)((double)f.QuadPart / 1e6);
}
#else
#include <time.h>
static uint32_t cycles(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint32_t)((uint64_t)ts.tv_sec * 1000000000ull + (uint64_t)ts.tv_nsec);
}
static float cycles_per_us(void) { return 1000.0f; }
#endif

static void print_line(const char *line, void *ctx)
{
    (void)ctx;
    printf("%s\n", line);
}

int main(int argc, char **argv)
{
    static TtSihReport rep;
    TtSihCfg cfg;
    int i, expect_done = 0;
    memset(&cfg, 0, sizeof(cfg));
    cfg.params = &tt_params_opus_vector_foc;
    cfg.rate_hz = 100.0f;
    cfg.max_s = 60.0f;
    cfg.cycles = cycles;
    cfg.cycles_per_us = cycles_per_us();
    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--route") && i + 1 < argc) cfg.route = argv[++i];
        else if (!strcmp(argv[i], "--rate") && i + 1 < argc) cfg.rate_hz = (float)atof(argv[++i]);
        else if (!strcmp(argv[i], "--max") && i + 1 < argc) cfg.max_s = (float)atof(argv[++i]);
        else if (!strcmp(argv[i], "--expect-done")) expect_done = 1;
        else { fprintf(stderr, "usage: opus_sih [--route name] [--rate hz] [--max s] [--expect-done]\n"); return 2; }
    }
    if (tt_sih_run(&cfg, &rep) != 0) { fprintf(stderr, "opus_sih: no such route\n"); return 2; }
    tt_sih_print(&cfg, &rep, print_line, 0);
    if (expect_done && rep.phase != OPUS_DONE) {
        printf("FAIL: the mission did not finish\n");
        return 1;
    }
    return 0;
}
