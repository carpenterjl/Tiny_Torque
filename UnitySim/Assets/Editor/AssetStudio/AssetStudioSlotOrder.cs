using System.Collections.Generic;
using System.IO;
using AIHWSim.EditorTools;
using AIHWSim.Pack;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Which material sits in which submesh slot, read off the FBX instead of
    /// guessed at.
    ///
    /// <b>Why this is not simply available.</b> The slot order is in the file the
    /// whole time. <see cref="PartModelPostprocessor"/> forces
    /// <c>materialImportMode = None</c> on everything under the three Resources
    /// roots and under Staging — correctly, because the game binds its own
    /// materials and an imported one would be a second answer — and that setting
    /// destroys the ordering on the way in. So every import Asset Studio could
    /// previously reach gave the slot COUNT and 42 copies of Unity's
    /// <c>Default-Material</c>, and which material belonged in which slot became
    /// a question for a human with a preview window.
    ///
    /// <b>What it replaces, and why that was worth doing.</b>
    /// <c>export.json</c> lists a material's objects in the order the MATERIALS
    /// appear in the file, which is Blender's material-list order and has no
    /// reason to match an object's slot order. It was therefore a proposal, and
    /// <c>DraftObject.slotsVerified</c> existed to stop a proposal being committed
    /// as a fact. Measured on the police car, that proposal is <b>backwards</b>:
    /// <c>export.json</c> offers <c>[M_Police_Dark, M_Police_Paint]</c> for
    /// <c>Police_Body</c> and the FBX says <c>[M_Police_Paint, M_Police_Dark]</c>.
    /// Getting that pair the wrong way round is not cosmetic — R3 established
    /// that a repaint writes by slot, so the reversed order recolours the trim and
    /// leaves the livery alone.
    ///
    /// <b>How.</b> Copy the FBX somewhere no postprocessor is scoped to, import it
    /// with materials ON, read <c>sharedMaterials</c> per renderer, delete the
    /// copy. Scratch in the strictest sense: the folder is created and removed
    /// inside one call, it is outside <c>Resources/</c> so nothing there can
    /// become a game key even for the moment it exists, and no committed asset is
    /// ever imported this way.
    /// </summary>
    public static class AssetStudioSlotOrder
    {
        /// <summary>
        /// Scratch import folder. A SIBLING of Staging, not a child: the scope
        /// test is a path-prefix match on <c>StagingDir</c>, so a child would be
        /// stripped exactly like a staged file and this whole class would read
        /// back nothing but <c>Default-Material</c>. <see cref="Read"/> asserts
        /// the folder is out of scope rather than trusting this comment.
        /// </summary>
        public const string ProbeDir = "Assets/TinyTorqueAssets/AssetStudio/SlotProbe";

        /// <summary>
        /// Object name → the material name in each submesh slot, straight off the
        /// FBX. Empty (with a sentence in <paramref name="problem"/>) when the
        /// order could not be read, which callers must treat as "ask the author"
        /// rather than as "there are no slots".
        /// </summary>
        public static Dictionary<string, string[]> Read(TtExport x, out string problem)
        {
            problem = "";
            var map = new Dictionary<string, string[]>();
            if (x == null) { problem = "no export to read"; return map; }

            string src = x.FbxPath;
            if (string.IsNullOrEmpty(src) || !File.Exists(src))
            {
                problem = "the export names " + (x.fbxFile ?? "(nothing)")
                        + " but that file is not in the export folder";
                return map;
            }

            string dst = ProbeDir + "/" + AssetStudio.SafeName(x.assetName) + ".fbx";
            if (PartModelPostprocessor.InScope(dst))
            {
                // Not paranoia: this is one edit to StagingDir away from being
                // true, and the failure it produces is a silent wrong answer.
                problem = "the slot-order scratch folder is inside PartModelPostprocessor's "
                        + "scope, so its materials would be stripped before they could be "
                        + "read. Move " + ProbeDir + " out from under the import rules.";
                return map;
            }

            try
            {
                PackPaths.EnsureFolder(ProbeDir);
                File.Copy(src, PackPaths.ToAbsolute(dst), true);
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);

                if (AssetImporter.GetAtPath(dst) is not ModelImporter mi)
                {
                    problem = "the export's FBX did not import as a model";
                    return map;
                }

                // Explicit rather than relying on the project default. Out here
                // the default happens to be ImportViaMaterialDescription, which
                // also works, but "happens to" is not a contract and a project
                // setting could change it under us.
                mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
                mi.SaveAndReimport();

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
                if (go == null) { problem = "the imported model had no prefab to read"; return map; }

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] ms = r.sharedMaterials;
                    if (ms == null || ms.Length == 0) continue;
                    var names = new string[ms.Length];
                    for (int i = 0; i < ms.Length; i++)
                        names[i] = ms[i] == null ? "" : ms[i].name;
                    map[r.gameObject.name] = names;
                }
            }
            catch (System.Exception e)
            {
                problem = "could not read the slot order: " + e.Message;
                map.Clear();
            }
            finally
            {
                Discard();
            }

            return map;
        }

        /// <summary>
        /// Remove the scratch folder. In a <c>finally</c>, because a scratch FBX
        /// left in the project is a file the author did not put there and cannot
        /// account for — and this one carries the export's materials, which is
        /// precisely the state every other copy in this tool exists to avoid.
        /// </summary>
        private static void Discard()
        {
            if (!Directory.Exists(PackPaths.ToAbsolute(ProbeDir))) return;
            AssetDatabase.DeleteAsset(ProbeDir);
        }
    }
}
