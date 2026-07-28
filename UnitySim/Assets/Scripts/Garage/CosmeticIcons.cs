using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Palette thumbnails for the cosmetics, photographed from the real meshes
    /// by <see cref="PartIconFactory.Snapshot"/> — the same one-shot camera the
    /// garage and track-builder palettes use, so a cosmetic's icon can never
    /// drift from what actually gets bolted on.
    ///
    /// Cosmetics land on the viz layer that snapshot culls to, so this is a
    /// cache and nothing else.
    /// </summary>
    public static class CosmeticIcons
    {
        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>();

        public static Texture2D Get(string meshKey, int size = 64)
        {
            if (string.IsNullOrEmpty(meshKey)) return null;
            if (_cache.TryGetValue(meshKey, out var t) && t != null) return t;
            t = PartIconFactory.Snapshot(p => CosmeticCatalog.Build(p, meshKey), size);
            _cache[meshKey] = t;
            return t;
        }
    }
}
