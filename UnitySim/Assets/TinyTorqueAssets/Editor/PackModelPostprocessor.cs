using UnityEditor;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Import settings for the asset pack's own mesh copies under
    /// <c>Assets/TinyTorqueAssets/Models/</c>.
    ///
    /// This deliberately duplicates <c>AIHWSim.EditorTools.PartModelPostprocessor</c>
    /// rather than widening it. Two reasons:
    ///   • widening that file's scope means bumping its <c>GetVersion()</c>, which
    ///     reimports all 204 shipping assets to change nothing about them;
    ///   • the pack is meant to be removable — everything it needs lives inside its
    ///     own folder, and nothing under <c>Assets/Scripts</c> or <c>Assets/Editor</c>
    ///     is touched.
    ///
    /// One setting differs from the game's: <b>every pack mesh is CPU-readable</b>.
    /// The game only needs readability for body shells and track props (runtime
    /// MeshCollider cooking); the pack cooks mesh colliders into prefabs for
    /// everything and the scatter brush raycasts against them, so the exception
    /// list would just be a list of future bugs.
    /// </summary>
    public sealed class PackModelPostprocessor : AssetPostprocessor
    {
        private static bool IsPackModel(string path) =>
            path.Replace('\\', '/').Contains(PackPaths.ModelsRoot + "/");

        private void OnPreprocessModel()
        {
            if (!IsPackModel(assetPath)) return;
            var mi = (ModelImporter)assetImporter;

            mi.useFileScale = false;   // ignore the FBX's own unit scale
            mi.globalScale = 1f;       // 1 unit in Blender == 1 unit (metre) in Unity

            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.importVisibility = false;
            mi.addCollider = false;
            // Geometry only — the pack's materials are generated from the same
            // measured C# tables the game builds its runtime materials from, so an
            // imported FBX material would be a second, wrong source of truth.
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.meshCompression = ModelImporterMeshCompression.Off;

            // Authored split/weighted normals, same reasoning as the game's importer:
            // these were exported with mesh_smooth_type='EDGE' after a Weighted
            // Normal pass, and recomputing from a smoothing angle softens exactly
            // the creases the models were built around.
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;

            mi.isReadable = true;
        }

        public override uint GetVersion() => 1;
    }
}
