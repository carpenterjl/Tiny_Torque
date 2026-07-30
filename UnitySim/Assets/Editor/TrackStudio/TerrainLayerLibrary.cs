using System.IO;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Gets a terrain ready to be painted with a floor type, creating whatever is
    /// missing rather than refusing.
    ///
    /// Terrain painting has three prerequisites that are easy to get wrong and
    /// invisible until a stroke does nothing: the floor needs a
    /// <see cref="TerrainLayer"/> asset, the scene's <see cref="TerrainFloorTable"/>
    /// needs a row tying that asset to the floor index, and the terrain itself needs
    /// the layer in its <c>terrainLayers</c> array. Making the author satisfy all
    /// three by hand — for 18 floors across 13 terrains — is asking them to do
    /// bookkeeping the tool can do, so the brush provisions instead.
    ///
    /// The generated layer's texture is <see cref="FloorTypeDef.Tex"/>, the same
    /// procedural texture the tile map paints that floor with, so a terrain painted
    /// dirt looks like the dirt tiles rather than like a placeholder.
    /// </summary>
    internal static class TerrainLayerLibrary
    {
        private const string Dir = "Assets/TrackData/TerrainLayers";

        /// <summary>Metres per texture repeat. Terrain layers tile far coarser than
        /// a 2 m track tile, and a 1 m repeat on a 500 m terrain shimmers.</summary>
        private const float TileSize = 6f;

        /// <summary>
        /// The index of <paramref name="floorType"/> in this terrain's layer array,
        /// creating the asset, the table row and the terrain slot as needed.
        /// Returns -1 only if the floor has no texture to build a layer from.
        /// </summary>
        internal static int EnsureLayer(TerrainData td, TerrainFloorTable table, int floorType)
        {
            if (td == null || table == null) return -1;

            int existing = IndexOf(td, table, floorType);
            if (existing >= 0) return existing;

            // Reuse before creating. The scene's table usually already names a layer
            // for this floor — it is just not on THIS terrain — and generating a
            // second asset for it would give one map two different-looking grasses
            // that drive identically, which is a confusing thing to inherit.
            var layer = LayerFromTable(table, floorType) ?? FindOrCreateAsset(floorType);
            if (layer == null) return -1;

            EnsureRow(table, layer, floorType);
            return Append(td, layer);
        }

        /// <summary>The layer this scene already uses for a floor, or null.</summary>
        private static TerrainLayer LayerFromTable(TerrainFloorTable table, int floorType)
        {
            for (int i = 0; i < table.rows.Length; i++)
                if (table.rows[i].floorType == floorType && table.rows[i].layer != null)
                    return table.rows[i].layer;
            return null;
        }

        /// <summary>The terrain's own layer index that maps to this floor, or -1.
        /// Resolved per terrain because <c>terrainLayers</c> is per-terrain: layer 2
        /// may be grass on one tile and gravel on the next.</summary>
        internal static int IndexOf(TerrainData td, TerrainFloorTable table, int floorType)
        {
            var layers = td != null ? td.terrainLayers : null;
            if (layers == null || table == null) return -1;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null) continue;
                // Only a row counts. Falling back to FloorFor's default would make
                // every unmapped layer answer "yes" for the default floor and paint
                // the wrong channel.
                if (HasRow(table, layers[i]) && table.FloorFor(layers[i]) == floorType)
                    return i;
            }
            return -1;
        }

        // -------------------------------------------------------------------

        private static bool HasRow(TerrainFloorTable table, TerrainLayer layer)
        {
            for (int i = 0; i < table.rows.Length; i++)
                if (table.rows[i].layer == layer) return true;
            return false;
        }

        private static void EnsureRow(TerrainFloorTable table, TerrainLayer layer, int floorType)
        {
            if (HasRow(table, layer)) return;
            Undo.RecordObject(table, "Map terrain layer");
            var rows = table.rows;
            System.Array.Resize(ref rows, rows.Length + 1);
            rows[rows.Length - 1] = new TerrainFloorTable.Row
            {
                layer = layer,
                floorType = floorType,
            };
            table.rows = rows;
            EditorUtility.SetDirty(table);
        }

        private static int Append(TerrainData td, TerrainLayer layer)
        {
            var layers = td.terrainLayers;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i] == layer) return i;

            // Assigning a longer array is how a layer is added; Unity widens the
            // alphamap and zeroes the new channel, so nothing already painted moves.
            Undo.RegisterCompleteObjectUndo(td, "Add terrain layer");
            System.Array.Resize(ref layers, layers.Length + 1);
            layers[layers.Length - 1] = layer;
            td.terrainLayers = layers;
            EditorUtility.SetDirty(td);
            return layers.Length - 1;
        }

        /// <summary>
        /// The <c>.terrainlayer</c> asset for a floor, created on first use.
        /// Named from the floor's stable <c>id</c> rather than its index, so
        /// appending a floor to the catalog never re-points an existing asset.
        /// </summary>
        private static TerrainLayer FindOrCreateAsset(int floorType)
        {
            if (floorType < 0 || floorType >= TrackCatalog.Floors.Length) return null;
            var def = TrackCatalog.Floors[floorType];
            string path = $"{Dir}/Floor_{def.id}.terrainlayer";

            var existing = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (existing != null) return existing;

            var tex = SaveTexture(def);
            if (tex == null)
            {
                TrackStudio.Warn($"floor '{def.id}' has no texture, so no TerrainLayer " +
                                 "could be generated for it.");
                return null;
            }

            var layer = new TerrainLayer
            {
                diffuseTexture = tex,
                tileSize = new Vector2(TileSize, TileSize),
                specular = Color.black,
                metallic = 0f,
                smoothness = 0f,
            };
            AssetDatabase.CreateAsset(layer, path);
            AssetDatabase.SaveAssets();
            TrackStudio.Log($"LAYER created '{path}' for floor {floorType} ({def.label}).");
            return layer;
        }

        /// <summary>
        /// <c>FloorTypeDef.Tex</c> is generated at runtime and lives only in memory,
        /// but a TerrainLayer holds an asset reference — so it has to be written out
        /// once as a PNG that both the editor and a player build can load.
        /// </summary>
        private static Texture2D SaveTexture(FloorTypeDef def)
        {
            string path = $"{Dir}/Floor_{def.id}.png";
            var onDisk = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (onDisk != null) return onDisk;

            var src = def.Tex;
            if (src == null) return null;

            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(path, src.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            // Terrain splats repeat, and the default clamp shows a seam at every
            // tile edge.
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.mipmapEnabled = true;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
