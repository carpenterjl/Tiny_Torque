using System.IO;
using AIHWSim.Garage;
using AIHWSim.Pack;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Point an existing asset at a new Blender export: pick a folder, look at
    /// it, confirm.
    ///
    /// <b>Why this is its own path and not "just commit it under that key".</b>
    /// Committing is an AUTHORING flow — it has to ask what the asset is, because
    /// nothing knows yet: its key, its kind, its drag coefficient, which slot a
    /// hat occupies. Replacing asks none of those, because the answers already
    /// exist in the row being replaced and a new mesh has no standing to change
    /// them. Everything this flow fills in, it fills in from the catalogue.
    ///
    /// <b>What is deliberately still refused.</b> A replacement is an overwrite of
    /// a file the project ships, so it is authorised per key and recorded on the
    /// draft (<see cref="AssetStudioDraft.replacesKey"/>) rather than being a mode
    /// the whole tool sits in. Changing the draft's key afterwards revokes it. The
    /// kinds with no registry to join — props and fittings — are refused here for
    /// the same reasons <c>AssetStudioCommit.Refusal</c> refuses them, checked up
    /// front so the answer arrives before the folder picker rather than after.
    ///
    /// <b>The FBX is overwritten in place, and that is the good outcome.</b> Same
    /// path, same <c>.meta</c>, same GUID — so every scene and prefab referencing
    /// the asset keeps referencing it. Deleting and re-adding would churn the GUID
    /// and quietly break them.
    /// </summary>
    public static class AssetStudioReplace
    {
        /// <summary>Whether this row is something a new export could stand in
        /// for: an asset that is actually in the project, under a key, of a kind
        /// that has a registry a manifest can join.</summary>
        public static bool CanReplace(AssetRow row) =>
            row != null && row.HasProject && !string.IsNullOrEmpty(row.key)
            && (row.kind == AssetKind.CarBody || row.kind == AssetKind.Wheel
                || row.kind == AssetKind.Cosmetic);

        /// <summary>Why this row cannot be replaced, or "". The sentence the
        /// button's tooltip shows when it is disabled.</summary>
        public static string Refusal(AssetRow row)
        {
            if (row == null) return "no row.";
            if (!row.HasProject)
                return "this row is an export that is not in the project yet — commit it "
                     + "as a new asset rather than replacing anything.";
            if (string.IsNullOrEmpty(row.key)) return "this row has no key.";
            return row.kind switch
            {
                AssetKind.CarBody or AssetKind.Wheel or AssetKind.Cosmetic => "",
                AssetKind.Prop =>
                    "props are out of Asset Studio's v1 scope: the scenery call sites skip "
                    + "material binding when their token array is empty, so a prop with a "
                    + "manifest would import and never bind.",
                AssetKind.Fitting =>
                    "a fitting (battery, antenna, light bar) has no registry a manifest can "
                    + "join — its key comes from a switch on an int. Replacing one of those "
                    + "meshes is a file swap, not a commit.",
                _ => "this row's kind is " + row.kind + ", which has no replace path.",
            };
        }

        /// <summary>
        /// Run the flow. Returns the draft, ready to preview and commit, or null
        /// if the author backed out at any point.
        ///
        /// Every step that can fail says why and stops; none of them leaves a
        /// half-made draft behind, because the draft is not created until the
        /// export has parsed and the confirmation has been given.
        /// </summary>
        public static AssetStudioDraft Begin(AssetRow row)
        {
            string no = Refusal(row);
            if (!string.IsNullOrEmpty(no))
            {
                EditorUtility.DisplayDialog("Replace mesh", "Cannot replace this: " + no, "OK");
                return null;
            }

            string start = AssetStudio.SourceDir;
            string dir = EditorUtility.OpenFolderPanel(
                $"Replace {row.key} with which export?",
                string.IsNullOrEmpty(start) ? "" : start, "");
            if (string.IsNullOrEmpty(dir)) return null;

            if (!File.Exists(Path.Combine(dir, TtExport.FileName)))
            {
                EditorUtility.DisplayDialog("Replace mesh",
                    $"That folder has no {TtExport.FileName}. Pick the asset's own export "
                    + "folder — the one holding the FBX, not the folder above it.", "OK");
                return null;
            }

            TtExport x = TtExport.Load(dir, out string problem);
            if (x == null)
            {
                EditorUtility.DisplayDialog("Replace mesh",
                    "That export could not be read:\n\n" + problem, "OK");
                return null;
            }

            if (!EditorUtility.DisplayDialog("Replace mesh",
                    Summary(row, x, dir), "Replace", "Cancel"))
                return null;

            return BeginWith(row, dir, x);
        }

        /// <summary>
        /// Everything <see cref="Begin"/> does once the author has chosen a folder
        /// and said yes — split out because a folder picker and a modal dialog are
        /// the two things a headless run cannot answer, and the rest of this flow
        /// is exactly what wants testing.
        /// </summary>
        public static AssetStudioDraft BeginWith(AssetRow row, string dir, TtExport x)
        {
            if (row == null || x == null || string.IsNullOrEmpty(dir)) return null;

            // ---- the draft, pre-filled from the row it is replacing ----------
            var draft = AssetStudioDrafts.Find(row);
            if (draft == null)
            {
                draft = ScriptableObject.CreateInstance<AssetStudioDraft>();
                PackPaths.EnsureFolder(AssetStudio.DraftDir);
                AssetDatabase.CreateAsset(draft, AssetStudioDrafts.PathFor(row.key));
            }

            Undo.RecordObject(draft, "Asset Studio: replace mesh");
            draft.key = row.key;
            draft.kind = row.kind.ToString();
            draft.replacesKey = row.key;
            InheritRowFacts(draft, row);

            // The row now knows where its geometry comes from, which is what lets
            // Sync and the preview treat this pair as one asset. AssetStudioDrafts
            // writes the same folder onto the draft, and AssetStudioCatalog reads
            // it back on the next Refresh, so the pairing outlives this window.
            row.exportDir = dir;
            row.Invalidate();

            AssetStudioDrafts.Sync(draft, row);
            AssetStudioDrafts.Propose(draft, row);
            AssetStudioDrafts.Save(draft);

            string staged = AssetStudioStaging.Stage(x, out string stageProblem);
            if (string.IsNullOrEmpty(staged))
                AssetStudio.Warn($"{row.key}: the draft is ready but the preview could not be "
                    + "staged (" + stageProblem + ").");

            AssetStudio.Log($"{row.key}: ready to replace with \"{x.assetName}\" — "
                + $"{x.materials?.Length ?? 0} material(s), {x.AllObjects().Count} object(s). "
                + "Nothing has been written to Resources/ yet; press Commit when the preview "
                + "looks right.");
            return draft;
        }

        /// <summary>
        /// What the confirmation actually says. It names the FILE being
        /// overwritten, because that is the irreversible half and a dialog that
        /// only says "replace body_patrol?" does not tell you what you are about
        /// to lose.
        /// </summary>
        private static string Summary(AssetRow row, TtExport x, string dir)
        {
            string old = row.fbxAssetPath;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Replace the mesh behind \"{row.key}\".");
            sb.AppendLine();
            sb.AppendLine("Overwrites: " + old);
            sb.AppendLine("With:       " + Path.Combine(dir, x.fbxFile ?? "?"));
            sb.AppendLine();
            sb.AppendLine($"The export declares {x.materials?.Length ?? 0} material(s) and "
                        + $"{x.AllObjects().Count} object(s).");
            sb.AppendLine();
            sb.AppendLine("The catalogue row keeps its identity — the key saved designs name, "
                        + "its label and its aero. The manifest supplies what the new mesh "
                        + "measures and which material sits in which slot.");
            sb.AppendLine();
            sb.AppendLine("Nothing is written until you press Commit. The file above is "
                        + "tracked by git, so this is revertible.");
            return sb.ToString();
        }

        /// <summary>
        /// Copy the catalogue row's authored facts onto the draft, so the commit
        /// pipeline's refusals are answered by the asset being replaced rather
        /// than by a human retyping what the project already knows.
        ///
        /// <b>Only the facts a mesh has no opinion about.</b> Drag coefficient,
        /// label, which slot a hat occupies, how rare it is — these belong to the
        /// design and survive a re-model. Anything the geometry decides
        /// (<c>authorSize</c>, the yaw, the slot bindings) is deliberately absent:
        /// it is measured from the new export a few lines later.
        /// </summary>
        private static void InheritRowFacts(AssetStudioDraft draft, AssetRow row)
        {
            switch (row.kind)
            {
                case AssetKind.CarBody:
                    BodyDef b = BodyCatalog.ById(row.key);
                    if (b == null) break;
                    draft.label = b.label ?? "";
                    draft.cd = b.cd;
                    draft.clA = b.clA;
                    break;

                case AssetKind.Wheel:
                    WheelDef w = WheelCatalog.ById(row.key);
                    if (w == null) break;
                    draft.label = w.label ?? "";
                    draft.garageOffered = w.garageOffered;
                    break;

                case AssetKind.Cosmetic:
                    CosmeticItem c = CosmeticCatalog.ById(row.key);
                    if (c == null) break;
                    draft.label = c.label ?? "";
                    draft.cosmeticSlot = c.slot.ToString();
                    draft.cosmeticRarity = c.rarity.ToString();
                    draft.cosmeticTheme = c.theme.ToString();
                    draft.description = c.description ?? "";
                    break;
            }
        }
    }
}
