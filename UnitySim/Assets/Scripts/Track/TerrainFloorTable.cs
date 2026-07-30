using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Maps a Unity <see cref="TerrainLayer"/> to a <c>TrackCatalog.Floors</c>
    /// index, so a painted terrain drives like the thing it looks like.
    ///
    /// Without this, terrain is invisible to the physics: <c>SurfaceMap.At</c>
    /// resolves a wheel contact through a SurfaceTag or the tile floor slab, and a
    /// TerrainCollider hit matches neither — every terrain in the project returned
    /// the 1.0 baseline no matter what it was painted with.
    ///
    /// <b>Why an asset and not a code table.</b> Every other palette in this project
    /// (TrackCatalog.Floors, PartVisualFactory.AccentTokens, CosmeticCatalog.Tokens)
    /// is a static C# table, and that is right for them because they key on strings
    /// the code owns. A TerrainLayer is an ASSET REFERENCE. A code table would have
    /// to key on the layer's name, and the layer name is precisely the thing an
    /// artist renames — it would break silently, painting a mountain with ice.
    /// So the split is: asset references on the left, the existing append-only
    /// floor index on the right.
    ///
    /// <b>It lives in Scripts/, not an Editor/ folder</b>, unlike ScatterPreset:
    /// the runtime reads this at Awake to bake its lookup, so it must reach a
    /// player build.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Terrain Floor Table",
                     fileName = "TerrainFloorTable")]
    public sealed class TerrainFloorTable : ScriptableObject
    {
        [System.Serializable]
        public struct Row
        {
            public TerrainLayer layer;

            /// <summary>Index into <c>TrackCatalog.Floors</c> — the same append-only
            /// id persisted in track JSON and read by <c>SurfaceTag.floorType</c>.</summary>
            [FloorType]
            public int floorType;
        }

        [Tooltip("One row per TerrainLayer used anywhere in the scene. The validator " +
                 "fails on a layer with no row rather than letting it fall to the default.")]
        public Row[] rows = System.Array.Empty<Row>();

        [Tooltip("Floor type for a layer with no row. 0 = dirt (frictionMult 1.00), " +
                 "which is the physics baseline.")]
        public int defaultFloorType;

        /// <summary>
        /// The floor index for a layer, or <see cref="defaultFloorType"/>.
        /// Called once per alphamap texel during the bake, never per wheel.
        /// </summary>
        public int FloorFor(TerrainLayer layer)
        {
            if (layer != null)
                for (int i = 0; i < rows.Length; i++)
                    if (rows[i].layer == layer) return rows[i].floorType;
            return defaultFloorType;
        }
    }
}
