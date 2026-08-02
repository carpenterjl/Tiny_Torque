using System.IO;
using AIHWSim.Core.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Authors the free-flight scene. Its own file rather than a menu item added to
    /// <c>SceneBuilderMenu</c>, which owns the shipped scenes and the
    /// <c>AddSceneToBuild</c> helper — keeping this separate is what leaves that
    /// file, and <c>EditorBuildSettings.asset</c>, at a zero-line diff.
    ///
    /// <b>Not registered in Build Settings</b>, for the three reasons
    /// <c>CreatePhysicsDebugScene</c> already gives: <c>OpenScene</c> and
    /// <c>AssetDatabase.FindAssets</c> both reach an unregistered scene, so nothing
    /// headless needs it; leaving the asset untouched keeps the Release build
    /// byte-identical and clear of the zero-GUID hazard; and
    /// <c>OpusMissionRunner</c> picks its scene by scanning that list, so a new
    /// entry could collide with it.
    ///
    /// The scene is AUTHORED: <see cref="RcPlaneSceneBuilder"/> builds every
    /// GameObject — airfield, aircraft, cameras, runner, HUD — at edit time with
    /// the same code the runtime path uses, so it can all be adjusted in the
    /// inspector before Play. <see cref="RcPlaneBootstrap"/> adopts it through a
    /// <see cref="AIHWSim.Core.Flight.FlightSceneDescriptor"/> at Awake, and
    /// still builds everything from code if the descriptor is missing (the old
    /// one-GameObject scene keeps working).
    /// </summary>
    public static class RcPlaneMenu
    {
        private const string ScenePath = "Assets/Scenes/RcPlaneScene.unity";

        [MenuItem("Tools/AIHWSim/Create RC Plane Scene", priority = 40)]
        public static void CreateScene()
        {
            // The scene now holds hand-adjustable objects, so overwriting it is
            // destructive in a way the one-GameObject version never was.
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog(
                    "Replace RC Plane scene?",
                    ScenePath + " already exists. Rebuilding replaces every object "
                    + "in it — hand edits and inspector changes will be lost.\n\n"
                    + "Rebuilding is also how new C# defaults get picked up: the "
                    + "authored values were baked in when the scene was created.",
                    "Replace it", "Cancel"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets/Scenes");
            RcPlaneSceneBuilder.CreateAuthoredScene(ScenePath);

            EditorUtility.DisplayDialog(
                "RC Plane scene",
                $"Created {ScenePath}. Every object is in the hierarchy — adjust "
                + "in the inspector, then press Play to fly.\n\n"
                + "KSP layout. Keyboard: W/S pitch, A/D yaw, Q/E roll; Shift/Ctrl run "
                + "the throttle, X cuts it, Z is full.\n"
                + "Gamepad: triggers throttle, left stick rolls and pitches, right "
                + "stick yaws, LB/RB cut and full.\n"
                + "[T] toggles SAS, [F] holds it off, 1-6 pick a mode.\n"
                + "[V] cycles ground station / chase / boresight, [R] resets.\n\n"
                + "NOTE: authored values are saved INTO this scene. Changing a C# "
                + "default later will not change them — re-run this menu item (it "
                + "asks first) to pick the new defaults up.",
                "OK");
        }

        [MenuItem("Tools/AIHWSim/Open RC Plane Scene", priority = 41)]
        public static void OpenScene()
        {
            if (!File.Exists(ScenePath)) { CreateScene(); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        // ---- the VTOL jet, on the same airfield -------------------------

        private const string JetScenePath = "Assets/Scenes/RcJetScene.unity";

        [MenuItem("Tools/AIHWSim/Create VTOL Jet Scene", priority = 42)]
        public static void CreateJetScene()
        {
            if (File.Exists(JetScenePath) && !EditorUtility.DisplayDialog(
                    "Replace VTOL Jet scene?",
                    JetScenePath + " already exists. Rebuilding replaces every "
                    + "object in it — hand edits and inspector changes will be "
                    + "lost.\n\nRebuilding is also how new C# defaults get picked "
                    + "up: the authored values were baked in when the scene was "
                    + "created.",
                    "Replace it", "Cancel"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Directory.CreateDirectory(Path.GetDirectoryName(JetScenePath) ?? "Assets/Scenes");
            RcPlaneSceneBuilder.CreateAuthoredScene(
                JetScenePath, RcPlaneBootstrap.Aircraft.HydraVtol);

            EditorUtility.DisplayDialog(
                "VTOL Jet scene",
                $"Created {JetScenePath} — the same airfield, a Harrier-class "
                + "VTOL, and a target range (drones, dodging trucks, statics). "
                + "Everything is in the hierarchy; drag waypoints and drone "
                + "circuits before Play.\n\n"
                + "Spawns HOVERING with SAS holding attitude and the throttle at "
                + "the predicted hover setting.\n"
                + "Num8/Num2 swing the nozzles aft/down (pad: East / D-pad "
                + "down). Everything else flies like the trainer: WASDQE, "
                + "Shift/Ctrl throttle, X cut, Z full, [T] SAS, [V] view, [R] "
                + "reset.",
                "OK");
        }

        [MenuItem("Tools/AIHWSim/Open VTOL Jet Scene", priority = 43)]
        public static void OpenJetScene()
        {
            if (!File.Exists(JetScenePath)) { CreateJetScene(); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
            EditorSceneManager.OpenScene(JetScenePath, OpenSceneMode.Single);
        }
    }
}
