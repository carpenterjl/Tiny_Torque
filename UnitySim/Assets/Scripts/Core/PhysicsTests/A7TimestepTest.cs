using AIHWSim.Core.Flight;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A7 — timestep sensitivity. P9's twin, and it protects every other row in
    /// this suite the same way.</b>
    ///
    /// Each of the other flight tests reports one number at one integration rate.
    /// If any of them depends on the step — if 400 Hz is not converged — then all of
    /// them are quietly reporting a property of the integrator rather than a
    /// property of the aeroplane, and <b>nothing else here would reveal it</b>. So
    /// the same glide is flown at 200, 400 and 800 Hz and the spread is the answer.
    ///
    /// <b>Why a glide rather than a manoeuvre.</b> A glide is a small force
    /// integrated over a long time, which is the shape of problem an integrator gets
    /// wrong slowly. A short, sharp manoeuvre would stress the fast modes but would
    /// finish before any energy drift accumulated. This is the same reasoning P9
    /// uses to repeat the coastdown rather than the braking test.
    ///
    /// <b>Every stage starts from an identical state, not from a settled one.</b>
    /// That is the difference between this and A4. A "settle until steady" entry
    /// would let the three rates begin their measurement windows at different points
    /// in the phugoid, and the resulting difference would look exactly like a
    /// timestep effect. Placing the aircraft at the same position, attitude and
    /// airspeed and measuring over the same ELAPSED window means the oscillation is
    /// at the same phase in all three — so any difference that remains is the step
    /// size and can be nothing else.
    ///
    /// <b>Every control is held at exactly zero, and that is load-bearing.</b>
    /// <c>SimulationRunner</c> fixes its control-tick decimation in Start, so raising
    /// the physics rate also raises the control rate — a second variable moving in
    /// step with the first, which would make the result uninterpretable. With the
    /// commands constant at zero there is nothing for a control tick to deliver, so
    /// the confound is removed by construction rather than argued away. It is
    /// possible here only because the throttle is shut: a stopped propeller applies
    /// no torque reaction, so there is no roll to trim out and no leveller is
    /// needed. The bank angle is checked at the end anyway — a drift would be
    /// reported as INVALID rather than counted as timestep sensitivity.
    ///
    /// The glide ratio is taken from the POSITIONS at the two ends of the window,
    /// not from averaged instantaneous speeds. That makes it a genuine integral of
    /// the trajectory, which is the quantity an integrator error shows up in.
    /// </summary>
    public sealed class A7TimestepTest : FlightTest
    {
        [Header("A7")]
        public int[] rates = { 200, 400, 800 };
        [Tooltip("Seconds after the launch before the window opens — long enough "
                 + "for the launch transient to be past.")]
        public float windowStart = 10f;
        [Tooltip("Seconds after the launch when the window closes.")]
        public float windowEnd = 40f;
        [Tooltip("Allowed spread in glide ratio across the three rates. 0.5 %, the "
                 + "same limit P9 holds the car to.")]
        public float spreadLimit = 0.005f;
        [Tooltip("Bank angle (deg) beyond which the glide is no longer the "
                 + "symmetric one this test assumes.")]
        public float bankLimit = 3f;

        protected override string TestId => "A7";
        protected override string Title => "Timestep sensitivity (glide)";
        protected override string Expected => "< 0.5 % spread across 200 / 400 / 800 Hz";

        protected override float TestAltitude => 1200f;

        private int _stage;
        private float _stageT;
        private readonly float[] _ld = new float[8];
        private readonly float[] _sink = new float[8];
        private float _worstBank;

        private bool _open;
        private Vector3 _openPos;

        protected override void Idle(ScriptedPilot p) => p.Neutral();

        protected override void Arm()
        {
            BeginStage();
            // No sync, deliberately — see the class note. A settled entry would put
            // the three rates at different phugoid phases.
        }

        private void BeginStage()
        {
            // Global, and deliberately so: this is the quantity under test. Both
            // must be written — SimulationRunner recomputes Time.fixedDeltaTime only
            // in Awake and Start, so setting the field alone would change what the
            // runner reports without changing what Unity actually steps.
            Time.fixedDeltaTime = 1f / rates[_stage];
            Runner.physicsRateHz = rates[_stage];

            Plane.propulsionEnabled = true;      // the prop is stopped by the throttle,
                                                 // not switched off — a windmilling
                                                 // prop is part of what a dead-stick
                                                 // glide has to carry
            Plane.LaunchAt(new Vector3(0f, TestAltitude, 0f), Quaternion.identity, LaunchSpeed);
            _stageT = 0f;
            _open = false;
        }

        protected override void Fly(ScriptedPilot p, float t)
        {
            _stageT += Time.fixedDeltaTime;
            // Every stick at zero, every stage, every rate. Nothing here reads the
            // aircraft's state, so no control loop can differ between rates.
            p.Neutral();
        }

        protected override void Sample(float dt)
        {
            _worstBank = Mathf.Max(_worstBank, Mathf.Abs(Wrap180(Plane.transform.eulerAngles.z)));

            if (!_open && _stageT >= windowStart)
            {
                _open = true;
                _openPos = Plane.transform.position;
            }
        }

        protected override Verdict? Evaluate()
        {
            if (_stageT < windowEnd) return null;

            Vector3 now = Plane.transform.position;
            Vector3 d = now - _openPos;
            float drop = -d.y;
            if (drop < 0.5f)
                return new Verdict
                {
                    kind = Kind.Invalid,
                    detail = $"stage {_stage + 1} lost only {drop:0.00} m in "
                             + $"{windowEnd - windowStart:0} s — not a glide",
                };

            _ld[_stage] = new Vector2(d.x, d.z).magnitude / drop;
            _sink[_stage] = drop / (windowEnd - windowStart);
            Debug.Log($"[AERO] A7 {rates[_stage]} Hz → L/D {_ld[_stage]:0.00000} "
                      + $"({drop:0.00} m lost, sink {_sink[_stage]:0.000} m/s)");

            _stage++;
            if (_stage < rates.Length) { BeginStage(); return null; }

            float min = float.MaxValue, max = float.MinValue, mean = 0f;
            for (int i = 0; i < rates.Length; i++)
            {
                min = Mathf.Min(min, _ld[i]);
                max = Mathf.Max(max, _ld[i]);
                mean += _ld[i];
            }
            mean /= rates.Length;
            float spread = mean > 1e-6f ? (max - min) / mean : 0f;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < rates.Length; i++)
                sb.Append($"{rates[i]} Hz {_ld[i]:0.0000}"
                          + (i < rates.Length - 1 ? " · " : ""));

            string detail = sb + $" (L/D over an identical {windowEnd - windowStart:0} s "
                            + $"window from an identical launch) · worst bank "
                            + $"{_worstBank:0.00}° · every stick held at zero, so the "
                            + $"control rate cannot enter";

            if (_worstBank > bankLimit)
                return new Verdict
                {
                    kind = Kind.Invalid,
                    value = spread * 100f,
                    units = "%",
                    detail = detail + $" · ⚠ the glide did not stay symmetric "
                             + $"(> {bankLimit:0}° of bank), so the three runs are not "
                             + "the same manoeuvre and the spread is not a timestep result",
                };

            return spread <= spreadLimit
                ? Verdict.Pass(spread * 100f, "%", detail)
                : Verdict.Fail(spread * 100f, "%",
                    detail + " — the glide depends on the step, so every other row in "
                    + "this suite is reporting the integrator as well as the aeroplane");
        }

        protected override void DrawExtra()
        {
            base.DrawExtra();
            GUILayout.Label($"stage   {_stage + 1}/{rates.Length} at "
                            + $"{rates[Mathf.Min(_stage, rates.Length - 1)]} Hz   "
                            + $"t {_stageT:0.0} s");
        }
    }
}
