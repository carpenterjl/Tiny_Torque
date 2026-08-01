using System;
using System.IO;
using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// Base class for a single physics measurement: builds the world and the
    /// full-scale Tiguan, drives the car from a script, watches one quantity,
    /// and reports a verdict against a stated reference.
    ///
    /// <b>Scripted, not driven.</b> A coastdown whose start speed depends on a
    /// human releasing the throttle near 32 m/s is not repeatable, and a number
    /// that is not repeatable is not a measurement. Every test here sets its own
    /// initial condition and its own inputs. Pressing <b>M</b> hands you the
    /// controls — and marks the verdict INVALID, because a result you influenced
    /// should never be reported as one you didn't.
    ///
    /// <b>The verdict on screen and the verdict in the gate are the same
    /// object.</b> <see cref="Result"/> is written to JSON and drawn in the HUD
    /// from one place, so the headless validator cannot report something the
    /// screen disagrees with.
    ///
    /// <b>Ordering that matters.</b> The car is built, then the camera, then the
    /// runner — <see cref="DebugVehicleRig"/>'s class comment explains why, and
    /// it is load-bearing: CsvLogger snapshots its column list once, so the
    /// telemetry component must exist before the runner's Start enables logging.
    /// </summary>
    [DefaultExecutionOrder(-4000)]
    public abstract class PhysicsTest : MonoBehaviour
    {
        // ---- what a subclass declares ----

        /// <summary>Short id: "P1". Names the result file and the console row.</summary>
        protected abstract string TestId { get; }
        /// <summary>Human title for the HUD.</summary>
        protected abstract string Title { get; }
        /// <summary>The reference this test is judged against, as text — shown on
        /// the HUD beside the live value so a number is never read without it.</summary>
        protected abstract string Expected { get; }
        /// <summary>The world this test needs. Default is the plain straight.</summary>
        protected virtual PhysicsTestEnvironment.EnvSpec Environment =>
            PhysicsTestEnvironment.EnvSpec.Default();

        /// <summary>Where the car starts. Flat ground at the measured rest height
        /// by default; <see cref="OnSurfaceAt"/> puts it on the slope instead.</summary>
        protected virtual (Vector3 pos, Quaternion rot) SpawnPose() =>
            (new Vector3(0f, DebugVehicles.TiguanChassisRestY, 0f), Quaternion.identity);

        /// <summary>What the driver holds before the run begins. Neutral for
        /// almost everything — but a test that parks on a slope has to hold the
        /// handbrake through its own settle, or it slides away before it arms.</summary>
        protected virtual void Idle(ScriptedDriver d) => d.Neutral();

        /// <summary>Set the initial condition. Called once, after the settle.</summary>
        protected virtual void Arm() { }
        /// <summary>Write this tick's inputs. <paramref name="t"/> is seconds
        /// since the run phase began.</summary>
        protected abstract void Drive(ScriptedDriver d, float t);
        /// <summary>Accumulate a sample. Called every fixed step during the run.</summary>
        protected abstract void Sample(float dt);
        /// <summary>Return the verdict, or <c>null</c> to keep running until the
        /// timeout. Called every fixed step once the run phase is live.</summary>
        protected abstract Verdict? Evaluate();

        /// <summary>Extra HUD lines, drawn under the standard block.</summary>
        protected virtual void DrawExtra() { }
        /// <summary>Graph panes for this test. Default shows speed and the three
        /// body-frame accelerations.</summary>
        protected virtual void ConfigureGraph(GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("accel (m/s²)", "veh/a_long", "veh/a_lat", "veh/a_vert");
        }

        // ---- inspector ----

        [Header("Rates")]
        [Tooltip("400 to match TrackBootstrap, which is what the Opus gate runs on.")]
        public int physicsRateHz = 400;
        public int controlRateHz = 100;

        [Header("Timing")]
        [Tooltip("Seconds of untouched settle before the test arms. The static "
                 + "probe uses 5 s and its numbers are the baseline, so changing "
                 + "this changes what P0/P2 are being compared against.")]
        public float settleSec = 5f;
        [Tooltip("Give up and FAIL after this many seconds of run phase. Sized "
                 + "for the longest test: P9 runs three coastdowns back to back.")]
        public float timeoutSec = 240f;

        [Header("Options")]
        public bool logCsv = true;
        [Tooltip("Leave play mode when the verdict lands. Always on in batch mode.")]
        public bool exitWhenDone = false;

        // ---- verdict ----

        public enum Kind
        {
            /// <summary>Measured, inside its band. Gates.</summary>
            Pass,
            /// <summary>Measured, outside its band. Gates.</summary>
            Fail,
            /// <summary>Measured and reported, but never gates — the reference is
            /// a real-car figure this model is known to structurally disagree
            /// with. Gating on a known modelling limit turns it into a red build
            /// everyone learns to ignore.</summary>
            Info,
            /// <summary>Not a result: manual override, or the run timed out.</summary>
            Invalid,
        }

        public struct Verdict
        {
            public Kind kind;
            public float value;
            public string units;
            public string detail;

            public static Verdict Pass(float v, string units, string detail = "") =>
                new Verdict { kind = Kind.Pass, value = v, units = units, detail = detail };
            public static Verdict Fail(float v, string units, string detail = "") =>
                new Verdict { kind = Kind.Fail, value = v, units = units, detail = detail };
            public static Verdict Info(float v, string units, string detail = "") =>
                new Verdict { kind = Kind.Info, value = v, units = units, detail = detail };

            /// <summary>Pass when <paramref name="v"/> is within
            /// <paramref name="tol"/> of <paramref name="target"/>, else Fail.</summary>
            public static Verdict Band(float v, float target, float tol, string units)
            {
                float err = v - target;
                string d = $"err {err:+0.####;-0.####} (tol ±{tol:0.####})";
                return Mathf.Abs(err) <= tol ? Pass(v, units, d) : Fail(v, units, d);
            }
        }

        [Serializable]
        public sealed class Result
        {
            public string testId = "";
            public string title = "";
            public string kind = "";        // Pass / Fail / Info / Invalid
            public float value;
            public string units = "";
            public string expected = "";
            public string detail = "";
            public float elapsedSec;
            public int physicsRateHz;
            public bool manual;             // a human touched it: value is not a measurement
        }

        // ---- state ----

        protected enum Phase { Settle, Arm, Sync, Run, Done }

        protected CarVehicle Car { get; private set; }
        protected Rigidbody Body { get; private set; }
        protected SimulationRunner Runner { get; private set; }
        protected TelemetryHub Hub => Runner != null ? Runner.Hub : null;
        protected ScriptedDriver Driver { get; private set; }
        protected Phase CurrentPhase { get; private set; } = Phase.Settle;
        /// <summary>Seconds since the run phase began. Zero before it does.</summary>
        protected float RunTime { get; private set; }

        private CarInput _input;
        private float _phaseT;
        private Result _result;
        private bool _manual;
        private float _syncTargetSpeed = -1f;
        private bool _syncNeedsSpeed;

        // ---- build ----

        private void Awake()
        {
            var (cam, graph) = PhysicsTestEnvironment.Build(Environment);

            // Colliders were created moments ago in Build(); queries read the
            // physics scene, which has not been told about them yet.
            Physics.SyncTransforms();
            var (spawn, spawnRot) = SpawnPose();
            var rig = DebugVehicleRig.BuildCar(DebugVehicles.VwTiguan(), spawn, spawnRot);
            Car = rig.car;
            Body = Car.GetComponent<Rigidbody>();
            _input = rig.input;

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = Car.transform;

            DebugVehicleRig.AttachRunner(ref rig, graph, physicsRateHz, controlRateHz, logCsv);
            Runner = rig.runner;

            // A per-test CSV, and it must be set before the runner's Start:
            // CsvLogger.Begin fixes the filename and the column list there.
            Runner.logLabel = TestId;

            // The runner's own M binding toggles Manual/Autonomous. With no
            // controller DLL loaded that would stop the car dead mid-test, and it
            // would collide with this class's M (manual override). One key, one
            // meaning.
            Runner.allowModeToggle = false;

            Driver = new ScriptedDriver();
            _input.source = Driver;

            _result = new Result
            {
                testId = TestId,
                title = Title,
                expected = Expected,
                physicsRateHz = physicsRateHz,
                kind = "",
            };

            if (Application.isBatchMode) exitWhenDone = true;
        }

        private void Start()
        {
            if (Runner != null && Runner.graph != null)
            {
                Runner.graph.ClearPanes();
                ConfigureGraph(Runner.graph);
            }
        }

        private void Update()
        {
            if (_manual || CurrentPhase == Phase.Done) return;
            if (Input.GetKeyDown(KeyCode.M)) TakeOverManually();
        }

        /// <summary>
        /// Hand the car to the keyboard. The verdict becomes INVALID rather than
        /// simply being suppressed: a run a human touched must not be able to
        /// masquerade as a measurement, and silence would look like "not run yet".
        /// </summary>
        private void TakeOverManually()
        {
            _manual = true;
            _input.source = new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad);
            Finish(new Verdict
            {
                kind = Kind.Invalid,
                units = "",
                detail = "manual override — drive it yourself; this is not a measurement",
            });
        }

        // ---- the state machine ----

        private void FixedUpdate()
        {
            if (_manual || CurrentPhase == Phase.Done) return;
            float dt = Time.fixedDeltaTime;
            _phaseT += dt;

            switch (CurrentPhase)
            {
                case Phase.Settle:
                    Idle(Driver);
                    // The static probe settles 5 s before reading anything, and
                    // its numbers are this suite's baseline. Same wait, same
                    // starting state.
                    if (_phaseT >= settleSec) Enter(Phase.Arm);
                    break;

                case Phase.Arm:
                    Idle(Driver);
                    Arm();
                    Enter(_syncTargetSpeed > 0f ? Phase.Sync : Phase.Run);
                    break;

                case Phase.Sync:
                    Idle(Driver);
                    // Wheels rolling true, and — only when the caller launched
                    // deliberately fast — back down to the speed the window is
                    // defined at. Waiting for a speed that was never overshot
                    // would hang forever in exactly the tests that coast without
                    // losing any (P0 runs with drag disabled on purpose).
                    if (WheelsSynced()
                        && (!_syncNeedsSpeed || Speed <= _syncTargetSpeed))
                        Enter(Phase.Run);
                    else if (_phaseT > 10f)
                        Finish(new Verdict
                        {
                            kind = Kind.Invalid,
                            detail = "wheels never synced after launch — " + WheelStateText(),
                        });
                    break;

                case Phase.Run:
                    RunTime = _phaseT;
                    Drive(Driver, RunTime);
                    Sample(dt);
                    var v = Evaluate();
                    if (v.HasValue) { Finish(v.Value); break; }
                    if (RunTime > timeoutSec)
                        Finish(new Verdict
                        {
                            kind = Kind.Invalid,
                            detail = $"timed out after {timeoutSec:0} s",
                        });
                    break;
            }
        }

        private void Enter(Phase p)
        {
            CurrentPhase = p;
            _phaseT = 0f;
            if (p == Phase.Run) RunTime = 0f;
        }

        // ---- launching at speed ----

        /// <summary>
        /// Start the run already moving, without driving up to speed.
        ///
        /// The powertrain on this vehicle is declared fiction — no gearbox, no
        /// torque curve — so a test that accelerates first is measuring the
        /// fiction. Setting the body's velocity alone is not enough either: the
        /// wheels would still be stationary, the tyres would see slip ratio −1,
        /// and the run would open with a locked-wheel skid.
        ///
        /// So set the body's velocity and let the tyres spin the wheels up
        /// naturally, then start measuring. Against J ≈ 1.8 kg·m² the tyre torque
        /// is ~700 rad/s², so they sync in a fraction of a second.
        ///
        /// <paramref name="overshootMps"/> is for tests whose window is defined
        /// at an exact speed (P1 measures 32 → 22): launch above it and the run
        /// begins when the car falls back through the target with the wheels
        /// already true. Leave it at zero when the test simply needs to be
        /// moving — waiting for a speed that was never overshot never arrives in
        /// a test that coasts without losing speed, which is precisely what P0
        /// is built to do.
        /// </summary>
        protected void LaunchAt(float targetMps, float overshootMps = 0f)
        {
            _syncTargetSpeed = targetMps;
            _syncNeedsSpeed = overshootMps > 0f;
            Body.linearVelocity = Car.transform.forward * (targetMps + overshootMps);
            Body.angularVelocity = Vector3.zero;
        }

        /// <summary>Per-wheel slip / ω / contact, for a diagnostic that says what
        /// went wrong rather than only that something did.</summary>
        protected string WheelStateText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"v {Speed:0.000} ");
            for (int i = 0; i < Car.WheelCount; i++)
                sb.Append($"[{i} slip {Car.WheelSlipRatio(i):0.0000} "
                          + $"ω {Car.WheelOmega(i):0.00} "
                          + $"{(Car.WheelGrounded(i) ? "gnd" : "AIR")}] ");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Disconnect the driveline — the model's equivalent of coasting in
        /// neutral. <b>Any coasting test needs this, and the reason is not
        /// obvious.</b>
        ///
        /// At zero throttle the ESC state machine falls through to
        /// <c>MotorModel.WheelTorque(0 V)</c>, and a DC motor at zero volts is
        /// not disconnected — it is a SHORT. Back-EMF drives
        /// <c>I = −Kt·ω/R</c> through the winding and the motor becomes a brake.
        /// For an RC car that is right: a brushed ESC at neutral really does
        /// brake. For a 1500 kg car it is wrong, because a real one has a clutch
        /// or a torque converter and coasts freely.
        ///
        /// Measured here, it is worth about <b>1.7 m/s² at 10 m/s</b> — six times
        /// the whole aerodynamic drag at that speed. A coastdown run with the
        /// driveline live measures the motor's winding resistance and reports it
        /// as Cd·A.
        ///
        /// The disconnect is physical rather than a flag: raising the winding
        /// resistance is an open circuit, so the current, and with it the torque,
        /// goes to zero through the model's own equation instead of around it.
        /// </summary>
        protected void SetFreewheel(bool on)
        {
            var motors = Car.Motors;
            if (_motorResistance == null)
            {
                _motorResistance = new float[motors.Count];
                for (int i = 0; i < motors.Count; i++)
                    _motorResistance[i] = motors[i] != null ? motors[i].motor.resistance : 0f;
            }
            for (int i = 0; i < motors.Count; i++)
            {
                var m = motors[i];
                if (m == null) continue;
                var p = m.motor;
                p.resistance = on ? 1e6f : _motorResistance[i];
                m.motor = p;
            }
        }

        private float[] _motorResistance;

        /// <summary>
        /// A spawn pose sitting on whatever surface is under (x, z), aligned to
        /// it. Found by raycast rather than by arithmetic: the slope's height at
        /// a given z is a function of its rotation, position and scale, and three
        /// chances to get a sign wrong is three too many for a placement whose
        /// failure mode is a car quietly starting 10 cm inside the ground.
        /// </summary>
        protected static (Vector3 pos, Quaternion rot) OnSurfaceAt(
            float x, float z, Vector3 facing)
        {
            var origin = new Vector3(x, 500f, z);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f,
                                 ~0, QueryTriggerInteraction.Ignore))
                return (new Vector3(x, DebugVehicles.TiguanChassisRestY, z),
                        Quaternion.identity);

            // Rest height is measured perpendicular to the ground, so it goes
            // along the surface normal, not along world up.
            var pos = hit.point + hit.normal * DebugVehicles.TiguanChassisRestY;
            var fwd = Vector3.ProjectOnPlane(facing, hit.normal).normalized;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            return (pos, Quaternion.LookRotation(fwd, hit.normal));
        }

        /// <summary>
        /// Put the car at a speed and start measuring immediately, with no wait
        /// for the wheels to spin up.
        ///
        /// For a test that drives — rather than coasts — the launch transient is
        /// harmless and the wait is actively wrong: a driven wheel carries real
        /// motor torque, so it holds a steady slip of a couple of percent
        /// forever, and a sync gate gets stuck waiting for a zero that a working
        /// car never reaches.
        /// </summary>
        protected void SetSpeedNow(float mps)
        {
            Body.linearVelocity = Car.transform.forward * mps;
            Body.angularVelocity = Vector3.zero;
        }

        /// <summary>Every grounded wheel rolling within 1 % of the road speed.</summary>
        private bool WheelsSynced()
        {
            for (int i = 0; i < Car.WheelCount; i++)
            {
                if (!Car.WheelGrounded(i)) return false;
                if (Mathf.Abs(Car.WheelSlipRatio(i)) > 0.01f) return false;
            }
            return true;
        }

        // ---- channels ----

        /// <summary>Latest value of a telemetry channel, or 0 if absent. Reading
        /// the hub rather than the physics keeps a test from accidentally
        /// depending on state no other observer can see.</summary>
        protected float Ch(string name)
        {
            var hub = Hub;
            if (hub == null) return 0f;
            return hub.TryGetChannel(name, out var c) ? c.Latest : 0f;
        }

        protected float Speed => Body != null ? Body.linearVelocity.magnitude : 0f;

        // ---- finishing ----

        private void Finish(Verdict v)
        {
            Enter(Phase.Done);

            _result.kind = v.kind.ToString();
            _result.value = v.value;
            _result.units = v.units ?? "";
            _result.detail = v.detail ?? "";
            _result.elapsedSec = RunTime;
            _result.manual = _manual;

            // 8 significant places, not 5: P6b's drift is micrometres and "0.#####"
            // printed it as a bare "0", which in a gate line reads as "exactly
            // zero" rather than "far below the limit".
            string line = $"[PHYS] {TestId} {v.kind.ToString().ToUpperInvariant()} "
                          + $"{v.value:0.########} {_result.units} (expect {Expected})"
                          + (string.IsNullOrEmpty(_result.detail) ? "" : $" — {_result.detail}");
            if (v.kind == Kind.Fail) Debug.LogError(line);
            else Debug.Log(line);

            WriteResult();

            if (logCsv && Runner != null && Runner.HasUnsavedTelemetry)
                Runner.SaveTelemetry();

            if (exitWhenDone) StartCoroutine(ExitSoon());
        }

        /// <summary>Where the result JSON lands. <c>-physResultDir</c> lets the
        /// headless validator collect them somewhere it chose; otherwise the
        /// system temp folder, same as the static probe.</summary>
        public static string ResultDir
        {
            get
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-physResultDir") return args[i + 1];
                return Path.GetTempPath();
            }
        }

        public static string ResultPathFor(string testId) =>
            Path.Combine(ResultDir, $"phys_{testId}.json");

        /// <summary>True when the whole suite is being walked in ONE editor
        /// session (<c>-physSuite</c>). A finished test must then leave play mode
        /// without killing the process, or the runner would never reach the second
        /// scene. Single-test runs keep exiting, which is what makes them usable
        /// one command at a time.</summary>
        public static bool SuiteMode
        {
            get
            {
                var args = System.Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                    if (args[i] == "-physSuite") return true;
                return false;
            }
        }

        private void WriteResult()
        {
            try
            {
                string path = ResultPathFor(TestId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(_result, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PHYS] {TestId} could not write result: {e.Message}");
            }
        }

        /// <summary>One frame of grace so the final HUD draws and the CSV flush
        /// completes before play mode ends.</summary>
        private System.Collections.IEnumerator ExitSoon()
        {
            yield return null;
            yield return null;
#if UNITY_EDITOR
            // Suite mode leaves play mode but keeps the process: the validator has
            // nine more scenes to open. See SuiteMode.
            if (Application.isBatchMode && !SuiteMode) UnityEditor.EditorApplication.Exit(0);
            else UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // ---- HUD ----

        private void OnGUI()
        {
            const float w = 380f;
            GUILayout.BeginArea(new Rect(10f, 10f, w, 260f), GUI.skin.box);

            GUILayout.Label($"<b>{TestId} — {Title}</b>", RichLabel());
            GUILayout.Label($"expect  {Expected}");
            GUILayout.Space(4f);

            GUILayout.Label($"phase   {CurrentPhase}"
                            + (CurrentPhase == Phase.Run ? $"   t {RunTime:0.00} s" : ""));
            GUILayout.Label($"speed   {Speed:0.00} m/s   ({Speed * 3.6f:0.0} km/h)");

            DrawExtra();

            GUILayout.Space(4f);
            if (CurrentPhase == Phase.Done)
            {
                var old = GUI.color;
                GUI.color = _result.kind == nameof(Kind.Pass) ? Color.green
                          : _result.kind == nameof(Kind.Fail) ? Color.red
                          : _result.kind == nameof(Kind.Info) ? Color.cyan
                          : Color.yellow;
                GUILayout.Label($"<b>{_result.kind.ToUpperInvariant()}  "
                                + $"{_result.value:0.#####} {_result.units}</b>", RichLabel());
                GUI.color = old;
                if (!string.IsNullOrEmpty(_result.detail))
                    GUILayout.Label(_result.detail);
            }
            else
            {
                GUILayout.Label(_manual ? "manual" : "M — take over (voids the result)");
            }

            GUILayout.Label("frictionless ground · assists OFF · tyre model only");
            GUILayout.EndArea();
        }

        private static GUIStyle _rich;
        private static GUIStyle RichLabel() =>
            _rich ??= new GUIStyle(GUI.skin.label) { richText = true };
    }
}
