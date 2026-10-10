/*
 * test_safety.c — the safety layer (FW-08) against injected faults (VAL-11).
 *
 * A synthetic car (the FOC twin's parameters: four driven wheels, front
 * odometry encoders, a 2S pack, an RC receiver at 50 Hz) is driven straight
 * at 2 m/s by a minimal core. For each fault of the catalogue in tt_fault.h
 * the run checks that the safety layer
 *   - flags the right TT_SF_* bit, within the time the fault allows,
 *   - answers with the right category: zero torque at once (0), or a stop
 *     on the motors that brings the car to rest (1), then zero torque,
 *   - holds what must hold (latches, refusal to re-arm),
 * and that the clean drive, a single encoder glitch and a short brownout
 * trip nothing. Registered with CTest as `tt_safety_faults`.
 */
#include "tt_safety.h"
#include "tt_fault.h"

#include <math.h>
#include <stdio.h>
#include <string.h>

static int g_fail = 0;
#define CHECK(name, cond) do { if (!(cond)) { printf("FAIL: %s\n", name); g_fail++; } \
                               else printf("ok   %s\n", name); } while (0)

static unsigned long long g_rng = 88172645463325252ull;
static double urand(void)
{
    g_rng ^= g_rng << 13; g_rng ^= g_rng >> 7; g_rng ^= g_rng << 17;
    return (double)(g_rng >> 11) * (1.0 / 9007199254740992.0);
}
static double gauss(void)
{
    double u1 = 1.0 - urand(), u2 = urand();
    return sqrt(-2.0 * log(u1)) * cos(6.283185307179586 * u2);
}

#define DT       0.01f
#define V_TARGET 2.0f

typedef struct {
    float  run_s;          /* how long to run                              */
    float  core_stop_s;    /* the core stops asking to drive at this time  */
    float  fence[4];
    int    pose;           /* feed a pose (geofence / position lost)      */
    int    rc_arm_off;     /* the arm switch starts off                    */
    float  batt_v;         /* resting pack voltage (0 = 8.2 V)             */
    float  push_v;         /* the car is rolling at this speed at t = 0    */
    float  reset_at_s;     /* operator reset (tt_safety_reset) here, 0 = no */
    float  cycle_arm_at_s; /* arm switch off then on here, 0 = no          */
} Opts;

typedef struct {
    float    t_trip;       /* first tick with any fault bit; -1 = never  */
    uint32_t first;        /* bits at the trip                            */
    uint32_t bits_seen;    /* every bit ever raised                       */
    float    t_zero;       /* first tick after the trip with no torque out */
    float    v_trip, x_trip;
    float    t_rest;       /* the car at rest after the trip; -1 = never  */
    float    x_end, v_end;
    uint8_t  cat;          /* category in force at the end of the fault  */
    uint8_t  state_end;
    uint8_t  ever_armed;
    float    t_armed;      /* first tick armed; -1 = never                */
    float    max_t_after;  /* largest |torque| applied after t_zero        */
    float    decel;        /* mean deceleration during the stop           */
} Result;

