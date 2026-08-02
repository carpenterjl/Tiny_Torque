using System;
using System.IO;
using System.Text;
using AIHWSim.Core.PhysicsTests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[AERO] — the flight gate.</b> Walks every scene in
    /// <see cref="FlightTestMenu.All"/> in ONE editor session, runs each test
    /// TWICE, and emits the block a build gate greps.
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.FlightValidationRunner.RunHeadless \
    ///   -logFile &lt;log&gt; -physSuite [-physResultDir &lt;dir&gt;]
    /// </code>
    ///
    /// <b>This is a deliberate COPY of <see cref="PhysicsValidationRunner"/>, not a
    /// shared base.</b> Sharing the stepper would mean editing that file — and the
    /// whole point of the milestone this was written for is that the physics gate,
    /// which ten byte-compared results depend on, changes by zero lines. Extracting
    /// the common stepper is a legitimate follow-up, as its own independently
    /// provable refactor.
    ///
    /// <b>Two things in the copy MUST differ, and both are silent if missed.</b>
    /// The <c>SessionState</c> keys are <c>AIHWSim.AeroVal.*</c> rather than
    /// <c>PhysVal.*</c> — sharing them lets a half-finished run of one gate stomp
    /// the other, and because SessionState survives domain reloads but not editor
    /// restarts, that presents as intermittent flakiness in the PHYSICS gate rather
    /// than here. And because both runners hook <c>playModeStateChanged</c> from
    /// <c>[InitializeOnLoadMethod]</c>, both handlers fire on every play-mode
    /// change, so each must early-out on its own Active flag.
    /// </summary>
    public static class FlightValidationRunner
    {
        private const string ActiveKey = "AIHWSim.AeroVal.Active";
        private const string IndexKey = "AIHWSim.AeroVal.Index";
        private const string ResultKey = "AIHWSim.AeroVal.Result.";

        /// <summary>Two identical runs may differ by this fraction before a
        /// PASS-kind test is called non-deterministic.</summary>
        private const double SpreadTolerance = 0.001;   // 0.1 %

        /// <summary>
        /// Which tests this run covers. Normally all of them; <c>-aeroOnly A3,A6</c>
        /// narrows it while a single row is being worked on, because a full pass is
        /// twenty minutes and iterating on one test should not cost that.
        ///
        /// <b>A narrowed run is not the gate</b>, and the summary line says so
        /// explicitly rather than leaving a shorter "ALL PASS" to be mistaken for the
        /// real thing. Nothing may be signed off on a subset.
        /// </summary>
        private static FlightTestMenu.Entry[] Selected
        {
            get
            {
                string filter = Arg("-aeroOnly");
                if (string.IsNullOrEmpty(filter)) return FlightTestMenu.All;

                var ids = new System.Collections.Generic.HashSet<string>(
                    filter.Split(','), StringComparer.OrdinalIgnoreCase);
                var picked = new System.Collections.Generic.List<FlightTestMenu.Entry>();
                foreach (var e in FlightTestMenu.All)
                    if (ids.Contains(e.Id)) picked.Add(e);
                return picked.ToArray();
            }
        }

        private static bool IsSubset => Selected.Length != FlightTestMenu.All.Length;

        private static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        private static int TotalSteps => Selected.Length * 2;
        private static FlightTestMenu.Entry EntryAt(int step) => Selected[step / 2];
        private static int PassAt(int step) => (step % 2) + 1;

        [MenuItem("Tools/AIHWSim/Flight Tests/Run Full [AERO] Validation", priority = 110)]
        public static void RunFromMenu() => Begin();

        public static void RunHeadless()
        {
            if (!PhysicsTest.SuiteMode)
            {
                Debug.LogError("[AERO] -physSuite is required, or each test exits the "
                               + "editor and only the first scene runs");
                EditorApplication.Exit(3);
                return;
            }
            Begin();
        }

        private static void Begin()
        {
            var selected = Selected;
            if (selected.Length == 0)
            {
                Debug.LogError("[AERO] -aeroOnly matched no tests");
                EditorApplication.Exit(3);
                return;
            }

            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(IndexKey, 0);
            foreach (var e in selected)
                for (int p = 1; p <= 2; p++)
                    SessionState.EraseString(ResultKey + e.Id + "." + p);

            Debug.Log($"[AERO] validation start — {selected.Length} tests × 2 runs"
                      + (IsSubset ? "  (SUBSET — not the gate)" : ""));
            StepInto(0);
        }

        [InitializeOnLoadMethod]
        private static void Hook() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            // Our own flag: the physics runner's handler is live in the same
            // session and must not be confused with this one.
            if (!SessionState.GetBool(ActiveKey, false)) return;

            int step = SessionState.GetInt(IndexKey, 0);
            Collect(step);

            int next = step + 1;
            SessionState.SetInt(IndexKey, next);
            if (next < TotalSteps) EditorApplication.delayCall += () => StepInto(next);
            else EditorApplication.delayCall += Report;
        }

        private static void StepInto(int step)
        {
            var entry = EntryAt(step);
            int pass = PassAt(step);

            string scene = ResolveScene(entry);
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError($"[AERO] scene for '{entry.Id}' not found — run "
                               + "Tools/AIHWSim/Flight Tests/Create All Flight Test Scenes");
                Finish(3);
                return;
            }

            // Clear the stale result first, so a scene that crashes before writing
            // cannot be collected as the previous pass's answer.
            string outPath = PhysicsTest.ResultPathFor("aero", entry.Id);
            if (File.Exists(outPath)) File.Delete(outPath);

            Debug.Log($"[AERO] {entry.Id} run {pass}/2");
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static string ResolveScene(FlightTestMenu.Entry entry)
        {
            if (File.Exists(entry.ScenePath)) return entry.ScenePath;
            foreach (string guid in AssetDatabase.FindAssets($"t:Scene FlightTest_{entry.Id}"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith($"FlightTest_{entry.Id}.unity", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        private static void Collect(int step)
        {
            var entry = EntryAt(step);
            int pass = PassAt(step);
            string path = PhysicsTest.ResultPathFor("aero", entry.Id);
            string json = File.Exists(path) ? File.ReadAllText(path) : "";
            SessionState.SetString(ResultKey + entry.Id + "." + pass, json);
        }

        [Serializable]
        private class Result
        {
            public string testId, title, expected, kind, units, detail;
            public double value;
        }

        private static Result Load(string id, int pass)
        {
            string json = SessionState.GetString(ResultKey + id + "." + pass, "");
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<Result>(json); }
            catch { return null; }
        }

        private static void Report()
        {
            var sb = new StringBuilder();
            int failed = 0, missing = 0, unstable = 0;

            var selected = Selected;
            foreach (var e in selected)
            {
                Result a = Load(e.Id, 1), b = Load(e.Id, 2);
                if (a == null || b == null)
                {
                    Debug.LogError($"[AERO] {e.Id} MISSING — no result from "
                                   + (a == null ? "run 1" : "run 2"));
                    missing++;
                    continue;
                }

                double spread = 0.0;
                if (a.value != b.value)
                {
                    double scale = Math.Max(Math.Abs(a.value), Math.Abs(b.value));
                    spread = scale > 0.0 ? Math.Abs(a.value - b.value) / scale : 0.0;
                }

                bool gates = string.Equals(a.kind, "Pass", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a.kind, "Fail", StringComparison.OrdinalIgnoreCase);

                // INVALID counts as a failure here, and that is a deliberate
                // difference from a plain Fail. Invalid means the run produced NO
                // measurement — it timed out, or it never reached the condition the
                // verdict is defined at. A gate that reports ALL PASS when two of
                // its five tests measured nothing is worse than one that goes red:
                // it certifies silence.
                bool isInvalid = string.Equals(a.kind, "Invalid", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(b.kind, "Invalid", StringComparison.OrdinalIgnoreCase);
                bool isFail = isInvalid
                           || string.Equals(a.kind, "Fail", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(b.kind, "Fail", StringComparison.OrdinalIgnoreCase);
                bool isUnstable = gates && spread > SpreadTolerance;

                if (isFail) failed++;
                if (isUnstable) unstable++;

                string line = $"[AERO] {e.Id} {a.kind.ToUpperInvariant()} "
                              + $"{a.value:0.########} {a.units} (expect {a.expected}) "
                              + $"· run2 {b.value:0.########} · spread {spread * 100.0:0.####} %"
                              + (isUnstable ? "  ← NON-DETERMINISTIC" : "")
                              + (string.IsNullOrEmpty(a.detail) ? "" : $" — {a.detail}");
                if (isFail || isUnstable) Debug.LogError(line); else Debug.Log(line);
                sb.AppendLine(line);
            }

            int bad = failed + missing + unstable;
            // A subset never prints "ALL PASS". The phrase is what a build gate
            // greps for, and letting three tests out of eight produce it would be a
            // green light nobody earned.
            string scope = IsSubset
                ? $"SUBSET {selected.Length}/{FlightTestMenu.All.Length} tests × 2 runs "
                  + "— NOT THE GATE"
                : $"{selected.Length} tests × 2 runs";
            string summary = bad == 0
                ? (IsSubset ? $"[AERO] RESULT subset clean ({scope})"
                            : $"[AERO] RESULT ALL PASS ({scope})")
                : $"[AERO] RESULT {failed} FAILED/INVALID · {unstable} NON-DETERMINISTIC "
                  + $"· {missing} MISSING ({scope})";
            if (bad == 0) Debug.Log(summary); else Debug.LogError(summary);

            Finish(bad == 0 ? 0 : 1);
        }

        private static void Finish(int code)
        {
            SessionState.SetBool(ActiveKey, false);
            if (Application.isBatchMode)
                EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }
    }
}
