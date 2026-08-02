using System.Collections.Generic;
using System.IO;
using AIHWSim.Pack;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Copies an external export's FBX into the project so it can be RENDERED.
    ///
    /// <b>Why this exists at all.</b> Unity renders meshes it has imported, and it
    /// imports files under <c>Assets/</c>. A Blender export sitting two directories
    /// outside the project is therefore not previewable by writing better preview
    /// code — there is no mesh object to hand a camera. Everything else Asset
    /// Studio says about an un-imported export (its materials, its objects, the
    /// scale and yaw it needs) is read out of <c>export.json</c> and needs no copy;
    /// only the picture does.
    ///
    /// <b>Why it is safe.</b> The destination is outside <c>Resources/</c>, so
    /// <c>Resources.Load</c> cannot reach a staged file and it can never become a
    /// key the game finds by accident. It is scratch: "Clear preview staging"
    /// empties the folder, and the commit pipeline will copy into
    /// <c>Resources/</c> from the EXPORT rather than from here, so nothing
    /// downstream can come to depend on a staged copy still being around.
    ///
    /// <b>Why the copy is byte-guarded.</b> Same reason the pack's is
    /// (<c>PackImportMenu.SameBytes</c>): rewriting an unchanged file re-imports
    /// it and churns its <c>.meta</c> GUID, and a preview that reimports a 40 MB
    /// FBX every time you click its row is a preview nobody opens twice.
    /// </summary>
    public static class AssetStudioStaging
    {
        /// <summary>Project-relative path of the staged FBX for
        /// <paramref name="x"/>, whether or not it exists yet.</summary>
        public static string PathFor(TtExport x) =>
            x == null ? "" : AssetStudio.StagedPathFor(x.assetName);

        public static bool IsStaged(TtExport x)
        {
            string p = PathFor(x);
            return !string.IsNullOrEmpty(p) && File.Exists(PackPaths.ToAbsolute(p));
        }

        /// <summary>
        /// Ensure the staged copy exists and matches the export. Returns the
        /// project-relative path, or "" with a sentence in
        /// <paramref name="problem"/>.
        /// </summary>
        public static string Stage(TtExport x, out string problem)
        {
            problem = "";
            if (x == null) { problem = "no export to stage"; return ""; }

            string src = x.FbxPath;
            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                problem = "export.json names " + (x.fbxFile ?? "(nothing)")
                        + " but that file is not in the export folder";
                return "";
            }

            string dst = PathFor(x);
            string dstAbs = PackPaths.ToAbsolute(dst);

            if (File.Exists(dstAbs) && SameBytes(src, dstAbs)) return dst;

            try
            {
                PackPaths.EnsureFolder(AssetStudio.StagingDir);
                File.Copy(src, dstAbs, true);
            }
            catch (System.Exception e)
            {
                problem = "could not stage the FBX: " + e.Message;
                return "";
            }

            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            AssetStudio.Log("staged " + Path.GetFileName(src) + " -> " + dst
                            + " (preview only; not a game key)");
            return dst;
        }

        /// <summary>
        /// Empty the staging folder. Explicit rather than automatic: a staged FBX
        /// costs disk and nothing else, and deleting one out from under an open
        /// preview would be a puzzle rather than a tidy-up.
        /// </summary>
        [MenuItem(AssetStudio.Menu + "Clear preview staging",
                  priority = AssetStudio.PrioClearStaging)]
        public static void Clear()
        {
            string abs = PackPaths.ToAbsolute(AssetStudio.StagingDir);
            if (!Directory.Exists(abs)) { AssetStudio.Log("staging is already empty."); return; }

            var doomed = new List<string>();
            foreach (string f in Directory.GetFiles(abs))
                if (!f.EndsWith(".meta")) doomed.Add(PackPaths.ToProjectRelative(f));
            if (doomed.Count == 0) { AssetStudio.Log("staging is already empty."); return; }

            foreach (string p in doomed) AssetDatabase.DeleteAsset(p);
            AssetDatabase.Refresh();
            AssetStudio.Log($"cleared {doomed.Count} staged "
                            + (doomed.Count == 1 ? "file." : "files."));
        }

        private static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            return PackHash.Of(a) == PackHash.Of(b);
        }
    }
}
