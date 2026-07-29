using System.Collections.Generic;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Headless check on the built-in track presets. Everything it looks for fails
    /// SILENTLY at runtime, which is why it exists: TrackFactory skips an item
    /// whose id it cannot resolve (deliberately — that is what makes old saves
    /// load in new builds), a floor index past the end of the catalog throws deep
    /// inside the mesh build, and a checkpoint sequence with a gap in it simply
    /// never lets a lap complete. None of those announce themselves; a typo in a
    /// preset would just quietly produce a map missing half its props.
    ///
    /// Run with (editor must be closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.TrackPresetValidator.Report -logFile &lt;log&gt;
    /// then grep the log for "[TPV] RESULT".
    /// </summary>
    public static class TrackPresetValidator
    {
        public static void Report()
        {
            int fail = 0;
            foreach (var (name, kind, build) in TrackPresets.All)
            {
                var problems = new List<string>();
                TrackDesign d = null;
                try
                {
                    d = build();
                    d.EnsureFloor();
                    d.EnsureSplines();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[TPV] FAIL {name}: threw {e.GetType().Name}: {e.Message}");
                    fail++;
                    continue;
                }

                // Floor indices must exist in the catalog.
                int floors = TrackCatalog.Floors.Length;
                var badFloor = new HashSet<int>();
                foreach (int f in d.floor)
                    if (f < 0 || f >= floors) badFloor.Add(f);
                foreach (var s in d.splines)
                    foreach (int f in s.surface)
                        if (f < 0 || f >= floors) badFloor.Add(f);
                foreach (int f in badFloor) problems.Add($"floor id {f} >= {floors}");

                // Item ids must resolve, or TrackFactory drops them without a word.
                int finish = 0, spawn = 0, boxes = 0;
                var cps = new List<int>();
                var unknown = new HashSet<string>();
                float halfW = d.WorldWidth * 0.5f, halfL = d.WorldLength * 0.5f;
                int outside = 0;

                foreach (var it in d.items)
                {
                    var def = TrackCatalog.Item(it.itemId);
                    if (def == null) { unknown.Add(it.itemId); continue; }
                    switch (def.behavior)
                    {
                        case ItemBehavior.Finish: finish++; break;
                        case ItemBehavior.Spawn: spawn++; break;
                        case ItemBehavior.Checkpoint: cps.Add(it.order); break;
                        case ItemBehavior.ItemBox: boxes++; break;
                    }
                    if (Mathf.Abs(it.x) > halfW || Mathf.Abs(it.z) > halfL) outside++;
                }

                foreach (string u in unknown) problems.Add($"unknown item id '{u}'");

                // A map with no finish line and a RING of spawns is an arena for
                // the mini-game modes, and the circuit rules below are the wrong
                // question to ask it: there is nothing to lap, and the spawn ring
                // is the load-bearing feature rather than a duplicate. A
                // free-roam map looks the same from here and is not the same
                // thing — it is one player wandering, so it needs spawns to be
                // put back at but has no two sides to balance.
                bool roam = kind == TrackPresets.TrackKind.FreeRoam;
                bool arena = !roam && finish == 0 && spawn >= 2;
                if (roam)
                {
                    // TrackRespawn falls back to the nearest free spawn when
                    // there is no racing line, so a free-roam map with none is
                    // one where the respawn key does nothing.
                    if (spawn < 4) problems.Add($"free-roam spawn count {spawn} (want 4+)");
                    if (finish > 0) problems.Add($"free-roam map has a finish line");
                    if (cps.Count > 0) problems.Add($"free-roam map has {cps.Count} checkpoint(s)");
                    if (d.splines.Count > 0)
                        problems.Add($"free-roam map has {d.splines.Count} spline(s) — " +
                                     "a town has no racing line and BotPath would follow it");
                }
                else if (arena)
                {
                    if (spawn < 4) problems.Add($"arena spawn count {spawn} (want 4+)");
                    if (spawn % 2 != 0)
                        problems.Add($"arena spawn count {spawn} is odd (a team mode needs even sides)");
                    if (cps.Count > 0) problems.Add($"arena has {cps.Count} checkpoint(s)");
                }
                else
                {
                    if (finish != 1) problems.Add($"finish count {finish} (want 1)");
                    if (spawn > 1) problems.Add($"spawn count {spawn} (want 0 or 1)");
                }
                if (outside > 0) problems.Add($"{outside} item(s) outside the map");

                // A scale of 0 builds an invisible item and is what a map saved
                // before per-item scale deserialises to — TrackDesign.EnsureItems
                // repairs it, so seeing one here means a preset authored it.
                int badScale = 0;
                foreach (var it in d.items) if (!(it.scale > 0f)) badScale++;
                if (badScale > 0) problems.Add($"{badScale} item(s) with scale <= 0");

                // BotPath.Build follows the spline with the MOST control points,
                // not the longest or the first. A themed map that grew a second
                // decorative spline would hand the bots a side lane and nothing
                // would say so — hence: one spline, and it must be the closed
                // racing loop.
                if (d.splines.Count > 1)
                {
                    SplineSpec most = null;
                    foreach (var s in d.splines)
                        if (most == null || s.Count > most.Count) most = s;
                    problems.Add($"{d.splines.Count} splines — BotPath would follow the " +
                                 $"{most.Count}-point one" + (most.closed ? "" : ", which is not closed"));
                }

                // Checkpoint orders drive LapTimer's in-order gate: they must be a
                // dense 0..n-1 run or a lap can never be validated.
                cps.Sort();
                for (int i = 0; i < cps.Count; i++)
                    if (cps[i] != i) { problems.Add($"checkpoint orders not dense 0..{cps.Count - 1}"); break; }

                string line = $"{name} [{kind}]: {d.width}x{d.length}@{d.tileSize:0.#}m " +
                              $"({d.WorldWidth:0}x{d.WorldLength:0}m) items={d.items.Count} " +
                              $"splines={d.splines.Count} cp={cps.Count} boxes={boxes} " +
                              $"spawns={spawn} amb='{d.ambience}'";
                if (problems.Count == 0) Debug.Log($"[TPV] PASS {line}");
                else { Debug.LogError($"[TPV] FAIL {line} - {string.Join("; ", problems)}"); fail++; }
            }

            fail += CheckSplineGeometry();
            fail += CheckBuilds();
            fail += CheckPropAssets();
            fail += CheckPropTokens();
            fail += CheckDriveIns();
            fail += CheckColliderCoverage();

            Debug.Log($"[TPV] RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({TrackPresets.All.Length} presets)");
        }

        /// <summary>
        /// The two ways a 3D circuit fails that nothing else notices.
        ///
        /// A gradient the car cannot climb just looks like a car that stops; the
        /// RC drivetrain makes roughly 50 N of thrust against 1.8 kg, so ~25% is
        /// where a climb starts eating real speed and 40% is a wall. And a track
        /// that crosses over itself — Neon Vortex II does, deliberately — is only
        /// a bridge if the decks clear each other: any two points of the ribbon
        /// within 1.5 m horizontally need either enough vertical gap for the car
        /// to pass under (0.35 m clears a 0.10 m car plus the 0.04 m skirt) or
        /// none at all, because a 0.2 m step is an invisible wall at speed.
        /// </summary>
        private static int CheckSplineGeometry()
        {
            const float MaxGradePct = 40f;      // hard fail: unclimbable
            const float WarnGradePct = 25f;     // costs real speed
            const float NearXZ = 1.5f;          // "these decks overlap"
            const float MinClearY = 0.35f;      // car + skirt fits under
            int fail = 0;

            foreach (var (name, kind, build) in TrackPresets.All)
            {
                TrackDesign d;
                try { d = build(); d.EnsureSplines(); } catch { continue; }

                for (int si = 0; si < d.splines.Count; si++)
                {
                    var samples = SplineMath.SampleAll(d.splines[si]);
                    if (samples.Count < 3) continue;

                    float loY = float.MaxValue, hiY = float.MinValue;
                    float maxGrade = 0f, minW = float.MaxValue, maxBank = 0f;
                    foreach (var s in samples)
                    {
                        loY = Mathf.Min(loY, s.pos.y); hiY = Mathf.Max(hiY, s.pos.y);
                        minW = Mathf.Min(minW, s.width);
                        maxBank = Mathf.Max(maxBank, Mathf.Abs(s.roll));
                    }
                    for (int i = 1; i < samples.Count; i++)
                    {
                        float run = samples[i].dist - samples[i - 1].dist;
                        if (run < 1e-4f) continue;
                        maxGrade = Mathf.Max(maxGrade,
                            Mathf.Abs(samples[i].pos.y - samples[i - 1].pos.y) / run * 100f);
                    }

                    // Self-proximity: compare every pair that is far apart ALONG
                    // the curve but close in plan view. Skipping neighbours by arc
                    // length is what stops the curve from flagging itself.
                    int crossings = 0; float worstClear = float.MaxValue;
                    float total = samples[samples.Count - 1].dist;
                    for (int i = 0; i < samples.Count; i++)
                        for (int j = i + 1; j < samples.Count; j++)
                        {
                            float along = samples[j].dist - samples[i].dist;
                            if (d.splines[si].closed) along = Mathf.Min(along, total - along);
                            if (along < 6f) continue;                       // same stretch
                            Vector3 a = samples[i].pos, b = samples[j].pos;
                            float dxz = new Vector2(a.x - b.x, a.z - b.z).magnitude;
                            if (dxz > NearXZ) continue;
                            crossings++;
                            worstClear = Mathf.Min(worstClear, Mathf.Abs(a.y - b.y));
                        }

                    string tag = $"{name}[{si}]";
                    string line = $"len={total:0.0}m rise={hiY - loY:0.00}m grade={maxGrade:0.0}% " +
                                  $"width={minW:0.0}m bank={maxBank:0}deg";
                    if (crossings > 0) line += $" overpass(clear={worstClear:0.00}m)";

                    if (maxGrade > MaxGradePct)
                    { Debug.LogError($"[TPV] FAIL geom {tag}: {line} - grade over {MaxGradePct}%"); fail++; }
                    else if (crossings > 0 && worstClear < MinClearY)
                    { Debug.LogError($"[TPV] FAIL geom {tag}: {line} - decks {worstClear:0.00}m apart, car will clip"); fail++; }
                    else if (maxGrade > WarnGradePct)
                        Debug.LogWarning($"[TPV] WARN geom {tag}: {line} - steep");
                    else
                        Debug.Log($"[TPV] GEOM {tag}: {line}");
                }
            }
            return fail;
        }

        /// <summary>
        /// Actually run each preset through TrackFactory. The checks above read
        /// the design; this one proves the design can be turned into a scene —
        /// a throw in the mesh builder or a missing collider is otherwise only
        /// discovered by loading the map and finding a hole in the world. The
        /// ribbon must come back with at least one MeshCollider or the car would
        /// drive straight through it.
        /// </summary>
        private static int CheckBuilds()
        {
            int fail = 0;
            foreach (var (name, kind, build) in TrackPresets.All)
            {
                TrackDesign d;
                try { d = build(); }
                catch { continue; }   // already reported by the structural pass

                BuiltTrack built = null;
                try
                {
                    built = TrackFactory.Build(d, interactive: false);
                    int ribbonMeshes = 0, ribbonColliders = 0;
                    if (built.splineRoots != null)
                        foreach (var root in built.splineRoots)
                        {
                            if (root == null) continue;
                            ribbonMeshes += root.GetComponentsInChildren<MeshFilter>(true).Length;
                            ribbonColliders += root.GetComponentsInChildren<MeshCollider>(true).Length;
                        }
                    int items = built.root.transform.childCount;
                    // What the map actually costs to draw. The ported maps run
                    // 350-750 items of two to nine pieces each, and this is the
                    // number to watch if one of them ever hitches on load.
                    // Triangles too, since Torque Falls: the town's item count
                    // is only half its story — its trees carry 6 300 triangles
                    // each and there are hundreds of them.
                    int renderers = built.root.GetComponentsInChildren<Renderer>(true).Length;
                    long tris = 0;
                    foreach (var mf in built.root.GetComponentsInChildren<MeshFilter>(true))
                        if (mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;
                    // Colliders too, since the mesh migration: cooking cost and
                    // broadphase load are proportional to this, and the town is
                    // the number to watch (cook time itself is logged by
                    // TrackFactory.BuildItems).
                    int colliders = built.root.GetComponentsInChildren<Collider>(true).Length;

                    if (d.splines.Count > 0 && ribbonColliders == 0)
                    {
                        Debug.LogError($"[TPV] FAIL build {name}: {d.splines.Count} spline(s) but no MeshCollider");
                        fail++;
                    }
                    else
                        Debug.Log($"[TPV] BUILD {name}: roots={items} renderers={renderers} " +
                                  $"tris={tris} colliders={colliders} " +
                                  $"ribbonMesh={ribbonMeshes} ribbonCollider={ribbonColliders}");
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[TPV] FAIL build {name}: threw {e.GetType().Name}: {e.Message}");
                    fail++;
                }
                finally
                {
                    if (built != null && built.root != null) Object.DestroyImmediate(built.root);
                }
            }
            return fail;
        }

        /// <summary>
        /// Every mesh-backed item's id must name a real FBX under
        /// Resources/TrackProps. A mismatch is invisible in play: TrackCatalog
        /// falls back to the primitive shape and the map still loads, just wrong —
        /// so the id/asset agreement has to be asserted rather than eyeballed.
        /// Child object names are logged too, because those are what
        /// PartMeshLibrary.AssignByName matches material tokens against.
        /// </summary>
        private static int CheckPropAssets()
        {
            int fail = 0;
            foreach (var def in TrackCatalog.Items)
            {
                if (def.category != ItemCategory.Scenery) continue;
                var src = Resources.Load<GameObject>(PartMeshLibrary.PropRoot + def.id);
                if (src == null)
                {
                    Debug.LogError($"[TPV] FAIL prop {def.id}: no mesh at Resources/{PartMeshLibrary.PropRoot}{def.id}");
                    fail++;
                    continue;
                }
                var names = new List<string>();
                foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
                    names.Add(mf.gameObject.name);
                Debug.Log($"[TPV] PROP {def.id} [{def.theme}] parts={names.Count} :: {string.Join(",", names)}");
            }
            return fail;
        }

        /// <summary>
        /// Every FBX child name must resolve to its OWN material token.
        ///
        /// PartMeshLibrary.AssignByName is a first-match SUBSTRING matcher, so a
        /// token listed after one it contains is swallowed: brickpale takes
        /// brick, siggreen takes green, trimdk takes trim. Nothing else can
        /// catch it — the prop still loads, still passes its extent and its
        /// triangle budget, and renders a plausible WRONG colour. The city pack
        /// has fifty tokens with eight such containments in it, which is what
        /// made this worth asserting rather than reading carefully.
        /// </summary>
        private static int CheckPropTokens()
        {
            int fail = 0;
            foreach (var def in TrackCatalog.Items)
            {
                var table = TrackCatalog.TokensFor(def.theme);
                if (table == null || table.Length == 0) continue;
                var src = Resources.Load<GameObject>(PartMeshLibrary.PropRoot + def.id);
                if (src == null) continue;      // already reported by CheckPropAssets

                var seen = new HashSet<string>();
                string why = "";
                foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
                {
                    string n = mf.gameObject.name.ToLowerInvariant();
                    int cut = n.LastIndexOf('_');
                    if (cut <= 0) continue;
                    // Only <token>_<n> is a tokenised piece. A single-material
                    // prop imports with the FBX ROOT carrying the mesh, named
                    // after the file — so city_fence_chain would otherwise be
                    // read as the token "city_fence" and reported missing.
                    string tail = n.Substring(cut + 1);
                    bool numeric = tail.Length > 0;
                    foreach (char ch in tail) if (ch < '0' || ch > '9') { numeric = false; break; }
                    if (!numeric) continue;
                    string want = n.Substring(0, cut);
                    if (!seen.Add(want)) continue;
                    bool known = false;
                    foreach (var (token, _) in table) if (token == want) { known = true; break; }
                    if (!known) { why += $" '{want}' has no token"; continue; }
                    string got = null;
                    foreach (var (token, _) in table)
                        if (n.Contains(token)) { got = token; break; }
                    if (got != want) why += $" '{want}'->'{got}'";
                }
                if (why.Length > 0)
                {
                    Debug.LogError($"[TPV] FAIL tokens {def.id} [{def.theme}]:{why}");
                    fail++;
                }
            }
            return fail;
        }

        /// <summary>
        /// The drive-in props must still be drivable through.
        ///
        /// The Blender kit asserts a 4.60 x 3.90 clearance on every aperture and
        /// walks each finished mesh to refuse a building with something standing
        /// in its doorway. Collision is now the mesh itself, so what this guards
        /// against is an FBX RE-EXPORT growing a doorsill, a threshold or a
        /// mis-welded skirt across an opening — which would brick up the one
        /// prop the map was designed around while looking fine from anywhere
        /// else. An exact box overlap along each corridor at car height, plus a
        /// control probe that must find something solid so a query that finds
        /// nothing at all cannot pass as a clear doorway.
        ///
        /// Since the mesh migration the control probe STRADDLES A WALL FACE
        /// rather than sitting inside a wall's volume: a non-convex MeshCollider
        /// is a hollow surface, and an overlap box fully inside it touching no
        /// triangles reports nothing — the exact false-pass the control probe
        /// exists to rule out.
        /// </summary>
        private static int CheckDriveIns()
        {
            // id, corridor centre + half extents (the prop's own local frame,
            // at car height), and a point that MUST be solid (on a wall).
            //
            // Frame note: the FBX writer mirrors X (source-left lands on Unity
            // +X — the same handedness flip every vehicle came through), so
            // authored bay coordinates negate their X on the way here. The
            // original corridors sat on the MIRRORED side — over the garage's
            // closed roller-door bay and its interior tyre stacks — and only
            // passed because the hand-authored hulls had been written to the
            // same wrong assumption. Probing the real meshes is what exposed
            // it: everything the probe hit (roller door, tyres, workbench) is
            // the closed bay's furniture, exactly mirrored.
            var cases = new (string id, Vector3 c, Vector3 half, Vector3 solid)[]
            {
                // Authored open bay x -4.9..-0.3 (blender) -> +0.03..+0.49
                // here; open front AND back. Control on the closed bay's
                // roller door.
                ("city_garage",   new Vector3(0.26f, 0.07f, -0.09f), new Vector3(0.19f, 0.05f, 0.52f),
                                  new Vector3(-0.30f, 0.07f, 0.42f)),
                ("city_autoshop", new Vector3(-0.545f, 0.07f, -0.37f), new Vector3(0.20f, 0.05f, 0.52f),
                                  new Vector3(0.375f, 0.07f, -0.41f)),
                ("city_firehouse", new Vector3(0.31f, 0.07f, -0.21f), new Vector3(0.19f, 0.05f, 0.54f),
                                  new Vector3(0f, 0.07f, -0.81f)),
                ("city_arena",    new Vector3(3.65f, 0.07f, 0f), new Vector3(0.60f, 0.05f, 0.28f),
                                  new Vector3(-4.35f, 0.07f, 0f)),
                // The filling station's lane runs along X between the two pump
                // islands (authored 4.9 wide, soffit at DRIVE_H + 0.70): the
                // fifth drive-in the catalog names, untested until the mesh
                // migration made its collision real. X-symmetric, so the
                // mirror is moot. Control point on the kiosk's front wall.
                ("city_gas",      new Vector3(0f, 0.07f, 0.245f), new Vector3(0.85f, 0.05f, 0.19f),
                                  new Vector3(0f, 0.07f, -0.45f)),
            };

            int fail = 0;
            foreach (var (id, c, half, solid) in cases)
            {
                var def = TrackCatalog.Item(id);
                if (def == null) { Debug.LogError($"[TPV] FAIL drivein {id}: no such item"); fail++; continue; }

                var root = new GameObject("DriveInProbe");
                try
                {
                    TrackFactory.BuildItemVisual(def, root.transform);
                    Physics.SyncTransforms();
                    var mine = new HashSet<Collider>(root.GetComponentsInChildren<Collider>(true));

                    int blocked = Count(c, half, mine);
                    // 0.08 half-extent so the probe crosses BOTH faces of the
                    // thinnest authored wall (0.04) wherever it stands.
                    int control = Count(solid, Vector3.one * 0.08f, mine);
                    if (control == 0)
                    {
                        Debug.LogError($"[TPV] FAIL drivein {id}: control point found nothing solid — " +
                                       "the corridor test is not testing anything");
                        fail++;
                    }
                    else if (blocked > 0)
                    {
                        Debug.LogError($"[TPV] FAIL drivein {id}: {blocked} hull(s) across the " +
                                       $"{half.x * 2f:0.00} x {half.z * 2f:0.00} m opening");
                        fail++;
                    }
                    else
                    {
                        Debug.Log($"[TPV] DRIVEIN {id}: clear ({half.x * 2f:0.00} x {half.z * 2f:0.00} m)");
                    }
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
            return fail;
        }

        /// <summary>
        /// Every static scenery item's collision must track its visual.
        ///
        /// This is the regression fence around the ghost-wall era: the old
        /// primitive hulls shipped a 12.6 m volcano with a 5 m floating sphere,
        /// an arena ring 0.42 m inside its stands, and ramps whose collision
        /// feet hovered 4–19 cm off the ground — all invisible to every other
        /// check, because the map loads and the prop LOOKS right. Combined
        /// collider bounds must cover the combined renderer bounds to within
        /// 5 cm per axis and overshoot them by no more than 10 cm. With
        /// MeshColliders this holds by construction; what it actually catches
        /// is the future prop that quietly opts back into an authored hull, or
        /// a piece whose collider silently failed to cook.
        /// </summary>
        private static int CheckColliderCoverage()
        {
            const float MaxShortfall = 0.05f;
            const float MaxOvershoot = 0.10f;
            int fail = 0, covered = 0;

            foreach (var def in TrackCatalog.Items)
            {
                if (def.category != ItemCategory.Scenery || def.dynamic) continue;

                var root = new GameObject("CoverProbe");
                try
                {
                    TrackFactory.BuildItemVisual(def, root.transform);
                    Physics.SyncTransforms();   // Collider.bounds needs it (autosync off)

                    Bounds? vis = null, col = null;
                    bool anySolid = false, anyTrigger = false;
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                        vis = vis.HasValue ? Enclose(vis.Value, r.bounds) : r.bounds;
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        if (c.isTrigger) { anyTrigger = true; continue; }
                        anySolid = true;
                        col = col.HasValue ? Enclose(col.Value, c.bounds) : c.bounds;
                    }

                    // Drive-through-by-design (haunt_ghost / haunt_wisp): all
                    // collision is trigger, and that is the point of the prop.
                    if (!anySolid && anyTrigger) continue;
                    if (!vis.HasValue) continue;   // nothing rendered (probe-only defs)
                    covered++;

                    if (!col.HasValue)
                    {
                        Debug.LogError($"[TPV] FAIL cover {def.id}: renders but has NO collider");
                        fail++;
                        continue;
                    }

                    string why = "";
                    for (int axis = 0; axis < 3; axis++)
                    {
                        // How far the collider falls SHORT of the visual on
                        // each side (>0 = uncovered geometry, the ghost wall),
                        // and how far it POKES PAST it (>0 = invisible wall).
                        float shortLow = col.Value.min[axis] - vis.Value.min[axis];
                        float shortHigh = vis.Value.max[axis] - col.Value.max[axis];
                        float uncovered = Mathf.Max(shortLow, shortHigh);
                        float overshoot = Mathf.Max(-shortLow, -shortHigh);
                        if (uncovered > MaxShortfall)
                            why += $" axis {axis}: visual uncovered by {uncovered:0.000} m";
                        if (overshoot > MaxOvershoot)
                            why += $" axis {axis}: collider overshoots by {overshoot:0.000} m";
                    }
                    if (why.Length > 0)
                    {
                        Debug.LogError($"[TPV] FAIL cover {def.id} [{def.theme}]:{why}");
                        fail++;
                    }
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
            if (fail == 0) Debug.Log($"[TPV] COVER {covered} static scenery items: colliders track renderers");
            return fail;
        }

        private static Bounds Enclose(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

        private static int Count(Vector3 centre, Vector3 half, HashSet<Collider> mine)
        {
            int n = 0;
            foreach (var col in Physics.OverlapBox(centre, half, Quaternion.identity,
                                                   ~0, QueryTriggerInteraction.Ignore))
                if (mine.Contains(col)) n++;
            return n;
        }
    }
}
