using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>
    /// Geometry and coefficients for one propeller. Sizes are quoted the way props
    /// are sold — a "10×6" is 10 inches across with a 6 inch geometric pitch.
    /// </summary>
    [System.Serializable]
    public struct PropellerSpec
    {
        public float diameterM;
        public float pitchM;
        /// <summary>Blade polar inertia about the shaft (kg·m²).</summary>
        public float inertiaKgM2;

        /// <summary>⚠ Static thrust coefficient. 0.12 is the APC 10×6E band.</summary>
        public float ct0;
        /// <summary>⚠ Static power coefficient. 0.048 for the same prop.</summary>
        public float cp0;
        /// <summary>Advance ratio at which thrust reaches zero. ⚠ partly derivable:
        /// geometrically the prop screws through the air at its own pitch, so
        /// J₀ ≈ p/D = 0.60; cambered blade sections carry that a little further,
        /// to ≈0.68. The 1.13× correction is real but not sourced.</summary>
        public float zeroThrustJ;
        /// <summary>⚠ Advance ratio at which shaft power reaches zero. Always higher
        /// than the thrust one, because a prop still costs torque after it has
        /// stopped pushing.</summary>
        public float zeroPowerJ;

        /// <summary>+1 for a right-hand prop (clockwise seen from behind, the usual
        /// case). Sets which way the torque reaction rolls the airframe.</summary>
        public int rotationSign;

        /// <summary>APC 10×6E on a direct-drive outrunner — the standard .40-size
        /// trainer combination. Inertia is a thin-rod estimate for a 17 g blade set:
        /// J = m·D²/12 = 0.017 × 0.254²/12.</summary>
        public static PropellerSpec Trainer10x6() => new PropellerSpec
        {
            diameterM = 0.254f,          // 10 in
            pitchM = 0.1524f,            // 6 in
            inertiaKgM2 = 9.14e-5f,
            ct0 = 0.12f,
            cp0 = 0.048f,
            zeroThrustJ = 0.68f,
            zeroPowerJ = 0.90f,
            rotationSign = +1,
        };
    }

    /// <summary>
    /// Propeller thrust and shaft torque, and the shaft integrator that couples
    /// them to the existing <see cref="MotorModel"/>.
    ///
    /// <b>RPM is never commanded.</b> The throttle sets a voltage; the motor makes
    /// torque against its own back-EMF; the prop takes torque away as a function of
    /// how fast it is turning and how fast the air is already moving past it. What
    /// the shaft actually does is the equilibrium of those two, so cruise speed,
    /// climb rate and the way thrust sags as the aircraft accelerates are all
    /// consequences rather than numbers anyone chose. This is the same closed loop
    /// the car has through its wheels — the motor model's own doc block calls the
    /// achievable torque "an emergent function of how fast the wheel is actually
    /// turning", and a propeller is only a differently shaped load.
    ///
    /// <code>
    ///   J = V / (n·D)                        advance ratio, n in rev/s
    ///   T = C_T(J)·ρ·n²·D⁴                   thrust
    ///   Q = C_P(J)·ρ·n²·D⁵ / 2π              shaft torque  (from P = C_P·ρ·n³·D⁵)
    /// </code>
    /// The 2π is the power-to-torque conversion, not a fitted factor.
    ///
    /// Sanity, at the trainer's full-throttle equilibrium: the motor makes
    /// 0.185 N·m at 858 rad/s and the prop absorbs exactly that, giving 8 200 rpm
    /// and 11.4 N of static thrust against a 19.6 N airframe — a thrust-to-weight
    /// of 0.58, which is a docile trainer and not a rocket.
    ///
    /// <b>In:</b> torque reaction (the airframe rolls against the prop) and the
    /// angular momentum needed for gyroscopic precession, which the caller applies.
    /// <b>Declared out:</b> P-factor, which needs a blade-element prop rather than
    /// a C_T(J) curve, and slipstream swirl.
    ///
    /// <b>Graduation path for the ⚠ coefficients:</b> the UIUC Propeller Data Site
    /// publishes wind-tunnel C_T and C_P against J for this exact propeller. Loading
    /// that as a cited table would move ct0/cp0/J₀ from estimates to measurements.
    /// Further out, a propeller blade IS a rotating lifting surface, so
    /// <see cref="PanelAero"/> could produce these curves from authored blade chord
    /// and twist — which is the full derive-from-geometry answer.
    /// </summary>
    public static class PropellerModel
    {
        /// <summary>Below this shaft speed (rev/s) the advance ratio is meaningless;
        /// thrust carries an n² factor so the clamp cannot leak into a force.</summary>
        private const float MinRevsPerSec = 1e-3f;

        /// <summary>Shaft speed in rev/s — the unit every propeller coefficient is
        /// defined in, and a very easy one to confuse with rad/s.</summary>
        public static float RevsPerSecond(float omegaRadPerSec) =>
            omegaRadPerSec / (2f * Mathf.PI);

        /// <summary>Advance ratio. Clamped at the top because a nearly stopped prop
        /// in fast air is outside the linear fit's validity, not because the number
        /// is inconvenient.</summary>
        public static float AdvanceRatio(in PropellerSpec p, float omega, float vAxial)
        {
            float n = Mathf.Max(RevsPerSecond(omega), MinRevsPerSec);
            float d = Mathf.Max(1e-4f, p.diameterM);
            return Mathf.Clamp(vAxial / (n * d), -1f, 3f * Mathf.Max(0.05f, p.zeroThrustJ));
        }

        public static void Coefficients(in PropellerSpec p, float j, out float ct, out float cp)
        {
            ct = p.ct0 * (1f - j / Mathf.Max(0.05f, p.zeroThrustJ));
            cp = p.cp0 * (1f - j / Mathf.Max(0.05f, p.zeroPowerJ));
        }

        /// <summary>Thrust (N), positive forward. Goes negative past the zero-thrust
        /// advance ratio, which is a windmilling prop braking the aircraft — real,
        /// and what makes a dead-stick glide worse than a folded prop.</summary>
        public static float Thrust(in PropellerSpec p, float omega, float vAxial)
        {
            float n = RevsPerSecond(omega);
            Coefficients(p, AdvanceRatio(p, omega, vAxial), out float ct, out _);
            float d2 = p.diameterM * p.diameterM;
            return ct * AeroDynamics.AirDensity * n * n * d2 * d2;
        }

        /// <summary>Shaft torque the prop demands (N·m), positive = opposing rotation.</summary>
        public static float ShaftTorque(in PropellerSpec p, float omega, float vAxial)
        {
            float n = RevsPerSecond(omega);
            Coefficients(p, AdvanceRatio(p, omega, vAxial), out _, out float cp);
            float d2 = p.diameterM * p.diameterM;
            float d5 = d2 * d2 * p.diameterM;
            return cp * AeroDynamics.AirDensity * n * n * d5 / (2f * Mathf.PI);
        }

        /// <summary>
        /// Advance the shaft one step. Returns the new shaft speed (rad/s).
        ///
        /// <b>Semi-implicit on the speed-dependent terms.</b> Both the motor's
        /// back-EMF (dτ/dω = −Kt²/R) and the prop's load (dQ/dω = 2Q/ω) oppose a
        /// change in shaft speed, and treating them implicitly damps the increment
        /// by 1/(1+k) instead of letting an explicit step overshoot. It is one
        /// division, it is unconditionally stable, and it is what makes the shaft
        /// give the same answer at 200 Hz and 800 Hz — which the timestep test
        /// checks rather than assumes.
        ///
        /// The shaft is held at or above zero: an ESC will not drive a prop
        /// backwards, and a prop that has been stopped by the airflow stays stopped.
        /// </summary>
        public static float StepShaft(in PropellerSpec p, in MotorParams m,
                                      float commandedVolts, float omega, float vAxial,
                                      float dt, out float motorTorque, out float loadTorque,
                                      out float current)
        {
            float inertia = Mathf.Max(1e-7f, p.inertiaKgM2 + Mathf.Max(0f, m.rotorInertia));

            // gearRatio is 1 for a direct-drive prop, so the motor model's shaft
            // speed IS the prop's — see DebugPlanes for why efficiency is 1 too.
            motorTorque = MotorModel.WheelTorque(m, commandedVolts, omega,
                                                 out _, out current);
            loadTorque = ShaftTorque(p, omega, vAxial);

            float r = Mathf.Max(1e-3f, m.resistance);
            float backEmfSlope = m.kt * m.kt / r;
            float propSlope = omega > 1f ? 2f * Mathf.Abs(loadTorque) / omega : 0f;
            float k = dt * (backEmfSlope + propSlope) / inertia;

            float next = omega + (dt / inertia) * (motorTorque - loadTorque) / (1f + k);
            return Mathf.Max(0f, next);
        }

        /// <summary>
        /// Angular momentum of the spinning prop, body frame (kg·m²/s), along the
        /// thrust axis. The caller turns this into gyroscopic precession with
        /// τ = −ω_body × H — which is why a hard yaw pitches the nose, and why it
        /// is worth measuring the sign rather than trusting the cross product.
        /// </summary>
        public static Vector3 AngularMomentumBody(in PropellerSpec p, float omega) =>
            Vector3.forward * (p.inertiaKgM2 * omega * Mathf.Sign(p.rotationSign == 0 ? 1 : p.rotationSign));
    }
}
