using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Brushed-DC motor electrical model plus datasheet↔constants conversion.
    ///
    /// The canonical parameters are the electrical constants (Kt, R, gear, …).
    /// Torque is produced by driving a commanded voltage against the motor's
    /// back-EMF at the current shaft speed, so the achievable torque/current is
    /// an emergent function of how fast the wheel is actually turning — which the
    /// vehicle physics decides. That closes the electrical loop through the sim:
    ///
    ///   I  = (V − Kt·ω_motor) / R          (back-EMF opposes applied voltage)
    ///   τ  = (Kt·I − b·ω_motor) · gear · η  (electromechanical torque, geared)
    ///
    /// with the current clamped to the stall current ±V/R.
    /// </summary>
    [System.Serializable]
    public struct MotorParams
    {
        public float maxVoltage;      // supply rail (V); |command| clamps here
        public float kt;              // torque constant Kt = Ke (N·m/A = V·s/rad)
        public float resistance;      // winding resistance R (Ω)
        public float gearRatio;       // motor:wheel reduction (ω_motor = gear·ω_wheel)
        public float noLoadCurrent;   // I0 (A) — friction/iron losses at no load
        public float viscousDamping;  // b (N·m·s/rad) on the motor shaft
        public float efficiency;      // gearbox efficiency (0..1)
        public float maxCurrent;      // ESC current limit (A); 0 = unlimited
                                      // (old JSON deserializes to 0 → back-compat)

        // ---- Iteration 13 realism (all 0 on old JSON = legacy behaviour) ----
        public float coulombScale;    // Coulomb friction scale; Tc = scale·kt·I0. 0 = none
        public float rotorInertia;    // rotor inertia J (kg·m², motor shaft); 0 = none
        public int escPwmSteps;       // ESC PWM resolution (voltage steps); 0 = continuous
        public float escDeadbandV;    // ESC input deadband (V); 0 = none
        public float escTimeConstMs;  // ESC first-order lag time constant (ms); 0 = instant
        public float escSlewVPerS;    // ESC output slew limit (V/s); 0 = unlimited

        // ---- Iteration 22: hobby-ESC drive/brake/reverse behaviour ----
        // A real car ESC never drives against rotation: opposite-sign throttle
        // while moving is a proportional (shorted-winding) brake, and reverse
        // engages only after a dwell in neutral at rest.
        // Wheel speed above which the ESC treats the car as MOVING (rad/s);
        // 0 = the legacy 2 rad/s. That literal encodes a GROUND speed, not a
        // wheel speed: 2 rad/s is 0.07 m/s on a 33 mm wheel but 0.70 m/s on a
        // 349 mm one, so a full-scale car left on it is still "stationary" at
        // walking pace and the ESC drives when it should brake — which breaks
        // the end of every braking measurement.
        public float escMovingOmega;

        public float escDragBrakePct;     // brake duty at neutral while spinning (%); 0 = coast
        public float escBrakeStrengthPct; // full-brake duty scale (%); ≤0 = 100 (old JSON)
        public float escReverseLockMs;    // neutral dwell before reverse engages; ≤0 = 150 (old JSON)

        // ---- Phase 2 (sim-to-real ACT-01..07): what drives this motor ----
        // Every field below is 0 in old JSON, and 0 means "as before", except
        // that a hobby ESC at neutral with no drag brake now coasts (ACT-03).

        /// <summary><see cref="MotorDriveMode"/> as an int (JsonUtility-safe).
        /// 0 = HobbyEsc, the only kind there was.</summary>
        public int driveMode;
        /// <summary>Gearbox efficiency when the WHEEL drives the motor (braking,
        /// regen). The wheel then feels τ_m·G/η_back: the losses add to the
        /// braking. 0 = the legacy ×efficiency both ways (BUG-05) on the voltage
        /// drives; an FOC drive always divides, by <c>efficiency</c> if unset.</summary>
        public float etaBack;

        // FOC current drive (driveMode = CurrentFoc). The command is Iq in amps.
        public int polePairs;            // electrical / mechanical; 0 → 7
        public float inductance;         // phase L (H); 0 = no cross-coupling term
        public float currentLoopHz;      // closed current-loop bandwidth; 0 = instant
        public float cmdPeriodMs;        // command sample-and-hold period; 0 = every step
        public float cmdLatencyMs;       // transport delay, MCU frame → driver; 0 = none
        public float maxRegenCurrent;    // braking-current limit (A); 0 = maxCurrent
        public float modulationMax;      // usable fraction of V_bus; 0 → 0.95
        public float busOvTripV;         // over-voltage fault (V); 0 = none
        public float busOvDerateV;       // regen fades from here to the trip; 0 = trip − 0.3 V
        public float cmdTimeoutMs;       // driver watchdog: no command frame this long
                                         // = bridge off (fault 16); 0 = none

        // Thermal (ACT-05): winding ↔ case ↔ ambient. R_wc = 0 = model off.
        public float thermalRwcKPerW;    // winding → case
        public float thermalCwJPerK;     // winding heat capacity
        public float thermalRcaKPerW;    // case → ambient
        public float thermalCcJPerK;     // case heat capacity
        public float tempDerateStartC;   // current limits derate linearly from here…
        public float tempLimitC;         // …to zero here; 0 = no derate

        // Cogging and torque ripple (ACT-06), motor shaft. 0 = off.
        public float coggingNm;          // cogging amplitude
        public int coggingPerRev;        // cogging periods per mechanical turn
        public float rippleFrac;         // 6th-harmonic ripple as a fraction of kt·Iq

        // Gear lash (ACT-07), referred to the WHEEL: total free play (rad).
        // 0 = rigid, the rotor inertia riding on the wheel as before. The teeth
        // are rigid once in contact and engage inelastically — the stiff-spring
        // limit, which needs no sub-stepping.
        public float lashRad;

        // A 540-class brushed motor on a 2S LiPo (the 1/10 RC / F1TENTH staple):
        // ~23,000 rpm no-load, 82 A stall (ESC-clamped to 40 A), 8:1 reduction.
        // Two on the rear wheels top a ~1.8 kg car out near ~10 m/s.
        public static MotorParams Default()
        {
            return new MotorParams
            {
                maxVoltage = 7.4f,
                kt = 0.003f,
                resistance = 0.09f,
                gearRatio = 8f,
                noLoadCurrent = 1.2f,
                viscousDamping = 1e-6f,
                efficiency = 0.85f,
                maxCurrent = 40f,
                coulombScale = 1f,
                rotorInertia = 5e-6f,    // 540-class rotor ≈ 50 g·cm²
                escPwmSteps = 1024,
                escDeadbandV = 0.10f,
                escTimeConstMs = 5f,
                escSlewVPerS = 0f,       // lag dominates; slew off by default
                escDragBrakePct = 0f,    // hobby ESCs ship with drag brake off
                escBrakeStrengthPct = 100f,
                escReverseLockMs = 150f,
            };
        }
    }

    /// <summary>What sits between the firmware's command and the motor.</summary>
    public enum MotorDriveMode
    {
        /// <summary>Volts through a hobby ESC: deadband, PWM, lag, and the
        /// drive/brake/reverse state machine. The legacy model.</summary>
        HobbyEsc = 0,
        /// <summary>Signed volts straight onto the winding, four-quadrant, no
        /// state machine (a bench supply or an H-bridge with no brake logic).</summary>
        Voltage4Q = 1,
        /// <summary>An FOC driver with a closed current loop. The command is Iq
        /// (A); negative while rolling forward is regen braking, reversing has
        /// no lockout, and zero current coasts.</summary>
        CurrentFoc = 2,
    }

    /// <summary>Datasheet-style figures, an alternate way to specify a motor.</summary>
    [System.Serializable]
    public struct MotorDatasheet
    {
        public float nominalVoltage;  // Vn (V)
        public float stallTorque;     // τs at Vn, motor shaft (N·m)
        public float noLoadRpm;       // ω0 at Vn, motor shaft (rev/min)
        public float noLoadCurrent;   // I0 (A)
    }

    public static class MotorModel
    {
        private const float TwoPiOver60 = Mathf.PI * 2f / 60f;

        /// <summary>
        /// Compute geared wheel torque for a commanded voltage at the current wheel
        /// speed, and report the electrical operating point (V, I) actually seen.
        /// <paramref name="railV"/> is the live bus voltage when a battery is
        /// modelled (a fresh 2S pack sits at 8.4 V, above the 7.4 V nominal);
        /// 0 = no battery, so the nominal <c>maxVoltage</c> is the rail.
        /// </summary>
        public static float WheelTorque(in MotorParams p, float commandedVoltage, float wheelOmega,
                                        out float voltage, out float current, float railV = 0f)
        {
            float vmax = Mathf.Max(0.01f, railV > 0f ? railV : p.maxVoltage);
            voltage = Mathf.Clamp(commandedVoltage, -vmax, vmax);

            float r = Mathf.Max(1e-3f, p.resistance);
            float gear = Mathf.Max(1e-3f, p.gearRatio);
            float omegaMotor = wheelOmega * gear;

            float stall = vmax / r;                       // ±stall current bound
            if (p.maxCurrent > 0f) stall = Mathf.Min(stall, p.maxCurrent); // ESC limit
            current = Mathf.Clamp((voltage - p.kt * omegaMotor) / r, -stall, stall);

            float tauEm = p.kt * current;
            float tauVisc = p.viscousDamping * omegaMotor;

            // Coulomb (static/running) friction: Tc = scale·kt·I0 on the motor shaft.
            // Running: opposes rotation. Near standstill: a breakaway branch that is
            // dissipative-only — it reduces |net torque| but never reverses it, so a
            // stalled motor below breakaway produces exactly zero (no 400 Hz chatter).
            // coulombScale = 0 (old JSON) reproduces the legacy frictionless equation.
            float tc = Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);
            float torqueMotor = WithFriction(tauEm, tauVisc, tc, omegaMotor);
            return GearToWheel(in p, torqueMotor, omegaMotor);
        }

        /// <summary>
        /// Shaft torque after friction. Running: Coulomb opposes rotation.
        /// Near standstill: a breakaway branch that is dissipative-only — it
        /// reduces |net torque| but never reverses it, so a stalled motor below
        /// breakaway produces exactly zero (no 400 Hz chatter).
        /// </summary>
        public static float WithFriction(float tauEm, float tauVisc, float tc, float omegaMotor)
        {
            if (Mathf.Abs(omegaMotor) > 0.5f)
                return tauEm - tauVisc - tc * Mathf.Sign(omegaMotor);
            float net = tauEm - tauVisc;
            return Mathf.Sign(net) * Mathf.Max(0f, Mathf.Abs(net) - tc);
        }

        /// <summary>Coulomb friction torque Tc = scale·kt·I0 (motor shaft).</summary>
        public static float CoulombTorque(in MotorParams p) =>
            Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);

        /// <summary>
        /// Open circuit: no current, so only the motor's friction and the
        /// gearbox act — what a real ESC does at neutral with drag brake off
        /// (ACT-03 / BUG-04). The car coasts.
        /// </summary>
        public static float CoastTorque(in MotorParams p, float wheelOmega)
        {
            float omegaMotor = wheelOmega * Mathf.Max(1e-3f, p.gearRatio);
            float torqueMotor = WithFriction(0f, p.viscousDamping * omegaMotor,
                                             CoulombTorque(in p), omegaMotor);
            return GearToWheel(in p, torqueMotor, omegaMotor);
        }

        /// <summary>
        /// Motor-shaft torque → wheel torque through the gearbox. Driving, the
        /// gearbox loses (1 − η) of it. Back-driven (the wheel turns the motor:
        /// braking, regen) the losses ADD to the braking, so the wheel feels
        /// τ_m·G/η_back (ACT-04). <c>etaBack</c> = 0 keeps the legacy ×η both
        /// ways (BUG-05) — the same expression, so existing designs' torques are
        /// unchanged bit for bit.
        /// </summary>
        public static float GearToWheel(in MotorParams p, float torqueMotor, float omegaMotor)
        {
            float gear = Mathf.Max(1e-3f, p.gearRatio);
            if (p.etaBack > 0f && torqueMotor * omegaMotor < 0f)
                return torqueMotor * gear / Mathf.Clamp(p.etaBack, 0.05f, 1f);
            return torqueMotor * gear * Mathf.Clamp01(p.efficiency <= 0f ? 1f : p.efficiency);
        }

        /// <summary>The FOC drive's version: always divides when back-driven,
        /// by <c>etaBack</c> or else by <c>efficiency</c> — an FOC design is new,
        /// so it has no legacy number to keep.</summary>
        public static float FocGearToWheel(in MotorParams p, float torqueMotor, float omegaMotor)
        {
            float gear = Mathf.Max(1e-3f, p.gearRatio);
            float etaD = Mathf.Clamp(p.efficiency <= 0f ? 1f : p.efficiency, 0.05f, 1f);
            float etaB = p.etaBack > 0f ? Mathf.Clamp(p.etaBack, 0.05f, 1f) : etaD;
            return torqueMotor * omegaMotor < 0f
                ? torqueMotor * gear / etaB
                : torqueMotor * gear * etaD;
        }

        /// <summary>
        /// The Iq interval an FOC inverter can actually push at this speed
        /// (ACT-01, voltage ceiling). With Id = 0 the winding needs
        /// Vq = R·Iq + Ke·ω_m and Vd = −ω_e·L·Iq, and the modulator can supply
        /// |V_dq| ≤ V_lim. Solving that circle for Iq gives [lo, hi]: the torque
        /// roll-off at speed, and less of it on a sagging pack. When the
        /// back-EMF alone is outside the circle (no field weakening is modelled)
        /// both ends collapse onto the nearest reachable point.
        /// </summary>
        public static void FocIqWindow(float r, float ke, float l, int polePairs,
                                       float omegaM, float vLim, out float lo, out float hi)
        {
            float xl = polePairs * omegaM * l;          // ω_e·L
            float e = ke * omegaM;                      // back-EMF
            float a = r * r + xl * xl;
            float b = 2f * r * e;
            float c = e * e - vLim * vLim;
            float disc = b * b - 4f * a * c;
            if (disc <= 0f) { lo = hi = -b / (2f * a); return; }
            float s = Mathf.Sqrt(disc);
            lo = (-b - s) / (2f * a);
            hi = (-b + s) / (2f * a);
        }

        /// <summary>
        /// Shorted-winding proportional brake — what a hobby ESC's brake actually
        /// is. At brake duty d the bridge shorts the motor for d of each PWM
        /// period, so the braking current is the back-EMF driven through the
        /// winding resistance: I = d·Kt·|ω_motor|/R (ESC current limit applies),
        /// and the torque always opposes rotation — it can never spin the wheel
        /// backwards, and it fades to nothing at rest (why cars need a friction
        /// brake to hold). Coulomb + viscous losses still act. The current
        /// circulates inside the bridge: it draws nothing from the pack.
        /// </summary>
        public static float BrakeTorque(in MotorParams p, float duty, float wheelOmega,
                                        out float current, float railV = 0f)
        {
            float r = Mathf.Max(1e-3f, p.resistance);
            float gear = Mathf.Max(1e-3f, p.gearRatio);
            float omegaMotor = wheelOmega * gear;
            float vmax = Mathf.Max(0.01f, railV > 0f ? railV : p.maxVoltage);

            float stall = vmax / r;
            if (p.maxCurrent > 0f) stall = Mathf.Min(stall, p.maxCurrent);
            float iBrake = Mathf.Min(Mathf.Clamp01(duty) * p.kt * Mathf.Abs(omegaMotor) / r, stall);
            current = -Mathf.Sign(omegaMotor) * iBrake;   // circulating, signed vs. rotation

            float tc = Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);
            float torqueMotor = -Mathf.Sign(omegaMotor) * (p.kt * iBrake + tc)
                                - p.viscousDamping * omegaMotor;
            return GearToWheel(in p, torqueMotor, omegaMotor);
        }

        // ---- datasheet ↔ constants ----

        /// <summary>
        /// Derive electrical constants from datasheet figures.
        /// Ke=Kt=K. No-load: Vn = I0·R + K·ω0. Stall: τs = K·(Vn/R).
        /// Eliminating K gives R = Vn² / (τs·ω0 + Vn·I0), then K = τs·R/Vn.
        /// </summary>
        public static void ApplyDatasheet(ref MotorParams p, in MotorDatasheet d)
        {
            float vn = Mathf.Max(0.01f, d.nominalVoltage);
            float w0 = Mathf.Max(1e-3f, d.noLoadRpm * TwoPiOver60);   // rad/s, motor shaft
            float ts = Mathf.Max(1e-4f, d.stallTorque);
            float i0 = Mathf.Max(0f, d.noLoadCurrent);

            float r = vn * vn / (ts * w0 + vn * i0);
            float k = ts * r / vn;

            p.resistance = r;
            p.kt = k;
            p.maxVoltage = vn;
            p.noLoadCurrent = i0;
        }

        /// <summary>Inverse of <see cref="ApplyDatasheet"/> for display in the garage.</summary>
        public static MotorDatasheet ToDatasheet(in MotorParams p)
        {
            float r = Mathf.Max(1e-3f, p.resistance);
            float k = Mathf.Max(1e-4f, p.kt);
            float vn = p.maxVoltage;
            // No-load: torque = 0 → K·I0(friction) balances; approximate ω0 from V = K·ω0 + I0·R.
            float w0 = (vn - p.noLoadCurrent * r) / k;   // rad/s
            float ts = k * (vn / r);                     // stall torque
            return new MotorDatasheet
            {
                nominalVoltage = vn,
                stallTorque = ts,
                noLoadRpm = Mathf.Max(0f, w0) / TwoPiOver60,
                noLoadCurrent = p.noLoadCurrent,
            };
        }
    }
}
