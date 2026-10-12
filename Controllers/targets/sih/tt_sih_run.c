/*
 * tt_sih_run.c — the firmware against the on-target plant, timed (HIL-06).
 * See tt_sih_run.h.
 */
#include "tt_sih_run.h"
#include "tt_sih.h"
#include "opus_app.h"

#include <stdio.h>
#include <string.h>

static OpusApp g_app;      /* static: the firmware's state is ~10 kB */
static TtSih   g_sih;

static float pct_us(const TtSihReport *r, float q)
{
    uint32_t want = (uint32_t)(q * (float)r->ticks), acc = 0u, i;
    for (i = 0; i < TT_SIH_HIST_US; i++) {
        acc += r->hist[i];
        if (acc > want) return (float)i;
    }
    return (float)TT_SIH_HIST_US;
}

int tt_sih_run(const TtSihCfg *cfg, TtSihReport *rep)
{
    const float dt = 1.0f / cfg->rate_hz;
    const float period_us = 1e6f * dt;
    double fw_sum = 0.0, plant_sum = 0.0;
    /* the proving ground's spawn, where the manoeuvre and every route
     * start: (-5, 15) heading -y, in the anchors' site frame */
    float start[3] = { -5.0f, 15.0f, -1.57079633f };
    TtCarDesc car;

    memset(rep, 0, sizeof(*rep));
    opus_app_init(&g_app, cfg->params, cfg->rate_hz);
    if (cfg->route && cfg->route[0]) {
        const TtRoute *r;
        char name[32];
        size_t n = strcspn(cfg->route, "@");
        if (n >= sizeof(name)) return -1;
        memcpy(name, cfg->route, n);
        name[n] = '\0';
        r = tt_route_find(name);
        if (r == 0) return -1;
        memcpy(start, r->start, sizeof(start));
    }
    tt_sih_init(&g_sih, cfg->params, start[0], start[1], start[2]);
    tt_sih_describe(&g_sih, &car);
    opus_app_configure(&g_app, &car);
    if (cfg->route && cfg->route[0] && opus_app_set_route(&g_app, cfg->route) != 0) return -1;

    while (g_sih.t < cfg->max_s) {
        TtMeas m;
        TtAct act;
        uint32_t c0, c1, c2;
        float us;
        if (cfg->tick_start) {
            float late = (float)cfg->tick_start() / cfg->cycles_per_us;
            if (late > rep->late_max_us) rep->late_max_us = late;
        }
        c0 = cfg->cycles();
        tt_sih_meas(&g_sih, dt, &m);
        c1 = cfg->cycles();
        opus_app_step(&g_app, &m, &act);
        c2 = cfg->cycles();
        tt_sih_step(&g_sih, &act, dt);
        plant_sum += (double)(uint32_t)(c1 - c0);
        plant_sum += (double)(uint32_t)(cfg->cycles() - c2);
        us = (float)(uint32_t)(c2 - c1) / cfg->cycles_per_us;
        fw_sum += (double)us;
        if (us > rep->fw_max_us) {
            rep->fw_max_us = us;
            rep->fw_max_tick = rep->ticks;
            rep->fw_max_phase = g_app.fw.mission.phase;
        }
        if (us > period_us) rep->over_period++;
        rep->hist[us < (float)(TT_SIH_HIST_US - 1u) ? (uint32_t)us : TT_SIH_HIST_US - 1u]++;
        rep->ticks++;
        rep->phase = g_app.fw.mission.phase;
        rep->fault = g_app.fw.mission.fault;
        if (rep->phase == OPUS_DONE || rep->phase == OPUS_FAULT) break;
    }
    if (rep->ticks > 0u) {
        rep->fw_mean_us = (float)(fw_sum / (double)rep->ticks);
        rep->plant_mean_us = (float)(plant_sum / (double)rep->ticks / (double)cfg->cycles_per_us);
        rep->fw_p50_us = pct_us(rep, 0.50f);
        rep->fw_p99_us = pct_us(rep, 0.99f);
        rep->load_pct = 100.0f * rep->fw_mean_us / period_us;
        rep->headroom_pct = 100.0f * (1.0f - rep->fw_max_us / period_us);
    }
    rep->x = g_sih.x;
    rep->y = g_sih.y;
    rep->psi = g_sih.psi;
    rep->sim_s = g_sih.t;
    return 0;
}

void tt_sih_print(const TtSihCfg *cfg, const TtSihReport *r,
                  void (*out)(const char *line, void *ctx), void *ctx)
{
    char b[160];
    snprintf(b, sizeof(b), "SIH %s at %.0f Hz: %lu ticks, %.2f s of driving",
             cfg->route && cfg->route[0] ? cfg->route : "the manoeuvre", (double)cfg->rate_hz,
             (unsigned long)r->ticks, (double)r->sim_s);
    out(b, ctx);
    snprintf(b, sizeof(b), "firmware per tick (us): mean %.1f  p50 %.0f  p99 %.0f  max %.1f (tick %lu, phase %d)",
             (double)r->fw_mean_us, (double)r->fw_p50_us, (double)r->fw_p99_us, (double)r->fw_max_us,
             (unsigned long)r->fw_max_tick, r->fw_max_phase);
    out(b, ctx);
    snprintf(b, sizeof(b), "CPU load %.2f %% of the %.0f us period; worst-tick headroom %.1f %%; %lu ticks over the period",
             (double)r->load_pct, (double)(1e6f / cfg->rate_hz), (double)r->headroom_pct,
             (unsigned long)r->over_period);
    out(b, ctx);
    snprintf(b, sizeof(b), "plant per tick (us, not the firmware's): %.1f; tick start late by up to %.1f us",
             (double)r->plant_mean_us, (double)r->late_max_us);
    out(b, ctx);
    snprintf(b, sizeof(b), "mission ended in phase %d, fault 0x%X, at (%.3f, %.3f) heading %.2f deg",
             r->phase, (unsigned)r->fault, (double)r->x, (double)r->y, (double)(r->psi * 57.29578f));
    out(b, ctx);
}