static Result run(const TtFault *fault, const Opts *o)
{
    const TtParams *p = &tt_params_opus_vector_foc;
    TtSafetyCfg cfg;
    TtSafety s;
    TtFaultInj inj;
    TtMeas m;
    TtCmd core, out;
    TtSafetyPose pose;
    Result r;
    double x = 1.0, v = o->push_v, enc_m[TT_MAX_WHEELS] = {0};
    float t = 0.0f, batt = o->batt_v > 0.0f ? o->batt_v : 8.2f;
    uint32_t cseq = 0, tick = 0;
    int w, stopping = 0;
    float pose_age = 0.0f, v_stop0 = 0.0f, t_stop0 = 0.0f, rc_arm = o->rc_arm_off ? -1.0f : 1.0f;

    memset(&r, 0, sizeof(r));
    r.t_trip = r.t_zero = r.t_rest = r.t_armed = -1.0f;
    r.cat = 255u;
    tt_safety_default_cfg(&cfg);
    memcpy(cfg.fence, o->fence, sizeof(cfg.fence));
    tt_safety_init(&s, &cfg, p);
    tt_fault_init(&inj, fault);
    memset(&pose, 0, sizeof(pose));

    while (t < o->run_s) {
        float a_long = 0.0f, tq;
        int bridge_off[TT_MAX_WHEELS] = {0};

        /* ---- the measurement frame -------------------------------------- */
        memset(&m, 0, sizeof(m));
        tick++;
        m.now_us = (tt_us_t)(tick * 10000u);
        m.dt_s = DT;
        for (w = 0; w < TT_MAX_WHEELS; w++) {
            float mpc = tt_m_per_count(p, w);
            m.drv[w].st.valid = 1; m.drv[w].st.seq = tick; m.drv[w].st.t_us = m.now_us;
            m.drv[w].omega_m = (float)(v / p->wheel_radius_m * p->gear[w]) + 0.05f * (float)gauss();
            m.drv[w].vbus_v = batt;
            m.drv[w].temp_c = 45.0f;
            if (p->odo_wheel_mask & (1u << w)) {
                m.enc[w].st.valid = 1; m.enc[w].st.seq = tick; m.enc[w].st.t_us = m.now_us;
                m.enc[w].count = mpc > 0.0f ? (int32_t)floor(enc_m[w] / mpc) : 0;
            }
        }
        m.imu.st.valid = 1; m.imu.st.seq = tick; m.imu.st.t_us = m.now_us;
        m.imu.gyro[2] = 0.002f * (float)gauss();
        m.imu.accel[0] = 0.05f * (float)gauss();
        m.imu.accel[2] = 9.81f + 0.05f * (float)gauss();
        m.batt.st.valid = 1; m.batt.st.seq = tick / 10u; m.batt.st.t_us = m.now_us;
        m.batt.v = batt;
        m.rc.st.valid = 1; m.rc.st.seq = tick / 2u; m.rc.st.t_us = m.now_us;   /* 50 Hz */
        m.rc.ch[4] = rc_arm;
        m.rc.ch[5] = -1.0f;
        if (o->cycle_arm_at_s > 0.0f && t >= o->cycle_arm_at_s && t < o->cycle_arm_at_s + 0.1f)
            m.rc.ch[4] = -1.0f;
        m.uwb_n = 1;
        m.uwb[0].st.valid = 1; m.uwb[0].st.seq = tick; m.uwb[0].anchor = (uint8_t)(tick % 4u);
        m.uwb[0].range_m = 5.0f;
        tt_fault_apply_meas(&inj, t, &m);
        for (w = 0; w < TT_MAX_WHEELS; w++)
            if (m.drv[w].fault & (TT_DRV_OVERVOLT | TT_DRV_CMD_TIMEOUT | TT_DRV_SENSOR | TT_DRV_OVERCURRENT))
                bridge_off[w] = 1;

        /* ---- the core: arm after half a second, hold 2 m/s --------------- */
        memset(&core, 0, sizeof(core));
        core.seq = ++cseq;
        core.t_us = m.now_us;
        core.arm = (uint8_t)(t >= 0.5f && t < o->core_stop_s);
        if (core.arm && t >= 0.8f) {
            tq = 0.3f * (V_TARGET - (float)v);
            if (tq > 0.3f) tq = 0.3f;
            if (tq < -0.3f) tq = -0.3f;
            for (w = 0; w < TT_MAX_WHEELS; w++) core.wheel_torque_nm[w] = tq;
        }
        tt_fault_apply_cmd(&inj, t, &core);

        /* ---- the pose (a stand-in for the EKF) --------------------------- */
        if (o->pose && t >= 1.0f) {
            int lost = fault->kind == TT_FAULT_UWB_LOSS && tt_fault_active(&inj, t);
            pose_age = lost ? pose_age + DT : 0.0f;
            pose.valid = 1u;
            pose.x = (float)x;
            pose.y = 0.0f;
            pose.age_s = pose_age;
        }

        if (o->reset_at_s > 0.0f && t >= o->reset_at_s && t < o->reset_at_s + 0.5f * DT)
            tt_safety_reset(&s);
        tt_safety_step(&s, &m, &core, o->pose ? &pose : 0, &out);

        /* ---- bookkeeping -------------------------------------------------- */
        if (s.state == TT_SAFE_ARMED || s.state == TT_SAFE_DRIVING) {
            if (!r.ever_armed) r.t_armed = t;
            r.ever_armed = 1u;
        }
        r.bits_seen |= s.faults;
        if (s.faults && r.t_trip < 0.0f) {
            r.t_trip = t; r.first = s.faults; r.v_trip = (float)v; r.x_trip = (float)x;
        }
        if (s.state == TT_SAFE_FAULT) r.cat = s.cat;
        {
            float tmax = 0.0f;
            for (w = 0; w < TT_MAX_WHEELS; w++)
                if (out.arm && fabsf(out.wheel_torque_nm[w]) > tmax) tmax = fabsf(out.wheel_torque_nm[w]);
            if (r.t_trip >= 0.0f && r.t_zero < 0.0f && tmax == 0.0f) r.t_zero = t;
            if (r.t_zero >= 0.0f && tmax > r.max_t_after) r.max_t_after = tmax;
            if (s.state == TT_SAFE_FAULT && s.cat == 1u && !stopping && tmax > 0.0f) {
                stopping = 1; v_stop0 = (float)v; t_stop0 = t;
            }
        }
        if (r.t_trip >= 0.0f && r.t_rest < 0.0f && fabs(v) < 0.02) {
            r.t_rest = t;
            if (stopping && t > t_stop0) r.decel = v_stop0 / (t - t_stop0);
        }

        /* ---- the car ------------------------------------------------------ */
        {
            double f = 0.0;
            if (out.arm)
                for (w = 0; w < TT_MAX_WHEELS; w++)
                    if (!bridge_off[w]) f += out.wheel_torque_nm[w] / p->wheel_radius_m;
            if (fabs(v) > 1e-4) f -= (v > 0.0 ? 1.0 : -1.0) * 0.4;   /* rolling + bearings */
            else if (fabs(f) < 0.4) f = 0.0;
            a_long = (float)(f / p->mass_eff_kg);
            {
                double v0 = v;
                v += a_long * DT;
                if (v0 * v < 0.0 && !(out.arm && fabs(f) > 0.4)) v = 0.0;   /* friction stops it */
            }
            x += v * DT;
            for (w = 0; w < TT_MAX_WHEELS; w++) enc_m[w] += v * DT;
        }
        (void)a_long;
        t += DT;
    }
    r.x_end = (float)x;
    r.v_end = (float)v;
    r.state_end = s.state;
    return r;
}

