using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// The checks that the Tiguan came through Blender the right way up, the
    /// right way round and the right way out.
    ///
    /// <b>Not one of them uses a reference object.</b> That is deliberate, and it
    /// is the lesson UNITY_EXPORT.md §9 records at length: the circuits pipeline
    /// shipped every track inside out while its axis test read PASS, because the
    /// marker solid the test measured against had itself been authored inverted
    /// and the two errors cancelled. <b>A reference object is only ground truth
    /// if something independent says so.</b> So a Tiguan marker is exactly what
    /// is NOT built here. Instead:
    ///
    ///   • a car has a roof, and roofs point up;
    ///   • a Unity primitive cube is correct by definition, not by authorship,
    ///     so it can calibrate the sign of a signed volume;
    ///   • a wheel is narrower than it is tall, and round in its own plane;
    ///   • a shape's proportions are scale-free, so they catch a NON-uniform
    ///     scale that correct absolute bounds on a free axis would miss.
    ///
    /// Run as part of <see cref="PartModelValidator.Report"/> — one gate, not
    /// two, so nobody can pass [PMV] while these are quietly not running.
    /// </summary>
    public static class TiguanChecks
    {
        // Position quantum for the watertight test, in metres. The FBX importer
        // SPLITS vertices at every hard edge and UV seam, so two triangles that
        // share an edge geometrically do not share vertex indices — matching
        // edges by index would call every mesh in this car open. Matching by
        // rounded position is what makes the test mean what it says.
        private const float WeldQuantum = 1e-5f;

        // Below this the signed volume is float noise and its sign means
        // nothing — a closed but zero-thickness shell scores ~1e-9 and would
        // otherwise be reported as inverted on the strength of rounding.
        private const float MinVolumeM3 = 1e-6f;

        // Share of closed pieces allowed to be inward-facing before this is a
        // systematic flip rather than a handful of authored cavities. See the
        // long note on Winding() for why the number is not zero.
        private const float MaxInwardShare = 0.25f;

        // Proportions, from the published car: 4.486 long x 1.839 wide (body) —
        // but the MESH is 2.099 wide because it carries the door mirrors, so the
        // ratio checked is against the measured mesh, not the published body.
        private const float RatioTol = 0.005f;   // 0.5 %

        /// <summary>Returns the number of failures; logs each one.</summary>
        public static int Run()
        {
            int fail = 0;
            fail += Body();
            fail += Wheel("wheel_tiguan");
            fail += Wheel("wheel_tiguan_r");
            fail += Manifest();
            return fail;
        }

        // -------------------------------------------------------------- body

        private static int Body()
        {
            var inst = Instantiate("body_tiguan");
            if (inst == null) return 1;
            try
            {
                int fail = 0;
                var b = Bounds(inst);

                // 1. Winding, per closed piece.
                //
                //    NOT an "a roof points up" area check, and the reason is
                //    worth keeping: a car body is CLOSED, so its upward and
                //    downward areas are equal and the measure reads ~50 % —
                //    measured 49.8 % here — whether it is inside out or not. It
                //    is the same balanced-mesh blind spot CircuitValidator
                //    already had to learn to skip. A check that cannot fail is
                //    worse than no check, because it looks like coverage.
                fail += Winding("body_tiguan", inst);

                // 2. Proportions. Absolute bounds catch a wrong scale; ratios
                //    catch a NON-UNIFORM one, which is what a mismatched author
                //    size produces and which a free-height Spec cannot see.
                fail += Ratio("body_tiguan", "Z/X", b.size.z / b.size.x, 4.4864f / 2.0990f);
                fail += Ratio("body_tiguan", "Z/Y", b.size.z / b.size.y, 4.4864f / 1.4716f);

                Debug.Log($"[PMV] TIGUAN body "
                    + $"size=({b.size.x:0.0000},{b.size.y:0.0000},{b.size.z:0.0000})");
                return fail;
            }
            finally { Object.DestroyImmediate(inst); }
        }

        // ------------------------------------------------------------- wheel

        private static int Wheel(string key)
        {
            var inst = Instantiate(key);
            if (inst == null) return 1;
            try
            {
                int fail = 0;
                var b = Bounds(inst);

                // 3. A wheel is narrower than it is tall, and round in its own
                //    plane. If the axle came out along Z — which is exactly what
                //    copying the circuits exporter's matrix would do — this
                //    fires, and unlike an absolute bounds check it says WHY.
                if (b.size.x >= b.size.y)
                {
                    Debug.LogError($"[PMV] FAIL {key}: width {b.size.x:0.0000} >= "
                        + $"height {b.size.y:0.0000} — the axle is not along X.");
                    fail++;
                }
                // Round in its own plane, but NOT perfectly: the tyre is
                // modelled with a flat contact patch, so it is ~10 mm shorter
                // than it is wide. A wheel that came back exactly circular has
                // LOST the contact patch.
                float squash = b.size.z - b.size.y;
                if (squash < 0.004f || squash > 0.020f)
                {
                    Debug.LogError($"[PMV] FAIL {key}: across-minus-tall "
                        + $"{squash:0.0000} outside 0.004..0.020 — the loaded "
                        + "contact-patch flattening is wrong or gone.");
                    fail++;
                }

                // 4. Winding, per closed piece — same calibrated test as the
                //    body. Note the tyre is NOT one closed solid here: the
                //    export separates by material, so the sidewall (tigrubber)
                //    and the tread band (tigtread) are two open sheets and
                //    neither has a signed volume on its own. The rim, cap,
                //    bolts and disc are closed and carry the check.
                fail += Winding(key, inst);

                Debug.Log($"[PMV] TIGUAN {key} "
                    + $"size=({b.size.x:0.0000},{b.size.y:0.0000},{b.size.z:0.0000})");
                return fail;
            }
            finally { Object.DestroyImmediate(inst); }
        }

        // ---------------------------------------------------------- manifest

        private static int Manifest()
        {
            var tokens = AIHWSim.Vehicles.PartVisualFactory.TiguanTokens;
            if (tokens == null || tokens.Length == 0)
            {
                Debug.LogError("[PMV] FAIL tiguan_materials: the manifest did not "
                    + "load, so every Tiguan renderer would keep its import "
                    + "material. Re-run Blender/build_tiguan_part.py.");
                return 1;
            }
            // Longest-first is the ordering guarantee that makes a first-match
            // substring bind safe. Assert it rather than trust the sort.
            for (int i = 1; i < tokens.Length; i++)
            {
                if (tokens[i - 1].Item1.Length < tokens[i].Item1.Length)
                {
                    Debug.LogError("[PMV] FAIL tiguan_materials: token table is not "
                        + $"longest-first at '{tokens[i - 1].Item1}' / "
                        + $"'{tokens[i].Item1}' — a compound token can be "
                        + "swallowed by one it contains.");
                    return 1;
                }
            }
            Debug.Log($"[PMV] TIGUAN manifest {tokens.Length} materials, ordered");
            return 0;
        }

        // ----------------------------------------------------------- helpers

        private static GameObject Instantiate(string key)
        {
            var src = Resources.Load<GameObject>("PartModels/" + key);
            if (src == null)
            {
                Debug.LogError($"[PMV] FAIL {key}: missing asset");
                return null;
            }
            return Object.Instantiate(src);
        }

        private static Bounds Bounds(GameObject inst)
        {
            var rs = inst.GetComponentsInChildren<Renderer>(true);
            var b = rs[0].bounds;
            for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
            return b;
        }

        private static int Ratio(string key, string what, float got, float want)
        {
            if (Mathf.Abs(got - want) / want <= RatioTol) return 0;
            Debug.LogError($"[PMV] FAIL {key}: ratio {what} = {got:0.0000}, "
                + $"expected {want:0.0000} — the mesh is scaled NON-UNIFORMLY.");
            return 1;
        }

        /// <summary>
        /// Every closed piece must be wound the same way round as a Unity cube.
        ///
        /// This is the reference-free form of the check the circuits pipeline
        /// got wrong. The calibration solid is a <see cref="PrimitiveType.Cube"/>
        /// — correct by DEFINITION, being the engine's own idea of an
        /// outward-wound mesh — rather than an authored marker, which is only
        /// correct if somebody authored it correctly, and once did not.
        ///
        /// Per piece rather than aggregated, because this project's actual
        /// recurring bug is a builder whose face order depends on a side
        /// variable: correct on one side, mirrored on the other. Summed, those
        /// two cancel and the total looks fine.
        ///
        /// <b>Why the threshold is a share and not zero.</b> An inward-wound
        /// closed solid is not always a mistake — it is how you model a CAVITY.
        /// This car has five: <c>TIG_FuelHousing</c> and the four
        /// <c>TIG_HandlePocket_*</c>, all recesses you are meant to see the
        /// inside of, and all correctly inverted. A check that failed on them
        /// would be wrong, and the usual answer — an exception list of five
        /// names — rots the moment the model gains a sixth pocket.
        ///
        /// What this check is actually for is a SYSTEMATIC flip, which is what
        /// a wrong axis matrix or a stray flip_normals produces: that inverts
        /// essentially every piece at once, not five of a hundred and seventy.
        /// So the gate is the share, and the outliers are named in the log
        /// where a real regression in a small part is still visible to a reader.
        /// </summary>
        private static int Winding(string key, GameObject inst)
        {
            float unit = SignedVolume(UnitCubeMesh());
            if (unit == 0f)
            {
                Debug.LogError($"[PMV] FAIL {key}: cube calibration returned 0 — "
                    + "the winding check cannot run.");
                return 1;
            }

            int closed = 0, wrong = 0, unreadable = 0;
            string first = null;
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var me = mf.sharedMesh;
                if (me == null) continue;
                if (!me.isReadable) { unreadable++; continue; }

                // Only a WATERTIGHT mesh has an inside, and only a mesh with an
                // inside has a meaningful signed-volume sign. Judging that by
                // volume-against-bounding-box instead would flag curved open
                // sheets — a wheel arch liner, a bonnet skin — whose signed
                // volume is real-looking and completely arbitrary. Measured:
                // that heuristic accused 19 of this car's 205 panels of being
                // inverted, and every one of them was an open sheet.
                if (!IsWatertight(me)) continue;
                float v = SignedVolume(me);
                if (Mathf.Abs(v) < MinVolumeM3) continue;   // sign is float noise
                closed++;
                if (v * unit < 0f)
                {
                    wrong++;
                    if (wrong <= 8)
                        first = (first == null ? "" : first + ", ")
                              + $"{mf.gameObject.name} ({v:0.######} m3)";
                }
            }

            if (unreadable > 0)
            {
                // An unreadable mesh is a BROKEN check, not an absent finding —
                // it would otherwise be counted as "nothing wrong here".
                Debug.LogError($"[PMV] FAIL {key}: {unreadable} mesh(es) are not "
                    + "CPU-readable, so their winding was never checked. See "
                    + "PartModelPostprocessor's isReadable rule.");
                return 1;
            }
            if (closed == 0)
            {
                Debug.LogError($"[PMV] FAIL {key}: no closed piece to check — "
                    + "either the export changed shape or ClosedFrac is wrong.");
                return 1;
            }
            float share = (float)wrong / closed;
            if (share > MaxInwardShare)
            {
                Debug.LogError($"[PMV] FAIL {key}: {wrong}/{closed} closed pieces "
                    + $"({share:P0}) are wound inside out ({first}). That is a "
                    + "SYSTEMATIC flip, not a set of cavities — check the export "
                    + "matrix. Blender does not backface-cull in the viewport, so "
                    + "this is invisible upstream and shows in Unity as geometry "
                    + "drawn from below.");
                return 1;
            }
            if (wrong > 0)
            {
                // Named, not hidden. These are expected to be cavities; if the
                // list ever grows a part that is not one, this line is where it
                // shows up.
                Debug.Log($"[PMV] TIGUAN {key} winding OK ({closed} closed pieces, "
                    + $"{wrong} inward — cavities: {first})");
                return 0;
            }
            Debug.Log($"[PMV] TIGUAN {key} winding OK ({closed} closed pieces)");
            return 0;
        }

        /// <summary>
        /// Whether every edge is shared by exactly two triangles — i.e. the mesh
        /// encloses a volume and the sign of that volume means something.
        ///
        /// Edges are keyed by rounded POSITION, not by vertex index, because the
        /// FBX importer splits vertices at hard edges and UV seams: a cube
        /// arrives with 24 vertices, not 8, and an index-keyed test would call
        /// it open.
        /// </summary>
        private static bool IsWatertight(Mesh me)
        {
            var v = me.vertices;
            var count = new System.Collections.Generic.Dictionary<(long, long, long, long, long, long), int>();

            (long, long, long) Key(Vector3 p) => (
                Mathf.RoundToInt(p.x / WeldQuantum),
                Mathf.RoundToInt(p.y / WeldQuantum),
                Mathf.RoundToInt(p.z / WeldQuantum));

            void Edge(Vector3 a, Vector3 b)
            {
                var ka = Key(a);
                var kb = Key(b);
                // Undirected: order the endpoints so both triangles hash the same.
                var k = System.Collections.Generic.Comparer<(long, long, long)>.Default
                            .Compare(ka, kb) <= 0
                    ? (ka.Item1, ka.Item2, ka.Item3, kb.Item1, kb.Item2, kb.Item3)
                    : (kb.Item1, kb.Item2, kb.Item3, ka.Item1, ka.Item2, ka.Item3);
                count.TryGetValue(k, out int n);
                count[k] = n + 1;
            }

            for (int s = 0; s < me.subMeshCount; s++)
            {
                var tri = me.GetTriangles(s);
                for (int i = 0; i + 2 < tri.Length; i += 3)
                {
                    Vector3 a = v[tri[i]], b = v[tri[i + 1]], c = v[tri[i + 2]];
                    Edge(a, b); Edge(b, c); Edge(c, a);
                }
            }
            if (count.Count == 0) return false;
            foreach (var n in count.Values) if (n != 2) return false;
            return true;
        }

        /// <summary>Signed volume of a closed mesh, from the winding alone.</summary>
        private static float SignedVolume(Mesh me)
        {
            if (me == null || !me.isReadable) return 0f;
            var v = me.vertices;
            double vol = 0.0;
            for (int s = 0; s < me.subMeshCount; s++)
            {
                var tri = me.GetTriangles(s);
                for (int i = 0; i + 2 < tri.Length; i += 3)
                    vol += Vector3.Dot(Vector3.Cross(v[tri[i]], v[tri[i + 1]]),
                                       v[tri[i + 2]]);
            }
            return (float)(vol / 6.0);
        }

        private static Mesh _cube;

        /// <summary>A Unity primitive cube's mesh. Correct by definition — it is
        /// the engine's own idea of an outward-wound solid — which is the whole
        /// reason it can calibrate a sign that an authored marker cannot.</summary>
        private static Mesh UnitCubeMesh()
        {
            if (_cube != null) return _cube;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go);
            return _cube;
        }
    }
}
