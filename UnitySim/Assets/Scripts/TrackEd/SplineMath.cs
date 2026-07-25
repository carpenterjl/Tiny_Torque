using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Catmull-Rom evaluation and ribbon sampling for <see cref="SplineSpec"/>.
    /// Open splines clamp their end tangents (duplicated neighbors); closed
    /// splines wrap. Frames come from parallel transport (no sudden flips) with
    /// the user's per-point roll applied on top, and a closure correction so a
    /// closed loop's seam has continuous roll.
    /// </summary>
    public static class SplineMath
    {
        /// <summary>One ribbon cross-section sample along the curve.</summary>
        public struct Sample
        {
            public Vector3 pos;
            public Vector3 tan;     // unit tangent
            public float roll;      // interpolated bank (deg)
            public float width;     // interpolated ribbon width (m)
            public float dist;      // cumulative arc length (m)
            public int surfaceType; // owning segment's surface (not interpolated)
            public int pointIndex;  // owning segment's first control point
        }

        public static int SegmentCount(SplineSpec s) =>
            s.Count < 2 ? 0 : (s.closed ? s.Count : s.Count - 1);

        private static Vector3 P(SplineSpec s, int i)
        {
            int n = s.Count;
            if (s.closed) return s.points[((i % n) + n) % n];
            return s.points[Mathf.Clamp(i, 0, n - 1)];
        }

        /// <summary>Catmull-Rom position on segment seg at t in [0,1].</summary>
        public static Vector3 Position(SplineSpec s, int seg, float t)
        {
            Vector3 p0 = P(s, seg - 1), p1 = P(s, seg), p2 = P(s, seg + 1), p3 = P(s, seg + 2);
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        /// <summary>Catmull-Rom derivative (unnormalized tangent).</summary>
        public static Vector3 Tangent(SplineSpec s, int seg, float t)
        {
            Vector3 p0 = P(s, seg - 1), p1 = P(s, seg), p2 = P(s, seg + 1), p3 = P(s, seg + 2);
            float t2 = t * t;
            return 0.5f * ((-p0 + p2) +
                           2f * (2f * p0 - 5f * p1 + 4f * p2 - p3) * t +
                           3f * (-p0 + 3f * p1 - 3f * p2 + p3) * t2);
        }

        private static float Scalar(IReadOnlyList<float> vals, SplineSpec s, int seg, float t)
        {
            int n = s.Count;
            int a = s.closed ? ((seg % n) + n) % n : Mathf.Clamp(seg, 0, n - 1);
            int b = s.closed ? (a + 1) % n : Mathf.Clamp(seg + 1, 0, n - 1);
            return Mathf.Lerp(vals[a], vals[b], t);
        }

        /// <summary>
        /// Sample the whole spline at roughly <paramref name="step"/> meters.
        /// Closed splines do NOT emit a duplicate seam sample — the mesh builder
        /// wraps indices so the seam ring is shared exactly.
        /// </summary>
        public static List<Sample> SampleAll(SplineSpec s, float step = 0.4f)
        {
            var outSamples = new List<Sample>();
            int segs = SegmentCount(s);
            if (segs == 0) return outSamples;
            s.EnsureArrays();

            float dist = 0f;
            Vector3 prev = Position(s, 0, 0f);
            for (int seg = 0; seg < segs; seg++)
            {
                // Estimate segment length with a coarse polyline.
                float len = 0f;
                Vector3 e = Position(s, seg, 0f);
                for (int k = 1; k <= 8; k++)
                {
                    Vector3 q = Position(s, seg, k / 8f);
                    len += Vector3.Distance(e, q);
                    e = q;
                }
                int count = Mathf.Max(2, Mathf.CeilToInt(len / Mathf.Max(0.25f, step)) + 1);

                // Emit [0,1) for every segment; the final open-spline sample is
                // appended after the loop so interior seams aren't duplicated.
                for (int k = 0; k < count - 1; k++)
                {
                    float t = (float)k / (count - 1);
                    AppendSample(s, seg, t, ref dist, ref prev, outSamples);
                }
            }

            if (!s.closed)
            {
                float endDist = dist;
                Vector3 endPrev = prev;
                AppendSample(s, segs - 1, 1f, ref endDist, ref endPrev, outSamples);
            }
            return outSamples;
        }

        private static void AppendSample(SplineSpec s, int seg, float t,
            ref float dist, ref Vector3 prev, List<Sample> list)
        {
            Vector3 p = Position(s, seg, t);
            if (list.Count > 0) dist += Vector3.Distance(prev, p);
            prev = p;

            Vector3 tan = Tangent(s, seg, t);
            if (tan.sqrMagnitude < 1e-8f) tan = Vector3.forward;
            int n = s.Count;
            int owner = s.closed ? ((seg % n) + n) % n : Mathf.Clamp(seg, 0, n - 1);
            list.Add(new Sample
            {
                pos = p,
                tan = tan.normalized,
                roll = Scalar(s.rollDeg, s, seg, t),
                width = Mathf.Max(0.3f, Scalar(s.widths, s, seg, t)),
                dist = dist,
                surfaceType = Mathf.Clamp(s.surface[owner], 0, TrackCatalog.Floors.Length - 1),
                pointIndex = owner,
            });
        }

        /// <summary>
        /// Parallel-transport frames along the samples, then apply per-sample
        /// roll. For closed loops the transported end frame generally mismatches
        /// the start frame; the signed seam angle is distributed as corrective
        /// roll along the length so the seam is continuous.
        /// </summary>
        public static void ComputeFrames(List<Sample> samples, bool closed,
            out Vector3[] right, out Vector3[] up)
        {
            int n = samples.Count;
            right = new Vector3[n];
            up = new Vector3[n];
            if (n == 0) return;

            // Initial frame: right = tangent x up-guess.
            Vector3 t0 = samples[0].tan;
            Vector3 upGuess = Mathf.Abs(Vector3.Dot(t0, Vector3.up)) > 0.98f ? Vector3.forward : Vector3.up;
            Vector3 r = Vector3.Cross(upGuess, t0).normalized;
            if (r.sqrMagnitude < 1e-8f) r = Vector3.right;
            // Make the INITIAL frame's up face skyward; transport then preserves
            // continuity for the whole curve (never flip mid-ribbon).
            if (Vector3.Cross(r, t0).y < 0f) r = -r;
            var transported = new Vector3[n];
            transported[0] = r;

            // Transport: rotate the previous right by the minimal rotation
            // between consecutive tangents.
            for (int i = 1; i < n; i++)
            {
                Quaternion q = Quaternion.FromToRotation(samples[i - 1].tan, samples[i].tan);
                transported[i] = (q * transported[i - 1]).normalized;
            }

            // Closed-loop closure: measure the seam mismatch (transport once more
            // back to the first tangent) and unwind it linearly along the curve.
            float closure = 0f;
            float total = Mathf.Max(0.001f, samples[n - 1].dist);
            if (closed && n > 2)
            {
                Quaternion q = Quaternion.FromToRotation(samples[n - 1].tan, samples[0].tan);
                Vector3 rEnd = (q * transported[n - 1]).normalized;
                closure = Vector3.SignedAngle(transported[0], rEnd, samples[0].tan);
            }

            for (int i = 0; i < n; i++)
            {
                float corr = closed ? -closure * (samples[i].dist / total) : 0f;
                float roll = samples[i].roll + corr;
                Quaternion rollQ = Quaternion.AngleAxis(roll, samples[i].tan);
                right[i] = (rollQ * transported[i]).normalized;
                up[i] = Vector3.Cross(right[i], samples[i].tan).normalized;
            }
        }
    }
}
