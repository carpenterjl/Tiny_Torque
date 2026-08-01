using System;
using System.IO;
using AIHWSim.Core.PhysicsTests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <c>Tools ▸ AIHWSim ▸ Physics Tests</c> — one scene per measurement, opened
    /// from a menu.
    ///
    /// Each test is its own scene rather than a mode of one scene because the
    /// tests need different ground (a 2.4 km straight, a 600 m pad, a 10 % slope)
    /// and because a scene you can open, watch and re-run is the deliverable. The
    /// headless validator opens exactly these scenes, so what the gate measures
    /// is what you watched.
    ///
    /// <b>None of them is registered in Build Settings</b>, following
    /// <see cref="SceneBuilderMenu"/>'s Physics Debug precedent:
    /// <c>EditorSceneManager.OpenScene</c> and <c>AssetDatabase.FindAssets</c>
    /// both reach an unregistered scene, so nothing needs the entry — and staying
    /// out of <c>EditorBuildSettings.asset</c> keeps the Release build unchanged
    /// and clear of the zero-GUID hazard documented there.
    /// </summary>
    public static class PhysicsTestMenu
    {
        private const string Root = "Tools/AIHWSim/Physics Tests/";
        private const string SceneDir = "Assets/Scenes/PhysicsTests";
        private const string AutoPlayKey = "AIHWSim.PhysicsTests.AutoPlay";
        private const string AutoPlayItem = Root + "Auto-Play on Open";

        /// <summary>
        /// The suite, in the order it is meant to be read: the two calibration
        /// tests first (they prove the harness), then the single-subsystem ones,
        /// then the compound ones whose references this model is known to
        /// disagree with.
        /// </summary>
        public readonly struct Entry
        {
            public readonly string Id;
            public readonly string Label;
            public readonly Type Component;
            public Entry(string id, string label, Type component)
            {
                Id = id; Label = label; Component = component;
            }
            public string ScenePath => $"{SceneDir}/PhysTest_{Id}.unity";
        }

        public static readonly Entry[] All =
        {
            new Entry("P0",  "P0  Free-roll residual",          typeof(P0FreeRollTest)),
            new Entry("P2",  "P2  Static weight & ride height",  typeof(P2StaticTest)),
            new Entry("P1",  "P1  Coastdown → Cd·A",             typeof(P1CoastdownTest)),
            new Entry("P3",  "P3  Heave frequency & damping",    typeof(P3HeaveTest)),
            new Entry("P6a", "P6a Threshold braking",            typeof(P6aBrakingTest)),
            new Entry("P6b", "P6b Park hold on 10 % slope",      typeof(P6bParkHoldTest)),
            new Entry("P4",  "P4  Skidpad",                      typeof(P4SkidpadTest)),
            new Entry("P5",  "P5  Understeer gradient",           typeof(P5UndersteerTest)),
            new Entry("P7",  "P7  Roll gradient",                 typeof(P7RollTest)),
            new Entry("P9",  "P9  Timestep sensitivity",          typeof(P9TimestepTest)),
        };

        public static Entry? Find(string id)
        {
            foreach (var e in All)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // ---- scene creation ----

        [MenuItem(Root + "Create All Test Scenes", priority = 100)]
        public static void CreateAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Directory.CreateDirectory(SceneDir);
            foreach (var e in All) CreateScene(e);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {All.Length} physics test scenes in {SceneDir}.\n\n"
                + "Open one from this menu and press Play — it drives itself and "
                + "reports a verdict on screen.\n\n"
                + "Deliberately NOT added to Build Settings.\n\n"
                + "Re-run this after changing a test's default values in code: "
                + "the scene stores whatever the defaults were when it was "
                + "created, and a saved value always wins over a new default.",
                "OK");
        }

        private static void CreateScene(Entry e)
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject($"PhysTest_{e.Id}");
            go.AddComponent(e.Component);
            Selection.activeGameObject = go;

            EditorSceneManager.SaveScene(scene, e.ScenePath);
        }

        // ---- opening ----

        [MenuItem(AutoPlayItem, priority = 120)]
        private static void ToggleAutoPlay()
        {
            bool v = !AutoPlay;
            EditorPrefs.SetBool(AutoPlayKey, v);
            // Fully qualified: this project has its own AIHWSim.Menu namespace,
            // which otherwise wins the lookup inside AIHWSim.EditorTools.
            UnityEditor.Menu.SetChecked(AutoPlayItem, v);
        }

        [MenuItem(AutoPlayItem, validate = true)]
        private static bool ToggleAutoPlayValidate()
        {
            // Fully qualified: this project has its own AIHWSim.Menu namespace,
            // which otherwise wins the lookup inside AIHWSim.EditorTools.
            UnityEditor.Menu.SetChecked(AutoPlayItem, AutoPlay);
            return true;
        }

        /// <summary>Default ON: the tests are auto-driven, so opening one and not
        /// playing it shows an empty scene with a single GameObject in it.</summary>
        private static bool AutoPlay => EditorPrefs.GetBool(AutoPlayKey, true);

        public static void Open(string id)
        {
            var entry = Find(id);
            if (entry == null) { Debug.LogError($"[PHYS] unknown test '{id}'"); return; }
            var e = entry.Value;

            if (!File.Exists(e.ScenePath))
            {
                if (!EditorUtility.DisplayDialog("AIHWSim",
                        $"{e.ScenePath} does not exist yet.\n\nCreate the test scenes now?",
                        "Create", "Cancel"))
                    return;
                CreateAll();
            }

            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorSceneManager.OpenScene(e.ScenePath, OpenSceneMode.Single);
            if (AutoPlay) EditorApplication.EnterPlaymode();
        }

        [MenuItem(Root + "P0  Free-roll residual", priority = 200)]
        private static void OpenP0() => Open("P0");

        [MenuItem(Root + "P2  Static weight and ride height", priority = 201)]
        private static void OpenP2() => Open("P2");

        [MenuItem(Root + "P1  Coastdown to Cd.A", priority = 202)]
        private static void OpenP1() => Open("P1");

        [MenuItem(Root + "P3  Heave frequency and damping", priority = 203)]
        private static void OpenP3() => Open("P3");

        [MenuItem(Root + "P6a Threshold braking", priority = 204)]
        private static void OpenP6a() => Open("P6a");

        [MenuItem(Root + "P6b Park hold on 10 percent slope", priority = 205)]
        private static void OpenP6b() => Open("P6b");

        [MenuItem(Root + "P4  Skidpad", priority = 206)]
        private static void OpenP4() => Open("P4");

        [MenuItem(Root + "P5  Understeer gradient", priority = 207)]
        private static void OpenP5() => Open("P5");

        [MenuItem(Root + "P7  Roll gradient", priority = 208)]
        private static void OpenP7() => Open("P7");

        [MenuItem(Root + "P9  Timestep sensitivity", priority = 209)]
        private static void OpenP9() => Open("P9");
    }
}
