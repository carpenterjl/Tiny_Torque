/*
 * tt_replay.c — replay a recorded run through the firmware and diff its
 * commands (VAL-03).
 *
 * The input is a wire capture (core/tt_wire.h): the frames that went to the
 * firmware and came back, in order — what `tt_bridge --record` writes during
 * a lockstep run in the sim, and what the car's firmware writes to its log
 * when it frames its own TtMeas and TtAct the same way (VAL-05). For every
 * MEAS the current Opus firmware (opus_app) runs on exactly that TtMeas, and
 * its TtAct and log frame are compared with the recorded ACT:
 *
 *   - the firmware that made the recording, replayed: bit-identical, or the
 *     firmware is not deterministic;
 *   - a changed firmware or parameter set on an old recording: where, when
 *     and by how much its commands differ — the cheapest way to see what a
 *     change does to a drive, or why the car did what the sim did not.
 *
 * Usage:
 *   tt_replay <capture.ttw> [--params auto|opus_vector|opus_vector_foc]
 *             [--tol X] [--csv <diff.csv>] [--quiet]
 *
 * --params auto (default) picks the parameter set whose tt_params_hash()
 * matches the recorded INFO. --tol is the largest |difference| still called
 * equal (default 0: bit-identical; NaN equals NaN). --csv writes one row per
 * compared tick: recorded and replayed value of every channel, with a `time`
 * column, so the Telemetry Analyzer (Tools/telemetry.html) plots it.
 *
 * Exit 0 when every compared tick is equal, 1 when any differs, 2 on a bad
 * capture.
 */
#include "tt_wire.h"
#include "opus_app.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAX_CH 64

static const struct { const char *name; const TtParams *p; } k_sets[] = {
    { "opus_vector", &tt_params_opus_vector },
    { "opus_vector_foc", &tt_params_opus_vector_foc },
};

static int same(float a, float b, float tol)
{
    if (isnan(a) || isnan(b)) return isnan(a) && isnan(b);
    if (tol <= 0.0f) {
        uint32_t ua, ub;
        memcpy(&ua, &a, 4);
        memcpy(&ub, &b, 4);
        return ua == ub || (a == 0.0f && b == 0.0f);
    }
    return fabsf(a - b) <= tol;
}

