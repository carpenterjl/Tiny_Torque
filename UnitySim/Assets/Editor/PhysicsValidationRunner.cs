using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIHWSim.Core.PhysicsTests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[PHYS] — the physics gate.</b> Walks every scene in
    /// <see cref="PhysicsTestMenu.All"/> in ONE editor session, runs each test
    /// TWICE, and emits the block the build gate greps for.
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.PhysicsValidationRunner.RunHeadless \
    ///   -logFile &lt;log&gt; -physSuite [-physResultDir &lt;dir&gt;]
    /// </code>
    ///
    /// No <c>-quit</c> and no <c>-nographics</c> — play mode keeps the process
    /// alive and this project's cars carry a camera sensor. <c>-physSuite</c> is
    /// what stops each finished test from calling <c>EditorApplication.Exit</c>
    /// (see <see cref="PhysicsTest.SuiteMode"/>); without it the run would end
    /// after the first scene and report a pass on one tenth of the suite.
    ///
    /// <b>It opens the same scenes a human opens from the menu</b> rather than
    /// building an equivalent world, and reads the same result JSON the on-screen
    /// HUD is showing. That is the whole design: there is no second code path for
    /// the gate to disagree with, so "it passed headless but looks wrong on
    /// screen" cannot happen.
    ///
    /// <b>Why twice.</b> Same reasoning as the lap-2/lap-3 check in the racing-line
    /// calibration: a measurement that moves between two identical runs has
    /// something non-deterministic in the loop, and that is worth knowing before
    /// anyone trusts a fifth digit. The spread is reported for every test and
    /// gates only the PASS-kind ones — an INFO row is already declared as not
    /// comparable to anything.
    ///
    /// State has to survive the domain reload that entering play mode triggers, so
    /// the queue lives in <c>SessionState</c> and the sequencing hangs off
    /// <c>playModeStateChanged</c> — the pattern <see cref="CosmeticProbe"/>
    /// established.
    /// </summary>
    public static class PhysicsValidationRunner
    {
        private const string ActiveKey = "AIHWSim.PhysVal.Active";
        private const string IndexKey = "AIHWSim.PhysVal.Index";
        private const string ResultKey = "AIHWSim.PhysVal.Result.";   // + id + "." + pass

        /// <summary>Two identical runs may differ by this fraction before a
        /// PASS-kind test is called non-deterministic.</summary>
        private const double SpreadTolerance = 0.001;   // 0.1 %

        /// <summary>Work list: every test, twice, in menu order. Pass 1 of all ten
        /// then pass 2 of all ten would be equivalent; interleaving them per test
        /// keeps a scene's two runs adjacent in the log, which is what you want
        /// when one of them disagrees.</summary>
        private static int TotalSteps => PhysicsTestMenu.All.Length * 2;

        private static PhysicsTestMenu.Entry EntryAt(int step) =>
            PhysicsTestMenu.All[step / 2];

        private static int PassAt(int step) => (step % 2) + 1;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run Full [PHYS] Validation", priority = 110)]
        public static void RunFromMenu() => Begin();

        public static void RunHeadless()
        {
            if (!PhysicsTest.SuiteMode)
            {
                Debug.LogError("[PHYS] -physSuite is required, or each test exits "
                               + "the editor and only the first scene runs");
                EditorApplication.Exit(3);
                return;
            }
            Begin();
        }

        private static void Begin()
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(IndexKey, 0);
            foreach (var e in PhysicsTestMenu.All)
                for (int p = 1; p <= 2; p++)
                    SessionState.EraseString(ResultKey + e.Id + "." + p);

            Debug.Log($"[PHYS] validation start — {PhysicsTestMenu.All.Length} tests × 2 runs");
            StepInto(0);
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            if (!SessionState.GetBool(ActiveKey, false)) return;

            int step = SessionState.GetInt(IndexKey, 0);
            Collect(step);

            int next = step + 1;
            SessionState.SetInt(IndexKey, next);
            if (next < TotalSteps) EditorApplication.delayCall += () => StepInto(next);
            else EditorApplication.delayCall += Report;
        }

        /// <summary>Open the scene for this step and start it.</summary>
        private static void StepInto(int step)
        {
            var entry = EntryAt(step);
            int pass = PassAt(step);

            string scene = ResolveScene(entry);
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError($"[PHYS] scene for '{entry.Id}' not found — run "
                               + "Tools/AIHWSim/Physics Tests/Create All Test Scenes");
                Finish(3);
                return;
            }

            // Clear the stale result first, so a scene that crashes before writing
            // cannot be collected as the previous pass's answer.
            string outPath = PhysicsTest.ResultPathFor(entry.Id);
            if (File.Exists(outPath)) File.Delete(outPath);

            Debug.Log($"[PHYS] {entry.Id} run {pass}/2");
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static string ResolveScene(PhysicsTestMenu.Entry entry)
        {
            if (File.Exists(entry.ScenePath)) return entry.ScenePath;
            foreach (string guid in AssetDatabase.FindAssets($"t:Scene PhysTest_{entry.Id}"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith($"PhysTest_{entry.Id}.unity", StringComparison.OrdinalIgnoreCase))
                    return p;
            }
            return null;
        }

        /// <summary>Stash this pass's JSON before the next pass overwrites the
        /// file — both runs of a test write to the same path.</summary>
        private static void Collect(int step)
        {
            var entry = EntryAt(step);
            int pass = PassAt(step);
            string path = PhysicsTest.ResultPathFor(entry.Id);
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

            foreach (var e in PhysicsTestMenu.All)
            {
                Result a = Load(e.Id, 1), b = Load(e.Id, 2);
                if (a == null || b == null)
                {
                    Debug.LogError($"[PHYS] {e.Id} MISSING — no result from "
                                   + (a == null ? "run 1" : "run 2"));
                    missing++;
                    continue;
                }

                // Relative spread, with an exact-equality shortcut so two runs that
                // agree bit-for-bit are never called unstable by a division.
                double spread = 0.0;
                if (a.value != b.value)
                {
                    double scale = Math.Max(Math.Abs(a.value), Math.Abs(b.value));
                    spread = scale > 0.0 ? Math.Abs(a.value - b.value) / scale : 0.0;
                }

                bool gates = string.Equals(a.kind, "Pass", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(a.kind, "Fail", StringComparison.OrdinalIgnoreCase);
                bool isFail = string.Equals(a.kind, "Fail", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(b.kind, "Fail", StringComparison.OrdinalIgnoreCase);
                bool isUnstable = gates && spread > SpreadTolerance;

                if (isFail) failed++;
                if (isUnstable) unstable++;

                string line = $"[PHYS] {e.Id} {a.kind.ToUpperInvariant()} "
                              + $"{a.value:0.########} {a.units} (expect {a.expected}) "
                              + $"· run2 {b.value:0.########} · spread {spread * 100.0:0.####} %"
                              + (isUnstable ? "  ← NON-DETERMINISTIC" : "")
                              + (string.IsNullOrEmpty(a.detail) ? "" : $" — {a.detail}");
                if (isFail || isUnstable) Debug.LogError(line); else Debug.Log(line);
                sb.AppendLine(line);
            }

            int bad = failed + missing + unstable;
            string summary = bad == 0
                ? $"[PHYS] RESULT ALL PASS ({PhysicsTestMenu.All.Length} tests × 2 runs)"
                : $"[PHYS] RESULT {failed} FAILED · {unstable} NON-DETERMINISTIC · "
                  + $"{missing} MISSING";
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
