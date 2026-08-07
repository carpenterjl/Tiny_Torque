using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The two pieces of arithmetic behind a sculpt stroke: how far a vertex
    /// moves for its distance from the brush, and which vertices a brush touches.
    ///
    /// <b>Separate from <see cref="DeformableBody"/> on purpose.</b> Everything
    /// here is a pure function of arrays — no mesh, no GameObject, no frame — so
    /// <c>[BDEF]</c> can check the falloff shape and the gathering against a
    /// synthetic grid rather than against whatever a body happens to look like.
    /// A brush that quietly stopped welding, or a falloff that stopped reaching
    /// zero at the rim, would otherwise only show up as a torn car.
    /// </summary>
    public static class DeformFalloff
    {
        /// <summary>
        /// Smooth distance decay: 1 at the brush centre, 0 at the rim, with zero
        /// gradient at both ends.
        ///
        /// The smoothstep matters more than it looks. A linear falloff leaves a
        /// visible crease at the rim — the surface has a kink exactly where the
        /// weight stops changing — and a Gaussian never actually reaches zero, so
        /// every stroke moves the whole car by a little. This reaches zero, and
        /// arrives there flat.
        /// </summary>
        public static float Weight(float distance, float radius)
        {
            if (radius <= 0f) return distance <= 0f ? 1f : 0f;
            float s = 1f - Mathf.Clamp01(distance / radius);
            return s * s * (3f - 2f * s);
        }

        /// <summary>
        /// Every vertex inside the brush, with its weight. Plain — no welding —
        /// which is what the bench measures against a grid.
        /// </summary>
        public static int GatherIndices(Vector3[] verts, Vector3 center, float radius,
                                        List<int> outIdx, List<float> outW)
        {
            outIdx.Clear();
            outW.Clear();
            if (verts == null || radius <= 0f) return 0;

            float r2 = radius * radius;
            for (int i = 0; i < verts.Length; i++)
            {
                float d2 = (verts[i] - center).sqrMagnitude;
                if (d2 > r2) continue;
                outIdx.Add(i);
                outW.Add(Weight(Mathf.Sqrt(d2), radius));
            }
            return outIdx.Count;
        }

        /// <summary>
        /// How close two vertices have to be to count as the same point, in the
        /// mesh's own author units. 0.1 mm on a 0.42 m shell: far below any
        /// feature, far above the float error a rigid transform introduces.
        /// </summary>
        public const float WeldQuantum = 1e-4f;

        /// <summary>
        /// Group vertices that sit at the same position.
        ///
        /// <b>Why any of this is needed.</b> An FBX shell duplicates a vertex
        /// wherever the normal or the UV has to break — every hard edge, every
        /// material seam. Those copies are one point to the eye and several
        /// independent points to <c>mesh.vertices</c>, so a brush that moved only
        /// the copy it found tears the panel open along the seam it was pulling
        /// near. Grouping by position and moving the whole group is what keeps a
        /// sculpted body watertight.
        ///
        /// Quantised rather than compared pairwise: this runs once per body over
        /// several thousand vertices, and a hash is the difference between
        /// instant and a visible stall.
        /// </summary>
        public static void BuildWeldMap(Vector3[] verts, float quantum,
                                        out int[] groupOf, out List<int>[] members)
        {
            int n = verts != null ? verts.Length : 0;
            groupOf = new int[n];
            if (n == 0) { members = new List<int>[0]; return; }

            float inv = 1f / Mathf.Max(1e-9f, quantum);
            var map = new Dictionary<Vector3Int, int>(n);
            var lists = new List<List<int>>(n);

            for (int i = 0; i < n; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(verts[i].x * inv),
                                         Mathf.RoundToInt(verts[i].y * inv),
                                         Mathf.RoundToInt(verts[i].z * inv));
                if (!map.TryGetValue(key, out int g))
                {
                    g = lists.Count;
                    map.Add(key, g);
                    lists.Add(new List<int>(2));
                }
                groupOf[i] = g;
                lists[g].Add(i);
            }

            members = lists.ToArray();
        }

        /// <summary>
        /// Every vertex the brush touches, welded: a group is in or out as a
        /// whole, and the weight it gets is the one its CLOSEST member earned.
        ///
        /// Closest rather than averaged, because the members are the same point —
        /// any spread between their distances is float noise, and taking the
        /// minimum is the answer that does not depend on which copy the exporter
        /// happened to write first.
        /// </summary>
        public static int GatherWelded(Vector3[] verts, Vector3 center, float radius,
                                       int[] groupOf, List<int>[] members,
                                       List<int> outIdx, List<float> outW)
        {
            outIdx.Clear();
            outW.Clear();
            if (verts == null || members == null || groupOf == null || radius <= 0f) return 0;

            var best = new float[members.Length];
            for (int g = 0; g < best.Length; g++) best[g] = float.MaxValue;

            float r2 = radius * radius;
            bool any = false;
            for (int i = 0; i < verts.Length; i++)
            {
                float d2 = (verts[i] - center).sqrMagnitude;
                if (d2 > r2) continue;
                int g = groupOf[i];
                if (d2 < best[g]) { best[g] = d2; any = true; }
            }
            if (!any) return 0;

            for (int g = 0; g < members.Length; g++)
            {
                if (best[g] == float.MaxValue) continue;
                float w = Weight(Mathf.Sqrt(best[g]), radius);
                List<int> m = members[g];
                for (int k = 0; k < m.Count; k++)
                {
                    outIdx.Add(m[k]);
                    outW.Add(w);
                }
            }
            return outIdx.Count;
        }
    }
}
