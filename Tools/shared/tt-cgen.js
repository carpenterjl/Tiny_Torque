/* =====================================================================
   tt-cgen.js — generate a compilable controller from a vehicle design and
   a set of tuned gains.

   The output follows the conventions already in Controllers/:
     • portable library code includes only <math.h>, <string.h> and pid.h,
       and keeps its state in a caller-owned struct — so the same source
       compiles for an MCU with no changes;
     • only the targets/sim adapter includes controller_api.h, and it is the
       only file that knows about actuator slots or the sensor manifest;
     • every tunable is a #define in one config header, prefixed by what
       kind of number it is (VE_ vehicle, GA_ gains, LI_ limits, SQ_
       sequencing) so it is obvious which ones are measurements and which
       are choices;
     • the debug[] index enum and the ctrl_get_debug_names() string are
       written together from one list, because they must never drift.
   ===================================================================== */
(function (global) {
    'use strict';

    function ident(name) {
        let s = String(name || 'my_controller').toLowerCase()
            .replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
        if (!s) s = 'my_controller';
        if (/^[0-9]/.test(s)) s = 'c_' + s;
        return s;
    }
    function upper(name) { return ident(name).toUpperCase(); }

    function f(v, dp) {
        if (!isFinite(v)) v = 0;
        const s = v.toFixed(dp === undefined ? 6 : dp);
        return s.replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '.0') + 'f';
    }

    /* Very small constants (rotor inertia is ~1e-6) lose all their significant
       digits in fixed notation, so those get scientific form instead. */
    function fsci(v, sig) {
        if (!isFinite(v) || v === 0) return '0.0f';
        const s = v.toExponential(sig === undefined ? 4 : sig);
        // "5.0000e-6" -> "5.0e-6f": drop trailing zeros in the mantissa.
        return s.replace(/\.?0+e/, 'e').replace(/e([+-])(\d)$/, 'e$1$2')
                .replace(/^(\d+)e/, '$1.0e') + 'f';
    }

    /* Everything the generator needs, pulled out of the design once. */
    function extract(design, opts) {
        const S = global.TT.Schema, M = global.TT.Motor;
        const d = S.normalize(design);
        const o = opts || {};
        const stats = S.stats(d);

        const powered = d.wheels.filter(function (w) { return w.powered; });
        const steered = d.wheels.filter(function (w) { return w.allowsSteering; });
        const motor = powered.length ? powered[0].motor : S.newMotor();
        const radius = powered.length ? powered[0].radius : (d.wheels[0] ? d.wheels[0].radius : 0.033);

        let zF = -Infinity, zR = Infinity;
        d.wheels.forEach(function (w) { zF = Math.max(zF, w.localPos.z); zR = Math.min(zR, w.localPos.z); });
        const wheelbase = (zF - zR > 1e-3) ? zF - zR : 0.30;

        const plant = M.plant({
            kt: motor.kt, gearRatio: motor.gearRatio, wheelRadius: radius,
            resistance: motor.resistance, efficiency: motor.efficiency,
            motorCount: Math.max(1, powered.length), rotorInertia: motor.rotorInertia,
            mass: stats.totalMass, maxVoltage: motor.maxVoltage, maxCurrent: motor.maxCurrent
        });

        // Odometry prefers the free-rolling wheels: a driven wheel's encoder
        // reads wheelspin, not ground speed.
        const encoders = d.sensors.filter(function (s) { return s.kind === S.SensorType.Encoder; });
        const freeEnc = encoders.filter(function (s) {
            return d.wheels[s.wheelIndex] && !d.wheels[s.wheelIndex].powered;
        });
        const odoEnc = (freeEnc.length ? freeEnc : encoders).slice(0, 4);

        const batt = d.sensors.filter(function (s) { return s.kind === S.SensorType.Battery; })[0];
        const tof = d.sensors.filter(function (s) { return s.kind === S.SensorType.Tof; });

        return {
            design: d, stats: stats, plant: plant, motor: motor, radius: radius,
            wheelbase: wheelbase, poweredCount: powered.length,
            steerAngle: steered.length ? steered[0].steerAngle : 28,
            hasSteering: steered.length > 0,
            odoEnc: odoEnc, encoders: encoders, battery: batt, tof: tof,
            cpr: odoEnc.length ? odoEnc[0].cprTicks : 4096,
            encGear: odoEnc.length ? odoEnc[0].encoderGearRatio : 1,
            railV: d.batteries.length ? d.batteries[0].nominalV : motor.maxVoltage,
            name: o.name || (d.name || 'my_controller')
        };
    }

    // ---- the config header ------------------------------------------------

    function cfgHeader(ex, gains) {
        const P = upper(ex.name), g = ex.plant;
        const guard = P + '_CFG_H';
        return `/*
 * ${ident(ex.name)}_cfg.h — every tunable for this controller, in one place.
 *
 * Generated from the vehicle "${ex.design.name}" by the Tiny Torque Control Lab.
 *
 * The prefixes matter. VE_ values describe the vehicle and come from its
 * datasheet or its design file — change them only when the hardware changes.
 * CAL_ values are MEASURED on the car and are the ones you re-derive after any
 * mechanical change (see the Calibration Companion). GA_ and LI_ are choices:
 * gains you tuned and limits you imposed.
 *
 * Derived values are written as expressions, never as pre-computed numbers, so
 * that editing a vehicle constant cannot leave a stale derived one behind.
 */
#ifndef ${guard}
#define ${guard}

/* ------------------------------------------------------------- vehicle --
 * Physical facts. If you change a pinion or a tyre, change these.
 */
#define VE_WHEEL_R         ${f(ex.radius, 4)}    /* m, rolling radius */
#define VE_WHEELBASE       ${f(ex.wheelbase, 4)}    /* m, front axle to rear axle */
#define VE_MASS            ${f(ex.stats.totalMass, 4)}    /* kg, all-up */
#define VE_KT              ${f(ex.motor.kt, 7)}  /* N*m/A, torque constant (= back-EMF constant) */
#define VE_GEAR            ${f(ex.motor.gearRatio, 3)}    /* motor : wheel reduction */
#define VE_R_MOTOR         ${f(ex.motor.resistance, 5)}    /* ohm, per SIMULATED motor */
#define VE_ETA             ${f(ex.motor.efficiency, 3)}    /* drivetrain efficiency */
#define VE_N_MOTORS        ${ex.poweredCount}          /* motors the controller commands together */
#define VE_ROTOR_J         ${fsci(ex.motor.rotorInertia)}   /* kg*m^2, per motor */
#define VE_V_RAIL          ${f(ex.railV, 3)}    /* V, nominal pack voltage */
#define VE_ESC_DEADBAND_V  ${f(ex.motor.escDeadbandV, 3)}    /* V, below this the ESC does nothing */
#define VE_MAX_STEER_DEG   ${f(ex.steerAngle, 1)}   /* deg, full lock */
#define VE_ENC_CPR         ${ex.cpr}         /* counts per wheel revolution (post-quadrature) */
#define VE_ENC_WRAP        65536        /* the tick counter wraps here */

/* ------------------------------------------------------------- derived --
 * Never edit these directly — they follow from the block above.
 *
 * A motor turning at road speed v generates VE_BEMF_V_PER_MS volts of back-EMF.
 * That single number is why a speed loop can be mostly feed-forward: at cruise
 * it accounts for the overwhelming majority of the voltage you need to command.
 */
#define VE_BEMF_V_PER_MS     (VE_KT * VE_GEAR / VE_WHEEL_R)          /* ${g.bemfVPerMs.toFixed(4)} V per m/s */
#define VE_FORCE_PER_AMP     (VE_BEMF_V_PER_MS * VE_ETA)             /* ${g.forcePerAmp.toFixed(4)} N per motor-amp */
#define VE_FORCE_PER_AMP_ALL (VE_FORCE_PER_AMP * (float)VE_N_MOTORS) /* ${g.forcePerAmpAll.toFixed(4)} N total */

/* Effective longitudinal inertia. The rotors spin at gear times the wheel rate,
 * so their inertia reflects to the road as N*J*gear^2/r^2 = ${g.reflectedInertia.toFixed(3)} kg
 * on top of the ${ex.stats.totalMass.toFixed(3)} kg of car. Leave it out of your force
 * calculations and every acceleration command comes out ${((g.massEff / Math.max(0.001, ex.stats.totalMass) - 1) * 100).toFixed(0)} % too small.
 */
#define VE_MASS_EFF        (VE_MASS + (float)VE_N_MOTORS * VE_ROTOR_J * \\
                            VE_GEAR * VE_GEAR / (VE_WHEEL_R * VE_WHEEL_R))

/* --------------------------------------------------------- calibration --
 * MEASURED, not calculated. Run the Calibration Companion and paste the
 * results here. The defaults below are placeholders that will get you moving
 * but will not get you accurate.
 */
#define CAL_SCALE          ${f(gains.calScale === undefined ? 0.0 : gains.calScale, 5)}    /* v_ground = v_enc * (1 + CAL_SCALE) */
#define CAL_TRACTION_EFF   ${f(gains.tractionEff === undefined ? 0.99 : gains.tractionEff, 3)}    /* fraction of commanded force that reaches the road */

/* Coast-down drag, F = c0 + c1*v + c2*v^2 (newtons). c0 is rolling and
 * bearing friction, c2 is aerodynamic, c1 absorbs the rest. */
#define VE_DRAG_C0         ${f(gains.dragC0 === undefined ? 0.90 : gains.dragC0, 4)}
#define VE_DRAG_C1         ${f(gains.dragC1 === undefined ? 0.38 : gains.dragC1, 4)}
#define VE_DRAG_C2         ${f(gains.dragC2 === undefined ? 0.015 : gains.dragC2, 4)}

/* -------------------------------------------------------------- limits --
 * Authority caps. These bound what the controller may ask for, which is what
 * keeps a bad measurement from becoming a crash.
 */
#define LI_A_MAX           ${f(gains.aMax, 2)}    /* m/s^2, commanded acceleration clamp */
#define LI_V_MAX           ${f(gains.vMax, 2)}    /* m/s, speed clamp */
#define LI_STEER_RATE_DEG  ${f(ex.design.steerRate, 1)}   /* deg/s, servo slew (informational) */
#define LI_YAW_RATE_MAX    ${f(gains.yawRateMax, 3)}    /* rad/s, yaw-rate command clamp */

/* --------------------------------------------------------------- gains --
 * Tuned in the Control Lab against the plant constants above.
 *
 * These are TRIM gains: the feed-forward path does most of the work, and the
 * PID only corrects what the model got wrong. That is why they can be modest
 * and still hold tight — and why they stay sane when the model is right.
 */
#define GA_SPD_KP          ${f(gains.spdKp, 3)}
#define GA_SPD_KI          ${f(gains.spdKi, 3)}
#define GA_SPD_KD          ${f(gains.spdKd, 3)}
#define GA_SPD_TRIM        ${f(gains.aMax, 2)}    /* m/s^2, clamp on the PID's own contribution */

#define GA_YAW_KP          ${f(gains.yawKp, 3)}
#define GA_YAW_KI          ${f(gains.yawKi, 3)}
#define GA_YAW_KD          ${f(gains.yawKd, 3)}
#define GA_YAW_TRIM_DEG    ${f(gains.yawTrimDeg, 2)}   /* deg of steering the yaw PID may add */
#define GA_YAW_FILT        ${f(gains.yawFilt, 3)}    /* low-pass alpha on the measured yaw rate */

/* ---------------------------------------------------------- sequencing --
 */
#define SQ_ARM_SECONDS     ${f(gains.armSeconds === undefined ? 1.0 : gains.armSeconds, 2)}    /* stationary self-check before moving */
#define SQ_STALL_V         ${f(ex.motor.escDeadbandV + 0.02, 3)}    /* V, minimum useful drive command */

#endif /* ${guard} */
`;
    }

    // ---- the portable library header --------------------------------------

    function libHeader(ex) {
        const id = ident(ex.name), P = upper(ex.name);
        const T = pascal(id);
        return `/*
 * ${id}.h — portable control library for "${ex.design.name}".
 *
 * No simulator headers, no globals: all state lives in a ${T}State the
 * caller owns. Compile this file for the sim target or for an MCU without
 * touching it.
 */
#ifndef ${P}_H
#define ${P}_H

#include "pid.h"

#ifdef __cplusplus
extern "C" {
#endif

/* What the controller is being asked to do this tick. */
typedef struct ${T}Setpoint {
    float speed_mps;      /* target forward speed */
    float yaw_rate_rps;   /* target yaw rate, rad/s, positive = left */
    int   enable;         /* 0 = coast to a stop and hold */
} ${T}Setpoint;

/* What the world looks like this tick. Fill everything you have; the
 * fields you cannot measure should be left at the documented sentinel. */
typedef struct ${T}Meas {
    float dt;             /* seconds since the last call */
    float speed_mps;      /* measured forward speed */
    float yaw_rate_rps;   /* measured yaw rate (gyro), rad/s */
    float batt_v;         /* pack terminal voltage; <= 0 if unknown */
    float range_front_m;  /* nearest obstacle ahead; large if nothing */
} ${T}Meas;

/* What to do about it. */
typedef struct ${T}Cmd {
    float motor_v;        /* volts, signed, applied to every drive motor */
    float steer;          /* [-1, 1], positive = right */
    float brake;          /* [0, 1], friction brake */
} ${T}Cmd;

typedef enum ${T}Phase {
    ${P}_FAULT = -1,
    ${P}_BOOT = 0,
    ${P}_ARMING,
    ${P}_RUN,
    ${P}_HOLD
} ${T}Phase;

typedef struct ${T}State {
    ${T}Phase phase;
    float     t;              /* seconds since init */
    float     phase_t;        /* seconds in the current phase */
    unsigned  fault;          /* bitmask; 0 = healthy */

    Pid   spd_pid;
    Pid   yaw_pid;

    float v_filt;             /* filtered speed */
    float yaw_filt;           /* filtered yaw rate */
    float odo_m;              /* integrated distance */

    /* Last-computed internals, published as debug channels. */
    float a_cmd, f_req, i_cmd, v_ff, steer_ff_deg;
} ${T}State;

/* Fault bits. */
#define ${P}_FAULT_DT      0x01u   /* dt outside anything believable */
#define ${P}_FAULT_BATT    0x02u   /* pack voltage collapsed */
#define ${P}_FAULT_NAN     0x04u   /* a measurement arrived non-finite */

void ${id}_init(${T}State* st);
void ${id}_step(${T}State* st, const ${T}Meas* m, const ${T}Setpoint* sp, ${T}Cmd* out);

#ifdef __cplusplus
}
#endif

#endif /* ${P}_H */
`;
    }

    function pascal(id) {
        return id.split('_').filter(Boolean).map(function (p) {
            return p.charAt(0).toUpperCase() + p.slice(1);
        }).join('');
    }

    // ---- the portable library implementation ------------------------------

    function libSource(ex) {
        const id = ident(ex.name), P = upper(ex.name), T = pascal(id);
        return `/*
 * ${id}.c — the control laws.
 *
 * Two loops, both built the same way: a feed-forward term that inverts a model
 * of the plant, plus a PID that trims whatever the model got wrong.
 *
 *   Longitudinal:  wanted acceleration -> force -> current -> VOLTAGE
 *                  V = (Kt*gear/r)*v + R*I
 *                  The first term is back-EMF and dominates at speed; the
 *                  second is what actually produces new force.
 *
 *   Lateral:       wanted yaw rate -> steering angle, via the kinematic
 *                  bicycle model  delta = atan(L * psi_dot / v)
 *                  The PID absorbs the understeer the kinematic model ignores.
 *
 * Nothing here knows it is in a simulator.
 */
#include "${id}.h"
#include "${id}_cfg.h"

#include <math.h>
#include <string.h>

#define RAD2DEG 57.2957795f
#define DEG2RAD 0.0174532925f

static float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

/* Deliberately not isfinite(): some freestanding libcs do not provide it. */
static int finitef_(float v) { return (v == v) && (v * 0.0f == 0.0f); }

/* Coast-down drag at speed v, signed against motion. */
static float drag_n(float v) {
    float a = v < 0.0f ? -v : v;
    float mag = VE_DRAG_C0 + VE_DRAG_C1 * a + VE_DRAG_C2 * a * a;
    return v < 0.0f ? -mag : mag;
}

static void enter(${T}State* st, ${T}Phase p) {
    st->phase = p;
    st->phase_t = 0.0f;
}

void ${id}_init(${T}State* st) {
    memset(st, 0, sizeof(*st));
    pid_init(&st->spd_pid, GA_SPD_KP, GA_SPD_KI, GA_SPD_KD, -GA_SPD_TRIM, GA_SPD_TRIM);
    pid_init(&st->yaw_pid, GA_YAW_KP, GA_YAW_KI, GA_YAW_KD, -GA_YAW_TRIM_DEG, GA_YAW_TRIM_DEG);
    enter(st, ${P}_BOOT);
}

/* Longitudinal: acceleration -> force -> current -> volts. */
static float longitudinal(${T}State* st, const ${T}Meas* m, float v_ref) {
    float a_trim = pid_update(&st->spd_pid, v_ref, st->v_filt, m->dt);
    float a_cmd  = clampf(a_trim, -LI_A_MAX, LI_A_MAX);

    /* Force needed = accelerate the effective mass, and beat the drag. */
    float f_req   = VE_MASS_EFF * a_cmd + drag_n(st->v_filt);
    float f_motor = f_req / CAL_TRACTION_EFF;
    float i_each  = f_motor / VE_FORCE_PER_AMP_ALL;

    /* The inverse model. The first term is what the motor is already
     * generating by turning; the second is the extra push. */
    float v_bemf = VE_BEMF_V_PER_MS * st->v_filt;
    float v_cmd  = v_bemf + VE_R_MOTOR * i_each;

    /* Below the ESC's deadband nothing happens at all, so a small positive
     * demand must either be pushed up to the deadband or abandoned. */
    if (v_cmd > 0.0f && v_cmd < VE_ESC_DEADBAND_V) {
        v_cmd = (f_motor > 0.05f) ? VE_ESC_DEADBAND_V : 0.0f;
    }

    /* Never command more than the pack can actually deliver. */
    float rail = (m->batt_v > 1.0f) ? m->batt_v : VE_V_RAIL;
    float v_lim = 0.95f * rail;

    st->a_cmd = a_cmd;
    st->f_req = f_req;
    st->i_cmd = i_each;
    st->v_ff  = v_bemf;
    return clampf(v_cmd, -v_lim, v_lim);
}

/* Lateral: yaw rate -> steering, kinematic feed-forward plus a trim. */
static float lateral(${T}State* st, const ${T}Meas* m, float yaw_ref) {
    /* At a standstill the kinematic model divides by zero, and steering does
     * nothing anyway — floor the speed it sees. */
    float v_eff = st->v_filt > 0.3f ? st->v_filt : 0.3f;
    float ff_deg = atan2f(VE_WHEELBASE * yaw_ref, v_eff) * RAD2DEG;

    float trim_deg = pid_update(&st->yaw_pid, yaw_ref, st->yaw_filt, m->dt);
    float deg = ff_deg + trim_deg;

    st->steer_ff_deg = ff_deg;
    /* The host takes positive steer as RIGHT while yaw rate is positive LEFT,
     * so the command is negated exactly once, here. */
    return -clampf(deg / VE_MAX_STEER_DEG, -1.0f, 1.0f);
}

void ${id}_step(${T}State* st, const ${T}Meas* m, const ${T}Setpoint* sp, ${T}Cmd* out) {
    out->motor_v = 0.0f;
    out->steer   = 0.0f;
    out->brake   = 0.0f;
    if (!st || !m || !sp) return;

    /* --- sanity, before anything acts on the numbers --- */
    if (!(m->dt > 0.0f) || m->dt > 0.5f) {
        st->fault |= ${P}_FAULT_DT;
        enter(st, ${P}_FAULT);
    }
    if (!finitef_(m->speed_mps) || !finitef_(m->yaw_rate_rps)) {
        st->fault |= ${P}_FAULT_NAN;
        enter(st, ${P}_FAULT);
    }
    if (m->batt_v > 0.0f && m->batt_v < 0.5f * VE_V_RAIL) {
        st->fault |= ${P}_FAULT_BATT;
        enter(st, ${P}_FAULT);
    }
    if (st->phase == ${P}_FAULT) {
        out->brake = 1.0f;   /* fail safe: stop, do not coast */
        return;
    }

    st->t += m->dt;
    st->phase_t += m->dt;

    /* --- filtering --- */
    st->v_filt = m->speed_mps;
    st->yaw_filt += (m->yaw_rate_rps - st->yaw_filt) * GA_YAW_FILT;
    st->odo_m += st->v_filt * m->dt;

    switch (st->phase) {
    case ${P}_BOOT:
        pid_reset(&st->spd_pid);
        pid_reset(&st->yaw_pid);
        enter(st, ${P}_ARMING);
        break;

    case ${P}_ARMING:
        /* Sit still long enough to prove the loop is running and the
         * measurements are steady before commanding anything. */
        out->brake = 1.0f;
        if (st->phase_t >= SQ_ARM_SECONDS) enter(st, ${P}_RUN);
        break;

    case ${P}_RUN: {
        if (!sp->enable) { enter(st, ${P}_HOLD); break; }

        float v_ref = clampf(sp->speed_mps, -LI_V_MAX, LI_V_MAX);
        float yaw_ref = clampf(sp->yaw_rate_rps, -LI_YAW_RATE_MAX, LI_YAW_RATE_MAX);

        out->motor_v = longitudinal(st, m, v_ref);
        out->steer   = lateral(st, m, yaw_ref);

        /* Asking for a big slowdown that the motor alone cannot deliver:
         * blend in the friction brake rather than just coasting. */
        if (st->a_cmd < -1.0f && st->v_filt > 0.2f) {
            out->brake = clampf((-st->a_cmd - 1.0f) / LI_A_MAX, 0.0f, 1.0f);
        }
        break;
    }

    case ${P}_HOLD:
        out->brake = 1.0f;
        pid_reset(&st->spd_pid);
        pid_reset(&st->yaw_pid);
        if (sp->enable) enter(st, ${P}_RUN);
        break;

    default:
        break;
    }
}
`;
    }

    // ---- the sim adapter ---------------------------------------------------

    function simMain(ex) {
        const id = ident(ex.name), P = upper(ex.name), T = pascal(id);

        const encNames = ex.odoEnc.map(function (s) { return s.name; });
        const encBinds = encNames.map(function (n, i) {
            return `        else if (strcmp(sensors[i].name, "${n}") == 0) g_off_enc[${i}] = sensors[i].data_offset;`;
        }).join('\n');
        const encList = encNames.length ? encNames.join(', ') : '(none — falling back to wheel_vel)';

        const battBind = ex.battery
            ? `        else if (strcmp(sensors[i].name, "${ex.battery.name}") == 0) g_off_batt = sensors[i].data_offset;`
            : '';
        const tofBind = ex.tof.length
            ? `        else if (strcmp(sensors[i].name, "${ex.tof[0].name}") == 0) g_off_tof = sensors[i].data_offset;`
            : '';

        return `/*
 * ${id}_main.c — the only file that talks to the simulator.
 *
 * Its whole job is translation: turn the host's sensor manifest and input
 * struct into the plain ${T}Meas the library understands, and turn the
 * library's ${T}Cmd back into actuator slots. Everything above this line is
 * portable; everything below it is host-specific.
 *
 * Sensors are bound BY NAME. These names come from the vehicle design, so if
 * you rename a sensor in the garage you must rename it here too:
 *     encoders used for odometry: ${encList}
 *
 * Setpoint convention (what the host's driver input provides):
 *     setpoint[0] = target forward speed (m/s)
 *     setpoint[1] = steer command [-1, 1]
 */
#include "controller_api.h"
#include "${id}.h"
#include "${id}_cfg.h"

#include <string.h>
#include <math.h>

#define MAX_MOTORS 8
#define SP_SPEED 0
#define SP_STEER 1

/* debug[] layout — this enum and the string in ctrl_get_debug_names() are one
 * list written twice. Change them together or the graphs mislabel themselves. */
enum {
    DBG_PHASE = 0,
    DBG_FAULT,
    DBG_TARGET_SPEED,
    DBG_SPEED,
    DBG_SPEED_ERR,
    DBG_A_CMD,
    DBG_F_REQ,
    DBG_I_CMD,
    DBG_MOTOR_V,
    DBG_V_FF,
    DBG_YAW_REF,
    DBG_YAW_RATE,
    DBG_STEER_FF,
    DBG_STEER_CMD,
    DBG_ODO_M,
    DBG_BATT_V,
    DBG_COUNT
};

static ${T}State g_ctl;
static int g_ready = 0;

static int   g_motor_actuator[MAX_MOTORS];
static float g_motor_vmax[MAX_MOTORS];
static int   g_motor_count = 0;

/* Flat offsets into CtrlInputs.sensor_data, resolved once in ctrl_configure. */
static int g_off_enc[4] = { -1, -1, -1, -1 };
static int g_off_batt = -1;
static int g_off_tof = -1;

static float clampf(float v, float lo, float hi) {
    return v < lo ? lo : (v > hi ? hi : v);
}

/* Bounds-checked read out of the flat sensor block. */
static float slice(const CtrlInputs* in, int off, int idx, float fallback) {
    if (off < 0 || in->sensor_data == 0) return fallback;
    if (off + idx >= in->sensor_data_len) return fallback;
    return in->sensor_data[off + idx];
}

CTRL_EXPORT int ctrl_init(float control_rate_hz) {
    (void)control_rate_hz;
    ${id}_init(&g_ctl);
    g_motor_count = 0;
    g_off_enc[0] = g_off_enc[1] = g_off_enc[2] = g_off_enc[3] = -1;
    g_off_batt = -1;
    g_off_tof = -1;
    g_ready = 1;
    return 0;
}

CTRL_EXPORT void ctrl_configure(const SensorInfo* sensors, int count) {
    g_motor_count = 0;
    for (int i = 0; i < count; i++) {
        if (sensors[i].type == SENSOR_MOTOR) {
            if (g_motor_count < MAX_MOTORS &&
                sensors[i].actuator_index >= 0 && sensors[i].actuator_index < 6) {
                g_motor_actuator[g_motor_count] = sensors[i].actuator_index;
                g_motor_vmax[g_motor_count] = sensors[i].range_max;
                g_motor_count++;
            }
        }
${encBinds || '        /* no encoders bound — speed comes from wheel_vel */'}
${battBind}
${tofBind}
    }
}

CTRL_EXPORT void ctrl_step(const CtrlInputs* in, CtrlOutputs* out) {
    memset(out, 0, sizeof(*out));
    if (!g_ready || in == 0) return;

    /* --- speed: prefer the encoders, fall back to the ABI wheel velocities --- */
    float speed = 0.0f;
    int used = 0;
    for (int i = 0; i < 4; i++) {
        if (g_off_enc[i] < 0) continue;
        /* Encoder slice is [angular_velocity_rad_s, tick_count]. */
        speed += slice(in, g_off_enc[i], 0, 0.0f) * VE_WHEEL_R;
        used++;
    }
    if (used > 0) {
        speed /= (float)used;
        /* Free-rolling wheels under-read the ground because they still drag a
         * little. CAL_SCALE is the measured correction. */
        speed *= (1.0f + CAL_SCALE);
    } else {
        speed = 0.25f * (in->wheel_vel[0] + in->wheel_vel[1] +
                         in->wheel_vel[2] + in->wheel_vel[3]) * VE_WHEEL_R;
    }

    ${T}Meas m;
    m.dt = in->dt_s;
    m.speed_mps = speed;
    /* Unity is left-handed with Y up, so a LEFT turn shows up as a NEGATIVE
     * gyro[1]; the library wants positive-is-left, hence the sign flip. */
    m.yaw_rate_rps = -in->gyro[1];
    m.batt_v = slice(in, g_off_batt, 0, -1.0f);
    m.range_front_m = slice(in, g_off_tof, 0, 1e6f);

    /* --- what we are being asked for --- */
    ${T}Setpoint sp;
    sp.speed_mps = in->setpoint[SP_SPEED];
    /* The driver's steer stick is an angle request; convert it to the yaw rate
     * that angle would produce at the current speed, so the loop below is
     * always closing on a RATE rather than on a position. */
    {
        float steer_cmd = clampf(in->setpoint[SP_STEER], -1.0f, 1.0f);
        float delta_rad = -steer_cmd * VE_MAX_STEER_DEG * 0.0174532925f;
        float v_eff = speed > 0.3f ? speed : 0.3f;
        sp.yaw_rate_rps = v_eff * tanf(delta_rad) / VE_WHEELBASE;
    }
    sp.enable = 1;

    ${T}Cmd cmd;
    ${id}_step(&g_ctl, &m, &sp, &cmd);

    /* --- drive the actuators --- */
    for (int k = 0; k < g_motor_count; k++) {
        int idx = g_motor_actuator[k];
        float vmax = g_motor_vmax[k] > 0.1f ? g_motor_vmax[k] : VE_V_RAIL;
        out->actuator[idx] = clampf(cmd.motor_v, -vmax, vmax);
    }
    out->actuator[CTRL_STEER_ACTUATOR] = clampf(cmd.steer, -1.0f, 1.0f);
    out->actuator[CTRL_BRAKE_ACTUATOR] = clampf(cmd.brake, 0.0f, 1.0f);

    /* --- telemetry --- */
    out->debug[DBG_PHASE]        = (float)g_ctl.phase;
    out->debug[DBG_FAULT]        = (float)g_ctl.fault;
    out->debug[DBG_TARGET_SPEED] = sp.speed_mps;
    out->debug[DBG_SPEED]        = m.speed_mps;
    out->debug[DBG_SPEED_ERR]    = sp.speed_mps - m.speed_mps;
    out->debug[DBG_A_CMD]        = g_ctl.a_cmd;
    out->debug[DBG_F_REQ]        = g_ctl.f_req;
    out->debug[DBG_I_CMD]        = g_ctl.i_cmd;
    out->debug[DBG_MOTOR_V]      = cmd.motor_v;
    out->debug[DBG_V_FF]         = g_ctl.v_ff;
    out->debug[DBG_YAW_REF]      = sp.yaw_rate_rps;
    out->debug[DBG_YAW_RATE]     = g_ctl.yaw_filt;
    out->debug[DBG_STEER_FF]     = g_ctl.steer_ff_deg;
    out->debug[DBG_STEER_CMD]    = cmd.steer;
    out->debug[DBG_ODO_M]        = g_ctl.odo_m;
    out->debug[DBG_BATT_V]       = m.batt_v;
}

CTRL_EXPORT void ctrl_shutdown(void) {
    g_ready = 0;
}

CTRL_EXPORT const char* ctrl_get_debug_names(void) {
    /* "target_speed" is a deliberate choice of name: the host's telemetry
     * exporter looks for exactly that channel to compute rise time, overshoot
     * and settling time for free. */
    return "phase,fault,target_speed,speed,speed_err,a_cmd,f_req,i_cmd,"
           "motor_v,v_ff,yaw_ref,yaw_rate,steer_ff,steer_cmd,odo_m,batt_v";
}
`;
    }

    // ---- build instructions -------------------------------------------------

    function buildNotes(ex) {
        const id = ident(ex.name);
        return `Build instructions for ${id}_controller.dll
${'='.repeat(40 + id.length)}

1. Copy the files into the Controllers tree:

       Controllers/${id}/${id}.h
       Controllers/${id}/${id}.c
       Controllers/${id}/${id}_cfg.h
       Controllers/targets/sim/${id}_main.c

2. Add a library and a controller target to Controllers/CMakeLists.txt,
   next to the existing ones:

       add_library(${id}lib STATIC ${id}/${id}.c)
       target_include_directories(${id}lib PUBLIC ${id})
       target_link_libraries(${id}lib PUBLIC pidlib)

       add_controller(${id}_controller targets/sim/${id}_main.c ${id}lib)

3. Build:

       ./build.ps1 -Target ${id}_controller

   The DLL is copied into UnitySim/Assets/Plugins/x86_64/ automatically.
   If the build cannot find a 64-bit compiler:

       winget install --id BrechtSanders.WinLibs.POSIX.UCRT --exact

4. Point the vehicle at it. In "${ex.design.name}"'s JSON set:

       "controllerDll": "${id}_controller.dll"

   or edit the field in the Car Setup tool's step 7 and re-export.

5. Drive it. Load the vehicle, switch to Autonomous, and watch the
   debug channels — the graph overlay picks them up by name.

Notes
-----
* The sim hot-reloads the DLL, so you can rebuild while the editor is open.
* ${ex.odoEnc.length
        ? 'Odometry reads these encoders by name: ' + ex.odoEnc.map(function (s) { return s.name; }).join(', ') +
          '. Rename them in the garage and you must rename them in ' + id + '_main.c too.'
        : 'This vehicle has no encoders, so speed comes from the ABI wheel_vel channel. Fitting encoders and re-generating gives you a better measurement.'}
* CAL_SCALE and the drag polynomial in ${id}_cfg.h are placeholders until you
  measure them. Until then the feed-forward is approximate and the PID is
  doing more work than it should.
`;
    }

    function generate(design, gains, opts) {
        const ex = extract(design, opts);
        const id = ident(ex.name);
        return {
            id: id,
            files: [
                { name: id + '_cfg.h', text: cfgHeader(ex, gains), lang: 'c' },
                { name: id + '.h', text: libHeader(ex), lang: 'c' },
                { name: id + '.c', text: libSource(ex), lang: 'c' },
                { name: id + '_main.c', text: simMain(ex), lang: 'c' },
                { name: 'BUILD.txt', text: buildNotes(ex), lang: 'text' }
            ],
            extract: ex
        };
    }

    global.TT = global.TT || {};
    global.TT.CGen = {
        generate: generate, ident: ident, extract: extract,
        cfgHeader: cfgHeader, libHeader: libHeader, libSource: libSource, simMain: simMain
    };
})(typeof window !== 'undefined' ? window : globalThis);
