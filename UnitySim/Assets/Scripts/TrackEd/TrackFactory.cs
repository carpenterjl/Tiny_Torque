using System.Collections.Generic;
using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>Result of building a <see cref="TrackDesign"/> into live objects.</summary>
    public sealed class BuiltTrack
    {
        public GameObject root;
        public LapTimer lapTimer;          // null when the map has no finish item
        public Vector3 spawnPos;
        public Quaternion spawnRot;
        public Collider floorCollider;     // the single floor slab (paint raycasts, SurfaceMap)
        public Renderer[,] tileRenderers;  // [tx, tz] visual per tile (builder repaint)
        public List<GameObject> splineRoots = new List<GameObject>();  // one per spline
        public List<MeshCollider> ribbonColliders = new List<MeshCollider>(); // surface runs only

        /// <summary>
        /// EVERY spawn item on the map, dropped to the surface, with the placed
        /// item's <c>order</c> carried through as its team index.
        ///
        /// A race needs one grid and derives the rest of the row from it, so
        /// <see cref="spawnPos"/> stays exactly what it was. An arena needs a
        /// ring of them, and a team arena needs to know which end each belongs
        /// to — which is what <c>order</c> already means for checkpoints and
        /// costs nothing to reuse here.
        /// </summary>
        public List<SpawnPoint> spawns = new List<SpawnPoint>();
    }

    /// <summary>One authored spawn: where, facing which way, for whose team.</summary>
    public struct SpawnPoint
    {
        public Vector3 pos;
        public Quaternion rot;
        public int team;   // PlacedItem.order; 0/1 in a team arena, 0 elsewhere
    }

    /// <summary>
    /// Single build path from a <see cref="TrackDesign"/> to live GameObjects —
    /// used by BOTH the builder preview and the drive scene, so what you build is
    /// what you drive. Physics layout: ONE invisible floor-slab collider for the
    /// whole map (top at y = 0) plus collider-less thin visual boxes per tile;
    /// per-tile friction is a positional lookup via <see cref="SurfaceMap"/>.
    /// When <paramref name="interactive"/> is true, behavior is wired up
    /// (SurfaceMap, lap/checkpoint triggers, lights, dynamic rigidbodies).
    /// </summary>
    public static class TrackFactory
    {
        private static Material _surroundMat;
        private static PhysicsMaterial _floorPhys;
        private static PhysicsMaterial _propPhys;

        public static BuiltTrack Build(TrackDesign d, bool interactive)
        {
            d.EnsureFloor();
            d.EnsureSplines();
            d.EnsureItems();
            var built = new BuiltTrack { root = new GameObject("CustomTrack") };
            var rootT = built.root.transform;

            var amb = MapAmbience.Resolve(d.ambience);
            MapAmbience.Apply(amb, rootT);
            BuildSurround(rootT, amb);
            BuildFloor(d, rootT, interactive, built);
            BuildSplines(d, rootT, built);
            // New colliders must be queryable for the item/spawn drop raycasts.
            Physics.SyncTransforms();
            BuildItems(d, rootT, interactive, built);
            ResolveSpawn(d, built);

            return built;
        }

        /// <summary>
        /// Ribbon geometry for every spline. Colliders are built in BOTH modes:
        /// the drive scene needs them for physics, the builder for placement,
        /// painting, and insert raycasts.
        /// </summary>
        private static void BuildSplines(TrackDesign d, Transform parent, BuiltTrack built)
        {
            var splinesRoot = new GameObject("Splines");
            splinesRoot.transform.SetParent(parent, false);
            for (int i = 0; i < d.splines.Count; i++)
            {
                var root = RibbonMeshBuilder.Build(d.splines[i], i, splinesRoot.transform, colliders: true);
                built.splineRoots.Add(root);
                foreach (var run in root.GetComponentsInChildren<SplineRunMarker>())
                {
                    var mc = run.GetComponent<MeshCollider>();
                    if (mc != null) built.ribbonColliders.Add(mc);
                }
            }
        }

        /// <summary>
        /// Drop a point onto the highest drivable surface below it (ribbon runs +
        /// the floor slab only — never other items). Items placed on a ribbon
        /// therefore re-seat themselves whenever the spline is redrawn.
        /// </summary>
        public static bool DropToSurface(BuiltTrack built, Vector3 hint,
            out Vector3 pos, out Vector3 normal)
        {
            var ray = new Ray(hint + Vector3.up * 3f, Vector3.down);
            const float maxDist = 60f;
            bool found = false;
            float best = float.MaxValue;
            pos = new Vector3(hint.x, 0f, hint.z);
            normal = Vector3.up;

            if (built.floorCollider != null &&
                built.floorCollider.Raycast(ray, out var fhit, maxDist))
            {
                best = fhit.distance; pos = fhit.point; normal = fhit.normal; found = true;
            }
            foreach (var mc in built.ribbonColliders)
            {
                if (mc != null && mc.Raycast(ray, out var rhit, maxDist) && rhit.distance < best)
                {
                    best = rhit.distance; pos = rhit.point; normal = rhit.normal; found = true;
                }
            }
            return found;
        }

        // ---- floor --------------------------------------------------------

        /// <summary>
        /// The outfield the map floats on. Themed maps tint it to their own
        /// ground colour and need it big — the enchanted vale is 112 m across
        /// and its sky dome is 400 m out, so a 300 m plane would end inside
        /// the view as a hard edge with sky underneath it (the same horizon
        /// trap the Blender maps document).
        /// </summary>
        private static void BuildSurround(Transform parent, AmbienceDef amb)
        {
            var col = amb != null ? amb.groundColor : new Color(0.38f, 0.27f, 0.16f);
            if (_surroundMat == null || _surroundMat.color != col)
            {
                _surroundMat = TrackBuilder.StandardMat(col);
                _surroundMat.name = "SurroundGround";
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "SurroundGround";
            ground.transform.SetParent(parent, false);
            ground.transform.position = new Vector3(0f, -0.06f, 0f);
            ground.transform.localScale = new Vector3(70f, 1f, 70f); // 700 m
            ground.GetComponent<Renderer>().sharedMaterial = _surroundMat;
        }

        private static void BuildFloor(TrackDesign d, Transform parent, bool interactive, BuiltTrack built)
        {
            // One invisible slab collider for the whole map, top face at y = 0.
            var slab = new GameObject("FloorSlab");
            slab.transform.SetParent(parent, false);
            var box = slab.AddComponent<BoxCollider>();
            box.size = new Vector3(d.WorldWidth, 1f, d.WorldLength);
            box.center = new Vector3(0f, -0.5f, 0f);
            if (_floorPhys == null)
                _floorPhys = new PhysicsMaterial("TrackFloor")
                {
                    dynamicFriction = 0.6f,
                    staticFriction = 0.6f,
                    frictionCombine = PhysicsMaterialCombine.Average,
                };
            box.sharedMaterial = _floorPhys;
            built.floorCollider = box;

            if (interactive)
            {
                var map = slab.AddComponent<SurfaceMap>();
                map.Bind(d, box);
            }

            var floorRoot = new GameObject("Floor");
            floorRoot.transform.SetParent(parent, false);

            // Two floors, because the two callers want opposite things. The
            // builder repaints single tiles as you drag the brush, so it needs
            // one renderer per tile; the drive scene never repaints and only
            // wants the geometry cheap, and on a 56x56 ported map that is 3136
            // GameObjects it can do without.
            if (interactive) BuildFloorMerged(d, floorRoot.transform);
            else BuildFloorTiles(d, floorRoot.transform, built);
        }

        /// <summary>Per-tile renderers, so <c>RepaintTile</c> can swap one.</summary>
        private static void BuildFloorTiles(TrackDesign d, Transform parent, BuiltTrack built)
        {
            built.tileRenderers = new Renderer[d.width, d.length];
            for (int tz = 0; tz < d.length; tz++)
                for (int tx = 0; tx < d.width; tx++)
                {
                    var c = d.TileCenter(tx, tz);
                    var tile = TrackBuilder.Tile($"Tile_{tx}_{tz}", new Vector3(c.x, 0f, c.z),
                        d.tileSize, TrackCatalog.Floors[d.FloorAt(tx, tz)].Mat, parent);
                    built.tileRenderers[tx, tz] = tile.GetComponent<Renderer>();
                }
        }

        /// <summary>
        /// One merged mesh per floor type actually used. A quad per tile, welded
        /// into a single mesh — same pixels as the per-tile floor (the tiles were
        /// only ever seen from above), at one draw call per surface instead of a
        /// static-batching pass over thousands of objects.
        /// </summary>
        private static void BuildFloorMerged(TrackDesign d, Transform parent)
        {
            var byType = new Dictionary<int, List<Vector2Int>>();
            for (int tz = 0; tz < d.length; tz++)
                for (int tx = 0; tx < d.width; tx++)
                {
                    int t = d.FloorAt(tx, tz);
                    if (!byType.TryGetValue(t, out var list))
                        byType[t] = list = new List<Vector2Int>();
                    list.Add(new Vector2Int(tx, tz));
                }

            float h = d.tileSize * 0.5f;
            foreach (var kv in byType)
            {
                var tiles = kv.Value;
                var verts = new Vector3[tiles.Count * 4];
                var uvs = new Vector2[tiles.Count * 4];
                var norms = new Vector3[tiles.Count * 4];
                var tris = new int[tiles.Count * 6];
                for (int i = 0; i < tiles.Count; i++)
                {
                    var c = d.TileCenter(tiles[i].x, tiles[i].y);
                    int v = i * 4, t6 = i * 6;
                    verts[v + 0] = new Vector3(c.x - h, 0f, c.z - h);
                    verts[v + 1] = new Vector3(c.x - h, 0f, c.z + h);
                    verts[v + 2] = new Vector3(c.x + h, 0f, c.z + h);
                    verts[v + 3] = new Vector3(c.x + h, 0f, c.z - h);
                    uvs[v + 0] = Vector2.zero; uvs[v + 1] = Vector2.up;
                    uvs[v + 2] = Vector2.one; uvs[v + 3] = Vector2.right;
                    norms[v + 0] = norms[v + 1] = norms[v + 2] = norms[v + 3] = Vector3.up;
                    tris[t6 + 0] = v; tris[t6 + 1] = v + 1; tris[t6 + 2] = v + 2;
                    tris[t6 + 3] = v; tris[t6 + 4] = v + 2; tris[t6 + 5] = v + 3;
                }
                var mesh = new Mesh { name = $"Floor_{TrackCatalog.Floors[kv.Key].id}" };
                // 80x80 tiles is 25 600 vertices, comfortably past the 16-bit
                // index limit once a map uses only one or two surfaces.
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.uv = uvs;
                mesh.normals = norms;
                mesh.triangles = tris;
                mesh.RecalculateBounds();

                var go = new GameObject($"Floor_{TrackCatalog.Floors[kv.Key].id}");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = TrackCatalog.Floors[kv.Key].Mat;
            }
        }

        // ---- items ----------------------------------------------------------

        private static void BuildItems(TrackDesign d, Transform parent, bool interactive, BuiltTrack built)
        {
            // Wall-clock the item pass: since the mesh migration this is where
            // MeshColliders cook (once per unique mesh, engine-cached), and the
            // 1 200-item town is the load worth watching. If this line ever
            // reports hundreds of milliseconds, pre-bake with Physics.BakeMesh
            // jobs before instantiating.
            var cookWatch = System.Diagnostics.Stopwatch.StartNew();

            var itemsRoot = new GameObject("Items");
            itemsRoot.transform.SetParent(parent, false);

            // Finish first so checkpoints can reference the LapTimer.
            if (interactive)
            {
                var finish = d.FindByBehavior(ItemBehavior.Finish);
                if (finish != null)
                {
                    var (fp, fr) = ItemPose(built, finish);
                    built.lapTimer = MakeGateTrigger(fp, fr, itemsRoot.transform, 1.6f)
                        .AddComponent<LapTimer>();
                }
            }

            var checkpoints = interactive ? d.OrderedCheckpoints() : null;
            // Scenery that provably never moves gets static-batched at the end.
            // A themed map runs 40-60 mesh props; left unbatched that is a few
            // hundred draw calls on its own, more than the whole rest of the scene.
            var batchable = interactive
                ? new System.Collections.Generic.List<GameObject>() : null;

            for (int i = 0; i < d.items.Count; i++)
            {
                var it = d.items[i];
                var def = TrackCatalog.Item(it.itemId);
                if (def == null) continue; // legacy/unknown id: skip quietly

                var go = new GameObject($"Item_{i}_{def.id}");
                go.transform.SetParent(itemsRoot.transform, false);
                def.build(go.transform); // parent is at origin here
                var (pos, rot) = ItemPose(built, it);
                go.transform.SetPositionAndRotation(pos, rot);
                // Uniform scale on the root, so the authored visual, the
                // invisible hull and the lamp offset all move together. Set
                // AFTER build() because the hull helpers write localScale on
                // their own children.
                float s = it.scale > 0f ? it.scale : 1f;
                if (!Mathf.Approximately(s, 1f)) go.transform.localScale = Vector3.one * s;
                go.AddComponent<PlacedItemMarker>().index = i;

                if (!interactive) continue;

                // Anything with a Rigidbody or a behaviour component can move or
                // be toggled, so only inert scenery is eligible for batching —
                // and so is anything animated (a batched ghost cannot bob).
                // A pinned dynamic prop is inert by definition, so it batches too.
                bool live = def.dynamic && !it.pinned;
                if (!live && def.behavior == ItemBehavior.None && !def.animated)
                    batchable.Add(go);

                if (live)
                {
                    // Knock-aroundable pieces: every collidered child gets a body.
                    // Rubbery friction + damping so hit props tumble, slow, and
                    // come to rest instead of gliding away forever.
                    if (_propPhys == null)
                        _propPhys = new PhysicsMaterial("TrackProp")
                        {
                            dynamicFriction = 0.7f,
                            staticFriction = 0.8f,
                            bounciness = 0.15f,
                            frictionCombine = PhysicsMaterialCombine.Average,
                        };
                    foreach (var col in go.GetComponentsInChildren<Collider>())
                    {
                        col.sharedMaterial = _propPhys;
                        var rb = col.gameObject.AddComponent<Rigidbody>();
                        // RC-scale: shoveable by a 1.8 kg car. Mass follows the
                        // cube of the item's scale, or a double-size brick would
                        // weigh the same as the small one and fly off like foam.
                        rb.mass = def.dynamicMass * s * s * s;
                        rb.linearDamping = 0.3f;
                        rb.angularDamping = 1.5f;  // bleeds off rolling/tumbling
                        rb.interpolation = RigidbodyInterpolation.Interpolate;
                        if (def.bottomHeavy)       // weighted base (traffic cones)
                            rb.centerOfMass = new Vector3(0f, 0.025f * s, 0f);
                    }
                }

                switch (def.behavior)
                {
                    case ItemBehavior.Checkpoint:
                    {
                        var trig = MakeGateTrigger(go.transform.position, go.transform.rotation,
                            go.transform, 1.35f);
                        var cp = trig.AddComponent<Checkpoint>();
                        cp.index = checkpoints.IndexOf(it); // dense rank in sorted order
                        cp.timer = built.lapTimer;
                        break;
                    }
                    case ItemBehavior.ItemBox:
                    {
                        // Only wire it up in an arcade session; outside one an
                        // authored box stays as scenery rather than a pickup the
                        // player can drive through and get nothing from.
                        // The trigger collider is authored by the catalog (so the
                        // builder can select the box); this only brings it to life.
                        var box = go.transform.Find("Box");
                        if (box == null) break;
                        if (Core.SessionConfig.Arcade)
                        {
                            var ib = box.gameObject.AddComponent<Arcade.ArcadeItemBox>();
                            ib.viz = box.Find("Viz");
                        }
                        else
                        {
                            box.gameObject.SetActive(false);
                        }
                        break;
                    }
                    case ItemBehavior.Light:
                    {
                        var lightGo = new GameObject("Lamp");
                        lightGo.transform.SetParent(go.transform, false);
                        lightGo.transform.localPosition = def.lightPos;
                        // Cancel the item's scale on the lamp itself: the head
                        // still rides up with a taller post (localPosition is
                        // scaled by the parent) but the pool of light stays 5 m
                        // whatever the prop's size. Doing it on the transform
                        // rather than on `range` keeps it correct regardless of
                        // how the pipeline folds scale into light range.
                        lightGo.transform.localScale = Vector3.one / Mathf.Max(0.05f, s);
                        var l = lightGo.AddComponent<Light>();
                        l.type = LightType.Point;
                        l.range = 5f;
                        l.intensity = 2.2f;
                        l.color = new Color(1.0f, 0.93f, 0.75f);
                        break;
                    }
                }
            }

            if (interactive && built.lapTimer != null)
                built.lapTimer.CheckpointCount = checkpoints.Count;

            if (batchable != null && batchable.Count > 0)
                StaticBatchingUtility.Combine(batchable.ToArray(), itemsRoot);

            cookWatch.Stop();
            Debug.Log($"[TrackFactory] items built in {cookWatch.ElapsedMilliseconds} ms " +
                      $"({d.items.Count} items, incl. MeshCollider cooking + static batch)");
        }

        /// <summary>The surface-dropped pose for a placed item (position + yaw
        /// tilted to the surface normal, so items sit flush on raised/banked
        /// ribbon sections and follow spline redraws).</summary>
        private static (Vector3, Quaternion) ItemPose(BuiltTrack built, PlacedItem it)
        {
            var yaw = Quaternion.Euler(0f, it.yawDeg, 0f);
            if (DropToSurface(built, new Vector3(it.x, it.y, it.z), out var pos, out var normal))
                return (pos, Quaternion.FromToRotation(Vector3.up, normal) * yaw);
            return (new Vector3(it.x, 0f, it.z), yaw);
        }

        /// <summary>
        /// An invisible gate trigger volume across a resolved pose's local X axis.
        /// Public because hand-authored scene tracks build their finish and
        /// checkpoint gates through this exact call: two implementations of "what
        /// a gate is" would drift in width, height or the half-metre lift, and the
        /// symptom — a lap that counts on one track source and not the other —
        /// would be maddening to chase.
        /// </summary>
        public static GameObject MakeGateTrigger(Vector3 pos, Quaternion rot, Transform parent, float width)
        {
            var trig = new GameObject("GateTrigger");
            trig.transform.SetParent(parent, false);
            trig.transform.SetPositionAndRotation(pos + rot * (Vector3.up * 0.5f), rot);
            var box = trig.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(width, 1f, 0.25f);
            return trig;
        }

        // ---- spawn ----------------------------------------------------------

        private static void ResolveSpawn(TrackDesign d, BuiltTrack built)
        {
            // Spawn height: drop onto the local surface (raised-ribbon spawns
            // work) and hover the chassis slightly above it.
            Vector3 Drop(Vector3 hint)
            {
                if (DropToSurface(built, hint, out var p, out _)) return p + Vector3.up * 0.08f;
                return new Vector3(hint.x, 0.08f, hint.z);
            }

            // Collect them all first: the arena modes need the whole ring, and
            // the single-spawn answer below is just the first of them.
            foreach (var it in d.items)
            {
                var def = TrackCatalog.Item(it.itemId);
                if (def == null || def.behavior != ItemBehavior.Spawn) continue;
                built.spawns.Add(new SpawnPoint
                {
                    pos = Drop(new Vector3(it.x, it.y, it.z)),
                    rot = Quaternion.Euler(0f, it.yawDeg, 0f),
                    team = Mathf.Max(0, it.order),
                });
            }

            var spawn = d.FindByBehavior(ItemBehavior.Spawn);
            if (spawn != null)
            {
                built.spawnPos = Drop(new Vector3(spawn.x, spawn.y, spawn.z));
                built.spawnRot = Quaternion.Euler(0f, spawn.yawDeg, 0f);
                return;
            }

            var finish = d.FindByBehavior(ItemBehavior.Finish);
            if (finish != null)
            {
                // 1.5 m behind the line, facing through it (same offset as the oval).
                var rot = Quaternion.Euler(0f, finish.yawDeg, 0f);
                var dir = rot * Vector3.forward;
                built.spawnPos = Drop(new Vector3(finish.x, finish.y, finish.z) - dir * 1.5f);
                built.spawnRot = rot;
                return;
            }

            built.spawnPos = new Vector3(0f, 0.08f, 0f);
            built.spawnRot = Quaternion.identity;
        }

        // ---- shared visual entry (ghosts + icons) ---------------------------

        /// <summary>
        /// Build just an item's visuals under <paramref name="parent"/> (assumed
        /// at the origin during the call). No markers, triggers, or physics wiring.
        /// </summary>
        public static GameObject BuildItemVisual(ItemDef def, Transform parent)
        {
            var go = new GameObject(def.id);
            go.transform.SetParent(parent, false);
            def.build(go.transform);
            return go;
        }
    }
}
