using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P1 — coastdown, fitted for Cd·A.</b> The strongest test in the suite,
    /// and the only one whose reference is two independently published numbers
    /// rather than something estimated: VW's Cd 0.31 and frontal area 2.47 m²,
    /// giving <b>Cd·A = 0.7657 m²</b>.
    ///
    /// It works because this world has no rolling resistance at all
    /// (<c>SurfaceInfo.Baseline.rollingResist == 0</c>) and P0 has already shown
    /// there is no parasitic drag either. So with the driveline disconnected,
    /// aerodynamic drag is the ONLY horizontal force on the car, and the
    /// deceleration is a clean measurement of it. A real-world coastdown is aero
    /// plus Crr and cannot separate them without two runs; this one is separated
    /// by construction.
    ///
    /// <b>The fit is a straight line, on purpose.</b> With drag alone,
    /// <c>m·dv/dt = −½ρ·Cd·A·v²</c> integrates to
    /// <c>1/v = 1/v₀ + (ρ·Cd·A / 2m)·t</c> — so 1/v against t is a LINE whose
    /// slope gives Cd·A directly. Least-squares over the whole window uses every
    /// sample instead of two endpoints, and a curved 1/v plot would be visible
    /// evidence of a force that should not be there.
    ///
    /// <b>The driveline must be disconnected or this measures the motor.</b> See
    /// <c>PhysicsTest.SetFreewheel</c>: a DC motor at zero volts is a short, and
    /// its back-EMF braking is several times the aerodynamic drag at these
    /// speeds. Left connected, this test reports the winding resistance as Cd·A.
    ///
    /// There is an independent sanity check on the reference itself: at the
    /// published 200 km/h top speed, Cd·A = 0.7657 implies 89.6 kW at the wheels
    /// from a 110 kW crank — an 81 % driveline, which is plausible. That
    /// self-consistency is what makes this the test worth trusting most.
    /// </summary>
    public sealed class P1CoastdownTest : PhysicsTest
    {
        protected override string TestId => "P1";
        protected override string Title => "Coastdown → Cd·A";
        protected override string Expected => "0.766 m² ±3 % (published 0.31 × 2.47)";

        [Header("P1")]
        public float startSpeed = 32f;
        public float endSpeed = 22f;
        [Tooltip("PUBLISHED Cd 0.31 × frontal area 2.47 m².")]
        public float cdaTarget = 0.7657f;
        [Tooltip("Fractional tolerance. 3 % is tighter than the spread between "
                 + "published Cd figures for this car, so it is the model being "
                 + "tested rather than the reference.")]
        public float tolFrac = 0.03f;

        // Least-squares accumulators for 1/v against t.
        private int _n;
        private double _st, _sy, _stt, _sty;
        private float _v0;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("a_long (m/s²)", "veh/a_long");
        }

        protected override void Arm()
        {
            SetFreewheel(true);
            // Overshoot: the window is defined at exactly 32 m/s, so launch above
            // it and let the run begin as the car falls back through.
            LaunchAt(startSpeed, 2f);
        }

        protected override void Drive(ScriptedDriver d, float t) => d.Neutral();

        protected override void Sample(float dt)
        {
            float v = Speed;
            if (v <= endSpeed) return;
            if (_n == 0) _v0 = v;

            double y = 1.0 / v;
            _st += RunTime; _sy += y;
            _stt += (double)RunTime * RunTime; _sty += RunTime * y;
            _n++;
        }

        protected override Verdict? Evaluate()
        {
            if (Speed > endSpeed || _n < 100) return null;

            double den = _n * _stt - _st * _st;
            if (System.Math.Abs(den) < 1e-12)
                return new Verdict { kind = Kind.Invalid, detail = "degenerate fit" };

            // slope of 1/v vs t  =  ρ·Cd·A / 2m
            double slope = (_n * _sty - _st * _sy) / den;
            // EFFECTIVE mass, not chassis mass — and this is not a refinement,
            // it is 3.7 % and it is the difference between a pass and a fail.
            //
            // Slowing the car also slows four spinning wheels, and the torque to
            // do that comes from the road. A wheel of inertia J resists like an
            // extra J/r² of translating mass, so the drag force sees
            // 1500 + 6.8/0.349² = 1555.8 kg, not 1500. Dividing by the chassis
            // mass alone reports Cd·A low by exactly that ratio (measured: 0.7383
            // against 0.7657 — 3.58 % low, where the ratio predicts 3.59 %).
            //
            // Real coastdown analysis calls this the equivalent mass and includes
            // it for the same reason. Reading the inertia back from the car keeps
            // it right if a wheel is ever re-specified — and it correctly picks
            // up the reflected rotor inertia on the driven axle.
            float rr = DebugVehicles.LoadedRadius * DebugVehicles.LoadedRadius;
            float mEff = Body.mass;
            for (int i = 0; i < Car.WheelCount; i++)
                mEff += Car.WheelSpinInertia(i) / rr;

            // Same air density the force used, so the fit inverts the model's own
            // equation rather than a textbook one that might differ in the third
            // decimal.
            float cda = (float)(2.0 * mEff * slope / AeroDynamics.AirDensity);

            float err = (cda - cdaTarget) / cdaTarget;
            string detail = $"{_v0:0.00} → {Speed:0.00} m/s in {RunTime:0.00} s · "
                            + $"{_n} samples · err {err * 100f:+0.00;-0.00} % · "
                            + $"m_eff {mEff:0.0} kg (chassis {Body.mass:0}) · "
                            + "driveline freewheeling";

            return Mathf.Abs(err) <= tolFrac
                ? Verdict.Pass(cda, "m²", detail)
                : Verdict.Fail(cda, "m²", detail);
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"window  {startSpeed:0.0} → {endSpeed:0.0} m/s");
            GUILayout.Label($"samples {_n}");
        }
    }
}
