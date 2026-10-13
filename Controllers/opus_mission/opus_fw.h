/*
 * opus_fw.h — the Opus firmware's loop body: mission, navigation, safety.
 *
 * What the scheduler calls once per tick on every target (the sim adapter,
 * host replay, the MCU's main loop):
 *
 *     opus_fw_step(&fw, &meas, &cmd);     TtMeas in, TtCmd out
 *
 * Inside, in order:
 *   1. the mission (opus_mission.h) computes its command from the
 *      measurements; it launches only once the safety layer has armed;
 *   2. the navigation EKF (core/tt_nav.h, FW-14) takes the gyro, the
 *      odometer speed and any fresh UWB range, and gives the site position;
 *   3. the safety layer (core/tt_safety.h, FW-08) checks the frame and the
 *      mission's command, and passes it, stops the car, or cuts torque. When
 *      it trips, the mission is told (FA_SAFETY) and stops sequencing.
 *
 * The anchors' surveyed positions arrive with each range; the firmware keeps
 * them as its site survey, and the geofence is the box they span, shrunk by
 * OPUS_FW_FENCE_MARGIN_M.
 *
 * On a route (opus_fw_set_route, NAV-04) the filter starts at the route's
 * surveyed start instead of searching for its pose, and the mission steers
 * from the filter's pose each tick; the search still runs once over the
 * first metres as a check, and a seed it disagrees with stops the run
 * (FA_NAV).
 */
#ifndef OPUS_FW_H
#define OPUS_FW_H

#include "opus_mission.h"
#include "tt_nav.h"
#include "tt_safety.h"

#define OPUS_FW_ANCHORS         16
#define OPUS_FW_FENCE_MARGIN_M  0.5f

typedef struct {
    OpusState    mission;
    TtNav        nav;
    TtSafety     safe;
    TtSafetyPose pose;
    float        anchor[OPUS_FW_ANCHORS][3];
    uint8_t      anchor_known[OPUS_FW_ANCHORS];
    int          n_anchors;
    uint32_t     uwb_seq[TT_MAX_UWB];
    uint8_t      uwb_seen[TT_MAX_UWB];
    uint32_t     nav_used_prev;
    /* The flow sensor (FW-14): the height it was mounted at (0 = none), and
     * the last read taken. */
    float        flow_h_m;
    uint32_t     flow_seq;
    uint8_t      flow_seen;
} OpusFw;

/* Power-on. `nav` and `safe` may be NULL for the defaults. */
void opus_fw_init(OpusFw *fw, const TtParams *p, float rate_hz,
                  const TtNavCfg *nav, const TtSafetyCfg *safe);

/* The car was put back at the start: a new run, every latch cleared. */
void opus_fw_reset(OpusFw *fw);

/* Follow route `r` (NULL: the fixed manoeuvre) at most at v_max (<= 0: the
 * route's own); a new run on it. */
void opus_fw_set_route(OpusFw *fw, const TtRoute *r, float v_max);

/* The car has a flow sensor `height_m` over the floor (<= 0: none). Its
 * speed goes to the EKF when it tracks (FW-14). */
void opus_fw_set_flow(OpusFw *fw, float height_m);

/* One tick. */
void opus_fw_step(OpusFw *fw, const TtMeas *m, TtCmd *out);

#endif /* OPUS_FW_H */
