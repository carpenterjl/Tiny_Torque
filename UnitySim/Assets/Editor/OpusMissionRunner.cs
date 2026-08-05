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
            // -opusController runs a different DLL on the same car and track;
            // -opusTrack names a different one (a preset OR a scene track);
            // -opusCam WxH forces the camera resolution. All absent — which is
            // every mission run — leaves the scored mission exactly as it was.
            Begin(result, timeout, ArgValue("-opusController") ?? "",
                  ArgValue("-opusTrack"), ArgValue("-opusCam"));
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

        private static void Begin(string resultPath, float timeoutSec,
                                  string controllerDll = "", string track = null,
                                  string cam = null)
        {
            var req = new MissionAutorun.Request
            {
                resultPath = resultPath,
                timeoutSec = timeoutSec,
                controllerDll = controllerDll ?? "",
            };
            if (!string.IsNullOrEmpty(track)) req.track = track;
            // "128x128". Both halves or neither — a half-parsed size would run at
            // some resolution nobody asked for, which is worse than refusing.
            if (!string.IsNullOrEmpty(cam))
            {
                var parts = cam.Split('x', 'X');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int cw) && int.TryParse(parts[1], out int ch) &&
                    cw > 0 && ch > 0)
                {
                    req.camWidth = cw;
                    req.camHeight = ch;
                }
                else
                {
                    Debug.LogError($"[OpusMissionRunner] -opusCam '{cam}' is not WxH.");
                    EditorApplication.Exit(4);
                    return;
                }
            }
            File.WriteAllText(MissionAutorun.RequestPath, JsonUtility.ToJson(req, true));
            if (File.Exists(resultPath)) File.Delete(resultPath);

            // A scene track opens ITSELF; MissionAutorun pulls TrackScene in
            // additively on top, exactly as GameFlow.LoadTrack does. Anything else
            // opens TrackScene directly, which is every scored mission run.
            string want = AIHWSim.Track.SceneTrackCatalog.Resolve(req.track)
                          ?? GameFlow.TrackSceneName;

            string scene = EditorBuildSettings.scenes
                .Select(s => s.path)
                // Exact file name, not EndsWith: "UCSD_TrackScene.unity" ends with
                // "TrackScene.unity" too, and the only thing keeping the mission
                // off it would be that it happens to be registered later.
                .FirstOrDefault(p => p != null && string.Equals(
                    Path.GetFileName(p), want + ".unity",
                    StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError($"[OpusMissionRunner] '{want}' is not in the build settings.");
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
