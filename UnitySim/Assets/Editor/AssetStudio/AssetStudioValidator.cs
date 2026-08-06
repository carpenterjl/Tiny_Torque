using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AIHWSim.EditorTools;
using AIHWSim.Pack;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// The gate on everything Asset Studio has committed — one row per managed
    /// asset, one RESULT line, and nothing to run separately.
    ///
    /// <b>What it is FOR.</b> The 207 shipped assets are guarded by
    /// <c>[PMV]</c>'s hand-written table, which is an independent prediction: a
    /// person wrote down what an FBX round trip should preserve, and Unity has to
    /// agree. A committed asset has no such second opinion — the pipeline wrote
    /// both the file and the row describing it. So this validator asks the
    /// questions that DO have two sides: does the manifest agree with the mesh
    /// Unity actually imported, do its texture paths resolve, do the import
    /// settings match what the map is bound as, has the copy under
    /// <c>Resources/</c> been edited since it was committed, and did the key
    /// actually reach the catalogue it claims to join.
    ///
    /// <b>Every check compares two things that can disagree.</b> A check that
    /// re-derives a number from the same source that wrote it proves only that
    /// the file is a file; where this validator measures, it measures the
    /// PREFAB, through <c>AssetStudioCommit.MeasuredSize</c> — the pipeline's own
    /// arithmetic rather than a transcription of it, so the recorded size moves
    /// only when the geometry does.
    ///
    /// <b>Zero managed assets is a pass and says so.</b> That is the shipped
    /// state of the repository and will be until somebody commits something; a
    /// gate that could only be exercised by content nobody has authored yet would
    /// be a gate nobody notices has broken.
    ///
    /// Run with (editor must be closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.AssetTools.AssetStudioValidator.Report -logFile &lt;log&gt;
    /// then grep the log for "[AST] RESULT".
    /// </summary>
    public static class AssetStudioValidator
    {
        private const float TolMetres = 0.002f;   // +-2 mm, [PMV]'s tolerance

        [MenuItem(AssetStudio.Menu + "Validate committed assets [AST]",
                  priority = AssetStudio.PrioValidate)]
        public static void Report()
        {
            // The session may have a manifest cached from before an import; the
            // gate should read what is on disk now.
            AssetStudioCommit.ResetCaches();

            int fail = 0, n = 0, skipped = 0;
            fail += Vocabulary();
            fail += Layout();

            List<string> exports = ExportDirs();
            foreach (Managed m in Discover())
            {
                n++;
                var why = new List<string>();
                var note = new List<string>();

                Integrity(m, why);
                Coverage(m, why);
                Textures(m, why);
                Geometry(m, why);
                Verbatim(m, why);
                Registry(m, why);
                Overrides(m, why, note);
                if (!Freshness(m, exports, why, note)) skipped++;

                string line = $"{m.Key}: {m.Man.kind} {m.Man.materialMode}, "
                            + $"{m.Man.MaterialCount} material(s), {m.Man.ObjectCount} object(s)"
                            + (note.Count > 0 ? "  [" + string.Join("] [", note) + "]" : "");
                if (why.Count == 0) Debug.Log($"{AssetStudio.Tag} PASS {line}");
                else
                {
                    Debug.LogError($"{AssetStudio.Tag} FAIL {line} - " + string.Join("; ", why));
                    fail++;
                }
            }

            Debug.Log($"{AssetStudio.Tag} RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({n} managed asset(s), {skipped} source check(s) skipped)");
        }

        // ==================== what there is to check ====================

        /// <summary>One committed asset: its manifest, where it lives, and the
        /// prefab Unity built from it.</summary>
        private sealed class Managed
        {
            public string Key = "";
            public AssetManifest Man;
            public string Root = "";          // "Assets/Resources/PartModels"
            public string ResourceRoot = "";  // "PartModels/"
            public string ManifestPath = "";
            public string FbxPath = "";
            public GameObject Prefab;
            public AssetKind Kind = AssetKind.Unassigned;
        }

        /// <summary>
        /// Every manifest sitting directly under one of the three Resources
        /// roots, read off DISK rather than through <c>Resources.LoadAll</c>.
        ///
        /// Deliberately not <c>AssetManifests.Discover</c>, even though the
        /// catalogues use it: a manifest whose <c>.meta</c> never got written, or
        /// whose JSON does not parse, is invisible to <c>Resources</c> and is
        /// exactly the state this gate exists to name. Top level only, for the
        /// same reason the browser lists top level only — <c>Resources.Load</c> is
        /// handed the stem, so a manifest in a subfolder describes a key nothing
        /// can ask for.
        /// </summary>
        private static List<Managed> Discover()
        {
            var found = new List<Managed>();
            foreach (string root in AssetStudio.Roots)
            {
                string abs = PackPaths.ToAbsolute(root);
                if (!Directory.Exists(abs)) continue;

                foreach (string file in Directory.GetFiles(abs, "*" + AssetManifests.Suffix + ".json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string key = name.Substring(0, name.Length - AssetManifests.Suffix.Length);
                    string projectRel = root + "/" + Path.GetFileName(file);

                    AssetManifest man = AssetManifests.FromJson(File.ReadAllText(file), projectRel);
                    if (man == null)
                    {
                        // FromJson already said why, in the parser's own words.
                        Debug.LogError($"{AssetStudio.Tag} FAIL {key}: its manifest does not parse, "
                            + "so the asset binds through the legacy token tables and this gate "
                            + "cannot check anything else about it.");
                        continue;
                    }

                    string rootName = AssetStudio.RootName(root);
                    found.Add(new Managed
                    {
                        Key = key,
                        Man = man,
                        Root = root,
                        ResourceRoot = rootName + "/",
                        ManifestPath = projectRel,
                        FbxPath = root + "/" + key + ".fbx",
                        Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root + "/" + key + ".fbx"),
                        Kind = AssetStudioCatalog.KindFor(rootName, key),
                    });
                }
            }
            found.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return found;
        }

        // ==================== manifest integrity ====================

        private static void Integrity(Managed m, List<string> why)
        {
            AssetManifest man = m.Man;

            if (man.schema != 1)
                why.Add($"schema {man.schema} is not one this build reads");

            // The file name is the authority — Resources.Load is handed the stem
            // — and AssetManifests.Discover silently corrects a mismatch at load.
            // Silently is right for the game and wrong for a gate: the file says
            // one thing and the folder another, and somebody should fix it.
            if (man.key != m.Key)
                why.Add($"the manifest calls itself \"{man.key}\" but sits beside {m.Key}.fbx");

            if (m.Prefab == null)
                why.Add($"{m.FbxPath} is missing or did not import — the manifest describes "
                      + "an asset that is not there");

            if (man.materialMode != AssetMaterialModes.Manifest
                && man.materialMode != AssetMaterialModes.Verbatim)
                why.Add($"materialMode \"{man.materialMode}\" is neither "
                      + $"{AssetMaterialModes.Manifest} nor {AssetMaterialModes.Verbatim}");

            // The kind decides which registry the key joins and which geometry
            // contract it is held to, so a kind that disagrees with where the file
            // actually sits is a row in the wrong table.
            if (!Enum.TryParse(man.kind, out AssetKind kind) || kind == AssetKind.Unassigned)
                why.Add($"kind \"{man.kind}\" is not one of {string.Join("/", KindNames())}");
            else if (kind != m.Kind)
                why.Add($"kind \"{man.kind}\" disagrees with where the asset lives — "
                      + $"{m.Root}/{m.Key}.fbx is a {m.Kind}");

            var mats = new HashSet<string>(StringComparer.Ordinal);
            foreach (AssetMaterialDef d in man.materials)
            {
                if (d == null || string.IsNullOrEmpty(d.name)) { why.Add("a material has no name"); continue; }
                // Slots join to materials BY NAME, so two materials sharing one is
                // a slot whose answer depends on which the lookup reached first.
                if (!mats.Add(d.name)) why.Add($"two materials are both called \"{d.name}\"");
            }

            var objs = new HashSet<string>(StringComparer.Ordinal);
            foreach (AssetObjectDef o in man.objects)
            {
                if (o == null || string.IsNullOrEmpty(o.name)) { why.Add("an object has no name"); continue; }
                if (!objs.Add(o.name)) why.Add($"two objects are both called \"{o.name}\"");
            }
        }

        // ==================== material / object coverage ====================

        private static void Coverage(Managed m, List<string> why)
        {
            AssetManifest man = m.Man;

            int bindable = 0;
            foreach (AssetObjectDef o in man.objects)
            {
                if (o?.slots == null) continue;
                for (int i = 0; i < o.slots.Length; i++)
                {
                    string s = o.slots[i];
                    // An EMPTY entry is a statement — "leave this slot as
                    // imported" — and the one way an author says it. A NAMED one
                    // that matches nothing is a dangling reference.
                    if (string.IsNullOrEmpty(s)) continue;
                    bindable++;
                    if (man.MaterialDef(s) == null)
                        why.Add($"{o.name} slot {i} names material \"{s}\", which this "
                              + "manifest does not have");
                }
            }

            // Manifest mode with nothing to bind renders black or magenta and
            // nothing says so: the importer strips the FBX's materials, so
            // "leave as imported" leaves every slot NULL. Verbatim is the
            // opposite — its whole point is that the manifest binds nothing.
            if (!man.IsVerbatim && bindable == 0 && man.ObjectCount > 0)
                why.Add("Manifest mode, but not one slot names a material — the importer "
                      + "strips the FBX's own, so every renderer would arrive with a null "
                      + "material");

            if (m.Prefab == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Renderer r in m.Prefab.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name;
                seen.Add(n);
                AssetObjectDef o = man.ObjectDef(n);
                if (o == null)
                {
                    why.Add($"the mesh has \"{n}\" and the manifest does not — it would keep "
                          + "its imported material and render wrong");
                    continue;
                }
                int slots = Mathf.Max(1, r.sharedMaterials?.Length ?? 1);
                if (o.SlotCount != slots)
                    why.Add($"{n} has {slots} submesh slot(s) and the manifest lists "
                          + $"{o.SlotCount} — re-commit, which reads the count off the import");
            }
            foreach (AssetObjectDef o in man.objects)
                if (o != null && !string.IsNullOrEmpty(o.name) && !seen.Contains(o.name))
                    why.Add($"the manifest names \"{o.name}\", which the mesh no longer has");
        }

        // ==================== textures and their import settings ====================

        /// <summary>
        /// Every map path resolves, lands in this asset's own folder, and was
        /// imported as the KIND of map the slot binds it as.
        ///
        /// The settings half is the part with two sides. R2's texture
        /// postprocessor sets sRGB off and NormalMap on by FILE NAME suffix; this
        /// asks the same question through the SLOT the manifest binds the texture
        /// to. A metallic-smoothness map that was not named
        /// <c>*_MetallicSmoothness</c> imports as colour data, comes out gamma
        /// decoded, and produces a car that is subtly too shiny in a way no
        /// screenshot names.
        /// </summary>
        private static void Textures(Managed m, List<string> why)
        {
            foreach (AssetMaterialDef d in m.Man.materials)
            {
                if (d == null) continue;
                Check(d.name, "albedo", d.mapAlbedo, false, false);
                Check(d.name, "metallic/smoothness", d.mapMetallicSmoothness, true, false);
                Check(d.name, "emission", d.mapEmission, false, false);
                Check(d.name, "normal", d.mapNormal, false, true);
            }

            void Check(string mat, string slot, string path, bool linear, bool normal)
            {
                if (string.IsNullOrEmpty(path)) return;

                // Per-key, never shared. R4 measured the alternative: two assets
                // whose exporter both wrote a "BaseColor" map collide in a shared
                // folder and one silently wins.
                string want = AssetStudioCommit.ResourceDirFor(m.Kind, m.Key) + "/";
                if (!path.StartsWith(want, StringComparison.Ordinal))
                    why.Add($"{mat} {slot} map \"{path}\" is outside this asset's own folder "
                          + $"({want})");

                var tex = Resources.Load<Texture2D>(path);
                if (tex == null)
                {
                    why.Add($"{mat} {slot} map \"{path}\" does not resolve — the material "
                          + "would render off its flat values with the map silently absent");
                    return;
                }

                var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
                if (ti == null) return;
                if (linear && ti.sRGBTexture)
                    why.Add($"{mat} {slot} map \"{path}\" imported as sRGB — it is data, not "
                          + "colour, and gamma decoding it makes metal and gloss both wrong");
                if (normal && ti.textureType != TextureImporterType.NormalMap)
                    why.Add($"{mat} normal map \"{path}\" imported as {ti.textureType}, not "
                          + "NormalMap");
                if (!normal && ti.textureType == TextureImporterType.NormalMap)
                    why.Add($"{mat} map \"{path}\" imported as a NormalMap but is bound as "
                          + $"the {slot} map");
            }
        }

        // ==================== the geometry contract ====================

        private static void Geometry(Managed m, List<string> why)
        {
            AssetManifest man = m.Man;

            if (man.authorScale <= 0f)
                why.Add($"authorScale {man.authorScale} is zero or negative");
            if (Mathf.Abs(Mathf.Repeat(man.authorYawDeg, 90f)) > 0.01f)
                why.Add($"authorYawDeg {man.authorYawDeg} is not a multiple of 90 — a bounding "
                      + "box does not survive anything else");

            // The pivot fix, checked for the two ways it can be nonsense. It is
            // deliberately NOT checked against the measured size below: a
            // translation does not change a bounding box's extents, so authorSize
            // stays true whatever this is — which is the property that lets an
            // author nudge a car without re-measuring anything.
            Vector3 off = man.AuthorOffset;
            Vector3 offM = off * Mathf.Max(1e-6f, man.authorScale);
            if (float.IsNaN(off.sqrMagnitude) || float.IsInfinity(off.sqrMagnitude))
                why.Add("authorOffset is not a finite number");

            if (m.Prefab == null) return;

            Vector3 measured = AssetStudioCommit.MeasuredSize(m.Prefab, man.authorScale, man.authorYawDeg);
            Vector3 recorded = man.AuthorSize;

            if (Mathf.Abs(offM.x) > measured.x || Mathf.Abs(offM.y) > measured.y
                || Mathf.Abs(offM.z) > measured.z)
                why.Add($"authorOffset {F(off)} comes to {F(offM)} m on a {F(measured)} m asset "
                      + "— further than the mesh is big, so it is not a pivot fix. It is in "
                      + $"MESH units (times authorScale {man.authorScale:0.#####}), not metres");
            if (Off(measured.x, recorded.x) || Off(measured.y, recorded.y) || Off(measured.z, recorded.z))
                why.Add($"authorSize records {F(recorded)} and the mesh measures {F(measured)} — "
                      + "the geometry has changed since it was committed; re-commit");

            // Every car in this game is built facing +Z: the camera mount, the
            // lights, the sensors and the wheel placement are all written that
            // way, so a body whose long axis came out on X is not a car that is
            // merely rotated, it is a car whose front is its side.
            if (m.Kind == AssetKind.CarBody && measured.z < measured.x)
                why.Add($"after the {man.authorYawDeg}° yaw the body is longer across X "
                      + $"({measured.x:0.000}) than along Z ({measured.z:0.000}) — the game "
                      + "builds every car facing +Z");

            // The spec is what [PMV] holds the asset to, and it is written from
            // authorSize by the same commit. They can only disagree if one was
            // hand-edited, which is exactly when the disagreement matters.
            if (m.Kind == AssetKind.CarBody) Pinned(2, man.spec.z, recorded.z, "z");
            else if (m.Kind == AssetKind.Wheel)
            {
                Pinned(1, man.spec.y, recorded.y, "y");
                Pinned(2, man.spec.z, recorded.z, "z");
            }

            int tris = AssetStudioCommit.TriangleCount(m.Prefab);
            if (man.spec.maxTris <= 0)
                why.Add($"no triangle budget — [PMV] would hold this asset's {tris} triangles "
                      + "to nothing at all; re-commit, which measures one");
            else if (tris > man.spec.maxTris)
                why.Add($"{tris} triangles against a budget of {man.spec.maxTris}");

            void Pinned(int axis, float spec, float size, string name)
            {
                if (spec < 0f) why.Add($"spec.{name} is unpinned; a {m.Kind} pins it");
                else if (Off(spec, size))
                    why.Add($"spec.{name} {spec:0.####} disagrees with authorSize {size:0.####}");
            }
        }

        // ==================== verbatim mode ====================

        private static void Verbatim(Managed m, List<string> why)
        {
            if (!m.Man.IsVerbatim) return;

            string dir = AssetStudioCommit.MaterialsDirFor(m.Kind, m.Key);
            foreach (AssetMaterialDef d in m.Man.materials)
            {
                if (d == null || string.IsNullOrEmpty(d.name)) continue;
                string path = dir + "/" + d.name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    why.Add($"Verbatim, but {path} is not there — \"{d.name}\" is whatever "
                          + "Unity made of the FBX");
                    continue;
                }
                // R4's real finding, and the main cost of verbatim mode: the
                // exporter runs under URP and this game is Built-in RP, so its
                // .mat files resolve here to Hidden/InternalErrorShader and render
                // magenta. Nothing in this repository can fix that — verbatim
                // means verbatim — so it is a failure with a way out rather than
                // a warning somebody scrolls past.
                if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                    why.Add($"{path} is on a shader this project does not have "
                          + $"(\"{(mat.shader == null ? "<null>" : mat.shader.name)}\") and "
                          + "renders magenta. Re-author it against Built-in RP Standard, or "
                          + "switch the asset to Manifest mode");
            }

            if (m.Prefab == null) return;
            // The remap is what makes verbatim verbatim; a null slot means it did
            // not take, which is the import-ORDER failure R4 measured.
            foreach (Renderer r in m.Prefab.GetComponentsInChildren<Renderer>(true))
            {
                Material[] slots = r.sharedMaterials;
                for (int i = 0; i < (slots?.Length ?? 0); i++)
                    if (slots[i] == null)
                    {
                        why.Add($"{r.gameObject.name} slot {i} imported with no material — the "
                              + "remap did not take. Import the Materials folder first, then "
                              + "the model");
                        return;
                    }
            }
        }

        // ==================== did the key reach a table ====================

        /// <summary>
        /// The asset registered. This is the whole claim of the milestone — art
        /// in a folder becomes content — and it is the one thing a manifest
        /// cannot assert about itself: the catalogues compose from
        /// <c>AssetManifests.Discover</c>, which reads through <c>Resources</c>,
        /// so a manifest whose <c>.meta</c> is missing parses perfectly here and
        /// is invisible to the game.
        /// </summary>
        private static void Registry(Managed m, List<string> why)
        {
            switch (m.Kind)
            {
                case AssetKind.CarBody:
                    BodyDef b = BodyCatalog.ById(m.Key);
                    if (b == null || b.id != m.Key) why.Add(Missing("BodyCatalog"));
                    else if (b.meshKey != m.Key)
                        why.Add($"BodyCatalog has \"{m.Key}\" pointing at mesh \"{b.meshKey}\"");
                    // A blank cd is fine — DragEstimator measures one off the mesh.
                    // What is not fine is a mesh that cannot BE measured, because
                    // then the row really does fall back to a constant nobody
                    // chose. That is what this asks now.
                    else if (m.Man.vehicle.cd < 0f && DragEstimator.SilhouetteFor(b) == null)
                        why.Add("no drag coefficient and no measurable geometry — check the "
                              + "model's Read/Write flag, or state a cd in the manifest");
                    break;

                case AssetKind.Wheel:
                    WheelDef w = WheelCatalog.ById(m.Key);
                    if (w == null || w.id != m.Key) why.Add(Missing("WheelCatalog"));
                    else if (w.authorRadius <= 0f)
                        why.Add($"WheelCatalog resolved an author radius of {w.authorRadius}");
                    break;

                case AssetKind.Cosmetic:
                    if (Garage.CosmeticCatalog.ById(m.Key) == null) why.Add(Missing("CosmeticCatalog"));
                    break;

                default:
                    // Refused at commit; reachable only by a hand-written
                    // manifest, which is exactly the case worth naming.
                    why.Add($"a {m.Kind} has no registry a manifest can join — its key comes "
                          + "from a switch on an int, so this asset imports and is asked for "
                          + "by nothing");
                    break;
            }

            string Missing(string table) =>
                $"{table} has no row for \"{m.Key}\". The catalogues compose from manifests "
                + "found through Resources, so this one is on disk and invisible to the game — "
                + "usually a missing .meta or a manifest outside the asset's Resources root";
        }

        // ==================== overrides and freshness ====================

        private static void Overrides(Managed m, List<string> why, List<string> note)
        {
            AssetNotesDef n = m.Man.notes;
            if (!n.verificationOverridden) return;
            if (string.IsNullOrWhiteSpace(n.overrideReason))
                why.Add("committed past a failed exporter verification with no reason written "
                      + "down — the reason is the only thing that makes an override a decision");
            else
                // Loud, on every run, for as long as it lasts.
                note.Add("VERIFICATION OVERRIDDEN: " + n.overrideReason);
        }

        /// <summary>
        /// Two hashes, two questions — and only one of them needs the export
        /// folder.
        ///
        /// <c>committedHash</c> against the FBX on disk asks whether somebody
        /// edited the project's copy, and a re-commit would throw that edit away;
        /// that is a FAILURE and it is answerable on any machine.
        /// <c>source.fbxMd5</c> against the export asks whether Blender has moved
        /// on, which is not a defect in this repository at all — it is news, and
        /// it is reported as news.
        ///
        /// Returns false when the source half was skipped.
        /// </summary>
        private static bool Freshness(Managed m, List<string> exports, List<string> why,
                                      List<string> note)
        {
            string fbxAbs = PackPaths.ToAbsolute(m.FbxPath);
            if (string.IsNullOrEmpty(m.Man.committedHash))
                why.Add("no committedHash — nothing can tell whether the copy under Resources/ "
                      + "is still the file that was committed");
            else if (File.Exists(fbxAbs) && PackHash.Of(fbxAbs) != m.Man.committedHash)
                why.Add($"{m.FbxPath} no longer hashes to what was committed — it was edited in "
                      + "the project, and the next commit would silently discard that edit");

            string dir = ExportFor(m, exports);
            if (string.IsNullOrEmpty(dir))
            {
                note.Add(string.IsNullOrEmpty(AssetStudio.SourceDir)
                    ? "source not checked: no export folder is configured on this machine"
                    : $"source not checked: no export named \"{m.Man.source.assetName}\"");
                return false;
            }

            TtExport x = TtExport.Load(dir, out string problem);
            if (x == null) { note.Add("source not checked: " + problem); return false; }

            string srcFbx = x.FbxPath;
            if (string.IsNullOrEmpty(srcFbx) || !File.Exists(srcFbx))
            { note.Add("source not checked: the export has no FBX"); return false; }

            if (PackHash.Of(srcFbx) != m.Man.source.fbxMd5)
                note.Add($"DRIFTED: the export has changed since this was committed "
                       + $"(exported {x.exportedAtUtc})");
            return true;
        }

        /// <summary>The export folder this asset was committed from, matched on
        /// the name the exporter gave it.</summary>
        private static string ExportFor(Managed m, List<string> exports)
        {
            string want = m.Man.source?.assetName ?? "";
            if (string.IsNullOrEmpty(want)) return "";
            foreach (string dir in exports)
                if (string.Equals(Path.GetFileName(dir), want, StringComparison.OrdinalIgnoreCase))
                    return dir;
            return "";
        }

        private static List<string> ExportDirs()
        {
            try { return AssetStudioCatalog.ExportFolders(); }
            catch (Exception) { return new List<string>(); }
        }

        // ==================== the two whole-project checks ====================

        /// <summary>
        /// The runtime's <c>AssetKinds</c> strings and the editor's
        /// <c>AssetKind</c> enum are one vocabulary written twice — the runtime
        /// cannot see the editor assembly, so it spells the names itself — and
        /// this is the check the runtime's own doc comment promises. A kind
        /// added on one side and not the other produces a manifest whose
        /// <c>kind</c> nothing recognises, which is a silent registration
        /// failure.
        /// </summary>
        private static int Vocabulary()
        {
            var runtime = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldInfo f in typeof(AssetKinds).GetFields(
                         BindingFlags.Public | BindingFlags.Static))
                if (f.IsLiteral && f.FieldType == typeof(string))
                    runtime.Add((string)f.GetRawConstantValue());

            var editor = new HashSet<string>(KindNames(), StringComparer.Ordinal);

            string why = "";
            foreach (string s in editor)
                if (!runtime.Contains(s)) why += $" AssetKind.{s} has no AssetKinds constant";
            foreach (string s in runtime)
                if (!editor.Contains(s)) why += $" AssetKinds.{s} is not an AssetKind";

            if (why.Length == 0) { Debug.Log($"{AssetStudio.Tag} PASS vocabulary ({runtime.Count} kinds)"); return 0; }
            Debug.LogError($"{AssetStudio.Tag} FAIL vocabulary -{why}");
            return 1;
        }

        /// <summary>Every <c>AssetKind</c> a manifest can legally carry —
        /// Unassigned is the browser's "nobody has said yet" and never reaches a
        /// file.</summary>
        private static IEnumerable<string> KindNames()
        {
            foreach (string s in Enum.GetNames(typeof(AssetKind)))
                if (s != nameof(AssetKind.Unassigned)) yield return s;
        }

        /// <summary>
        /// The commit pipeline and the model postprocessor have to agree about
        /// where a verbatim asset's materials live, and they compute it
        /// separately — one from a kind and a key, the other from a model's asset
        /// path. Asserted rather than assumed, which is what
        /// <c>AssetStudioCommit.MaterialsDirFor</c>'s own doc says this gate
        /// does. Disagreement is invisible until an author picks Verbatim and
        /// gets fourteen missing materials.
        /// </summary>
        private static int Layout()
        {
            string why = "";
            foreach (AssetKind kind in new[]
                     { AssetKind.CarBody, AssetKind.Wheel, AssetKind.Cosmetic, AssetKind.Prop })
            {
                string key = "probe_layout";
                string mine = AssetStudioCommit.MaterialsDirFor(kind, key);
                string theirs = PartModelPostprocessor.MaterialsDir(
                    AssetStudioCommit.FbxPathFor(kind, key));
                if (mine != theirs)
                    why += $" {kind}: commit writes {mine}, the importer reads {theirs};";

                string manifest = AssetStudio.ManifestPathFor(AssetStudioCommit.FbxPathFor(kind, key));
                string imported = PartModelPostprocessor.ManifestPath(
                    AssetStudioCommit.FbxPathFor(kind, key));
                if (manifest != imported)
                    why += $" {kind}: commit writes {manifest}, the importer reads {imported};";
            }
            if (why.Length == 0) { Debug.Log($"{AssetStudio.Tag} PASS layout"); return 0; }
            Debug.LogError($"{AssetStudio.Tag} FAIL layout -{why}");
            return 1;
        }

        // ==================== helpers ====================

        private static bool Off(float a, float b) => Mathf.Abs(a - b) > TolMetres;

        private static string F(Vector3 v) => $"{v.x:0.####} x {v.y:0.####} x {v.z:0.####} m";
    }
}
