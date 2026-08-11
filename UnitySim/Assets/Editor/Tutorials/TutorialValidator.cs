using System.Collections.Generic;
using System.IO;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Track;
using AIHWSim.Tutorial;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[TUT] — the tutorial gate.</b> Holds the claims a tutorial has to
    /// satisfy to be reachable and finishable, and which nothing else can check:
    /// <b>every catalogue row leads somewhere, and every scene can actually be
    /// completed</b>.
    ///
    /// The failure modes here are all silent. A scene missing from Build Settings
    /// is a menu item that fades to black. A step whose condition is
    /// TriggerVolume with no volume assigned, or whose id does not match the
    /// catalogue, is a lesson that either never ends or never pays. A director
    /// with no steps is a car on an empty floor. None of that is a compile error,
    /// and none of it shows in a diff — scene files are the part of this project
    /// that rots quietest, which is why the mode templates have a gate too.
    ///
    /// It re-opens each scene and reads what was SERIALIZED, not what the builder
    /// intended. Hand edits are the expected state of these files, so checking
    /// the builder's inputs would check nothing.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.TutorialValidator.Report -logFile &lt;log&gt;
    /// </code>
    /// </summary>
    public static class TutorialValidator
    {
        private const string Tag = "[TUT]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Tutorials/Validate Tutorials [TUT]", priority = 411)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            CheckCatalog();
            CheckProgressSchema();
            foreach (var row in TutorialCatalog.All)
            {
                if (string.IsNullOrEmpty(row.scene)) CheckOverlay(row);
                else CheckScene(row);
            }

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks over "
                  + $"{TutorialCatalog.All.Length} tutorials)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        // ---- the catalogue --------------------------------------------------

        /// <summary>Ids are the persisted identity of a lesson — they are written
        /// into progress.json and into every scene's director. A duplicate makes
        /// two lessons share a completion mark; an empty one can never be
        /// matched.</summary>
        private static void CheckCatalog()
        {
            var seen = new HashSet<string>();
            var scenes = new HashSet<string>();
            foreach (var row in TutorialCatalog.All)
            {
                _checks += 2;
                if (string.IsNullOrEmpty(row.id)) Fails.Add("catalog: a row has an empty id");
                else if (!seen.Add(row.id)) Fails.Add($"catalog: duplicate id '{row.id}'");

                if (string.IsNullOrEmpty(row.label))
                    Fails.Add($"catalog: '{row.id}' has no label");

                if (string.IsNullOrEmpty(row.scene)) continue;
                _checks++;
                if (!scenes.Add(row.scene))
                    Fails.Add($"catalog: two rows share scene '{row.scene}' — completing "
                              + "one would look like completing the other");
            }

            // The "play all" sequence and the finish-them-all crate both count
            // rows, so an unreachable row would make the crate unearnable.
            _checks++;
            if (TutorialCatalog.All.Length == 0) Fails.Add("catalog: no tutorials at all");
        }

        /// <summary>
        /// A profile written before tutorials existed still loads, and one
        /// written with a sequence in progress still carries it.
        ///
        /// Worth a gate because both halves are silent when they break. The
        /// project's whole migration story is "JsonUtility leaves a field
        /// initializer alone for a key the JSON predates" — true, but only while
        /// the field HAS an initializer, and a null List reaches the menu as a
        /// NullReferenceException on the first draw. The queue matters for the
        /// opposite reason: JsonUtility silently drops what it cannot serialize,
        /// and a queue that does not survive a save is a "play all" run that
        /// forgets everything after the lesson it is on.
        /// </summary>
        private static void CheckProgressSchema()
        {
            // A v2 profile: everything tutorials added is simply absent.
            const string v2 = "{\"version\":2,\"scrap\":40,\"unlocked\":[\"paint_gold\"]}";
            var old = JsonUtility.FromJson<Persistence.PlayerProgress>(v2);

            _checks += 4;
            if (old == null) { Fails.Add("progress: a v2 profile did not parse at all"); return; }
            if (old.scrap != 40) Fails.Add("progress: v2 scrap was lost on load");
            if (old.tutorialsDone == null)
                Fails.Add("progress: tutorialsDone is null on a v2 profile — the hub "
                          + "would throw before it drew");
            if (old.tutorial == null || old.tutorial.queue == null)
                Fails.Add("progress: tutorial state is null on a v2 profile");

            // A v3 profile mid-sequence, round-tripped the way the store does it.
            var mid = new Persistence.PlayerProgress();
            mid.tutorialsDone.Add("arcade");
            mid.tutorial.active = true;
            mid.tutorial.id = "sim_controllers";
            mid.tutorial.stepIndex = 3;
            mid.tutorial.sequenceMode = true;
            mid.tutorial.queue.Add("sim_sensors");
            mid.tutorial.queue.Add("sim_ipc");

            var back = JsonUtility.FromJson<Persistence.PlayerProgress>(JsonUtility.ToJson(mid));

            _checks += 4;
            if (back == null) { Fails.Add("progress: a v3 profile did not round-trip"); return; }
            if (back.tutorial.id != "sim_controllers" || back.tutorial.stepIndex != 3)
                Fails.Add("progress: the resume point did not survive a round trip");
            if (back.tutorial.queue.Count != 2 || back.tutorial.queue[1] != "sim_ipc")
                Fails.Add("progress: the tutorial queue did not survive a round trip — "
                          + "a 'play all' run would forget everything after this lesson");
            if (!back.tutorialsDone.Contains("arcade"))
                Fails.Add("progress: completion marks did not survive a round trip");
            if (back.version != 3)
                Fails.Add($"progress: a fresh profile says version {back.version}, expected 3");
        }

        /// <summary>An overlay row has no scene, so its steps are its only
        /// content. A row with neither is a menu button that does nothing.</summary>
        private static void CheckOverlay(TutorialCatalog.Row row)
        {
            _checks++;
            if (!TutorialScripts.Has(row.id))
                Fails.Add($"{row.id}: no scene AND no step script in TutorialScripts.For — "
                          + "the hub would offer a lesson with nothing in it");
        }

        // ---- one scene ---------------------------------------------------------

        private static void CheckScene(TutorialCatalog.Row row)
        {
            string path = TutorialSceneBuilder.ScenePath(row.scene);

            _checks++;
            if (!File.Exists(path))
            {
                Fails.Add($"{row.scene}: scene file missing — run "
                          + "Tools ▸ AIHWSim ▸ Tutorials ▸ Create Missing Tutorial Scenes");
                return;
            }

            // Registered AND enabled, or SceneManager.LoadScene cannot find it at
            // runtime and the menu item loads a black screen. This is the check
            // SceneTrackCatalog's doc warns about, for the same reason.
            _checks++;
            if (!InBuildSettings(path))
                Fails.Add($"{row.scene}: not enabled in Build Settings — the menu would "
                          + "fade to a scene that cannot be loaded");

            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);

            CheckNoMissingScripts(row);
            CheckOneSun(row);
            CheckComposition(row);
            CheckDirectorAndSteps(row);
        }

        private static bool InBuildSettings(string path)
        {
            string want = path.Replace('\\', '/');
            foreach (var s in EditorBuildSettings.scenes)
                if (s.path.Replace('\\', '/') == want) return s.enabled;
            return false;
        }

        /// <summary>The bones every driving scene needs to compose a session.</summary>
        private static void CheckComposition(TutorialCatalog.Row row)
        {
            var d = Object.FindFirstObjectByType<SceneTrackDescriptor>();
            var boot = Object.FindFirstObjectByType<TrackBootstrap>();

            _checks += 2;
            if (d == null) { Fails.Add($"{row.scene}: no SceneTrackDescriptor"); return; }
            if (boot == null) { Fails.Add($"{row.scene}: no TrackBootstrap"); return; }

            // The oval would be built ON TOP of the authored map — the one
            // setting that silently ruins one of these scenes.
            _checks++;
            if (boot.buildDefaultOval)
                Fails.Add($"{row.scene}: TrackBootstrap still builds the default oval");

            _checks++;
            if (Object.FindFirstObjectByType<TrackSpawnMarker>() == null)
                Fails.Add($"{row.scene}: no TrackSpawnMarker — the car would spawn at the "
                          + "origin, which on most maps is under the floor");
        }

        /// <summary>
        /// The lesson itself: one director, wired to this row, with steps that
        /// can each actually complete.
        /// </summary>
        private static void CheckDirectorAndSteps(TutorialCatalog.Row row)
        {
            var dirs = Object.FindObjectsByType<TutorialDirector>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            _checks++;
            if (dirs.Length != 1)
            {
                Fails.Add($"{row.scene}: {dirs.Length} TutorialDirector(s), expected 1 — "
                          + "two would each draw their own results overlay");
                return;
            }
            var dir = dirs[0];

            // The id is how a finished lesson finds its catalogue row to pay out
            // from. A mismatch runs the lesson and banks nothing.
            _checks++;
            if (dir.tutorialId != row.id)
                Fails.Add($"{row.scene}: director id '{dir.tutorialId}' does not match "
                          + $"catalogue id '{row.id}' — finishing it would pay nothing");

            var steps = new List<TutorialStep>();
            foreach (Transform child in dir.transform)
            {
                var s = child.GetComponent<TutorialStep>();
                if (s != null && child.gameObject.activeSelf && s.enabled) steps.Add(s);
            }

            _checks++;
            if (steps.Count == 0)
            {
                Fails.Add($"{row.scene}: the director has no active TutorialStep children — "
                          + "the lesson would finish the instant it started");
                return;
            }

            for (int i = 0; i < steps.Count; i++) CheckStep(row, i, steps[i]);
        }

        private static void CheckStep(TutorialCatalog.Row row, int i, TutorialStep s)
        {
            string where = $"{row.scene} step {i + 1} ('{s.name}')";

            _checks++;
            if (string.IsNullOrWhiteSpace(s.title) && string.IsNullOrWhiteSpace(s.body))
                Fails.Add($"{where}: no title and no body — an objective panel with "
                          + "nothing in it");

            switch (s.condition)
            {
                case TutorialCondition.TriggerVolume:
                    // The engine treats a null trigger as already satisfied rather
                    // than deadlocking, so this is the gate that has to catch it.
                    _checks++;
                    if (s.trigger == null)
                        Fails.Add($"{where}: TriggerVolume with no volume assigned — "
                                  + "the step would complete immediately");
                    else
                    {
                        _checks++;
                        var col = s.trigger.GetComponent<Collider>();
                        if (col == null || !col.isTrigger)
                            Fails.Add($"{where}: '{s.trigger.name}' is not a trigger "
                                      + "collider — it is a wall the player will hit");
                    }
                    break;

                case TutorialCondition.InputHeld:
                    _checks++;
                    if (s.seconds <= 0f)
                        Fails.Add($"{where}: InputHeld with a hold time of {s.seconds}s");
                    break;

                case TutorialCondition.Timer:
                    _checks++;
                    if (s.seconds <= 0f)
                        Fails.Add($"{where}: Timer with a wait of {s.seconds}s — "
                                  + "the text would flash past unread");
                    break;

                case TutorialCondition.SpeedReached:
                    _checks++;
                    if (s.amount <= 0f)
                        Fails.Add($"{where}: SpeedReached with a target of {s.amount} m/s, "
                                  + "which a parked car already meets");
                    break;

                case TutorialCondition.Signal:
                case TutorialCondition.ScreenReached:
                case TutorialCondition.TelemetryObserved:
                    _checks++;
                    if (string.IsNullOrWhiteSpace(s.token))
                        Fails.Add($"{where}: {s.condition} with no token — nothing can "
                                  + "ever satisfy it and the lesson stops here");
                    break;
            }
        }

        // ---- shared ---------------------------------------------------------------

        private static void CheckNoMissingScripts(TutorialCatalog.Row row)
        {
            int missing = 0;
            foreach (var go in Object.FindObjectsByType<GameObject>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                foreach (var c in go.GetComponents<Component>())
                    if (c == null) missing++;

            _checks++;
            if (missing > 0)
                Fails.Add($"{row.scene}: {missing} Missing Script component(s)");
        }

        private static void CheckOneSun(TutorialCatalog.Row row)
        {
            int suns = 0;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.enabled) suns++;

            _checks++;
            if (suns != 1)
                Fails.Add($"{row.scene}: {suns} enabled directional light(s), expected 1");
        }
    }
}
