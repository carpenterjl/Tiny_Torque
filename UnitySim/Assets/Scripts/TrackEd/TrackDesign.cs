using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// One placed track item (wall/obstacle/misc). Position is world XZ, already
    /// snapped at placement time; the map is centered on the world origin.
    /// </summary>
    [Serializable]
    public class PlacedItem
    {
        public string itemId = "";   // TrackCatalog key, e.g. "ramp", "wall_tall"
        public float x, z;           // world position
        public float y;              // height hint — items drop onto the surface below (0 = floor)
        public float yawDeg;         // heading, 15-degree steps from the editor
        public int order = -1;       // checkpoint sequence; -1 for everything else
        // Uniform size multiplier on the whole item root — visual, collision
        // hull and lamp offset alike. The map ports lean on this heavily (the
        // Blender layouts vary nearly every placement between 0.55x and 1.9x).
        // JsonUtility gives a missing field 0, so TrackDesign.EnsureItems has
        // to repair it or every pre-scale save would build items at zero size.
        public float scale = 1f;
        // "Scenery, whatever the catalog says." A dynamic ItemDef normally gets
        // a Rigidbody per collidered piece; the ported maps place hundreds of
        // dominoes, bricks and pumpkins as decorative fill, and that many live
        // bodies is a physics bill for nothing. Pinned ones stay put and stay
        // eligible for static batching.
        public bool pinned;

        public PlacedItem Clone() => (PlacedItem)MemberwiseClone();
    }

    /// <summary>
    /// A buildable track map: a WxL grid of floor-tile type ids plus a list of
    /// placed items. Flat JsonUtility-friendly fields; floor is row-major
    /// (idx = tz * width + tx) and tile ids index <see cref="TrackCatalog.Floors"/>.
    /// The map is centered on the world origin.
    /// </summary>
    [Serializable]
    public class TrackDesign
    {
        public string name = "New Track";
        public int width = 20;        // tiles along X
        public int length = 20;       // tiles along Z
        public float tileSize = 1f;   // meters per tile (RC-scale world)
        public int[] floor;           // width*length floor-type ids
        public List<PlacedItem> items = new List<PlacedItem>();
        public List<SplineSpec> splines = new List<SplineSpec>();
        /// <summary>
        /// <see cref="MapAmbience"/> key: sky, fog, key light and glow points
        /// for this map. "" is the neutral daylight every map had before the
        /// TinyTorque map ports, so old saves are unaffected.
        /// </summary>
        public string ambience = "";

        /// <summary>Tile-count ceiling per side, enforced by <see cref="Resize"/>.</summary>
        public const int MaxTiles = 80;

        /// <summary>Deep copy via JSON round-trip (same idiom as VehicleDesign).</summary>
        public TrackDesign Clone() => JsonUtility.FromJson<TrackDesign>(JsonUtility.ToJson(this));

        public static TrackDesign Default(int w = 20, int l = 20)
        {
            var d = new TrackDesign { width = w, length = l, floor = new int[w * l] };
            return d; // all zeros = default floor type (dirt)
        }

        // ---- grid helpers -------------------------------------------------

        public bool InBounds(int tx, int tz) => tx >= 0 && tx < width && tz >= 0 && tz < length;

        public int FloorAt(int tx, int tz)
        {
            if (!InBounds(tx, tz) || floor == null || floor.Length != width * length) return 0;
            return Mathf.Clamp(floor[tz * width + tx], 0, TrackCatalog.Floors.Length - 1);
        }

        public void SetFloor(int tx, int tz, int type)
        {
            EnsureFloor();
            if (!InBounds(tx, tz)) return;
            floor[tz * width + tx] = Mathf.Clamp(type, 0, TrackCatalog.Floors.Length - 1);
        }

        /// <summary>Repair missing spline data (old or hand-edited JSON).</summary>
        public void EnsureSplines()
        {
            splines ??= new List<SplineSpec>();
            foreach (var s in splines) s?.EnsureArrays();
            splines.RemoveAll(s => s == null);
        }

        /// <summary>
        /// Repair item defaults (old or hand-edited JSON). JsonUtility fills a
        /// field the document does not mention with the type's zero, not the
        /// C# initialiser — so every map saved before per-item scale existed
        /// deserialises with scale 0, which would build every prop at nothing.
        /// </summary>
        public void EnsureItems()
        {
            items ??= new List<PlacedItem>();
            items.RemoveAll(it => it == null);
            foreach (var it in items)
                if (!(it.scale > 0f)) it.scale = 1f;   // also catches NaN
        }

        /// <summary>Repair a missing/mis-sized floor array (old or hand-edited JSON).</summary>
        public void EnsureFloor()
        {
            if (floor == null || floor.Length != width * length)
            {
                var old = floor;
                floor = new int[width * length];
                if (old != null)
                    Array.Copy(old, floor, Mathf.Min(old.Length, floor.Length));
            }
        }

        /// <summary>World-space center of a tile (map centered on the origin, y = 0).</summary>
        public Vector3 TileCenter(int tx, int tz)
        {
            float x = (tx - width * 0.5f + 0.5f) * tileSize;
            float z = (tz - length * 0.5f + 0.5f) * tileSize;
            return new Vector3(x, 0f, z);
        }

        /// <summary>Map a world point onto the tile grid. False when outside the map.</summary>
        public bool WorldToTile(Vector3 world, out int tx, out int tz)
        {
            tx = Mathf.FloorToInt(world.x / tileSize + width * 0.5f);
            tz = Mathf.FloorToInt(world.z / tileSize + length * 0.5f);
            return InBounds(tx, tz);
        }

        public float WorldWidth => width * tileSize;
        public float WorldLength => length * tileSize;

        // ---- resize -------------------------------------------------------

        /// <summary>
        /// Grow/shrink the map by whole rows/columns per edge (negative shrinks),
        /// preserving existing tiles and keeping items over the same tiles. Because
        /// the map stays centered on the origin, an asymmetric resize shifts the
        /// grid under the world — so surviving items are offset by half the delta
        /// and items falling outside the new bounds are culled.
        /// </summary>
        public void Resize(int addWest, int addEast, int addSouth, int addNorth, int fillType = 0)
        {
            EnsureFloor();
            // The ceiling moved 60 → 80 for the TinyTorque map ports: the
            // enchanted vale is 112 m across, which is 56 tiles at the 2 m
            // tileSize those maps use.
            int newW = Mathf.Clamp(width + addWest + addEast, 4, MaxTiles);
            int newL = Mathf.Clamp(length + addSouth + addNorth, 4, MaxTiles);
            // Re-derive the actually applied deltas after clamping (east/north absorb overflow).
            addEast = newW - width - addWest;
            addNorth = newL - length - addSouth;

            var next = new int[newW * newL];
            for (int i = 0; i < next.Length; i++) next[i] = fillType;
            for (int z = 0; z < length; z++)
            {
                int nz = z + addSouth;
                if (nz < 0 || nz >= newL) continue;
                for (int x = 0; x < width; x++)
                {
                    int nx = x + addWest;
                    if (nx < 0 || nx >= newW) continue;
                    next[nz * newW + nx] = floor[z * width + x];
                }
            }

            // Keep items over the same tiles. A tile at index x maps to world
            // (x - W/2 + .5)*s; after the resize that tile sits at index x+addWest
            // with width newW, i.e. world' = (x + addWest - newW/2 + .5)*s.
            // Items therefore shift by (addWest - (newW-W)/2)*s (same for Z).
            float dx = (addWest - (newW - width) * 0.5f) * tileSize;
            float dz = (addSouth - (newL - length) * 0.5f) * tileSize;

            width = newW; length = newL; floor = next;

            float halfW = WorldWidth * 0.5f, halfL = WorldLength * 0.5f;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                items[i].x += dx;
                items[i].z += dz;
                if (items[i].x < -halfW || items[i].x > halfW ||
                    items[i].z < -halfL || items[i].z > halfL)
                    items.RemoveAt(i);
            }

            // Splines shift with the grid too; a spline is culled only when EVERY
            // point falls outside the new bounds (partial overhang is legal).
            EnsureSplines();
            for (int i = splines.Count - 1; i >= 0; i--)
            {
                var s = splines[i];
                bool anyInside = false;
                for (int k = 0; k < s.points.Count; k++)
                {
                    var p = s.points[k];
                    p.x += dx; p.z += dz;
                    s.points[k] = p;
                    if (p.x >= -halfW && p.x <= halfW && p.z >= -halfL && p.z <= halfL)
                        anyInside = true;
                }
                if (!anyInside) splines.RemoveAt(i);
            }
        }

        // ---- item queries ---------------------------------------------------

        /// <summary>First item with the given catalog behavior, or null.</summary>
        public PlacedItem FindByBehavior(ItemBehavior behavior)
        {
            foreach (var it in items)
            {
                var def = TrackCatalog.Item(it.itemId);
                if (def != null && def.behavior == behavior) return it;
            }
            return null;
        }

        public int CountBehavior(ItemBehavior behavior)
        {
            int n = 0;
            foreach (var it in items)
            {
                var def = TrackCatalog.Item(it.itemId);
                if (def != null && def.behavior == behavior) n++;
            }
            return n;
        }

        /// <summary>Checkpoints sorted by their order field.</summary>
        public List<PlacedItem> OrderedCheckpoints()
        {
            var cps = new List<PlacedItem>();
            foreach (var it in items)
            {
                var def = TrackCatalog.Item(it.itemId);
                if (def != null && def.behavior == ItemBehavior.Checkpoint) cps.Add(it);
            }
            cps.Sort((a, b) => a.order.CompareTo(b.order));
            return cps;
        }

        /// <summary>Renumber checkpoint orders to a dense 0..n-1 sequence.</summary>
        public void NormalizeCheckpointOrders()
        {
            var cps = OrderedCheckpoints();
            for (int i = 0; i < cps.Count; i++) cps[i].order = i;
        }
    }
}
