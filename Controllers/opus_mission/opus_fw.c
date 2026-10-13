/*
 * opus_fw.c — the Opus firmware's loop body. See opus_fw.h.
 */
#include "opus_fw.h"
#include "mission_cfg.h"

#include <math.h>
#include <string.h>

static int finitef_(float v) { return v == v && v - v == 0.0f; }

void opus_fw_init(OpusFw *fw, const TtParams *p, float rate_hz,
                  const TtNavCfg *nav, const TtSafetyCfg *safe)
{
    TtNavCfg nc;
    TtSafetyCfg sc;
    memset(fw, 0, sizeof(*fw));
    if (nav) nc = *nav; else tt_nav_default_cfg(&nc);
    if (safe) sc = *safe; else tt_safety_default_cfg(&sc);
    opus_init(&fw->mission, p, rate_hz);
    tt_nav_reset(&fw->nav, &nc);
    tt_safety_init(&fw->safe, &sc, p);
}

/* On a route with a surveyed start, the filter starts there. */
static void seed(OpusFw *fw)
{
    const TtRoute *r = fw->mission.route;
    if (r && r->has_start)
        tt_nav_seed(&fw->nav, r->start[0], r->start[1], r->start[2],
                    RT_SEED_POS_M, RT_SEED_PSI_RAD);
}

void opus_fw_set_route(OpusFw *fw, const TtRoute *r, float v_max)
{
    opus_set_route(&fw->mission, r, v_max);
    opus_fw_reset(fw);
}

void opus_fw_set_flow(OpusFw *fw, float height_m)
{
    fw->flow_h_m = height_m > 0.0f ? height_m : 0.0f;
}

/* The flow sensor's forward speed over the floor, when it tracks: image
 * motion is v / h less the pitch rate (the aim swings with the body), on the
 * height the driver states or the one the car was described with. */
static int take_flow(OpusFw *fw, const TtMeas *m, float *v)
{
    const TtFlow *f = &m->flow;
    float h;
    if (!f->st.valid) return 0;
    if (fw->flow_seen && f->st.seq == fw->flow_seq) return 0;   /* held over */
    fw->flow_seen = 1u;
    fw->flow_seq = f->st.seq;
    h = f->height_m > 0.0f ? f->height_m : fw->flow_h_m;
    if (!(h > 0.0f) || f->quality < NV_FLOW_QUALITY) return 0;
    *v = h * (f->flow_x + m->imu.gyro[1]);
    if (!finitef_(*v) || fabsf(*v) > NV_FLOW_VMAX) return 0;
    return 1;
}

void opus_fw_reset(OpusFw *fw)
{
    TtNavCfg nc = fw->nav.cfg;
    opus_reset(&fw->mission);
    tt_nav_reset(&fw->nav, &nc);
    seed(fw);
    tt_safety_reset(&fw->safe);
    memset(&fw->pose, 0, sizeof(fw->pose));
    memset(fw->uwb_seen, 0, sizeof(fw->uwb_seen));
    fw->nav_used_prev = 0u;
    fw->flow_seen = 0u;
    /* The site survey stays: the anchors did not move. */
}

