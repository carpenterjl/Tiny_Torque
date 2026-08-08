using System.Collections.Generic;
using AIHWSim.Arcade;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using AIHWSim.Modes;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[DSC] — the config gate.</b> Holds the one claim the whole
    /// settings-asset layer rests on: <b>a freshly created asset changes
    /// nothing</b>.
    ///
    /// Every asset in <c>Core/Config</c> and <see cref="AssistTuningOverride"/>
    /// carries field initialisers that are supposed to be the literals the code
    /// already used. "Supposed to be" is the problem — those literals live in
    /// two files now, and nothing in the project would notice if one of them
    /// moved. The design dump cannot see it (no design reaches these), [PHYS]
    /// cannot see it (no test assigns one), and a play session would only
    /// notice if a human happened to look at the right number. So this compares
    /// the two sources directly, in edit mode, in about a second.
    ///
    /// Each check is a comparison of two things that CAN disagree, which is the
    /// only kind worth making about a default: the asset against the code it was
    /// transcribed from, not the asset against itself.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.DrivingConfigValidator.Report -logFile &lt;log&gt;
    /// </code>
    /// </summary>
    public static class DrivingConfigValidator
    {
        private const string Tag = "[DSC]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Driving Scene/Validate Config Defaults [DSC]", priority = 402)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            CheckLevelDefaults();
            CheckLevelClamps();
            CheckPhysicsDefaults();
            CheckAssistDefaults();
            CheckModeDefaults();
            CheckArcadeDefaults();
            CheckRateAuthority();

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual))
                Fails.Add($"{what}: asset says {actual}, code says {expected}");
        }

        private static void Near(string what, float expected, float actual)
        {
            _checks++;
            // Bit-equality, not a tolerance: these are supposed to be the SAME
            // literal, and "close enough" is how a 0.3 quietly becomes a 0.30001.
            if (expected != actual)
                Fails.Add($"{what}: asset says {actual}, code says {expected}");
        }

        /// <summary>
        /// A new LevelSettings must describe the session
        /// <see cref="SessionConfig.SetSinglePlayer"/> produces — the one every
        /// legacy entry path already made. Compared by APPLYING it over that
        /// state and asserting nothing moved, so the check exercises
        /// <c>ApplyTo</c> rather than re-reading the fields it just set.
        /// </summary>
        private static void CheckLevelDefaults()
        {
            var before = Snapshot();
            try
            {
                SessionConfig.SetSinglePlayer();
                var expect = Capture();

                var lvl = ScriptableObject.CreateInstance<LevelSettings>();
                lvl.ApplyTo();
                var got = Capture();
                Object.DestroyImmediate(lvl);

                Eq("LevelSettings.match", expect.match, got.match);
                Eq("LevelSettings.targetLaps", expect.laps, got.laps);
                Eq("LevelSettings.targetScore", expect.score, got.score);
                Eq("LevelSettings.timeLimitSec", expect.timeLimit, got.timeLimit);
                Eq("LevelSettings.countdownSeconds", expect.countdown, got.countdown);
                Eq("LevelSettings.resultsWaitSeconds", expect.resultsWait, got.resultsWait);
                Eq("LevelSettings.rubberBand", expect.rubberBand, got.rubberBand);
                Eq("LevelSettings.arcade", expect.arcade, got.arcade);
                Eq("LevelSettings.trackLimits", expect.trackLimits, got.trackLimits);
                Eq("LevelSettings.arcadeHandling", expect.arcadeHandling, got.arcadeHandling);
            }
            finally { Restore(before); }
        }

        /// <summary>
        /// Negative counts are clamped rather than written through. A lap count
        /// of −1 does not mean anything, and <c>TargetLaps &gt; 0</c> is the test
        /// three separate composition branches make.
        /// </summary>
        private static void CheckLevelClamps()
        {
            var before = Snapshot();
            try
            {
                var lvl = ScriptableObject.CreateInstance<LevelSettings>();
                lvl.targetLaps = -5;
                lvl.countdownSeconds = -5;
                lvl.resultsWaitSeconds = -5;
                lvl.timeLimitSec = -5;
                lvl.targetScore = -5;
                lvl.ApplyTo();
                Object.DestroyImmediate(lvl);

                Eq("clamp targetLaps", 0, SessionConfig.TargetLaps);
                Eq("clamp countdownSeconds", 0, SessionConfig.CountdownSeconds);
                Eq("clamp resultsWaitSeconds", 0, SessionConfig.ResultsWaitSeconds);
                Eq("clamp timeLimitSec", 0, SessionConfig.TimeLimitSec);
                Eq("clamp targetScore", 1, SessionConfig.TargetScore);
            }
            finally { Restore(before); }
        }

        /// <summary>
        /// A new PhysicsSettings must leave the solver where
        /// <c>PhysicsTuning.Apply(null)</c> puts it. Both are applied for real
        /// and the globals read back, because what these numbers DO is the
        /// subject — reading the asset's fields would only prove it agrees with
        /// itself.
        /// </summary>
        private static void CheckPhysicsDefaults()
        {
            float offset = Physics.defaultContactOffset;
            int iters = Physics.defaultSolverIterations;
            int vIters = Physics.defaultSolverVelocityIterations;
            float depen = Physics.defaultMaxDepenetrationVelocity;
            float maxDt = Time.maximumDeltaTime;
            try
            {
                PhysicsTuning.Apply(null);
                float eOffset = Physics.defaultContactOffset;
                int eIters = Physics.defaultSolverIterations;
                int eVIters = Physics.defaultSolverVelocityIterations;
                float eDepen = Physics.defaultMaxDepenetrationVelocity;
                float eMaxDt = Time.maximumDeltaTime;

                var ps = ScriptableObject.CreateInstance<PhysicsSettings>();
                PhysicsTuning.Apply(ps);

                Near("PhysicsSettings.defaultContactOffset", eOffset, Physics.defaultContactOffset);
                Eq("PhysicsSettings.defaultSolverIterations", eIters, Physics.defaultSolverIterations);
                Eq("PhysicsSettings.defaultSolverVelocityIterations",
                   eVIters, Physics.defaultSolverVelocityIterations);
                Near("PhysicsSettings.defaultMaxDepenetrationVelocity",
                     eDepen, Physics.defaultMaxDepenetrationVelocity);
                Near("PhysicsSettings.maximumDeltaTime", eMaxDt, Time.maximumDeltaTime);

                // The rates are the scene's, not the engine's, so they are checked
                // against the bootstrap default they replace rather than against a
                // global — 400 Hz is the step the RC suspension is authored for and
                // an asset that quietly said 500 would retune every car.
                Eq("PhysicsSettings.physicsRateHz", 400, ps.physicsRateHz);
                Eq("PhysicsSettings.controlRateHz", 100, ps.controlRateHz);

                Object.DestroyImmediate(ps);
            }
            finally
            {
                Physics.defaultContactOffset = offset;
                Physics.defaultSolverIterations = iters;
                Physics.defaultSolverVelocityIterations = vIters;
                Physics.defaultMaxDepenetrationVelocity = depen;
                Time.maximumDeltaTime = maxDt;
            }
        }

        /// <summary>
        /// Every AssistTuning accessor must read the same with a fresh override
        /// installed as with none. This is the check with real teeth: seventeen
        /// numbers were duplicated from a static class into a ScriptableObject,
        /// and they sit in different files where nothing but this would notice
        /// one drifting.
        /// </summary>
        private static void CheckAssistDefaults()
        {
            var prev = AssistTuning.Override;
            try
            {
                AssistTuning.Override = null;
                var d = ReadAll();

                var ov = ScriptableObject.CreateInstance<AssistTuningOverride>();
                AssistTuning.Override = ov;
                var a = ReadAll();
                Object.DestroyImmediate(ov);

                for (int i = 0; i < d.Length; i++)
                    Near($"AssistTuning.{Names[i]}", d[i], a[i]);
            }
            finally { AssistTuning.Override = prev; }
        }

        private static readonly string[] Names =
        {
            "SteerLimitRefSpeed", "SteerLimitMinSpeed", "CounterSteerMinLongSpeed",
            "CounterSteerGain", "CounterSteerClamp", "StabilityGain", "StabilityTorqueClamp",
            "TractionOnset", "TractionBand", "AbsOnset", "AbsBand", "LaunchSlipTarget",
            "LaunchGain", "LaunchFloor", "LaunchEngageSpeed", "LaunchReleaseSpeed",
            "LaunchReleaseRate",
            // The four ramps, sampled HALFWAY between anchor and 1. Below the
            // anchor a ramp is the identity on its base and would catch a moved
            // base but nothing else; AT 1 it returns its top-end literal and
            // would catch nothing at all. Halfway is the only sample where both
            // ends are in the answer.
            "StabilityClamp(0.85)", "SteerLimitRef(0.90)",
            "TractionOnsetFor(0.95)", "AbsOnsetFor(0.95)",
        };

        private static float[] ReadAll() => new[]
        {
            AssistTuning.SteerLimitRefSpeed, AssistTuning.SteerLimitMinSpeed,
            AssistTuning.CounterSteerMinLongSpeed, AssistTuning.CounterSteerGain,
            AssistTuning.CounterSteerClamp, AssistTuning.StabilityGain,
            AssistTuning.StabilityTorqueClamp, AssistTuning.TractionOnset,
            AssistTuning.TractionBand, AssistTuning.AbsOnset, AssistTuning.AbsBand,
            AssistTuning.LaunchSlipTarget, AssistTuning.LaunchGain, AssistTuning.LaunchFloor,
            AssistTuning.LaunchEngageSpeed, AssistTuning.LaunchReleaseSpeed,
            AssistTuning.LaunchReleaseRate,
            AssistTuning.StabilityClamp(0.85f), AssistTuning.SteerLimitRef(0.90f),
            AssistTuning.TractionOnsetFor(0.95f), AssistTuning.AbsOnsetFor(0.95f),
        };

        /// <summary>
        /// Every <see cref="ModeConfig"/> accessor must read the same with a
        /// fresh <see cref="ModeConfigOverride"/> installed as with none.
        /// </summary>
        private static void CheckModeDefaults()
        {
            var prev = ModeConfig.Override;
            try
            {
                var ov = ScriptableObject.CreateInstance<ModeConfigOverride>();
                CheckMirror("ModeConfig", typeof(ModeConfig), ov,
                            a => ModeConfig.Override = (ModeConfigOverride)a);
                Object.DestroyImmediate(ov);

                // The two knobs with no literal behind them: both are scales,
                // and both are documented as writing nothing at all at 1. A
                // default of anything else would silently retune every arena.
                Near("ModeConfig.BallGravityScale default is 1", 1f, ModeConfig.BallGravityScale);
                Near("ModeConfig.ArenaGravityScale default is 1", 1f, ModeConfig.ArenaGravityScale);
            }
            finally { ModeConfig.Override = prev; }
        }

        /// <summary>
        /// The same for the arcade layer's tunables. Roughly seventy numbers,
        /// which is exactly why this is written by reflection rather than as a
        /// list somebody has to remember to extend.
        /// </summary>
        private static void CheckArcadeDefaults()
        {
            var prev = ArcadeConfig.Override;
            try
            {
                var ov = ScriptableObject.CreateInstance<ArcadeConfigOverride>();
                CheckMirror("ArcadeConfig", typeof(ArcadeConfig), ov,
                            a => ArcadeConfig.Override = (ArcadeConfigOverride)a);
                Object.DestroyImmediate(ov);
            }
            finally { ArcadeConfig.Override = prev; }
        }

        /// <summary>
        /// Read every accessor twice — with no asset, then with a fresh one —
        /// and require the two answers to be identical.
        ///
        /// <b>Driven from the ASSET's fields, not from a written-out list.</b>
        /// Each public field on the override must have a static property of the
        /// matching PascalCase name on the static class, and a field with no
        /// such property is a FAILURE rather than a skip — otherwise the one
        /// mistake this is here to catch (a knob added to the asset and wired to
        /// nothing, or wired to a differently-named accessor) would be the one
        /// it cannot see. Adding a knob therefore adds its own check.
        /// </summary>
        private static void CheckMirror(string label, System.Type statics,
                                        ScriptableObject asset,
                                        System.Action<ScriptableObject> install)
        {
            const System.Reflection.BindingFlags Fields =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
            const System.Reflection.BindingFlags Props =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

            foreach (var f in asset.GetType().GetFields(Fields))
            {
                string propName = char.ToUpperInvariant(f.Name[0]) + f.Name.Substring(1);
                var p = statics.GetProperty(propName, Props);
                _checks++;
                if (p == null)
                {
                    Fails.Add($"{label}.{propName}: the asset has a '{f.Name}' field but the "
                              + "static class has no accessor of that name — the value is "
                              + "authored and then ignored");
                    continue;
                }

                install(null);
                object shipped = p.GetValue(null);
                install(asset);
                object fromAsset = p.GetValue(null);
                install(null);

                // Boxed Equals rather than a float compare, so this also covers
                // AssistSettings, whose five channels must all match.
                if (!Equals(shipped, fromAsset))
                    Fails.Add($"{label}.{propName}: asset says {fromAsset}, code says {shipped}");
            }
        }

        /// <summary>
        /// The two-rates warning must fire for two objects and stay silent for
        /// one changing its mind.
        ///
        /// Both halves are needed and the second is the one that matters: a
        /// runner reconfigures itself at two different rates on EVERY rig in the
        /// project (Awake on the component defaults, Start on the builder's), so
        /// a check that only looked at the rate fired twenty times in a clean
        /// [PHYS] run. A diagnostic that cries wolf on every correct scene is
        /// worse than none, and this is what stops it regressing to that.
        ///
        /// The third half is the menu's shape, and it is the one that actually
        /// went wrong in the field: FOUR rigs, Unity running every Awake before
        /// any Start, all four settling on the same rate. Per-object bookkeeping
        /// alone does not survive that — the last rig's default is still the
        /// recorded owner when the first rig's real rate arrives — so the
        /// provisional flag is what this pins.
        ///
        /// Also the only place the warning is exercised at all: a genuine
        /// two-runner conflict needs a scene built to have one, and the
        /// production paths all take care not to.
        /// </summary>
        private static void CheckRateAuthority()
        {
            float dt = Time.fixedDeltaTime;
            var a = ScriptableObject.CreateInstance<LevelSettings>();   // stand-in requesters:
            var b = ScriptableObject.CreateInstance<LevelSettings>();   // any two distinct Objects
            var c = ScriptableObject.CreateInstance<LevelSettings>();
            var d = ScriptableObject.CreateInstance<LevelSettings>();
            a.name = "RunnerA";
            b.name = "RunnerB";
            c.name = "RunnerC";
            d.name = "RunnerD";

            int warnings = 0;
            void OnLog(string msg, string _, LogType t)
            {
                if (t == LogType.Warning && msg.StartsWith("[RATE]")) warnings++;
            }

            Application.logMessageReceived += OnLog;
            try
            {
                PhysicsRateAuthority.Reset();
                PhysicsRateAuthority.Apply(500, a);
                PhysicsRateAuthority.Apply(400, a);
                Eq("[RATE] silent when one object changes its own rate", 0, warnings);

                PhysicsRateAuthority.Reset();
                warnings = 0;
                PhysicsRateAuthority.Apply(400, a);
                PhysicsRateAuthority.Apply(500, b);
                Eq("[RATE] warns when two objects disagree", 1, warnings);

                // Once, not once per step: this runs inside FixedUpdate's caller
                // on some paths and a per-frame warning would bury the log.
                PhysicsRateAuthority.Apply(400, a);
                PhysicsRateAuthority.Apply(500, b);
                Eq("[RATE] warns only once per session", 1, warnings);

                // MenuAttract's four bot rigs: every Awake reports the component
                // default 500, then every Start reports the builder's 400. Nobody
                // disagrees about anything, so nothing may be logged — and the
                // latch above is why it matters, since one false positive here
                // silences every real conflict for the rest of the process.
                PhysicsRateAuthority.Reset();
                warnings = 0;
                var rigs = new Object[] { a, b, c, d };
                foreach (var r in rigs) PhysicsRateAuthority.Apply(500, r, provisional: true);
                foreach (var r in rigs) PhysicsRateAuthority.Apply(400, r);
                Eq("[RATE] silent when four rigs settle on one rate", 0, warnings);
                // Approximate, unlike the 1/500 assert below: Unity hands
                // fixedDeltaTime back quantized (1/400 reads as 0.002499993), so an
                // exact compare here is testing Unity's storage, not the authority.
                _checks++;
                if (Mathf.Abs(Time.fixedDeltaTime - 1f / 400) > 1e-6f)
                    Fails.Add("[RATE] four settled rigs left fixedDeltaTime at "
                              + $"{Time.fixedDeltaTime}, expected {1f / 400}");

                // A provisional pass still writes the step — it is Awake, and the
                // first FixedUpdate must not run on the previous scene's rate.
                PhysicsRateAuthority.Reset();
                warnings = 0;
                PhysicsRateAuthority.Apply(200, a, provisional: true);
                _checks++;
                if (Mathf.Abs(Time.fixedDeltaTime - 1f / 200) > 1e-6f)
                    Fails.Add("[RATE] a provisional request did not apply the step: "
                              + $"fixedDeltaTime is {Time.fixedDeltaTime}, expected {1f / 200}");

                // ...and a real conflict must still be caught after one.
                PhysicsRateAuthority.Apply(400, a);
                PhysicsRateAuthority.Apply(500, b);
                Eq("[RATE] still warns after a provisional pass", 1, warnings);

                PhysicsRateAuthority.Reset();
                PhysicsRateAuthority.Apply(500, a);

                _checks++;
                if (Time.fixedDeltaTime != 1f / 500)
                    Fails.Add($"[RATE] did not apply the requested step: fixedDeltaTime is "
                              + $"{Time.fixedDeltaTime}, expected {1f / 500}");
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                PhysicsRateAuthority.Reset();
                Time.fixedDeltaTime = dt;
                Object.DestroyImmediate(a);
                Object.DestroyImmediate(b);
                Object.DestroyImmediate(c);
                Object.DestroyImmediate(d);
            }
        }

        // ---- SessionConfig snapshot/restore ---------------------------------
        // The validator writes to a static the whole editor session shares, so
        // it puts it back. Not paranoia: leaving TargetLaps set would change
        // what pressing Play does next, from a tool that claims to only look.

        private struct Rules
        {
            public MatchMode match;
            public int laps, score, timeLimit, countdown, resultsWait;
            public bool rubberBand, arcade, trackLimits, arcadeHandling;
        }

        private static Rules Capture() => new Rules
        {
            match = SessionConfig.Match,
            laps = SessionConfig.TargetLaps,
            score = SessionConfig.TargetScore,
            timeLimit = SessionConfig.TimeLimitSec,
            countdown = SessionConfig.CountdownSeconds,
            resultsWait = SessionConfig.ResultsWaitSeconds,
            rubberBand = SessionConfig.RubberBand,
            arcade = SessionConfig.Arcade,
            trackLimits = SessionConfig.TrackLimits,
            arcadeHandling = SessionConfig.ArcadeHandling,
        };

        private static (Rules rules, SessionMode mode, bool champ) Snapshot() =>
            (Capture(), SessionConfig.Mode, SessionConfig.ChampionshipRound);

        private static void Restore((Rules rules, SessionMode mode, bool champ) s)
        {
            var r = s.rules;
            SessionConfig.Match = r.match;
            SessionConfig.TargetLaps = r.laps;
            SessionConfig.TargetScore = r.score;
            SessionConfig.TimeLimitSec = r.timeLimit;
            SessionConfig.CountdownSeconds = r.countdown;
            SessionConfig.ResultsWaitSeconds = r.resultsWait;
            SessionConfig.RubberBand = r.rubberBand;
            SessionConfig.Arcade = r.arcade;
            SessionConfig.TrackLimits = r.trackLimits;
            SessionConfig.ArcadeHandling = r.arcadeHandling;
            SessionConfig.Mode = s.mode;
            SessionConfig.ChampionshipRound = s.champ;
        }
    }
}