static Opts base(void)
{
    Opts o;
    memset(&o, 0, sizeof(o));
    o.run_s = 8.0f;
    o.core_stop_s = 100.0f;
    return o;
}

static TtFault mk(TtFaultKind k, int sensor, int index, float t0, float dur, float mag)
{
    TtFault f;
    memset(&f, 0, sizeof(f));
    f.kind = k; f.sensor = sensor; f.index = index;
    f.t_start_s = t0; f.dur_s = dur; f.mag = mag;
    return f;
}

/* Common expectations: `bit` raised within `within` s of `t0`; category
 * `cat`; a category-0 fault leaves no torque from the trip tick on, a
 * category-1 one stops the car on the motors. */
static void expect(const char *name, const Result *r, uint32_t bit, float t0, float within, int cat)
{
    char buf[160];
    snprintf(buf, sizeof(buf), "%s: flags 0x%04x within %.0f ms (at %.3f s, bits 0x%04x)",
             name, (unsigned)bit, within * 1000.0f, r->t_trip, (unsigned)r->bits_seen);
    CHECK(buf, r->t_trip >= t0 - 1e-4f && r->t_trip <= t0 + within + 1e-4f && (r->first & bit));
    snprintf(buf, sizeof(buf), "%s: category %d (got %d)", name, cat, (int)r->cat);
    CHECK(buf, r->cat == (uint8_t)cat);
    if (cat == 0) {
        snprintf(buf, sizeof(buf), "%s: torque cut on the trip tick (%.3f s)", name, r->t_zero);
        CHECK(buf, r->t_zero >= 0.0f && r->t_zero - r->t_trip < 0.5f * DT && r->max_t_after == 0.0f);
    } else {
        snprintf(buf, sizeof(buf), "%s: stops on the motors, %.2f m/s^2 from %.2f m/s, at rest at %.2f s",
                 name, r->decel, r->v_trip, r->t_rest);
        CHECK(buf, r->t_rest > 0.0f && r->decel > 1.8f && r->decel < 3.2f &&
                   r->t_rest - r->t_trip < r->v_trip / 2.0f + 0.5f);
        snprintf(buf, sizeof(buf), "%s: then no torque", name);
        CHECK(buf, r->t_zero >= 0.0f && r->max_t_after == 0.0f);
    }
}


#define T0 3.0f

