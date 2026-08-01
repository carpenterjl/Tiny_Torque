using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P3 — heave frequency and damping.</b> The only test that exercises the
    /// suspension on its own. The body is lifted, released, and the resulting
    /// decay is measured against what the authored springs and dampers predict.
    ///
    /// <b>The reference is hand-derived, not read back from the design.</b>
    /// Sprung mass is 1500 − 4×25 = 1400 kg; the four springs total
    /// 2×30 000 + 2×23 000 = 106 000 N/m; so the pure heave mode is
    /// <c>f = √(106000/1400) / 2π = <b>1.385 Hz</b></c>, inside the 1.2–1.4 Hz
    /// passenger-car band. Damping ratios are authored 0.30 front / 0.32 rear,
    /// so the mode should land near 0.31. Comparing the simulated response to the
    /// closed-form second-order prediction is what makes this a test of the
    /// integrator rather than a restatement of the inputs.
    ///
    /// <b>The displacement is 10 mm, and it has to be small.</b> The car rests at
    /// suspension compression 0.9578 — this model has no load-dependent static
    /// sag, so the rest point comes from <c>targetPosition</c> alone — which
    /// leaves only <c>(1 − 0.9578) × 0.30 m = 12.7 mm</c> of bump travel. The
    /// 50 mm release an earlier plan called for would bottom the suspension on
    /// the first downswing and the "frequency" measured would be the sound of a
    /// bump stop that does not exist in this model. That 12.7 mm is a direct
    /// consequence of the declared-fiction 0.30 m travel, and it is the sharpest
    /// illustration of what that fiction costs.
    /// </summary>
    public sealed class P3HeaveTest : CarPhysicsTest
    {
        protected override string TestId => "P3";
        protected override string Title => "Heave frequency & damping";
        protected override string Expected => "1.385 Hz (spring-derived) · ζ ≈ 0.31";

        [Header("P3")]
        [Tooltip("Lift in mm. Keep it under the ~12.7 mm of bump travel the "
                 + "static compression leaves — see the class comment.")]
        public float heaveMm = 10f;
        public float freqTarget = 1.385f;
        public float freqTol = 0.15f;
        public float zetaTarget = 0.31f;
        public float zetaTol = 0.10f;
        public float windowSec = 6f;

        private float _rest;
        private float _prev, _prevPrev;
        private float _tPrev;
        private int _peaks;
        private float _t1, _a1, _t2, _a2;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("ride height (m)", "veh/ride_height");
            g.AddPane("a_vert (m/s²)", "veh/a_vert");
            g.AddPane("compression", "veh/susp_0", "veh/susp_2");
        }

        protected override void Arm()
        {
            _rest = Ch("veh/ride_height");
            var p = Car.transform.position;
            Car.transform.position = new Vector3(p.x, p.y + heaveMm * 0.001f, p.z);
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
        }

        protected override void Drive(ScriptedDriver d, float t) => d.Neutral();

        protected override void Sample(float dt)
        {
            float x = Ch("veh/ride_height") - _rest;

            // Local maximum: the middle of the last three samples is the peak.
            // Peaks rather than zero crossings, because the amplitude ratio of
            // successive peaks is what gives the damping ratio, and the same
            // detector then gives the period for free.
            if (_prevPrev < _prev && _prev >= x && _prev > 0.0002f)
            {
                _peaks++;
                if (_peaks == 1) { _t1 = _tPrev; _a1 = _prev; }
                else if (_peaks == 2) { _t2 = _tPrev; _a2 = _prev; }
            }
            _prevPrev = _prev;
            _prev = x;
            _tPrev = RunTime;
        }

        protected override Verdict? Evaluate()
        {
            if (_peaks < 2 && RunTime < windowSec) return null;
            if (_peaks < 2)
                return new Verdict
                {
                    kind = Kind.Invalid,
                    detail = $"only {_peaks} peak(s) in {windowSec:0} s — "
                             + "over-damped, bottoming, or never released",
                };

            float period = _t2 - _t1;
            if (period <= 1e-4f)
                return new Verdict { kind = Kind.Invalid, detail = "degenerate period" };

            float fd = 1f / period;                              // damped frequency
            // Logarithmic decrement over one cycle.
            float delta = Mathf.Log(Mathf.Max(_a1, 1e-6f) / Mathf.Max(_a2, 1e-6f));
            float zeta = delta / Mathf.Sqrt(4f * Mathf.PI * Mathf.PI + delta * delta);
            // Undamped natural frequency, which is what the spring/mass formula
            // predicts — the measured one is damped and always lower.
            float fn = fd / Mathf.Sqrt(Mathf.Max(1e-6f, 1f - zeta * zeta));

            bool ok = Mathf.Abs(fn - freqTarget) <= freqTol
                      && Mathf.Abs(zeta - zetaTarget) <= zetaTol;

            string detail = $"ζ {zeta:0.000} (expect {zetaTarget:0.00}±{zetaTol:0.00}) · "
                            + $"damped {fd:0.000} Hz · peaks {_a1 * 1000f:0.0}→{_a2 * 1000f:0.0} mm";

            return ok ? Verdict.Pass(fn, "Hz", detail) : Verdict.Fail(fn, "Hz", detail);
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"lift    {heaveMm:0.0} mm  (bump travel ≈ 12.7 mm)");
            GUILayout.Label($"peaks   {_peaks}");
            GUILayout.Label($"dev     {(Ch("veh/ride_height") - _rest) * 1000f:0.0} mm");
        }
    }
}
