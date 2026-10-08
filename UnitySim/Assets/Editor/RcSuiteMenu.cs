using System;
using System.IO;
using AIHWSim.Core.PhysicsTests;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Launches the RC-scale physics suite (<see cref="RcSuite"/>, VAL-01).
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.RcSuiteMenu.RunHeadless \
    ///   -logFile &lt;log&gt; -rcResult &lt;out.txt&gt; [-rcVehicle "Opus Vector FOC"]
    /// </code>
    ///
    /// It writes a request file, opens an empty scene and enters play mode;
    /// the runtime side builds its own ground and cars, steps physics by hand,
    /// writes the report (plus a .json beside it) and exits with 0 on a pass.
    /// </summary>
    public static class RcSuiteMenu
    {
        [MenuItem("Tools/AIHWSim/Physics Tests/Run [RC] RC-scale suite (R0–R10)", priority = 123)]
        public static void RunFromMenu() => Begin(Path.Combine(Path.GetTempPath(), "tt_rc_suite.txt"), null);

        public static void RunHeadless() =>
            Begin(ArgValue("-rcResult") ?? Path.Combine(Path.GetTempPath(), "tt_rc_suite.txt"),
                  ArgValue("-rcVehicle"));

        private static void Begin(string resultPath, string vehicle)
        {
            var req = new RcSuite.Request { resultPath = resultPath };
            if (!string.IsNullOrEmpty(vehicle)) req.vehicle = vehicle;
            File.WriteAllText(RcSuite.RequestPath, JsonUtility.ToJson(req, true));
            if (File.Exists(resultPath)) File.Delete(resultPath);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Debug.Log($"[RC] entering play mode; report -> {resultPath}");
            EditorApplication.EnterPlaymode();
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