int main(void)
{
    Opts o = base();
    TtFault none = mk(TT_FAULT_NONE, 0, 0, 0, 0, 0), f;
    Result r;

    /* ---- clean runs: nothing trips ---------------------------------------- */
    o.fence[0] = 0.0f; o.fence[1] = 100.0f; o.fence[2] = -5.0f; o.fence[3] = 5.0f;
    o.pose = 1;
    r = run(&none, &o);
    printf("clean: armed at %.2f s, end state %d, x %.2f m, v %.2f m/s, bits 0x%04x\n",
           r.t_armed, r.state_end, r.x_end, r.v_end, (unsigned)r.bits_seen);
    CHECK("clean drive: arms, drives, no fault bit ever", r.ever_armed && r.bits_seen == 0u &&
          r.state_end == TT_SAFE_DRIVING && fabsf(r.v_end - V_TARGET) < 0.1f);
    o = base();
    o.core_stop_s = 6.0f;
    r = run(&none, &o);
    CHECK("clean drive: the core drops its arm request -> disarmed", r.bits_seen == 0u &&
          r.state_end == TT_SAFE_DISARMED);

    f = mk(TT_FAULT_ENC_GLITCH, 0, TT_FL, T0, 0.0f, 5000.0f);
    r = run(&f, &o);
    CHECK("one encoder glitch: ignored, no trip", r.bits_seen == 0u);
    f = mk(TT_FAULT_BROWNOUT, 0, 0, T0, 0.3f, 5.5f);
    r = run(&f, &o);
    CHECK("brownout of 0.3 s: under the debounce, no trip", r.bits_seen == 0u);
    o = base();
    o.pose = 1; o.fence[1] = 100.0f; o.fence[2] = -5.0f; o.fence[3] = 5.0f;
    f = mk(TT_FAULT_UWB_LOSS, 0, 255, T0, 0.6f, 0.0f);
    r = run(&f, &o);
    CHECK("position fix lost for 0.6 s: inside the timeout, no trip", r.bits_seen == 0u);

    /* ---- arming ------------------------------------------------------------ */
    o = base();
    o.rc_arm_off = 1;
    r = run(&none, &o);
    CHECK("RC arm switch off: never arms, no fault", !r.ever_armed && r.bits_seen == 0u &&
          r.state_end == TT_SAFE_DISARMED);
    o = base();
    o.batt_v = 6.8f;
    r = run(&none, &o);
    CHECK("pack at 6.8 V (3.4 V/cell): never arms", !r.ever_armed);
    o = base();
    o.push_v = 0.5f;
    o.run_s = 6.0f;
    r = run(&none, &o);
    printf("rolling start: armed at %.2f s\n", r.t_armed);
    CHECK("rolling at 0.5 m/s: arms only once at rest", r.ever_armed && r.t_armed > 2.5f);

    /* ---- category 0: torque off at once ------------------------------------ */
    o = base();
    f = mk(TT_FAULT_RC_KILL, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("kill switch", &r, TT_SF_KILL, T0, 0.011f, 0);
    f = mk(TT_FAULT_CORE_STALL, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("core stall", &r, TT_SF_WATCHDOG, T0, 0.031f, 0);
    f = mk(TT_FAULT_CORE_NAN, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("core commands NaN", &r, TT_SF_NAN, T0, 0.011f, 0);
    f = mk(TT_FAULT_OVERRUN, 0, 0, T0, 0.0f, 0.2f);
    r = run(&f, &o);
    expect("tick 200 ms late", &r, TT_SF_OVERRUN, T0, 0.011f, 0);
    f = mk(TT_FAULT_DRV_TRIP, 0, TT_RL, T0, 0.0f, (float)TT_DRV_OVERVOLT);
    r = run(&f, &o);
    expect("driver over-voltage trip", &r, TT_SF_DRV_TRIP, T0, 0.011f, 0);
    f = mk(TT_FAULT_DRV_TRIP, 0, TT_FR, T0, 0.0f, (float)TT_DRV_CMD_TIMEOUT);
    r = run(&f, &o);
    expect("driver command timeout", &r, TT_SF_DRV_TRIP, T0, 0.011f, 0);
    f = mk(TT_FAULT_STALE, TT_FS_DRV, TT_FR, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("driver feedback stale", &r, TT_SF_DRV_STALE, T0, 0.07f, 0);
    f = mk(TT_FAULT_TIPOVER, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("tip-over", &r, TT_SF_TIPOVER, T0, 0.31f, 0);

    /* ---- category 1: a stop on the motors ----------------------------------- */
    f = mk(TT_FAULT_RC_LINK, 0, 0, T0, 0.0f, 1.0f);
    r = run(&f, &o);
    expect("RC frames lost", &r, TT_SF_RC_LOST, T0, 0.21f, 1);
    f = mk(TT_FAULT_RC_LINK, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("RC receiver failsafe", &r, TT_SF_RC_LOST, T0, 0.011f, 1);
    f = mk(TT_FAULT_STALE, TT_FS_RC, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("RC receiver silent", &r, TT_SF_RC_LOST, T0, 0.13f, 1);
    f = mk(TT_FAULT_RC_DISARM, 0, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("RC arm switch off while driving", &r, TT_SF_RC_DISARM, T0, 0.011f, 1);
    f = mk(TT_FAULT_DRV_HOT, 0, TT_RR, T0, 0.0f, 125.0f);
    r = run(&f, &o);
    expect("winding at 125 C", &r, TT_SF_DRV_HOT, T0, 0.011f, 1);
    f = mk(TT_FAULT_STALE, TT_FS_ENC, TT_FL, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("odometry encoder stale", &r, TT_SF_ENC, T0, 0.07f, 1);
    f = mk(TT_FAULT_STUCK, TT_FS_ENC, TT_FR, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("odometry encoder stuck (counts freeze, samples fresh)", &r, TT_SF_ENC, T0, 0.22f, 1);
    f = mk(TT_FAULT_ENC_GLITCH, 0, TT_FR, T0, 0.0f, 5000.0f);
    f.period_s = 0.2f;
    r = run(&f, &o);
    expect("encoder glitching (3 in 0.4 s)", &r, TT_SF_ENC, T0 + 0.4f, 0.011f, 1);
    f = mk(TT_FAULT_STALE, TT_FS_IMU, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("IMU stale (I2C NACK)", &r, TT_SF_IMU, T0, 0.07f, 1);
    f = mk(TT_FAULT_NAN, TT_FS_IMU, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("IMU reads NaN", &r, TT_SF_IMU, T0, 0.011f, 1);
    f = mk(TT_FAULT_BROWNOUT, 0, 0, T0, 2.0f, 5.5f);
    r = run(&f, &o);
    expect("pack at 5.5 V for 2 s", &r, TT_SF_UNDERVOLT, T0 + 0.5f, 0.011f, 1);
    f = mk(TT_FAULT_STALE, TT_FS_BATT, 0, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("pack sense stale", &r, TT_SF_BATT, T0, 0.61f, 1);
    o = base();
    o.pose = 1; o.fence[1] = 100.0f; o.fence[2] = -5.0f; o.fence[3] = 5.0f;
    f = mk(TT_FAULT_UWB_LOSS, 0, 255, T0, 0.0f, 0.0f);
    r = run(&f, &o);
    expect("position fix lost", &r, TT_SF_POS_LOST, T0 + 1.0f, 0.021f, 1);
    o = base();
    o.pose = 1; o.fence[0] = 0.0f; o.fence[1] = 6.0f; o.fence[2] = -5.0f; o.fence[3] = 5.0f;
    r = run(&none, &o);
    printf("geofence: tripped at x = %.3f m\n", r.x_trip);
    CHECK("geofence: trips on crossing x = 6 m", (r.first & TT_SF_GEOFENCE) &&
          r.x_trip > 6.0f && r.x_trip < 6.03f);
    expect("geofence", &r, TT_SF_GEOFENCE, r.t_trip, 0.0f, 1);

    /* ---- latches and recovery ------------------------------------------------ */
    o = base();
    f = mk(TT_FAULT_RC_KILL, 0, 0, T0, 1.0f, 0.0f);
    r = run(&f, &o);
    CHECK("kill switch released: still FAULT (latched)", r.state_end == TT_SAFE_FAULT);
    o.core_stop_s = 5.0f;
    o.run_s = 7.0f;
    r = run(&f, &o);
    CHECK("kill released, core disarms: still FAULT until acknowledged", r.state_end == TT_SAFE_FAULT);
    o.cycle_arm_at_s = 6.0f;
    r = run(&f, &o);
    CHECK("arm switch cycled: latch cleared -> disarmed", r.state_end == TT_SAFE_DISARMED);
    o = base();
    o.run_s = 25.0f;
    o.reset_at_s = 6.0f;
    r = run(&f, &o);
    CHECK("operator reset after a kill: re-arms once at rest, drives again",
          r.state_end == TT_SAFE_DRIVING);
    o = base();
    f = mk(TT_FAULT_RC_LINK, 0, 0, T0, 0.5f, 1.0f);
    r = run(&f, &o);
    printf("RC back after 0.5 s: at rest at %.2f s, end x %.2f m, state %d\n",
           r.t_rest, r.x_end, r.state_end);
    CHECK("RC link back: the stop completes and the car stays stopped (no auto-resume)",
          r.t_rest > 0.0f && fabsf(r.v_end) < 0.01f && r.state_end == TT_SAFE_FAULT);

    printf(g_fail ? "%d check(s) FAILED\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
