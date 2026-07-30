using System;
using System.IO;
using System.Linq;
using AIHWSim.Track;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Runs the racing-line calibration in play mode and writes the measured
    /// scalars back into the <see cref="RacingLineAsset"/>.
    ///
    /// Headless:
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt;
    ///   -executeMethod AIHWSim.TrackTools.RacingLineCalibrationRunner.Report
    ///   -logFile cal.log -trkScene TTA_Sandbox -trkResult C:\path\cal.json
    /// </code>
    /// <b>No <c>-quit</c>, and no <c>-nographics</c>.</b> Play mode must keep the
    /// process alive until the watcher exits it (the same reason OpusMissionRunner
    /// omits -quit), and the car carries a camera sensor that needs a graphics
    /// device.
    /// </summary>
    public static class RacingLineCalibrationRunner
    {
        private const string PendingKey = "TrackStudio.CalPending";
        private const string ResultKey = "TrackStudio.CalResult";
        private const string LineKey = "TrackStudio.CalLine";

        [MenuItem(TrackStudio.Menu + "5. Calibrate racing line (play mode)",
                  priority = TrackStudio.PrioCalibrate)]
        public static void RunFromMenu()
        {
            var d = UnityEngine.Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null) { TrackStudio.Warn("no SceneTrackDescriptor in this scene."); return; }
            if (d.racingLine == null) { TrackStudio.Warn("bake a racing line first."); return; }
            Begin(EditorSceneManager.GetActiveScene().path, d.racingLine, DefaultResultPath(), 180f);
        }

        public static void Report()
        {
            string sceneArg = ArgValue("-trkScene");
            string result = ArgValue("-trkResult") ?? DefaultResultPath();
            float timeout = float.TryParse(ArgValue("-trkTimeout"), out float t) ? t : 180f;

            string scenePath = ResolveScenePath(sceneArg);
            if (scenePath == null)
            {
                TrackStudio.Error($"FAIL no scene asset for '-trkScene {sceneArg}'");
                EditorApplication.Exit(3);
                return;
            }

            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var d = UnityEngine.Object.FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null || d.racingLine == null)
            {
                TrackStudio.Error($"FAIL '{scenePath}' has no descriptor or no baked racing line");
                EditorApplication.Exit(3);
                return;
            }
            Begin(scenePath, d.racingLine, result, timeout);
        }

        private static void Begin(string scenePath, RacingLineAsset line,
            string resultPath, float timeoutSec)
        {
            var req = new RacingLineAutorun.Request
            {
                linePath = AssetDatabase.GetAssetPath(line),
                resultPath = resultPath,
                vehicle = "TT Patrol",
                timeoutSec = timeoutSec,
            };
            File.WriteAllText(RacingLineAutorun.RequestPath, JsonUtility.ToJson(req, true));
            if (File.Exists(resultPath)) File.Delete(resultPath);

            // SessionState survives the domain reload that entering play mode
            // triggers, which plain statics do not. The runtime side gets its copy
            // through the request file for the same reason.
            SessionState.SetBool(PendingKey, true);
            SessionState.SetString(ResultKey, resultPath);
            SessionState.SetString(LineKey, req.linePath);

            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            TrackStudio.Log($"CALIBRATE entering play mode on '{scenePath}'; " +
                            $"result -> {resultPath}");
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;   // play mode ended
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (!SessionState.GetBool(PendingKey, false)) return;

            string resultPath = SessionState.GetString(ResultKey, "");
            string linePath = SessionState.GetString(LineKey, "");
            SessionState.EraseBool(PendingKey);

            int code = ApplyResult(resultPath, linePath) ? 0 : 2;
            CleanupRequest();

            if (InternalIsBatch())
                // Deferred: exiting from inside the state callback can trip over the
                // editor's own teardown.
                EditorApplication.delayCall += () => EditorApplication.Exit(code);
        }

        private static bool ApplyResult(string resultPath, string linePath)
        {
            if (!File.Exists(resultPath))
            {
                TrackStudio.Error("FAIL calibration produced no result file");
                return false;
            }

            RacingLineAutorun.Result res;
            try { res = JsonUtility.FromJson<RacingLineAutorun.Result>(File.ReadAllText(resultPath)); }
            catch (Exception e) { TrackStudio.Error($"FAIL result unreadable: {e.Message}"); return false; }

            if (res == null || !res.ok)
            {
                TrackStudio.Error($"FAIL calibration: {(res != null ? res.fault : "no result")}");
                return false;
            }

            var line = AssetDatabase.LoadAssetAtPath<RacingLineAsset>(linePath);
            if (line == null) { TrackStudio.Error($"FAIL racing line missing at '{linePath}'"); return false; }

            float repeat = Mathf.Abs(res.lap3 - res.lap2);
            if (repeat > 0.02f)
            {
                // Surface roughness is a deterministic positional field, so two laps
                // of the same line MUST agree. A gap means something non-deterministic
                // is in the loop, and fitting it would bake noise into the asset.
                TrackStudio.Error($"FAIL laps 2 and 3 differ by {repeat * 1000f:0} ms — " +
                                  "the run was not deterministic, so the fit is noise");
                return false;
            }

            Undo.RecordObject(line, "Apply calibration");
            var c = line.calibration;
            c.valid = true;
            c.muScale = res.muScale;
            c.accelA0 = res.accelA0;
            c.vMax = res.vMax;
            c.brakeUse = res.brakeUse;
            c.measuredLapSec = res.lap2;
            c.predictedLapSec = line.predictedLapSec;
            c.residualPct = res.lap2 > 0.01f
                ? Mathf.Abs(line.predictedLapSec - res.lap2) / res.lap2 * 100f : 0f;
            c.lapRepeatDeltaSec = repeat;
            c.limitFraction = res.limitFraction;
            c.vehicle = res.vehicle;
            line.calibration = c;
            EditorUtility.SetDirty(line);
            AssetDatabase.SaveAssets();

            TrackStudio.Log($"CALIBRATE mu {c.muScale:0.000}, a0 {c.accelA0:0.00} m/s^2, " +
                $"vMax {c.vMax:0.00} m/s, measured {c.measuredLapSec:0.000} s vs predicted " +
                $"{c.predictedLapSec:0.000} s ({c.residualPct:0.0}% residual), repeat " +
                $"{repeat * 1000f:0} ms, limit fraction {c.limitFraction:0.00})");

            if (c.residualPct > 2f)
                TrackStudio.Warn($"residual is {c.residualPct:0.0}% — re-bake the line so " +
                                 "the profile uses the measured scalars, then re-check.");
            return true;
        }

        private static void CleanupRequest()
        {
            try { if (File.Exists(RacingLineAutorun.RequestPath)) File.Delete(RacingLineAutorun.RequestPath); }
            catch { /* a leftover request is harmless: it is consumed-and-deleted on read */ }
        }

        private static string DefaultResultPath() =>
            Path.Combine(Path.GetTempPath(), "tinytorque_raceline_result.json");

        private static string ResolveScenePath(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath))
                return EditorSceneManager.GetActiveScene().path;
            if (nameOrPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(nameOrPath))
                return nameOrPath;
            return AssetDatabase.FindAssets("t:Scene " + nameOrPath)
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == nameOrPath);
        }

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        private static bool InternalIsBatch() => Application.isBatchMode;
    }
}
