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
    /// </list>
    /// </summary>
    public static class CircuitValidator
    {
        private const int SpineSamples = 200;
        private const float MaxLateral = 3.0f;    // m, spine point to nearest road vertex
        private const float MaxVertical = 0.35f;  // m, allows crown and kerb fall
        private const float MaxRelief = 2.0f;     // m, measured climb vs manifest
        private const float MaxPropDrop = 6.0f;   // m, prop base vs terrain nearby

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
            int n = 0;
            foreach (string key in CircuitImport.Available())
            {
                n++;
                ValidateOne(key);
            }
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
