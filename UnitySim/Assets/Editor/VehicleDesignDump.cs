using System;
using System.IO;
using AIHWSim.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Editor half of the design dump: opens an empty scene and enters play
    /// mode, where <see cref="VehicleDesignDumpRun"/> does the work.
    ///
    /// Play mode is not optional — see that class for why. An empty scene is
    /// enough: nothing here needs a track, and opening one would drag
    /// TrackBootstrap's entire world in alongside the measurement.
    ///
    /// Run with (editor must be closed, and note there is NO -quit — play mode
    /// has to keep the process alive; the runtime half exits it):
    ///
    ///   Unity.exe -batchmode -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.VehicleDesignDump.Run
    ///     -logFile &lt;log&gt; -designDump &lt;out.txt&gt;
    ///
    /// Take a baseline before a physics refactor, repeat after, and diff. An
    /// empty diff is the claim "no existing design moved", proved rather than
    /// asserted. PowerShell does not wait on a Unity batch run — poll for the
    /// output file in a later call.
    /// </summary>
    public static class VehicleDesignDump
    {
        [MenuItem("Tools/AIHWSim/Dump Vehicle Design Physics")]
        public static void RunFromMenu() => Begin(DefaultPath());

        public static void Run() => Begin(ArgValue("-designDump") ?? DefaultPath());

        private static string DefaultPath() =>
            Path.Combine(Path.GetTempPath(), "design_dump.txt");

        private static void Begin(string outPath)
        {
            File.WriteAllText(VehicleDesignDumpRun.RequestPath, outPath);
            if (File.Exists(outPath)) File.Delete(outPath);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            Debug.Log($"[DUMP] entering play mode; dump -> {outPath}");
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            Cleanup();
            if (Application.isBatchMode)
                // Deferred: exiting from inside the state callback can trip over
                // the editor's own teardown.
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static void Cleanup()
        {
            try
            {
                if (File.Exists(VehicleDesignDumpRun.RequestPath))
                    File.Delete(VehicleDesignDumpRun.RequestPath);
            }
            catch { /* the runtime half consumes it at boot; this covers runs that never got there */ }
        }

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
