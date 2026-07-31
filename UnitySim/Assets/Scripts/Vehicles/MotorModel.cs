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
        /// </summary>
        public static float WheelTorque(in MotorParams p, float commandedVoltage, float wheelOmega,
                                        out float voltage, out float current)
        {
            float vmax = Mathf.Max(0.01f, p.maxVoltage);
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
            float torqueMotor;
            if (Mathf.Abs(omegaMotor) > 0.5f)
            {
                torqueMotor = tauEm - tauVisc - tc * Mathf.Sign(omegaMotor);
            }
            else
            {
                float net = tauEm - tauVisc;
                torqueMotor = Mathf.Sign(net) * Mathf.Max(0f, Mathf.Abs(net) - tc);
            }
            return torqueMotor * gear * Mathf.Clamp01(p.efficiency <= 0f ? 1f : p.efficiency);
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
                                        out float current)
        {
            float r = Mathf.Max(1e-3f, p.resistance);
            float gear = Mathf.Max(1e-3f, p.gearRatio);
            float omegaMotor = wheelOmega * gear;
            float vmax = Mathf.Max(0.01f, p.maxVoltage);

            float stall = vmax / r;
            if (p.maxCurrent > 0f) stall = Mathf.Min(stall, p.maxCurrent);
            float iBrake = Mathf.Min(Mathf.Clamp01(duty) * p.kt * Mathf.Abs(omegaMotor) / r, stall);
            current = -Mathf.Sign(omegaMotor) * iBrake;   // circulating, signed vs. rotation

            float tc = Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);
            float torqueMotor = -Mathf.Sign(omegaMotor) * (p.kt * iBrake + tc)
                                - p.viscousDamping * omegaMotor;
            return torqueMotor * gear * Mathf.Clamp01(p.efficiency <= 0f ? 1f : p.efficiency);
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
