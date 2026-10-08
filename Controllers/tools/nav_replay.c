/*
 * nav_replay.c — run the navigation EKF (core/tt_nav) offline on a sim
 * run's telemetry and score it against the ground truth in the same log.
 * The first piece of the log-replay (SIL) harness, VAL-03.
 *
 *   nav_replay <run.telemetry.csv> [--odo dbg/v_meas] [--imu imu] [--uwb uwb]
 *              [--flow flow] [--lever-x -0.06] [--lever-y 0] [--tag-z 0.19]
 *              [--flow-h 0.025] [--flow-k 47.7] [--out est.csv]
 *
 * Get the log with OpusMissionRunner ... -opusTelemetry 1 (one row per
 * control tick, every channel). Inputs, by channel name:
 *   sens/<imu>/gz, ax, gy           SENSOR_IMU6, chip frame = body FLU here
 *   <odo>                           odometer speed (the firmware's own)
 *   sens/<flow>/dx, dy, squal       SENSOR_FLOW counts per read
 *   sens/<uwb>/id, range, nlos_db, ax, ay, az   SENSOR_UWB, one per read
 *   veh/pos_x, veh/pos_z, veh/yaw_deg           truth (Unity world)
 * A UWB or flow read is taken as new when its values change.
 * Truth in the UWB world frame: x = Unity z, y = -Unity x, psi = -yaw.
 */
#define _CRT_SECURE_NO_WARNINGS   /* plain C stdio on MSVC too */
#include "tt_nav.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define MAXCOL 1024
static char g_line[1 << 18];
static char *g_hdr[MAXCOL];
static int g_ncol;

static int col(const char *name)
{
    int i;
    for (i = 0; i < g_ncol; i++)
        if (strcmp(g_hdr[i], name) == 0) return i;
    return -1;
}

static int split(char *s, char **out, int max)
{
    int n = 0;
    char *p = s;
    while (n < max) {
        char *c = strchr(p, ',');
        out[n++] = p;
        if (!c) break;
        *c = '\0';
        p = c + 1;
    }
    if (n > 0) { char *e = out[n - 1] + strlen(out[n - 1]); while (e > out[n - 1] && (e[-1] == '\n' || e[-1] == '\r')) *--e = '\0'; }
    return n;
}

static double val(char **f, int n, int c) { return (c >= 0 && c < n && f[c][0]) ? atof(f[c]) : NAN; }

