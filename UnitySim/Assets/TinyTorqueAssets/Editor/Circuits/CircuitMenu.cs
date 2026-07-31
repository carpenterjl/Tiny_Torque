using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Menu entries and the headless entry point for the circuit pipeline.
    ///
    ///     Unity.exe -batchmode -projectPath &lt;UnitySim&gt; ^
    ///         -executeMethod AIHWSim.Pack.Circuits.CircuitMenu.RunHeadless ^
    ///         -logFile circuits.log
    ///
    /// <b>No -nographics.</b> Material generation resolves the Standard shader,
    /// which needs a graphics device — the same reason [TPV] and the pack's own
    /// headless run omit it.
    ///
    /// The steps are separate menu items because each is individually
    /// re-runnable and usually you want one of them. They run in order and the
    /// order matters: the axis test gates everything (a mirrored import is worth
    /// finding before 16 000 trees are placed with it), the copy has to precede
    /// the build, and materials are generated per circuit from that circuit's
    /// manifest.
    /// </summary>
    public static class CircuitMenu
    {
        [MenuItem("Tools/TinyTorque Assets/Circuits/1. Import circuit assets", priority = 201)]
        public static void ImportAll()
        {
            var keys = CircuitImport.Available();
            if (keys.Count == 0)
            {
                CircuitPaths.Err(CircuitPaths.SourceProblem);
                return;
            }
            try
            {
                AssetDatabase.StartAssetEditing();
                foreach (string k in keys) CircuitImport.CopyOne(k);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            CircuitPaths.Log("imported " + keys.Count + " circuit(s): "
                             + string.Join(", ", keys));
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/2. Build circuit scenes", priority = 202)]
        public static void BuildAll()
        {
            foreach (string k in CircuitImport.Available()) BuildOne(k);
        }

        public static bool BuildOne(string key)
        {
            var man = CircuitManifest.Load(CircuitPaths.ManifestPath(key));
            if (man == null) return false;
            return CircuitSceneBuilder.Build(man);
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/Set export folder...", priority = 220)]
        public static void SetSourceFolder()
        {
            string cur = CircuitPaths.SourceDir;
            string picked = EditorUtility.OpenFolderPanel(
                "Blender circuit export (the folder holding circuits.json)",
                string.IsNullOrEmpty(cur) ? "" : cur, "");
            if (string.IsNullOrEmpty(picked)) return;
            EditorPrefs.SetString(CircuitPaths.SourcePrefKey, picked);
            CircuitPaths.Log(string.IsNullOrEmpty(CircuitPaths.SourceDir)
                ? "that folder has no circuits.json: " + picked
                : "export folder set to " + picked);
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/Rebuild everything", priority = 199)]
        public static void RunAll()
        {
            CircuitMaterials.ResetCaches();
            if (!CircuitAxisTest.Run(out string axis))
            {
                CircuitPaths.Err(axis);
                CircuitPaths.Err("stopping: everything downstream places geometry "
                                 + "with the conversion that just failed");
                return;
            }
            CircuitPaths.Log(axis);
            ImportAll();
            BuildAll();
        }

        /// <summary>Headless: run the lot, then report one greppable line per
        /// circuit and a single verdict, in the shape the project's other
        /// validators use.</summary>
        public static void RunHeadless()
        {
            CircuitMaterials.ResetCaches();
            var lines = new List<string>();
            bool pass = CircuitAxisTest.Run(out string axis);
            lines.Add(axis);

            if (pass)
            {
                ImportAll();
                foreach (string k in CircuitImport.Available())
                {
                    var man = CircuitManifest.Load(CircuitPaths.ManifestPath(k));
                    if (man == null) { pass = false; continue; }
                    if (!CircuitSceneBuilder.Build(man)) pass = false;
                    lines.Add(CircuitSceneBuilder.LastReport);
                }
            }

            foreach (string l in lines) Debug.Log(CircuitPaths.Tag + " " + l);
            Debug.Log(CircuitPaths.Tag + " RESULT " + (pass ? "ALL PASS" : "FAIL"));
        }
    }
}
