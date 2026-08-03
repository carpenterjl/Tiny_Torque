using System.Collections.Generic;
using System.IO;
using AIHWSim.Pack;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>How a committed asset stands against the export it came from.</summary>
    public enum CommitState
    {
        /// <summary>Nothing under <c>Resources/</c> yet.</summary>
        NotCommitted,
        /// <summary>The committed copy matches the export byte for byte.</summary>
        Current,
        /// <summary>Blender has moved on: the export's FBX no longer hashes to
        /// what the manifest recorded. Re-commit to take the new geometry.</summary>
        SourceDrifted,
        /// <summary>Somebody edited the copy under <c>Resources/</c> — the change
        /// a re-commit would discard without asking.</summary>
        ProjectEdited,
        /// <summary>Committed, and the export folder is no longer reachable. Not
        /// a fault: the asset is complete and the source lives on another
        /// machine.</summary>
        SourceGone,
    }

    /// <summary>
    /// The step that writes: an authored draft becomes an asset the game can
    /// load.
    ///
    /// <b>Everything before this was reversible by deleting a scratch folder.</b>
    /// This is the one place Asset Studio touches <c>Resources/</c>, so every
    /// decision here is about being repeatable rather than clever — MD5-guarded
    /// copies so an unchanged file is not rewritten (a rewrite reimports, and a
    /// reimport churns the <c>.meta</c> GUID every reference in the project is
    /// keyed on), a manifest written from the draft rather than patched in place,
    /// and a second commit that reports <c>0 copied, N already current</c>.
    ///
    /// <b>Import order is load-bearing.</b> <c>PartModelPostprocessor</c> reads
    /// the sibling manifest during the model's own import and, in Verbatim mode,
    /// looks up each <c>.mat</c> through <c>LoadAssetAtPath</c> — which only sees
    /// assets Unity has ALREADY imported. So the sidecar folder is imported
    /// first and the FBX second, as two explicit calls. That is also why this
    /// pipeline does not wrap itself in <c>StartAssetEditing</c> the way the pack
    /// steps do: batching defers every import to the end, which is precisely the
    /// ordering the postprocessor cannot survive. The copies are plain file
    /// writes; the imports are ordered by hand.
    ///
    /// <b>What it does not do is decide anything.</b> Every number it writes came
    /// off the draft or off the imported prefab; the gates below refuse rather
    /// than filling a blank in, because a drag coefficient chosen by a fallback
    /// constant is a car nobody chose.
    /// </summary>
    public static class AssetStudioCommit
    {
        // ==================== destinations ====================

        /// <summary>Which <c>Resources/</c> root a kind commits into. Props are
        /// out of v1 scope and land in TrackProps only so the path arithmetic has
        /// an answer; the gate refuses them before this is reached.</summary>
        public static string RootFor(AssetKind kind) => kind switch
        {
            AssetKind.Cosmetic => AssetStudio.CosmeticsRoot,
            AssetKind.Prop => AssetStudio.TrackPropsRoot,
            _ => AssetStudio.PartModelsRoot,
        };

        /// <summary>Where the FBX lands. The stem IS the key, because
        /// <c>Resources.Load</c> is handed the stem alone.</summary>
        public static string FbxPathFor(AssetKind kind, string key) =>
            RootFor(kind) + "/" + key + ".fbx";

        /// <summary>
        /// The per-key sidecar folder: textures, and in Verbatim mode a
        /// <c>Materials/</c> beside them.
        ///
        /// Per key rather than one shared texture folder, and that is not tidiness
        /// — R4 measured the alternative. A material Unity replaces gets named
        /// after its diffuse TEXTURE, so two assets whose exporter both wrote a
        /// <c>BaseColor</c> map collide in a shared folder and one silently wins.
        /// </summary>
        public static string AssetDirFor(AssetKind kind, string key) =>
            RootFor(kind) + "/" + key;

        /// <summary>The same folder as a <c>Resources.Load</c> prefix —
        /// "PartModels/body_police". What a manifest's map paths are relative
        /// to.</summary>
        public static string ResourceDirFor(AssetKind kind, string key) =>
            AssetStudio.RootName(RootFor(kind)) + "/" + key;

        /// <summary>Where a Verbatim asset's own <c>.mat</c> files go. Must agree
        /// with <c>PartModelPostprocessor.MaterialsDir</c>, which is what looks
        /// them up at import; asserted by <c>[AST]</c> rather than assumed.</summary>
        public static string MaterialsDirFor(AssetKind kind, string key) =>
            AssetDirFor(kind, key) + "/Materials";

        // ==================== state ====================

        /// <summary>The manifest a key already carries, read off disk (not
        /// through <c>Resources.Load</c>, which would answer from a cache this
        /// pipeline is about to invalidate). Null when nothing is committed.</summary>
        public static AssetManifest CommittedManifest(AssetKind kind, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string p = AssetStudio.ManifestPathFor(FbxPathFor(kind, key));
            string abs = PackPaths.ToAbsolute(p);
            return File.Exists(abs) ? AssetManifests.FromJson(File.ReadAllText(abs), p) : null;
        }

        /// <summary>
        /// How a key stands against the export it was committed from.
        ///
        /// Two hashes, two questions. <c>source.fbxMd5</c> is what the EXPORT
        /// hashed when it was committed, so a mismatch there means Blender moved
        /// on; <c>committedHash</c> is what the copy hashed, so a mismatch there
        /// means the project's copy was edited and a re-commit would throw that
        /// edit away. They are equal the instant a commit finishes and answer
        /// different questions forever after, which is why both are recorded.
        ///
        /// <b><c>exportedAtUtc</c> is recorded and deliberately not part of the
        /// verdict.</b> A re-export with identical geometry moves the timestamp
        /// and changes nothing, and calling that "drifted" would train an author
        /// to ignore the word. The hash is the question; the timestamp is what
        /// tells them WHEN, in the window, once the hash has said IF.
        /// </summary>
        public static CommitState StateOf(AssetKind kind, string key, TtExport export)
        {
            AssetManifest man = CommittedManifest(kind, key);
            if (man == null) return CommitState.NotCommitted;

            string fbxAbs = PackPaths.ToAbsolute(FbxPathFor(kind, key));
            if (File.Exists(fbxAbs) && !string.IsNullOrEmpty(man.committedHash)
                && PackHash.Of(fbxAbs) != man.committedHash)
                return CommitState.ProjectEdited;

            string srcFbx = export?.FbxPath ?? "";
            if (string.IsNullOrEmpty(srcFbx) || !File.Exists(srcFbx))
                return CommitState.SourceGone;

            return PackHash.Of(srcFbx) == man.source?.fbxMd5
                ? CommitState.Current : CommitState.SourceDrifted;
        }

        public static string Describe(CommitState s) => s switch
        {
            CommitState.Current => "Committed and current",
            CommitState.SourceDrifted => "Drifted — the export has changed since it was committed",
            CommitState.ProjectEdited => "Edited in project — the copy under Resources/ no "
                                       + "longer matches what was committed",
            CommitState.SourceGone => "Committed; the export folder is not on this machine",
            _ => "Not committed",
        };

        // ==================== the pipeline ====================

        /// <summary>What one commit did, for the log line and the window.</summary>
        public sealed class Result
        {
            public string key = "";
            public bool ok;
            public string problem = "";
            public int filesCopied, filesCurrent;
            public int objects, slots, unmapped, orphans;
            public Vector3 measured;
            public bool overridden;
            public string overrideReason = "";
        }

        /// <summary>
        /// Commit <paramref name="draft"/>. Returns a filled
        /// <see cref="Result"/>; <c>ok == false</c> carries the sentence that
        /// says why, and nothing has been written.
        ///
        /// The gates come first and all of them, so an author fixing three
        /// problems finds out about three problems.
        /// </summary>
        public static Result Commit(AssetStudioDraft draft, AssetRow row)
        {
            var res = new Result { key = draft != null ? draft.key : "?" };

            string refusal = Refusal(draft, row);
            if (!string.IsNullOrEmpty(refusal)) { res.problem = refusal; return res; }

            AssetKind kind = draft.Kind;
            string key = draft.key;
            TtExport x = row.Export;
            res.overridden = draft.verificationOverridden;
            res.overrideReason = draft.overrideReason;

            string fbxDst = FbxPathFor(kind, key);
            string dir = AssetDirFor(kind, key);

            // ---- 1. copy ----------------------------------------------------
            PackPaths.EnsureFolder(RootFor(kind));
            PackPaths.EnsureFolder(dir);

            // Everything that has to be imported BEFORE the model, collected as
            // it is copied. Explicit paths rather than one recursive import of
            // the folder: the ordering below is the whole point of this step and
            // "import that directory" leaves the order to Unity.
            var sidecar = new List<string>();

            if (!CopyGuarded(x.FbxPath, fbxDst, res, out string copyProblem))
            { res.problem = copyProblem; return res; }

            var wanted = new List<string>();
            foreach (DraftMaterial m in draft.materials)
            {
                if (m == null) continue;
                foreach (string f in new[]
                         { m.mapAlbedo, m.mapMetallicSmoothness, m.mapEmission, m.mapNormal })
                    if (!string.IsNullOrEmpty(f) && !wanted.Contains(f)) wanted.Add(f);
            }
            foreach (string file in wanted)
            {
                string src = FindTexture(x, file);
                if (string.IsNullOrEmpty(src))
                {
                    // Named, not fatal. A map the export lists and does not ship
                    // is the exporter's textureWarnings case; the material still
                    // renders off its flat values, and the manifest still names
                    // the path so re-committing a fixed export lands it.
                    AssetStudio.Warn($"{key}: texture \"{file}\" is not in the export folder — "
                        + "the manifest still names it, so re-commit once the export has it.");
                    continue;
                }
                string dst = dir + "/" + Path.GetFileName(src);
                if (!CopyGuarded(src, dst, res, out copyProblem))
                { res.problem = copyProblem; return res; }
                sidecar.Add(dst);
            }

            if (draft.materialMode == DraftMaterialModes.Verbatim
                && !CopyVerbatimMaterials(draft, x, kind, sidecar, res, out copyProblem))
            { res.problem = copyProblem; return res; }

            // ---- 2. manifest, then the two-phase import ---------------------
            // The manifest has to exist BEFORE the model is imported: the
            // postprocessor reads it during OnPreprocessModel to decide whether
            // to keep the FBX's own materials, and there is no second chance
            // inside one import.
            //
            // But it is written here ONLY when that decision would otherwise be
            // missing or wrong, because at this point the manifest still carries
            // the draft's PROPOSED slot lists and size — numbers step 3 is about
            // to replace off the import. Writing it unconditionally means every
            // commit rewrites the file twice, so a re-commit that changes nothing
            // still churns it, and "commit twice, nothing moves" stops being
            // true. The authoritative write is the one after the measurement.
            AssetManifest man = BuildManifest(draft, x, kind);
            string manifestPath = AssetStudio.ManifestPathFor(fbxDst);
            AssetManifest onDisk = CommittedManifest(kind, key);
            // Skipping it costs nothing: everything step 3 fills in is a function
            // of the draft and the import, so the write below reproduces the
            // existing file byte for byte whenever neither has moved.
            if (onDisk == null || onDisk.materialMode != man.materialMode)
                WriteIfDifferent(manifestPath, JsonUtility.ToJson(man, true), res);

            // Everything above went in through File.Copy, which the AssetDatabase
            // knows nothing about until it is told. Refresh first so the ordered
            // pass below is re-importing assets that exist rather than importing
            // them for the first time in whatever order Refresh chose.
            AssetDatabase.Refresh();

            // Textures and materials first, the model second. R4 measured why:
            // the postprocessor resolves a Verbatim asset's materials through
            // LoadAssetAtPath, which only ever sees assets Unity has ALREADY
            // imported, so a model imported alongside its materials finds none of
            // them. The manifest itself needs no such ordering — the postprocessor
            // reads it off DISK, which is exactly why it is a sibling file.
            foreach (string p in sidecar)
                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(fbxDst, ImportAssetOptions.ForceUpdate);

            // ---- 3. read the truth back off the import -----------------------
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxDst);
            if (prefab == null)
            {
                res.problem = "the FBX copied but did not import — " + fbxDst;
                return res;
            }
            Measure(man, draft, prefab, res);
            man.committedHash = PackHash.Of(PackPaths.ToAbsolute(fbxDst));

            // The measured pass rewrites the manifest, and the rewrite is what
            // makes slots[] and authorSize facts about the imported asset rather
            // than about the draft's proposal. Guarded, so a re-commit of an
            // unchanged asset leaves the file's timestamp alone too.
            WriteIfDifferent(manifestPath, JsonUtility.ToJson(man, true), res);
            AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ---- 4. let the running session see it --------------------------
            ResetCaches();

            res.ok = true;
            return res;
        }

        /// <summary>
        /// Every reason this draft cannot be committed, or "".
        ///
        /// All of them at once on purpose. A gate that stops at the first
        /// complaint turns one fix-and-retry into four.
        /// </summary>
        public static string Refusal(AssetStudioDraft draft, AssetRow row)
        {
            if (draft == null) return "no draft.";
            if (row == null) return "no catalogue row.";
            TtExport x = row.Export;
            if (x == null) return "no readable export behind this row: " + row.problem;

            var why = new List<string>();
            AssetKind kind = draft.Kind;
            string key = draft.key ?? "";

            if (string.IsNullOrWhiteSpace(key)) why.Add("the key is empty");
            else if (key != AssetStudio.SafeName(key))
                why.Add($"the key \"{key}\" is not a legal file stem");

            switch (kind)
            {
                case AssetKind.CarBody when !key.StartsWith("body_"):
                    // Not a convention: CarVehicle only ever builds "body_" + …,
                    // so a body committed under another stem is unreachable.
                    why.Add("a car body's key must start with \"body_\"");
                    break;
                case AssetKind.Wheel when !key.StartsWith("wheel_"):
                    why.Add("a wheel's key must start with \"wheel_\"");
                    break;
                case AssetKind.Unassigned:
                    why.Add("the kind is still Unassigned — pick what this asset IS");
                    break;
                case AssetKind.Prop:
                    why.Add("props are out of Asset Studio's v1 scope: the scenery call "
                          + "sites skip material binding entirely when their token array "
                          + "is empty, so a prop with a manifest would import and never "
                          + "bind (see PartMeshLibrary.TryInstantiate)");
                    break;
            }

            // Overwriting a shipped asset is always a mistake, and a QUIET one:
            // BodyCatalog's seed rows win over any later addition of the same
            // key, so a committed body_patrol would replace the car's mesh while
            // the row describing it stayed the old one.
            if (!string.IsNullOrWhiteSpace(key))
            {
                string dst = FbxPathFor(kind, key);
                if (File.Exists(PackPaths.ToAbsolute(dst)) && CommittedManifest(kind, key) == null)
                    why.Add($"{dst} already exists and was not committed by Asset Studio — "
                          + "it is one of the assets the project ships. Choose another key");
            }

            if (x.verification != null && !x.verification.passed && !draft.verificationOverridden)
                why.Add($"the exporter's verification FAILED ({x.verification.IssueCount} "
                      + "issue(s)). Fix the export, or tick the override and write down why");
            if (draft.verificationOverridden && string.IsNullOrWhiteSpace(draft.overrideReason))
                why.Add("the verification override needs a reason — it is written into the "
                      + "manifest and printed by every [AST] run");

            int dangling = draft.DanglingSlots();
            if (dangling > 0)
                why.Add($"{dangling} slot(s) name a material this draft does not have");
            int unverified = draft.UnverifiedSlots();
            if (unverified > 0)
                why.Add($"{unverified} multi-slot object(s) still carry the PROPOSED slot "
                      + "order. The import gives the slot count and nothing else, so which "
                      + "material sits in which slot is a guess until someone looks");

            if (kind == AssetKind.CarBody && draft.cd < 0f)
                why.Add("no drag coefficient. A body needs one and it cannot be measured off "
                      + "geometry; 0.15 is a teardrop, 1.2 a flat plate broadside");
            if (draft.authorScale <= 0f)
                why.Add("the uniform scale is zero or negative — press \"Propose\"");
            if (Mathf.Abs(Mathf.Repeat(draft.authorYawDeg, 90f)) > 0.01f)
                why.Add($"authorYawDeg {draft.authorYawDeg} is not a multiple of 90 — a "
                      + "bounding box does not survive anything else");

            if (kind == AssetKind.Cosmetic)
            {
                if (!System.Enum.TryParse(draft.cosmeticSlot, out Garage.CosmeticSlot _))
                    why.Add($"\"{draft.cosmeticSlot}\" is not a cosmetic slot");
                if (!System.Enum.TryParse(draft.cosmeticRarity, out Garage.Rarity _))
                    why.Add($"\"{draft.cosmeticRarity}\" is not a rarity");
                if (!System.Enum.TryParse(draft.cosmeticTheme, out Garage.CosmeticTheme _))
                    why.Add($"\"{draft.cosmeticTheme}\" is not a theme");
            }

            return why.Count == 0 ? "" : string.Join("; ", why) + ".";
        }

        // ==================== the pieces ====================

        /// <summary>
        /// The draft as the manifest the game reads. A projection, not a
        /// transformation — the one thing that changes shape is the texture
        /// paths, which are bare export file names on the draft and
        /// <c>Resources.Load</c> paths here, because that is the difference
        /// between a file on this machine and an asset in the project.
        /// </summary>
        public static AssetManifest BuildManifest(AssetStudioDraft draft, TtExport x, AssetKind kind)
        {
            string resDir = ResourceDirFor(kind, draft.key);
            string Map(string file) =>
                string.IsNullOrEmpty(file) ? "" : resDir + "/" + Path.GetFileNameWithoutExtension(file);

            var mats = new List<AssetMaterialDef>(draft.materials.Count);
            foreach (DraftMaterial m in draft.materials)
            {
                if (m == null) continue;
                AssetMaterialDef d = m.ToDef();
                d.mapAlbedo = Map(d.mapAlbedo);
                d.mapMetallicSmoothness = Map(d.mapMetallicSmoothness);
                d.mapEmission = Map(d.mapEmission);
                d.mapNormal = Map(d.mapNormal);
                mats.Add(d);
            }

            var objs = new List<AssetObjectDef>(draft.objects.Count);
            foreach (DraftObject o in draft.objects)
            {
                if (o == null) continue;
                objs.Add(new AssetObjectDef
                {
                    name = o.name,
                    slots = o.slots.ToArray(),
                    role = o.role,
                    healthHp = o.healthHp,
                    group = o.group,
                });
            }

            string srcFbx = x?.FbxPath ?? "";
            return new AssetManifest
            {
                schema = draft.schema,
                key = draft.key,
                kind = kind.ToString(),
                label = draft.label,
                materialMode = draft.materialMode,
                source = new AssetSourceDef
                {
                    assetName = draft.sourceAssetName,
                    sourceBlend = draft.sourceBlend,
                    exportedAtUtc = draft.sourceExportedAtUtc,
                    fbxMd5 = File.Exists(srcFbx) ? PackHash.Of(srcFbx) : "",
                },
                authorScale = draft.authorScale,
                authorYawDeg = draft.authorYawDeg,
                authorSize = new[] { draft.authorSize.x, draft.authorSize.y, draft.authorSize.z },
                spec = SpecFor(draft, kind),
                materials = mats.ToArray(),
                objects = objs.ToArray(),
                notes = new AssetNotesDef
                {
                    geometryFixes = draft.notesGeometryFixes,
                    textureWarnings = draft.notesTextureWarnings,
                    verificationOverridden = draft.verificationOverridden,
                    overrideReason = draft.overrideReason,
                },
                vehicle = new AssetVehicleDef
                {
                    cd = draft.cd, clA = draft.clA, garageOffered = draft.garageOffered,
                },
                cosmetic = new AssetCosmeticDef
                {
                    slot = kind == AssetKind.Cosmetic ? draft.cosmeticSlot : "",
                    rarity = draft.cosmeticRarity,
                    theme = draft.cosmeticTheme,
                    description = draft.description,
                },
            };
        }

        /// <summary>
        /// The geometry contract this asset will be validated against.
        ///
        /// <b>One axis pinned, and it is the one the pipeline set.</b> A body is
        /// uniformly scaled to length 0.420, so length is a promise and width and
        /// height are consequences — pinning them would fail every correctly
        /// proportioned car, which is exactly the note
        /// <c>PartModelValidator</c> already carries beside its own body rows.
        /// </summary>
        private static AssetSpecDef SpecFor(AssetStudioDraft draft, AssetKind kind)
        {
            var s = new AssetSpecDef { specSource = "measured" };
            if (kind == AssetKind.CarBody) s.z = draft.authorSize.z;
            else if (kind == AssetKind.Wheel) { s.y = draft.authorSize.y; s.z = draft.authorSize.z; }
            return s;
        }

        /// <summary>
        /// Replace the draft's proposed slot lists with the imported prefab's
        /// real ones, and the proposed size with the measured one.
        ///
        /// <b>This is why the commit imports before it finishes writing.</b> The
        /// slot COUNT exists only on the imported mesh — <c>export.json</c> lists
        /// a material's objects in file order, not slot order — so a manifest
        /// whose slot arrays came from the draft alone would be a proposal
        /// wearing the manifest's authority. A draft entry that is too short is
        /// padded with empty ("leave as imported"), one that is too long is
        /// truncated, and both are counted so the log says it happened.
        /// </summary>
        private static void Measure(AssetManifest man, AssetStudioDraft draft,
                                    GameObject prefab, Result res)
        {
            var byName = new Dictionary<string, AssetObjectDef>();
            foreach (AssetObjectDef o in man.objects)
                if (o != null && !string.IsNullOrEmpty(o.name)) byName[o.name] = o;

            var final = new List<AssetObjectDef>();
            var seen = new HashSet<string>();
            foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name;
                int count = Mathf.Max(1, r.sharedMaterials?.Length ?? 1);
                seen.Add(n);

                if (!byName.TryGetValue(n, out AssetObjectDef o))
                {
                    // In the mesh, not in the draft. It gets an entry with empty
                    // slots — "leave as imported" — rather than being dropped,
                    // because the binder reports an object it has no entry for
                    // and cannot report one that was never written down.
                    o = new AssetObjectDef { name = n, role = DraftRoles.Structural };
                    res.unmapped++;
                }
                var slots = new string[count];
                for (int i = 0; i < count; i++) slots[i] = o.SlotAt(i) ?? "";
                o.slots = slots;
                final.Add(o);
                res.slots += count;
            }
            foreach (string name in byName.Keys)
                if (!seen.Contains(name)) res.orphans++;

            man.objects = final.ToArray();
            res.objects = final.Count;

            // Measured off the prefab and corrected the way the game will load
            // it, which is the independent check: export.json's own
            // unityDimensions never enter this number.
            Bounds b = PartVisualFactory.LocalRendererBounds(prefab.transform, prefab.transform);
            Vector3 size = b.size * draft.authorScale;
            int quarter = Mathf.RoundToInt(Mathf.Repeat(draft.authorYawDeg, 360f) / 90f) & 3;
            if (quarter == 1 || quarter == 3) size = new Vector3(size.z, size.y, size.x);
            man.authorSize = new[] { size.x, size.y, size.z };
            res.measured = size;

            if (draft.Kind == AssetKind.CarBody) man.spec.z = size.z;
            else if (draft.Kind == AssetKind.Wheel) { man.spec.y = size.y; man.spec.z = size.z; }
        }

        /// <summary>
        /// Copy a Verbatim asset's own <c>.mat</c> files and the textures they
        /// reference, WITH their <c>.meta</c>.
        ///
        /// The <c>.meta</c> is the point: a material references its textures by
        /// GUID, so copying the material and letting Unity mint fresh GUIDs for
        /// the pictures leaves every map dangling. R4 measured the other half of
        /// that trap — committing one export under two keys duplicates the GUIDs,
        /// Unity silently reassigns one side, and the result looks exactly like an
        /// importer bug. So an incoming GUID that is already spoken for by a
        /// different path is a refusal, by name.
        /// </summary>
        private static bool CopyVerbatimMaterials(AssetStudioDraft draft, TtExport x,
            AssetKind kind, List<string> sidecar, Result res, out string problem)
        {
            problem = "";
            string srcDir = Path.Combine(x.dir ?? "", "Materials");
            if (!Directory.Exists(srcDir))
            {
                problem = "Verbatim mode, but the export has no Materials/ folder. "
                        + "Switch to Manifest mode, which rebuilds materials from the "
                        + "exported numbers.";
                return false;
            }

            string dstDir = MaterialsDirFor(kind, draft.key);
            PackPaths.EnsureFolder(dstDir);

            foreach (string src in Directory.GetFiles(srcDir, "*.mat"))
            {
                string dst = dstDir + "/" + Path.GetFileName(src);
                if (!CopyGuarded(src, dst, res, out problem)) return false;
                if (!CopyMeta(src, dst, res, out problem)) return false;
                sidecar.Add(dst);
            }
            // The textures a .mat points at are already in the sidecar folder
            // beside it, copied above — but only the ones a DRAFT MATERIAL names.
            // Verbatim materials reference whatever they reference, so the whole
            // Textures/ folder comes over with its .meta.
            string texDir = Path.Combine(x.dir ?? "", "Textures");
            if (!Directory.Exists(texDir)) return true;
            foreach (string src in Directory.GetFiles(texDir))
            {
                if (src.EndsWith(".meta")) continue;
                string dst = AssetDirFor(kind, draft.key) + "/" + Path.GetFileName(src);
                if (!CopyGuarded(src, dst, res, out problem)) return false;
                if (!CopyMeta(src, dst, res, out problem)) return false;
                if (!sidecar.Contains(dst)) sidecar.Add(dst);
            }
            return true;
        }

        private static bool CopyMeta(string src, string dst, Result res, out string problem)
        {
            problem = "";
            string srcMeta = src + ".meta";
            if (!File.Exists(srcMeta)) return true;   // Unity will mint one

            string guid = GuidIn(srcMeta);
            if (!string.IsNullOrEmpty(guid))
            {
                string owner = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(owner)
                    && owner.Replace('\\', '/') != dst.Replace('\\', '/'))
                {
                    problem = $"{Path.GetFileName(src)} carries GUID {guid}, which this "
                            + $"project has already given to {owner}. Committing it would "
                            + "make Unity silently reassign one of the two and every texture "
                            + "link on the loser would come out unresolved. Commit this "
                            + "export under ONE key.";
                    return false;
                }
            }
            return CopyGuarded(srcMeta, dst + ".meta", res, out problem);
        }

        private static string GuidIn(string metaPath)
        {
            foreach (string line in File.ReadLines(metaPath))
                if (line.StartsWith("guid:")) return line.Substring(5).Trim();
            return "";
        }

        /// <summary>The exporter writes bare file names; the files live in
        /// <c>Textures/</c>. A hand-flattened export is accepted too, the same way
        /// the preview's loader accepts one.</summary>
        private static string FindTexture(TtExport x, string file)
        {
            if (string.IsNullOrEmpty(x?.dir) || string.IsNullOrEmpty(file)) return "";
            string p = Path.Combine(x.dir, "Textures", file);
            if (File.Exists(p)) return p;
            p = Path.Combine(x.dir, file);
            return File.Exists(p) ? p : "";
        }

        /// <summary>
        /// Copy only when the bytes differ.
        ///
        /// Rewriting an identical file reimports it, and a reimport churns the
        /// <c>.meta</c> GUID that every prefab, material and scene referencing it
        /// is keyed on. It is also what makes "commit twice" report
        /// <c>0 copied, N already current</c> instead of looking like a second
        /// commit did something.
        /// </summary>
        private static bool CopyGuarded(string srcAbs, string dstProjectRel,
                                        Result res, out string problem)
        {
            problem = "";
            string dstAbs = PackPaths.ToAbsolute(dstProjectRel);
            if (File.Exists(dstAbs) && SameBytes(srcAbs, dstAbs)) { res.filesCurrent++; return true; }
            try
            {
                PackPaths.EnsureFolder(Path.GetDirectoryName(dstProjectRel)?.Replace('\\', '/'));
                File.Copy(srcAbs, dstAbs, true);
            }
            catch (System.Exception e)
            {
                problem = $"could not copy {Path.GetFileName(srcAbs)} -> {dstProjectRel}: {e.Message}";
                return false;
            }
            res.filesCopied++;
            return true;
        }

        private static void WriteIfDifferent(string projectRel, string text, Result res)
        {
            string abs = PackPaths.ToAbsolute(projectRel);
            if (File.Exists(abs) && File.ReadAllText(abs) == text) { res.filesCurrent++; return; }
            File.WriteAllText(abs, text);
            res.filesCopied++;
        }

        private static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            return PackHash.Of(a) == PackHash.Of(b);
        }

        /// <summary>
        /// Forget everything a running session has cached about assets.
        ///
        /// All three, together, because they cache different halves of the same
        /// answer and any one left warm makes a freshly committed asset look
        /// broken: <c>PartMeshLibrary</c> caches the MISS (a key that was absent
        /// stays absent for the session), <c>AssetManifests</c> caches the parsed
        /// manifest and every material built from it, and <c>TiguanMaterials</c>
        /// caches the one hand-written manifest that predates all of this.
        /// </summary>
        public static void ResetCaches()
        {
            PartMeshLibrary.ResetCache();
            AssetManifests.ResetCache();
            TiguanMaterials.ResetCache();
        }

        // ==================== menu items ====================

        [MenuItem(AssetStudio.Menu + "1. Sync drafts from exports", priority = AssetStudio.PrioSync)]
        public static void SyncAll()
        {
            AssetStudioCatalog.Refresh();
            int synced = 0, drifted = 0;
            foreach (AssetRow r in AssetStudioCatalog.Rows)
            {
                AssetStudioDraft d = AssetStudioDrafts.Find(r);
                if (d == null || r.Export == null) continue;
                AssetStudioDrafts.Sync(d, r);
                AssetStudioDrafts.Save(d);
                synced++;
                if (StateOf(d.Kind, d.key, r.Export) == CommitState.SourceDrifted) drifted++;
            }
            AssetStudio.Log($"SYNC {synced} draft(s) re-read from their exports, "
                            + $"{drifted} committed asset(s) drifted.");
        }

        [MenuItem(AssetStudio.Menu + "2. Commit all drafts", priority = AssetStudio.PrioCommit)]
        public static void CommitAll()
        {
            AssetStudioCatalog.Refresh();
            int ok = 0, refused = 0, copied = 0, current = 0;
            foreach (AssetRow r in AssetStudioCatalog.Rows)
            {
                AssetStudioDraft d = AssetStudioDrafts.Find(r);
                if (d == null) continue;
                Result res = Commit(d, r);
                if (res.ok)
                {
                    ok++; copied += res.filesCopied; current += res.filesCurrent;
                    AssetStudio.Log(Line(res));
                }
                else { refused++; AssetStudio.Warn($"{res.key}: not committed — {res.problem}"); }
            }
            AssetStudio.Log($"COMMIT {ok} committed, {refused} refused, "
                            + $"{copied} file(s) copied, {current} already current.");
            AssetStudioCatalog.Refresh();
        }

        /// <summary>One line per committed asset, and the override reason is on
        /// it — an override stays loud for as long as it lasts, which is what
        /// makes it a decision rather than a way past the gate.</summary>
        public static string Line(Result r) =>
            $"COMMIT {r.key}: {r.filesCopied} copied, {r.filesCurrent} already current, "
            + $"{r.objects} object(s), {r.slots} slot(s), measured "
            + $"{r.measured.x:0.####} x {r.measured.y:0.####} x {r.measured.z:0.####} m"
            + (r.unmapped > 0 ? $", {r.unmapped} object(s) the draft never mapped" : "")
            + (r.orphans > 0 ? $", {r.orphans} draft entry(s) the mesh no longer has" : "")
            // The REASON, not just the fact. An override whose justification is
            // only in a file is an override nobody re-reads; printing the sentence
            // every run is what keeps it a decision somebody has to keep making.
            + (r.overridden ? "  [VERIFICATION OVERRIDDEN: " + r.overrideReason + "]" : "");

        [MenuItem(AssetStudio.Menu + "3. Refresh imports", priority = AssetStudio.PrioRefreshImports)]
        public static void RefreshImports()
        {
            int n = 0;
            foreach (string root in AssetStudio.Roots)
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { root }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!File.Exists(PackPaths.ToAbsolute(
                            AssetStudio.ManifestPathFor(path)))) continue;
                    // Sidecar first, model second — the order the postprocessor's
                    // LoadAssetAtPath lookups depend on.
                    string dir = Path.ChangeExtension(path, null);
                    if (AssetDatabase.IsValidFolder(dir))
                        AssetDatabase.ImportAsset(dir, ImportAssetOptions.ImportRecursive
                                                       | ImportAssetOptions.ForceUpdate);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    n++;
                }
            }
            AssetDatabase.Refresh();
            ResetCaches();
            AssetStudio.Log($"REFRESH {n} managed asset(s) reimported, sidecars first.");
        }

        [MenuItem(AssetStudio.Menu + "4. Reset runtime caches", priority = AssetStudio.PrioResetCaches)]
        public static void ResetCachesMenu()
        {
            ResetCaches();
            AssetStudio.Log("CACHES cleared — meshes, manifests and their materials.");
        }

        [MenuItem(AssetStudio.Menu + "Rebuild everything", priority = AssetStudio.PrioRebuildAll)]
        public static void RebuildAll()
        {
            SyncAll();
            CommitAll();
            RefreshImports();
            ResetCachesMenu();
            AssetStudio.Log("REBUILD done.");
        }
    }
}
