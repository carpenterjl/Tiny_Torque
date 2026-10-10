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
} OpusFw;

/* Power-on. `nav` and `safe` may be NULL for the defaults. */
void opus_fw_init(OpusFw *fw, const TtParams *p, float rate_hz,
                  const TtNavCfg *nav, const TtSafetyCfg *safe);

/* The car was put back at the start: a new run, every latch cleared. */
void opus_fw_reset(OpusFw *fw);

/* One tick. */
void opus_fw_step(OpusFw *fw, const TtMeas *m, TtCmd *out);

#endif /* OPUS_FW_H */
