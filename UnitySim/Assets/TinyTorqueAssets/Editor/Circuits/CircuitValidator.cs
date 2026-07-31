using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Checks a built circuit scene against the manifest it was built from.
    ///
    /// The scene builder already reports that it placed the right <i>number</i>
    /// of things. That is worth having and it is not the same question as
    /// whether they are in the right <i>place</i> — a circuit converted with the
    /// wrong axis mapping places exactly the right number of everything, and a
    /// mirrored one renders perfectly.
    ///
    /// So this measures geometry against the one description of the circuit that
    /// did not go through the FBX pipeline at all: the manifest's spine array,
    /// which is station → position, heading, bank and half-width for the whole
    /// lap, written straight out of the surveyed centreline.
    ///
    /// <list type="number">
    /// <item><b>The road is on the centreline.</b> For 200 stations, the nearest
    /// <c>Trk_Surface</c> vertex to the converted spine point. The road ribbon
    /// carries a vertex at u = 0, and stations are at most 5 m apart, so this
    /// should land within a couple of metres horizontally and centimetres
    /// vertically. It is the whole conversion — axes, handedness, scale, sign —
    /// measured end to end on real geometry. A mirrored circuit fails it by
    /// kilometres.</item>
    /// <item><b>The relief is real.</b> Measured climb between the lowest and
    /// highest stations against the manifest's own elevation range. Catches a
    /// circuit that arrived flattened or scaled — Spa without Eau Rouge's 41 m
    /// looks fine in a screenshot and is not Spa.</item>
    /// <item><b>Colliders cooked.</b> Every mesh the manifest marked
    /// <c>collider: mesh</c> has one, with bounds that track its renderer. A
    /// collider that silently failed to cook looks perfect and drives straight
    /// through, which is the same check the pack holds its props to.</item>
    /// <item><b>Props are on the ground.</b> Each placed prop against the
    /// terrain height near it. Catches an instance transform that converted
    /// differently from the mesh it places.</item>
    /// <item><b>The road faces up.</b> Area-weighted, from the winding and from
    /// the shipped normals independently. This is here because the axis test's
    /// winding half was checked against a reference solid that had itself been
    /// authored inside out, so it certified a flip that inverted every circuit —
    /// invisible from above, drawn from below. "A road points up" needs no
    /// reference object to be wrong about.</item>
    /// </list>
    /// </summary>
    public static class CircuitValidator
    {
        private const int SpineSamples = 200;
        private const float MaxLateral = 3.0f;    // m, spine point to nearest road vertex
        private const float MaxVertical = 0.35f;  // m, allows crown and kerb fall
        private const float MaxRelief = 2.0f;     // m, measured climb vs manifest
        private const float MaxPropDrop = 6.0f;   // m, prop base vs terrain nearby

        // A mesh is judged on facing only if it is mostly a horizontal sheet, and
        // that is measured rather than taken from its group: PITS holds the pit
        // road surface and the pit building, and "which way is up" is a fact
        // about the first and a matter of taste about the second.
        private const float FlatCos = 0.6f;        // |n.y| above this counts as horizontal
        private const float FlatShare = 0.5f;      // ...and half the area must be
        private const float BalanceMax = 0.2f;     // ...and it must be one-sided
        private const float MinUpShare = 0.8f;     // of that, this much must face up
        private const float BarrierMin = 0.2f;     // below this a barrier is double-sided

        /// <summary>Per-mesh signed facing, keyed by name, gathered across every
        /// circuit in a run. See <see cref="CheckAcrossCircuits"/>.</summary>
        private static readonly Dictionary<string, List<KeyValuePair<string, float>>> _facing
            = new Dictionary<string, List<KeyValuePair<string, float>>>();
        private const float SignEps = 0.05f;

        private static int _fail;

        private static void Fail(string m)
        {
            _fail++;
            CircuitPaths.Err("FAIL " + m);
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/3. Validate circuit scenes", priority = 203)]
        public static void ValidateAll()
        {
            _fail = 0;
            _facing.Clear();
            int n = 0;
            foreach (string key in CircuitImport.Available())
            {
                n++;
                ValidateOne(key);
            }
            if (n > 1) CheckAcrossCircuits();
            if (n == 0) CircuitPaths.Err(CircuitPaths.SourceProblem);
            if (_fail == 0 && n > 0)
                CircuitPaths.Log("RESULT ALL PASS (" + n + " circuits)");
            else
                CircuitPaths.Err("RESULT " + _fail + " FAILED");
        }

        /// <summary>Headless entry: build then validate, one verdict.</summary>
        public static void RunHeadless()
        {
            CircuitMenu.RunAll();
            ValidateAll();
        }

        public static void ValidateOne(string key)
        {
            var man = CircuitManifest.Load(CircuitPaths.ManifestPath(key));
            if (man == null) { Fail(key + ": no manifest"); return; }
            string scenePath = CircuitPaths.ScenePath(key);
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) { Fail(key + ": scene did not open: " + scenePath); return; }

            var byName = new Dictionary<string, GameObject>();
            foreach (var go in scene.GetRootGameObjects())
                Collect(go.transform, byName);

            var sb = new StringBuilder();
            sb.Append(CircuitPaths.Display(key)).Append(": ");

            CheckRoadOnSpine(man, byName, sb);
            CheckRelief(man, byName, sb);
            CheckColliders(man, byName, sb);
            CheckProps(man, byName, sb);
            CheckKerbStripes(byName, sb);
            CheckFacing(man, byName, sb);
            CheckBarriersFaceTrack(man, byName, sb);
            CheckDrivable(man, scene, sb);
            CheckTreeChunks(scene, sb);

            CircuitPaths.Log(sb.ToString());
        }

        private static void Collect(Transform t, Dictionary<string, GameObject> d)
        {
            // First wins: names are unique per circuit by construction, and a
            // duplicate would mean the builder ran twice into one scene.
            if (!d.ContainsKey(t.name)) d[t.name] = t.gameObject;
            for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), d);
        }

        // ---------------------------------------------------------------------

        private static void CheckRoadOnSpine(CircuitManifest man,
                                             Dictionary<string, GameObject> byName,
                                             StringBuilder sb)
        {
            if (!byName.TryGetValue("Trk_Surface", out var road) ||
                road.GetComponent<MeshFilter>() == null)
            {
                Fail(man.circuit + ": no Trk_Surface in the scene");
                return;
            }
            var verts = road.GetComponent<MeshFilter>().sharedMesh.vertices;
            int n = man.SpineCount;
            if (n == 0 || verts.Length == 0)
            {
                Fail(man.circuit + ": empty spine or empty road mesh");
                return;
            }

            // A uniform grid over the road vertices, so 200 nearest-neighbour
            // queries against ~200 000 vertices is not 40 million distance tests.
            const float cell = 12f;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < verts.Length; i++)
            {
                var k = (Mathf.FloorToInt(verts[i].x / cell), Mathf.FloorToInt(verts[i].z / cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i);
            }

            float worstLat = 0f, worstVert = 0f;
            float worstLatS = 0f, worstVertS = 0f;
            int step = Mathf.Max(1, n / SpineSamples), checkedN = 0;
            for (int i = 0; i < n; i += step)
            {
                float s = man.Spine(i, 0);
                Vector3 p = CircuitAxis.Position(man.Spine(i, 1), man.Spine(i, 2),
                                                 man.Spine(i, 3));
                float bestLat = float.MaxValue, bestVert = 0f;
                int cx = Mathf.FloorToInt(p.x / cell), cz = Mathf.FloorToInt(p.z / cell);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cz + dz), out var l)) continue;
                        foreach (int vi in l)
                        {
                            Vector3 v = verts[vi];
                            float lat = new Vector2(v.x - p.x, v.z - p.z).magnitude;
                            if (lat < bestLat) { bestLat = lat; bestVert = Mathf.Abs(v.y - p.y); }
                        }
                    }
                if (bestLat == float.MaxValue) bestLat = 999f;
                checkedN++;
                if (bestLat > worstLat) { worstLat = bestLat; worstLatS = s; }
                if (bestVert > worstVert) { worstVert = bestVert; worstVertS = s; }
            }

            sb.AppendFormat("road-on-spine {0:F2} m lateral (s={1:F0}), {2:F3} m vertical "
                            + "(s={3:F0}) over {4} stations; ",
                            worstLat, worstLatS, worstVert, worstVertS, checkedN);
            if (worstLat > MaxLateral)
                Fail(man.circuit + string.Format(
                    ": the road mesh is {0:F1} m from the surveyed centreline at s={1:F0}. "
                    + "This is the axis conversion, not a modelling tolerance — a mirrored "
                    + "or transposed circuit fails here by hundreds of metres.",
                    worstLat, worstLatS));
            if (worstVert > MaxVertical)
                Fail(man.circuit + string.Format(
                    ": the road surface is {0:F2} m off the spine's own elevation at s={1:F0}.",
                    worstVert, worstVertS));
        }

        private static void CheckRelief(CircuitManifest man,
                                        Dictionary<string, GameObject> byName,
                                        StringBuilder sb)
        {
            if (man.elevation == null || man.elevation.Length < 2) return;
            float want = man.elevation[1] - man.elevation[0];
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < man.SpineCount; i++)
            {
                float z = man.Spine(i, 3);
                lo = Mathf.Min(lo, z);
                hi = Mathf.Max(hi, z);
            }
            // Measured on the scene, not on the numbers: the road mesh's own
            // vertical extent is what a car actually climbs.
            float meshRange = 0f;
            if (byName.TryGetValue("Trk_Surface", out var road))
            {
                var r = road.GetComponent<MeshRenderer>();
                if (r != null) meshRange = r.bounds.size.y;
            }
            sb.AppendFormat("relief {0:F1} m mesh vs {1:F1} m published; ", meshRange, want);
            if (Mathf.Abs(meshRange - want) > MaxRelief + 0.05f * want)
                Fail(man.circuit + string.Format(
                    ": the road climbs {0:F1} m in the scene, {1:F1} m in the manifest. "
                    + "A circuit that arrived flattened or scaled looks right in a "
                    + "screenshot and is not the circuit.", meshRange, want));
        }

        private static void CheckColliders(CircuitManifest man,
                                           Dictionary<string, GameObject> byName,
                                           StringBuilder sb)
        {
            int want = 0, got = 0, bad = 0;
            foreach (var e in man.world)
            {
                if (e.collider != "mesh") continue;
                want++;
                if (!byName.TryGetValue(e.name, out var go)) { bad++; continue; }
                var mc = go.GetComponent<MeshCollider>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mc == null || mc.sharedMesh == null) { bad++; continue; }
                got++;
                if (mr != null && Vector3.Distance(mc.bounds.size, mr.bounds.size) > 0.25f)
                {
                    bad++;
                    Fail(man.circuit + ": " + e.name
                         + " collider bounds do not track its renderer — a collider that "
                         + "failed to cook looks perfect and drives straight through");
                }
            }
            sb.AppendFormat("colliders {0}/{1}; ", got, want);
            if (got != want)
                Fail(man.circuit + ": " + (want - got) + " of " + want
                     + " collidable meshes have no cooked MeshCollider");
        }

        /// <summary>
        /// Tree chunk meshes are generated assets, and a missing mesh reference
        /// is silent: the GameObject is there, the renderer is there, and the
        /// forest is not. This is the line that says so by name.
        /// </summary>
        private static void CheckTreeChunks(UnityEngine.SceneManagement.Scene scene,
                                            StringBuilder sb)
        {
            int total = 0, empty = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!mf.name.StartsWith("Trees_")) continue;
                    total++;
                    if (mf.sharedMesh == null) empty++;
                }
            if (total == 0) return;
            sb.AppendFormat("; tree chunks {0}/{1}", total - empty, total);
            if (empty > 0)
                Fail(scene.name + ": " + empty + " of " + total + " tree chunk meshes "
                     + "are missing — run "
                     + "Tools > TinyTorque Assets > Circuits > 2. Build circuit scenes");
        }

        /// <summary>
        /// The striped kerb band actually alternates.
        ///
        /// Striping is a material-slot assignment driven off the station, so
        /// every failure mode of it lands the whole band on one slot: a
        /// uniformly red kerb, which is exactly what the flat material this
        /// replaces already looked like, and therefore exactly the change you
        /// would not notice had failed. Red and white blocks are the same
        /// length, so their triangle counts should be within a few percent of
        /// each other — anything wildly lopsided means the alternation is not
        /// happening.
        /// </summary>
        private static void CheckKerbStripes(Dictionary<string, GameObject> byName,
                                             StringBuilder sb)
        {
            int bands = 0;
            float worstSkew = 0f;
            foreach (var kv in byName)
            {
                if (!kv.Key.StartsWith("Trk_KerbStripe_")) continue;
                var mf = kv.Value.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.subMeshCount < 3)
                { Fail(kv.Key + ": striped kerb mesh has no red/white submeshes"); continue; }
                bands++;
                int red = mf.sharedMesh.GetTriangles(1).Length / 3;
                int white = mf.sharedMesh.GetTriangles(2).Length / 3;
                if (red + white == 0)
                { Fail(kv.Key + ": no kerb blocks at all"); continue; }
                float skew = Mathf.Abs(red - white) / (float)(red + white);
                worstSkew = Mathf.Max(worstSkew, skew);
                if (skew > 0.25f)
                    Fail(kv.Key + string.Format(
                        ": {0} red vs {1} white blocks — the stripe alternation is not "
                        + "happening, and a uniformly red kerb is what this replaced",
                        red, white));
            }
            if (bands > 0)
                sb.AppendFormat("; kerb stripes {0} bands, worst skew {1:P0}",
                                bands, worstSkew);
        }

        /// <summary>
        /// Every near-horizontal surface faces up, measured two independent ways.
        ///
        /// Winding decides what Unity culls; the shipped normals decide how it
        /// shades. They are set by different lines of the exporter and can
        /// disagree, so both are measured — a surface can be invisible with
        /// perfect normals, or visible and lit from underneath.
        /// </summary>
        private static void CheckFacing(CircuitManifest man,
                                        Dictionary<string, GameObject> byName,
                                        StringBuilder sb)
        {
            int n = 0;
            float worst = 1f;
            string worstName = null;
            bool worstFromWinding = true;

            foreach (var e in man.world)
            {
                if (!byName.TryGetValue(e.name, out var go)) continue;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                // Recorded for every mesh, sheet or not — a wall says nothing on
                // its own and a great deal next to the same wall on another
                // circuit. See CheckAcrossCircuits.
                if (!_facing.TryGetValue(e.name, out var list))
                    _facing[e.name] = list = new List<KeyValuePair<string, float>>();
                list.Add(new KeyValuePair<string, float>(
                    man.circuit, MeanUp(mf.sharedMesh)));

                float w = UpShare(mf.sharedMesh, true);
                if (float.IsNaN(w)) continue;       // not a sheet; says nothing
                n++;
                float s = UpShare(mf.sharedMesh, false);
                if (w < worst) { worst = w; worstName = e.name; worstFromWinding = true; }
                if (!float.IsNaN(s) && s < worst)
                { worst = s; worstName = e.name; worstFromWinding = false; }
            }
            if (n == 0) return;

            sb.AppendFormat("; facing {0} sheets, worst {1:P0} up ({2})",
                            n, worst, worstName);
            if (worst < MinUpShare)
                Fail(man.circuit + string.Format(
                    ": only {0:P0} of {1}'s horizontal area faces up, by its {2}. "
                    + "That surface is invisible from above and drawn from below. "
                    + "If every sheet is inverted it is REVERSE_WINDING in "
                    + "scripts/export_unity.py; if one is, it is that builder "
                    + "winding its quads off a side variable, which is how the "
                    + "grid boxes, the pit markings and the TECPRO blocks each "
                    + "came out wrong.",
                    worst, worstName, worstFromWinding ? "winding" : "shipped normals"));
        }

        /// <summary>
        /// Of the area that is near-horizontal, the share facing up — or NaN if
        /// this mesh is not mostly a horizontal sheet and therefore has no
        /// opinion. Direction comes from the triangle winding (what Unity culls)
        /// or from the shipped normals (how Unity lights it); those are set by
        /// different lines of the exporter and can disagree.
        /// </summary>
        private static float UpShare(Mesh m, bool fromWinding)
        {
            var v = m.vertices;
            var t = m.triangles;
            var nn = m.normals;
            if (!fromWinding && (nn == null || nn.Length == 0)) return float.NaN;

            double up = 0.0, down = 0.0, all = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                // Exactly how Unity derives a face normal for its front-face test.
                Vector3 fn = Vector3.Cross(b - a, c - a);
                float w = fn.magnitude;
                if (w <= 1e-9f) continue;
                all += w;

                Vector3 dir;
                if (fromWinding) dir = fn / w;
                else
                {
                    dir = nn[t[i]] + nn[t[i + 1]] + nn[t[i + 2]];
                    if (dir.sqrMagnitude <= 1e-12f) continue;
                    dir.Normalize();
                }
                if (dir.y >= FlatCos) up += w;
                else if (dir.y <= -FlatCos) down += w;
            }
            if (all <= 0.0) return float.NaN;
            double flat = up + down;
            if (flat / all < FlatShare) return float.NaN;   // a wall
            // Balanced up against down is a closed or double-sided shell — a
            // debris fence with a top and a bottom reads exactly 50 %, and
            // inverting it would still read exactly 50 %. It has no opinion.
            if (Mathf.Min((float)up, (float)down) / flat >= BalanceMax)
                return float.NaN;
            return (float)(up / flat);
        }

        /// <summary>
        /// Press Play and you drive.
        ///
        /// Four things have to be true and each fails silently on its own: the
        /// scene needs a <c>TrackBootstrap</c> (no game, no car), a
        /// <c>SceneTrackDescriptor</c> (the bootstrap falls back to the
        /// procedural oval and logs an error into a scene that looks fine), a
        /// dense run of spawn markers (a gap leaves a grid slot nobody occupies,
        /// and <c>TrackBootstrap.SpawnPose</c> only consults the authored grid at
        /// all when there is more than one marker), and solid ground under pole.
        ///
        /// The last one is the one worth measuring rather than reasoning about.
        /// <c>SceneTrackBuilder.Drop</c> raycasts from 3 m above the marker and
        /// keeps the authored height on a miss — so a grid floating over a hole
        /// in the collider does not error, it drops the car through the world on
        /// Play, several seconds after anything you could point at.
        /// </summary>
        private static void CheckDrivable(CircuitManifest man,
                                          UnityEngine.SceneManagement.Scene scene,
                                          StringBuilder sb)
        {
            AIHWSim.Track.SceneTrackDescriptor desc = null;
            bool boot = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (desc == null)
                    desc = root.GetComponentInChildren<AIHWSim.Track.SceneTrackDescriptor>(true);
                if (!boot)
                    boot = root.GetComponentInChildren<AIHWSim.Core.TrackBootstrap>(true) != null;
            }

            if (!boot)
                Fail(man.circuit + ": no TrackBootstrap — pressing Play loads the "
                     + "scene and never builds a car.");
            if (desc == null)
            {
                Fail(man.circuit + ": no SceneTrackDescriptor — TrackBootstrap "
                     + "falls back to the procedural oval and the circuit just sits "
                     + "there.");
                return;
            }

            var spawns = desc.Spawns();
            int want = man.grid != null ? man.grid.Length : 0;
            sb.AppendFormat("; drivable {0} spawns", spawns.Count);
            if (spawns.Count != want)
                Fail(man.circuit + string.Format(
                    ": {0} spawn markers for {1} grid slots", spawns.Count, want));
            for (int i = 0; i < spawns.Count; i++)
                if (spawns[i].gridOrder != i)
                {
                    Fail(man.circuit + string.Format(
                        ": spawn gridOrder is not a dense 0..n-1 run (slot {0} says {1}) "
                        + "— a gap silently leaves a starting box empty.",
                        i, spawns[i].gridOrder));
                    break;
                }

            if (!desc.HasCorridor)
                Fail(man.circuit + ": the descriptor has no usable bot corridor "
                     + "(centerline and halfWidths must be the same length, >= 2)");

            // Ground under every slot, not just pole: the grid runs 80 m back down
            // the road and the far end is where a collider gap would be.
            Physics.SyncTransforms();
            int floating = 0;
            foreach (var s in spawns)
            {
                var ray = new Ray(s.transform.position + Vector3.up * 3f, Vector3.down);
                if (!Physics.Raycast(ray, 63f, ~0, QueryTriggerInteraction.Ignore))
                    floating++;
            }
            if (floating > 0)
                Fail(man.circuit + string.Format(
                    ": {0} of {1} grid slots have no collider beneath them. The car "
                    + "keeps its authored height and falls through the world on Play.",
                    floating, spawns.Count));
        }

        /// <summary>
        /// A barrier faces the circuit.
        ///
        /// Vertical geometry has no opinion about up, so the facing check above
        /// skips it entirely — and a guardrail is the one thing beside the road
        /// you are always looking at. A W-beam is a single-sided sheet: turned
        /// the wrong way it does not shade oddly, it disappears, and you see the
        /// far side of the circuit through it. <c>armco</c> is handed a point
        /// list and builds its profile along the left normal of travel, which is
        /// toward the track on one side and away from it on the other, so this
        /// failure is guaranteed on exactly one side unless somebody reverses
        /// it. It was, on all three circuits, at -0.64.
        ///
        /// Meshes that come out near zero are double-sided (fences, TECPRO
        /// blocks, the pit wall) and are left alone: they read the same either
        /// way, so there is nothing to assert.
        /// </summary>
        private static void CheckBarriersFaceTrack(CircuitManifest man,
                                                   Dictionary<string, GameObject> byName,
                                                   StringBuilder sb)
        {
            var spine = new List<Vector3>();
            for (int i = 0; i < man.SpineCount; i++)
                spine.Add(CircuitAxis.Position(man.Spine(i, 1), man.Spine(i, 2),
                                               man.Spine(i, 3)));
            if (spine.Count == 0) return;

            const float cell = 25f;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < spine.Count; i++)
            {
                var k = (Mathf.FloorToInt(spine[i].x / cell), Mathf.FloorToInt(spine[i].z / cell));
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i);
            }

            int judged = 0;
            float worst = 1f;
            string worstName = null;
            foreach (var e in man.world)
            {
                if (e.group != "BARRIER") continue;
                if (!byName.TryGetValue(e.name, out var go)) continue;
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;

                float f = FacesTrack(mf.sharedMesh, spine, grid, cell);
                if (Mathf.Abs(f) < BarrierMin) continue;    // double-sided
                judged++;
                if (f < worst) { worst = f; worstName = e.name; }
            }
            if (judged == 0) return;

            sb.AppendFormat("; barriers {0} single-sided, worst {1:F2} ({2})",
                            judged, worst, worstName);
            if (worst < 0f)
                Fail(man.circuit + string.Format(
                    ": {0} faces away from the circuit ({1:F2}). A single-sided "
                    + "guardrail turned outwards is invisible from the racing "
                    + "line — you see the far side of the track through it.",
                    worstName, worst));
        }

        /// <summary>Area-weighted mean of dot(face normal, horizontal direction
        /// to the nearest centreline point). +1 all toward the track, -1 all
        /// away, ~0 double-sided. Every seventh triangle: barriers run to tens
        /// of thousands and the answer is a bulk property.</summary>
        private static float FacesTrack(Mesh m, List<Vector3> spine,
                                        Dictionary<(int, int), List<int>> grid, float cell)
        {
            var v = m.vertices;
            var t = m.triangles;
            double sum = 0.0, area = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 21)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                Vector3 fn = Vector3.Cross(b - a, c - a);
                float w = fn.magnitude;
                if (w <= 1e-9f) continue;
                Vector3 nrm = fn / w;
                if (Mathf.Abs(nrm.y) > 0.7f) continue;      // a cap, not a face
                Vector3 ctr = (a + b + c) / 3f;

                float best = float.MaxValue;
                Vector3 bp = Vector3.zero;
                int cx = Mathf.FloorToInt(ctr.x / cell), cz = Mathf.FloorToInt(ctr.z / cell);
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cz + dz), out var l)) continue;
                        foreach (int si in l)
                        {
                            float d = new Vector2(spine[si].x - ctr.x, spine[si].z - ctr.z).sqrMagnitude;
                            if (d < best) { best = d; bp = spine[si]; }
                        }
                    }
                if (best == float.MaxValue) continue;
                var to = new Vector2(bp.x - ctr.x, bp.z - ctr.z);
                if (to.sqrMagnitude < 1e-6f) continue;
                to.Normalize();
                sum += (nrm.x * to.x + nrm.z * to.y) * w;
                area += w;
            }
            return area > 0.0 ? (float)(sum / area) : 0f;
        }

        /// <summary>Area-weighted mean upward component: +1 every face up, -1
        /// every face down, ~0 for anything balanced or vertical.</summary>
        private static float MeanUp(Mesh m)
        {
            var v = m.vertices;
            var t = m.triangles;
            double up = 0.0, area = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                float w = fn.magnitude;
                if (w <= 1e-9f) continue;
                up += fn.y;          // == (fn.y / w) * w, without the divide
                area += w;
            }
            return area > 0.0 ? (float)(up / area) : 0f;
        }

        /// <summary>
        /// The same mesh, wound the same way on every circuit.
        ///
        /// This is the check with no reference object and no notion of up: it
        /// only asks whether <c>Pit_Wall</c> agrees with <c>Pit_Wall</c>. That
        /// makes it the one check that sees the bug this pipeline actually has
        /// — a builder whose quad order depends on a side variable, so the
        /// geometry is correct on a circuit whose pits are at positive u and
        /// mirrored, hence inside out, on one where they are not. Interlagos
        /// looked right and Monza and Spa did not, four separate times, and
        /// every one of them is invisible to any single-circuit test.
        ///
        /// Vertical and balanced meshes are included deliberately: their own
        /// facing means nothing, and their disagreement means everything.
        /// </summary>
        private static void CheckAcrossCircuits()
        {
            int compared = 0, split = 0;
            foreach (var kv in _facing)
            {
                if (kv.Value.Count < 2) continue;
                compared++;
                float hi = float.MinValue, lo = float.MaxValue;
                string hiAt = null, loAt = null;
                foreach (var e in kv.Value)
                {
                    if (e.Value > hi) { hi = e.Value; hiAt = e.Key; }
                    if (e.Value < lo) { lo = e.Value; loAt = e.Key; }
                }
                if (hi <= SignEps || lo >= -SignEps) continue;
                split++;
                Fail(string.Format(
                    "{0} is wound one way at {1} ({2:F2}) and the other at {3} ({4:F2}). "
                    + "The same builder cannot produce both, so its faces are ordered "
                    + "off a side variable and it is inside out on one of them.",
                    kv.Key, hiAt, hi, loAt, lo));
            }
            if (split == 0)
                CircuitPaths.Log("cross-circuit winding agrees on all "
                                 + compared + " shared meshes");
        }

        private static void CheckProps(CircuitManifest man,
                                       Dictionary<string, GameObject> byName,
                                       StringBuilder sb)
        {
            if (!byName.TryGetValue("Trk_Terrain", out var terrain)) return;
            var mr = terrain.GetComponent<MeshRenderer>();
            if (mr == null) return;
            Bounds tb = mr.bounds;
            tb.Expand(new Vector3(0f, 40f, 0f));   // props stand on the ground, not in it

            int outside = 0, total = 0;
            foreach (var inst in man.instances)
            {
                if (inst.proto != null && inst.proto.StartsWith("tree_")) continue;
                total++;
                if (!tb.Contains(CircuitAxis.Position(inst.p))) outside++;
            }
            sb.AppendFormat("props in bounds {0}/{1}", total - outside, total);
            if (outside > 0)
                Fail(man.circuit + ": " + outside + " of " + total
                     + " props are outside the terrain's own bounds — the instance "
                     + "transform converted differently from the mesh it places");
        }
    }
}
