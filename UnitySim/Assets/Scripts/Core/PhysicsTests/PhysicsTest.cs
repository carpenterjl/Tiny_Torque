using System;
using System.IO;
using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// Base class for a single physics measurement: stands a subject up, drives it
    /// from a script, watches one quantity, and reports a verdict against a stated
    /// reference.
    ///
    /// <b>This layer knows nothing about what it is measuring.</b> The settle /
    /// arm / sync / run machine, the verdict, the result JSON, the HUD and the
    /// headless plumbing are the same whether the subject has wheels or wings.
    /// <see cref="CarPhysicsTest"/> supplies the Tiguan and its wheel-shaped
    /// helpers; a flight subject supplies an aircraft. Neither inherits the
    /// other's vocabulary, and this class never learns which it has.
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
    /// <b>Ordering that matters.</b> The vehicle is built, then the camera, then
    /// the runner — <see cref="DebugVehicleRig"/>'s class comment explains why, and
    /// it is load-bearing: CsvLogger snapshots its column list once, so the
    /// telemetry component must exist before the runner's Start enables logging.
    /// That whole sequence lives inside <see cref="BuildSubject"/>, so it cannot be
    /// split across a hook boundary by accident.
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

        /// <summary>Set the initial condition. Called once, after the settle.</summary>
        protected virtual void Arm() { }
        /// <summary>Accumulate a sample. Called every fixed step during the run.</summary>
        protected abstract void Sample(float dt);
        /// <summary>Return the verdict, or <c>null</c> to keep running until the
        /// timeout. Called every fixed step once the run phase is live.</summary>
        protected abstract Verdict? Evaluate();

        /// <summary>Extra HUD lines, drawn under the standard block.</summary>
        protected virtual void DrawExtra() { }
        /// <summary>Graph panes for this test. The subject layer supplies the
        /// default, because which channels are worth watching depends entirely on
        /// what is being measured.</summary>
        protected virtual void ConfigureGraph(GraphOverlay g) { }

        // ---- what a SUBJECT layer supplies -------------------------------
        //
        // Everything above is about measuring; everything here is about what is
        // being measured. CarPhysicsTest fills these in for the Tiguan, and a
        // flight test fills them in for an aircraft — neither one inherits the
        // other's helpers, and the phase machine below never learns which it has.

        /// <summary>Build the world, the vehicle, the camera and the runner. Must
        /// leave <see cref="Body"/> and <see cref="Runner"/> assigned.</summary>
        protected abstract void BuildSubject();

        /// <summary>Swap the scripted controller in, replacing the human one.</summary>
        protected abstract void InstallScriptedInput();

        /// <summary>Hand the controls back to a person. One line, but it is the
        /// one line that differs between a steering wheel and a stick.</summary>
        protected abstract void HandControlsToHuman();

        /// <summary>Hold whatever the subject holds before the run begins.</summary>
        protected abstract void IdleInputs();

        /// <summary>Write this tick's inputs. <paramref name="t"/> is seconds since
        /// the run phase began.</summary>
        protected abstract void DriveInputs(float t);

        /// <summary>
        /// Whether the launch condition has been reached. A car waits for its
        /// wheels to spin up to road speed; an aircraft waits to settle at a trim
        /// airspeed. Same phase, same 10 s patience, same Invalid verdict if it
        /// never arrives — only the question differs.
        /// </summary>
        protected virtual bool SyncReady(out string why) { why = ""; return true; }

        /// <summary>Set by a subject that launched into a condition worth waiting
        /// for. Reproduces the original gate exactly: the sync phase was entered
        /// when a launch speed had been requested, and not otherwise.</summary>
        protected bool WantsSync { get; set; }

        /// <summary>Prefix on the console row and the result filename. Keeps a
        /// flight suite's output from being collected by the physics gate, and vice
        /// versa, without either runner needing to know the other exists.</summary>
        protected virtual string ResultFamily => "phys";

        /// <summary>The one-line reminder under the HUD of what this world is.</summary>
        protected virtual string HudFooter =>
            "frictionless ground · assists OFF · tyre model only";

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

        protected Rigidbody Body { get; set; }
        protected SimulationRunner Runner { get; set; }
        protected TelemetryHub Hub => Runner != null ? Runner.Hub : null;
        protected Phase CurrentPhase { get; private set; } = Phase.Settle;
        /// <summary>Seconds since the run phase began. Zero before it does.</summary>
        protected float RunTime { get; private set; }

        private float _phaseT;
        private Result _result;
        private bool _manual;

        // ---- build ----

        private void Awake()
        {
            // World, vehicle, camera, runner — the subject layer owns all four,
            // and the order inside it is load-bearing. See CarPhysicsTest.
            BuildSubject();

            // A per-test CSV, and it must be set before the runner's Start:
            // CsvLogger.Begin fixes the filename and the column list there.
            Runner.logLabel = TestId;

            // The runner's own M binding toggles Manual/Autonomous. With no
            // controller DLL loaded that would stop the car dead mid-test, and it
            // would collide with this class's M (manual override). One key, one
            // meaning.
            Runner.allowModeToggle = false;

            InstallScriptedInput();

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
            HandControlsToHuman();
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
                    IdleInputs();
                    // The static probe settles 5 s before reading anything, and
                    // its numbers are this suite's baseline. Same wait, same
                    // starting state.
                    if (_phaseT >= settleSec) Enter(Phase.Arm);
                    break;

                case Phase.Arm:
                    IdleInputs();
                    Arm();
                    Enter(WantsSync ? Phase.Sync : Phase.Run);
                    break;

                case Phase.Sync:
                    IdleInputs();
                    // Wait for the subject's own launch condition — see SyncReady.
                    if (SyncReady(out string why))
                        Enter(Phase.Run);
                    else if (_phaseT > 10f)
                        Finish(new Verdict
                        {
                            kind = Kind.Invalid,
                            detail = why,
                        });
                    break;

                case Phase.Run:
                    RunTime = _phaseT;
                    DriveInputs(RunTime);
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
            string line = $"{LogTag} {TestId} {v.kind.ToString().ToUpperInvariant()} "
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

        /// <summary>Result path for a physics test. Kept as the one-argument form
        /// the two editor runners already call, so neither of them changes.</summary>
        public static string ResultPathFor(string testId) => ResultPathFor("phys", testId);

        /// <summary>Result path for any suite. A flight test writes
        /// <c>aero_A1.json</c> beside the car's <c>phys_P1.json</c>, so the two
        /// gates can share a result directory without collecting each other's
        /// output.</summary>
        public static string ResultPathFor(string family, string testId) =>
            Path.Combine(ResultDir, $"{family}_{testId}.json");

        /// <summary>Console prefix. <c>[PHYS]</c> for the car suite; a flight suite
        /// overrides it so a build gate grepping one block never picks up the
        /// other's rows.</summary>
        protected virtual string LogTag => "[PHYS]";

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
                string path = ResultPathFor(ResultFamily, TestId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(_result, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag} {TestId} could not write result: {e.Message}");
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

            GUILayout.Label(HudFooter);
            GUILayout.EndArea();
        }

        private static GUIStyle _rich;
        private static GUIStyle RichLabel() =>
            _rich ??= new GUIStyle(GUI.skin.label) { richText = true };
    }
}
