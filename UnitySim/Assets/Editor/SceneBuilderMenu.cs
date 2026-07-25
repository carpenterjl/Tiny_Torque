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

        /// <summary>
        /// Ensure a scene is registered (and enabled) in Build Settings so
        /// SceneManager.LoadScene(name) works at runtime for the garage↔track flow.
        /// </summary>
        private static void AddSceneToBuild(string path)
        {
            var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(
                EditorBuildSettings.scenes);
            foreach (var s in scenes)
                if (s.path == path) { s.enabled = true; EditorBuildSettings.scenes = scenes.ToArray(); return; }
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
