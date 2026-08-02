using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Shared naming, logging, paths and menu ordering for Asset Studio — the
    /// editor-side browser and importer for externally authored art.
    ///
    /// A separate menu root from <c>Tools/AIHWSim/</c> (a flat list of one-shot
    /// actions) and from <c>Tools/TinyTorque Assets/</c> (the pack pipeline, which
    /// owns geometry the repository itself generates). Asset Studio's subject is
    /// the opposite: geometry that arrives from OUTSIDE the repository, authored in
    /// Blender by hand, which the pack pipeline has no opinion about.
    ///
    /// The class is named for the tool and the namespace for the family, matching
    /// <c>AIHWSim.TrackTools.TrackStudio</c> — a class with the same identifier as
    /// its own namespace is legal C# and a lasting resolution nuisance.
    /// </summary>
    public static class AssetStudio
    {
        public const string Menu = "Tools/Asset Studio/";

        /// <summary>Log prefix, greppable in a headless log next to [PMV], [TPV],
        /// [COS], [PACK] and [TRK].</summary>
        public const string Tag = "[AST]";

        public static void Log(string msg) => Debug.Log(Tag + " " + msg);
        public static void Warn(string msg) => Debug.LogWarning(Tag + " " + msg);
        public static void Error(string msg) => Debug.LogError(Tag + " " + msg);

        // ---- the three Resources roots ---------------------------------------

        /// <summary>
        /// The only folders the game can load a mesh from. <c>PartMeshLibrary</c>
        /// resolves every key through <c>Resources.Load</c> under one of these
        /// three, so an FBX anywhere else is unreachable at runtime no matter how
        /// correct it is — and gets none of <c>PartModelPostprocessor</c>'s import
        /// settings either, since that is scoped by the same path prefixes.
        /// </summary>
        public const string PartModelsRoot = "Assets/Resources/PartModels";
        public const string TrackPropsRoot = "Assets/Resources/TrackProps";
        public const string CosmeticsRoot = "Assets/Resources/Cosmetics";

        public static readonly string[] Roots =
            { PartModelsRoot, TrackPropsRoot, CosmeticsRoot };

        /// <summary>Trailing folder name only — "PartModels", "TrackProps",
        /// "Cosmetics" — which is also the <c>Resources.Load</c> path prefix.</summary>
        public static string RootName(string root) => Path.GetFileName(root);

        /// <summary>Where a draft lives while it is being authored. Outside
        /// <c>Resources/</c> and in no build scene, so it costs a player build
        /// nothing — the same argument the pack makes for its own working
        /// files.</summary>
        public const string DraftDir = "Assets/TinyTorqueAssets/AssetStudio/Drafts";

        /// <summary>
        /// Where an external export's FBX is copied so it can be RENDERED before
        /// anyone has decided to commit it.
        ///
        /// This exists because Unity cannot render a mesh it has not imported,
        /// and it cannot import a file outside <c>Assets/</c> — so a preview of
        /// the police car sitting in the Blender export folder is not a matter of
        /// writing better preview code, it is a matter of the file being
        /// somewhere the AssetDatabase can see. The copy is explicit and
        /// user-pressed, never automatic.
        ///
        /// Outside <c>Resources/</c>, which is the safety property: a staged FBX
        /// is unreachable by <c>Resources.Load</c>, so nothing here can become a
        /// key the game accidentally finds. Staging is scratch — "Clear preview
        /// staging" empties it, and the commit pipeline copies into
        /// <c>Resources/</c> from the EXPORT, never from here.
        /// </summary>
        public const string StagingDir = "Assets/TinyTorqueAssets/AssetStudio/Staging";

        /// <summary>Project-relative path a given export stages to. Flat, one FBX
        /// per asset name, so re-staging overwrites rather than accumulating.</summary>
        public static string StagedPathFor(string assetName) =>
            StagingDir + "/" + SafeName(assetName) + ".fbx";

        /// <summary>An asset name reduced to what is safe in a file name. Blender
        /// object names allow spaces and punctuation that a path does not.</summary>
        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        /// <summary>The manifest that rides beside an asset's FBX. Sibling rather
        /// than a <c>Manifests/</c> subfolder because <c>PartModelPostprocessor</c>
        /// has to find it by path arithmetic DURING import, where it cannot call
        /// <c>Resources.Load</c> — and because that is where the one manifest the
        /// project already ships, <c>tiguan_materials.json</c>, lives.</summary>
        public static string ManifestPathFor(string fbxAssetPath) =>
            Path.Combine(Path.GetDirectoryName(fbxAssetPath) ?? "",
                         Path.GetFileNameWithoutExtension(fbxAssetPath) + "_asset.json")
                .Replace('\\', '/');

        // ---- the external export folder --------------------------------------

        /// <summary>Where the Blender exporter writes its per-asset folders.
        /// Settable from the menu when the probe misses.</summary>
        public const string SourcePrefKey = "TinyTorque.AssetExportDir";

        /// <summary>
        /// Probed, not hard-coded, with an EditorPrefs override — the same shape as
        /// <see cref="AIHWSim.Pack.Circuits.CircuitPaths.SourceDir"/> and for the
        /// same reason: a machine that keeps the art repository somewhere else
        /// should get a sentence telling it so, not a silent empty list.
        /// </summary>
        private static readonly string[] Probes =
        {
            "../../Unity Testing/Game Tests/Assets/TinyTorqueTests",
            "../../AI_3D_Modeling/Test Exports",
            "../../AI_3D_Modeling/TinyTorque_RC/export/assets",
            "../TinyTorqueExports",
        };

        /// <summary>The export root, or "" with a reason in
        /// <see cref="SourceProblem"/>.</summary>
        public static string SourceDir
        {
            get
            {
                string over = EditorPrefs.GetString(SourcePrefKey, "");
                if (!string.IsNullOrEmpty(over))
                    return IsExportRoot(over) ? over : "";

                string cwd = Directory.GetCurrentDirectory();
                foreach (string rel in Probes)
                {
                    string p = Path.GetFullPath(Path.Combine(cwd, rel));
                    if (IsExportRoot(p)) return p;
                }
                return "";
            }
        }

        /// <summary>A sentence, not a bool — "nothing has been exported yet" and
        /// "the export folder moved" look identical from the window otherwise.
        /// Empty when the folder is fine.</summary>
        public static string SourceProblem
        {
            get
            {
                string over = EditorPrefs.GetString(SourcePrefKey, "");
                if (!string.IsNullOrEmpty(over))
                    return IsExportRoot(over) ? ""
                        : "The configured export folder holds no asset folders "
                        + "(a folder containing export.json): " + over;
                if (!string.IsNullOrEmpty(SourceDir)) return "";
                return "No Blender export folder found. Export an asset, then use "
                     + "Tools > Asset Studio > Set export folder... to point at the "
                     + "folder that CONTAINS the per-asset folders (the one with "
                     + "POLICE/ inside it, not POLICE/ itself).";
            }
        }

        /// <summary>An export ROOT holds per-asset folders; an asset folder holds
        /// the <c>export.json</c>. Accepts being handed either, so pointing the
        /// picker one level too deep is not a dead end.</summary>
        public static bool IsExportRoot(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, TtExport.FileName))) return true;
            foreach (string sub in Directory.GetDirectories(dir))
                if (File.Exists(Path.Combine(sub, TtExport.FileName))) return true;
            return false;
        }

        // ---- menu priorities --------------------------------------------------

        /// <summary>
        /// Unity draws a separator when consecutive priorities differ by 11 or
        /// more, which is what puts the window, the numbered pipeline steps, the
        /// run-everything item and the validator into four visual blocks — the
        /// trick Track Studio and the pack both use.
        /// </summary>
        public const int PrioWindow = 20;
        public const int PrioSetSource = 21;
        public const int PrioClearStaging = 22;
        public const int PrioSync = 100;
        public const int PrioCommit = 101;
        public const int PrioRefreshImports = 102;
        public const int PrioResetCaches = 103;
        public const int PrioRebuildAll = 110;
        public const int PrioValidate = 200;
    }

    /// <summary>
    /// What an asset IS, which decides its geometry contract. Inferred from the key
    /// prefix and the Resources root rather than authored, because that is exactly
    /// how the game decides: <c>CarVehicle.BodyMeshKey</c> builds "body_*" and
    /// <c>PartVisualFactory.BuildWheelViz</c> builds "wheel_*", and nothing else in
    /// <c>PartModels/</c> is either.
    /// </summary>
    public enum AssetKind
    {
        CarBody,
        Wheel,
        Cosmetic,
        Prop,
        /// <summary>In PartModels but neither a body nor a wheel — the battery,
        /// the antennas, the light bars. Renders at authored size with no runtime
        /// scale contract at all.</summary>
        Fitting,
        /// <summary>An external export that has not been assigned a kind yet.</summary>
        Unassigned,
    }
}
