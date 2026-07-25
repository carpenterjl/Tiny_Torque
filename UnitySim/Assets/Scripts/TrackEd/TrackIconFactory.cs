using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Palette icons for the track builder. Floor tiles show their generated
    /// texture directly; items are photographed from their real
    /// <see cref="TrackFactory.BuildItemVisual"/> geometry via the shared
    /// <see cref="PartIconFactory.Snapshot"/> so thumbnails never drift.
    /// </summary>
    public static class TrackIconFactory
    {
        private static readonly Dictionary<string, Texture2D> _cache = new Dictionary<string, Texture2D>();

        public static Texture2D FloorIcon(int floorType)
        {
            var floors = TrackCatalog.Floors;
            if (floorType < 0 || floorType >= floors.Length) floorType = 0;
            return floors[floorType].Tex;
        }

        public static Texture2D ItemIcon(string itemId)
        {
            if (_cache.TryGetValue(itemId, out var t) && t != null) return t;
            var def = TrackCatalog.Item(itemId);
            if (def == null) return Texture2D.grayTexture;

            t = PartIconFactory.Snapshot(parent =>
            {
                var go = TrackFactory.BuildItemVisual(def, parent);
                // Snapshot's camera culls to VizLayer; strip colliders so the
                // temporary geometry can't touch physics for its single frame.
                foreach (var col in go.GetComponentsInChildren<Collider>(true))
                    Object.Destroy(col);
                foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                    tr.gameObject.layer = PartVisualFactory.VizLayer;
            }, 64);
            _cache[itemId] = t;
            return t;
        }
    }
}
