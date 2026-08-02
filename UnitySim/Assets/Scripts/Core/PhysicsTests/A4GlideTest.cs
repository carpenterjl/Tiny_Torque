using AIHWSim.Core.Flight;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A4 — glide ratio, power off.</b>
    ///
    /// Shut the motor down and let the aeroplane settle into a steady glide. The
    /// ratio of forward speed to sink rate IS the lift-to-drag ratio, by a
    /// force triangle and nothing else: in an unpowered steady glide the flight
    /// path angle satisfies <c>tan γ = D/L</c>.
    ///
    /// <b>Two comparisons, gated differently, and the difference is the point.</b>
    /// <list type="bullet">
    /// <item>Against the model's OWN C_L/C_D at the glide's angle of attack, this
    ///       must agree — that is self-consistency, and a disagreement means the
    ///       forces and the reported coefficients are not the same thing. It
    ///       <b>gates</b>.</item>
    /// <item>Against a real fixed-gear trainer's 10–13, it is reported as
    ///       <b>Info</b>. That band is a general expectation for the class, not a
    ///       measurement of this airframe, and the project's rule is that a known
    ///       modelling limit must never turn the build red — a gate everyone learns
    ///       to ignore protects nothing.</item>
    /// </list>
    ///
    /// The airframe's own closed form predicts L/D_max = ½√(π·AR·e/C_D0) = 10.89.
    /// That is the best achievable, at the one speed where induced and parasitic
    /// drag are equal; a glide at any other speed is worse, so this test measures a
    /// point on the polar rather than its peak.
    ///
    /// <b>The propeller is not free-wheeling in a helpful direction.</b> Past its
    /// zero-thrust advance ratio a windmilling prop makes NEGATIVE thrust, and at
    /// glide speeds that is real drag the airframe has to carry — which is exactly
    /// why a dead-stick glide is worse than a folded prop, and why the number here
    /// sits below the clean-airframe polar.
    /// </summary>
    public sealed class A4GlideTest : FlightTest
    {
        [Tooltip("Seconds to settle into the glide before measuring.")]
        public float settleGlideSeconds = 25f;
        [Tooltip("Seconds to average over — several phugoid cycles.")]
        public float windowSeconds = 24f;
        [Tooltip("Allowed disagreement with the model's own Cl/Cd, as a fraction.")]
        public float selfConsistencyTol = 0.06f;

        protected override string TestId => "A4";
        protected override string Title => "Glide ratio (power off)";
        protected override string Expected =>
            "self-consistent with the model's own C_L/C_D · real trainer 10–13 (Info)";

        protected override float TestAltitude => 1200f;

        private float _t;
        private float _sumFwd, _sumSink, _sumCl, _sumCd, _sumTas, _sumThrust;
        private int _n;

        protected override void Idle(ScriptedPilot p)
        {
            p.Neutral();
            p.throttle = 0.719f;     // roughly trim, so the launch is gentle
            HoldWingsLevel(p);
        }

        protected override void Arm() => LaunchAtTrim(LaunchSpeed);

        protected override void Fly(ScriptedPilot p, float t)
        {
            _t = t;
            p.Neutral();
            p.throttle = 0f;         // dead stick
            HoldWingsLevel(p);       // wings level only — pitch stays open-loop, so
                                     // the glide is the aircraft's own trim
        }

        protected override void Sample(float dt)
        {
            if (_t < settleGlideSeconds) return;

            Vector3 v = Body.linearVelocity;
            _sumFwd += new Vector2(v.x, v.z).magnitude;
            _sumSink += -v.y;
            _sumTas += Air.Tas;
            _sumThrust += Plane.Thrust;

            float denom = Air.Q * Spec.WingArea;
            if (denom > 1e-4f)
            {
                _sumCl += Plane.LastAero.lift / denom;
                _sumCd += Plane.LastAero.drag / denom;
            }
            _n++;
        }

        protected override Verdict? Evaluate()
        {
            if (_t < settleGlideSeconds + windowSeconds || _n == 0) return null;

            float fwd = _sumFwd / _n;
            float sink = _sumSink / _n;
            if (sink < 0.05f) return null;   // still climbing; not a glide yet

            float ld = fwd / sink;
            float cl = _sumCl / _n;
            float cd = _sumCd / _n;
            float clOverCd = Mathf.Abs(cd) > 1e-5f ? cl / cd : 0f;
            float predicted = Spec.PredictedLiftToDragMax;

            // PanelAero's C_D is the LIFTING SURFACES only. The fuselage, gear and
            // interference drag is applied separately by PlaneVehicle, and the
            // windmilling propeller adds its own. All three are on the aeroplane
            // and all three must be in the comparison, or this test measures the
            // aircraft against a drag figure that describes only part of it —
            // which reads as a 40 % modelling error when nothing is wrong.
            float meanTas = _sumTas / _n;
            float qS = 0.5f * AeroDynamics.AirDensity * meanTas * meanTas * Spec.WingArea;
            float cdParasitic = Spec.parasiticCdA / Spec.WingArea;
            float cdProp = qS > 1e-4f ? -(_sumThrust / _n) / qS : 0f;   // +ve when braking
            float cdTotal = cd + cdParasitic + cdProp;
            float clOverCdTotal = Mathf.Abs(cdTotal) > 1e-5f ? cl / cdTotal : 0f;

            float err = clOverCdTotal > 1e-3f ? (ld - clOverCdTotal) / clOverCdTotal : 1f;

            string detail = $"{fwd:0.00} m/s forward, {sink:0.000} m/s sink at "
                            + $"{meanTas:0.0} m/s · C_L {cl:0.000} · "
                            + $"C_D surfaces {cd:0.0000} + parasitic {cdParasitic:0.0000} "
                            + $"+ prop {cdProp:0.0000} = {cdTotal:0.0000} "
                            + $"→ C_L/C_D {clOverCdTotal:0.00} (surfaces alone would say "
                            + $"{clOverCd:0.00}) · self-consistency err {err * 100f:+0.0;-0.0} % · "
                            + $"airframe polar peak {predicted:0.00} at best-glide speed · "
                            + $"⚠ the real-trainer 10–13 band is a class expectation, "
                            + $"not a measurement of this aeroplane";

            return Mathf.Abs(err) <= selfConsistencyTol
                ? Verdict.Pass(ld, "L/D", detail)
                : Verdict.Fail(ld, "L/D", detail + $" (tol ±{selfConsistencyTol * 100f:0}%)");
        }
    }
}
