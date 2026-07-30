using AIHWSim.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// One-click creation of a playable scene: a fresh scene containing a single
    /// SimBootstrap object, which builds everything else at Play time.
    /// </summary>
    public static class SceneBuilderMenu
    {
        [MenuItem("Tools/AIHWSim/Create Bootstrap Scene")]
        public static void CreateBootstrapScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("SimBootstrap");
            go.AddComponent<SimBootstrap>();
            Selection.activeGameObject = go;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            const string path = "Assets/Scenes/SimMain.unity";
            EditorSceneManager.SaveScene(scene, path);
            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {path}.\nPress Play to run the simulation.", "OK");
        }

        [MenuItem("Tools/AIHWSim/Create Track Scene")]
        public static void CreateTrackScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("TrackBootstrap");
            go.AddComponent<TrackBootstrap>();
            Selection.activeGameObject = go;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            const string path = "Assets/Scenes/TrackScene.unity";
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuild(path);
            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {path}.\nPress Play, then drive with WASD/gamepad. Press M for Autonomous.", "OK");
        }

        [MenuItem("Tools/AIHWSim/Create Garage Scene")]
        public static void CreateGarageScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("GarageBootstrap");
            go.AddComponent<AIHWSim.Garage.GarageBootstrap>();
            Selection.activeGameObject = go;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            const string path = "Assets/Scenes/GarageScene.unity";
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuild(path);
            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {path}.\nAssemble a vehicle, then press Drive to test it on the track.\n" +
                "Tip: also create the Track Scene so Drive/Garage can switch between them.", "OK");
        }

        [MenuItem("Tools/AIHWSim/Create Menu Scene")]
        public static void CreateMenuScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("MenuBootstrap");
            go.AddComponent<AIHWSim.Menu.MenuBootstrap>();
            Selection.activeGameObject = go;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            const string path = "Assets/Scenes/MenuScene.unity";
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuild(path);
            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {path}.\nThis is the game's entry point — Single Player, Multiplayer, Options.\n" +
                "Tip: drag it to the top of Build Settings so builds boot into the menu.", "OK");
        }

        [MenuItem("Tools/AIHWSim/Create Track Builder Scene")]
        public static void CreateTrackBuilderScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var go = new GameObject("TrackBuilderBootstrap");
            go.AddComponent<AIHWSim.TrackEd.TrackBuilderBootstrap>();
            Selection.activeGameObject = go;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            const string path = "Assets/Scenes/TrackBuilderScene.unity";
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuild(path);
            EditorUtility.DisplayDialog("AIHWSim",
                $"Created {path}.\nPaint floor tiles, place walls/obstacles, then press Drive.\n" +
                "Tip: also create the Track Scene so Drive can load your map.", "OK");
        }

        [MenuItem("Tools/AIHWSim/Create Map Debug Scene")]
        public static void CreateMapDebugScene()
        {
            const string path = "Assets/TinyTorqueAssets/Scenes/TTA_Sandbox.unity";

            // This writes a BLANK scene to a fixed path. Every other menu item here
            // creates a scene that is empty by design, so overwriting is harmless —
            // but TTA_Sandbox is hand-authored (terrains, painted alphamaps, baked
            // lighting, spline roads), and a stray click would silently destroy all
            // of it with nothing but Ctrl+Z between you and the loss.
            if (System.IO.File.Exists(path) &&
                !EditorUtility.DisplayDialog("AIHWSim",
                    $"{path} already exists and will be REPLACED with an empty scene.\n\n" +
                    "Any terrain, painted surfaces, baked lighting and authored geometry " +
                    "in it will be lost.",
                    "Replace it", "Cancel"))
                return;

            // 1. Create a blank scene with default lights and camera
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.DefaultGameObjects,
                NewSceneMode.Single);

            // 2. Add your scene's specific bootstrap object (optional)
            var go = new GameObject("TrackBootstrap");
            // Note: If you don't have a specific script for this scene yet, 
            // you can comment out or remove the AddComponent line below:
            // go.AddComponent<AIHWSim.MyNamespace.MyNewSceneBootstrap>(); 
            Selection.activeGameObject = go;

            // 3. Ensure the folder structure exists
            if (!AssetDatabase.IsValidFolder("Assets/TinyTorqueAssets/Scenes"))
                AssetDatabase.CreateFolder("Assets/TinyTorqueAssets", "Scenes");

            // 4. Save the file and automatically inject it into Build Settings
            EditorSceneManager.SaveScene(scene, path);
            AddSceneToBuild(path);

            // 5. Alert the developer that it succeeded
            EditorUtility.DisplayDialog("AIHWSim", $"Created {path} and added it to Build Settings.", "OK");
        }

        /// <summary>
        /// Ensure a scene is registered (and enabled) in Build Settings so
        /// SceneManager.LoadScene(name) works at runtime for the garage↔track flow.
        /// Matching is on the NORMALIZED project-relative path, not the raw string:
        /// an ordinal compare let "TinyTorqueAssets/Scenes/TTA_Sandbox.unity" miss
        /// the already-registered "Assets/TinyTorqueAssets/Scenes/TTA_Sandbox.unity"
        /// and get appended as a second entry — with an all-zero GUID, because the
        /// path resolved to nothing. A zero-GUID row is a dangling reference that
        /// fails the player build, and it sat in the settings unnoticed.
        /// </summary>
        private static void AddSceneToBuild(string path)
        {
            path = Normalize(path);
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            foreach (var s in scenes)
                if (Normalize(s.path) == path)
                {
                    s.enabled = true;
                    EditorBuildSettings.scenes = scenes.ToArray();
                    return;
                }
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>Project-relative, forward-slashed, "Assets/"-prefixed.</summary>
        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            path = path.Replace('\\', '/').Trim('/');
            if (!path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                path = "Assets/" + path;
            return path;
        }
    }
}
