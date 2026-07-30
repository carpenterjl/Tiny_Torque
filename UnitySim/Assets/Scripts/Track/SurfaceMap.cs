using System.Collections.Generic;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Per-tile surface properties resolved from a wheel's ground contact.
    /// frictionMult scales the wheel friction stiffness; rollingResist is an
    /// extra brake torque; boostAccel/bumpAmp drive boost pads and rumble strips.
    /// </summary>
    public struct SurfaceInfo
    {
        public float frictionMult;
        public float rollingResist;
        public float boostAccel;
        public float bumpAmp;
        public float roughAmp;    // roughness force amplitude (fraction of per-wheel weight)
        public float roughLen;    // roughness feature wavelength (m)

        public static readonly SurfaceInfo Baseline = new SurfaceInfo { frictionMult = 1f };
    }

    /// <summary>
    /// Floor-surface lookup for a wheel contact, across both track sources.
    ///
    /// On a tile map it lives on the single invisible floor slab and maps a hit
    /// point to a tile index. On a hand-authored scene track it additionally
    /// resolves Unity Terrain: each terrain's alphamap is baked ONCE at bind into
    /// one floor id per texel, so the per-wheel lookup stays two multiplies and an
    /// array index. Anywhere with no SurfaceMap at all (the oval scene, the garage,
    /// the diff-drive scene) <see cref="At"/> is never reached — those are untouched.
    /// </summary>
    public sealed class SurfaceMap : MonoBehaviour
    {
        public static SurfaceMap Active { get; private set; }

        // ---- tile-map binding ----
        private TrackDesign _design;
        private Collider _floor;

        // ---- scene-track binding ----
        private readonly List<TerrainSlot> _terrainSlots = new List<TerrainSlot>();
        private readonly Dictionary<Collider, int> _terrainOfCollider =
            new Dictionary<Collider, int>();
        /// <summary>Floor type for an untagged mesh collider on a scene track;
        /// -1 on a tile map, where an untagged collider means "not the floor".</summary>
        private int _sceneFallback = -1;

        private readonly Dictionary<Collider, int> _tagCache = new Dictionary<Collider, int>();

        /// <summary>
        /// One terrain, flattened. <see cref="floorIds"/> is already mapped through
        /// the TerrainFloorTable, so the hot path never touches a TerrainLayer.
        /// </summary>
        private sealed class TerrainSlot
        {
            public Terrain terrain;
            public Vector3 origin;      // terrain.transform.position
            public float sizeX, sizeZ;  // terrainData.size
            public int amW, amH;        // alphamapWidth / alphamapHeight
            public byte[] floorIds;     // amW * amH
        }

        // -------------------------------------------------------------------
        // binding
        // -------------------------------------------------------------------

        /// <summary>Bind to a tile map: positional lookup against the floor slab.</summary>
        public void Bind(TrackDesign design, Collider floorCollider)
        {
            _design = design;
            _floor = floorCollider;
            _sceneFallback = -1;
            _terrainSlots.Clear();
            _terrainOfCollider.Clear();
            _tagCache.Clear();
        }

        /// <summary>
        /// Bind to a hand-authored scene: bake every Terrain's alphamap down to one
        /// floor id per texel, and set the fallback for mesh colliders carrying no
        /// SurfaceTag.
        ///
        /// The bake is the whole point. <see cref="At"/> runs once per grounded
        /// wheel per physics step — at 400 Hz with an eight-car grid that is 12 800
        /// calls a second — and the naive `GetAlphamaps(x, z, 1, 1)` per call is
        /// unacceptable twice over: it allocates a managed float[1,1,layers] every
        /// time, and it is an engine interop call per wheel. Reading each terrain
        /// once and collapsing it to a byte array turns the hot path into arithmetic.
        /// </summary>
        public void BindScene(SceneTrackDescriptor d)
        {
            _design = null;
            _floor = null;
            _tagCache.Clear();
            _terrainSlots.Clear();
            _terrainOfCollider.Clear();
            _sceneFallback = d != null ? Mathf.Max(0, d.sceneFallbackFloor) : -1;

            if (d == null) return;
            var terrains = d.Terrains();
            if (terrains == null || terrains.Length == 0) return;

            var table = d.terrainFloors;
            if (table == null)
            {
                Debug.LogWarning($"[SurfaceMap] {terrains.Length} terrain(s) in " +
                    $"'{d.displayName}' but no TerrainFloorTable — every one of them " +
                    "will drive as the baseline. Assign one on the descriptor.");
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            long texels = 0;
            foreach (var t in terrains)
            {
                var slot = BakeTerrain(t, table);
                if (slot == null) continue;
                _terrainSlots.Add(slot);
                _terrainOfCollider[t.GetComponent<TerrainCollider>()] = _terrainSlots.Count - 1;
                texels += slot.floorIds.Length;
            }
            watch.Stop();

            // Timed and logged the way TrackFactory times mesh cooking: a scene that
            // grows a terrain or doubles its alphamap resolution should show up in
            // the log, not as "loading feels slower now". Past roughly 250 ms the
            // answer is to bake these to a side asset at import time.
            Debug.Log($"[SurfaceMap] baked {_terrainSlots.Count} terrain(s), " +
                      $"{texels / 1024} Ktexels in {watch.ElapsedMilliseconds} ms");
        }

        private static TerrainSlot BakeTerrain(Terrain t, TerrainFloorTable table)
        {
            if (t == null || t.terrainData == null) return null;
            var td = t.terrainData;
            var layers = td.terrainLayers;
            if (layers == null || layers.Length == 0) return null;

            int w = td.alphamapWidth, h = td.alphamapHeight;

            // Map each LAYER ASSET through the table up front, so the per-texel loop
            // is an int lookup. terrainLayers is PER-TERRAIN: layer 0 may be dirt on
            // one terrain and grass on the next, so a raw layer index must never
            // reach the floor id.
            var layerFloor = new int[layers.Length];
            for (int l = 0; l < layers.Length; l++) layerFloor[l] = table.FloorFor(layers[l]);

            // Indexing is [z, x, layer] — y-then-x. Getting this backwards yields a
            // track that is right along one axis and transposed along the other,
            // which reads as "grip is randomly wrong in patches".
            float[,,] maps = td.GetAlphamaps(0, 0, w, h);
            var ids = new byte[w * h];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int best = 0;
                    float bestW = -1f;
                    for (int l = 0; l < layers.Length; l++)
                    {
                        float v = maps[z, x, l];
                        if (v > bestW) { bestW = v; best = l; }
                    }
                    ids[z * w + x] = (byte)Mathf.Clamp(layerFloor[best], 0, 255);
                }
            }

            var size = td.size;
            return new TerrainSlot
            {
                terrain = t,
                origin = t.transform.position,
                sizeX = Mathf.Max(0.001f, size.x),
                sizeZ = Mathf.Max(0.001f, size.z),
                amW = w,
                amH = h,
                floorIds = ids,
            };
        }

        private void OnEnable() => Active = this;

        private void OnDisable()
        {
            if (Active == this) Active = null;
            _tagCache.Clear();
            _terrainOfCollider.Clear();
        }

        // -------------------------------------------------------------------
        // lookup
        // -------------------------------------------------------------------

        /// <summary>Surface under a wheel contact; baseline off any known surface.</summary>
        public static SurfaceInfo At(in WheelHit hit)
        {
            var a = Active;
            if (a == null || hit.collider == null) return SurfaceInfo.Baseline;

            // 1. Tagged colliders win. On a tile map that is the spline ribbon runs;
            //    on a scene track it is the road mesh, and a SurfaceTag on a
            //    terrain's own object is a deliberate whole-terrain override.
            if (!a._tagCache.TryGetValue(hit.collider, out int tagged))
            {
                var tag = hit.collider.GetComponent<SurfaceTag>();
                tagged = tag != null ? tag.floorType : -1;
                a._tagCache[hit.collider] = tagged;
            }
            if (tagged >= 0) return FromFloor(tagged);

            // 2. Terrain: the alphamap's dominant layer, pre-baked to a floor id.
            if (a._terrainSlots.Count > 0 && a.TryTerrain(hit.collider, hit.point, out int ti))
                return FromFloor(ti);

            // 3. The tile map's floor slab, positionally.
            if (a._design != null && hit.collider == a._floor) return a.Lookup(hit.point);

            // 4. An untagged mesh collider on a scene track. Asphalt by default
            //    rather than the 1.0 baseline, so a road someone forgot to tag
            //    drives like a road instead of like nothing.
            if (a._sceneFallback >= 0) return FromFloor(a._sceneFallback);

            return SurfaceInfo.Baseline;
        }

        /// <summary>
        /// Which terrain, and what is painted there.
        ///
        /// "Which terrain" needs no spatial index: hit.collider IS that terrain's
        /// TerrainCollider, so Unity has already answered it and one dictionary
        /// probe is the whole cost. Overlapping or non-grid-aligned terrains fall
        /// out for free. Negatives are cached too, so a wheel on a prop costs a
        /// probe rather than a GetComponent every step.
        ///
        /// There is deliberately NO second-level position cache. Four wheels
        /// alternating would thrash a single-entry memo, and the hash and compare
        /// cost more than the two multiplies they would save.
        /// </summary>
        private bool TryTerrain(Collider col, Vector3 p, out int floorId)
        {
            floorId = 0;
            if (!_terrainOfCollider.TryGetValue(col, out int i))
            {
                i = -1;
                if (col is TerrainCollider)
                {
                    var t = col.GetComponent<Terrain>();
                    if (t != null)
                    {
                        // A collider not seen at bind time — its Terrain was added
                        // after the bake, or the component was re-fetched. Match the
                        // Terrain itself; a terrain that was never baked declines and
                        // caches the -1, rather than silently reading another
                        // terrain's texels.
                        for (int k = 0; k < _terrainSlots.Count; k++)
                            if (_terrainSlots[k].terrain == t) { i = k; break; }
                    }
                }
                _terrainOfCollider[col] = i;
            }
            if (i < 0) return false;

            var s = _terrainSlots[i];
            int ax = Mathf.Clamp((int)((p.x - s.origin.x) / s.sizeX * s.amW), 0, s.amW - 1);
            int az = Mathf.Clamp((int)((p.z - s.origin.z) / s.sizeZ * s.amH), 0, s.amH - 1);
            floorId = s.floorIds[az * s.amW + ax];
            return true;
        }

        private static SurfaceInfo FromFloor(int type)
        {
            var f = TrackCatalog.Floors[Mathf.Clamp(type, 0, TrackCatalog.Floors.Length - 1)];
            return new SurfaceInfo
            {
                frictionMult = f.frictionMult,
                rollingResist = f.rollingResist,
                boostAccel = f.boostAccel,
                bumpAmp = f.bumpAmp,
                roughAmp = f.roughAmp,
                roughLen = f.roughLen,
            };
        }

        private SurfaceInfo Lookup(Vector3 worldPoint)
        {
            if (!_design.WorldToTile(worldPoint, out int tx, out int tz))
                return SurfaceInfo.Baseline;
            return FromFloor(_design.FloorAt(tx, tz));
        }
    }
}
