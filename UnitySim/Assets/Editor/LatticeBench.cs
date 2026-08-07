using System.Text;
using AIHWSim.BodyEd;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// [LATT] — the crash-frame solver, benched the way the flight model is:
    /// every stability, determinism and plasticity claim in
    /// <see cref="LatticeSolver"/>'s doc is a numbered check here, run in edit
    /// mode with no scene and no play button. The physics gates never see this
    /// solver (cars without a lattice never build one), so this bench IS its
    /// gate.
    /// </summary>
    public static class LatticeBench
    {
        private const string Tag = "[LATT]";
        private const float Dt = 1f / 400f;   // the driving scenes' fixed step

        private static int _checks, _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [LATT] Lattice Solver Bench", priority = 123)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            Substeps();
            Determinism();
            Stability();
            Plasticity();
            Propagation();
            Momentum();
            Clustering();
            Damage();
            Breaking();
            Sleeping();
            Binding();
            Cost();

            Debug.Log(_log.ToString().TrimEnd());
            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- fixtures ------------------------------------------------------------

        /// <summary>One grid cell as a lattice: 8 nodes, 24 beams (edges + face
        /// diagonals), every constant explicit — the configuration simple enough
        /// to reason about by hand.</summary>
        private static void Cube(float spacing, float mass, float k, float zeta,
                                 float breakStrain,
                                 out LatticeNode[] nodes, out LatticeBeam[] beams)
        {
            nodes = new LatticeNode[8];
            for (int i = 0; i < 8; i++)
                nodes[i] = new LatticeNode
                {
                    localPos = new Vector3((i & 1) * spacing, ((i >> 1) & 1) * spacing,
                                           ((i >> 2) & 1) * spacing),
                    mass = mass,
                };
            var list = new System.Collections.Generic.List<LatticeBeam>();
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                {
                    int x = i ^ j;
                    if ((x & 1) + ((x >> 1) & 1) + ((x >> 2) & 1) > 2) continue;
                    list.Add(new LatticeBeam
                    {
                        a = i, b = j, spring = k, dampingRatio = zeta,
                        breakStrain = breakStrain,
                    });
                }
            beams = list.ToArray();
        }

        /// <summary>The generated mid-fidelity box frame — the realistic-scale
        /// fixture, identical to what the studio's Generate button makes.</summary>
        private static void BoxFrame(out LatticeNode[] nodes, out LatticeBeam[] beams,
                                     out float spacing)
        {
            Vector3 size = new Vector3(0.40f, 0.12f, 0.20f);
            Vector3 h = size * 0.5f;
            var verts = new[]
            {
                new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
                new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
                new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z),
                new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z),
            };
            var tris = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                3, 6, 2, 3, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5,
            };
            spacing = LatticeBuilder.SpacingFor(0.5f, 0.40f);
            LatticeBuilder.Generate(verts, tris, spacing, out nodes, out beams);
            s_boxNodeCount = nodes.Length;
        }

        private static void Settle(LatticeSolver s, float maxSeconds = 3f)
        {
            int steps = Mathf.CeilToInt(maxSeconds / Dt);
            for (int i = 0; i < steps && !s.Asleep; i++) s.Step(Dt);
        }

        private static float MaxPlastic(LatticeSolver s)
        {
            float worst = 0f;
            for (int i = 0; i < s.NodeCount; i++)
                worst = Mathf.Max(worst, s.PlasticOffset(i).magnitude);
            return worst;
        }

        private static float MaxDisp(LatticeSolver s)
        {
            float worst = 0f;
            for (int i = 0; i < s.NodeCount; i++)
                worst = Mathf.Max(worst, s.Displacement(i).magnitude);
            return worst;
        }

        // ---- 1. the substep formula ------------------------------------------------

        private static void Substeps()
        {
            // Three configs, ω computed by hand: a cube node has 3 edges + 6
            // face diagonals = 9 beams... but the solver's bound is Σk over the
            // node's beams plus the anchor, so compute it the same way and check
            // the formula, not the topology.
            foreach ((float k, float m) in new[] { (240f, 0.006f), (1000f, 0.006f), (2000f, 0.002f) })
            {
                Cube(0.05f, m, k, 0.35f, 0.35f, out var nodes, out var beams);
                using var s = new LatticeSolver(nodes, beams);

                int beamsAtNode0 = 0;
                foreach (LatticeBeam beam in beams) if (beam.a == 0 || beam.b == 0) beamsAtNode0++;
                float kAnchor = m * LatticeSolver.AnchorOmega * LatticeSolver.AnchorOmega;
                float wHand = Mathf.Sqrt((beamsAtNode0 * k + kAnchor) / m);

                Check($"omega_max by hand (k={k:0}, m={m * 1000f:0}g)", s.OmegaMax, wHand,
                      wHand * 0.001f, "rad/s",
                      "every cube corner is identical, so the conservative bound is exact here");

                s.Step(Dt);   // resolves the substep count
                int sHand = Mathf.Max(1, Mathf.CeilToInt(s.OmegaMax * Dt / LatticeSolver.TargetOmegaH));
                bool rescaled = sHand > LatticeSolver.MaxSubsteps;
                Check($"substeps follow the formula (k={k:0})", s.Substeps,
                      rescaled ? LatticeSolver.MaxSubsteps : sHand, 0f, "", "");
                Bool($"omega*h within target (k={k:0})",
                     s.OmegaMax * (Dt / s.Substeps) <= LatticeSolver.TargetOmegaH * 1.001f,
                     "the 4x stability margin the whole solver rests on");
                if (rescaled)
                    Bool("the over-budget config rescaled rather than exploded",
                         s.StiffnessRescaled, "softer beats unstable, and it says so");
            }
            Line("");
        }

        // ---- 2. determinism --------------------------------------------------------

        private static void Determinism()
        {
            BoxFrame(out var nodes, out var beams, out _);
            using var s1 = new LatticeSolver(nodes, beams);
            using var s2 = new LatticeSolver(nodes, beams);

            var hits = new (int step, Vector3 p, Vector3 d, float j)[]
            {
                (0, new Vector3(0.18f, 0.03f, 0.02f), new Vector3(-1f, 0f, 0.2f), 2.5f),
                (120, new Vector3(-0.18f, 0.02f, -0.05f), new Vector3(1f, 0.1f, 0f), 4f),
                (300, new Vector3(0f, 0.05f, 0.10f), new Vector3(0f, -0.3f, -1f), 1.2f),
            };

            foreach (LatticeSolver s in new[] { s1, s2 })
            {
                int hi = 0;
                for (int step = 0; step < 800; step++)
                {
                    while (hi < hits.Length && hits[hi].step == step)
                    {
                        s.ApplyHit(hits[hi].p, hits[hi].d, hits[hi].j);
                        hi++;
                    }
                    s.Step(Dt);
                }
            }

            bool identical = true;
            for (int i = 0; i < s1.NodeCount && identical; i++)
                identical = s1.Displacement(i) == s2.Displacement(i)
                            && s1.PlasticOffset(i) == s2.PlasticOffset(i);
            for (int i = 0; i < s1.BeamCount && identical; i++)
                identical = s1.IsBroken(i) == s2.IsBroken(i);
            Bool("two runs of the same hits are bit-identical", identical,
                 "LAN peers replay the same quantized hits into their own solver — " +
                 "anything less than bit-equality drifts the dents apart");
            Line($"determinism fixture: {s1.NodeCount} nodes, {s1.BeamCount} beams, " +
                 $"S = {s1.Substeps}, {s1.BrokenTotal} broken");
            Line("");
        }

        // ---- 3. stability ----------------------------------------------------------

        private static void Stability()
        {
            // The nastiest legal configuration: maximum editor spring (10x
            // default order), nearly no damping, light nodes.
            Cube(0.05f, 0.002f, 2000f, 0.05f, 10f, out var nodes, out var beams);
            using var s = new LatticeSolver(nodes, beams);
            s.ApplyHit(nodes[0].localPos, new Vector3(1f, 1f, 1f), 5f);

            // Ring-down: the ENVELOPE only decays. Instantaneous KE oscillates
            // with the phase — a point sample every N steps aliases against the
            // ring frequency and reads rises that are not there — so each block
            // records its own maximum and the maxima must fall.
            float prev = float.MaxValue;
            bool decaying = true, bounded = true, nan = false;
            for (int block = 0; block < 10; block++)
            {
                float blockMax = 0f;
                for (int i = 0; i < 100; i++)
                {
                    s.Step(Dt);
                    blockMax = Mathf.Max(blockMax, s.KineticEnergy());
                }
                if (float.IsNaN(blockMax)) nan = true;
                if (block >= 1 && blockMax > prev * 1.01f) decaying = false;
                prev = blockMax;
                if (MaxDisp(s) > LatticeSolver.MaxDisplacement * 1.001f) bounded = false;
            }
            Bool("energy only decays after the transient", decaying,
                 "an explicit integrator that gains energy is the classic soft-body failure");
            Bool("no NaN in a quarter second of worst case", !nan && s.NaNResets == 0, "");
            Bool("displacement stays inside the clamp", bounded,
                 $"{LatticeSolver.MaxDisplacement * 1000f:0} mm is the whole crumple budget");
            Line("");
        }

        // ---- 4. plasticity ---------------------------------------------------------

        private static void Plasticity()
        {
            BoxFrame(out var nodes, out var beams, out _);
            using var s = new LatticeSolver(nodes, beams);
            Vector3 hitP = new Vector3(0.18f, 0.03f, 0f);
            Vector3 hitD = new Vector3(-1f, 0f, 0f);

            s.ApplyHit(hitP, hitD, 3f);
            Settle(s);
            float dent1 = MaxPlastic(s);
            Bool("a hard hit leaves a dent", dent1 > 1e-4f,
                 "plasticity is the feature — a lattice that springs all the way back is " +
                 "a wobble toy, not crash damage");

            s.ApplyHit(hitP, hitD, 3f);
            Settle(s);
            float dent2 = MaxPlastic(s);
            Bool("the same hit again dents strictly deeper", dent2 > dent1 + 1e-5f,
                 "monotone damage: two crashes are worse than one");

            using var soft = new LatticeSolver(nodes, beams);
            soft.ApplyHit(hitP, hitD, 0.02f);
            Settle(soft);
            Check("a sub-yield tap leaves no dent", MaxPlastic(soft), 0f, 1e-6f, "m",
                  "parking-lot contact must not slowly consume the car");
            Line($"plasticity: first dent {dent1 * 1000f:0.00} mm, second {dent2 * 1000f:0.00} mm");
            Line("");
        }

        // ---- 4a. force propagation -------------------------------------------------

        /// <summary>The user note this answers: "forces need to propagate more
        /// through the mesh, dying out with distance from the impact point."
        /// Propagation is now the injection kernel, so it is measurable at
        /// injection — no stepping, no argument about what the beams did.</summary>
        private static void Propagation()
        {
            BoxFrame(out var nodes, out var beams, out _);
            using var s = new LatticeSolver(nodes, beams);
            Vector3 hitP = new Vector3(0.18f, 0.03f, 0f);
            Vector3 hitD = Vector3.left;
            const float j = 3f;

            float r = s.HitRadius(j);
            s.ApplyHit(hitP, hitD, j);

            // Every moved node is inside R, every node past R is bit-still, and
            // speed never rises with distance — the falloff, stated three ways.
            int moved = 0;
            bool insideOnly = true, monotone = true, nearestIsPeak = true;
            float peak = 0f, peakD = 0f;
            var order = new System.Collections.Generic.List<(float d, float v)>();
            for (int i = 0; i < s.NodeCount; i++)
            {
                float d = (nodes[i].localPos - hitP).magnitude;
                float v = s.Velocity(i).magnitude;
                if (v > 0f) { moved++; order.Add((d, v)); if (d > r + 1e-6f) insideOnly = false; }
                else if (d < r - 1e-6f) insideOnly = false;   // inside but untouched
                if (v > peak) { peak = v; peakD = d; }
            }
            order.Sort((x, y) => x.d.CompareTo(y.d));
            for (int i = 1; i < order.Count; i++)
                if (order[i].v > order[i - 1].v + 1e-6f) monotone = false;
            nearestIsPeak = peakD < r * 0.5f;

            Bool("a hit moves a whole region, not three nodes", moved >= 8,
                 "three-nearest injection was the 'forces don't propagate' report: the " +
                 "anchor and dampers kill the beam ring within a few centimetres, so " +
                 "whatever the kernel does not reach never moves at all");
            Bool("the kernel reaches exactly the nodes inside R", insideOnly,
                 "compact support: zero at the radius, so there is no faint " +
                 "car-wide shiver and no hard edge either");
            Bool("push falls off with distance, never rises", monotone,
                 "the quartic bump is monotone by construction; this pins that the " +
                 "distances it is fed are the pristine ones");
            Bool("the peak is at the contact", nearestIsPeak, "");

            // Superposition — the second user note. Two contacts in one step
            // must be the exact vector sum of each alone.
            Vector3 pA = new Vector3(0.18f, 0.03f, 0.04f), pB = new Vector3(-0.18f, 0.03f, -0.04f);
            using var sa = new LatticeSolver(nodes, beams); sa.ApplyHit(pA, Vector3.left, 2f);
            using var sb = new LatticeSolver(nodes, beams); sb.ApplyHit(pB, Vector3.right, 2f);
            using var sab = new LatticeSolver(nodes, beams);
            sab.ApplyHit(pA, Vector3.left, 2f);
            sab.ApplyHit(pB, Vector3.right, 2f);
            bool superposes = true;
            for (int i = 0; i < sab.NodeCount && superposes; i++)
                superposes = sab.Velocity(i) == sa.Velocity(i) + sb.Velocity(i);
            Bool("simultaneous hits superpose exactly", superposes,
                 "two impacts in one collision are two injections; anything but a sum " +
                 "would make the order they were sensed in matter");

            Line($"propagation: R = {r * 1000f:0} mm ({r / s.MeanBeamLength:0.0} beam lengths), " +
                 $"{moved} of {s.NodeCount} nodes pushed, peak {peak:0.00} m/s");
            Line("");
        }

        // ---- 4b. momentum ----------------------------------------------------------

        /// <summary>The third user note: "vehicles need to transfer momentum
        /// into their crashes." Node speed saturates, so momentum has to buy
        /// AREA — a harder hit must crush wider, not just as hard.</summary>
        private static void Momentum()
        {
            BoxFrame(out var nodes, out var beams, out _);
            Vector3 hitP = new Vector3(0.18f, 0.03f, 0f);
            Vector3 hitD = Vector3.left;

            (float depth, int width, float radius) Crash(float j)
            {
                using var s = new LatticeSolver(nodes, beams);
                float r = s.HitRadius(j);
                s.ApplyHit(hitP, hitD, j);
                Settle(s);
                int wide = 0;
                for (int i = 0; i < s.NodeCount; i++)
                    if (s.PlasticOffset(i).magnitude > 1e-4f) wide++;
                return (MaxPlastic(s), wide, r);
            }

            var soft = Crash(1.5f);
            var hard = Crash(6f);
            var huge = Crash(24f);

            Bool("a harder hit crushes a WIDER area", hard.width > soft.width
                 && huge.width > hard.width,
                 "the per-node speed cap saturates at ~0.35 N.s, so above that every " +
                 "hit injected identical velocity and a 100 km/h wall felt like a shove");
            Bool("a harder hit still dents deeper", hard.depth > soft.depth
                 && huge.depth >= hard.depth,
                 "wider must not come at the cost of monotone depth");
            Bool("the radius grows with the root of impulse", huge.radius > hard.radius
                 && hard.radius > soft.radius, "");
            Bool("one contact never reaches the whole car",
                 huge.radius <= 0.40f * LatticeSolver.MaxHitRadiusFrac + 1e-4f,
                 "the clamp is a fraction of the lattice's own extent");
            Bool("a tap stays a dimple", soft.width < s_boxNodeCount / 3,
                 "a parking-lot nudge that crumples a third of the car is not a nudge");

            Line($"momentum: 1.5 / 6 / 24 N.s → R {soft.radius * 1000f:0}/{hard.radius * 1000f:0}/" +
                 $"{huge.radius * 1000f:0} mm, {soft.width}/{hard.width}/{huge.width} nodes dented, " +
                 $"depth {soft.depth * 1000f:0.0}/{hard.depth * 1000f:0.0}/{huge.depth * 1000f:0.0} mm");
            Line("");
        }

        private static int s_boxNodeCount;

        // ---- 4c. contact clustering ------------------------------------------------

        /// <summary>Superposition's other half: one collision may be several
        /// impacts. Averaging nose and tail contacts dented the middle of the
        /// car — a place nothing touched.</summary>
        private static void Clustering()
        {
            var outP = new Vector3[CarSoftLattice.MaxHitSources];
            var outN = new Vector3[CarSoftLattice.MaxHitSources];
            var outC = new int[CarSoftLattice.MaxHitSources];

            int Run(Vector3[] pts)
            {
                var nrm = new Vector3[pts.Length];
                for (int i = 0; i < pts.Length; i++) nrm[i] = Vector3.forward;
                return CarSoftLattice.Cluster(pts, nrm, pts.Length, outP, outN, outC);
            }

            Bool("one contact is one source",
                 Run(new[] { new Vector3(0.2f, 0f, 0f) }) == 1,
                 "light contacts must behave exactly as they did before clustering");

            int near = Run(new[]
            {
                new Vector3(0.20f, 0f, 0f), new Vector3(0.22f, 0.01f, 0f),
                new Vector3(0.19f, -0.01f, 0.02f),
            });
            Bool("one contact patch stays one source", near == 1, "");
            Check("its point is the member centroid", outP[0].x,
                  (0.20f + 0.22f + 0.19f) / 3f, 1e-5f, "m", "");

            int split = Run(new[] { new Vector3(0.20f, 0f, 0f), new Vector3(-0.20f, 0f, 0f) });
            Bool("nose and tail are two sources", split == 2,
                 "the broadside case: averaging these dents the middle of the car");

            int capped = Run(new[]
            {
                new Vector3(0.20f, 0f, 0f), new Vector3(-0.20f, 0f, 0f),
                new Vector3(0f, 0.20f, 0f), new Vector3(0f, -0.20f, 0f),
                new Vector3(0f, 0f, 0.20f),
            });
            Bool("the source budget holds", capped == CarSoftLattice.MaxHitSources,
                 "each source is a LAN message; five contacts may not become five sends");

            // Determinism: the same points in the same order, twice, and the
            // total point count always conserved.
            var pts2 = new[]
            {
                new Vector3(0.20f, 0f, 0f), new Vector3(-0.20f, 0f, 0f),
                new Vector3(0.21f, 0.01f, 0f), new Vector3(0f, 0.25f, 0f),
                new Vector3(-0.19f, 0f, 0.01f),
            };
            int a = Run(pts2);
            var pa = new Vector3[a]; var ca = new int[a];
            System.Array.Copy(outP, pa, a); System.Array.Copy(outC, ca, a);
            int b = Run(pts2);
            bool same = a == b;
            int totalPts = 0;
            for (int i = 0; i < a && same; i++)
            {
                same = pa[i] == outP[i] && ca[i] == outC[i];
                totalPts += outC[i];
            }
            Bool("clustering is bit-deterministic", same,
                 "greedy in index order, no sorting, no ties — the hits a peer replays " +
                 "have to be the hits the owner sensed");
            Bool("every contact lands in exactly one cluster", totalPts == pts2.Length,
                 "the impulse is split by member count, so a lost point is lost momentum");
            Line("");
        }

        // ---- 4d. the damage scale --------------------------------------------------

        private static void Damage()
        {
            BoxFrame(out var nodes, out var beams, out _);
            Vector3 hitP = new Vector3(0.18f, 0.03f, 0f);
            Vector3 hitD = new Vector3(-1f, 0f, 0f);
            const float wallHit = 6f;   // ≈ the 1.6 kg car arriving at ~4 m/s

            float DentAt(float damage01)
            {
                using var s = new LatticeSolver(nodes, beams);
                s.SetDamageScale(LatticeBuilder.DamageScale(damage01));
                s.ApplyHit(hitP, hitD, wallHit);
                Settle(s);
                return MaxPlastic(s);
            }

            float lo = DentAt(0f), mid = DentAt(0.5f), hi = DentAt(1f);
            Bool("more damage dents deeper", lo < mid && mid < hi,
                 "the slider is the whole point — it has to move the outcome, not a number");
            Bool("a wall hit at neutral damage leaves a VISIBLE dent", mid > 0.004f,
                 "the reported bug: dents existed at millimetre scale nobody could see");
            Bool("minimum damage mostly springs back", lo < mid * 0.35f,
                 "0.1x raises the yield thresholds tenfold — a tank, not a soda can");

            using var sHi = new LatticeSolver(nodes, beams);
            sHi.SetDamageScale(LatticeBuilder.DamageScale(1f));
            sHi.ApplyHit(hitP, hitD, wallHit);
            Settle(sHi);
            using var sMid = new LatticeSolver(nodes, beams);
            sMid.ApplyHit(hitP, hitD, wallHit);
            Settle(sMid);
            int brokeHi = sHi.BrokenTotal, brokeMid = sMid.BrokenTotal;
            Bool("high damage breaks what neutral only bends", brokeHi > brokeMid,
                 "the break strain drops with the same scale, so chunks come off sooner");

            // Chunks must still be REACHABLE at neutral damage, or detachment
            // is a feature only the slider's top end has. The wide kernel
            // spreads strain, so a 6 N.s hit now bends where it used to snap —
            // the price of propagation, paid back at real crash impulses.
            using var sBig = new LatticeSolver(nodes, beams);
            sBig.ApplyHit(hitP, hitD, 40f);
            Settle(sBig);
            Bool("a heavy crash still snaps beams at neutral damage", sBig.BrokenTotal > 0,
                 "chunk detachment hangs off broken beams; if only 10x damage ever " +
                 "breaks anything, the debris path is dead on a default car");

            // Repair — the respawn key and the FRAME tab button both land in
            // ResetToRest, and the result must be indistinguishable from new.
            sHi.ResetToRest();
            bool pristine = sHi.Asleep && sHi.BrokenTotal == 0;
            for (int i = 0; i < sHi.NodeCount && pristine; i++)
                pristine = sHi.Displacement(i) == Vector3.zero
                           && sHi.PlasticOffset(i) == Vector3.zero;
            for (int i = 0; i < sHi.BeamCount && pristine; i++)
                pristine = !sHi.IsBroken(i);
            Bool("repair is bit-pristine", pristine,
                 "a repaired car must be the car that never crashed, not one near it");

            Line($"damage: dent {lo * 1000f:0.00} / {mid * 1000f:0.00} / {hi * 1000f:0.00} mm " +
                 $"at 0.1x / 1x / 10x; broken {brokeMid} at 1x vs {brokeHi} at 10x, " +
                 $"{sBig.BrokenTotal} for a 40 N.s crash at 1x");
            Line("");
        }

        // ---- 5. breaking -----------------------------------------------------------

        private static void Breaking()
        {
            Cube(0.05f, 0.006f, 240f, 0.35f, 0.03f, out var nodes, out var beams);
            using var s = new LatticeSolver(nodes, beams);
            s.ApplyHit(nodes[0].localPos, new Vector3(1f, 0.5f, 0.25f), 4f);

            int totalSeen = 0;
            bool countsAgree = true;
            for (int i = 0; i < 800; i++)
            {
                s.Step(Dt);
                totalSeen += s.BrokenThisStep;
                if (s.BrokenTotal != totalSeen) countsAgree = false;
            }
            Bool("a hit past the break strain snaps beams", s.BrokenTotal > 0, "");
            Bool("each break is counted exactly once", countsAgree,
                 "chunk detachment counts these — double-counting pops a panel twice");

            bool stayBroken = true;
            var brokenAt = new bool[s.BeamCount];
            for (int i = 0; i < s.BeamCount; i++) brokenAt[i] = s.IsBroken(i);
            s.ApplyHit(nodes[7].localPos, new Vector3(-1f, -0.5f, -0.25f), 2f);
            for (int i = 0; i < 400; i++) s.Step(Dt);
            for (int i = 0; i < s.BeamCount; i++)
                if (brokenAt[i] && !s.IsBroken(i)) stayBroken = false;
            Bool("broken stays broken", stayBroken, "one-way, like real metal");
            Line($"breaking: {s.BrokenTotal} of {s.BeamCount} beams");
            Line("");
        }

        // ---- 6. sleep --------------------------------------------------------------

        private static void Sleeping()
        {
            BoxFrame(out var nodes, out var beams, out _);
            using var s = new LatticeSolver(nodes, beams);

            Bool("an unhit lattice is born asleep", s.Asleep && !s.Step(Dt),
                 "most cars never crash; their whole solver cost is this branch");

            s.ApplyHit(new Vector3(0.15f, 0.02f, 0.05f), Vector3.left, 2f);
            Settle(s, 5f);
            Bool("a hit lattice rings down to sleep", s.Asleep,
                 "a solver that never sleeps is a permanent 400 Hz tax");

            var posBefore = new Vector3[s.NodeCount];
            for (int i = 0; i < s.NodeCount; i++) posBefore[i] = s.Displacement(i);
            bool untouched = true;
            for (int i = 0; i < 1000; i++) if (s.Step(Dt)) untouched = false;
            for (int i = 0; i < s.NodeCount && untouched; i++)
                untouched = s.Displacement(i) == posBefore[i];
            Bool("a sleeping lattice is bit-still", untouched,
                 "the dent survives, the CPU forgets");
            Line("");
        }

        // ---- 7. mesh binding and chunk boundary ------------------------------------

        private static void Binding()
        {
            Vector3 size = new Vector3(0.40f, 0.12f, 0.20f);
            Vector3 h = size * 0.5f;
            var baseVerts = new[]
            {
                new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z),
                new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z),
                new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z),
                new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, h.z),
            };
            BoxFrame(out var nodes, out var beams, out float spacing);
            using var solver = new LatticeSolver(nodes, beams);
            var binds = LatticeBuilder.BindVertices(nodes, baseVerts, spacing);
            var work = (Vector3[])baseVerts.Clone();

            // Zero lattice motion ⇒ bit-zero vertex delta — the property that
            // makes the mesh write skippable and an undamaged car exactly the
            // car that shipped.
            CarSoftLattice.WriteVertices(binds, solver, baseVerts, Vector3.one, work);
            bool bitEqual = true;
            for (int i = 0; i < work.Length; i++) if (work[i] != baseVerts[i]) bitEqual = false;
            Bool("zero displacement writes zero delta", bitEqual, "");

            // A real hit moves bound vertices, and no vertex further than the
            // worst node it is bound to.
            solver.ApplyHit(new Vector3(0.18f, 0.04f, 0.02f), Vector3.left, 3f);
            for (int i = 0; i < 40; i++) solver.Step(Dt);
            CarSoftLattice.WriteVertices(binds, solver, baseVerts, Vector3.one, work);

            float worstNode = 0f;
            for (int i = 0; i < solver.NodeCount; i++)
                worstNode = Mathf.Max(worstNode, solver.Displacement(i).magnitude);
            float worstVert = 0f;
            bool anyMoved = false;
            for (int i = 0; i < work.Length; i++)
            {
                float d = (work[i] - baseVerts[i]).magnitude;
                worstVert = Mathf.Max(worstVert, d);
                if (d > 1e-6f) anyMoved = true;
            }
            Bool("a hit moves the mesh", anyMoved && worstNode > 1e-4f, "");
            Bool("no vertex outruns its nodes", worstVert <= worstNode * 1.001f,
                 "weights are a convex combination — a vertex past its nodes means " +
                 "the weights stopped summing to one");

            // The render-scale conversion: half-scale author units mean a dent
            // twice as large in author numbers, identical in metres.
            var scaled = (Vector3[])baseVerts.Clone();
            for (int i = 0; i < scaled.Length; i++) scaled[i] *= 2f;   // author = 2× metres
            var work2 = (Vector3[])scaled.Clone();
            CarSoftLattice.WriteVertices(binds, solver, scaled, Vector3.one * 0.5f, work2);
            float wantAuthor = worstVert / 0.5f;
            float gotAuthor = 0f;
            for (int i = 0; i < work2.Length; i++)
                gotAuthor = Mathf.Max(gotAuthor, (work2[i] - scaled[i]).magnitude);
            Check("render scale converts the dent, not just the mesh", gotAuthor, wantAuthor,
                  wantAuthor * 1e-4f, "au",
                  "author units through the child's localScale — the body_patrol 12.573× " +
                  "trap, pre-empted this time");

            // The chunk boundary, exactly.
            Bool("six of ten broken detaches", CarSoftLattice.ShouldDetach(6, 10), "");
            Bool("five of ten does not", !CarSoftLattice.ShouldDetach(5, 10), "");
            Bool("an unsupported channel never detaches", !CarSoftLattice.ShouldDetach(0, 0),
                 "no support set means no evidence, not a free pass");
            Bool("a one-beam channel detaches when it breaks", CarSoftLattice.ShouldDetach(1, 1), "");
            Line("");
        }

        // ---- 8. cost ---------------------------------------------------------------

        private static void Cost()
        {
            BoxFrame(out var nodes, out var beams, out _);
            using var s = new LatticeSolver(nodes, beams);

            var sw = new System.Diagnostics.Stopwatch();
            const int steps = 4000;
            sw.Start();
            for (int i = 0; i < steps; i++)
            {
                if (i % 400 == 0)   // keep it awake — asleep would bench a branch
                    s.ApplyHit(new Vector3(0.1f, 0.03f, 0.02f), Vector3.left, 1.5f);
                s.Step(Dt);
            }
            sw.Stop();
            float usPerStep = (float)(sw.Elapsed.TotalMilliseconds * 1000.0 / steps);
            Line($"cost: {s.NodeCount} nodes, {s.BeamCount} beams, S = {s.Substeps} " +
                 $"→ {usPerStep:0.0} us per awake step (editor Mono; a build is faster)");
            Bool("an awake step fits the 400 Hz budget", usPerStep < 1500f,
                 "2.5 ms per fixed step total; the lattice may take a slice while ringing, " +
                 "not the meal — and it is asleep in a second");
            Line("");
        }

        // ---- harness ---------------------------------------------------------------

        private static void Check(string name, float got, float expect, float tol,
                                  string units, string why)
        {
            _checks++;
            bool ok = Mathf.Abs(got - expect) <= tol;
            if (!ok)
            {
                _failed++;
                Debug.LogError($"{Tag} FAIL {name,-42} {got:0.#####} {units}  " +
                               $"(expect {expect:0.#####} ±{tol:0.#####})" +
                               (why.Length > 0 ? $"  — {why}" : ""));
            }
            else _log.AppendLine($"{Tag} ok   {name,-42} {got:0.#####} {units}");
        }

        private static void Bool(string name, bool ok, string why)
        {
            _checks++;
            if (!ok)
            {
                _failed++;
                Debug.LogError($"{Tag} FAIL {name,-42}" + (why.Length > 0 ? $"  — {why}" : ""));
            }
            else _log.AppendLine($"{Tag} ok   {name,-42}" + (why.Length > 0 ? $"  — {why}" : ""));
        }

        private static void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
