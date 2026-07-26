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
    /// Drives one unattended mission run from the command line, so the Opus
    /// firmware can be scored and its calibration constants iterated without a
    /// human holding the controller.
    ///
    /// Run with (editor must be closed, and note there is NO -quit — play mode
    /// has to keep the process alive; the watcher exits it when the run ends):
    ///
    ///   Unity.exe -batchmode -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.OpusMissionRunner.RunHeadless
    ///     -logFile &lt;log&gt; -opusResult &lt;result.json&gt;
    ///
    /// The session is configured by <see cref="MissionAutorun"/> at runtime via a
    /// request file rather than by static fields here, because entering play mode
    /// triggers a domain reload that would wipe anything set from the editor side.
    /// </summary>
    public static class OpusMissionRunner
    {
        [MenuItem("Tools/AIHWSim/Run Opus Mission (headless scoring)")]
        public static void RunFromMenu() => Begin(DefaultResultPath(), 45f);

        public static void RunHeadless()
        {
            string result = ArgValue("-opusResult") ?? DefaultResultPath();
            float timeout = float.TryParse(ArgValue("-opusTimeout"), out float t) ? t : 45f;
            Begin(result, timeout);
        }

        private static string DefaultResultPath() =>
            Path.Combine(Path.GetTempPath(), "opus_mission_result.json");

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static void Begin(string resultPath, float timeoutSec)
        {
            var req = new MissionAutorun.Request { resultPath = resultPath, timeoutSec = timeoutSec };
            File.WriteAllText(MissionAutorun.RequestPath, JsonUtility.ToJson(req, true));
            if (File.Exists(resultPath)) File.Delete(resultPath);

            string scene = EditorBuildSettings.scenes
                .Select(s => s.path)
                .FirstOrDefault(p => p != null && p.EndsWith("TrackScene.unity", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError("[OpusMissionRunner] TrackScene is not in the build settings.");
                Cleanup();
                EditorApplication.Exit(3);
                return;
            }

            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Debug.Log($"[OpusMissionRunner] entering play mode; result -> {resultPath}");
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;   // play mode ended
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Cleanup();
            if (InternalEditorUtilityIsBatch())
            {
                // Deferred: exiting from inside the state callback can trip over
                // the editor's own teardown.
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
            }
        }

        private static bool InternalEditorUtilityIsBatch() => Application.isBatchMode;

        private static void Cleanup()
        {
            try { if (File.Exists(MissionAutorun.RequestPath)) File.Delete(MissionAutorun.RequestPath); }
            catch { /* MissionAutorun consumes it at boot; this only covers runs that never got that far */ }
        }
    }
}
