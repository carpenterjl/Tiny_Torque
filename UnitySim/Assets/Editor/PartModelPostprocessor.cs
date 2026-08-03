using System.IO;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Forces deterministic import settings on the Blender-authored part meshes
    /// under <c>Resources/PartModels/</c>, <c>Resources/TrackProps/</c> and
    /// <c>Resources/Cosmetics/</c>, so runtime code (PartMeshLibrary) can rely
    /// on exact scale and orientation regardless of Unity/FBX defaults:
    ///   • useFileScale off + globalScale 1  → 1 Blender metre = 1 Unity unit
    ///     (meshes authored in metres import at real size; no 0.01/100 surprises).
    ///   • no imported materials/cameras/lights/animation → the FBX carries shape
    ///     only; PartVisualFactory assigns the shared runtime materials by name.
    ///   • no auto colliders → PartMeshLibrary strips colliders anyway, but this
    ///     keeps the imported prefab clean.
    /// Scoped strictly to those folders so nothing else in the project is affected.
    /// </summary>
    public sealed class PartModelPostprocessor : AssetPostprocessor
    {
        /// <summary>
        /// Whether this path gets the settings below — public because Asset
        /// Studio's slot-order reader depends on being OUTSIDE this scope and
        /// should assert that rather than assume it.
        ///
        /// The reader imports a scratch copy of an export with its materials left
        /// on, purely to read which material sits in which submesh slot; a copy
        /// that landed in scope would be stripped by <c>materialImportMode
        /// = None</c> below and read back as 42 slots of Unity's
        /// <c>Default-Material</c>, which is not a failure that looks like one.
        /// </summary>
        public static bool InScope(string path) => IsPartModel(path);

        private static bool IsPartModel(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("Resources/PartModels/")   // vehicle parts
                || p.Contains("Resources/TrackProps/")   // track scenery + arcade props
                || p.Contains("Resources/Cosmetics/")    // unlockable cosmetics + crates
                // Asset Studio's preview staging: an external Blender export
                // copied in so it can be rendered before anyone decides to
                // commit it. It gets these settings for the whole point of the
                // preview — an FBX imported on Unity's defaults would arrive at
                // the file's own unit scale and carrying its own materials, so
                // the preview would show a different car from the one the game
                // would build, which is the one thing the preview exists to
                // rule out. Staged files are outside Resources/ and unreachable
                // by Resources.Load; see AssetStudioStaging.
                //
                // No GetVersion() bump: this widens WHICH assets are in scope
                // and changes nothing about the settings any existing asset
                // already has, so there is nothing to propagate — and a bump
                // would reimport 200+ FBX for a folder that starts out empty.
                || p.StartsWith(AssetTools.AssetStudio.StagingDir + "/");
        }

        private void OnPreprocessModel()
        {
            if (!IsPartModel(assetPath)) return;
            var mi = (ModelImporter)assetImporter;

            mi.useFileScale = false;   // ignore the FBX's own unit scale
            mi.globalScale = 1f;       // 1 unit in Blender == 1 unit (metre) in Unity

            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.importVisibility = false;
            mi.addCollider = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.None; // runtime overrides
            mi.meshCompression = ModelImporterMeshCompression.Off;

            // ...unless the asset ships a manifest that asks for VERBATIM
            // materials, in which case stripping them is the one thing that must
            // not happen. Decided here, during import, which is why the manifest
            // is a sibling FILE rather than something under a Manifests/ folder:
            // Resources.Load does not exist yet at this point and path arithmetic
            // does.
            ApplyMaterialMode(mi, assetPath);

            // Use the authored normals rather than recalculating them. The meshes
            // are exported with mesh_smooth_type='EDGE' after Shade Auto Smooth and
            // a Weighted Normal pass, so the split/weighted normals that keep body
            // creases, arch lips and tread-block edges crisp already live in the
            // FBX; letting Unity recompute them from a smoothing angle throws that
            // away and softens exactly the edges the models were built around.
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            // CPU-readable meshes, two cases:
            //   • body shells — the garage paint mode cooks runtime MeshColliders
            //     from them and reads hit UVs (RaycastHit.textureCoord);
            //   • EVERY track prop — TrackCatalog.MeshProp now cooks a non-convex
            //     MeshCollider per piece at map build, replacing the coarse
            //     primitive hulls. Runtime cooking needs the CPU copy, which a
            //     player build strips from non-readable meshes — it works in the
            //     editor either way, which is exactly the trap.
            // Wheels/battery/antenna/cosmetics stay render-only.
            string file = System.IO.Path.GetFileName(assetPath);
            string p = assetPath.Replace('\\', '/');
            //   • the Tiguan's WHEELS — the only wheels anywhere that are read.
            //     TiguanChecks measures winding and signed volume off the CPU
            //     copy, and without it those checks find no readable mesh and
            //     pass by finding nothing, which is the precise failure shape
            //     the whole winding gate exists to prevent. It is a debug asset;
            //     the memory does not matter.
            mi.isReadable = file.StartsWith("body_") || p.Contains("Resources/TrackProps/")
                            || file.StartsWith("wheel_tiguan");

            // The Tiguan is a real car at 1:1 in a kit of 1/10 toys, so it is the
            // only asset here anywhere near the 16-bit index ceiling — its shell
            // alone is ~100k triangles against the arcade bodies' few thousand.
            // Unity's default SPLITS a mesh past 65 535 vertices silently, and a
            // split shell is a shell with a seam down it AND a different renderer
            // count than the validator was told to expect. Scoped by name so every
            // other asset in these folders keeps importing bit-identically.
            if (file.StartsWith("body_tiguan") || file.StartsWith("wheel_tiguan"))
                mi.indexFormat = ModelImporterIndexFormat.UInt32;
        }

        /// <summary>Where a verbatim asset's own materials live, relative to the
        /// project: <c>Resources/PartModels/body_police/Materials/</c>, the same
        /// per-key folder its textures go in. It mirrors the Blender exporter's own
        /// layout (<c>FBX + export.json + Materials/ + Textures/</c>), so the commit
        /// pipeline copies a folder rather than redistributing its contents.</summary>
        public static string MaterialsDir(string modelAssetPath)
        {
            string dir = Path.GetDirectoryName(modelAssetPath)?.Replace('\\', '/') ?? "";
            return dir + "/" + Path.GetFileNameWithoutExtension(modelAssetPath) + "/Materials";
        }

        /// <summary>The sibling manifest's path for a model, whether or not it
        /// exists: <c>body_police.fbx</c> → <c>body_police_asset.json</c>.</summary>
        public static string ManifestPath(string modelAssetPath)
        {
            string dir = Path.GetDirectoryName(modelAssetPath)?.Replace('\\', '/') ?? "";
            return dir + "/" + Path.GetFileNameWithoutExtension(modelAssetPath) +
                   AssetManifests.Suffix + ".json";
        }

        /// <summary>
        /// Leave the FBX's materials alone when — and only when — a sibling
        /// manifest says <c>"materialMode": "Verbatim"</c>.
        ///
        /// <b>Verbatim is the mode where the exporter's Blender materials ARE the
        /// answer.</b> Unity is told to import them as Standard, to treat them as
        /// external assets rather than sub-assets of the model, and then handed an
        /// explicit remap per material name pointing at the <c>.mat</c> the
        /// exporter wrote. The remap is what makes it verbatim rather than
        /// approximately verbatim: without it Unity would build its own Standard
        /// material from the FBX's diffuse colour and drop every map the author
        /// wired in Blender.
        ///
        /// <b><c>InPrefab</c>, not <c>External</c>, and that was measured.</b> Both
        /// honour the remap; they differ only in what happens to a material the
        /// remap does NOT cover. <c>External</c> writes Unity's own replacement out
        /// as a real <c>.mat</c> file in a <c>Materials</c> folder beside the model
        /// — which here is <c>Resources/PartModels/Materials/</c>, a folder shared
        /// by all 207 assets — and it names it after the diffuse TEXTURE, so one
        /// withheld material produced a stray
        /// <c>Resources/PartModels/Materials/M_Police_Chrome_BaseColor.mat</c>.
        /// <c>InPrefab</c> keeps the replacement as a sub-asset of the FBX, where
        /// it belongs and where it cannot collide with another asset's material of
        /// the same name. Remapping IS the embedded-materials workflow; External is
        /// the legacy extract-to-disk one.
        ///
        /// <b>Import ORDER is load-bearing and cannot be checked from here.</b>
        /// <c>LoadAssetAtPath</c> only sees a <c>.mat</c> Unity has already
        /// imported, so a model imported in the same batch as its materials can
        /// find none of them. The commit pipeline must import the Materials folder
        /// first and only then <c>ImportAsset</c> the FBX. A miss is not silent —
        /// it names the file it wanted — but it is a re-import away from being
        /// right, and that sentence is the whole reason the warning exists.
        ///
        /// <b>No <c>GetVersion()</c> bump.</b> Every existing asset takes the
        /// unchanged <c>None</c> branch: 207 of 207 ship no manifest, so there is
        /// nothing to propagate and a bump would reimport 200+ FBX to reach an
        /// empty set.
        /// </summary>
        private static void ApplyMaterialMode(ModelImporter mi, string assetPath)
        {
            string manifestPath = ManifestPath(assetPath);
            if (!File.Exists(manifestPath)) return;   // no manifest: silent, and normal

            AssetManifest man = AssetManifests.FromJson(File.ReadAllText(manifestPath), manifestPath);
            if (man == null || !man.IsVerbatim) return;

            mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;

            string dir = MaterialsDir(assetPath);
            int found = 0, missing = 0, unusable = 0;
            foreach (AssetMaterialDef d in man.materials)
            {
                if (d == null || string.IsNullOrEmpty(d.name)) continue;
                string matPath = dir + "/" + d.name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    missing++;
                    Debug.LogWarning($"[PartModelPostprocessor] {assetPath} is Verbatim but " +
                                     $"{matPath} is not imported — \"{d.name}\" will be whatever " +
                                     "Unity makes of the FBX. Import the Materials folder first, " +
                                     "then re-import the model.");
                    continue;
                }
                if (!Renderable(mat)) unusable++;
                mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), d.name), mat);
                found++;
            }
            Debug.Log($"[PartModelPostprocessor] {assetPath}: Verbatim, {found} material(s) " +
                      $"remapped{(missing > 0 ? $", {missing} missing" : "")}" +
                      $"{(unusable > 0 ? $", {unusable} on a shader this project does not have" : "")}.");
        }

        /// <summary>
        /// False when a material's shader is missing from THIS project — the state
        /// Unity renders as magenta and reports nowhere.
        ///
        /// <b>The case this exists for is real and is the main cost of verbatim
        /// mode.</b> A Blender export carries whatever materials the exporting
        /// project's render pipeline uses, and the current exporter runs under URP
        /// while this game is Built-in RP: its <c>.mat</c> files reference
        /// Universal Render Pipeline/Lit, which resolves here to
        /// <c>Hidden/InternalErrorShader</c>. Nothing in this repo can fix that —
        /// verbatim means verbatim — so the honest thing is to say it by name at
        /// import, once, with the way out.
        /// </summary>
        private static bool Renderable(Material mat)
        {
            Shader s = mat.shader;
            if (s != null && s.name != "Hidden/InternalErrorShader") return true;
            Debug.LogWarning($"[PartModelPostprocessor] {AssetDatabase.GetAssetPath(mat)}: " +
                             $"shader \"{(s == null ? "<null>" : s.name)}\" — this material was " +
                             "authored against a render pipeline this project does not have, and " +
                             "will render magenta. Verbatim mode cannot fix that by definition. " +
                             "Re-author it against Built-in RP Standard, or switch the asset to " +
                             "Manifest mode, which rebuilds materials from the exported numbers " +
                             "and does not care which pipeline wrote them.");
            return false;
        }

        /// <summary>Bumped so a settings change here reimports every asset in
        /// scope on the next editor/batch run (the TrackProps isReadable flip
        /// must reach all 126 existing FBX, not just future ones).
        ///
        /// 3: the Tiguan's 32-bit index format.
        /// 4: the Tiguan wheels' isReadable. Separate bump because the rule
        /// changed AFTER 3's reimport had already run, and an OnPreprocessModel
        /// edit reaches nothing without one — the wheels stayed unreadable and
        /// their winding check reported itself broken, which is the right way
        /// for that to fail but still a wasted run.
        /// Expect a one-off reimport of every FBX in scope on the next editor
        /// start — slow, harmless, and worth knowing about before it looks like
        /// a hang.</summary>
        public override uint GetVersion() => 4;
    }
}
