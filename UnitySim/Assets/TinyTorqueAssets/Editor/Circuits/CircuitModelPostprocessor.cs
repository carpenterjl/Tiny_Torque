using UnityEditor;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Import settings for the circuit FBX files under
    /// <c>Assets/TinyTorqueAssets/Circuits/</c>.
    ///
    /// Separate from <see cref="PackModelPostprocessor"/> because two settings
    /// genuinely differ and both matter:
    ///
    /// <list type="bullet">
    /// <item><b>32-bit indices.</b> Spa's terrain is one 52 662-face mesh and its
    /// armco runs to 12 454 vertices per side. The 16-bit default silently
    /// splits a mesh past 65 535 vertices into pieces, and a split road is a
    /// road with a seam down it.</item>
    /// <item><b>Bake Axis Conversion off</b>, per UNITY_EXPORT.md §2 — but the
    /// conversion still has to be in the vertices, and it is: the exporter runs
    /// with <c>bake_space_transform=True</c>. That pairing is the load-bearing
    /// one, because this pipeline reads the <c>Mesh</c> out of the asset rather
    /// than instantiating the model (see <c>CircuitImport.LoadMesh</c>), so any
    /// conversion left on a node transform never happens.
    /// <para>Measured, not argued. With Blender baking off, the marker arrived
    /// with Y and Z unswapped — the circuit on its side — under <i>both</i> of
    /// Unity's Bake Axis Conversion settings, which differed only in which axis
    /// got negated. Neither Unity setting touches Blender's node transform, so
    /// neither could fix it. <c>CircuitAxisTest</c> re-measures on every run;
    /// change either half and it says so before anything is placed.</para></item>
    /// </list>
    ///
    /// Normals are imported, never recalculated: the kerbs and the road are
    /// deliberately hard-edged against each other and a smoothing angle would
    /// round exactly the crease the model was built around.
    /// </summary>
    public sealed class CircuitModelPostprocessor : AssetPostprocessor
    {
        private static bool IsCircuitModel(string path) =>
            path.Replace('\\', '/').Contains(CircuitPaths.Root + "/");

        private void OnPreprocessModel()
        {
            if (!IsCircuitModel(assetPath)) return;
            var mi = (ModelImporter)assetImporter;

            mi.useFileScale = false;          // Convert Units: off
            mi.globalScale = 1f;              // Scale Factor: 1
            mi.bakeAxisConversion = false;

            mi.importCameras = false;
            mi.importLights = false;
            mi.importAnimation = false;
            mi.importBlendShapes = false;
            mi.importVisibility = false;
            mi.addCollider = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.indexFormat = ModelImporterIndexFormat.UInt32;

            // Geometry only. The palette is rebuilt from the PBR numbers the
            // exporter probed out of the Blender node trees and bound by name —
            // an FBX-imported material would be a second, worse source of truth.
            mi.materialImportMode = ModelImporterMaterialImportMode.None;

            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.None;

            // The scene builder combines tree prototypes into chunk meshes and
            // cooks mesh colliders, both of which read vertices back.
            mi.isReadable = true;
            mi.optimizeMeshPolygons = false;
            mi.optimizeMeshVertices = false;
        }

        public override uint GetVersion() => 2;
    }
}
