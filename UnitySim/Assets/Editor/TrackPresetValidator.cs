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
            foreach (var (name, build) in TrackPresets.All)
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
                if (finish != 1) problems.Add($"finish count {finish} (want 1)");
                if (spawn > 1) problems.Add($"spawn count {spawn} (want 0 or 1)");
                if (outside > 0) problems.Add($"{outside} item(s) outside the map");

                // Checkpoint orders drive LapTimer's in-order gate: they must be a
                // dense 0..n-1 run or a lap can never be validated.
                cps.Sort();
                for (int i = 0; i < cps.Count; i++)
                    if (cps[i] != i) { problems.Add($"checkpoint orders not dense 0..{cps.Count - 1}"); break; }

                string line = $"{name}: {d.width}x{d.length} items={d.items.Count} " +
                              $"splines={d.splines.Count} cp={cps.Count} boxes={boxes}";
                if (problems.Count == 0) Debug.Log($"[TPV] PASS {line}");
                else { Debug.LogError($"[TPV] FAIL {line} - {string.Join("; ", problems)}"); fail++; }
            }

            fail += CheckSplineGeometry();
            fail += CheckBuilds();
            fail += CheckPropAssets();

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

            foreach (var (name, build) in TrackPresets.All)
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
            foreach (var (name, build) in TrackPresets.All)
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

                    if (d.splines.Count > 0 && ribbonColliders == 0)
                    {
                        Debug.LogError($"[TPV] FAIL build {name}: {d.splines.Count} spline(s) but no MeshCollider");
                        fail++;
                    }
                    else
                        Debug.Log($"[TPV] BUILD {name}: roots={items} ribbonMesh={ribbonMeshes} " +
                                  $"ribbonCollider={ribbonColliders}");
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
    }
}
