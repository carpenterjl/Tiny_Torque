using AIHWSim.BodyEd;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The crash frame's point-mass simulation: semi-implicit Euler over flat
    /// arrays, in the CAR'S LOCAL FRAME — the car's own rigid motion never
    /// enters the equations, so driving fast is not a hit. Pure C#: no
    /// MonoBehaviour, no Time reads, dt comes from the caller — which is what
    /// makes every claim below a bench check instead of an argument.
    ///
    /// <b>Deterministic by construction.</b> No randomness, fixed iteration
    /// order, forces accumulated into their own array before any integration.
    /// The same lattice fed the same hit sequence produces the same positions
    /// bit for bit — the property LAN dent-sync rests on.
    ///
    /// <b>Stability is arithmetic, not hope.</b> Semi-implicit Euler on a spring
    /// is stable while ω·h &lt; 2; this solver targets ω·h ≤ 0.5 (4× margin) by
    /// choosing its substep count from the stiffest node at build time:
    /// S = ceil(ω_max·dt/0.5). Past <see cref="MaxSubsteps"/> it rescales every
    /// spring by (MaxSubsteps/S)² instead — the simulator would rather be softer
    /// than unstable, and that rescale IS the "limited by the fidelity of the
    /// simulator" knob made explicit.
    ///
    /// <b>Dents are the plastic state, not the elastic one.</b> Every node is
    /// anchored to a plastic rest target <c>restPos</c> (critically damped,
    /// ω = 20 rad/s — two orders below the beams, so it never changes the
    /// substep count). A hit's elastic part rings down against that anchor in
    /// ~0.25 s; strain past the yield threshold moves the rest state itself,
    /// permanently. Anchoring to the ORIGINAL rest would spring every dent back
    /// out; no anchor at all would let injected momentum translate the whole
    /// lattice for ever, since a rigid translation has zero beam strain.
    ///
    /// <b>The inner loops are Burst jobs.</b> Beam forces are GATHERED per node
    /// through a CSR adjacency built once — each node walks its own beams and
    /// computes the shared force from its own side, so nothing is ever scattered
    /// into a neighbour and the parallel schedule has no races to lose. The two
    /// sides of a beam differ only by exact float negations, so the force is
    /// still exactly antisymmetric and momentum is still conserved to the bit.
    /// Every job is <c>FloatMode.Strict</c>: no reassociation, no fast-math
    /// reciprocals — the same reason the hits are quantized, since a LAN peer
    /// must reproduce this arithmetic, not merely approximate it.
    ///
    /// Native memory is persistent, so the solver is <see cref="IDisposable"/>
    /// and every owner disposes it: the car on destroy, the studio preview on
    /// repair, each bench fixture in a <c>using</c>.
    /// </summary>
    public sealed class LatticeSolver : System.IDisposable
    {
        // ---- tuning (public consts so the bench pins them) -----------------------

        /// <summary>Substep budget per 400 Hz step. Above this, springs soften.</summary>
        public const int MaxSubsteps = 8;

        /// <summary>Target ω·h per substep — 4× inside the stability bound.</summary>
        public const float TargetOmegaH = 0.5f;

        /// <summary>Hard cap on any node's speed, m/s. A crash visual, not a
        /// ballistics one.</summary>
        public const float MaxNodeSpeed = 4f;

        /// <summary>Hard cap on any node's displacement from its ORIGINAL rest,
        /// metres. 14 % of a 0.42 m car — a visible stove-in that cannot invert
        /// the mesh.</summary>
        public const float MaxDisplacement = 0.06f;

        /// <summary>Anchor natural frequency, rad/s.</summary>
        public const float AnchorOmega = 20f;

        /// <summary>Strain past which a beam yields, and the fraction of the
        /// excess that becomes permanent per outer step. 0.18, not 0.10: the
        /// surface lattice's fine cells carry ~8 mm beams, where a parking
        /// tap's millimetre of ring is already >10 % strain — the threshold
        /// has to clear the smallest legal contact on the SHORTEST generated
        /// beam, and the [LATT] tap check pins exactly that.</summary>
        public const float YieldStrain = 0.18f;
        public const float YieldFlow = 0.5f;

        /// <summary>
        /// The fraction of a beam's break strain that its PERMANENT stretch may
        /// reach before it tears — ductility spent rather than a snap.
        ///
        /// It has to be well under 1, and the reason is geometric: the lattice
        /// is a surface shell, so a hit pushes roughly PERPENDICULAR to the
        /// beams it crushes, and a perpendicular offset Δ across a beam of
        /// length L only lengthens it by about Δ²/2L — second order. A dent deep
        /// enough to fold a panel in half is nowhere near 35 % of stretch. The
        /// old three-node injection reached the snap limit only because it drove
        /// one node 60 mm while its neighbours sat still; spreading the crush
        /// (which is the whole point of the kernel) took that away, and with it
        /// every chunk detachment on a default car.
        /// </summary>
        public const float DuctileFraction = 0.45f;

        /// <summary>Elastic offset past which a node's rest target follows it.
        /// 4 mm on a 0.42 m car: a full-force hit (2.5 m/s against a ~250 rad/s
        /// node ⇒ ~10 mm swing) banks millimetres of visible dent, while the
        /// sub-millimetre swing of a parking tap stays entirely elastic.</summary>
        public const float NodeYieldM = 0.004f;

        /// <summary>Per-hit caps: node speed injected, and total kinetic energy —
        /// 0.5 J is the whole car's KE at 0.8 m/s. Crumples, never explodes.
        /// These are the DEFAULT-damage values; <see cref="SetDamageScale"/>
        /// scales the working copies.</summary>
        public const float MaxHitNodeSpeed = 3.5f;
        public const float MaxHitEnergyJ = 0.5f;

        /// <summary>The mass a hit's impulse is divided by to become node
        /// velocity — a slab of shell, NOT the individual nodes. Dividing by a
        /// gram-scale node mass would saturate <see cref="MaxHitNodeSpeed"/> on
        /// the smallest contact the injection layer lets through, making a
        /// parking tap and a head-on crash the same dent; a fixed effective
        /// mass also makes the response independent of the fidelity slider.</summary>
        public const float HitEffectiveMassKg = 0.1f;

        /// <summary>
        /// The impact kernel's radius, in MEAN BEAM LENGTHS:
        /// R = meanBeamLen · (Base + PerRootNs·√J). Beam-length units are what
        /// make a crash look the same at every fidelity — a finer lattice puts
        /// more nodes inside the same physical dent, not a smaller dent.
        ///
        /// <b>√J is where the car's momentum goes.</b> Node speed saturates at
        /// <see cref="MaxHitNodeSpeed"/> — beyond ≈0.35 N·s every hit injects
        /// the same peak velocity — so without this a 16 N·s head-on and a firm
        /// shove made the identical dent. Extra impulse now buys AREA: a tap
        /// dimples two or three beams, a wall at speed crushes seven-odd
        /// beam-lengths of nose. √ rather than linear because crush area is what
        /// grows with momentum, and because it keeps the response monotone
        /// without letting one big number swallow the whole car.
        /// </summary>
        public const float HitRadiusBase = 2.5f;
        public const float HitRadiusPerRootNs = 1.2f;

        /// <summary>Ceiling on the kernel, as a fraction of the lattice's
        /// largest extent — one contact may not shove the entire car.</summary>
        public const float MaxHitRadiusFrac = 0.35f;

        /// <summary>Sleep: total KE below this for <see cref="SleepAfterSteps"/>
        /// consecutive outer steps with no breaks. On sleep the residual
        /// elastic offset collapses onto the plastic rest — the dent stays, the
        /// unfinished ring dies — see <see cref="OuterStep"/> for why an offset
        /// threshold cannot work and why the collapse runs in that direction.</summary>
        public const float SleepEnergyJ = 1e-6f;
        public const int SleepAfterSteps = 20;

        /// <summary>On sleep, a plastic rest within this of PRISTINE snaps all
        /// the way back, so repeated marginal contacts cannot bank
        /// micro-dents for ever and an undamaged region is bit-identical to
        /// never having been hit — which is also what keeps the mesh write
        /// skippable. Well under the dents a real hit leaves (millimetres).</summary>
        public const float SleepSnapM = 2e-4f;

        // ---- state ---------------------------------------------------------------

        private NativeArray<float3> _pos, _vel, _force;
        private NativeArray<float3> _restPos0;  // original rest — the vertex-delta reference
        private NativeArray<float3> _restPos;   // plastic rest — the anchor target
        private NativeArray<float> _mass, _invMass;

        private NativeArray<int> _ba, _bb;
        private NativeArray<float> _k, _damp, _restLen, _restLen0, _breakStrain;
        private NativeArray<bool> _broken;

        /// <summary>Node → beams, CSR. Beams appear at BOTH endpoints and in
        /// ascending beam index, so a node sums its forces in exactly the order
        /// the single-threaded version did.</summary>
        private NativeArray<int> _nodeBeamStart, _nodeBeamIdx;

        /// <summary>The outer step's reduction, read back once per Step:
        /// 0 = kinetic energy, 1 = plastic flowed, 2 = beams broken.</summary>
        private NativeArray<float> _outer;

        private bool _disposed;

        // True counts. The native arrays are allocated at least one element
        // long (a zero-length NativeArray is legal but a needless special case
        // everywhere), so the arrays' own Length is not the answer.
        private readonly int _n, _b;

        private int _substeps = -1;             // resolved on the first Step
        private float _omegaMax;
        private int _stillSteps;

        // Damage-scaled working copies of the response constants — see
        // SetDamageScale. Defaults are the consts themselves (scale 1).
        private float _hitSpeedCap = MaxHitNodeSpeed;
        private float _hitEnergyCap = MaxHitEnergyJ;
        private float _yieldStrain = YieldStrain;
        private float _nodeYieldM = NodeYieldM;
        private float _breakDiv = 1f;
        private float _radiusScale = 1f;

        // Impact-kernel geometry, both fixed at build: the beam length radii are
        // measured in, and the ceiling one contact may reach.
        private float _meanBeamLen = 0.05f;
        private float _maxHitRadius = 0.2f;
        private readonly float[] _hitW;         // kernel scratch, reused, never grows

        public int NodeCount => _n;
        public int BeamCount => _b;
        public bool Asleep { get; private set; } = true;   // nothing has hit it yet

        /// <summary>Set on the outer step in which any plastic flow happened —
        /// the mesh-write path recalculates normals only then.</summary>
        public bool PlasticThisStep { get; private set; }

        /// <summary>Beams that snapped in the last outer step.</summary>
        public int BrokenThisStep { get; private set; }

        public int BrokenTotal { get; private set; }

        /// <summary>True when the substep budget forced a stiffness rescale —
        /// the caller logs it once.</summary>
        public bool StiffnessRescaled { get; private set; }

        /// <summary>How many times the NaN guard fired (should be never; the
        /// reset keeps a corrupted lattice from painting garbage).</summary>
        public int NaNResets { get; private set; }

        /// <summary>
        /// The design's damage amount (the FRAME tab slider, through
        /// <c>LatticeBuilder.DamageScale</c>): scales how hard a hit lands AND
        /// how easily it sticks. Above 1, hits inject more speed and the yield
        /// and break thresholds drop by the same factor; below 1 the reverse —
        /// at 0.1× a wall crash stays elastic and springs back out.
        ///
        /// Touches only hit injection and the plastic/break thresholds — never
        /// k, m or damping — so the substep count, ω_max and the stability
        /// contract are untouched by the slider. Comes from the layout JSON, so
        /// every LAN peer scales identically and dent-sync determinism holds.
        /// </summary>
        public void SetDamageScale(float scale)
        {
            float s = Mathf.Clamp(scale, 0.01f, 100f);
            _hitSpeedCap = Mathf.Min(MaxHitNodeSpeed * s, MaxNodeSpeed);
            _hitEnergyCap = MaxHitEnergyJ * s * s;
            _yieldStrain = YieldStrain / s;
            _nodeYieldM = NodeYieldM / s;
            _breakDiv = s;
            // Deeper AND wider: √s, so 10× damage crushes a ~3× broader area
            // rather than punching a deeper hole of the same width. Radius is
            // geometry, not stiffness — the substep contract is untouched.
            _radiusScale = Mathf.Sqrt(s);
        }

        /// <summary>The kernel radius a given impulse reaches, metres — public
        /// because it is a claim the bench pins, not an implementation detail.
        /// Derived from the QUANTIZED impulse the caller applies, so a LAN peer
        /// computes the identical number.</summary>
        public float HitRadius(float impulse)
        {
            float r = _meanBeamLen
                      * (HitRadiusBase + HitRadiusPerRootNs * Mathf.Sqrt(Mathf.Max(impulse, 0f)))
                      * _radiusScale;
            return Mathf.Clamp(r, _meanBeamLen * 0.75f, _maxHitRadius);
        }

        public float MeanBeamLength => _meanBeamLen;

        public bool IsBroken(int beam) => _broken[beam];
        public int BeamA(int beam) => _ba[beam];
        public int BeamB(int beam) => _bb[beam];
        public int Substeps => _substeps;
        public float OmegaMax => _omegaMax;

        /// <summary>A node's offset from its ORIGINAL rest — what the mesh
        /// write applies, so zero lattice motion is bit-zero vertex delta.</summary>
        public Vector3 Displacement(int node) => _pos[node] - _restPos0[node];

        /// <summary>A node's permanent offset — the dent without the ring.</summary>
        public Vector3 PlasticOffset(int node) => _restPos[node] - _restPos0[node];

        /// <summary>A node's velocity — the bench reads the impact kernel
        /// straight off it, before any stepping blurs the picture.</summary>
        public Vector3 Velocity(int node) => _vel[node];

        public float KineticEnergy()
        {
            float e = 0f;
            for (int i = 0; i < _n; i++) e += 0.5f * _mass[i] * math.lengthsq(_vel[i]);
            return e;
        }

        // ---- construction --------------------------------------------------------

        /// <summary>
        /// Build from the stored lattice. Sentinels resolve through
        /// <see cref="LatticeBuilder"/> — the one interpretation site — and the
        /// damper follows the house convention: c = 2ζ√(k·μ), reduced mass
        /// μ = mₐm_b/(mₐ+m_b), stored as a ratio and derived here
        /// (<c>CarVehicle.MakeWheel</c> does the same for the suspension).
        /// </summary>
        public LatticeSolver(LatticeNode[] nodes, LatticeBeam[] beams)
        {
            int n = nodes.Length, b = beams.Length;
            _n = n; _b = b;
            _pos = Nodes<float3>(n); _vel = Nodes<float3>(n); _force = Nodes<float3>(n);
            _restPos0 = Nodes<float3>(n); _restPos = Nodes<float3>(n);
            _mass = Nodes<float>(n); _invMass = Nodes<float>(n);

            float nAvg = n > 0 ? 2f * b / n : 1f;
            for (int i = 0; i < n; i++)
            {
                float3 p = nodes[i].localPos;
                _pos[i] = p; _restPos0[i] = p; _restPos[i] = p;
                float m = Mathf.Max(LatticeBuilder.ResolveMass(nodes[i].mass, n), 0.001f);
                _mass[i] = m;
                _invMass[i] = 1f / m;
            }

            _ba = Nodes<int>(b); _bb = Nodes<int>(b);
            _k = Nodes<float>(b); _damp = Nodes<float>(b);
            _restLen = Nodes<float>(b); _restLen0 = Nodes<float>(b);
            _breakStrain = Nodes<float>(b); _broken = Nodes<bool>(b);
            _outer = Nodes<float>(3);

            for (int i = 0; i < b; i++)
            {
                LatticeBeam beam = beams[i];
                _ba[i] = beam.a; _bb[i] = beam.b;
                float mu = _mass[beam.a] * _mass[beam.b] / (_mass[beam.a] + _mass[beam.b]);
                float k = LatticeBuilder.ResolveSpring(beam.spring, _mass[beam.a], nAvg);
                _k[i] = k;
                _damp[i] = 2f * LatticeBuilder.ResolveDampingRatio(beam.dampingRatio)
                           * Mathf.Sqrt(k * mu);
                float rest = Mathf.Max(LatticeBuilder.RestLength(nodes, beam), 1e-4f);
                _restLen[i] = rest; _restLen0[i] = rest;
                _breakStrain[i] = LatticeBuilder.ResolveBreakStrain(beam.breakStrain);
            }

            BuildAdjacency(n, b);

            // Impact-kernel geometry: radii are measured in mean beam lengths
            // (fidelity-invariant crashes), capped against the lattice's own
            // size (one contact never reaches the whole car).
            _hitW = new float[n];
            if (b > 0)
            {
                float sum = 0f;
                for (int i = 0; i < b; i++) sum += _restLen0[i];
                _meanBeamLen = Mathf.Max(sum / b, 1e-4f);
            }
            if (n > 0)
            {
                float3 lo = _restPos0[0], hi = _restPos0[0];
                for (int i = 1; i < n; i++)
                {
                    lo = math.min(lo, _restPos0[i]);
                    hi = math.max(hi, _restPos0[i]);
                }
                float3 ext = hi - lo;
                _maxHitRadius = Mathf.Max(math.cmax(ext) * MaxHitRadiusFrac, _meanBeamLen);
            }

            ComputeOmegaMax();
        }

        private static NativeArray<T> Nodes<T>(int len) where T : struct =>
            new NativeArray<T>(Mathf.Max(len, 1), Allocator.Persistent,
                               NativeArrayOptions.ClearMemory);

        /// <summary>
        /// The node → beam CSR the force job gathers through. Beams are listed
        /// at both endpoints, and within a node in ASCENDING BEAM INDEX — which
        /// is exactly the order the old single-threaded scatter accumulated
        /// them in, so summing here is not merely equivalent but identical.
        /// </summary>
        private void BuildAdjacency(int n, int b)
        {
            _nodeBeamStart = Nodes<int>(n + 1);
            _nodeBeamIdx = Nodes<int>(2 * b);

            var count = new int[n + 1];
            for (int i = 0; i < b; i++) { count[_ba[i]]++; count[_bb[i]]++; }
            int run = 0;
            for (int i = 0; i < n; i++) { _nodeBeamStart[i] = run; run += count[i]; }
            _nodeBeamStart[n] = run;

            var cursor = new int[n];
            for (int i = 0; i < n; i++) cursor[i] = _nodeBeamStart[i];
            for (int i = 0; i < b; i++)
            {
                _nodeBeamIdx[cursor[_ba[i]]++] = i;
                _nodeBeamIdx[cursor[_bb[i]]++] = i;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Free(ref _pos); Free(ref _vel); Free(ref _force);
            Free(ref _restPos0); Free(ref _restPos);
            Free(ref _mass); Free(ref _invMass);
            Free(ref _ba); Free(ref _bb);
            Free(ref _k); Free(ref _damp);
            Free(ref _restLen); Free(ref _restLen0);
            Free(ref _breakStrain); Free(ref _broken);
            Free(ref _nodeBeamStart); Free(ref _nodeBeamIdx);
            Free(ref _outer);
        }

        private static void Free<T>(ref NativeArray<T> a) where T : struct
        {
            if (a.IsCreated) a.Dispose();
            a = default;
        }

        private void ComputeOmegaMax()
        {
            // Conservative per-node bound: all of a node's springs pulling the
            // same way, plus the anchor. k and m never grow after build
            // (plasticity moves rest lengths, breaking removes springs), so this
            // is computed once and only ever becomes MORE conservative.
            var kSum = new float[Mathf.Max(_n, 1)];
            for (int i = 0; i < _b; i++)
            {
                if (_broken[i]) continue;
                kSum[_ba[i]] += _k[i]; kSum[_bb[i]] += _k[i];
            }
            _omegaMax = 0f;
            for (int i = 0; i < _n; i++)
            {
                float kAnchor = _mass[i] * AnchorOmega * AnchorOmega;
                _omegaMax = Mathf.Max(_omegaMax,
                                      Mathf.Sqrt((kSum[i] + kAnchor) * _invMass[i]));
            }
        }

        /// <summary>Substeps for this dt: S = ceil(ω_max·dt / 0.5). Resolved on
        /// the first step because dt is the caller's; past the budget, springs
        /// soften by (budget/S)² so the step that runs is always stable.</summary>
        private void EnsureSubsteps(float dt)
        {
            if (_substeps > 0) return;
            int s = Mathf.Max(1, Mathf.CeilToInt(_omegaMax * dt / TargetOmegaH));
            if (s > MaxSubsteps)
            {
                float scale = (float)MaxSubsteps / s;
                float k2 = scale * scale;
                for (int i = 0; i < _b; i++)
                {
                    _k[i] *= k2;
                    _damp[i] *= scale;   // c ∝ √k keeps ζ unchanged
                }
                StiffnessRescaled = true;
                ComputeOmegaMax();
                s = MaxSubsteps;
            }
            _substeps = s;
        }

        // ---- stepping ------------------------------------------------------------

        /// <summary>
        /// Advance one fixed step. Returns true when anything moved — the
        /// caller's cue to rewrite mesh vertices. Asleep, it is one branch:
        /// most frames have no contacts, and a lattice that has rung down costs
        /// nothing until the next hit.
        /// </summary>
        public bool Step(float dt)
        {
            PlasticThisStep = false;
            BrokenThisStep = 0;
            if (dt <= 0f) return false;
            // Resolved even while asleep — one early-outing branch — so the
            // substep count and any stiffness rescale exist from the first
            // step, not the first crash.
            EnsureSubsteps(dt);
            if (Asleep) return false;

            float h = dt / _substeps;

            // The whole step as one dependency chain: force → integrate, S
            // times, then the outer reduction. Scheduled once, completed once —
            // the caller's API stays synchronous, but the per-node work spreads
            // across worker cores and runs as Burst-compiled native code.
            var force = new ForceJob
            {
                pos = _pos, vel = _vel, force = _force, mass = _mass,
                restPos = _restPos, ba = _ba, bb = _bb, k = _k, damp = _damp,
                restLen = _restLen, broken = _broken,
                start = _nodeBeamStart, idx = _nodeBeamIdx, h = h,
            };
            var integrate = new IntegrateJob
            {
                pos = _pos, vel = _vel, force = _force, invMass = _invMass,
                restPos0 = _restPos0, h = h,
            };

            JobHandle dep = default;
            for (int s = 0; s < _substeps; s++)
            {
                dep = force.ScheduleParallel(_n, JobBatch, dep);
                dep = integrate.ScheduleParallel(_n, JobBatch, dep);
            }
            dep = new OuterJob
            {
                pos = _pos, vel = _vel, mass = _mass, restPos = _restPos,
                ba = _ba, bb = _bb, restLen = _restLen, restLen0 = _restLen0,
                breakStrain = _breakStrain, broken = _broken,
                beamCount = _b, nodeCount = _n,
                yieldStrain = _yieldStrain, nodeYield = _nodeYieldM, breakDiv = _breakDiv,
                outResults = _outer,
            }.Schedule(dep);
            dep.Complete();

            float ke = _outer[0];
            PlasticThisStep = _outer[1] != 0f;
            BrokenThisStep = (int)_outer[2];
            BrokenTotal += BrokenThisStep;

            // NaN guard: one accumulated float. A corrupted lattice resets to
            // rest and says so once, rather than painting garbage vertices.
            if (float.IsNaN(ke))
            {
                ResetToRest();
                NaNResets++;
                return true;
            }

            SleepCheck(ke);
            return true;
        }

        /// <summary>Nodes per worker chunk. Large enough that the scheduling
        /// overhead is noise on a small lattice, small enough that a 1 400-node
        /// frame still fills every core.</summary>
        private const int JobBatch = 64;

        /// <summary>
        /// Beam forces, GATHERED per node. Each node walks its own beams and
        /// computes the shared force from its own side — no scatter, so nothing
        /// races, and the two sides differ only by exact float negations
        /// (d → −d ⇒ n → −n, and dot(−u, −n) is bit-identical to dot(u, n)), so
        /// the pair is still exactly equal-and-opposite. The node's anchor force
        /// is added here too, since it depends on nothing but the node.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        private struct ForceJob : IJobFor
        {
            [ReadOnly] public NativeArray<float3> pos, vel, restPos;
            [ReadOnly] public NativeArray<float> mass, k, damp, restLen;
            [ReadOnly] public NativeArray<int> ba, bb, start, idx;
            [ReadOnly] public NativeArray<bool> broken;
            [WriteOnly] public NativeArray<float3> force;
            public float h;

            public void Execute(int i)
            {
                float3 f = float3.zero;
                float3 pi = pos[i], vi = vel[i];
                int e = start[i + 1];
                for (int c = start[i]; c < e; c++)
                {
                    int beam = idx[c];
                    if (broken[beam]) continue;
                    int a = ba[beam], b = bb[beam];
                    int other = a == i ? b : a;

                    float3 d = pos[other] - pi;
                    float len = math.length(d);
                    if (len < 1e-6f) continue;
                    float3 n = d / len;

                    float fs = k[beam] * (len - restLen[beam]);
                    float fd = damp[beam] * math.dot(vel[other] - vi, n);
                    // Beam force cap: one substep may add at most half the speed
                    // cap to the lightest node a beam touches — the "very simple
                    // force propagation model" bound, stated as arithmetic.
                    float fCap = math.min(mass[a], mass[b]) * (0.5f * MaxNodeSpeed) / h;
                    f += math.clamp(fs + fd, -fCap, fCap) * n;
                }

                float m = mass[i];
                float kAnchor = m * AnchorOmega * AnchorOmega;
                float cAnchor = 2f * m * AnchorOmega;   // critically damped
                force[i] = f + (-kAnchor * (pi - restPos[i]) - cAnchor * vi);
            }
        }

        /// <summary>Semi-implicit Euler, one node per work item. Every array
        /// element is written by exactly one item from a read-only snapshot, so
        /// the parallel schedule cannot reach the result.</summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        private struct IntegrateJob : IJobFor
        {
            public NativeArray<float3> pos, vel;
            [ReadOnly] public NativeArray<float3> force, restPos0;
            [ReadOnly] public NativeArray<float> invMass;
            public float h;

            public void Execute(int i)
            {
                float3 v = vel[i] + force[i] * (invMass[i] * h);
                float speed = math.length(v);
                if (speed > MaxNodeSpeed) v *= MaxNodeSpeed / speed;
                float3 p = pos[i] + v * h;

                // Displacement clamp about the ORIGINAL rest; on clamp, the
                // outward velocity component dies so the node does not grind
                // against its own leash.
                float3 off = p - restPos0[i];
                float d2 = math.lengthsq(off);
                if (d2 > MaxDisplacement * MaxDisplacement)
                {
                    float d = math.sqrt(d2);
                    float3 outward = off / d;
                    p = restPos0[i] + outward * MaxDisplacement;
                    float vOut = math.dot(v, outward);
                    if (vOut > 0f) v -= outward * vOut;
                }
                vel[i] = v;
                pos[i] = p;
            }
        }

        /// <summary>
        /// Plastic flow, breaking and the kinetic-energy total — a reduction
        /// over both beams and nodes, so it stays a single work item. Burst
        /// still buys it: it is the same arithmetic compiled natively.
        /// Monotone by construction: rest lengths only chase strain, rest
        /// positions only chase displacement, broken never unbreaks.
        ///
        /// A beam breaks on EITHER instantaneous strain past its break strain
        /// (a snap) or permanent stretch past it (ductility spent) — see the
        /// second test for why the first alone stopped being enough.
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        private struct OuterJob : IJob
        {
            [ReadOnly] public NativeArray<float3> pos, vel;
            [ReadOnly] public NativeArray<float> mass, restLen0, breakStrain;
            [ReadOnly] public NativeArray<int> ba, bb;
            public NativeArray<float3> restPos;
            public NativeArray<float> restLen;
            public NativeArray<bool> broken;
            [WriteOnly] public NativeArray<float> outResults;
            public int beamCount, nodeCount;
            public float yieldStrain, nodeYield, breakDiv;

            public void Execute()
            {
                float plastic = 0f;
                int brokeNow = 0;
                for (int i = 0; i < beamCount; i++)
                {
                    if (broken[i]) continue;
                    float len = math.length(pos[bb[i]] - pos[ba[i]]);
                    float strain = (len - restLen[i]) / restLen0[i];
                    float mag = math.abs(strain);

                    float limit = breakStrain[i] / breakDiv;
                    if (mag > limit)
                    {
                        broken[i] = true;
                        brokeNow++;
                        continue;
                    }
                    if (mag > yieldStrain)
                    {
                        float excess = (mag - yieldStrain) * math.sign(strain);
                        restLen[i] += YieldFlow * excess * restLen0[i];
                        plastic = 1f;
                    }

                    // Ductility is finite. A beam whose PERMANENT stretch has
                    // passed DuctileFraction of its break strain has nothing
                    // left to give, even if it never saw that much strain at any
                    // one instant — and a wide impact kernel loads slowly enough
                    // that it never would.
                    if (math.abs(restLen[i] - restLen0[i]) / restLen0[i]
                        > limit * DuctileFraction)
                    {
                        broken[i] = true;
                        brokeNow++;
                    }
                }

                float ke = 0f;
                for (int i = 0; i < nodeCount; i++)
                {
                    float3 off = pos[i] - restPos[i];
                    float d = math.length(off);
                    if (d > nodeYield)
                    {
                        restPos[i] += off * (YieldFlow * (d - nodeYield) / d);
                        plastic = 1f;
                    }
                    ke += 0.5f * mass[i] * math.lengthsq(vel[i]);
                }

                outResults[0] = ke;
                outResults[1] = plastic;
                outResults[2] = brokeNow;
            }
        }

        /// <summary>
        /// The sleep decision, and the one-off collapse when it fires. Managed
        /// rather than jobbed on purpose: it is a branch on a single float
        /// almost every step, and the loop inside runs once per crash.
        /// </summary>
        private void SleepCheck(float ke)
        {
            // Sleep on KINETIC ENERGY alone — not on offset, and not on plastic
            // quiet. Plastically shifted rests reach equilibria that satisfy no
            // offset threshold, and the flow itself decays asymptotically (each
            // step takes half the excess), so "no plastic event" is a condition
            // a vanishing, invisible tail can hold off for ever. If the lattice
            // has been visually still for 20 steps, it IS still — so collapse
            // the elastic residue ONTO the plastic rest and stop paying for the
            // tail. The direction matters: the surface lattice's soft low-
            // valence modes creep slowly enough that KE dies with a millimetre
            // of elastic offset left, and freezing the rest AT the residue
            // (the other direction) turned every parking tap into a permanent
            // millimetre dent. The dent is the plastic rest; the residue is
            // ring that had not finished dying, and it dies here.
            if (ke < SleepEnergyJ && BrokenThisStep == 0)
            {
                if (++_stillSteps >= SleepAfterSteps)
                {
                    for (int i = 0; i < _n; i++)
                    {
                        // A rest within a fifth of a millimetre of pristine IS
                        // pristine — without this, repeated marginal contacts
                        // bank micro-dents for ever, and an undamaged region
                        // stops being bit-identical to never having been hit.
                        float3 rest = _restPos[i];
                        if (math.lengthsq(rest - _restPos0[i]) < SleepSnapM * SleepSnapM)
                            rest = _restPos0[i];
                        _restPos[i] = rest;
                        _pos[i] = rest;
                        _vel[i] = float3.zero;
                    }
                    Asleep = true;
                }
            }
            else _stillSteps = 0;
        }

        // ---- hits ----------------------------------------------------------------

        /// <summary>
        /// Inject one contact. Point and direction are car-local (the caller
        /// quantized them to wire precision first, so LAN peers replay the same
        /// bytes); impulse in N·s.
        ///
        /// <b>The hit is a field, not three pokes.</b> Every node inside
        /// <see cref="HitRadius"/> of the contact takes velocity
        /// <c>v_peak·w(d)</c> with the quartic bump <c>w = (1 − (d/R)²)²</c> —
        /// full push at the contact, smoothly nothing at R, so the crush spreads
        /// through the panel instead of denting the three vertices under the
        /// bumper and asking the beams to carry the rest (the anchor and dampers
        /// kill that ring within a few centimetres, which is exactly what "it
        /// doesn't propagate" looked like). R grows with √impulse — see
        /// <see cref="HitRadiusPerRootNs"/> for why that is where the car's
        /// momentum ends up.
        ///
        /// Distances are measured against the PRISTINE rest positions, so a
        /// second hit on an already-crumpled panel picks the same node set a
        /// peer replaying the same bytes would. Injections are pure additions to
        /// velocity, so simultaneous contacts <b>superpose exactly</b> — two
        /// hits in one step are the vector sum of each applied alone.
        /// </summary>
        public void ApplyHit(Vector3 pointLocal, Vector3 dirLocal, float impulse)
        {
            if (_n == 0) return;
            Vector3 dir = dirLocal.sqrMagnitude > 1e-8f ? dirLocal.normalized : Vector3.down;

            float r = HitRadius(impulse);
            float invR2 = 1f / (r * r);
            float vPeak = Mathf.Min(impulse / HitEffectiveMassKg, _hitSpeedCap);

            // Pass 1: the kernel, and the energy it would inject. Index order,
            // no early exit — the same arithmetic in the same order everywhere.
            float energy = 0f;
            int nearest = 0;
            float nearestD2 = float.MaxValue;
            float3 point = pointLocal;
            for (int i = 0; i < _n; i++)
            {
                float d2 = math.lengthsq(_restPos0[i] - point);
                if (d2 < nearestD2) { nearestD2 = d2; nearest = i; }
                float t = 1f - d2 * invR2;
                if (t <= 0f) { _hitW[i] = 0f; continue; }
                float w = t * t;
                _hitW[i] = w;
                float v = vPeak * w;
                energy += 0.5f * _mass[i] * v * v;
            }

            // A contact that lands outside the frame entirely (a lattice that
            // does not cover the collider) still has to do something: the
            // nearest node takes the whole hit.
            if (energy <= 0f)
            {
                _hitW[nearest] = 1f;
                energy = 0.5f * _mass[nearest] * vPeak * vPeak;
            }

            // Pass 2: one global scale so however hard the world hits, the
            // lattice crumples rather than explodes. With a wide kernel of
            // gram-scale nodes this rarely binds — it is the pathological-case
            // net, not the shaping term.
            float scale = energy > _hitEnergyCap ? Mathf.Sqrt(_hitEnergyCap / energy) : 1f;
            float3 push = dir * (vPeak * scale);
            for (int i = 0; i < _n; i++)
                if (_hitW[i] > 0f) _vel[i] = _vel[i] + push * _hitW[i];

            Asleep = false;
            _stillSteps = 0;
        }

        /// <summary>Back to the pristine shape — the NaN guard's exit, and the
        /// bench's way to reuse one solver across scenarios.</summary>
        public void ResetToRest()
        {
            for (int i = 0; i < _n; i++)
            {
                float3 p = _restPos0[i];
                _pos[i] = p; _restPos[i] = p;
                _vel[i] = float3.zero;
            }
            for (int i = 0; i < _b; i++)
            {
                _restLen[i] = _restLen0[i];
                _broken[i] = false;
            }
            BrokenTotal = 0;
            Asleep = true;
        }
    }
}
