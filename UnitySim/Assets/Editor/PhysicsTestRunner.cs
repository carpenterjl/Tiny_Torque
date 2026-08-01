using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Runs one physics test scene without a human.
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.PhysicsTestRunner.RunHeadless \
    ///   -logFile &lt;log&gt; -physTest P2 -physResultDir &lt;dir&gt;
    /// </code>
    ///
    /// No <c>-quit</c> — play mode keeps the process alive and the test itself
    /// calls <c>EditorApplication.Exit</c> when its verdict lands. No
    /// <c>-nographics</c> either: this project's cars carry a camera sensor and
    /// the run dies without a graphics device.
    ///
    /// It opens the scene rather than building one, so <b>the gate measures the
    /// same scene you watched</b>. Scene resolution goes through
    /// <c>AssetDatabase.FindAssets</c>, so no Build Settings entry is needed.
    /// </summary>
    public static class PhysicsTestRunner
    {
        public static void RunHeadless()
        {
            string id = ArgValue("-physTest");
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogError("[PHYS] -physTest <id> is required");
                EditorApplication.Exit(3);
                return;
            }

            var entry = PhysicsTestMenu.Find(id);
            if (entry == null)
            {
                Debug.LogError($"[PHYS] unknown test '{id}'");
                EditorApplication.Exit(3);
                return;
            }

            string scene = entry.Value.ScenePath;
            if (!File.Exists(scene))
            {
                // Resolve by name in case the folder moved, before giving up.
                scene = AssetDatabase.FindAssets($"t:Scene PhysTest_{entry.Value.Id}")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => p.EndsWith($"PhysTest_{entry.Value.Id}.unity",
                                                    StringComparison.OrdinalIgnoreCase));
            }
            if (string.IsNullOrEmpty(scene))
            {
                Debug.LogError($"[PHYS] scene for '{id}' not found — run "
                               + "Tools/AIHWSim/Physics Tests/Create All Test Scenes");
                EditorApplication.Exit(3);
                return;
            }

            // Clear any stale result so a crashed run cannot be read as a pass.
            string outPath = AIHWSim.Core.PhysicsTests.PhysicsTest.ResultPathFor(entry.Value.Id);
            if (File.Exists(outPath)) File.Delete(outPath);

            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            if (Application.isBatchMode)
                EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == flag) return args[i + 1];
            return null;
        }
    }
}