int main(int argc, char **argv)
{
    const char *path = NULL, *odo = "dbg/v_meas", *imu = "imu", *uwb = "uwb", *flow = "flow", *out = NULL;
    double lever_x = -0.06, lever_y = 0.0, tag_z = 0.19, flow_h = 0.025, flow_k = 47.7;
    char name[128], *f[MAXCOL];
    int i, ct, cgz, cax, cgy, codo, cfx, cfq, cid, crg, cdb, cax_, cay_, caz_, cpx, cpz, cyaw;
    FILE *fp, *fo = NULL;
    TtNavCfg cfg;
    TtNav nav;
    double t_prev = NAN, last_id = NAN, last_rg = NAN, last_fx = NAN, t_flow = NAN;
    double se = 0.0, sp = 0.0, emax = 0.0, t_init = -1.0, se_mov = 0.0;
    long n = 0, n_mov = 0, rows = 0;

    for (i = 1; i < argc; i++) {
        if (!strcmp(argv[i], "--odo") && i + 1 < argc) odo = argv[++i];
        else if (!strcmp(argv[i], "--imu") && i + 1 < argc) imu = argv[++i];
        else if (!strcmp(argv[i], "--uwb") && i + 1 < argc) uwb = argv[++i];
        else if (!strcmp(argv[i], "--flow") && i + 1 < argc) flow = argv[++i];
        else if (!strcmp(argv[i], "--lever-x") && i + 1 < argc) lever_x = atof(argv[++i]);
        else if (!strcmp(argv[i], "--lever-y") && i + 1 < argc) lever_y = atof(argv[++i]);
        else if (!strcmp(argv[i], "--tag-z") && i + 1 < argc) tag_z = atof(argv[++i]);
        else if (!strcmp(argv[i], "--flow-h") && i + 1 < argc) flow_h = atof(argv[++i]);
        else if (!strcmp(argv[i], "--flow-k") && i + 1 < argc) flow_k = atof(argv[++i]);
        else if (!strcmp(argv[i], "--out") && i + 1 < argc) out = argv[++i];
        else path = argv[i];
    }
    if (!path) { fprintf(stderr, "usage: nav_replay <telemetry.csv> [options]\n"); return 2; }
    fp = fopen(path, "r");
    if (!fp) { fprintf(stderr, "cannot open %s\n", path); return 2; }
    if (!fgets(g_line, sizeof(g_line), fp)) { fprintf(stderr, "empty log\n"); return 2; }
    {
        static char hdr[1 << 18];
        const char *h = g_line;
        /* Unity's StreamWriter writes a UTF-8 byte-order mark. */
        if ((unsigned char)h[0] == 0xEF && (unsigned char)h[1] == 0xBB && (unsigned char)h[2] == 0xBF) h += 3;
        strcpy(hdr, h);
        g_ncol = split(hdr, g_hdr, MAXCOL);
    }
#define COLF(var, fmt, a) do { snprintf(name, sizeof(name), fmt, a); var = col(name); } while (0)
    ct = col("time");
    COLF(cgz, "sens/%s/gz", imu); COLF(cax, "sens/%s/ax", imu); COLF(cgy, "sens/%s/gy", imu);
    codo = col(odo);
    COLF(cfx, "sens/%s/dx", flow); COLF(cfq, "sens/%s/squal", flow);
    COLF(cid, "sens/%s/id", uwb); COLF(crg, "sens/%s/range", uwb); COLF(cdb, "sens/%s/nlos_db", uwb);
    COLF(cax_, "sens/%s/ax", uwb); COLF(cay_, "sens/%s/ay", uwb); COLF(caz_, "sens/%s/az", uwb);
    cpx = col("veh/pos_x"); cpz = col("veh/pos_z"); cyaw = col("veh/yaw_deg");
    if (ct < 0 || cgz < 0 || codo < 0 || cid < 0 || crg < 0 || cpx < 0 || cpz < 0 || cyaw < 0) {
        fprintf(stderr, "missing channels (time %d, gz %d, odo %d, uwb %d/%d, truth %d/%d/%d)\n",
                ct, cgz, codo, cid, crg, cpx, cpz, cyaw);
        return 2;
    }

    tt_nav_default_cfg(&cfg);
    cfg.lever[0] = (float)lever_x;
    cfg.lever[1] = (float)lever_y;
    cfg.tag_z = (float)tag_z;
    tt_nav_reset(&nav, &cfg);
    if (out) { fo = fopen(out, "w"); if (fo) fprintf(fo, "t,x,y,psi,x_true,y_true,psi_true,err\n"); }

    while (fgets(g_line, sizeof(g_line), fp)) {
        int nf = split(g_line, f, MAXCOL);
        double t = val(f, nf, ct), dt, gz = val(f, nf, cgz), ax = val(f, nf, cax), v = val(f, nf, codo);
        double vflow = 0.0, xt, yt, pt;
        int flow_ok = 0, nr = 0;
        TtNavRange r[1];
        rows++;
        if (isnan(t)) continue;
        dt = isnan(t_prev) ? 0.0 : t - t_prev;
        t_prev = t;
        if (isnan(gz)) gz = 0.0;
        if (isnan(ax)) ax = 0.0;
        if (isnan(v)) v = 0.0;

        /* A new flow read: counts over the time since the last one. */
        if (cfx >= 0) {
            double fx = val(f, nf, cfx), q = val(f, nf, cfq), gy = val(f, nf, cgy);
            if (!isnan(fx) && (isnan(last_fx) || fx != last_fx || t - t_flow > 0.015)) {
                double span = isnan(t_flow) ? dt : t - t_flow;
                if (q > 100.0 && span > 0.0) {
                    vflow = flow_h * (fx / flow_k / span + (isnan(gy) ? 0.0 : gy));
                    flow_ok = 1;
                }
                t_flow = t; last_fx = fx;
            }
        }
        /* A new UWB read. */
        {
            double id = val(f, nf, cid), rg = val(f, nf, crg);
            if (!isnan(id) && id >= 0.0 && (id != last_id || rg != last_rg)) {
                r[0].anchor[0] = (float)val(f, nf, cax_);
                r[0].anchor[1] = (float)val(f, nf, cay_);
                r[0].anchor[2] = (float)val(f, nf, caz_);
                r[0].range_m = (float)rg;
                r[0].nlos_db = cdb >= 0 ? (float)val(f, nf, cdb) : 0.0f;
                nr = 1;
            }
            last_id = id; last_rg = rg;
        }
        if (dt > 0.0)
            tt_nav_step(&nav, (float)dt, (float)gz, (float)ax, (float)v, (float)vflow, flow_ok, r, nr);

        xt = val(f, nf, cpz); yt = -val(f, nf, cpx); pt = -val(f, nf, cyaw) * 3.14159265358979 / 180.0;
        if (nav.stage == TT_NAV_RUN) {
            double ex = nav.s[TT_NAV_X] - xt, ey = nav.s[TT_NAV_Y] - yt, e = sqrt(ex * ex + ey * ey);
            double ep = tt_nav_wrap((float)(nav.s[TT_NAV_PSI] - pt));
            if (t_init < 0.0) t_init = t;
            if (t > t_init + 1.0) {
                se += e * e; sp += ep * ep; n++;
                if (e > emax) emax = e;
                if (fabs(v) > 0.1) { se_mov += e * e; n_mov++; }
            }
            if (fo) fprintf(fo, "%.4f,%.4f,%.4f,%.5f,%.4f,%.4f,%.5f,%.4f\n", t,
                            nav.s[TT_NAV_X], nav.s[TT_NAV_Y], nav.s[TT_NAV_PSI], xt, yt, pt, e);
        }
    }
    fclose(fp);
    if (fo) fclose(fo);

    printf("rows %ld; init at t = %.2f s (fit rms %.3f m); ranges used %u, gated %u\n",
           rows, t_init, nav.init_rms, nav.n_used, nav.n_gated);
    if (n == 0) { printf("NOT INITIALISED\n"); return 1; }
    printf("position RMSE %.3f m (moving %.3f m, max %.3f m); heading RMSE %.2f deg; k_w %.4f; b_g %.5f rad/s\n",
           sqrt(se / (double)n), n_mov ? sqrt(se_mov / (double)n_mov) : 0.0, emax,
           sqrt(sp / (double)n) * 57.29578, nav.s[TT_NAV_KW], nav.s[TT_NAV_BG]);
    return 0;
}
