using System.Collections.Generic;
using AIHWSim.Pack;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Where drafts live and how they are filled from an export.
    ///
    /// The interesting method here is <see cref="Sync"/>, and the interesting
    /// thing about it is what it does NOT overwrite. A re-export changes
    /// materials, geometry and object lists; it does not change the fact that
    /// Police_Door_L is a detachable door worth 40 hit points in group "door_l".
    /// Re-syncing an asset whose forty damage tags were hand-set and silently
    /// resetting them would make the second export more expensive than the first,
    /// which is the exact way an authoring tool stops being used.
    /// </summary>
    public static class AssetStudioDrafts
    {
        public static string PathFor(string stem) =>
            AssetStudio.DraftDir + "/" + AssetStudio.SafeName(stem) + "_draft.asset";

        /// <summary>The draft for a row, or null. Keyed on the game key when the
        /// asset has one and on the export's asset name before that — the same
        /// two identities the catalogue lists rows under.</summary>
        public static AssetStudioDraft Find(AssetRow row)
        {
            if (row == null) return null;
            foreach (string stem in Stems(row))
            {
                var d = AssetDatabase.LoadAssetAtPath<AssetStudioDraft>(PathFor(stem));
                if (d != null) return d;
            }
            return null;
        }

        private static IEnumerable<string> Stems(AssetRow row)
        {
            if (!string.IsNullOrEmpty(row.key)) yield return row.key;
            if (!string.IsNullOrEmpty(row.label)) yield return row.label;
        }

        /// <summary>
        /// Create the draft for <paramref name="row"/> and fill it from the
        /// export. Returns the existing one if there is one — this is the button
        /// an author presses twice by accident.
        /// </summary>
        public static AssetStudioDraft Create(AssetRow row)
        {
            AssetStudioDraft existing = Find(row);
            if (existing != null) return existing;

            var draft = ScriptableObject.CreateInstance<AssetStudioDraft>();
            draft.key = string.IsNullOrEmpty(row.key)
                ? ProposeKey(row) : row.key;
            draft.kind = row.kind.ToString();

            PackPaths.EnsureFolder(AssetStudio.DraftDir);
            string path = PathFor(string.IsNullOrEmpty(row.key) ? row.label : row.key);
            AssetDatabase.CreateAsset(draft, path);

            Sync(draft, row);
            AssetDatabase.SaveAssets();
            AssetStudio.Log("draft created at " + path);
            return draft;
        }

        /// <summary>
        /// A first guess at the game key for an un-imported export: the kind's
        /// prefix plus a lowercased asset name, so POLICE offers itself as
        /// "body_police" rather than as "POLICE".
        ///
        /// A guess, and editable — <c>CarVehicle.BodyMeshKey</c> only ever builds
        /// "body_*" and <c>BuildWheelViz</c> only ever builds "wheel_*", so the
        /// prefix is not a convention but a requirement, while the rest is the
        /// author's to name.
        /// </summary>
        public static string ProposeKey(AssetRow row)
        {
            string stem = AssetStudio.SafeName(row.label).ToLowerInvariant();
            return row.kind switch
            {
                AssetKind.CarBody => stem.StartsWith("body_") ? stem : "body_" + stem,
                AssetKind.Wheel => stem.StartsWith("wheel_") ? stem : "wheel_" + stem,
                _ => stem,
            };
        }

        /// <summary>
        /// Re-read the export into the draft, keeping every authored decision that
        /// still has something to attach to.
        ///
        /// Kept: damage role / health / group / slot mapping / slotsVerified, by
        /// OBJECT NAME. Kept: paintChannel and any hand-edited material value, by
        /// MATERIAL NAME. Replaced: everything the exporter is the authority on —
        /// which objects exist, which maps a material has, the source stamps.
        /// Anything the export no longer mentions is REPORTED, not deleted in
        /// silence, because "the door is gone" and "the door was renamed" look
        /// identical from here and only the author can tell them apart.
        /// </summary>
        public static void Sync(AssetStudioDraft draft, AssetRow row)
        {
            if (draft == null || row == null) return;
            TtExport x = row.Export;
            if (x == null) return;

            Undo.RecordObject(draft, "Asset Studio: sync from export");

            draft.sourceAssetName = x.assetName ?? "";
            draft.sourceBlend = x.sourceBlend ?? "";
            draft.sourceExportedAtUtc = x.exportedAtUtc ?? "";
            draft.sourceDir = row.exportDir ?? "";
            draft.notesGeometryFixes = x.geometryFixes?.Length ?? 0;
            draft.notesTextureWarnings = x.textureWarnings?.Length ?? 0;

            // ---- materials --------------------------------------------------
            var keptMats = new Dictionary<string, DraftMaterial>();
            foreach (DraftMaterial m in draft.materials)
                if (m != null && !string.IsNullOrEmpty(m.name)) keptMats[m.name] = m;

            var mats = new List<DraftMaterial>();
            foreach (TtMaterial tm in x.materials)
            {
                if (tm == null || string.IsNullOrEmpty(tm.name)) continue;
                keptMats.TryGetValue(tm.name, out DraftMaterial d);
                bool isNew = d == null;
                d ??= new DraftMaterial { name = tm.name };

                // The exporter owns these three: what the material IS and where
                // its pictures are.
                d.baked = tm.baked;
                d.mapAlbedo = tm.maps?.baseColor ?? "";
                d.mapMetallicSmoothness = tm.maps?.metallicSmoothness ?? "";
                d.mapEmission = tm.maps?.emission ?? "";
                d.mapNormal = tm.maps?.normal ?? "";

                // The flat values are only meaningful when the material is not
                // baked, and are only filled on FIRST sight — after that they are
                // the author's, and a re-export must not undo a hand-tuned
                // smoothness.
                if (isNew)
                {
                    if (!tm.baked && tm.values != null)
                    {
                        d.rgb = ColorOf(tm.values.baseColor);
                        d.metallic = Mathf.Clamp01(tm.values.metallic);
                        d.smoothness = tm.values.Smoothness;
                        d.alpha = tm.values.alpha <= 0f ? 1f : Mathf.Clamp01(tm.values.alpha);
                        d.emissionStrength = Mathf.Max(0f, tm.values.emissionStrength);
                        d.emission = d.emissionStrength > 0f ? d.rgb : Color.black;
                    }
                    // The decision taken with the user: a baked material is
                    // offered as the paint channel by default, so bodyColor
                    // multiplies the livery instead of being refused.
                    d.paintChannel = tm.baked;
                }
                mats.Add(d);
                keptMats.Remove(tm.name);
            }
            draft.materials = mats;

            // ---- objects ----------------------------------------------------
            var keptObjs = new Dictionary<string, DraftObject>();
            foreach (DraftObject o in draft.objects)
                if (o != null && !string.IsNullOrEmpty(o.name)) keptObjs[o.name] = o;

            Dictionary<string, int> slotCounts = SlotCounts(row);
            var objs = new List<DraftObject>();
            foreach (string name in x.AllObjects())
            {
                keptObjs.TryGetValue(name, out DraftObject d);
                bool isNew = d == null;
                d ??= new DraftObject { name = name };

                List<TtMaterial> on = x.MaterialsOn(name);
                int count = slotCounts.TryGetValue(name, out int c) ? c
                          : Mathf.Max(1, on.Count);

                if (isNew || d.slots.Count != count)
                {
                    // Proposal, not measurement — see DraftObject.slotsVerified.
                    var slots = new List<string>(count);
                    for (int i = 0; i < count; i++)
                        slots.Add(i < on.Count ? on[i].name : "");
                    d.slots = slots;
                    d.slotsVerified = false;
                }
                objs.Add(d);
                keptObjs.Remove(name);
            }
            draft.objects = objs;

            EditorUtility.SetDirty(draft);

            foreach (string orphan in keptObjs.Keys)
                AssetStudio.Warn($"{draft.key}: the export no longer names \"{orphan}\" — "
                    + "its damage tags and slot mapping have been dropped. If it was "
                    + "RENAMED rather than removed, undo this sync, rename it back in "
                    + "Blender, and re-export.");
            foreach (string orphan in keptMats.Keys)
                AssetStudio.Warn($"{draft.key}: the export no longer has material "
                    + $"\"{orphan}\"; any slot still pointing at it now dangles.");
        }

        /// <summary>
        /// Submesh slot count per object, read off the imported FBX — the only
        /// place it exists. The staged copy counts too: it is the same file under
        /// the same import settings.
        ///
        /// Only the COUNT is readable. <c>PartModelPostprocessor</c> forces
        /// <c>materialImportMode = None</c>, so every slot arrives null and which
        /// material belongs in which is not a question the import can answer.
        /// </summary>
        private static Dictionary<string, int> SlotCounts(AssetRow row)
        {
            var counts = new Dictionary<string, int>();
            string path = row.HasProject ? row.fbxAssetPath
                : row.Export != null && AssetStudioStaging.IsStaged(row.Export)
                    ? AssetStudioStaging.PathFor(row.Export) : "";
            if (string.IsNullOrEmpty(path)) return counts;

            var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (src == null) return counts;
            foreach (Renderer r in src.GetComponentsInChildren<Renderer>(true))
                counts[r.gameObject.name] = Mathf.Max(1, r.sharedMaterials?.Length ?? 1);
            return counts;
        }

        private static Color ColorOf(float[] rgb) =>
            rgb != null && rgb.Length >= 3
                ? new Color(rgb[0], rgb[1], rgb[2])
                : Color.white;

        // ==================== the geometry proposal ====================

        /// <summary>
        /// Fill <c>authorSize</c> / <c>authorYawDeg</c> from the export: one
        /// uniform factor to the kind's target length, and a quarter turn when the
        /// long axis is X.
        ///
        /// <b>Uniform, and that is the whole point.</b> The old exporter built
        /// every arcade shell by scaling to length 0.420 with a single factor,
        /// which is why <c>PartModelValidator</c> pins those bodies' length and
        /// leaves their width free. A per-axis fit to
        /// <c>CarVehicle.BodyMeshAuthorSize</c> would change proportions the
        /// original pipeline never touched, because that constant is a nominal
        /// divisor and not a measurement of any shell.
        /// </summary>
        public static bool Propose(AssetStudioDraft draft, AssetRow row)
        {
            TtExport x = row?.Export;
            if (draft == null || x == null || x.UnityDims == Vector3.zero) return false;

            // The DRAFT's kind, not the row's. The row's is inferred from a key
            // and a Resources folder, and an export that has neither is
            // Unassigned — so reading it here silently scaled every wheel
            // committed from a fresh export to a body's 0.420 m length instead of
            // to a 66 mm tyre. The draft is where a human said what this is.
            AssetKind kind = draft.Kind != AssetKind.Unassigned ? draft.Kind : row.kind;
            float target = kind == AssetKind.Wheel
                ? AssetStudioPreview.AuthorRadiusFor(draft.key) * 2f
                : CarVehicle.BodyMeshAuthorSize.z;

            float scale = x.UniformScaleTo(target);
            float yaw = x.LengthRunsAlongX ? -90f : 0f;

            Undo.RecordObject(draft, "Asset Studio: propose scale and yaw");
            draft.authorScale = scale;
            draft.authorYawDeg = yaw;
            draft.authorSize = x.AuthorSizeAfter(scale, yaw);
            EditorUtility.SetDirty(draft);
            return true;
        }

        /// <summary>Persist now. Called on focus loss and from the Save button —
        /// SetDirty alone leaves the draft in memory, and "restart the editor,
        /// draft intact" is the property this milestone claims.</summary>
        public static void Save(AssetStudioDraft draft)
        {
            if (draft == null) return;
            EditorUtility.SetDirty(draft);
            AssetDatabase.SaveAssetIfDirty(draft);
        }
    }
}
