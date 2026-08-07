using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Everything the crash frame derives rather than stores, in one place —
    /// generation, sentinel resolution, sanitation, rest lengths, vertex binding
    /// and pick math. Pure statics over plain arrays, no scene objects, so every
    /// line of it is benchable in edit mode and the editor and the driving path
    /// cannot disagree about what a stored lattice means.
    ///
    /// <b>Generation is deterministic by construction</b>: no randomness, cell
    /// keys sorted before emission, corner indices assigned in that sorted order.
    /// The same mesh at the same spacing is the same lattice, bit for bit —
    /// which is what lets LAN peers rebuild identical frames from the design
    /// JSON and replay the same hits into them.
    ///
    /// <b>The sentinels are interpreted here and only here.</b> A stored 0 mass,
    /// 0 spring, −1 ζ or −1 break strain means "derive the default"; generation
    /// writes resolved values explicitly, so the sentinels exist for hand-edited
    /// JSON. Five call sites each interpreting them would drift apart.
    /// </summary>
    public static class LatticeBuilder
    {
        // ---- constants the defaults come from ------------------------------------

        /// <summary>Mass of the shell the frame wraps, split equally over the
        /// nodes. ~a third of the 1.6 kg car — the body, not the chassis.</summary>
        public const float ShellMassKg = 0.5f;

        /// <summary>Per-node aggregate natural frequency the default springs are
        /// pinned to. Well under the 400 Hz driving step's ~127 Hz stability
        /// ceiling (ω·dt &lt; 2), leaving the runtime's substep loop headroom for
        /// the editor's 10× spring slider.</summary>
        public const float TargetNodeHz = 40f;

        public const float DefaultDampingRatio = 0.35f;
        public const float DefaultBreakStrain = 0.35f;

        /// <summary>Grid pitch as a fraction of body length, at the two ends of
        /// the fidelity slider. 0.42 m car: 67 mm coarse, 32 mm fine. The fine
        /// end is set by the beam budget, not by taste — a shell's surface at
        /// 0.05·length wants ~10 000 beams, which no 400 Hz step should carry;
        /// at 0.075 the whole slider range generates rather than refusing at
        /// the top.</summary>
        public const float SpacingMaxPerLength = 0.16f;
        public const float SpacingMinPerLength = 0.075f;

        /// <summary>Surface samples per cell pitch — the raster the adaptive
        /// generator reads curvature from and builds adjacency over. 8 gives
        /// two samples across even the finest (pitch/4) cell.</summary>
        public const int SampleSubdiv = 8;

        /// <summary>Normal spread (1 − |mean unit normal|) above which a cell
        /// subdivides. ~0.05 is ≈50° of normal variation inside one cell: a
        /// flat panel is exactly 0, a sculpted body's gentle curvature stays
        /// under it, a 90° corner (spread ≈ 0.3) is well over.</summary>
        public const float CurveSpread = 0.05f;

        /// <summary>How many times a curved cell may halve: pitch, /2, /4.</summary>
        public const int SubdivLevels = 2;

        /// <summary>Refusal guard on the raster itself — a pitch small enough
        /// to want this many samples is a pitch the beam cap was always going
        /// to refuse, so refuse before walking a million points.</summary>
        public const int MaxSamples = 2_000_000;

        /// <summary>Nodes closer than this fraction of the pitch merge. Two
        /// adjacent fine cells clipping the same corner can put centroids a
        /// couple of millimetres apart, and a 2 mm beam is a hair trigger —
        /// sub-millimetre motion reads as double-digit strain, so a parking tap
        /// would yield it. Merging removes the beams no strain threshold could
        /// ever be right for.</summary>
        public const float MergeMinPerSpacing = 0.09f;

        /// <summary>The FRAME tab's damage slider (0..1, default 0.5) → the
        /// multiplier the solver scales its hit response by. Log so "half the
        /// damage" and "twice the damage" sit the same distance from centre:
        /// 0 → 0.1×, 0.5 → 1×, 1 → 10×.</summary>
        public static float DamageScale(float damage01) =>
            Mathf.Pow(10f, 2f * Mathf.Clamp01(damage01) - 1f);

        /// <summary>Generation refuses a lattice past this many beams — the
        /// runtime solver's budget (≈100–200 µs per awake step at ~2000 beams,
        /// bounded here rather than discovered on the track).</summary>
        public const int MaxBeams = 6000;

        /// <summary>Vertex binding reaches this many grid pitches from a vertex
        /// before giving up; the nearest node is bound unconditionally.</summary>
        public const float BindReachPitches = 2f;

        // ---- fidelity ------------------------------------------------------------

        /// <summary>Slider position → grid pitch in metres. Right = finer.</summary>
        public static float SpacingFor(float fidelity01, float bodyLengthM) =>
            Mathf.Lerp(SpacingMaxPerLength, SpacingMinPerLength,
                       Mathf.Clamp01(fidelity01)) * Mathf.Max(bodyLengthM, 0.01f);

        // ---- generation ----------------------------------------------------------

        /// <summary>
        /// Wrap a triangle soup (metres, vehicle frame) in a lattice whose base
        /// cell pitch is <paramref name="spacing"/>, adapting to the surface:
        /// <b>flat panels get large cells, curves and corners get small ones.</b>
        ///
        /// Every triangle is rasterised into surface samples (pitch/8 apart, so
        /// a panel with no interior vertices still contributes everywhere).
        /// Cells are axis-aligned multiples of the pitch — <b>x = 0 stays a
        /// cell-corner plane, so a symmetric body gets a symmetric frame</b> —
        /// and a cell whose samples' normals spread more than
        /// <see cref="CurveSpread"/> halves, up to <see cref="SubdivLevels"/>
        /// times. A node is the CENTROID of its cell's samples, which is what
        /// puts every node ON the surface instead of on a grid corner floating
        /// off it. Beams come from sample adjacency along the surface — two
        /// cells joined wherever their samples touch — so force spreads the way
        /// the shell is actually connected; separate mesh islands are then
        /// bridged by their closest node pair so nothing is left without a load
        /// path.
        ///
        /// Deterministic by construction: fixed triangle/raster order, cell
        /// coordinates derived once at the finest level, no hash-order
        /// iteration anywhere the output can see.
        ///
        /// False when the result would exceed <see cref="MaxBeams"/> or
        /// <see cref="MaxSamples"/> (outputs are then empty) or the inputs are
        /// degenerate.
        /// </summary>
        public static bool Generate(Vector3[] verticesM, int[] triangles, float spacing,
                                    out LatticeNode[] nodes, out LatticeBeam[] beams)
        {
            nodes = System.Array.Empty<LatticeNode>();
            beams = System.Array.Empty<LatticeBeam>();
            if (verticesM == null || triangles == null || triangles.Length < 3) return false;
            if (spacing < 1e-4f) return false;

            float samplePitch = spacing / SampleSubdiv;
            float hFine = spacing / (1 << SubdivLevels);   // the finest cell pitch

            // -- refusal before the work: a pitch that wants millions of
            //    samples is one the beam cap would refuse anyway --
            long estimate = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int div = RasterDiv(verticesM[triangles[t]], verticesM[triangles[t + 1]],
                                    verticesM[triangles[t + 2]], samplePitch);
                estimate += (long)(div + 1) * (div + 2) / 2;
                if (estimate > MaxSamples) return false;
            }

            // -- pass 1: per-cell normal statistics at every level. Cell coords
            //    are computed ONCE at the finest level and shifted up (>> is a
            //    floor divide), so a sample can never disagree with itself about
            //    which coarse cell it is in near a boundary. --
            var stats = new Dictionary<long, (Vector3 nSum, int count)>[SubdivLevels + 1];
            for (int l = 0; l <= SubdivLevels; l++)
                stats[l] = new Dictionary<long, (Vector3, int)>();

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = verticesM[triangles[t]];
                Vector3 b = verticesM[triangles[t + 1]];
                Vector3 c = verticesM[triangles[t + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-14f) continue;
                n.Normalize();

                int div = RasterDiv(a, b, c, samplePitch);
                for (int i = 0; i <= div; i++)
                for (int j = 0; j <= div - i; j++)
                {
                    Vector3 p = a + (b - a) * (i / (float)div) + (c - a) * (j / (float)div);
                    FineCoords(p, hFine, out int fx, out int fy, out int fz);
                    for (int l = SubdivLevels; l >= 0; l--)
                    {
                        int shift = SubdivLevels - l;
                        long key = Pack(fx >> shift, fy >> shift, fz >> shift);
                        stats[l].TryGetValue(key, out var s);
                        stats[l][key] = (s.nSum + n, s.count + 1);
                    }
                }
            }
            if (stats[0].Count == 0) return false;

            // -- leaf decision: a cell is a leaf when its normals agree (flat
            //    enough) or it has no more levels to halve into --
            var leafAt = new HashSet<long>[SubdivLevels + 1];
            for (int l = 0; l <= SubdivLevels; l++) leafAt[l] = new HashSet<long>();
            foreach (var kv in stats[0])
                if (Spread(kv.Value) <= CurveSpread) leafAt[0].Add(kv.Key);
            for (int l = 1; l < SubdivLevels; l++)
                foreach (var kv in stats[l])
                {
                    Unpack(kv.Key, out int x, out int y, out int z);
                    bool parentLeaf = false;
                    for (int pl = l - 1; pl >= 0 && !parentLeaf; pl--)
                        parentLeaf = leafAt[pl].Contains(Pack(x >> (l - pl), y >> (l - pl),
                                                              z >> (l - pl)));
                    if (!parentLeaf && Spread(kv.Value) <= CurveSpread) leafAt[l].Add(kv.Key);
                }
            // The finest level is the unconditional fallback — no set needed.

            // -- pass 2: nodes are leaf-sample centroids; beams are raster
            //    adjacency between different leaves. Node indices go in
            //    encounter order, which is fixed by the triangle order. --
            var nodeId = new Dictionary<(int level, long key), int>();
            var posSum = new List<Vector3>();
            var posCount = new List<int>();
            var beamSet = new HashSet<long>();
            var beamList = new List<(int a, int b)>();
            int[][] ids = null;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = verticesM[triangles[t]];
                Vector3 b = verticesM[triangles[t + 1]];
                Vector3 c = verticesM[triangles[t + 2]];
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-14f) continue;

                int div = RasterDiv(a, b, c, samplePitch);
                if (ids == null || ids.Length < div + 1) ids = new int[div + 1][];
                for (int i = 0; i <= div; i++)
                {
                    if (ids[i] == null || ids[i].Length < div + 1 - i)
                        ids[i] = new int[div + 1 - i];
                    for (int j = 0; j <= div - i; j++)
                    {
                        Vector3 p = a + (b - a) * (i / (float)div) + (c - a) * (j / (float)div);
                        FineCoords(p, hFine, out int fx, out int fy, out int fz);
                        int level = SubdivLevels;
                        long key = Pack(fx, fy, fz);
                        for (int l = 0; l < SubdivLevels; l++)
                        {
                            int shift = SubdivLevels - l;
                            long k = Pack(fx >> shift, fy >> shift, fz >> shift);
                            if (leafAt[l].Contains(k)) { level = l; key = k; break; }
                        }
                        if (!nodeId.TryGetValue((level, key), out int id))
                        {
                            id = posSum.Count;
                            nodeId[(level, key)] = id;
                            posSum.Add(Vector3.zero);
                            posCount.Add(0);
                        }
                        posSum[id] += p;
                        posCount[id]++;
                        ids[i][j] = id;
                    }
                }

                // Raster neighbours — right, up, and the diagonal that closes
                // each little triangle — become beams where the leaf changes.
                for (int i = 0; i <= div; i++)
                for (int j = 0; j <= div - i; j++)
                {
                    int id = ids[i][j];
                    if (i + 1 + j <= div) Link(beamSet, beamList, id, ids[i + 1][j]);
                    if (i + j + 1 <= div) Link(beamSet, beamList, id, ids[i][j + 1]);
                    if (j > 0 && i + 1 + j - 1 <= div) Link(beamSet, beamList, id, ids[i + 1][j - 1]);
                }
            }

            int nCount = posSum.Count;
            if (nCount < 2) return false;
            var positions = new Vector3[nCount];
            for (int i = 0; i < nCount; i++) positions[i] = posSum[i] / posCount[i];

            Symmetrize(nodeId, positions);
            MergeClose(ref positions, beamSet, beamList, spacing * MergeMinPerSpacing);
            BridgeIslands(positions, beamSet, beamList);
            if (beamList.Count > MaxBeams) return false;
            beamList.Sort();
            nCount = positions.Length;

            // -- defaults, written explicitly so the file is self-contained --
            float mass = ShellMassKg / nCount;
            float nAvg = 2f * beamList.Count / nCount;
            float k0 = DefaultSpring(mass, nAvg);

            nodes = new LatticeNode[nCount];
            for (int i = 0; i < nCount; i++)
                nodes[i] = new LatticeNode { localPos = positions[i], mass = mass };

            beams = new LatticeBeam[beamList.Count];
            for (int i = 0; i < beamList.Count; i++)
                beams[i] = new LatticeBeam
                {
                    a = beamList[i].a,
                    b = beamList[i].b,
                    spring = k0,
                    dampingRatio = DefaultDampingRatio,
                    breakStrain = DefaultBreakStrain,
                };
            return true;
        }

        /// <summary>Raster resolution for one triangle: enough steps that
        /// samples sit ≤ <c>samplePitch</c> apart along its longest edge.</summary>
        private static int RasterDiv(Vector3 a, Vector3 b, Vector3 c, float samplePitch)
        {
            float e = Mathf.Max((b - a).magnitude, Mathf.Max((c - b).magnitude,
                                                             (a - c).magnitude));
            return Mathf.Clamp(Mathf.CeilToInt(e / samplePitch), 1, 512);
        }

        /// <summary>1 − |mean unit normal| — 0 for a flat cell, → 1 as normals
        /// scatter.</summary>
        private static float Spread((Vector3 nSum, int count) s) =>
            1f - s.nSum.magnitude / s.count;

        private static void Link(HashSet<long> set, List<(int a, int b)> list, int a, int b)
        {
            if (a == b) return;
            int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
            long key = ((long)lo << 32) | (uint)hi;
            if (set.Add(key)) list.Add((lo, hi));
        }

        /// <summary>
        /// Centroids are honest about the surface but not about the mirror: two
        /// triangulations of the same square (a box face and its x-mirror split
        /// along the other diagonal) sample it at different points, so mirrored
        /// cells' centroids differ by millimetres. The CELLS mirror exactly,
        /// though — x = 0 is a cell plane at every level, so cell x mirrors to
        /// −x−1 — which makes symmetry a pairing by key, not a search: each
        /// mirrored pair is averaged to exactly mirrored positions. Pairs are
        /// processed once (lower id leads), so hash iteration order cannot
        /// reach the output; nodes without a mirrored cell (an asymmetric body)
        /// are left exactly where their samples put them.
        /// </summary>
        private static void Symmetrize(Dictionary<(int level, long key), int> nodeId,
                                       Vector3[] positions)
        {
            foreach (var kv in nodeId)
            {
                Unpack(kv.Key.key, out int x, out int y, out int z);
                if (!nodeId.TryGetValue((kv.Key.level, Pack(-x - 1, y, z)), out int mid)
                    || mid <= kv.Value) continue;
                int id = kv.Value;
                Vector3 m = positions[mid];
                Vector3 sym = 0.5f * (positions[id] + new Vector3(-m.x, m.y, m.z));
                positions[id] = sym;
                positions[mid] = new Vector3(-sym.x, sym.y, sym.z);
            }
        }

        /// <summary>
        /// Collapse node clusters tighter than <paramref name="minLen"/> —
        /// see <see cref="MergeMinPerSpacing"/> for why they cannot stay.
        /// Union-find in index order, cluster position = member centroid, beams
        /// remapped with degenerates and duplicates dropped. Runs AFTER
        /// <see cref="Symmetrize"/>: mirrored clusters hold exactly mirrored
        /// members, so their centroids mirror too, and a cluster straddling
        /// x = 0 lands exactly on it.
        /// </summary>
        private static void MergeClose(ref Vector3[] positions, HashSet<long> beamSet,
                                       List<(int a, int b)> beamList, float minLen)
        {
            int n = positions.Length;
            float min2 = minLen * minLen;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int i)
            {
                while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
                return i;
            }

            bool any = false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if ((positions[i] - positions[j]).sqrMagnitude >= min2) continue;
                    int ri = Find(i), rj = Find(j);
                    if (ri == rj) continue;
                    parent[Mathf.Max(ri, rj)] = Mathf.Min(ri, rj);
                    any = true;
                }
            if (!any) return;

            // Dense reindex in root order, centroid per cluster.
            var remap = new int[n];
            var sum = new List<Vector3>();
            var count = new List<int>();
            for (int i = 0; i < n; i++)
                if (Find(i) == i) { remap[i] = sum.Count; sum.Add(Vector3.zero); count.Add(0); }
            for (int i = 0; i < n; i++)
            {
                int m = remap[Find(i)];
                remap[i] = m;
                sum[m] += positions[i];
                count[m]++;
            }
            var merged = new Vector3[sum.Count];
            for (int i = 0; i < merged.Length; i++) merged[i] = sum[i] / count[i];
            positions = merged;

            var oldBeams = new List<(int a, int b)>(beamList);
            beamList.Clear();
            beamSet.Clear();
            foreach ((int a, int b) in oldBeams) Link(beamSet, beamList, remap[a], remap[b]);
        }

        /// <summary>
        /// A body can be several mesh islands (light clusters, mirrors); each
        /// gets one bridging beam to the closest already-connected node, so no
        /// piece is left without a load path. Closest-pair scan in index order —
        /// deterministic, and N is small enough that O(N²) is nothing.
        /// </summary>
        private static void BridgeIslands(Vector3[] positions, HashSet<long> beamSet,
                                          List<(int a, int b)> beamList)
        {
            int n = positions.Length;
            var component = new int[n];
            for (int i = 0; i < n; i++) component[i] = i;
            // Union-find over the beams, path-halving.
            int Find(int i)
            {
                while (component[i] != i)
                { component[i] = component[component[i]]; i = component[i]; }
                return i;
            }
            foreach ((int a, int b) in beamList)
                component[Find(a)] = Find(b);

            while (true)
            {
                int main = Find(0);
                int bestA = -1, bestB = -1;
                float bestD = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (Find(i) != main) continue;
                    for (int j = 0; j < n; j++)
                    {
                        if (Find(j) == main) continue;
                        float d = (positions[i] - positions[j]).sqrMagnitude;
                        if (d < bestD) { bestD = d; bestA = i; bestB = j; }
                    }
                }
                if (bestB < 0) return;   // one component — done
                Link(beamSet, beamList, bestA, bestB);
                component[Find(bestB)] = main;
            }
        }

        private static void FineCoords(Vector3 p, float hFine, out int x, out int y, out int z)
        {
            x = Mathf.FloorToInt(p.x / hFine);
            y = Mathf.FloorToInt(p.y / hFine);
            z = Mathf.FloorToInt(p.z / hFine);
        }

        private static long Pack(int x, int y, int z) =>
            ((long)(x + 1_000_000) << 42) | ((long)(y + 1_000_000) << 21) | (uint)(z + 1_000_000) & 0x1FFFFF;

        private static void Unpack(long key, out int x, out int y, out int z)
        {
            x = (int)(key >> 42) - 1_000_000;
            y = (int)((key >> 21) & 0x1FFFFF) - 1_000_000;
            z = (int)(key & 0x1FFFFF) - 1_000_000;
        }

        // ---- sanitation and sentinel resolution ----------------------------------

        /// <summary>Drop beams a loaded file cannot mean — out-of-range or
        /// self-referential indices, duplicate pairs. Returns how many were
        /// dropped, for the load log. Never throws: a hand-edited JSON is a
        /// document, not an attack.</summary>
        public static int Sanitize(int nodeCount, ref LatticeBeam[] beams)
        {
            if (beams == null) { beams = System.Array.Empty<LatticeBeam>(); return 0; }
            var seen = new HashSet<long>();
            var keep = new List<LatticeBeam>(beams.Length);
            foreach (LatticeBeam beam in beams)
            {
                if (beam == null) continue;
                if (beam.a < 0 || beam.b < 0 || beam.a >= nodeCount || beam.b >= nodeCount) continue;
                if (beam.a == beam.b) continue;
                int lo = Mathf.Min(beam.a, beam.b), hi = Mathf.Max(beam.a, beam.b);
                if (!seen.Add(((long)lo << 32) | (uint)hi)) continue;
                keep.Add(beam);
            }
            int dropped = beams.Length - keep.Count;
            if (dropped > 0) beams = keep.ToArray();
            return dropped;
        }

        public static float RestLength(IReadOnlyList<LatticeNode> nodes, LatticeBeam beam) =>
            (nodes[beam.a].localPos - nodes[beam.b].localPos).magnitude;

        /// <summary>0 stored ⇒ an equal share of the shell mass.</summary>
        public static float ResolveMass(float stored, int nodeCount) =>
            stored > 0f ? stored : ShellMassKg / Mathf.Max(nodeCount, 1);

        /// <summary>The default spring pins each node's aggregate natural
        /// frequency at <see cref="TargetNodeHz"/>: a node of mass m held by
        /// n_avg springs of rate k has ω² = n_avg·k/m.</summary>
        public static float DefaultSpring(float nodeMass, float beamsPerNode)
        {
            float w = 2f * Mathf.PI * TargetNodeHz;
            return nodeMass * w * w / Mathf.Max(beamsPerNode, 1f);
        }

        public static float ResolveSpring(float stored, float nodeMass, float beamsPerNode) =>
            stored > 0f ? stored : DefaultSpring(nodeMass, beamsPerNode);

        public static float ResolveDampingRatio(float stored) =>
            stored >= 0f ? stored : DefaultDampingRatio;

        public static float ResolveBreakStrain(float stored) =>
            stored > 0f ? stored : DefaultBreakStrain;

        // ---- vertex binding ------------------------------------------------------

        /// <summary>One mesh vertex tied to up to three nodes. Sparse — a vertex
        /// with no binding never moves, and never costs anything either.</summary>
        public struct VertexBinding
        {
            public int vertex;
            /// <summary>Node indices; n1/n2 are −1 when fewer than three nodes
            /// were in reach.</summary>
            public int n0, n1, n2;
            /// <summary>Inverse-distance weights, normalised to sum 1 over the
            /// bound nodes.</summary>
            public float w0, w1, w2;
        }

        /// <summary>
        /// Tie each vertex (metres, vehicle frame) to its nearest nodes within
        /// <see cref="BindReachPitches"/>·spacing — the nearest one
        /// unconditionally, so no vertex is orphaned by a coarse grid. Ties break
        /// by node index, which is what keeps the binding deterministic when two
        /// nodes are equidistant on a symmetric body.
        /// </summary>
        public static VertexBinding[] BindVertices(LatticeNode[] nodes, Vector3[] verticesM,
                                                   float spacing)
        {
            if (nodes == null || nodes.Length == 0 || verticesM == null)
                return System.Array.Empty<VertexBinding>();

            float reach = Mathf.Max(spacing, 1e-4f) * BindReachPitches;
            float reach2 = reach * reach;
            var result = new List<VertexBinding>(verticesM.Length);

            for (int v = 0; v < verticesM.Length; v++)
            {
                Vector3 p = verticesM[v];
                int i0 = -1, i1 = -1, i2 = -1;
                float d0 = float.MaxValue, d1 = float.MaxValue, d2 = float.MaxValue;

                for (int i = 0; i < nodes.Length; i++)
                {
                    float d = (nodes[i].localPos - p).sqrMagnitude;
                    // Strict < everywhere: on a tie the earlier (lower) index,
                    // already in place, keeps its slot.
                    if (d < d0) { d2 = d1; i2 = i1; d1 = d0; i1 = i0; d0 = d; i0 = i; }
                    else if (d < d1) { d2 = d1; i2 = i1; d1 = d; i1 = i; }
                    else if (d < d2) { d2 = d; i2 = i; }
                }
                if (i0 < 0) continue;

                // Nearest is unconditional; the others must be within reach.
                if (d1 > reach2) { i1 = -1; }
                if (i1 < 0 || d2 > reach2) { i2 = -1; }

                float w0 = 1f / (Mathf.Sqrt(d0) + 0.005f);
                float w1 = i1 >= 0 ? 1f / (Mathf.Sqrt(d1) + 0.005f) : 0f;
                float w2 = i2 >= 0 ? 1f / (Mathf.Sqrt(d2) + 0.005f) : 0f;
                float sum = w0 + w1 + w2;

                result.Add(new VertexBinding
                {
                    vertex = v,
                    n0 = i0, n1 = i1, n2 = i2,
                    w0 = w0 / sum, w1 = w1 / sum, w2 = w2 / sum,
                });
            }
            return result.ToArray();
        }

        /// <summary>
        /// For each submesh channel, the beams that carry its load — every beam
        /// with at least one endpoint among the nodes its vertices bind to. This
        /// is what chunk detachment counts broken beams against.
        /// </summary>
        public static int[][] ChannelSupport(LatticeBeam[] beams, VertexBinding[] bindings,
                                             int[][] submeshTriangles)
        {
            int channels = submeshTriangles?.Length ?? 0;
            var result = new int[channels][];
            if (channels == 0) return result;

            // vertex → its bound nodes, once
            var vertNodes = new Dictionary<int, (int, int, int)>(bindings.Length);
            foreach (VertexBinding vb in bindings)
                vertNodes[vb.vertex] = (vb.n0, vb.n1, vb.n2);

            for (int c = 0; c < channels; c++)
            {
                var nodeSet = new HashSet<int>();
                int[] tris = submeshTriangles[c];
                if (tris != null)
                    foreach (int vi in tris)
                        if (vertNodes.TryGetValue(vi, out var nn))
                        {
                            nodeSet.Add(nn.Item1);
                            if (nn.Item2 >= 0) nodeSet.Add(nn.Item2);
                            if (nn.Item3 >= 0) nodeSet.Add(nn.Item3);
                        }

                var support = new List<int>();
                for (int bi = 0; bi < beams.Length; bi++)
                    if (nodeSet.Contains(beams[bi].a) || nodeSet.Contains(beams[bi].b))
                        support.Add(bi);
                result[c] = support.ToArray();
            }
            return result;
        }

        // ---- pick math -----------------------------------------------------------

        /// <summary>Nearest node whose sphere of <paramref name="radiusM"/> the
        /// ray crosses. Pure math rather than colliders: a thousand handles as
        /// physics objects would crowd every raycast the studio already does, and
        /// arithmetic lands in the bench.</summary>
        public static bool PickNode(Ray ray, IReadOnlyList<LatticeNode> nodes,
                                    Matrix4x4 localToWorld, float radiusM, out int index)
        {
            index = -1;
            if (nodes == null) return false;
            float bestT = float.MaxValue;
            Vector3 o = ray.origin, d = ray.direction.normalized;

            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 c = localToWorld.MultiplyPoint3x4(nodes[i].localPos);
                Vector3 oc = c - o;
                float t = Vector3.Dot(oc, d);
                if (t < 0f) continue;
                float miss2 = (oc - d * t).sqrMagnitude;
                if (miss2 > radiusM * radiusM) continue;
                if (t < bestT) { bestT = t; index = i; }
            }
            return index >= 0;
        }

        /// <summary>Nearest beam the ray passes within <paramref name="radiusM"/>
        /// of, by closest approach between the ray and the segment.</summary>
        public static bool PickBeam(Ray ray, IReadOnlyList<LatticeNode> nodes,
                                    IReadOnlyList<LatticeBeam> beams,
                                    Matrix4x4 localToWorld, float radiusM, out int index)
        {
            index = -1;
            if (nodes == null || beams == null) return false;
            float bestT = float.MaxValue;
            Vector3 o = ray.origin, d = ray.direction.normalized;

            for (int i = 0; i < beams.Count; i++)
            {
                LatticeBeam beam = beams[i];
                if (beam.a < 0 || beam.b < 0 || beam.a >= nodes.Count || beam.b >= nodes.Count)
                    continue;
                Vector3 p0 = localToWorld.MultiplyPoint3x4(nodes[beam.a].localPos);
                Vector3 p1 = localToWorld.MultiplyPoint3x4(nodes[beam.b].localPos);

                // Closest approach between the ray (o + t·d, d unit, t ≥ 0) and
                // the segment (p0 + s·u, s ∈ [0, 1]). Standard two-line system
                // with the segment parameter clamped FIRST, then the ray
                // parameter recomputed against the clamped point — clamping both
                // independently reports a distance neither line achieves.
                Vector3 u = p1 - p0;
                Vector3 w = p0 - o;
                float uu = Vector3.Dot(u, u);
                if (uu < 1e-10f) continue;
                float ud = Vector3.Dot(u, d);
                float uw = Vector3.Dot(u, w);
                float dw = Vector3.Dot(d, w);
                float den = uu - ud * ud;          // uu·sin² of the angle between them

                float s = den < 1e-8f * uu
                    ? 0f                            // near-parallel: any s, take an end
                    : Mathf.Clamp01((ud * dw - uw) / den);
                float t = Mathf.Max(0f, dw + s * ud);

                Vector3 onSeg = p0 + u * s;
                Vector3 onRay = o + d * t;
                if ((onSeg - onRay).sqrMagnitude > radiusM * radiusM) continue;
                if (t < bestT) { bestT = t; index = i; }
            }
            return index >= 0;
        }

    }
}