static uint32_t rd32(const uint8_t *p)
{
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

int main(int argc, char **argv)
{
    const char *path = 0, *params_arg = "auto", *csv_path = 0;
    float tol = 0.0f;
    int quiet = 0, i;
    FILE *f, *csv = 0;
    static uint8_t buf[1 << 16];
    static TtWireRx rx;
    static OpusApp app;
    const TtParams *params = 0;
    char names[MAX_CH][40];
    int n_names = 0;
    TtWireInfo info;
    int have_info = 0, inited = 0;
    float rate = 0.0f;
    TtCarDesc car;
    int have_car = 0;
    /* the replayed answer waiting for its recorded ACT */
    int pending = 0;
    uint32_t pending_tick = 0;
    uint64_t pending_time = 0;
    TtAct rep_act;
    float rep_log[MAX_CH];
    uint32_t rep_n = 0;
    /* results */
    unsigned meas = 0, compared = 0, differ = 0, unanswered = 0, inits = 0, resets = 0;
    long first_diff = -1;
    double max_d[6 + MAX_CH];
    int ch_diff_ticks[6 + MAX_CH];
    size_t n;

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--params") && i + 1 < argc) params_arg = argv[++i];
        else if (!strcmp(argv[i], "--tol") && i + 1 < argc) tol = (float)atof(argv[++i]);
        else if (!strcmp(argv[i], "--csv") && i + 1 < argc) csv_path = argv[++i];
        else if (!strcmp(argv[i], "--quiet")) quiet = 1;
        else if (!path && argv[i][0] != '-') path = argv[i];
        else { path = 0; break; }
    }
    if (!path) {
        fprintf(stderr, "usage: tt_replay <capture.ttw> [--params auto|opus_vector|opus_vector_foc] "
                        "[--tol X] [--csv <diff.csv>] [--quiet]\n");
        return 2;
    }
    if (strcmp(params_arg, "auto") != 0) {
        for (i = 0; i < (int)(sizeof(k_sets) / sizeof(k_sets[0])); i++)
            if (!strcmp(params_arg, k_sets[i].name)) params = k_sets[i].p;
        if (!params) { fprintf(stderr, "tt_replay: unknown parameter set %s\n", params_arg); return 2; }
    }
    f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "tt_replay: cannot open %s\n", path); return 2; }
    if (csv_path) csv = fopen(csv_path, "w");
    memset(max_d, 0, sizeof(max_d));
    memset(ch_diff_ticks, 0, sizeof(ch_diff_ticks));
    memset(&car, 0, sizeof(car));
    tt_wire_rx_init(&rx);

    while ((n = fread(buf, 1, sizeof(buf), f)) > 0) {
        size_t off = 0;
        while (off < n) {
            TtWireHdr h;
            const uint8_t *p;
            size_t plen;
            int got;
            off += tt_wire_rx_feed(&rx, buf + off, n - off, &got, &h, &p, &plen);
            if (!got) continue;

            switch (h.type) {
            case TT_WIRE_INIT: {
                float r;
                uint32_t u;
                if (plen != 4) break;
                u = rd32(p);
                memcpy(&r, &u, 4);
                if (r > 0.0f) rate = r;
                break;
            }
            case TT_WIRE_INFO:
                if (tt_wire_get_info(p, plen, &info) != 0) break;
                have_info = 1;
                if (!params)
                    for (i = 0; i < (int)(sizeof(k_sets) / sizeof(k_sets[0])); i++)
                        if (tt_params_hash(k_sets[i].p) == info.params_hash) params = k_sets[i].p;
                if (n_names == 0) {
                    const char *s = info.log_names;
                    while (*s && n_names < MAX_CH) {
                        int k = 0;
                        while (*s && *s != ',' && k < 39) names[n_names][k++] = *s++;
                        names[n_names][k] = '\0';
                        while (*s && *s != ',') s++;
                        if (*s == ',') s++;
                        n_names++;
                    }
                }
                if (rate > 0.0f && !(h.flags & TT_WF_NOT_READY)) {
                    if (!params) {
                        fprintf(stderr, "tt_replay: no parameter set matches the recording "
                                        "(hash %08lX); pass --params\n", (unsigned long)info.params_hash);
                        return 2;
                    }
                    opus_app_init(&app, params, rate);
                    if (have_car) opus_app_configure(&app, &car);
                    inited = 1;
                    inits++;
                    rate = 0.0f;
                }
                break;
            case TT_WIRE_CONFIG:
                if (tt_wire_get_car(p, plen, &car) == 0) {
                    have_car = 1;
                    if (inited) opus_app_configure(&app, &car);
                }
                break;
            case TT_WIRE_RESET:
                if (inited) { opus_app_reset(&app); resets++; }
                break;
            case TT_WIRE_SHUTDOWN:
                inited = 0;
                break;
            case TT_WIRE_MEAS: {
                TtMeas m;
                const float *lg;
                uint32_t k;
                if (pending) unanswered++;
                pending = 0;
                if (!inited || tt_wire_get_meas(p, plen, &m) != 0) break;
                meas++;
                opus_app_step(&app, &m, &rep_act);
                lg = opus_app_log(&app, &rep_n);
                if (rep_n > MAX_CH) rep_n = MAX_CH;
                for (k = 0; k < rep_n; k++) rep_log[k] = lg[k];
                pending = 1;
                pending_tick = h.tick;
                pending_time = h.time_us;
                break;
            }
            case TT_WIRE_ACT: {
                TtAct ra;
                float rl[MAX_CH];
                uint32_t rn = 0, k;
                float rec_v[6 + MAX_CH], rep_v[6 + MAX_CH];
                int n_ch, any = 0;
                if (!pending || h.tick != pending_tick) break;
                pending = 0;
                if (h.flags & TT_WF_NOT_READY) break;
                if (tt_wire_get_act(p, plen, &ra, rl, MAX_CH, &rn) != 0) break;
                compared++;
                for (k = 0; k < 4; k++) { rec_v[k] = ra.drive[k]; rep_v[k] = rep_act.drive[k]; }
                rec_v[4] = ra.steer_rad; rep_v[4] = rep_act.steer_rad;
                rec_v[5] = ra.brake_01; rep_v[5] = rep_act.brake_01;
                n_ch = 6 + (int)(rn < rep_n ? rn : rep_n);
                for (k = 0; k < (uint32_t)n_ch - 6u; k++) { rec_v[6 + k] = rl[k]; rep_v[6 + k] = rep_log[k]; }
                if (rn != rep_n) any = 1;
                for (i = 0; i < n_ch; i++) {
                    if (!same(rec_v[i], rep_v[i], tol)) {
                        double d = fabs((double)rec_v[i] - (double)rep_v[i]);
                        if (d != d) d = HUGE_VAL;                 /* NaN against a number */
                        any = 1;
                        ch_diff_ticks[i]++;
                        if (d > max_d[i]) max_d[i] = d;
                    }
                }
                if (any) {
                    differ++;
                    if (first_diff < 0) first_diff = (long)h.tick;
                }
                if (csv) {
                    static int header = 0;
                    if (!header) {
                        static const char *act_names[6] = { "drive0", "drive1", "drive2", "drive3",
                                                            "steer_rad", "brake_01" };
                        fprintf(csv, "time,tick");
                        for (i = 0; i < n_ch; i++) {
                            const char *nm = i < 6 ? act_names[i] : (i - 6 < n_names ? names[i - 6] : "log");
                            fprintf(csv, ",rec_%s,rep_%s", nm, nm);
                        }
                        fprintf(csv, "\n");
                        header = 1;
                    }
                    fprintf(csv, "%.6f,%lu", (double)pending_time * 1e-6, (unsigned long)h.tick);
                    for (i = 0; i < n_ch; i++) fprintf(csv, ",%.9g,%.9g", rec_v[i], rep_v[i]);
                    fprintf(csv, "\n");
                }
                break;
            }
            default:
                break;
            }
        }
    }
    fclose(f);
    if (csv) fclose(csv);
    if (pending) unanswered++;

    printf("tt_replay %s: firmware %s, params %s, %u init, %u reset, %u MEAS, %u compared, "
           "%u unanswered\n", path, have_info ? info.name : "?",
           params == &tt_params_opus_vector ? "opus_vector" :
           params == &tt_params_opus_vector_foc ? "opus_vector_foc" : "?",
           inits, resets, meas, compared, unanswered);
    if (rx.crc_errors + rx.cobs_errors + rx.overflows)
        printf("  capture damage: %u CRC, %u COBS, %u overrun\n",
               (unsigned)rx.crc_errors, (unsigned)rx.cobs_errors, (unsigned)rx.overflows);
    if (compared == 0) {
        printf("  nothing to compare\n");
        return 2;
    }
    if (differ == 0) {
        printf("  IDENTICAL: every tick's actuator writes and log frame match%s\n",
               tol > 0.0f ? " within the tolerance" : " bit for bit");
        return 0;
    }
    printf("  DIFFERENT: %u of %u ticks, first at tick %ld\n", differ, compared, first_diff);
    if (!quiet) {
        static const char *act_names[6] = { "drive0", "drive1", "drive2", "drive3", "steer_rad", "brake_01" };
        for (i = 0; i < 6 + n_names; i++) {
            if (!ch_diff_ticks[i]) continue;
            printf("    %-16s %6d ticks differ, max |d| %.6g\n",
                   i < 6 ? act_names[i] : names[i - 6], ch_diff_ticks[i], max_d[i]);
        }
    }
    return 1;
}
