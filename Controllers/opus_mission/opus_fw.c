/*
 * opus_fw.c — the Opus firmware's loop body. See opus_fw.h.
 */
#include "opus_fw.h"
#include "mission_cfg.h"

#include <string.h>

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

void opus_fw_reset(OpusFw *fw)
{
    TtNavCfg nc = fw->nav.cfg;
    opus_reset(&fw->mission);
    tt_nav_reset(&fw->nav, &nc);
    tt_safety_reset(&fw->safe);
    memset(&fw->pose, 0, sizeof(fw->pose));
    memset(fw->uwb_seen, 0, sizeof(fw->uwb_seen));
    fw->nav_used_prev = 0u;
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
    int nr;
    float dt = m->dt_s > 0.0f ? m->dt_s : 0.0f;

    /* 1. The mission, told what the safety layer decided last tick. */
    st->drive_ok = fw->safe.state == TT_SAFE_ARMED || fw->safe.state == TT_SAFE_DRIVING;
    if (fw->safe.state == TT_SAFE_FAULT) st->fault |= FA_SAFETY;
    opus_step(st, m, &core);

    /* 2. Navigation. */
    nr = take_ranges(fw, m, r);
    if (dt > 0.0f)
        tt_nav_step(&fw->nav, dt, m->imu.gyro[2], m->imu.accel[0], st->v_meas,
                    0.0f, 0, r, nr);
    if (fw->nav.stage == TT_NAV_RUN) {
        fw->pose.valid = 1u;
        fw->pose.x = fw->nav.s[TT_NAV_X];
        fw->pose.y = fw->nav.s[TT_NAV_Y];
        if (fw->nav.n_used != fw->nav_used_prev) fw->pose.age_s = 0.0f;
        else fw->pose.age_s += dt;
    }
    fw->nav_used_prev = fw->nav.n_used;

    /* 3. Safety. */
    tt_safety_step(&fw->safe, m, &core, &fw->pose, out);

    st->log.safe_state = (float)fw->safe.state;
    st->log.safe_fault = (float)fw->safe.faults;
    st->log.safe_v     = fw->safe.v;
    st->log.nav_ok     = fw->nav.stage == TT_NAV_RUN ? 1.0f : 0.0f;
    st->log.nav_x      = fw->nav.s[TT_NAV_X];
    st->log.nav_y      = fw->nav.s[TT_NAV_Y];
    st->log.nav_psi_deg = fw->nav.s[TT_NAV_PSI] * OPUS_RAD2DEG;
}
