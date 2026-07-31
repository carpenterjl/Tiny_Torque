using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Where the circuit kit lives, and where its source export comes from.
    ///
    /// The three Formula 1 circuits are not built in Unity — they are surveyed
    /// geometry produced by the Blender project next door
    /// (<c>AI_3D_Modeling/TinyTorque_RC</c>), exported by
    /// <c>scripts/export_unity.py</c> as FBX plus a JSON manifest, and rebuilt
    /// here. Nothing under <c>Circuits/</c> is authored by hand: re-running the
    /// import overwrites all of it, exactly like the rest of this pack.
    ///
    /// The source folder is <b>probed, not hard-coded</b>, with an EditorPrefs
    /// override — the same shape as <c>ControllerWorkspace</c> on the game side,
    /// and for the same reason: a machine that keeps the two repositories
    /// somewhere else should get a sentence telling it so, not a silent no-op.
    /// </summary>
    public static class CircuitPaths
    {
        public const string Tag = "[CIRC]";

        public const string Root = PackPaths.Root + "/Circuits";
        public const string MaterialsDir = PackPaths.MaterialsRoot + "/Circuits";
        public const string PrefabsRoot = PackPaths.PrefabsRoot + "/Circuits";
        public const string ScenesRoot = PackPaths.ScenesRoot + "/Circuits";

        /// <summary>Where <c>export_unity.py</c> wrote its output. Settable from
        /// the menu when the probe misses.</summary>
        public const string SourcePrefKey = "TinyTorque.CircuitExportDir";

        /// <summary>Relative to the Unity project folder, the Blender project is
        /// two levels up and across. Probed rather than assumed — see
        /// <see cref="SourceDir"/>.</summary>
        private static readonly string[] Probes =
        {
            "../../AI_3D_Modeling/TinyTorque_RC/export/unity",
            "../AI_3D_Modeling/TinyTorque_RC/export/unity",
            "../../../AI_3D_Modeling/TinyTorque_RC/export/unity",
        };

        /// <summary>The export folder, or "" with a reason in
        /// <see cref="SourceProblem"/>.</summary>
        public static string SourceDir
        {
            get
            {
                string over = EditorPrefs.GetString(SourcePrefKey, "");
                if (!string.IsNullOrEmpty(over))
                    return IsExport(over) ? over : "";

                string cwd = Directory.GetCurrentDirectory();
                foreach (string rel in Probes)
                {
                    string p = Path.GetFullPath(Path.Combine(cwd, rel));
                    if (IsExport(p)) return p;
                }
                return "";
            }
        }

        /// <summary>A sentence, not a bool — "no circuits appeared" and "the
        /// export folder moved" look identical from the menu otherwise.</summary>
        public static string SourceProblem
        {
            get
            {
                string over = EditorPrefs.GetString(SourcePrefKey, "");
                if (!string.IsNullOrEmpty(over))
                    return IsExport(over) ? ""
                        : "The configured export folder has no circuits.json: " + over;
                if (!string.IsNullOrEmpty(SourceDir)) return "";
                return "Could not find the Blender export. Run\n"
                     + "  blender -b --factory-startup -P scripts/export_unity.py -- --all\n"
                     + "in AI_3D_Modeling/TinyTorque_RC, then use "
                     + "Tools > TinyTorque Assets > Circuits > Set export folder...";
            }
        }

        private static bool IsExport(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "circuits.json"));

        // ---- per-circuit layout ---------------------------------------------

        /// <summary>"spa" -> "Spa". The manifest keys are lower case; the folders
        /// and scenes follow the pack's Pascal-case convention.</summary>
        public static string Display(string key) =>
            string.IsNullOrEmpty(key) ? key
                : char.ToUpperInvariant(key[0]) + key.Substring(1);

        public static string DirFor(string key) => Root + "/" + Display(key);
        public static string ManifestPath(string key) =>
            DirFor(key) + "/" + key + ".circuit.json";
        public static string PrefabDirFor(string key) =>
            PrefabsRoot + "/" + Display(key);
        public static string ScenePath(string key) =>
            ScenesRoot + "/TTA_" + Display(key) + ".unity";

        public static void EnsureLayout(string key)
        {
            PackPaths.EnsureFolder(Root);
            PackPaths.EnsureFolder(MaterialsDir);
            PackPaths.EnsureFolder(PrefabsRoot);
            PackPaths.EnsureFolder(ScenesRoot);
            if (string.IsNullOrEmpty(key)) return;
            PackPaths.EnsureFolder(DirFor(key));
            PackPaths.EnsureFolder(DirFor(key) + "/world");
            PackPaths.EnsureFolder(DirFor(key) + "/split");
            PackPaths.EnsureFolder(DirFor(key) + "/props");
            PackPaths.EnsureFolder(PrefabDirFor(key));
        }

        public static void Log(string msg) => Debug.Log(Tag + " " + msg);
        public static void Warn(string msg) => Debug.LogWarning(Tag + " " + msg);
        public static void Err(string msg) => Debug.LogError(Tag + " " + msg);
    }
}
