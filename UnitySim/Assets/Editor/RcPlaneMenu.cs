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
    /// The scene contains one GameObject with one component. Everything else —
    /// airfield, aircraft, cameras, HUD, telemetry — is built in
    /// <see cref="RcPlaneBootstrap"/>'s Awake, so the whole setup is reviewable as
    /// code rather than as scene YAML.
    /// </summary>
    public static class RcPlaneMenu
    {
        private const string ScenePath = "Assets/Scenes/RcPlaneScene.unity";

        [MenuItem("Tools/AIHWSim/Create RC Plane Scene", priority = 40)]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath) ?? "Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);
            var go = new GameObject("RcPlaneBootstrap");
            go.AddComponent<RcPlaneBootstrap>();
            Selection.activeGameObject = go;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "RC Plane scene",
                $"Created {ScenePath}. Press Play to fly.\n\n"
                + "KSP layout. Keyboard: W/S pitch, A/D yaw, Q/E roll; Shift/Ctrl run "
                + "the throttle, X cuts it, Z is full.\n"
                + "Gamepad: triggers throttle, left stick rolls and pitches, right "
                + "stick yaws, LB/RB cut and full.\n"
                + "[T] toggles SAS, [F] holds it off, 1-6 pick a mode.\n"
                + "[V] cycles ground station / chase / boresight, [R] resets.\n\n"
                + "NOTE: the inspector values on RcPlaneBootstrap are saved INTO this "
                + "scene. Changing a C# default later will not change them — re-run "
                + "this menu item to pick the new defaults up.",
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
    }
}
