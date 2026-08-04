using System.Collections.Generic;
using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    // RibbonMeshMarker and SplineRunMarker used to live here. They moved to their
    // own files — see either one for the whole story: a ribbon baked at EDIT time
    // and saved serialized them against a MonoScript stub, because Unity creates
    // one MonoScript per .cs file named after the file, and reloaded them as
    // Missing Scripts. Invisible while every ribbon was built at Play.

    /// <summary>
    /// Turns a <see cref="SplineSpec"/> into ribbon geometry: one Mesh (+
    /// MeshCollider + <see cref="SurfaceTag"/>) per contiguous surface run,
    /// optional red/white kerb stripes as a second submesh, an optional extruded
    /// edge-wall mesh, and optional painted lane lines as a separate
    /// collider-less mesh. The ribbon top sits <see cref="TopOffset"/>
    /// above each control point's y with a solid downward skirt, so ground-level
    /// ribbons sink into the floor slab (no knife edges, no z-fighting).
    /// </summary>
    public static class RibbonMeshBuilder
    {
        public const float TopOffset = 0.008f;
        public const float Skirt = 0.04f;
        public const float StripeWidth = 0.09f;
        public const float WallHeight = 0.09f;
        public const float WallWidth = 0.20f;

        /// <summary>How far painted lane lines float above the ribbon top. Enough
        /// to beat depth precision on a banked ribbon, small enough that it is
        /// paint rather than a kerb — and it never reaches the physics, because
        /// the line mesh carries no collider.</summary>
        public const float LineLift = 0.005f;

        private static Material _kerbMat, _wallMat;

        /// <summary>Lane-line materials by (colour, dash ratio). Small and bounded:
        /// a scene has one or two line styles, not one per ribbon.</summary>
        private static readonly Dictionary<long, Material> _lineMats =
            new Dictionary<long, Material>();

        private static Material KerbMat
        {
            get
            {
                if (_kerbMat == null)
                {
                    _kerbMat = TrackBuilder.StandardMat(Color.white);
                    _kerbMat.mainTexture = TrackBuilder.StripeTexture(
                        new Color(0.85f, 0.15f, 0.12f), new Color(0.94f, 0.94f, 0.94f), 2, 16);
                }
                return _kerbMat;
            }
        }

        private static Material WallMat =>
            _wallMat != null ? _wallMat : _wallMat = TrackBuilder.StandardMat(new Color(0.60f, 0.60f, 0.58f));

        /// <summary>Build one spline's geometry under a new "Spline_{index}" root.</summary>
        public static GameObject Build(SplineSpec s, int splineIndex, Transform parent, bool colliders)
        {
            var root = new GameObject($"Spline_{splineIndex}");
            root.transform.SetParent(parent, false);
            if (s == null || s.Count < 2) return root;
            s.EnsureArrays();

            var samples = SplineMath.SampleAll(s);
            int n = samples.Count;
            if (n < 2) return root;
            SplineMath.ComputeFrames(samples, s.closed, out var right, out var up);

            // Total loop length for wrapped v-coordinates on closed splines.
            float totalLen = samples[n - 1].dist +
                (s.closed ? Vector3.Distance(samples[n - 1].pos, samples[0].pos) : 0f);

            // Quads connect ring q -> q+1; closed loops wrap.
            int quadCount = s.closed ? n : n - 1;

            // Contiguous same-surface quad runs (quad q takes sample q's surface).
            var runs = FindRuns(samples, quadCount, s.closed);

            foreach (var (startQuad, count, surfaceType) in runs)
                BuildRun(s, splineIndex, root.transform, samples, right, up,
                    startQuad, count, surfaceType, n, totalLen, colliders);

            if (s.edgeWalls)
                BuildWalls(root.transform, samples, right, up, n, quadCount, colliders);

            BuildLines(root.transform, s, samples, right, up, n, quadCount, totalLen);

            return root;
        }

        private static List<(int start, int count, int surface)> FindRuns(
            List<SplineMath.Sample> samples, int quadCount, bool closed)
        {
            var runs = new List<(int, int, int)>();
            int runStart = 0;
            for (int q = 1; q <= quadCount; q++)
            {
                if (q == quadCount || samples[q].surfaceType != samples[runStart].surfaceType)
                {
                    runs.Add((runStart, q - runStart, samples[runStart].surfaceType));
                    runStart = q;
                }
            }
            // Closed loop whose first and last runs share a surface: merge across
            // the seam by letting the last run flow into the first (negative start).
            if (closed && runs.Count > 1)
            {
                var first = runs[0];
                var last = runs[runs.Count - 1];
                if (first.Item3 == last.Item3)
                {
                    runs.RemoveAt(runs.Count - 1);
                    runs[0] = (last.Item1 - quadCount /* wraps negative */, last.Item2 + first.Item2, first.Item3);
                }
            }
            return runs;
        }

        // ---- ring accessors (indices wrap on closed loops) --------------------

        private static (Vector3 top, Vector3 r, Vector3 u, float w, float v) Ring(
            List<SplineMath.Sample> samples, Vector3[] right, Vector3[] up,
            int i, int n, float totalLen)
        {
            int m = ((i % n) + n) % n;
            int wraps = Mathf.FloorToInt((float)i / n);
            var smp = samples[m];
            return (smp.pos + up[m] * TopOffset, right[m], up[m], smp.width,
                    (smp.dist + totalLen * wraps) / 4f);
        }

        // ---- surface run -------------------------------------------------------

        private static void BuildRun(SplineSpec s, int splineIndex, Transform parent,
            List<SplineMath.Sample> samples, Vector3[] right, Vector3[] up,
            int startQuad, int quads, int surfaceType, int n, float totalLen, bool colliders)
        {
            int rings = quads + 1;
            bool stripes = s.edgeStripes;

            // Vertex layout per ring:
            //  top:   0 TL, 1 TR            (normals = ring up)
            //  sides: 2 TL, 3 BL, 4 TR, 5 BR (skirt, normals recalculated)
            //  kerbs: 6..9 (stripe strips, submesh 1) when enabled
            int stride = stripes ? 10 : 6;
            var verts = new Vector3[rings * stride];
            var norms = new Vector3[rings * stride];
            var uvs = new Vector2[rings * stride];
            var samplePosArr = new Vector3[rings];
            var samplePtArr = new int[rings];

            for (int k = 0; k < rings; k++)
            {
                var (top, r, u, w, v) = Ring(samples, right, up, startQuad + k, n, totalLen);
                int m = (((startQuad + k) % n) + n) % n;
                samplePosArr[k] = top;
                samplePtArr[k] = samples[m].pointIndex;

                float half = w * 0.5f;
                Vector3 tl = top - r * half, tr = top + r * half;
                Vector3 bl = tl - u * Skirt, br = tr - u * Skirt;
                int b = k * stride;

                verts[b + 0] = tl; verts[b + 1] = tr;
                norms[b + 0] = u; norms[b + 1] = u;
                uvs[b + 0] = new Vector2(0f, v);
                uvs[b + 1] = new Vector2(w / 4f, v);

                verts[b + 2] = tl; verts[b + 3] = bl;
                verts[b + 4] = tr; verts[b + 5] = br;
                uvs[b + 2] = new Vector2(0f, v); uvs[b + 3] = new Vector2(0.05f, v);
                uvs[b + 4] = new Vector2(0f, v); uvs[b + 5] = new Vector2(0.05f, v);
                // side normals filled by RecalculateNormals pass below
                norms[b + 2] = norms[b + 3] = -r;
                norms[b + 4] = norms[b + 5] = r;

                if (stripes)
                {
                    Vector3 lift = u * 0.006f;
                    verts[b + 6] = tl + lift; verts[b + 7] = tl + r * StripeWidth + lift;
                    verts[b + 8] = tr - r * StripeWidth + lift; verts[b + 9] = tr + lift;
                    for (int j = 6; j <= 9; j++) norms[b + j] = u;
                    float sv = v * 4f / 1.2f; // stripe every 1.2 m
                    uvs[b + 6] = new Vector2(0f, sv); uvs[b + 7] = new Vector2(1f, sv);
                    uvs[b + 8] = new Vector2(0f, sv); uvs[b + 9] = new Vector2(1f, sv);
                }
            }

            var triSurf = new List<int>(quads * 18);
            var triKerb = stripes ? new List<int>(quads * 12) : null;
            for (int q = 0; q < quads; q++)
            {
                int a = q * stride, c = (q + 1) * stride;
                Quad(triSurf, a + 0, c + 0, a + 1, c + 1);   // top
                Quad(triSurf, a + 3, c + 3, a + 2, c + 2);   // left skirt (outward -r)
                Quad(triSurf, a + 4, c + 4, a + 5, c + 5);   // right skirt (outward +r)
                if (stripes)
                {
                    Quad(triKerb, a + 6, c + 6, a + 7, c + 7);
                    Quad(triKerb, a + 8, c + 8, a + 9, c + 9);
                }
            }

            var mesh = new Mesh { name = $"RibbonRun_{splineIndex}_{startQuad}" };
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.subMeshCount = stripes ? 2 : 1;
            mesh.SetTriangles(triSurf, 0);
            if (stripes) mesh.SetTriangles(triKerb, 1);
            mesh.RecalculateBounds();

            var go = new GameObject($"Run_{startQuad}_{TrackCatalog.Floors[surfaceType].id}");
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = stripes
                ? new[] { TrackCatalog.Floors[surfaceType].Mat, KerbMat }
                : new[] { TrackCatalog.Floors[surfaceType].Mat };
            go.AddComponent<RibbonMeshMarker>().mesh = mesh;

            var marker = go.AddComponent<SplineRunMarker>();
            marker.splineIndex = splineIndex;
            marker.samplePos = samplePosArr;
            marker.samplePointIndex = samplePtArr;

            if (colliders)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                go.AddComponent<SurfaceTag>().floorType = surfaceType;
            }
        }

        // ---- edge walls ----------------------------------------------------------

        private static void BuildWalls(Transform parent, List<SplineMath.Sample> samples,
            Vector3[] right, Vector3[] up, int n, int quadCount, bool colliders)
        {
            int rings = quadCount + 1;
            float totalLen = samples[n - 1].dist;
            // 8 verts per ring: 4 per edge (innerBottom, innerTop, outerTop, outerBottom)
            var verts = new Vector3[rings * 8];
            var uvs = new Vector2[rings * 8];
            for (int k = 0; k < rings; k++)
            {
                var (top, r, u, w, v) = Ring(samples, right, up, k, n, totalLen);
                float half = w * 0.5f;
                int b = k * 8;
                for (int side = 0; side < 2; side++)
                {
                    float sgn = side == 0 ? -1f : 1f;
                    Vector3 baseIn = top + r * (sgn * half);
                    Vector3 baseOut = baseIn + r * (sgn * WallWidth);
                    int o = b + side * 4;
                    verts[o + 0] = baseIn;                        // inner bottom
                    verts[o + 1] = baseIn + u * WallHeight;       // inner top
                    verts[o + 2] = baseOut + u * WallHeight;      // outer top
                    verts[o + 3] = baseOut - u * Skirt;           // outer bottom (skirted)
                    for (int j = 0; j < 4; j++) uvs[o + j] = new Vector2(j / 3f, v);
                }
            }

            var tris = new List<int>(quadCount * 36);
            for (int q = 0; q < quadCount; q++)
            {
                int a = q * 8, c = (q + 1) * 8;
                for (int side = 0; side < 2; side++)
                {
                    int o = side * 4;
                    bool flip = side == 0; // keep outward-facing winding on both edges
                    WallQuad(tris, a + o + 0, c + o + 0, a + o + 1, c + o + 1, flip);  // inner face
                    WallQuad(tris, a + o + 1, c + o + 1, a + o + 2, c + o + 2, flip);  // top face
                    WallQuad(tris, a + o + 2, c + o + 2, a + o + 3, c + o + 3, flip);  // outer face
                }
            }

            var mesh = new Mesh { name = "RibbonWalls" };
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("Walls");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = WallMat;
            go.AddComponent<RibbonMeshMarker>().mesh = mesh;
            if (colliders) go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        // ---- painted lane lines ---------------------------------------------------

        /// <summary>
        /// Paint <see cref="RoadLineStyle"/> onto the ribbon: a centre line or a
        /// double line, an optional pair inside the edges, solid or dashed.
        ///
        /// <b>Its own GameObject, with no collider and no <see cref="SurfaceTag"/>,
        /// and that is the whole safety argument.</b> The surface runs above are not
        /// touched by this feature at all, so a spline with no markings produces the
        /// same vertices it always did — inert by construction rather than by a test.
        /// It also keeps 5 mm of paint out of the MeshCollider: a lane line down the
        /// middle of the road is exactly where the wheels are, and a step there is
        /// suspension input, not decoration. (The kerb stripes DO ride in the
        /// collider, which is tolerable only because they sit at the edges.)
        ///
        /// One mesh for the whole spline rather than one per surface run: paint does
        /// not care what the road is made of, and a single strip keeps the dash phase
        /// continuous across a surface change.
        /// </summary>
        private static void BuildLines(Transform parent, SplineSpec s,
            List<SplineMath.Sample> samples, Vector3[] right, Vector3[] up,
            int n, int quadCount, float totalLen)
        {
            var style = s.lines;
            int lines = style != null ? style.LineCount : 0;
            if (lines == 0) return;

            int rings = quadCount + 1;
            int stride = lines * 2;
            float half = Mathf.Max(0.005f, style.width) * 0.5f;

            // Dash phase across the seam of a closed loop: stretch the period to the
            // nearest whole number of dashes, so the loop closes on a dash boundary
            // instead of showing one short dash wherever the start line happens to be.
            float period = style.DashPeriod;
            if (period > 0f && s.closed && totalLen > period)
                period = totalLen / Mathf.Max(1f, Mathf.Round(totalLen / period));

            var verts = new Vector3[rings * stride];
            var norms = new Vector3[rings * stride];
            var uvs = new Vector2[rings * stride];
            var offsets = new float[lines];

            for (int k = 0; k < rings; k++)
            {
                var (top, r, u, w, v) = Ring(samples, right, up, k, n, totalLen);
                LineOffsets(style, w, offsets);

                // Ring's v is distance/4 (the surface's own tiling); the dash pattern
                // wants whole periods, so undo that and re-scale.
                float dv = period > 0f ? v * 4f / period : v;
                Vector3 lift = u * LineLift;
                int b = k * stride;

                for (int i = 0; i < lines; i++)
                {
                    Vector3 c = top + r * offsets[i] + lift;
                    int o = b + i * 2;
                    verts[o + 0] = c - r * half;
                    verts[o + 1] = c + r * half;
                    norms[o + 0] = norms[o + 1] = u;
                    uvs[o + 0] = new Vector2(0f, dv);
                    uvs[o + 1] = new Vector2(1f, dv);
                }
            }

            var tris = new List<int>(quadCount * lines * 6);
            for (int q = 0; q < quadCount; q++)
            {
                int a = q * stride, c = (q + 1) * stride;
                for (int i = 0; i < lines; i++)
                {
                    int o = i * 2;
                    Quad(tris, a + o + 0, c + o + 0, a + o + 1, c + o + 1);
                }
            }

            var mesh = new Mesh { name = "RibbonLines" };
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();

            var go = new GameObject("Lines");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = LineMat(style);
            go.AddComponent<RibbonMeshMarker>().mesh = mesh;
        }

        /// <summary>
        /// Where each painted line sits, as a signed offset from the centreline of a
        /// ring <paramref name="w"/> metres wide. The COUNT is fixed by the style so
        /// the mesh stride is constant; only the offsets move, which is what lets an
        /// edge line follow a corner that widens.
        /// </summary>
        private static void LineOffsets(RoadLineStyle st, float w, float[] into)
        {
            int i = 0;
            float gap = Mathf.Max(0f, st.spacing);
            float lineW = Mathf.Max(0.005f, st.width);

            int centre = Mathf.Clamp(st.centreLines, 0, 2);
            if (centre == 1) into[i++] = 0f;
            else if (centre == 2)
            {
                // The gap is between the two lines, not between their centres.
                float d = (gap + lineW) * 0.5f;
                into[i++] = -d;
                into[i++] = d;
            }

            if (st.edgeLines)
            {
                // Inset from the edge by `spacing`, then in again by half the paint,
                // clamped so a line never hangs off a road narrower than its own trim.
                float e = Mathf.Max(0f, w * 0.5f - gap - lineW * 0.5f);
                into[i++] = -e;
                into[i++] = e;
            }
        }

        /// <summary>
        /// A material per (colour, dash ratio). Solid lines are a plain matte
        /// Standard material; dashed ones add an alpha-cutout mask whose v axis is
        /// one dash period, so the dash length is exact rather than quantised to the
        /// 0.4 m ribbon sampling — dashes shorter than one sample are the normal case
        /// at this scale, so per-quad culling was never an option.
        /// </summary>
        private static Material LineMat(RoadLineStyle st)
        {
            int steps = Mathf.RoundToInt(st.DashRatio * DashTexHeight);
            long key = ((long)st.color.GetHashCode() << 8) | (uint)steps;
            if (_lineMats.TryGetValue(key, out var cached) && cached != null) return cached;

            var mat = TrackBuilder.StandardMat(st.color);
            mat.name = $"RoadLine_{steps}";
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
            if (steps < DashTexHeight) MakeCutout(mat, DashTexture(steps));

            _lineMats[key] = mat;
            return mat;
        }

        private const int DashTexHeight = 64;

        /// <summary>One dash period as an alpha ramp: opaque for the painted part,
        /// transparent for the gap. Four pixels wide because a 1-pixel texture is a
        /// portability trap on some import paths, not because the width carries
        /// anything.</summary>
        private static Texture2D DashTexture(int paintedRows)
        {
            var tex = new Texture2D(4, DashTexHeight, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            for (int y = 0; y < DashTexHeight; y++)
            {
                var c = new Color(1f, 1f, 1f, y < paintedRows ? 1f : 0f);
                for (int x = 0; x < 4; x++) tex.SetPixel(x, y, c);
            }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Put a Standard material into cutout mode. Every one of these matters:
        /// setting <c>_Mode</c> alone changes what the inspector SAYS and nothing
        /// about what renders, because the shader branches on keywords and blend
        /// state that only the inspector's own code normally writes.
        /// </summary>
        private static void MakeCutout(Material mat, Texture2D mask)
        {
            mat.mainTexture = mask;
            mat.SetFloat("_Mode", 1f);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        private static void Quad(List<int> tris, int a0, int a1, int b0, int b1)
        {
            // Winding order: with the parallel-transport frames (up = right ×
            // tangent), this order fronts the top faces UP and the skirts
            // outward. The previous order was inverted — ribbons rendered
            // inside-out (top visible only from below) and, because raycasts
            // ignore backfaces, wheels/item-drops/paint rays passed straight
            // through raised ribbon tops.
            tris.Add(b0); tris.Add(a1); tris.Add(a0);
            tris.Add(b1); tris.Add(a1); tris.Add(b0);
        }

        private static void WallQuad(List<int> tris, int a0, int a1, int b0, int b1, bool flip)
        {
            if (!flip) Quad(tris, a0, a1, b0, b1);
            else Quad(tris, b0, b1, a0, a1);
        }
    }
}
