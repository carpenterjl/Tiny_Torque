/*
 * tt_fault.c — fault injection on the firmware's own frames. See tt_fault.h.
 */
#include "tt_fault.h"
#include "tt_safety.h"

#include <math.h>
#include <string.h>

void tt_fault_init(TtFaultInj *j, const TtFault *f)
{
    memset(j, 0, sizeof(*j));
    j->f = *f;
    j->next_glitch_s = f->t_start_s;
}

int tt_fault_active(const TtFaultInj *j, float t)
{
    if (j->f.kind == TT_FAULT_NONE || t < j->f.t_start_s) return 0;
    return j->f.dur_s <= 0.0f || t < j->f.t_start_s + j->f.dur_s;
}

static TtStamp *sensor_stamp(TtMeas *m, int sensor, int index)
{
    switch (sensor) {
    case TT_FS_ENC:  return &m->enc[index & 3].st;
    case TT_FS_DRV:  return &m->drv[index & 3].st;
    case TT_FS_IMU:  return &m->imu.st;
    case TT_FS_BATT: return &m->batt.st;
    case TT_FS_RC:   return &m->rc.st;
    default:         return &m->uwb[0].st;
    }
}

/* Copy one sensor's whole record from `from` into `to`. */
static void copy_sensor(TtMeas *to, const TtMeas *from, int sensor, int index)
{
    switch (sensor) {
    case TT_FS_ENC:  to->enc[index & 3] = from->enc[index & 3]; break;
    case TT_FS_DRV:  to->drv[index & 3] = from->drv[index & 3]; break;
    case TT_FS_IMU:  to->imu = from->imu; break;
    case TT_FS_BATT: to->batt = from->batt; break;
    case TT_FS_RC:   to->rc = from->rc; break;
    default:         to->uwb[0] = from->uwb[0]; break;
    }
}

static void nan_sensor(TtMeas *m, int sensor, int index)
{
    const float q = NAN;
    int k;
    switch (sensor) {
    case TT_FS_ENC:  m->enc[index & 3].st.valid = 0; break;   /* a count has no NaN */
    case TT_FS_DRV:  m->drv[index & 3].iq_a = q; m->drv[index & 3].omega_m = q; break;
    case TT_FS_IMU:  for (k = 0; k < 3; k++) { m->imu.gyro[k] = q; m->imu.accel[k] = q; } break;
    case TT_FS_BATT: m->batt.v = q; m->batt.i = q; break;
    case TT_FS_RC:   for (k = 0; k < 8; k++) m->rc.ch[k] = q; break;
    default:         m->uwb[0].range_m = q; break;
    }
}

void tt_fault_apply_meas(TtFaultInj *j, float t, TtMeas *m)
{
    const TtFault *f = &j->f;
    int w = f->index & 3;

    /* An encoder glitch persists in the cumulative count once it happened. */
    if (f->kind == TT_FAULT_ENC_GLITCH) {
        if (tt_fault_active(j, t) && t >= j->next_glitch_s) {
            j->glitch_offset += (int32_t)f->mag;
            j->next_glitch_s = f->period_s > 0.0f ? j->next_glitch_s + f->period_s : 1e30f;
        }
        m->enc[w].count += j->glitch_offset;
        return;
    }
    if (!tt_fault_active(j, t)) { j->has_held = 0; return; }

    switch (f->kind) {
    case TT_FAULT_STALE:
    case TT_FAULT_STUCK:
        if (!j->has_held) { j->held = *m; j->has_held = 1; }
        if (f->kind == TT_FAULT_STALE) {
            copy_sensor(m, &j->held, f->sensor, f->index);
        } else {
            TtStamp keep = *sensor_stamp(m, f->sensor, f->index);
            copy_sensor(m, &j->held, f->sensor, f->index);
            *sensor_stamp(m, f->sensor, f->index) = keep;
        }
        break;
    case TT_FAULT_NAN:
        nan_sensor(m, f->sensor, f->index);
        break;
    case TT_FAULT_DRV_TRIP:
        m->drv[w].fault = (uint16_t)(m->drv[w].fault | (uint16_t)f->mag);
        break;
    case TT_FAULT_DRV_HOT:
        m->drv[w].temp_c = f->mag;
        break;
    case TT_FAULT_BROWNOUT:
        m->batt.v = f->mag;
        break;
    case TT_FAULT_UWB_LOSS: {
        int i;
        for (i = 0; i < m->uwb_n && i < TT_MAX_UWB; i++)
            if (f->index == 255 || m->uwb[i].anchor == (uint8_t)f->index) {
                m->uwb[i].anchor = 255u;
                m->uwb[i].range_m = 0.0f;
            }
        break;
    }
    case TT_FAULT_RC_LINK:
        /* The receiver repeats the last good frame, flags it lost, and
         * declares failsafe after its hold time. */
        if (!j->has_held) { j->held = *m; j->has_held = 1; }
        memcpy(m->rc.ch, j->held.rc.ch, sizeof(m->rc.ch));
        m->rc.failsafe = (uint8_t)(m->rc.failsafe | TT_RC_FRAME_LOST);
        if (t - f->t_start_s >= f->mag) m->rc.failsafe = (uint8_t)(m->rc.failsafe | TT_RC_FAILSAFE);
        break;
    case TT_FAULT_RC_KILL:
        m->rc.ch[5] = 1.0f;
        break;
    case TT_FAULT_RC_DISARM:
        m->rc.ch[4] = -1.0f;
        break;
    case TT_FAULT_OVERRUN:
        if (!j->overrun_done) { m->dt_s += f->mag; j->overrun_done = 1; }
        break;
    case TT_FAULT_TIPOVER:
        m->imu.accel[0] = 0.0f;
        m->imu.accel[1] = 9.81f;
        m->imu.accel[2] = 0.0f;
        break;
    default:
        break;
    }
}

void tt_fault_apply_cmd(TtFaultInj *j, float t, TtCmd *c)
{
    int w;
    if (!tt_fault_active(j, t)) {
        j->last_cmd = *c;
        j->has_cmd = 1;
        return;
    }
    switch (j->f.kind) {
    case TT_FAULT_CORE_STALL:
        if (j->has_cmd) *c = j->last_cmd;   /* nothing new: same frame, same seq */
        break;
    case TT_FAULT_CORE_NAN:
        for (w = 0; w < TT_MAX_WHEELS; w++) c->wheel_torque_nm[w] = NAN;
        break;
    default:
        break;
    }
}
