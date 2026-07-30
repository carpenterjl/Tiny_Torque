using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Signed curvature along a polyline path.
    ///
    /// <b>This is a deliberate copy of <c>BotDriver</c>'s constructor maths
    /// (BotDriver.cs, the _kappa block), not a refactor of it.</b> Bot behaviour is
    /// load-bearing — lap times, race balance and difficulty tiers are all tuned
    /// around the line bots currently drive — so the racing-line tools were built
    /// with BotDriver untouched. Sharing the code would have meant editing it.
    /// The cost of the duplication is that the two can drift, which is why
    /// TrackStudioValidator checks them against each other numerically on a
    /// reference path rather than trusting this comment.
    ///
    /// Sign convention: <b>positive is a right turn</b>, matching
    /// <c>right = Cross(up, tangent)</c>, so a positive lateral offset moves toward
    /// the right edge — the inside of a right-hander.
    /// </summary>
    public static class PathCurvature
    {
        /// <summary>
        /// Per-node signed curvature in rad/m, box-smoothed over ±<paramref
        /// name="smoothHalfWindow"/> nodes. Raw per-node turn angles on dense
        /// spline samples are noisy enough to produce phantom apexes, which is what
        /// the smoothing is for.
        /// </summary>
        public static float[] Signed(IReadOnlyList<Vector3> path, bool closed,
            int smoothHalfWindow = 2)
        {
            int n = path != null ? path.Count : 0;
            var kappa = new float[n];
            if (n < 3) return kappa;
            bool wrap = closed && n >= 3;

            var raw = new float[n];
            for (int i = 0; i < n; i++)
            {
                int prev = wrap ? (i + n - 1) % n : Mathf.Max(0, i - 1);
                int next = wrap ? (i + 1) % n : Mathf.Min(n - 1, i + 1);
                if (prev == i || next == i) continue;   // open endpoints, patched below
                Vector3 a = Flat(path[i] - path[prev]);
                Vector3 b = Flat(path[next] - path[i]);
                if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f) continue;
                raw[i] = Vector3.SignedAngle(a, b, Vector3.up) * Mathf.Deg2Rad /
                    Mathf.Max(0.05f, 0.5f * (a.magnitude + b.magnitude));
            }
            if (!wrap && n >= 3) { raw[0] = raw[1]; raw[n - 1] = raw[n - 2]; }

            int h = Mathf.Max(0, smoothHalfWindow);
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;
                for (int j = -h; j <= h; j++)
                {
                    int k = wrap ? (i + j + n) % n : i + j;
                    if (k < 0 || k >= n) continue;
                    sum += raw[k];
                    count++;
                }
                kappa[i] = count > 0 ? sum / count : 0f;
            }
            return kappa;
        }

        /// <summary>Cumulative arc length at each node. The final closing segment
        /// of a loop is NOT included here — see <see cref="TotalLength"/>.</summary>
        public static float[] Cumulative(IReadOnlyList<Vector3> path)
        {
            int n = path != null ? path.Count : 0;
            var cum = new float[n];
            for (int i = 1; i < n; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            return cum;
        }

        /// <summary>Total path length, including the closing segment on a loop.</summary>
        public static float TotalLength(IReadOnlyList<Vector3> path, bool closed)
        {
            int n = path != null ? path.Count : 0;
            if (n < 2) return 0f;
            var cum = Cumulative(path);
            return cum[n - 1] + (closed ? Vector3.Distance(path[n - 1], path[0]) : 0f);
        }

        /// <summary>Per-node unit right vector (Cross(up, tangent)), the axis a
        /// lateral offset moves along.</summary>
        public static Vector3[] RightVectors(IReadOnlyList<Vector3> path, bool closed)
        {
            int n = path != null ? path.Count : 0;
            var right = new Vector3[n];
            if (n < 2) return right;
            for (int i = 0; i < n; i++)
            {
                int prev = closed ? (i + n - 1) % n : Mathf.Max(0, i - 1);
                int next = closed ? (i + 1) % n : Mathf.Min(n - 1, i + 1);
                Vector3 tan = Flat(path[next] - path[prev]);
                if (tan.sqrMagnitude < 1e-8f) tan = Vector3.forward;
                right[i] = Vector3.Cross(Vector3.up, tan.normalized).normalized;
            }
            return right;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }
    }
}
