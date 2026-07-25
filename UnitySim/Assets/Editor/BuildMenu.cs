using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// One-click Windows development build — the one-PC LAN test path: run the
    /// build as one player and the editor as the other (discovery or 127.0.0.1).
    /// Uses whatever scenes are registered in Build Settings (the Create-Scene
    /// menu items register them).
    /// </summary>
    public static class BuildMenu
    {
        [MenuItem("Tools/AIHWSim/Build Standalone (Dev)")]
        public static void BuildDev()
        {
            var scenes = EditorBuildSettings.scenes;
            if (scenes.Length == 0)
            {
                EditorUtility.DisplayDialog("AIHWSim",
                    "No scenes in Build Settings. Run the Tools ▸ AIHWSim ▸ Create ... Scene items first.", "OK");
                return;
            }

            const string path = "Builds/Dev/AIHWSim.exe";
            var report = BuildPipeline.BuildPlayer(scenes, path,
                BuildTarget.StandaloneWindows64, BuildOptions.Development);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorUtility.RevealInFinder(path);
            else
                Debug.LogError($"[BuildMenu] Build failed: {report.summary.result}");
        }

        /// <summary>
        /// Shareable release build (no Development watermark), output to
        /// Builds/Release/. The menu scene is forced to build index 0 so the exe
        /// boots into the main menu, regardless of Build Settings order. Also
        /// callable headless: Unity.exe -batchmode -quit -executeMethod
        /// AIHWSim.EditorTools.BuildMenu.BuildRelease.
        /// </summary>
        [MenuItem("Tools/AIHWSim/Build Standalone (Release)")]
        public static void BuildRelease()
        {
            var scenePaths = OrderedScenePaths();
            if (scenePaths.Length == 0)
            {
                if (!Application.isBatchMode)
                    EditorUtility.DisplayDialog("AIHWSim",
                        "No scenes in Build Settings. Run the Tools ▸ AIHWSim ▸ Create ... Scene items first.", "OK");
                Debug.LogError("[BuildMenu] No scenes registered — aborting release build.");
                return;
            }

            const string path = "Builds/Release/AI Hardware Control Sim.exe";
            var report = BuildPipeline.BuildPlayer(scenePaths, path,
                BuildTarget.StandaloneWindows64, BuildOptions.None);

            if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.Log($"[BuildMenu] Release build succeeded: {System.IO.Path.GetFullPath(path)}");
                if (!Application.isBatchMode) EditorUtility.RevealInFinder(path);
            }
            else
            {
                Debug.LogError($"[BuildMenu] Release build failed: {report.summary.result}");
            }
        }

        /// <summary>Enabled build scenes with MenuScene moved to index 0 (boot scene).</summary>
        private static string[] OrderedScenePaths()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var s in EditorBuildSettings.scenes)
                if (s.enabled) list.Add(s.path);

            int menuIdx = list.FindIndex(p =>
                System.IO.Path.GetFileNameWithoutExtension(p) == "MenuScene");
            if (menuIdx > 0)
            {
                string menu = list[menuIdx];
                list.RemoveAt(menuIdx);
                list.Insert(0, menu);
            }
            return list.ToArray();
        }
    }
}
