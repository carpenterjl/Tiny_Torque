using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P9 — timestep sensitivity.</b> Runs the coastdown three times, at 200,
    /// 400 and 800 Hz, and reports the spread in the Cd·A it recovers.
    ///
    /// <b>Cheap, and it protects every other number in the suite.</b> Each of the
    /// other tests reports one figure at one rate. If any of them depends on the
    /// integrator step — if the answer at 400 Hz is not converged — then all of
    /// them are quietly reporting a property of the timestep rather than a
    /// property of the car, and nothing else in the suite would reveal it. A
    /// spread under 0.5 % says the results are about the physics.
    ///
    /// The coastdown is the right sub-test to repeat: it is the longest run, the
    /// one whose signal is a small force integrated over many seconds, and
    /// therefore the one most exposed to step size. It is also the only test with
    /// an exact closed-form answer to compare against at each rate.
    ///
    /// <b>400 Hz is the reference, not 500.</b> <c>TrackBootstrap</c> runs at 400
    /// and the Opus mission is gated there, so measuring the car anywhere else
    /// would characterise an integrator the project does not ship.
    ///
    /// One caveat worth stating: <c>Time.fixedDeltaTime</c> is global, so this
    /// test rewrites it between sub-runs. It is the only test that does, and it
    /// is why nothing else may run in the same scene.
    /// </summary>
    public sealed class P9TimestepTest : CarPhysicsTest
    {
        protected override string TestId => "P9";
        protected override string Title => "Timestep sensitivity";
        protected override string Expected => "< 0.5 % spread across 200 / 400 / 800 Hz";

        [Header("P9")]
        public int[] rates = { 200, 400, 800 };
        public float startSpeed = 32f;
        [Tooltip("A shorter window than P1's, because this runs three of them. "
                 + "The spread is what matters here, not the absolute value, and "
                 + "the spread converges long before the window does.")]
        public float endSpeed = 28f;
        public float spreadLimit = 0.005f;

        private int _stage;
        private readonly float[] _cda = new float[3];
        private int _n;
        private double _st, _sy, _stt, _sty;
        private float _stageT;
        private bool _measuring;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("a_long (m/s²)", "veh/a_long");
        }

        protected override void Arm()
        {
            SetFreewheel(true);
            BeginStage();
        }

        private void BeginStage()
        {
            // Global, and deliberately so — this is the quantity under test.
            Time.fixedDeltaTime = 1f / rates[_stage];
            Runner.physicsRateHz = rates[_stage];

            _n = 0; _st = _sy = _stt = _sty = 0.0;
            _stageT = 0f;
            _measuring = false;

            var p = Car.transform.position;
            Car.transform.position = new Vector3(0f, DebugVehicles.TiguanChassisRestY, p.z);
            Car.transform.rotation = Quaternion.identity;
            Physics.SyncTransforms();
            Body.linearVelocity = Car.transform.forward * (startSpeed + 2f);
            Body.angularVelocity = Vector3.zero;
        }

        protected override void Drive(ScriptedDriver d, float t) => d.Neutral();

        protected override void Sample(float dt)
        {
            _stageT += dt;
            float v = Speed;

            if (!_measuring)
            {
                // Wait for the wheels to catch up and the car to fall back
                // through the window's start speed — the same discipline the
                // standalone coastdown uses.
                if (v <= startSpeed && WheelsRolling()) _measuring = true;
                return;
            }
            if (v <= endSpeed) return;

            double y = 1.0 / v;
            _st += _stageT; _sy += y;
            _stt += (double)_stageT * _stageT; _sty += _stageT * y;
            _n++;
        }

        private bool WheelsRolling()
        {
            for (int i = 0; i < Car.WheelCount; i++)
                if (Mathf.Abs(Car.WheelSlipRatio(i)) > 0.01f) return false;
            return true;
        }

        protected override Verdict? Evaluate()
        {
            if (!_measuring || Speed > endSpeed || _n < 50) return null;

            double den = _n * _stt - _st * _st;
            if (System.Math.Abs(den) < 1e-12)
                return new Verdict { kind = Kind.Invalid, detail = "degenerate fit" };
            double slope = (_n * _sty - _st * _sy) / den;

            float rr = DebugVehicles.LoadedRadius * DebugVehicles.LoadedRadius;
            float mEff = Body.mass;
            for (int i = 0; i < Car.WheelCount; i++)
                mEff += Car.WheelSpinInertia(i) / rr;

            _cda[_stage] = (float)(2.0 * mEff * slope / AeroDynamics.AirDensity);
            Debug.Log($"[PHYS] P9 {rates[_stage]} Hz → Cd·A {_cda[_stage]:0.00000} m²");

            _stage++;
            if (_stage < rates.Length) { BeginStage(); return null; }

            float min = Mathf.Min(_cda[0], Mathf.Min(_cda[1], _cda[2]));
            float max = Mathf.Max(_cda[0], Mathf.Max(_cda[1], _cda[2]));
            float mean = (_cda[0] + _cda[1] + _cda[2]) / 3f;
            float spread = mean > 1e-6f ? (max - min) / mean : 0f;

            string detail = $"{rates[0]} Hz {_cda[0]:0.0000} · "
                            + $"{rates[1]} Hz {_cda[1]:0.0000} · "
                            + $"{rates[2]} Hz {_cda[2]:0.0000} m²";

            return spread <= spreadLimit
                ? Verdict.Pass(spread * 100f, "%", detail)
                : Verdict.Fail(spread * 100f, "%", detail + " — result depends on the step");
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"stage   {_stage + 1}/{rates.Length} at "
                            + $"{rates[Mathf.Min(_stage, rates.Length - 1)]} Hz");
            GUILayout.Label($"samples {_n}   {(_measuring ? "measuring" : "settling")}");
        }
    }
}