/* Fresh UWB reads into the EKF's range list; new anchors into the survey. */
static int take_ranges(OpusFw *fw, const TtMeas *m, TtNavRange *r)
{
    int i, n = 0, added = 0;
    for (i = 0; i < m->uwb_n && i < TT_MAX_UWB; i++) {
        const TtUwb *u = &m->uwb[i];
        if (!u->st.valid) continue;
        if (fw->uwb_seen[i] && u->st.seq == fw->uwb_seq[i]) continue;
        fw->uwb_seen[i] = 1u;
        fw->uwb_seq[i] = u->st.seq;
        if (u->anchor >= OPUS_FW_ANCHORS || !(u->range_m > 0.0f)) continue;
        if (!fw->anchor_known[u->anchor]) {
            memcpy(fw->anchor[u->anchor], u->pos_m, sizeof(u->pos_m));
            fw->anchor_known[u->anchor] = 1u;
            added = 1;
        }
        memcpy(r[n].anchor, fw->anchor[u->anchor], sizeof(r[n].anchor));
        r[n].range_m = u->range_m;
        r[n].nlos_db = u->quality;
        n++;
    }
    if (added) {
        float a[OPUS_FW_ANCHORS][3];
        int k, na = 0;
        for (k = 0; k < OPUS_FW_ANCHORS; k++)
            if (fw->anchor_known[k]) { memcpy(a[na], fw->anchor[k], sizeof(a[na])); na++; }
        fw->n_anchors = na;
        tt_safety_fence_from_anchors(&fw->safe.cfg, (const float (*)[3])a, na,
                                     OPUS_FW_FENCE_MARGIN_M);
    }
    return n;
}

void opus_fw_step(OpusFw *fw, const TtMeas *m, TtCmd *out)
{
    OpusState *st = &fw->mission;
    TtCmd core;
    TtNavRange r[TT_MAX_UWB];
    int nr, flow_ok;
    float dt = m->dt_s > 0.0f ? m->dt_s : 0.0f, v_flow = 0.0f;

    /* 1. The mission, told what the safety layer decided last tick. */
    st->drive_ok = fw->safe.state == TT_SAFE_ARMED || fw->safe.state == TT_SAFE_DRIVING;
    if (fw->safe.state == TT_SAFE_FAULT) st->fault |= FA_SAFETY;
    opus_step(st, m, &core);

    /* 2. Navigation. */
    nr = take_ranges(fw, m, r);
    flow_ok = take_flow(fw, m, &v_flow);
    if (dt > 0.0f)
        tt_nav_step(&fw->nav, dt, m->imu.gyro[2], m->imu.accel[0], st->v_meas,
                    v_flow, flow_ok, r, nr);
    if (fw->nav.stage == TT_NAV_RUN) {
        /* The odometer's scale, once the ranges have pinned it (FW-15). */
        float var = fw->nav.P[TT_NAV_KW * TT_NAV_N + TT_NAV_KW];
        if (var < NV_KW_SIGMA_MAX * NV_KW_SIGMA_MAX)
            opus_set_odo_scale(st, fw->nav.s[TT_NAV_KW]);
        fw->pose.valid = 1u;
        fw->pose.x = fw->nav.s[TT_NAV_X];
        fw->pose.y = fw->nav.s[TT_NAV_Y];
        if (fw->nav.n_used != fw->nav_used_prev) fw->pose.age_s = 0.0f;
        else fw->pose.age_s += dt;
    }
    fw->nav_used_prev = fw->nav.n_used;
    /* The mission steers from this pose next tick (NAV-04); a seeded start
     * the heading search disagrees with stops the run. */
    opus_set_pose(st, fw->nav.s[TT_NAV_X], fw->nav.s[TT_NAV_Y], fw->nav.s[TT_NAV_PSI],
                  fw->nav.stage == TT_NAV_RUN);
    if (st->route && fw->nav.check == TT_NAV_CHECK_BAD) st->fault |= FA_NAV;

    /* 3. Safety. */
    tt_safety_step(&fw->safe, m, &core, &fw->pose, out);

    st->log.safe_state = (float)fw->safe.state;
    st->log.safe_fault = (float)fw->safe.faults;
    st->log.safe_v     = fw->safe.v;
    st->log.nav_ok     = fw->nav.stage == TT_NAV_RUN ? 1.0f : 0.0f;
    st->log.nav_x      = fw->nav.s[TT_NAV_X];
    st->log.nav_y      = fw->nav.s[TT_NAV_Y];
    st->log.nav_psi_deg = fw->nav.s[TT_NAV_PSI] * OPUS_RAD2DEG;
    st->log.nav_check  = (float)fw->nav.check;
}
