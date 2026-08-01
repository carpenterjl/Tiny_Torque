using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P0 — free-roll residual.</b> The car is set rolling with aerodynamic
    /// drag switched off and nothing touching the controls. Whatever
    /// deceleration remains is <i>parasitic</i>: it is force the model applies
    /// that nothing in the model asked for.
    ///
    /// <b>This test gates the suite.</b> Every other measurement is a difference
    /// between a commanded force and an observed one, so an unexplained drag
    /// term contaminates all of them — most severely P1, where the entire signal
    /// is a deceleration of a few hundredths of a g. Run this first; if it
    /// fails, nothing downstream is worth reading.
    ///
    /// Candidates it is looking for, all real hazards at this scale: the brush
    /// tyre model's <c>brushEps</c> floor (0.01 was justified at a 4.4 N RC
    /// corner load and would be ~160 N of drag at the Tiguan's 3679 N),
    /// <c>linearDamping</c> (a 1/s term, so it scales with mass — 0.02 would be
    /// 900 N at 30 m/s on 1500 kg), and the sticky-tyre hold failing to release.
    /// The Tiguan authors all three away; this is the check that it worked.
    ///
    /// <b>Turning drag off uses the sentinel, not an edit.</b>
    /// <c>ApplyAerodynamics</c> reads <c>dragCdOverride</c> only when it is
    /// &gt; 0, and falls back to <c>BodyCd(bodyShape)</c> — which for
    /// <c>BodyShape.Tiguan</c> is the 0.80 default — when it is zero. So the way
    /// to remove drag is a vanishingly small positive Cd, not a zero one. A zero
    /// here would have QUADRUPLED the drag instead of removing it, and the test
    /// would have failed while looking like it was measuring something else.
    /// </summary>
    public sealed class P0FreeRollTest : CarPhysicsTest
    {
        protected override string TestId => "P0";
        protected override string Title => "Free-roll residual (drag off)";
        protected override string Expected => "< 0.005 m/s²";

        [Header("P0")]
        [Tooltip("Roll speed (m/s). High enough that the tyres are rolling true, "
                 + "low enough that any leftover aero is far below the threshold.")]
        public float rollSpeed = 10f;
        [Tooltip("Measurement window (s). Longer is better: the signal is the "
                 + "velocity difference across it, so the noise floor falls as 1/T.")]
        public float windowSec = 8f;
        public float threshold = 0.005f;

        private float _v0 = -1f;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("a_long (m/s²)", "veh/a_long");
            g.AddPane("slip", "veh/slip_0", "veh/slip_1", "veh/slip_2", "veh/slip_3");
        }

        protected override void Arm()
        {
            // See the class comment: the sentinel is "> 0", so near-zero, not zero.
            Car.dragCdOverride = 1e-9f;
            // "Free roll" means the driveline is out of it. Without this the test
            // measures the motor shorting itself through its own windings — see
            // SetFreewheel, which is where the surprise is written down.
            SetFreewheel(true);
            LaunchAt(rollSpeed);
        }

        protected override void Drive(ScriptedDriver d, float t) => d.Neutral();

        protected override void Sample(float dt)
        {
            if (_v0 < 0f) _v0 = Speed;
        }

        protected override Verdict? Evaluate()
        {
            if (RunTime < windowSec) return null;

            // Mean deceleration across the window. Differencing the endpoints
            // rather than averaging the per-step a_long channel: the channel is a
            // one-step difference and its noise is the integrator's, while this
            // is the quantity the test is actually about.
            float a = (_v0 - Speed) / RunTime;

            string detail = $"{_v0:0.000} → {Speed:0.000} m/s over {RunTime:0.0} s "
                            + "· aero disabled via dragCdOverride";

            return Mathf.Abs(a) <= threshold
                ? Verdict.Pass(a, "m/s²", detail)
                : Verdict.Fail(a, "m/s²", detail + " — parasitic force present");
        }

        protected override void DrawExtra()
        {
            if (_v0 > 0f && RunTime > 0.1f)
                GUILayout.Label($"decel   {(_v0 - Speed) / RunTime:0.00000} m/s²");
            GUILayout.Label($"slip    {Ch("veh/slip_0"):0.0000}  {Ch("veh/slip_2"):0.0000}");
        }
    }
}
