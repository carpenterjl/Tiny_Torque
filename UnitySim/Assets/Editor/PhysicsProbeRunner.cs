using System;
using System.IO;
using System.Linq;
using AIHWSim.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Opens the physics-debug scene headlessly and runs the static probe.
    ///
    /// Resolves the scene with AssetDatabase rather than from Build Settings —
    /// that is what lets PhysicsDebugScene stay OUT of the build, which is the
    /// whole reason it can exist without changing the shipped game.
    ///
    /// Run with (editor closed; NO -quit, play mode keeps the process alive, and
    /// NO -nographics — this project's standing rule):
    ///
    ///   Unity.exe -batchmode -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.PhysicsProbeRunner.RunHeadless
    ///     -logFile &lt;log&gt; -physResult &lt;out.txt&gt;
    /// </summary>
    public static class PhysicsProbeRunner
    {
        [MenuItem("Tools/AIHWSim/Run Physics Static Probe")]
        public static void RunFromMenu() => Begin(DefaultPath());

        public static void RunHeadless() => Begin(ArgValue("-physResult") ?? DefaultPath());

        private static string DefaultPath() =>
            Path.Combine(Path.GetTempPath(), "phys_probe.txt");

        private static void Begin(string outPath)
        {
            string scene = AssetDatabase.FindAssets("t:Scene PhysicsDebugScene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => p.EndsWith("PhysicsDebugScene.unity",
                                                StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError("[PHYS] PhysicsDebugScene not found — run "
                               + "Tools > AIHWSim > Create Physics Debug Scene first.");
                EditorApplication.Exit(3);
                return;
            }

            File.WriteAllText(PhysicsProbeAutorun.RequestPath, outPath);
            if (File.Exists(outPath)) File.Delete(outPath);

            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Debug.Log($"[PHYS] entering play mode; probe -> {outPath}");
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            try
            {
                if (File.Exists(PhysicsProbeAutorun.RequestPath))
                    File.Delete(PhysicsProbeAutorun.RequestPath);
            }
            catch { }
            if (Application.isBatchMode)
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static string ArgValue(string flag)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++)
                if (string.Equals(a[i], flag, StringComparison.OrdinalIgnoreCase))
                    return a[i + 1];
            return null;
        }
    }
}
