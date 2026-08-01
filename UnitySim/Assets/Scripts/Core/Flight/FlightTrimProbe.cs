using System.IO;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// <b>[TRIM] — the first thing the aerodynamics has to prove.</b>
    ///
    /// It asks one deliberately reference-free question: <i>does a trimmed
    /// level-flight state exist at all?</i> Some throttle setting must hold the
    /// aircraft at a steady height and a steady speed, hands off. That is not a
    /// claim about any real aeroplane — no published number is involved — it is a
    /// claim about the model being self-consistent, and it is the aircraft
    /// equivalent of the car's free-roll and static-weight tests, which prove the
    /// harness before anything is measured with it.
    ///
    /// <b>Why it can fail in an informative way.</b> If lift were applied along the
    /// body up-axis instead of perpendicular to the local flow, the aircraft would
    /// gain energy every step and no throttle would hold it level. If the induced
    /// drag were counted once per panel, nothing would fly at all. If the CG were
    /// behind the neutral point, the elevator-free state would diverge instead of
    /// settling. Each of those is a real mistake, and each shows up here as "no
    /// trim found" long before a phugoid period could be misread as merely wrong.
    ///
    /// The search is a bisection on throttle with the stick centred, because the
    /// elevator-free trim speed is set by the airframe rather than by the pilot —
    /// so the answer it lands on is the aeroplane's OWN cruise, not one chosen for
    /// it.
    /// </summary>
    public sealed class FlightTrimProbe : MonoBehaviour
    {
        [Header("Rates")]
        public int physicsRateHz = 400;
        public int controlRateHz = 100;

        [Header("Search")]
        public float launchAltitude = 600f;
        public float launchAirspeed = 16f;
        /// <summary>
        /// Seconds per trial, and it has to be this long for a reason worth knowing.
        ///
        /// A launch that is not exactly at the trim speed excites the PHUGOID — the
        /// slow trade of height for speed that every aeroplane has. Here it runs at
        /// about 7.5 s a cycle and is very lightly damped (ζ ≈ 0.08, so the
        /// amplitude halves in ~10 s), which is entirely correct for an aircraft
        /// and utterly ruinous for a short measurement: a 14 s trial averages over
        /// two cycles of a live oscillation and reports climb rates that swing
        /// either side of zero with no relation to the throttle setting.
        ///
        /// 40 s is four half-lives of decay, and the window below spans three whole
        /// cycles, so what is left averages out rather than being mistaken for a
        /// trend.
        /// </summary>
        public float trialSeconds = 40f;
        [Tooltip("Seconds at the end of each trial that count — about three phugoid "
                 + "cycles, so a residual oscillation averages to nothing.")]
        public float windowSeconds = 24f;
        public int bisectionSteps = 7;

        [Header("Wings level")]
        /// <summary>
        /// Proportional aileron against bank angle — a stand-in for the pilot's
        /// thumb, and it is needed rather than optional.
        ///
        /// The propeller's torque reaction rolls the airframe steadily to the left;
        /// nothing in an aeroplane opposes that on its own, which is why every real
        /// model needs a click of aileron trim. Left alone, the aircraft banks, the
        /// vertical component of lift falls below weight, and it settles into a
        /// gentle descending spiral — at which point a longitudinal trim search
        /// measures the spiral instead of the trim, and never finds level flight at
        /// any throttle.
        ///
        /// It only touches ROLL. Pitch and throttle stay open-loop, so what the
        /// search converges on is still the aircraft's own trim and not an
        /// autopilot's.
        /// </summary>
        public bool holdWingsLevel = true;
        public float wingsLevelGain = 0.030f;
        public float rollDampGain = 0.004f;

        [Header("Verdict")]
        [Tooltip("Mean climb rate that counts as level (m/s).")]
        public float vspeedTolerance = 0.2f;
        [Tooltip("Allowed shift in MEAN airspeed between the first and second half "
                 + "of the window, as a fraction. Compares means rather than "
                 + "min/max, because an aircraft in a phugoid has a genuinely "
                 + "varying airspeed and a settled mean.")]
        public float tasDriftTolerance = 0.02f;

        private DebugPlaneRig.Rig _rig;
        private ScriptedPilot _pilot;
        private AircraftSpec _spec;

        private float _lo, _hi, _mid;
        private int _step;
        private float _t;
        private bool _done;

        // Accumulated in two halves of the window. Comparing half-means is what
        // separates "settled, with a phugoid still ringing" from "actually
        // drifting" — a min/max spread cannot tell those apart at all.
        private float _sumV1, _sumV2, _sumTas1, _sumTas2;
        private int _n1, _n2;
        // Diagnostics, so a failure says WHICH thing is off rather than only that
        // something is.
        private float _sumAlpha, _sumBank, _sumPitch, _sumCl, _sumThrust;
        private int _nDiag;
        // How hard the leveller had to work. Near zero means there was no spiral to
        // fight and the descent has some other cause; large means there was.
        private float _sumAil;
        private int _nAil;
        private Rigidbody _body;
        private float _bestThrottle, _bestV, _bestTas, _bestDrift;
        private bool _haveBest;

        private readonly StringBuilder _log = new StringBuilder();

        private void Awake()
        {
            _spec = DebugPlanes.SportRc();

            var (cam, graph) = FlightTestEnvironment.Build(FlightTestEnvironment.EnvSpec.Bare());
            Physics.SyncTransforms();

            _rig = DebugPlaneRig.BuildPlane(_spec, LaunchPos(), Quaternion.identity);
            _body = _rig.plane.GetComponent<Rigidbody>();

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = _rig.plane.transform;
            follow.offset = new Vector3(0f, 2.5f, -12f);

            DebugPlaneRig.AttachRunner(ref _rig, graph, physicsRateHz, controlRateHz,
                                       logCsv: false);
            _rig.runner.logLabel = "Trim";

            _pilot = new ScriptedPilot();
            _rig.input.source = _pilot;

            _lo = 0f;
            _hi = 1f;
            BeginTrial();
        }

        private Vector3 LaunchPos() => new Vector3(0f, launchAltitude, 0f);

        private void BeginTrial()
        {
            _mid = 0.5f * (_lo + _hi);
            _pilot.Neutral();
            _pilot.throttle = _mid;
            _rig.plane.LaunchAt(LaunchPos(), Quaternion.identity, launchAirspeed);
            _t = 0f;
            _sumV1 = _sumV2 = _sumTas1 = _sumTas2 = 0f;
            _n1 = _n2 = 0;
            _sumAlpha = _sumBank = _sumPitch = _sumCl = _sumThrust = 0f;
            _nDiag = 0;
            _sumAil = 0f;
            _nAil = 0;
        }

        /// <summary>
        /// Hold the wings level. Bank error plus roll-rate damping, which is what a
        /// thumb actually does — a pure proportional term on a lightly damped roll
        /// axis oscillates.
        ///
        /// <b>Both signs are POSITIVE, and getting that wrong is not subtle.</b> In
        /// this body frame +Z is forward, so a positive rotation about it carries
        /// the right wing UP — a positive z-Euler means the aircraft is banked
        /// LEFT, and correcting it calls for right aileron, which is a positive
        /// roll command. Writing the intuitive `-bank` makes the loop
        /// positive-feedback: the first run with it tumbled to 95° of bank and
        /// arrived at the ground at 38 m/s.
        /// </summary>
        private void HoldWingsLevel()
        {
            if (!holdWingsLevel) { _pilot.roll = 0f; return; }

            float bank = Wrap180(_rig.plane.transform.eulerAngles.z);
            Vector3 w = _rig.plane.transform.InverseTransformDirection(_body.angularVelocity);
            float rollRate = w.z * Mathf.Rad2Deg;
            _pilot.roll = Mathf.Clamp(bank * wingsLevelGain + rollRate * rollDampGain,
                                      -1f, 1f);
            _sumAil += Mathf.Abs(_pilot.roll);
            _nAil++;
        }

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;

        private void FixedUpdate()
        {
            if (_done) return;

            HoldWingsLevel();

            _t += Time.fixedDeltaTime;
            float windowStart = trialSeconds - windowSeconds;
            if (_t >= windowStart)
            {
                float v = _rig.plane.VerticalSpeed;
                float tas = _rig.plane.Air.Tas;
                if (_t < windowStart + windowSeconds * 0.5f)
                {
                    _sumV1 += v; _sumTas1 += tas; _n1++;
                }
                else
                {
                    _sumV2 += v; _sumTas2 += tas; _n2++;
                }

                var air = _rig.plane.Air;
                _sumAlpha += air.AlphaDeg;
                _sumBank += Wrap180(_rig.plane.transform.eulerAngles.z);
                _sumPitch += Wrap180(_rig.plane.transform.eulerAngles.x);
                float denom = air.Q * _spec.WingArea;
                _sumCl += denom > 1e-4f ? _rig.plane.LastAero.lift / denom : 0f;
                _sumThrust += _rig.plane.Thrust;
                _nDiag++;
            }

            // A trial that has left the sky has answered the question early.
            if (_rig.plane.AltitudeAgl < 5f) { EndTrial(diverged: true); return; }
            if (_t >= trialSeconds) EndTrial(diverged: false);
        }

        private void EndTrial(bool diverged)
        {
            int n = _n1 + _n2;
            float meanV = n > 0 ? (_sumV1 + _sumV2) / n : -999f;
            float meanTas = n > 0 ? (_sumTas1 + _sumTas2) / n : 0f;

            // Drift = how far the MEAN airspeed moved between the two halves of the
            // window. An aircraft with a live phugoid has a large min/max spread and
            // a near-identical pair of half-means; one that is genuinely losing
            // energy shows the opposite.
            float half1 = _n1 > 0 ? _sumTas1 / _n1 : 0f;
            float half2 = _n2 > 0 ? _sumTas2 / _n2 : 0f;
            float drift = meanTas > 1e-3f ? Mathf.Abs(half2 - half1) / meanTas : 1f;

            float d = Mathf.Max(1, _nDiag);
            _log.AppendLine($"[TRIM] thr {_mid:0.000}  climb {meanV:+0.000;-0.000}  "
                            + $"tas {meanTas:0.00} ({half1:0.00}→{half2:0.00}, drift {drift * 100f:0.0} %)  "
                            + $"a {_sumAlpha / d:+0.0;-0.0}°  pitch {_sumPitch / d:+0.0;-0.0}°  "
                            + $"bank {_sumBank / d:+0.0;-0.0}°  CL {_sumCl / d:0.000}  "
                            + $"T {_sumThrust / d:0.00} N  "
                            + $"|ail| {_sumAil / Mathf.Max(1, _nAil):0.000}"
                            + (diverged ? "  (hit the ground)" : ""));

            if (!_haveBest || Mathf.Abs(meanV) < Mathf.Abs(_bestV))
            {
                _haveBest = true;
                _bestV = meanV;
                _bestThrottle = _mid;
                _bestTas = meanTas;
                _bestDrift = drift;
            }

            // More throttle climbs; less descends. Bisect on the sign of the climb.
            if (meanV > 0f) _hi = _mid; else _lo = _mid;

            _step++;
            if (_step >= bisectionSteps) Finish();
            else BeginTrial();
        }

        private void Finish()
        {
            _done = true;

            // Level AND steady. Holding height while the speed bleeds away is not
            // trim, it is the top of a phugoid — so the airspeed has to be settled
            // across the window too, or the aircraft is merely passing through.
            bool level = Mathf.Abs(_bestV) <= vspeedTolerance;
            bool steady = _bestDrift <= tasDriftTolerance;
            bool pass = _haveBest && level && steady;

            _log.AppendLine();
            _log.AppendLine($"[TRIM] best throttle {_bestThrottle:0.000} → "
                            + $"{_bestTas:0.00} m/s at {_bestV:+0.000;-0.000} m/s climb");
            _log.AppendLine($"[TRIM] predicted stall {_spec.PredictedStallSpeed:0.00} m/s, "
                            + $"so trim sits at {_bestTas / Mathf.Max(0.01f, _spec.PredictedStallSpeed):0.00}× Vs "
                            + "— a trainer cruises around 1.5–2.5× its stall speed");
            Debug.Log(_log.ToString().TrimEnd());

            string summary = pass
                ? $"[TRIM] RESULT PASS — trimmed level flight exists at "
                  + $"{_bestTas:0.0} m/s on {_bestThrottle * 100f:0} % throttle "
                  + $"(climb {_bestV:+0.00;-0.00} m/s, tol ±{vspeedTolerance})"
                : $"[TRIM] RESULT FAIL — no level trim found; best was "
                  + $"{_bestV:+0.00;-0.00} m/s climb at {_bestThrottle * 100f:0} % throttle "
                  + $"with {_bestDrift * 100f:0.0} % airspeed drift";
            if (pass) Debug.Log(summary); else Debug.LogError(summary);

            WriteResult(pass);

            if (Application.isBatchMode)
            {
                // Two frames of grace so the log flushes before the editor goes.
                var go = new GameObject("TrimExit");
                var exit = go.AddComponent<DelayedExit>();
                exit.code = pass ? 0 : 1;
            }
        }

        private void WriteResult(bool pass)
        {
            string dir = ResultDir();
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "trim_result.json"),
                    JsonUtility.ToJson(new Result
                    {
                        pass = pass,
                        throttle = _bestThrottle,
                        tas = _bestTas,
                        climb = _bestV,
                        stallSpeed = _spec.PredictedStallSpeed,
                    }, true));
            }
            catch (System.Exception e) { Debug.LogWarning($"[TRIM] could not write result: {e.Message}"); }
        }

        private static string ResultDir()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-trimResultDir") return args[i + 1];
            return Path.GetTempPath();
        }

        [System.Serializable]
        private class Result
        {
            public bool pass;
            public float throttle, tas, climb, stallSpeed;
        }

        /// <summary>Leaves play mode after a couple of frames so the log flushes.</summary>
        private sealed class DelayedExit : MonoBehaviour
        {
            public int code;
            private int _frames;
            private void Update()
            {
                if (++_frames < 3) return;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.Exit(code);
#endif
            }
        }
    }
}
