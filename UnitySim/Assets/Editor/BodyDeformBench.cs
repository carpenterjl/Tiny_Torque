using System.Collections.Generic;
using System.Text;
using AIHWSim.BodyEd;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[BDEF] — the runtime body-deformation editor's bench check.</b> No play
    /// mode, no physics step: it builds the deformable meshes, generates the morph
    /// frames, pulls vertices, bakes, round-trips a layout through JSON, and
    /// measures the result.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.BodyDeformBench.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <b>What it is really for.</b> Almost every way this system can be wrong is
    /// invisible in a screenshot. A brush that stopped welding tears a seam you
    /// have to be looking at the right panel to see; a save that silently drops its
    /// offsets looks perfect until the next session; a shader that failed to
    /// compile falls back to one that stretches; and a collider baked in author
    /// units is twelve times the size of the police car with no error message
    /// anywhere. Each of those is one assertion here, and three of them were found
    /// by writing it.
    ///
    /// <b>It builds hidden GameObjects.</b> A <c>SkinnedMeshRenderer</c> cannot
    /// exist without one, and the point of the rig checks is that they go through
    /// the real component rather than a re-implementation of it. Each rig is
    /// created <c>HideAndDontSave</c> and destroyed in a <c>finally</c>, so an open
    /// scene neither gains an object nor gets marked dirty.
    /// </summary>
    public static class BodyDeformBench
    {
        private const string Tag = "[BDEF]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [BDEF] Body Deform Bench", priority = 122)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            // Measured fresh, for the reason [DRAG] gives: a cached mesh or
            // silhouette from earlier in this editor session would make the bench
            // a test of the cache.
            BodyMeshSource.ResetCache();
            DragEstimator.ResetCache();

            Sources();
            Materials();
            Morphs();
            Falloff();
            RigAndBake();
            Rendering();
            RoundTrip();
            Silhouette();

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- 1. the meshes the editor opens -----------------------------------------

        private static void Sources()
        {
            Line("body            verts  welds   tris   bboxW   bboxH   bboxL   scale");
            var eligible = BodyMeshSource.Eligible();
            foreach (BodyDef def in eligible)
            {
                Mesh m = BodyMeshSource.Build(def, out Vector3 scale);
                if (m == null) { Fail($"{def.id} built nothing"); continue; }

                Vector3[] v = m.vertices;
                DeformFalloff.BuildWeldMap(v, DeformFalloff.WeldQuantum,
                                           out _, out List<int>[] groups);
                Vector3 s = m.bounds.size;
                Line($"{def.id,-15} {v.Length,5} {groups.Length,6} " +
                     $"{m.triangles.Length / 3,6} {s.x,7:0.0000} {s.y,7:0.0000} {s.z,7:0.0000} " +
                     $"{scale.x:0.###}×{scale.y:0.###}×{scale.z:0.###}");

                Bool($"{def.id} has geometry", v.Length >= 8 && m.triangles.Length >= 3,
                     "a body that flattened to nothing cannot be sculpted");
                Bool($"{def.id} welds duplicates", groups.Length <= v.Length,
                     "a weld group can never outnumber the vertices it groups");
            }

            Bool("every offered body is eligible", eligible.Count == BodyCatalog.Offered.Length,
                 "a picker row the editor cannot open is a row that opens an empty stand");

            // The primitive path builds its own boxes, so its winding is this
            // file's responsibility rather than an exporter's. A closed mesh whose
            // triangles face outward has POSITIVE signed volume under the same
            // cross-product convention Unity's own meshes use; an inverted box
            // renders inside-out and would read as negative.
            Mesh box = BodyMeshSource.Build(BodyCatalog.ById("box"), out _);
            float vol = SignedVolume(box);
            Vector3 a = CarVehicle.BodyMeshAuthorSize;
            Check("box signed volume", vol, a.x * a.y * a.z, 1e-5f, "m³",
                  "positive means the faces point outward; the value is the box itself");
            Line("");
        }

        /// <summary>Σ v0 · (v1 × v2) / 6 — positive for outward-facing windings.</summary>
        private static float SignedVolume(Mesh m)
        {
            if (m == null) return 0f;
            Vector3[] v = m.vertices;
            int[] t = m.triangles;
            double sum = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
                sum += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]]));
            return (float)(sum / 6.0);
        }

        // ---- 2. the triplanar material ------------------------------------------------

        /// <summary>
        /// That the body's shader exists, compiles, and is the one actually used.
        ///
        /// <b>This failure is silent by design elsewhere.</b>
        /// <see cref="BodyEdMaterials"/> falls back to Standard when the triplanar
        /// shader cannot be found, because a magenta car would hide the shape the
        /// editor is for — but a fallback means textures stretch under deformation,
        /// which is the entire reason the shader was written. Without this check
        /// the only symptom would be a warning nobody reads and a body that looks
        /// slightly wrong in a way that is hard to name.
        /// </summary>
        private static void Materials()
        {
            Shader sh = Shader.Find(BodyEdMaterials.TriplanarShader);
            Bool("triplanar shader found", sh != null,
                 "Assets/Resources/Shaders/AIHWSimTriplanar.shader, by name");
            if (sh == null) return;

            Bool("triplanar shader compiles", !ShaderUtil.ShaderHasError(sh),
                 "a shader with errors renders magenta and drops to the fallback");
            Bool("triplanar shader supported", sh.isSupported,
                 "Built-in RP surface shader — no pipeline asset involved");

            Material m = BodyEdMaterials.Body();
            Bool("body uses triplanar, not the fallback",
                 m != null && m.shader != null && m.shader.name == BodyEdMaterials.TriplanarShader,
                 "Standard here means UVs, and a merged multi-part mesh has none worth having");
            foreach (string p in new[] { "_TileScale", "_BlendSharpness", "_Glossiness", "_Metallic" })
                Bool($"triplanar exposes {p}", m != null && m.HasProperty(p),
                     "the panel and BodyEdMaterials both set it by name");
            Line("");
        }

        // ---- 3. procedural morph frames ----------------------------------------------

        private static void Morphs()
        {
            foreach (string id in new[] { "box", "body_shell" })
            {
                BodyDef def = BodyCatalog.ById(id);
                Mesh src = BodyMeshSource.Build(def, out _);
                if (src == null) { Fail($"{id} has no source mesh"); continue; }

                Vector3[] baseVerts = src.vertices;
                Bounds b = BodyMorphs.BoundsOf(baseVerts);

                var clone = Object.Instantiate(src);
                string[] names = BodyMorphs.AddTo(clone, baseVerts);

                Check($"{id} frame count", clone.blendShapeCount, BodyMorphs.All.Length, 0f, "",
                      "one frame per morph, in slider order");

                for (int i = 0; i < names.Length; i++)
                {
                    Check($"{id} {names[i]} frames", clone.GetBlendShapeFrameCount(i), 1, 0f, "",
                          "a single frame — the slider IS the interpolation");
                    Check($"{id} {names[i]} weight", clone.GetBlendShapeFrameWeight(i, 0), 100f,
                          1e-4f, "", "so a 0..1 slider maps onto Unity's 0..100 unscaled");

                    var dv = new Vector3[baseVerts.Length];
                    clone.GetBlendShapeFrameVertices(i, 0, dv, null, null);

                    float maxD = 0f;
                    for (int k = 0; k < dv.Length; k++) maxD = Mathf.Max(maxD, dv[k].magnitude);
                    Bool($"{id} {names[i]} moves something", maxD > 1e-4f,
                         "a morph that displaces nothing is a slider that does nothing");

                    // Determinism: the frames are regenerated at every load, so a
                    // saved weight only means the same shape if this is exact.
                    Vector3[] again = BodyMorphs.Deltas(baseVerts, b, BodyMorphs.All[i]);
                    int diff = 0;
                    for (int k = 0; k < dv.Length; k++)
                        if (dv[k] != again[k]) diff++;
                    Check($"{id} {names[i]} deterministic", diff, 0, 0f, " verts",
                          "bit-for-bit on a second generation, or a saved weight " +
                          "means a different shape next session");

                    // Co-located vertices must receive identical deltas, or a morph
                    // tears the seam it crosses. This is free rather than enforced:
                    // the deltas are functions of position alone.
                    Bool($"{id} {names[i]} seam-safe", SeamSafe(baseVerts, dv),
                         "duplicated vertices at a hard edge move together");
                }

                BodyMeshSource.DestroyMesh(clone);
            }
            Line("");
        }

        private static bool SeamSafe(Vector3[] verts, Vector3[] deltas)
        {
            DeformFalloff.BuildWeldMap(verts, DeformFalloff.WeldQuantum, out _,
                                       out List<int>[] groups);
            foreach (List<int> g in groups)
                for (int i = 1; i < g.Count; i++)
                    if ((deltas[g[i]] - deltas[g[0]]).sqrMagnitude > 1e-16f) return false;
            return true;
        }

        // ---- 4. brush arithmetic --------------------------------------------------------

        private static void Falloff()
        {
            Check("falloff at the centre", DeformFalloff.Weight(0f, 0.1f), 1f, 1e-6f, "",
                  "full strength under the cursor");
            Check("falloff at the rim", DeformFalloff.Weight(0.1f, 0.1f), 0f, 1e-6f, "",
                  "reaches zero — a curve that only approaches it moves the whole car");
            Check("falloff beyond the rim", DeformFalloff.Weight(0.2f, 0.1f), 0f, 0f, "",
                  "clamped, not extrapolated");
            Check("falloff at half radius", DeformFalloff.Weight(0.05f, 0.1f), 0.5f, 1e-6f, "",
                  "smoothstep is symmetric about its midpoint");

            bool monotone = true;
            float prev = 2f;
            for (int i = 0; i <= 32; i++)
            {
                float w = DeformFalloff.Weight(i / 32f * 0.1f, 0.1f);
                if (w > prev + 1e-6f) monotone = false;
                prev = w;
            }
            Bool("falloff monotone", monotone, "further out can never mean further moved");

            // Gathering, against a grid whose answer can be counted by hand.
            var grid = new List<Vector3>();
            for (int x = -5; x <= 5; x++)
                for (int y = -5; y <= 5; y++)
                    grid.Add(new Vector3(x * 0.01f, y * 0.01f, 0f));
            var idx = new List<int>();
            var wts = new List<float>();
            DeformFalloff.GatherIndices(grid.ToArray(), Vector3.zero, 0.0205f, idx, wts);

            int expect = 0;
            foreach (Vector3 p in grid) if (p.magnitude <= 0.0205f) expect++;
            Check("gather count", idx.Count, expect, 0f, "",
                  "every vertex inside the brush and no vertex outside it");

            // Welding: three copies of one point must arrive and leave together.
            var dup = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f),
            };
            DeformFalloff.BuildWeldMap(dup, DeformFalloff.WeldQuantum, out int[] groupOf,
                                       out List<int>[] members);
            Check("weld groups", members.Length, 2, 0f, "", "three co-located, one apart");
            Bool("weld membership", groupOf[0] == groupOf[1] && groupOf[1] == groupOf[2]
                                    && groupOf[3] != groupOf[0],
                 "position decides the group, nothing else");

            DeformFalloff.GatherWelded(dup, Vector3.zero, 0.1f, groupOf, members, idx, wts);
            Check("welded gather count", idx.Count, 3, 0f, "",
                  "the whole group comes, or the seam tears");
            Bool("welded weights equal", Mathf.Approximately(wts[0], wts[1])
                                         && Mathf.Approximately(wts[1], wts[2]),
                 "same point, same weight, whatever order the exporter wrote them in");
            Line("");
        }

        // ---- 5. the real rig: sparse offsets, and the bake ----------------------------

        private static void RigAndBake()
        {
            BodyDef def = BodyCatalog.ById("box");
            Mesh src = BodyMeshSource.Build(def, out Vector3 scale);
            if (src == null) { Fail("no box mesh to build a rig on"); return; }
            Vector3[] baseVerts = src.vertices;

            var go = new GameObject("BDEF_Rig") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var body = go.AddComponent<DeformableBody>();
                if (!body.Init(def)) { Fail("DeformableBody.Init refused the box"); return; }
                Hide(go);

                Check("rig vertex count", body.VertexCount, baseVerts.Length, 0f, "",
                      "the work mesh is a clone of the source, not a resample");
                Bool("rig built a collider", body.Collision != null, "the brush needs a surface");
                Bool("collider is unscaled", body.Collision.transform.localScale == Vector3.one,
                     "BakeMesh(useScale) already applies the renderer's scale");

                // --- sparse offsets, through the real Apply path ---
                var layout = new VehicleLayoutData
                {
                    carBasePrefabID = def.id,
                    baseVertexCount = baseVerts.Length,
                    blendShapeNames = new string[0],
                    blendShapeWeights = new float[0],
                    offsetIndex = new[] { 0, 3, 7 },
                    offsetValue = new[]
                    {
                        new Vector3(0.010f, 0f, 0f),
                        new Vector3(0f, -0.020f, 0f),
                        new Vector3(0f, 0f, 0.030f),
                    },
                };
                body.Apply(layout);

                Vector3[] after = body.WorkMesh.vertices;
                int moved = 0, wrong = 0;
                for (int i = 0; i < after.Length; i++)
                {
                    Vector3 d = after[i] - baseVerts[i];
                    if (d.sqrMagnitude <= DeformableBody.OffsetEpsilon) continue;
                    moved++;
                    int slot = System.Array.IndexOf(layout.offsetIndex, i);
                    if (slot < 0 || (d - layout.offsetValue[slot]).sqrMagnitude > 1e-12f) wrong++;
                }
                Check("sparse offsets applied", moved, 3, 0f, " verts",
                      "exactly the listed vertices moved, and only those");
                Check("sparse offsets exact", wrong, 0, 0f, " verts",
                      "each by the vector it was stored with");
                Check("offset count reported", body.OffsetCount, 3, 0f, "",
                      "what the panel shows agrees with what the mesh holds");

                // A layout from a mesh of a different size must be refused rather
                // than scattered across whatever vertices happen to share indices.
                layout.baseVertexCount = baseVerts.Length + 1;
                body.Apply(layout);
                Check("mismatched layout refused", body.OffsetCount, 0, 0f, "",
                      "an index into a re-exported mesh addresses a different point");
                layout.baseVertexCount = baseVerts.Length;

                // --- the bake ---
                body.ResetOffsets();
                int tail = System.Array.IndexOf(BodyMorphs.All, MorphKind.TailChop);
                body.UpdateVehicleMorph(tail, 100f);
                body.Collision.Rebake(body);

                Vector3[] baked = body.Collision.BakedVertices;
                Bool("bake produced vertices", baked != null && baked.Length == baseVerts.Length,
                     "BakeMesh preserves vertex order, which is what makes a hit " +
                     "in metres address a vertex in author units");

                if (baked != null && baked.Length == baseVerts.Length)
                {
                    Vector3[] delta = BodyMorphs.Deltas(baseVerts,
                        BodyMorphs.BoundsOf(baseVerts), MorphKind.TailChop);
                    float worst = 0f;
                    for (int i = 0; i < baked.Length; i++)
                    {
                        Vector3 want = Vector3.Scale(baseVerts[i] + delta[i], scale);
                        worst = Mathf.Max(worst, (baked[i] - want).magnitude);
                    }
                    Check("bake equals base + morph", worst, 0f, 1e-5f, "m",
                          "one bake carries the morphs, so the collider and the drag " +
                          "readout cannot describe different cars");
                }

                // Measured off the BOUNDS rather than the vertices on purpose: the
                // vertices were checked above, and the bounds are what every
                // physics query starts from — this is the assertion that caught
                // BakeMesh leaving them at zero after a Clear().
                Vector3 ext = body.Collision.BakedMesh.bounds.size;
                Bool("baked bounds are real", ext.x > 1e-4f && ext.z > 1e-4f,
                     "BakeMesh leaves the bounds where Clear() put them unless " +
                     "somebody recalculates them");

                body.ResetMorphs();
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            BakeIsInMetres();
            Line("");
        }

        /// <summary>
        /// The trap this whole arrangement exists to avoid, checked on the one
        /// shipped body that can show it.
        ///
        /// <c>body_patrol</c>'s mesh arrives 12.573× oversized and is rendered
        /// down by <c>authorScale</c>, so its author units and its metres differ by
        /// more than an order of magnitude. The collider has to be in metres. The
        /// documented way to get that — <c>BakeMesh(mesh, useScale: true)</c> — is
        /// a NO-OP on a renderer with no bones, which this bench established by
        /// doubling the renderer's scale and watching the baked geometry not move;
        /// so <see cref="BodyDeformCollision"/> bakes in author units and applies
        /// the scale itself. Had that gone unnoticed, the police car would have
        /// collided as a five-metre object with nothing anywhere reporting it.
        /// </summary>
        private static void BakeIsInMetres()
        {
            BodyDef def = BodyCatalog.ById("body_patrol");
            Mesh src = BodyMeshSource.Build(def, out Vector3 scale);
            if (src == null) { Fail("no body_patrol mesh"); return; }

            Bool("body_patrol is author-scaled", Mathf.Abs(scale.z - 1f) > 0.1f,
                 "the check below is only meaningful on a body whose author units " +
                 "and metres actually differ");

            var go = new GameObject("BDEF_Scale") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var body = go.AddComponent<DeformableBody>();
                if (!body.Init(def)) { Fail("DeformableBody.Init refused body_patrol"); return; }
                Hide(go);
                body.Collision.Rebake(body);

                float authorZ = src.bounds.size.z;
                float bakedZ = body.Collision.BakedMesh.bounds.size.z;
                Line($"body_patrol  author {authorZ:0.000} units  baked {bakedZ:0.0000} m  " +
                     $"scale {scale.z:0.0000}");

                Check("baked length is in metres", bakedZ, authorZ * scale.z, 1e-4f, "m",
                      "the collider must be the size of the car, not the size of the FBX");
                Bool("baked length is not author units", Mathf.Abs(bakedZ - authorZ) > 0.1f,
                     "a collider still in author units would be twelve times the car");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static void Hide(GameObject root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        // ---- 6. does a bone-less renderer actually draw -------------------------------

        /// <summary>
        /// That the body appears on screen at all.
        ///
        /// <b>Why this is worth a render target.</b> Blendshapes are a skinned-mesh
        /// feature, so the body has to be a <c>SkinnedMeshRenderer</c> — and it has
        /// no bones, no bindposes and no root bone, which is a configuration
        /// nothing else in this project uses. Unity supports it (the renderer draws
        /// at its own transform and applies only the blendshapes), but "supported"
        /// is a claim about documentation. Everything else this bench checks would
        /// pass perfectly on a body that is invisible: the mesh would be right, the
        /// bake would be right, the drag would be right, and the editor would show
        /// an empty turntable.
        ///
        /// <b>Three renders, because two could not tell the cases apart.</b> The
        /// rig also carries four wheel markers, which kept drawing when only the
        /// skinned renderer was switched off — 673 px of "empty" frame. So: all
        /// on, body off, everything off. The first difference is the body's own
        /// contribution; the last is the control, without which a graphics device
        /// that draws nothing at all would pass this for the wrong reason, and this
        /// runs in batch mode.
        /// </summary>
        private static void Rendering()
        {
            const int N = 128;
            BodyDef def = BodyCatalog.ById("body_shell");

            var go = new GameObject("BDEF_Render") { hideFlags = HideFlags.HideAndDontSave };
            var lightGo = new GameObject("BDEF_Light") { hideFlags = HideFlags.HideAndDontSave };
            var camGo = new GameObject("BDEF_Cam") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture rt = null;
            Texture2D shot = null;
            try
            {
                var body = go.AddComponent<DeformableBody>();
                if (!body.Init(def)) { Fail("DeformableBody.Init refused body_shell"); return; }
                Hide(go);

                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);

                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.orthographic = true;
                cam.orthographicSize = 0.25f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 10f;
                camGo.transform.position = new Vector3(-0.9f, 0.12f, 0f);
                camGo.transform.LookAt(Vector3.zero);

                rt = new RenderTexture(N, N, 24) { hideFlags = HideFlags.HideAndDontSave };
                cam.targetTexture = rt;
                shot = new Texture2D(N, N, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };

                int all = Shoot(cam, rt, shot, N);

                body.Smr.enabled = false;
                int noBody = Shoot(cam, rt, shot, N);

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
                int nothing = Shoot(cam, rt, shot, N);

                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                    r.enabled = true;

                Line($"render    {all}/{N * N} px with the rig ({100f * all / (N * N):0.0} %), " +
                     $"{noBody} px with the body hidden, {nothing} px with nothing");

                Bool("a bone-less SkinnedMeshRenderer draws", all - noBody > N * N / 50,
                     "no bones, no bindposes — supported, but used nowhere else here");
                Bool("the render check can fail", nothing < N * N / 500,
                     "the control: a graphics device drawing nothing would pass the " +
                     "check above for the wrong reason");
            }
            finally
            {
                if (rt != null) rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(shot);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(lightGo);
                Object.DestroyImmediate(go);
            }
            Line("");
        }

        /// <summary>Render once and count pixels that are not the clear colour.</summary>
        private static int Shoot(Camera cam, RenderTexture rt, Texture2D shot, int n)
        {
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            shot.ReadPixels(new Rect(0, 0, n, n), 0, 0);
            shot.Apply(false);
            RenderTexture.active = prev;

            int lit = 0;
            foreach (Color c in shot.GetPixels())
                if (c.r + c.g + c.b > 0.05f) lit++;
            return lit;
        }

        // ---- 7. JSON round trip -------------------------------------------------------

        private static void RoundTrip()
        {
            var d = new VehicleLayoutData
            {
                version = 1,
                carBasePrefabID = "body_shell",
                wheelbaseLength = 0.304f,
                bodySize = new Vector3(0.20f, 0.10f, 0.42f),
                blendShapeNames = new[] { "NoseWidth", "TailChop", "RooflineDrop", "SidePinch" },
                blendShapeWeights = new[] { 0f, 37.5f, 100f, 12.3456f },
                offsetIndex = new[] { 4, 91, 1337 },
                offsetValue = new[]
                {
                    new Vector3(0.0012345f, -0.002f, 0.0035f),
                    new Vector3(-1e-5f, 0f, 0f),
                    new Vector3(0.1f, 0.2f, 0.3f),
                },
                baseVertexCount = 4096,
            };

            string json = JsonUtility.ToJson(d, true);
            var b = JsonUtility.FromJson<VehicleLayoutData>(json);

            if (b == null) { Fail("layout did not survive the round trip at all"); return; }

            Check("round trip version", b.version, d.version, 0f, "", "");
            Bool("round trip body id", b.carBasePrefabID == d.carBasePrefabID, "");
            Check("round trip wheelbase", b.wheelbaseLength, d.wheelbaseLength, 0f, "m",
                  "bit-exact; JsonUtility round-trips a float through R format");
            Check("round trip vertex count", b.baseVertexCount, d.baseVertexCount, 0f, "", "");
            Check("round trip weight count", b.blendShapeWeights.Length,
                  d.blendShapeWeights.Length, 0f, "", "");

            int wDiff = 0;
            for (int i = 0; i < d.blendShapeWeights.Length; i++)
                if (b.blendShapeWeights[i] != d.blendShapeWeights[i]) wDiff++;
            Check("round trip weights exact", wDiff, 0, 0f, "",
                  "a reloaded car has to be the SAME car, not a near one");

            int nDiff = 0;
            for (int i = 0; i < d.blendShapeNames.Length; i++)
                if (b.blendShapeNames[i] != d.blendShapeNames[i]) nDiff++;
            Check("round trip names exact", nDiff, 0, 0f, "",
                  "the names are what a load matches weights on");

            int oDiff = 0;
            for (int i = 0; i < d.offsetIndex.Length; i++)
                if (b.offsetIndex[i] != d.offsetIndex[i] ||
                    b.offsetValue[i] != d.offsetValue[i]) oDiff++;
            Check("round trip offsets exact", oDiff, 0, 0f, "", "sparse pairs stay paired");

            // A file with almost nothing in it must read as an undeformed body
            // rather than as an error — the 0-sentinel convention this project
            // uses everywhere else.
            var bare = JsonUtility.FromJson<VehicleLayoutData>("{\"carBasePrefabID\":\"box\"}");
            Bool("bare layout loads", bare != null && bare.carBasePrefabID == "box"
                                      && bare.wheelbaseLength == 0f && bare.version == 1,
                 "an absent key leaves the field at its initialiser, which IS the default");
            Line("");
        }

        // ---- 8. does the measurement follow the shape --------------------------------

        /// <summary>
        /// The claim the drag readout makes: deform the body and the number moves,
        /// in the direction the shape moved it.
        ///
        /// Written against soups built here rather than against a rig, so the check
        /// is about <c>TryEstimateSoup</c> and the correlation rather than about
        /// the editor's plumbing — which <see cref="RigAndBake"/> already covers.
        ///
        /// <b>Measured on a real shell, not on the primitive box, and that is a
        /// finding rather than a preference.</b> <c>DragEstimator.Rasterise</c>
        /// stamps a triangle's whole frontal footprint into every station its
        /// z-range spans. That is conservative and harmless on a mesh with
        /// thousands of small triangles, but a box has twenty-four vertices, so
        /// tapering its nose produces a frustum whose side faces are two triangles
        /// running the full length — and each of those hands its widest footprint
        /// to the nose stations. The taper is real geometry and reads as nearly
        /// prismatic. A sculptor is never in that situation; a bench that asserted
        /// on it would be measuring the rasteriser's resolution.
        /// </summary>
        private static void Silhouette()
        {
            BodyDef def = BodyCatalog.ById("body_shell");
            Mesh shell = BodyMeshSource.Build(def, out Vector3 scale);
            if (shell == null) { Fail("no body_shell mesh to measure"); return; }

            List<Vector3> flat = Soup(shell, scale, null);
            if (!DragEstimator.TryEstimateSoup(flat, null, "bdef:shell", out var basis))
            { Fail("the shell soup could not be measured"); return; }

            // A nose taper: pinch the front 40 % of the length toward the
            // centreline. Fewer square metres arriving abruptly, and the area that
            // does arrive builds over a run.
            Bounds b = shell.bounds;
            List<Vector3> tapered = Soup(shell, scale, v =>
            {
                float nz = (v.z - b.min.z) / Mathf.Max(1e-6f, b.size.z);
                float t = Mathf.Clamp01((nz - 0.6f) / 0.4f);
                float k = Mathf.Lerp(1f, 0.35f, t * t * (3f - 2f * t));
                return new Vector3(b.center.x + (v.x - b.center.x) * k,
                                   b.center.y + (v.y - b.center.y) * k, v.z);
            });
            DragEstimator.TryEstimateSoup(tapered, null, "bdef:tapered", out var taper);

            // Widened everywhere: same shape, more frontal area.
            List<Vector3> wide = Soup(shell, scale, v =>
                new Vector3(b.center.x + (v.x - b.center.x) * 1.3f, v.y, v.z));
            DragEstimator.TryEstimateSoup(wide, null, "bdef:wide", out var widened);

            Line($"shell     cd {basis.cd:0.0000}  area {basis.frontalArea:0.00000} m²");
            Line($"tapered   cd {taper.cd:0.0000}  area {taper.frontalArea:0.00000} m²");
            Line($"widened   cd {widened.cd:0.0000}  area {widened.frontalArea:0.00000} m²");

            Greater("a tapered nose measures cleaner than a flat one", basis.cd, taper.cd,
                    TaperMargin,
                    "the whole argument for measuring the shape rather than naming it");
            Greater("a widened body measures more frontal area",
                    widened.frontalArea, basis.frontalArea, 1e-5f,
                    "area is the term a sculptor moves most easily");
            Check("widening does not move cd much", widened.cd / basis.cd, 1f, 0.15f, "",
                  "a coefficient describes the shape; the metres belong to the area");
            Line("");
        }

        /// <summary>
        /// How much a 65 % nose pinch has to be worth on the touring shell.
        ///
        /// Set from what the geometry measures rather than from what a taper
        /// sounds like it ought to be worth — the same rule <c>[DRAG]</c>'s
        /// ordering checks follow. It is a floor with room under the observed
        /// value, so the assertion is "the taper is worth something substantial",
        /// not a second pin on a number already recorded above. As measured the
        /// pinch is worth 0.0378 of cd on the touring shell (0.3474 → 0.3096); the
        /// margin is set at roughly half of that.
        /// </summary>
        private const float TaperMargin = 0.02f;

        private static List<Vector3> Soup(Mesh m, Vector3 scale, System.Func<Vector3, Vector3> warp)
        {
            Vector3[] v = m.vertices;
            int[] t = m.triangles;
            var soup = new List<Vector3>(t.Length);
            for (int i = 0; i < t.Length; i++)
            {
                Vector3 p = v[t[i]];
                if (warp != null) p = warp(p);
                soup.Add(Vector3.Scale(p, scale));
            }
            return soup;
        }

        // ---- harness ------------------------------------------------------------------

        private static void Check(string name, float got, float expect, float tol,
                                  string units, string why)
        {
            _checks++;
            bool ok = Mathf.Abs(got - expect) <= tol;
            if (!ok) _failed++;

            string line = $"{(ok ? "ok  " : "FAIL")} {name,-36} {got,10:0.#####} {units,-6}"
                          + $" (expect {expect:0.#####} ±{tol:0.#####})" +
                          (string.IsNullOrEmpty(why) ? "" : $"  — {why}");
            if (ok) Line(line); else Debug.LogError($"{Tag} {line}");
        }

        private static void Bool(string name, bool ok, string why)
        {
            _checks++;
            if (!ok) _failed++;
            string line = $"{(ok ? "ok  " : "FAIL")} {name,-36}" +
                          (string.IsNullOrEmpty(why) ? "" : $"  — {why}");
            if (ok) Line(line); else Debug.LogError($"{Tag} {line}");
        }

        private static void Greater(string name, float bigger, float smaller, float margin,
                                    string why)
        {
            _checks++;
            bool ok = bigger > smaller + margin;
            string line = $"{(ok ? "ok  " : "FAIL")} {name,-48} {bigger:0.#####} > " +
                          $"{smaller:0.#####} (+{margin:0.#####})  — {why}";
            if (ok) Line(line); else { _failed++; Debug.LogError($"{Tag} {line}"); }
        }

        private static void Fail(string what)
        {
            _checks++; _failed++;
            Debug.LogError($"{Tag} FAIL {what}");
        }

        private static void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
