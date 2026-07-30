using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Converts a Unity <see cref="SplineContainer"/> into this project's own
    /// <see cref="SplineSpec"/>.
    ///
    /// <b>Why convert rather than teach the ribbon builder about Unity splines.</b>
    /// <see cref="RibbonMeshBuilder"/> threads a SplineSpec through every helper —
    /// surface runs, kerb submeshes, wall extrusion, the winding-order fix. Making
    /// it sample-driven would touch all of that, and the existing tile-map ribbons
    /// would have to be proven byte-identical afterwards. Converting instead means
    /// the ribbon builder is not modified AT ALL, so byte-identical output is
    /// guaranteed by construction rather than by a test. It also means the authored
    /// road lands in the one representation the bot path, the arcade spine and the
    /// racing-line solver already consume.
    ///
    /// The cost, stated plainly: Unity splines carry per-knot Bezier tangents, and
    /// SplineSpec is uniform Catmull-Rom through its points. The shape is preserved
    /// by RESAMPLING densely rather than by copying knots — spacing controls the
    /// fidelity, and a tight corner authored with long tangents needs a smaller
    /// spacing than a sweeping one. It is not a knot-for-knot round trip and is not
    /// meant to be.
    /// </summary>
    public static class UnitySplineSampler
    {
        /// <summary>Control-point spacing along the curve, in metres. 0.5 m at
        /// 1:10 scale is 5 m of real road — fine enough that Catmull-Rom through
        /// the results tracks a Bezier to well under a centimetre.</summary>
        public const float DefaultSpacing = 0.5f;

        /// <summary>Dense pre-pass used to build the arc-length table. Uniform in
        /// the spline's own t, which is NOT uniform in distance — which is the
        /// whole reason the table exists.</summary>
        private const int ArcSamplesPerMetre = 40;

        /// <summary>Everything the conversion needs that is not the curve itself.</summary>
        public struct Settings
        {
            public float spacing;
            public float defaultWidth;
            public int defaultSurface;
            public bool edgeWalls;
            public bool edgeStripes;

            public static Settings Default => new Settings
            {
                spacing = DefaultSpacing,
                defaultWidth = SplineSpec.DefaultWidth,
                defaultSurface = SplineSpec.DefaultSurface,
                edgeWalls = false,
                edgeStripes = false,
            };
        }

        /// <summary>
        /// Convert one spline of a container to a SplineSpec in WORLD space.
        ///
        /// World space, not local: SplineSpec points are consumed by the ribbon
        /// builder, the bot path and the surface lookup, none of which know about
        /// the container's transform. Baking the transform in here is the one place
        /// it can be done once.
        /// </summary>
        public static SplineSpec ToSpec(SplineContainer container, int splineIndex,
            Settings settings,
            SplineData<float> widthData = null,
            SplineData<float> rollData = null,
            SplineData<float> surfaceData = null)
        {
            var spec = new SplineSpec();
            if (container == null) return spec;
            var splines = container.Splines;
            if (splines == null || splineIndex < 0 || splineIndex >= splines.Count) return spec;

            var spline = splines[splineIndex];
            if (spline.Count < 2) return spec;

            var l2w = container.transform.localToWorldMatrix;
            spec.closed = spline.Closed;
            spec.edgeWalls = settings.edgeWalls;
            spec.edgeStripes = settings.edgeStripes;

            var table = BuildArcTable(spline, l2w);
            float total = table[table.Count - 1].dist;
            if (total <= 1e-4f) return spec;

            float spacing = Mathf.Max(0.05f, settings.spacing);
            // A closed loop must NOT emit a duplicate point at the seam — SplineSpec
            // wraps, so a repeated first point would produce a zero-length segment
            // and a NaN tangent right where the start line usually is.
            int count = Mathf.Max(2, Mathf.RoundToInt(total / spacing));
            float step = spec.closed ? total / count : total / (count - 1);

            for (int i = 0; i < count; i++)
            {
                float d = Mathf.Min(i * step, total);
                float t = TAtDistance(table, d);

                spec.points.Add(SampleWorld(spline, l2w, t));
                spec.rollDeg.Add(Evaluate(rollData, spline, t, 0f));
                spec.widths.Add(Mathf.Max(0.3f,
                    Evaluate(widthData, spline, t, settings.defaultWidth)));
                spec.surface.Add(Mathf.Clamp(
                    Mathf.RoundToInt(Evaluate(surfaceData, spline, t, settings.defaultSurface)),
                    0, TrackCatalog.Floors.Length - 1));
            }

            spec.EnsureArrays();
            return spec;
        }

        /// <summary>
        /// The centreline and per-node half width a bot corridor needs, sampled at
        /// the ribbon resolution the rest of the game uses (0.4 m, SplineMath's
        /// default) rather than at the authoring spacing.
        /// </summary>
        public static void ToCorridor(SplineSpec spec, out Vector3[] centerline,
            out float[] halfWidths, out bool closed)
        {
            closed = spec != null && spec.closed;
            if (spec == null || spec.Count < 2)
            {
                centerline = System.Array.Empty<Vector3>();
                halfWidths = System.Array.Empty<float>();
                return;
            }

            var samples = SplineMath.SampleAll(spec);
            centerline = new Vector3[samples.Count];
            halfWidths = new float[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                centerline[i] = samples[i].pos;
                halfWidths[i] = samples[i].width * 0.5f;
            }
        }

        // -------------------------------------------------------------------
        // arc length
        // -------------------------------------------------------------------

        private struct ArcEntry { public float t; public float dist; }

        /// <summary>
        /// Cumulative distance against curve parameter. Unity's t is uniform in
        /// the parameter, not in distance — stepping t evenly bunches points in
        /// tight corners and spreads them on straights, which is precisely backwards
        /// from what a road wants.
        /// </summary>
        private static List<ArcEntry> BuildArcTable(Spline spline, Matrix4x4 l2w)
        {
            // A rough length first, only to choose how densely to measure.
            float rough = 0f;
            Vector3 prevRough = SampleWorld(spline, l2w, 0f);
            for (int i = 1; i <= 64; i++)
            {
                var p = SampleWorld(spline, l2w, i / 64f);
                rough += Vector3.Distance(prevRough, p);
                prevRough = p;
            }

            int n = Mathf.Clamp(Mathf.CeilToInt(rough * ArcSamplesPerMetre), 64, 20000);
            var table = new List<ArcEntry>(n + 1);
            Vector3 prev = SampleWorld(spline, l2w, 0f);
            float dist = 0f;
            table.Add(new ArcEntry { t = 0f, dist = 0f });
            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n;
                var p = SampleWorld(spline, l2w, t);
                dist += Vector3.Distance(prev, p);
                prev = p;
                table.Add(new ArcEntry { t = t, dist = dist });
            }
            return table;
        }

        /// <summary>Curve parameter at a given arc length, by binary search on the
        /// table and a linear blend inside the bracketing pair.</summary>
        private static float TAtDistance(List<ArcEntry> table, float d)
        {
            int lo = 0, hi = table.Count - 1;
            if (d <= 0f) return 0f;
            if (d >= table[hi].dist) return 1f;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (table[mid].dist <= d) lo = mid; else hi = mid;
            }
            float span = table[hi].dist - table[lo].dist;
            float f = span > 1e-6f ? (d - table[lo].dist) / span : 0f;
            return Mathf.Lerp(table[lo].t, table[hi].t, f);
        }

        private static Vector3 SampleWorld(Spline spline, Matrix4x4 l2w, float t)
        {
            float3 local = spline.EvaluatePosition(Mathf.Clamp01(t));
            return l2w.MultiplyPoint3x4(new Vector3(local.x, local.y, local.z));
        }

        /// <summary>
        /// Read embedded spline data at t, falling back to a constant when the
        /// channel is empty. An unauthored width channel meaning "the default
        /// everywhere" is the common case — the alternative, forcing a keyframe at
        /// both ends before a road can be baked, would be tedious for no gain.
        /// </summary>
        private static float Evaluate(SplineData<float> data, Spline spline, float t, float fallback)
        {
            if (data == null || data.Count == 0) return fallback;
            return data.Evaluate(spline, Mathf.Clamp01(t), PathIndexUnit.Normalized,
                new UnityEngine.Splines.Interpolators.LerpFloat());
        }
    }
}
