using AIHWSim.Core.Flight;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Runs <see cref="FlightTrimProbe"/> in a scene of its own.
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.FlightTrimRunner.RunHeadless \
    ///   -logFile &lt;log&gt; [-trimResultDir &lt;dir&gt;]
    /// </code>
    ///
    /// No <c>-quit</c> and no <c>-nographics</c>: play mode keeps the process alive
    /// and the probe exits by itself, and the graphics device is needed for the
    /// same reason the car tests need it.
    ///
    /// The scene is built in memory rather than saved, because this is a probe
    /// rather than a fixture — nothing else opens it, so an asset would only be one
    /// more thing whose serialized defaults could disagree with the code.
    /// </summary>
    public static class FlightTrimRunner
    {
        [MenuItem("Tools/AIHWSim/Physics Tests/Run [TRIM] Flight Trim Probe", priority = 121)]
        public static void RunFromMenu() => Begin();

        public static void RunHeadless() => Begin();

        private static void Begin()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var go = new GameObject("FlightTrimProbe");
            go.AddComponent<FlightTrimProbe>();
            EditorApplication.EnterPlaymode();
        }
    }
}
