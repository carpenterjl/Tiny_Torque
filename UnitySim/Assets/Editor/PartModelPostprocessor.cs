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
                || p.Contains("Resources/Cosmetics/");   // unlockable cosmetics + crates
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
            // Body shells stay CPU-readable: the garage paint mode cooks runtime
            // MeshColliders from them and reads hit UVs (RaycastHit.textureCoord),
            // which requires readable meshes in player builds. Everything else
            // (wheels/battery/antenna) is render-only.
            string file = System.IO.Path.GetFileName(assetPath);
            mi.isReadable = file.StartsWith("body_");
        }
    }
}
