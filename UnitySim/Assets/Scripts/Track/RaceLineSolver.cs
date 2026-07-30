using System.Collections.Generic;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Solves an ideal racing line over a track corridor, then puts a
    /// friction-limited speed profile on it.
    ///
    /// Pure maths, no editor and no scene access, so the Track Studio window, the
    /// headless calibration run and the validator all call the same code and get
    /// the same answer. Determinism matters here and is asserted: no RNG, no
    /// iteration over a hash container, no dependence on floating-point
    /// accumulation order across runs.
    /// </summary>
    public static class RaceLineSolver
    {
        /// <summary>Car half-width and edge margin, mirroring BotDriver's constants.
        /// Duplicated for the same reason PathCurvature is — BotDriver stays
        /// untouched — and checked against it by the validator.</summary>
        public const float CarHalfWidth = 0.20f;
        public const float EdgeMargin = 0.30f;

        /// <summary>Curvature above which a node counts as a corner, matching
        /// BotDriver's KappaRef (~5.5 m radius).</summary>
        public const float KappaRef = 0.18f;

        private const float G = 9.81f;

        // -------------------------------------------------------------------
        // inputs
        // -------------------------------------------------------------------

        public struct Settings
        {
            /// <summary>0 = pure minimum curvature, 1 = pure shortest path.</summary>
            public float shortestPathBlend;
            public int iterations;
            /// <summary>SOR over-relaxation. The Hessian diagonal is exactly 12 for
            /// unit normals, so ~1.3 is well-behaved without per-track tuning.</summary>
            public float sor;
            public float regularizer;

            public float muScale;
            public float accelA0;
            public float vMax;
            public float brakeUse;
            public bool useBanking;

            public static Settings Default => new Settings
            {
                // Pure minimum curvature runs wide on every corner exit, which is
                // wrong for 1/10-scale cars on tight circuits where the following
                // straight is often shorter than the arc saved.
                shortestPathBlend = 0.15f,
                iterations = 400,
                sor = 1.3f,
                regularizer = 1e-3f,
                muScale = 1f,
                accelA0 = 6f,
                vMax = 11f,
                brakeUse = 0.8f,
                useBanking = true,
            };
        }

        /// <summary>One node of the drivable corridor the line is solved inside.</summary>
        public struct Node
        {
            public Vector3 center;
            public Vector3 right;      // unit, Cross(up, tangent)
            public float halfLeft;     // usable metres left of centre (positive)
            public float halfRight;    // usable metres right of centre (positive)
            public int surface;        // TrackCatalog.Floors index
            public float bankRad;      // positive drops the right edge
        }

        public sealed class Result
        {
            public Vector3[] points;
            public float[] lateral;
            public float[] curvature;
            public float[] speed;
            public bool[] braking;
            public int[] apexIndices;
            public List<RacingLineAsset.BrakeZone> brakeZones = new List<RacingLineAsset.BrakeZone>();
            public float lapSeconds;
            public float length;
            /// <summary>Fraction of nodes limited by grip rather than by power or
            /// the speed cap — how much of this lap is actually at the limit.</summary>
            public float limitFraction;
        }

        // -------------------------------------------------------------------
        // corridor
        // -------------------------------------------------------------------

        /// <summary>
        /// Build a corridor from a SplineSpec: centreline, per-node normals and the
        /// half width a car can actually use, which is the ribbon's half width less
        /// the car's own half width and a margin off the edge.
        /// </summary>
        public static Node[] CorridorFrom(SplineSpec spec, out bool closed)
        {
            closed = spec != null && spec.closed;
            if (spec == null || spec.Count < 2) return System.Array.Empty<Node>();

            var samples = SplineMath.SampleAll(spec);
            int n = samples.Count;
            if (n < 3) return System.Array.Empty<Node>();
            SplineMath.ComputeFrames(samples, spec.closed, out var right, out _);

            var nodes = new Node[n];
            for (int i = 0; i < n; i++)
            {
                float usable = Mathf.Max(0f, samples[i].width * 0.5f - CarHalfWidth - EdgeMargin);
                var r = right[i];
                r.y = 0f;
                if (r.sqrMagnitude < 1e-8f) r = Vector3.right; else r.Normalize();
                nodes[i] = new Node
                {
                    center = samples[i].pos,
                    right = r,
                    halfLeft = usable,
                    halfRight = usable,
                    surface = samples[i].surfaceType,
                    bankRad = samples[i].roll * Mathf.Deg2Rad,
                };
            }
            return nodes;
        }

        // -------------------------------------------------------------------
        // solve
        // -------------------------------------------------------------------

        public static Result Solve(Node[] nodes, bool closed, Settings s)
        {
            int n = nodes != null ? nodes.Length : 0;
            var res = new Result();
            if (n < 3)
            {
                res.points = System.Array.Empty<Vector3>();
                res.lateral = System.Array.Empty<float>();
                res.curvature = System.Array.Empty<float>();
                res.speed = System.Array.Empty<float>();
                res.braking = System.Array.Empty<bool>();
                res.apexIndices = System.Array.Empty<int>();
                return res;
            }

            var lat = new float[n];
            SolveLateral(nodes, closed, s, lat);

            var pts = new Vector3[n];
            for (int i = 0; i < n; i++) pts[i] = nodes[i].center + nodes[i].right * lat[i];

            res.points = pts;
            res.lateral = lat;
            res.curvature = PathCurvature.Signed(pts, closed);
            res.length = PathCurvature.TotalLength(pts, closed);

            BuildSpeedProfile(nodes, pts, res.curvature, closed, s, res);
            res.apexIndices = FindApexes(res.curvature, res.speed, pts, closed);
            BuildBrakeZones(pts, res, closed);
            return res;
        }

        /// <summary>
        /// Minimum curvature over lateral offsets, by projected SOR.
        ///
        /// Parameterise node i by a scalar offset n_i along its normal:
        ///   p_i = c_i + n_i * r_i,  n_i in [-halfLeft, +halfRight]
        /// and minimise the discrete bending energy
        ///   J = sum_i || p_{i-1} - 2 p_i + p_{i+1} ||^2
        /// which is linear least squares in n (the sample spacing is near-uniform by
        /// construction, so the second difference is a fair curvature proxy).
        ///
        /// <b>Why SOR and not a direct banded solve.</b> A closed loop makes the
        /// pentadiagonal normal-equation system CYCLIC, needing Sherman-Morrison or a
        /// doubled domain; SOR just indexes modulo n and the seam stops being a case
        /// at all. And the box constraint makes this a QP rather than a plain least
        /// squares — clamping after each coordinate update IS projected Gauss-Seidel,
        /// which converges here because the system is symmetric positive semidefinite
        /// and the regulariser makes it diagonally dominant.
        /// </summary>
        private static void SolveLateral(Node[] nd, bool closed, Settings s, float[] lat)
        {
            int n = nd.Length;
            float eps = Mathf.Clamp01(s.shortestPathBlend);
            float omega = Mathf.Clamp(s.sor, 0.1f, 1.9f);
            float lambda = Mathf.Max(0f, s.regularizer);
            int iters = Mathf.Max(1, s.iterations);

            // Scale the two objectives by their value at n = 0 so the blend slider
            // means the same thing on a 20 m kart loop and a 300 m circuit.
            float scaleJ = 0f, scaleS = 0f;
            for (int i = 0; i < n; i++)
            {
                scaleJ += Second(nd, lat, i, n, closed).sqrMagnitude;
                scaleS += (nd[Idx(i + 1, n, closed)].center - nd[i].center).sqrMagnitude;
            }
            scaleJ = Mathf.Max(1e-6f, scaleJ);
            scaleS = Mathf.Max(1e-6f, scaleS);
            float wJ = (1f - eps) / scaleJ;
            float wS = eps / scaleS;

            for (int it = 0; it < iters; it++)
            {
                float maxDelta = 0f;
                for (int i = 0; i < n; i++)
                {
                    // Open paths pin their endpoints: an unconstrained end wanders
                    // off the road looking for a straighter continuation that is
                    // not there.
                    if (!closed && (i == 0 || i == n - 1)) continue;

                    var r = nd[i].right;

                    // dJ/dn_i from the three second differences that involve node i.
                    float g = 0f;
                    for (int k = -1; k <= 1; k++)
                    {
                        int c = Idx(i + k, n, closed);
                        if (!closed && (c <= 0 || c >= n - 1)) continue;
                        float coeff = (k == 0) ? -2f : 1f;
                        g += 2f * coeff * Vector3.Dot(Second(nd, lat, c, n, closed), r);
                    }
                    g *= wJ;
                    // Unit normals make the J Hessian diagonal exactly 1 + 4 + 1
                    // doubled = 12, so no per-track step tuning is needed.
                    float h = 12f * wJ;

                    if (eps > 0f)
                    {
                        int p = Idx(i - 1, n, closed), q = Idx(i + 1, n, closed);
                        Vector3 a = Point(nd, lat, i) - Point(nd, lat, p);
                        Vector3 b = Point(nd, lat, q) - Point(nd, lat, i);
                        g += wS * 2f * (Vector3.Dot(a, r) - Vector3.Dot(b, r));
                        h += 4f * wS;
                    }

                    g += lambda * lat[i];
                    h += lambda;

                    float next = lat[i] - omega * g / Mathf.Max(1e-9f, h);
                    next = Mathf.Clamp(next, -nd[i].halfLeft, nd[i].halfRight);
                    maxDelta = Mathf.Max(maxDelta, Mathf.Abs(next - lat[i]));
                    lat[i] = next;
                }
                if (maxDelta < 1e-4f) break;
            }
        }

        private static int Idx(int i, int n, bool closed) =>
            closed ? ((i % n) + n) % n : Mathf.Clamp(i, 0, n - 1);

        private static Vector3 Point(Node[] nd, float[] lat, int i) =>
            nd[i].center + nd[i].right * lat[i];

        private static Vector3 Second(Node[] nd, float[] lat, int i, int n, bool closed)
        {
            int p = Idx(i - 1, n, closed), q = Idx(i + 1, n, closed);
            return Point(nd, lat, p) - 2f * Point(nd, lat, i) + Point(nd, lat, q);
        }

        // -------------------------------------------------------------------
        // speed profile
        // -------------------------------------------------------------------

        /// <summary>
        /// Friction-limited cornering speed, then a forward pass bounded by drive
        /// acceleration and a backward pass bounded by braking, both on the friction
        /// ellipse. On a closed loop the two passes are iterated to a fixed point,
        /// because the speed at the start line depends on the speed arriving at it.
        /// </summary>
        private static void BuildSpeedProfile(Node[] nd, Vector3[] pts, float[] kappa,
            bool closed, Settings s, Result res)
        {
            int n = pts.Length;
            var v = new float[n];
            var braking = new bool[n];
            var mu = new float[n];
            var ds = new float[n];
            float vMax = Mathf.Max(0.5f, s.vMax);

            for (int i = 0; i < n; i++)
            {
                var f = TrackCatalog.Floors[Mathf.Clamp(nd[i].surface, 0,
                    TrackCatalog.Floors.Length - 1)];
                mu[i] = Mathf.Max(0.05f, f.frictionMult * Mathf.Max(0.05f, s.muScale));
                ds[i] = Vector3.Distance(pts[i], pts[Idx(i + 1, n, closed)]);
            }

            int limited = 0;
            for (int i = 0; i < n; i++)
            {
                float k = Mathf.Max(Mathf.Abs(kappa[i]), 1e-3f);
                float m = mu[i];
                float vc;
                if (s.useBanking)
                {
                    // Banking adds grip on the loaded side. tan(phi) is clamped well
                    // clear of 1/mu so a steeply banked node cannot divide by ~0 and
                    // report an infinite corner speed.
                    float tanPhi = Mathf.Clamp(Mathf.Tan(nd[i].bankRad), -0.6f, 0.6f);
                    float denom = Mathf.Max(0.05f, k * (1f - m * tanPhi));
                    vc = Mathf.Sqrt(Mathf.Max(0f, G * (m + tanPhi) / denom));
                }
                else
                {
                    vc = Mathf.Sqrt(G * m / k);
                }
                v[i] = Mathf.Min(vc, vMax);
                if (vc < vMax) limited++;
            }

            // Rolling resistance shows up as a small constant decel; it is a brake
            // torque per wheel in the car, which at this scale is a fraction of a
            // m/s^2 — small, but it is why a long straight on grass does not reach
            // the same terminal speed as asphalt.
            float RollDecel(int i)
            {
                var f = TrackCatalog.Floors[Mathf.Clamp(nd[i].surface, 0,
                    TrackCatalog.Floors.Length - 1)];
                return f.rollingResist * 40f;
            }

            int passes = closed ? 4 : 1;
            for (int pass = 0; pass < passes; pass++)
            {
                float maxDelta = 0f;

                // Forward: what the drive can actually build, inside what is left of
                // the friction circle after cornering has taken its share.
                for (int step = 1; step < (closed ? n + 1 : n); step++)
                {
                    int i = Idx(step, n, closed);
                    int prev = Idx(step - 1, n, closed);
                    float aLat = v[prev] * v[prev] * Mathf.Abs(kappa[prev]);
                    float budget = mu[prev] * G;
                    float aLong = Mathf.Sqrt(Mathf.Max(0f, budget * budget - aLat * aLat));
                    float drive = Mathf.Max(0f, s.accelA0 * (1f - v[prev] / vMax));
                    float a = Mathf.Min(drive, aLong) - RollDecel(prev);
                    float cap = Mathf.Sqrt(Mathf.Max(0f, v[prev] * v[prev] + 2f * a * ds[prev]));
                    if (cap < v[i]) { maxDelta = Mathf.Max(maxDelta, v[i] - cap); v[i] = cap; }
                }

                // Backward: how early braking has to start to make the next node.
                for (int step = (closed ? n : n - 2); step >= 0; step--)
                {
                    int i = Idx(step, n, closed);
                    int next = Idx(step + 1, n, closed);
                    float aLat = v[next] * v[next] * Mathf.Abs(kappa[i]);
                    float budget = mu[i] * G;
                    float aLong = Mathf.Sqrt(Mathf.Max(0f, budget * budget - aLat * aLat))
                                  * Mathf.Clamp01(s.brakeUse);
                    float cap = Mathf.Sqrt(Mathf.Max(0f, v[next] * v[next] + 2f * aLong * ds[i]));
                    if (cap < v[i])
                    {
                        maxDelta = Mathf.Max(maxDelta, v[i] - cap);
                        v[i] = cap;
                        braking[i] = true;
                    }
                }

                if (maxDelta < 1e-3f) break;
            }

            float t = 0f;
            for (int i = 0; i < (closed ? n : n - 1); i++)
            {
                float vAvg = Mathf.Max(0.05f, 0.5f * (v[i] + v[Idx(i + 1, n, closed)]));
                t += ds[i] / vAvg;
            }

            res.speed = v;
            res.braking = braking;
            res.lapSeconds = t;
            res.limitFraction = n > 0 ? (float)limited / n : 0f;
        }

        // -------------------------------------------------------------------
        // features
        // -------------------------------------------------------------------

        /// <summary>
        /// Apexes: local curvature maxima above <see cref="KappaRef"/>, clustered so
        /// one long corner reports one apex rather than forty, and resolved to the
        /// slowest node in each cluster — which is where the car is actually at its
        /// tightest, not merely where the geometry peaks.
        /// </summary>
        private static int[] FindApexes(float[] kappa, float[] speed, Vector3[] pts, bool closed)
        {
            int n = kappa.Length;
            var apexes = new List<int>();
            if (n < 5) return apexes.ToArray();

            const float MinSeparation = 1.5f;   // metres
            int i = 0;
            while (i < n)
            {
                if (Mathf.Abs(kappa[i]) < KappaRef) { i++; continue; }
                float sign = Mathf.Sign(kappa[i]);
                int start = i, best = i;
                while (i < n && Mathf.Abs(kappa[i]) >= KappaRef * 0.6f &&
                       Mathf.Sign(kappa[i]) == sign)
                {
                    if (speed[i] < speed[best]) best = i;
                    i++;
                }
                if (i > start) apexes.Add(best);
            }

            // Drop any that ended up within MinSeparation of the previous keeper.
            var kept = new List<int>();
            foreach (int a in apexes)
            {
                if (kept.Count > 0 &&
                    Vector3.Distance(pts[a], pts[kept[kept.Count - 1]]) < MinSeparation)
                {
                    if (speed[a] < speed[kept[kept.Count - 1]]) kept[kept.Count - 1] = a;
                    continue;
                }
                kept.Add(a);
            }
            return kept.ToArray();
        }

        private static void BuildBrakeZones(Vector3[] pts, Result res, bool closed)
        {
            int n = pts.Length;
            var cum = PathCurvature.Cumulative(pts);
            int i = 0;
            while (i < n)
            {
                if (!res.braking[i]) { i++; continue; }
                int start = i;
                while (i < n && res.braking[i]) i++;
                int end = Mathf.Min(i, n - 1);
                res.brakeZones.Add(new RacingLineAsset.BrakeZone
                {
                    sStart = cum[start],
                    sEnd = cum[end],
                    vEntry = res.speed[start],
                    vExit = res.speed[end],
                });
            }
        }

        /// <summary>
        /// Stable hash of the corridor a line was solved against, so a spline edit
        /// invalidates the bake. Quantised to a tenth of a millimetre: float noise
        /// from an unrelated recompile must not read as an edit, and a real edit is
        /// never that small.
        /// </summary>
        public static string HashCorridor(Node[] nodes, bool closed)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;   // FNV-1a 64
                void Mix(int v)
                {
                    h ^= (uint)v;
                    h *= 1099511628211UL;
                }
                Mix(closed ? 1 : 0);
                Mix(nodes != null ? nodes.Length : 0);
                if (nodes != null)
                    foreach (var nd in nodes)
                    {
                        Mix(Mathf.RoundToInt(nd.center.x * 10000f));
                        Mix(Mathf.RoundToInt(nd.center.y * 10000f));
                        Mix(Mathf.RoundToInt(nd.center.z * 10000f));
                        Mix(Mathf.RoundToInt(nd.halfLeft * 10000f));
                        Mix(Mathf.RoundToInt(nd.halfRight * 10000f));
                        Mix(nd.surface);
                        Mix(Mathf.RoundToInt(nd.bankRad * 10000f));
                    }
                return h.ToString("x16");
            }
        }
    }
}
