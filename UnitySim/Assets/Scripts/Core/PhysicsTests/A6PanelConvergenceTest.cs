using AIHWSim.Core.Flight;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A6 — does the answer depend on how finely the wing is chopped up?</b>
    ///
    /// The panel model is a discretisation: the wing is a continuous surface and
    /// this replaces it with eight strips a side. Every force it produces therefore
    /// carries a resolution error, and the only honest way to know whether eight is
    /// enough is to run it at four and sixteen and see whether the answer moves.
    /// This is the aerodynamic twin of <see cref="P9TimestepTest"/>: the same
    /// question about a different axis of discretisation.
    ///
    /// <b>Roll rate is the right observable to convergence-test.</b> Most quantities
    /// here — trim speed, glide ratio, load factor — depend on the wing's TOTAL
    /// lift, and a total converges almost immediately because the errors at
    /// different stations cancel. Roll damping does not: it is a moment, so each
    /// strip's error is weighted by how far out it sits and the outboard strips
    /// dominate. If any number in this model is going to care about panel count,
    /// it is this one.
    ///
    /// <b>Info, and the reason is about the reference rather than the result.</b>
    /// The non-dimensional roll rate pb/2V is quoted in the literature at 0.16–0.19
    /// for a sport model at full aileron, but the underlying damping derivative
    /// C_lp is published with a 30–40 % spread across sources, and the aileron
    /// authority term carries the ⚠ estimated section data with it. Gating a build
    /// on agreement with a band that wide would be gating on nothing. What this row
    /// DOES answer, and answers as a measurement, is the internal question: is the
    /// panel count converged? A spread of a few percent between 4 and 16 panels says
    /// the shipped 8 is not the thing being measured, and that is what decides
    /// whether a lifting-line solve would be worth building.
    ///
    /// <b>What the manoeuvre actually is.</b> Full aileron from trimmed level
    /// flight, averaged over one second starting half a second after the stick goes
    /// over — long enough for the roll to reach its damping equilibrium (the roll
    /// mode's time constant here is well under 0.2 s) and short enough that the
    /// aircraft has only rolled through about a turn. Pitch and yaw are left
    /// open-loop throughout, so what is measured includes the adverse yaw and the
    /// nose drop a real roll has. That makes the absolute number a one-second
    /// average through a real manoeuvre rather than a textbook steady roll — stated
    /// plainly, because it is the same manoeuvre at every panel count and so the
    /// CONVERGENCE comparison is unaffected by it.
    /// </summary>
    public sealed class A6PanelConvergenceTest : FlightTest
    {
        [Tooltip("Panel-count multipliers applied to every surface. The middle one "
                 + "must be 1.0 — that is the shipped resolution and the row's "
                 + "headline roll rate comes from it.")]
        public float[] panelScales = { 0.5f, 1f, 2f };
        [Tooltip("Trim throttle, from the trim probe.")]
        public float trimThrottle = 0.719f;
        [Tooltip("Seconds of trimmed flight before the stick goes over. SHORT on "
                 + "purpose: every stage is launched into an identical state, and a "
                 + "long settle only gives the phugoid time to carry the three stages "
                 + "to different airspeeds — which is exactly what wrecked the first "
                 + "run, where the three rolls happened at 16.8, 13.0 and 12.3 m/s "
                 + "and the 'panel-count' spread was mostly an airspeed spread.")]
        public float settleSeconds = 3f;
        [Tooltip("Airspeed spread across stages beyond which the three rolls are not "
                 + "the same manoeuvre and no panel-count conclusion can be drawn.")]
        public float tasSpreadLimit = 0.03f;
        [Tooltip("Seconds after full aileron before the average starts. The roll "
                 + "mode settles in well under 0.2 s, so this is many multiples.")]
        public float rollStart = 0.5f;
        public float rollEnd = 1.5f;
        [Tooltip("Convergence spread across panel counts that would say 8 panels is "
                 + "not enough. Reported, not gated.")]
        public float convergenceNote = 0.05f;

        protected override string TestId => "A6";
        protected override string Title => "Panel-count convergence (roll rate)";
        protected override string Expected =>
            "converged in panel count · pb/2V 0.16–0.19 (Info — C_lp literature spreads 30–40 %)";

        protected override float TestAltitude => 800f;

        private int[] _basePanels;
        private int _stage;
        private float _stageT;
        private readonly float[] _rateDegS = new float[8];
        private readonly float[] _pb2v = new float[8];
        private readonly float[] _tasAtRoll = new float[8];
        private readonly int[] _panelCount = new int[8];

        private float _sumRate, _sumTas;
        private int _n;

        protected override void Arm()
        {
            _basePanels = new int[Spec.surfaces.Count];
            for (int i = 0; i < Spec.surfaces.Count; i++)
                _basePanels[i] = Spec.surfaces[i].panelsPerSide;
            BeginStage();
            // No sync: each stage sets its own initial condition, and waiting for a
            // settled one would let the three stages start from different states.
        }

        private void BeginStage()
        {
            float scale = panelScales[_stage];
            for (int i = 0; i < Spec.surfaces.Count; i++)
            {
                LiftingSurface s = Spec.surfaces[i];
                s.panelsPerSide = Mathf.Max(2, Mathf.RoundToInt(_basePanels[i] * scale));
                Spec.surfaces[i] = s;
            }

            // Rebuilds the panel array from the edited geometry. Mass, CG and
            // inertia come from the parts list and are untouched by this, so the
            // only thing that changes between stages is the discretisation — which
            // is the entire point.
            Plane.Configure(Spec);
            _panelCount[_stage] = CountPanels();

            Plane.LaunchAt(new Vector3(0f, TestAltitude, 0f), Quaternion.identity, LaunchSpeed);
            _stageT = 0f;
            _sumRate = 0f;
            _sumTas = 0f;
            _n = 0;
        }

        private int CountPanels()
        {
            int n = 0;
            foreach (LiftingSurface s in Spec.surfaces)
                n += Mathf.Max(2, s.panelsPerSide) * (s.mirrored ? 2 : 1);
            return n;
        }

        protected override void Fly(ScriptedPilot p, float t)
        {
            _stageT += Time.fixedDeltaTime;
            p.Neutral();
            p.throttle = trimThrottle;

            if (_stageT < settleSeconds)
            {
                HoldWingsLevel(p);
                return;
            }

            // Full aileron, and nothing else. Pitch and yaw stay open-loop so the
            // roll carries its own adverse yaw and nose drop — a leveller or a
            // pitch hold here would be measuring the autopilot's contribution to
            // the roll rate, which is exactly the thing this must not do.
            p.roll = 1f;
        }

        protected override void Sample(float dt)
        {
            float since = _stageT - settleSeconds;
            if (since < rollStart || since > rollEnd) return;

            // Body roll rate about +Z. A positive aileron command rolls RIGHT,
            // which in this frame is a NEGATIVE rotation about +Z, so the magnitude
            // is what the manoeuvre is worth — see FlightTest.HoldWingsLevel for
            // why that sign is the way round it is.
            float p = Body.transform.InverseTransformDirection(Body.angularVelocity).z;
            _sumRate += Mathf.Abs(p);
            _sumTas += Air.Tas;
            _n++;
        }

        protected override Verdict? Evaluate()
        {
            if (_stageT < settleSeconds + rollEnd || _n == 0) return null;

            float rate = _sumRate / _n;                    // rad/s
            float tas = Mathf.Max(1f, _sumTas / _n);
            _rateDegS[_stage] = rate * Mathf.Rad2Deg;
            _pb2v[_stage] = rate * Spec.WingSpan / (2f * tas);
            _tasAtRoll[_stage] = tas;

            Debug.Log($"[AERO] A6 {_panelCount[_stage]} panels → "
                      + $"{_rateDegS[_stage]:0.0} °/s, pb/2V {_pb2v[_stage]:0.0000} "
                      + $"at {tas:0.00} m/s");

            _stage++;
            if (_stage < panelScales.Length) { BeginStage(); return null; }

            int nominal = NominalStage();
            float min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < panelScales.Length; i++)
            {
                min = Mathf.Min(min, _rateDegS[i]);
                max = Mathf.Max(max, _rateDegS[i]);
            }
            float mean = 0f;
            for (int i = 0; i < panelScales.Length; i++) mean += _rateDegS[i];
            mean /= panelScales.Length;
            float spread = mean > 1e-4f ? (max - min) / mean : 0f;

            // The one that actually matters for shipping: how far the resolution the
            // aircraft flies at sits from the finest one measured. A three-way spread
            // can be dominated by the coarsest run, which nothing uses.
            int finest = panelScales.Length - 1;
            float vsFinest = _rateDegS[finest] > 1e-4f
                ? (_rateDegS[nominal] - _rateDegS[finest]) / _rateDegS[finest] : 0f;

            // Was it even the same manoeuvre three times? Roll rate depends strongly
            // on airspeed, so unless the three stages rolled at the same speed this
            // row is comparing flight conditions and calling the difference a
            // panel-count effect. The first run did exactly that — 16.8, 13.0 and
            // 12.3 m/s — and reported a confident "52.7 %, NOT CONVERGED" that meant
            // nothing. Reporting INVALID is the honest answer to that state.
            float tasMin = float.MaxValue, tasMax = float.MinValue, tasMean = 0f;
            for (int i = 0; i < panelScales.Length; i++)
            {
                tasMin = Mathf.Min(tasMin, _tasAtRoll[i]);
                tasMax = Mathf.Max(tasMax, _tasAtRoll[i]);
                tasMean += _tasAtRoll[i];
            }
            tasMean /= panelScales.Length;
            float tasSpread = tasMean > 1e-3f ? (tasMax - tasMin) / tasMean : 1f;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < panelScales.Length; i++)
                sb.Append($"{_panelCount[i]} panels {_rateDegS[i]:0.0} °/s"
                          + $" (pb/2V {_pb2v[i]:0.000} at {_tasAtRoll[i]:0.00} m/s)"
                          + (i < panelScales.Length - 1 ? " · " : ""));

            if (tasSpread > tasSpreadLimit)
                return new Verdict
                {
                    kind = Kind.Invalid,
                    value = spread * 100f,
                    units = "%",
                    detail = sb + $" · ⚠ the three stages rolled at airspeeds spread "
                             + $"{tasSpread * 100f:0.0} % apart, so this compares flight "
                             + "conditions rather than panel counts and no convergence "
                             + "conclusion can be drawn from it",
                };

            string detail =
                sb + $" · spread {spread * 100f:0.00} % · shipped resolution is "
                + $"{vsFinest * 100f:+0.00;-0.00} % from the finest"
                + (spread > convergenceNote
                   ? " · ⚠ NOT CONVERGED — the roll rate is partly a property of the "
                     + "panel count, which is what a lifting-line solve would remove"
                   : " · converged: 8 panels a side is resolution the answer no longer "
                     + "depends on")
                + $" · ⚠ INFO against the 0.16–0.19 literature band: C_lp is published "
                + $"with a 30–40 % spread and the aileron term carries ⚠ estimated "
                + $"section data, so agreement there is weak evidence either way — the "
                + $"convergence above is the measurement this row actually makes";

            return Verdict.Info(spread * 100f, "%", detail);
        }

        private int NominalStage()
        {
            for (int i = 0; i < panelScales.Length; i++)
                if (Mathf.Abs(panelScales[i] - 1f) < 1e-4f) return i;
            return 0;
        }

        protected override void DrawExtra()
        {
            base.DrawExtra();
            GUILayout.Label($"stage   {_stage + 1}/{panelScales.Length}   "
                            + $"{_panelCount[Mathf.Min(_stage, panelScales.Length - 1)]} panels   "
                            + $"t {_stageT:0.0} s");
        }
    }
}
