using AIHWSim.Core.Flight;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A1 — static thrust: the propeller and motor, with no aerodynamics at all.</b>
    ///
    /// The aeroplane is held still and given full throttle. The shaft accelerates
    /// until the motor's torque curve crosses the propeller's load curve, and that
    /// crossing is solvable by hand:
    /// <code>
    ///   τ_motor(ω) = 0.699 − 6.09e-4·ω        (Kt·(V − Kt·ω)/R, less friction)
    ///   Q_prop(ω)  = 2.51e-7·ω²               (C_P·ρ·n²·D⁵/2π)
    ///   ⇒ ω = 850 rad/s = 8 120 rpm, T = 11.2 N
    /// </code>
    ///
    /// <b>No aerodynamics enters this at all</b>, which is exactly why it is worth
    /// its own row: if it fails, the fault is in the motor model, the propeller
    /// coefficients or the shaft integrator, and cannot be anywhere else. Every
    /// later test rests on thrust being right, so this is the one that says whether
    /// they are standing on anything.
    ///
    /// Thrust-to-weight comes out at 0.57 — a docile trainer that will climb but
    /// not hang on its prop, which is what the airframe was specified to be.
    /// </summary>
    public sealed class A1StaticThrustTest : FlightTest
    {
        [Tooltip("Seconds to let the shaft reach equilibrium. The electrical time "
                 + "constant is ~80 ms, so this is many multiples of it.")]
        public float spinUpSeconds = 4f;
        public float toleranceN = 0.30f;

        protected override string TestId => "A1";
        protected override string Title => "Static thrust";
        protected override string Expected => "11.21 N at 8 120 rpm (motor/prop equilibrium)";

        protected override FlightTestEnvironment.EnvSpec Environment =>
            FlightTestEnvironment.EnvSpec.Bare();
        protected override float TestAltitude => 500f;

        private float _t;

        protected override void Idle(ScriptedPilot p) => p.Neutral();

        protected override void Arm()
        {
            // Held still, so the propeller sees zero inflow and the measurement is
            // the static case by construction rather than by waiting for the
            // aircraft to stop accelerating.
            //
            // Frozen by CONSTRAINTS rather than made kinematic: a kinematic body
            // ignores AddForce, and the propeller's thrust and torque reaction
            // would be silently discarded — plus Unity warns once per call. Frozen
            // constraints leave it a real dynamic body that simply cannot move.
            Body.constraints = RigidbodyConstraints.FreezeAll;
            Plane.aeroEnabled = false;
            _t = 0f;
        }

        protected override void Fly(ScriptedPilot p, float t)
        {
            p.Neutral();
            p.throttle = 1f;
        }

        protected override void Sample(float dt) => _t += dt;

        protected override Verdict? Evaluate()
        {
            if (_t < spinUpSeconds) return null;

            float thrust = Plane.Thrust;
            float rpm = Plane.PropOmega * 60f / (2f * Mathf.PI);
            float weight = Spec.TotalMass * Mathf.Abs(Physics.gravity.y);
            float j = PropellerModel.AdvanceRatio(Spec.propeller, Plane.PropOmega, 0f);

            string detail = $"{rpm:0} rpm · ω {Plane.PropOmega:0.0} rad/s · J {j:0.000} · "
                            + $"T/W {thrust / weight:0.000} · "
                            + $"the crossing of the motor and propeller torque curves, "
                            + $"with no aerodynamics involved";

            const float predicted = 11.21f;
            float err = thrust - predicted;
            detail += $" · err {err:+0.000;-0.000} N (tol ±{toleranceN})";
            return Mathf.Abs(err) <= toleranceN
                ? Verdict.Pass(thrust, "N", detail)
                : Verdict.Fail(thrust, "N", detail);
        }
    }
}
