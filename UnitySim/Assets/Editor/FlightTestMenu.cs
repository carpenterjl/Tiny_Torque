using System;
using System.IO;
using AIHWSim.Core.PhysicsTests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// The flight test scenes, authored the same four-line way
    /// <see cref="PhysicsTestMenu"/> authors the car ones: new scene, one
    /// GameObject, one component, save. Everything else — airfield, aircraft,
    /// cameras, telemetry — is built by <see cref="FlightTest"/> at runtime, so the
    /// scene asset holds nothing that could drift from the code.
    ///
    /// <b>A separate array from the car suite, on purpose.</b> Appending these to
    /// <c>PhysicsTestMenu.All</c> would change the
    /// <c>[PHYS] RESULT ALL PASS (10 tests × 2 runs)</c> line that a build gate
    /// greps, even on a run where nothing regressed — and a new, un-baselined test
    /// going non-deterministic on its first day would turn the CAR gate red with
    /// the two facts inseparable in the log.
    /// </summary>
    public static class FlightTestMenu
    {
        private const string Root = "Tools/AIHWSim/Flight Tests/";
        private const string SceneDir = "Assets/Scenes/FlightTests";

        public readonly struct Entry
        {
            public readonly string Id;
            public readonly string Label;
            public readonly Type Component;

            public Entry(string id, string label, Type component)
            {
                Id = id;
                Label = label;
                Component = component;
            }

            public string ScenePath => $"{SceneDir}/FlightTest_{Id}.unity";
        }

        /// <summary>
        /// Reading order: prove the harness, then the propulsion alone, then the
        /// things that need aerodynamics — and among those, the parameter-free ones
        /// before the ones that lean on a coefficient.
        /// </summary>
        public static readonly Entry[] All =
        {
            new Entry("A0", "A0  Ballistic drop (aero off)", typeof(A0BallisticTest)),
            new Entry("A1", "A1  Static thrust",             typeof(A1StaticThrustTest)),
            new Entry("A2", "A2  Level turn load factor",    typeof(A2LevelTurnTest)),
            new Entry("A3", "A3  Stall speed and progression", typeof(A3StallTest)),
            new Entry("A4", "A4  Glide ratio",               typeof(A4GlideTest)),
            new Entry("A5", "A5  Phugoid period",            typeof(A5PhugoidTest)),
            new Entry("A6", "A6  Panel-count convergence",   typeof(A6PanelConvergenceTest)),
            new Entry("A7", "A7  Timestep sensitivity",      typeof(A7TimestepTest)),
        };

        public static Entry? Find(string id)
        {
            foreach (var e in All)
                if (string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        [MenuItem(Root + "Create All Flight Test Scenes", priority = 100)]
        public static void CreateAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(SceneDir);
            foreach (var e in All) CreateScene(e);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Flight test scenes",
                $"Created {All.Length} scenes in {SceneDir}.\n\n"
                + "NOTE: a test's public inspector values are saved INTO its scene. "
                + "Changing a C# default afterwards will NOT change an existing "
                + "scene — re-run this to pick the new defaults up.",
                "OK");
        }

        private static void CreateScene(Entry e)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);
            var go = new GameObject($"FlightTest_{e.Id}");
            go.AddComponent(e.Component);
            Selection.activeGameObject = go;
            EditorSceneManager.SaveScene(scene, e.ScenePath);
        }

        public static void Open(string id)
        {
            var entry = Find(id);
            if (entry == null) return;
            if (!File.Exists(entry.Value.ScenePath)) CreateAll();
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(entry.Value.ScenePath, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        [MenuItem(Root + "A0  Ballistic drop", priority = 200)] private static void O0() => Open("A0");
        [MenuItem(Root + "A1  Static thrust", priority = 201)] private static void O1() => Open("A1");
        [MenuItem(Root + "A2  Level turn", priority = 202)] private static void O2() => Open("A2");
        [MenuItem(Root + "A3  Stall", priority = 203)] private static void O3() => Open("A3");
        [MenuItem(Root + "A4  Glide ratio", priority = 204)] private static void O4() => Open("A4");
        [MenuItem(Root + "A5  Phugoid", priority = 205)] private static void O5() => Open("A5");
        [MenuItem(Root + "A6  Panel convergence", priority = 206)] private static void O6() => Open("A6");
        [MenuItem(Root + "A7  Timestep", priority = 207)] private static void O7() => Open("A7");
    }
}
