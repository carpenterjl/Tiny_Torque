/* =====================================================================
   tt-motor.js — brushed-DC motor maths shared by the setup wizard, the
   converter page and the control lab.

   Mirrors UnitySim/.../Vehicles/MotorModel.cs (datasheet ⇄ constants) and
   the derived-constant block of Controllers/opus_mission/mission_cfg.h
   (the plant constants firmware feed-forward is built on).
   ===================================================================== */
(function (global) {
    'use strict';

    const TWO_PI_OVER_60 = Math.PI * 2 / 60;
    const RPM_PER_RAD_S = 60 / (Math.PI * 2);

    // ---- Kv ⇄ Kt --------------------------------------------------------

    /* Kv (rpm per volt, unloaded) → torque constant. Kt and Ke are the same
       number in SI: Kt [N·m/A] = Ke [V·s/rad] = 60 / (2π·Kv). */
    function ktFromKv(kv) { return kv > 0 ? 60 / (2 * Math.PI * kv) : 0; }
    function kvFromKt(kt) { return kt > 0 ? 60 / (2 * Math.PI * kt) : 0; }

    // ---- Datasheet ⇄ constants (MotorModel.ApplyDatasheet / ToDatasheet) --

    /* Vn, stall torque τs, no-load speed ω0 and no-load current I0 →
       winding resistance and torque constant:

           R  = Vn² / (τs·ω0 + Vn·I0)
           Kt = τs·R / Vn

       Derivation: at stall the whole rail drops across R, so τs = Kt·Vn/R;
       at no load the rail balances back-EMF plus the I0·R drop, so
       Vn = Kt·ω0 + I0·R. Eliminating Kt gives the R expression.
       Clamps match the C# so the two agree on degenerate input. */
    function applyDatasheet(ds) {
        const vn = Math.max(0.01, ds.nominalVoltage);
        const w0 = Math.max(1e-3, ds.noLoadRpm * TWO_PI_OVER_60);   // rad/s, motor shaft
        const ts = Math.max(1e-4, ds.stallTorque);
        const i0 = Math.max(0, ds.noLoadCurrent);
        const r = vn * vn / (ts * w0 + vn * i0);
        const k = ts * r / vn;
        return { resistance: r, kt: k, maxVoltage: vn, noLoadCurrent: i0 };
    }

    /* Inverse, for showing datasheet-style figures next to constants. */
    function toDatasheet(p) {
        const r = Math.max(1e-3, p.resistance);
        const k = Math.max(1e-4, p.kt);
        const vn = p.maxVoltage;
        const w0 = (vn - p.noLoadCurrent * r) / k;   // rad/s
        return {
            nominalVoltage: vn,
            stallTorque: k * (vn / r),
            noLoadRpm: Math.max(0, w0) * RPM_PER_RAD_S,
            noLoadCurrent: p.noLoadCurrent
        };
    }

    // ---- Unit helpers a datasheet actually uses --------------------------

    function kgcmToNm(kgcm) { return kgcm * 0.0980665; }        // servo torque
    function nmToKgcm(nm) { return nm / 0.0980665; }
    function ozinToNm(ozin) { return ozin * 0.00706155; }
    /* Servo speed specs are "seconds per 60°" — invert for deg/s. */
    function secPer60ToDegPerSec(s) { return s > 0 ? 60 / s : 0; }
    function degPerSecToSecPer60(dps) { return dps > 0 ? 60 / dps : 0; }
    function gcm2ToKgm2(gcm2) { return gcm2 * 1e-7; }           // rotor inertia

    // ---- Plant constants (mission_cfg.h derived block) -------------------

    /* Everything a longitudinal controller needs, derived from the motor
       constants + wheel radius + how many motors share the job. These are
       the numbers the generated *_cfg.h will carry.

         BEMF_V_PER_MS  = kt·gear/r    volts of back-EMF per m/s of road speed
         FORCE_PER_AMP  = BEMF·η       newtons at the contact patch per amp
         massEff        = m + N·J·gear²/r²   (reflected rotor inertia)
         tauElec        = L/R (unknown here — reported only if L is given)
         tauMech        = massEff·R·r² / (kt²·gear²·N·η)   first-order speed lag
    */
    function plant(opts) {
        const kt = Math.max(1e-6, opts.kt);
        const gear = Math.max(1e-3, opts.gearRatio);
        const r = Math.max(1e-3, opts.wheelRadius);
        const R = Math.max(1e-4, opts.resistance);
        const eta = opts.efficiency > 0 ? Math.min(1, opts.efficiency) : 1;
        const n = Math.max(1, opts.motorCount || 1);
        const J = Math.max(0, opts.rotorInertia || 0);
        const mass = Math.max(0.01, opts.mass || 1);

        const bemfVPerMs = kt * gear / r;
        const forcePerAmp = bemfVPerMs * eta;
        const forcePerAmpAll = forcePerAmp * n;
        const reflectedInertia = n * J * gear * gear / (r * r);
        const massEff = mass + reflectedInertia;
        // Free-acceleration time constant of the speed loop, open-loop.
        const tauMech = massEff * R / (bemfVPerMs * forcePerAmpAll);
        // Speed the rail alone can sustain against back-EMF (no load, no drag).
        const noLoadSpeed = opts.maxVoltage > 0 ? opts.maxVoltage / bemfVPerMs : 0;
        const stallCurrent = opts.maxVoltage > 0 ? opts.maxVoltage / R : 0;
        const escStall = opts.maxCurrent > 0 ? Math.min(stallCurrent, opts.maxCurrent) : stallCurrent;
        const stallForceAll = escStall * forcePerAmpAll;

        return {
            bemfVPerMs: bemfVPerMs,
            forcePerAmp: forcePerAmp,
            forcePerAmpAll: forcePerAmpAll,
            reflectedInertia: reflectedInertia,
            massEff: massEff,
            tauMech: tauMech,
            noLoadSpeed: noLoadSpeed,
            stallCurrent: stallCurrent,
            escStallCurrent: escStall,
            stallForceAll: stallForceAll,
            launchAccel: stallForceAll / massEff,
            motorCount: n, kt: kt, gearRatio: gear, wheelRadius: r,
            resistance: R, efficiency: eta, mass: mass,
            maxVoltage: opts.maxVoltage || 0
        };
    }

    /* The inverse model firmware uses to turn a wanted force into a voltage:
           V = (kt·gear/r)·v + R·I,   I = F_total / (forcePerAmp·N)
       ~95 % of the demand at cruise is the back-EMF term, which is why this
       loop can be accurate with only a small PID trim on top. */
    function voltageForForce(p, speedMs, forceN) {
        const iEach = forceN / Math.max(1e-6, p.forcePerAmpAll);
        return {
            voltage: p.bemfVPerMs * speedMs + p.resistance * iEach,
            currentEach: iEach,
            feedForwardShare: p.bemfVPerMs * speedMs /
                Math.max(1e-9, Math.abs(p.bemfVPerMs * speedMs + p.resistance * iEach))
        };
    }

    // ---- One real motor spread across N simulated motors ------------------

    /* The sim gives every powered wheel its own motor, but a real RC car
       usually has ONE motor driving an axle through a differential. To make
       the pair reproduce the single real motor, EXTENSIVE quantities are
       split and INTENSIVE ones are left alone (the Opus Vector convention):

         resistance   × N   (N parallel current paths must total the real R)
         noLoadCurrent ÷ N  (the real idle draw is shared)
         maxCurrent   ÷ N   (the real ESC limit is shared)
         rotorInertia ÷ N   (one real rotor, split across N models)
         kt, gearRatio, efficiency, maxVoltage — unchanged (intensive)

       Without this, N sim motors would produce N× the real thrust. */
    function splitAcrossMotors(realMotor, n) {
        const k = Math.max(1, n | 0);
        const out = Object.assign({}, realMotor);
        out.resistance = realMotor.resistance * k;
        out.noLoadCurrent = realMotor.noLoadCurrent / k;
        if (realMotor.maxCurrent > 0) out.maxCurrent = realMotor.maxCurrent / k;
        out.rotorInertia = (realMotor.rotorInertia || 0) / k;
        return out;
    }

    /* Undo the split — useful when reading an existing design back and
       showing the user what the real part was. */
    function combineFromMotors(simMotor, n) {
        const k = Math.max(1, n | 0);
        const out = Object.assign({}, simMotor);
        out.resistance = simMotor.resistance / k;
        out.noLoadCurrent = simMotor.noLoadCurrent * k;
        if (simMotor.maxCurrent > 0) out.maxCurrent = simMotor.maxCurrent * k;
        out.rotorInertia = (simMotor.rotorInertia || 0) * k;
        return out;
    }

    // ---- A few real parts, for the wizard's one-click presets -------------
    // Every figure is a published datasheet number unless marked derived.

    const MotorPresets = [
        {
            name: 'Castle 1410-3800Kv (Opus Vector)',
            blurb: '4-pole sensored brushless, 2–3S. The motor in the reference car.',
            kv: 3800, resistance: 0.030, noLoadCurrent: 1.8, rotorInertia: 6.44e-6,
            maxVoltage: 7.4, maxCurrent: 60, gearRatio: 11.2, efficiency: 0.85,
            note: 'Kt derived from Kv; R and J estimated (vendors rarely publish either).'
        },
        {
            name: '540-class brushed (stock RC)',
            blurb: 'The classic silver-can 540 on 2S — the sim default.',
            kv: 3180, resistance: 0.09, noLoadCurrent: 1.2, rotorInertia: 5e-6,
            maxVoltage: 7.4, maxCurrent: 40, gearRatio: 8, efficiency: 0.85,
            note: 'Matches MotorParams.Default().'
        },
        {
            name: '550-class brushed (torque)',
            blurb: 'Longer can, more torque, lower revs — crawler/heavy-buggy staple.',
            kv: 2200, resistance: 0.065, noLoadCurrent: 1.5, rotorInertia: 9e-6,
            maxVoltage: 7.4, maxCurrent: 60, gearRatio: 12, efficiency: 0.82,
            note: 'Estimated from typical 550 specs.'
        },
        {
            name: '3650 brushless 4300Kv',
            blurb: 'Common 1/10 sensorless racing can on 2S.',
            kv: 4300, resistance: 0.021, noLoadCurrent: 1.9, rotorInertia: 7e-6,
            maxVoltage: 7.4, maxCurrent: 80, gearRatio: 10, efficiency: 0.87,
            note: 'R/J estimated.'
        },
        {
            name: 'N20 gearmotor (micro)',
            blurb: 'Tiny geared DC motor for desk-scale rovers.',
            kv: 1000, resistance: 3.2, noLoadCurrent: 0.06, rotorInertia: 2e-8,
            maxVoltage: 6, maxCurrent: 1.5, gearRatio: 30, efficiency: 0.6,
            note: 'Gear ratio is the built-in gearbox.'
        }
    ];

    /* Turn a preset (or the same fields typed by hand) into a complete
       MotorParams object ready for a WheelSpec. */
    function motorFromPreset(p, over) {
        const m = Object.assign({}, global.TT.Schema.MOTOR_DEFAULT, {
            maxVoltage: p.maxVoltage,
            kt: ktFromKv(p.kv),
            resistance: p.resistance,
            gearRatio: p.gearRatio,
            noLoadCurrent: p.noLoadCurrent,
            efficiency: p.efficiency,
            maxCurrent: p.maxCurrent,
            rotorInertia: p.rotorInertia
        }, over || {});
        return m;
    }

    const ServoPresets = [
        { name: 'Savox SC-1251MG (Opus Vector)', secPer60: 0.09, voltage: 6.0, stallKgcm: 9.0, blurb: 'Low-profile digital metal-gear — the reference car\'s servo.' },
        { name: 'Savox SC-1258TG', secPer60: 0.08, voltage: 6.0, stallKgcm: 12.0, blurb: 'Titanium gear, quicker and stronger.' },
        { name: 'Hobby standard (analog)', secPer60: 0.16, voltage: 6.0, stallKgcm: 5.0, blurb: 'Budget analog servo — slow enough to feel in the steering.' },
        { name: 'MG996R', secPer60: 0.17, voltage: 6.0, stallKgcm: 11.0, blurb: 'Ubiquitous cheap metal-gear servo.' }
    ];

    const BatteryPresets = [
        { name: '2S 5200 mAh shorty (Opus Vector)', cells: 2, capacitymAh: 5200, internalR: 0.020, massKg: 0.265 },
        { name: '2S 1300 mAh', cells: 2, capacitymAh: 1300, internalR: 0.030, massKg: 0.180 },
        { name: '2S 3000 mAh', cells: 2, capacitymAh: 3000, internalR: 0.024, massKg: 0.200 },
        { name: '3S 2200 mAh', cells: 3, capacitymAh: 2200, internalR: 0.028, massKg: 0.210 },
        { name: '1S 1000 mAh (micro)', cells: 1, capacitymAh: 1000, internalR: 0.060, massKg: 0.030 }
    ];
    function packNominalV(cells) { return cells * 3.7; }

    global.TT = global.TT || {};
    global.TT.Motor = {
        ktFromKv: ktFromKv, kvFromKt: kvFromKt,
        applyDatasheet: applyDatasheet, toDatasheet: toDatasheet,
        kgcmToNm: kgcmToNm, nmToKgcm: nmToKgcm, ozinToNm: ozinToNm,
        secPer60ToDegPerSec: secPer60ToDegPerSec, degPerSecToSecPer60: degPerSecToSecPer60,
        gcm2ToKgm2: gcm2ToKgm2,
        plant: plant, voltageForForce: voltageForForce,
        splitAcrossMotors: splitAcrossMotors, combineFromMotors: combineFromMotors,
        MotorPresets: MotorPresets, motorFromPreset: motorFromPreset,
        ServoPresets: ServoPresets, BatteryPresets: BatteryPresets, packNominalV: packNominalV,
        RPM_PER_RAD_S: RPM_PER_RAD_S, TWO_PI_OVER_60: TWO_PI_OVER_60
    };
})(typeof window !== 'undefined' ? window : globalThis);
