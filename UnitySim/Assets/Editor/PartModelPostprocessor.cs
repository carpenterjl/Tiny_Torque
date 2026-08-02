using UnityEditor;

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
