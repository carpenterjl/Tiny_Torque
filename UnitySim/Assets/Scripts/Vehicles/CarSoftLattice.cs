using System.Collections.Generic;
using AIHWSim.BodyEd;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The crash frame on a driving car: contacts in, dents out.
    ///
    /// Sits on the car ROOT (Unity delivers collision callbacks to components on
    /// the rigidbody's GameObject — the <c>VehicleAudio</c>/<c>CarImpact</c>
    /// precedent), steps <see cref="LatticeSolver"/> from its own FixedUpdate at
    /// the scene's 400 Hz, and rewrites the deformed body mesh's vertices in
    /// LateUpdate on the frames the solver actually moved.
    ///
    /// <b>What it will never do</b>, because four physics gates and the LAN
    /// model depend on it: add or resize a collider on the car, touch mass, CoM
    /// or inertia, apply any force to the car's rigidbody, or write a
    /// <c>Time.*</c>/<c>Physics.*</c> global. It exists at all only when the
    /// design's layout carries a lattice — every pre-frame design, every physics
    /// test and the Opus mission build none, so for them this file might as well
    /// not exist.
    ///
    /// <b>Hits are quantized to wire precision BEFORE they are applied</b>, even
    /// offline: the record the local solver integrates is bit-identical to the
    /// one a LAN peer replays, so owner and ghost dent the same or the sync
    /// design is broken at the source.
    /// </summary>
    public sealed class CarSoftLattice : MonoBehaviour
    {
        /// <summary>One quantized contact — the unit of replay.</summary>
        public struct LatticeHit
        {
            /// <summary>Car-local metres, quantized to 1 mm.</summary>
            public Vector3 pointLocal;
            /// <summary>Car-local, components quantized to 1/127. Normalised by
            /// the solver, identically everywhere.</summary>
            public Vector3 dirLocal;
            /// <summary>N·s, quantized to 1/1024, ≤ 64.</summary>
            public float impulse;
        }

        /// <summary>Contacts below this never reach the lattice. A 1.6 kg car
        /// needs ≳0.5 m/s of normal closing speed to register.</summary>
        public const float MinImpulse = 0.02f;

        /// <summary>Per-collider debounce — the root box plus four wheels make
        /// one crash several callbacks (the CarImpact convention).</summary>
        public const float DebounceS = 0.08f;

        /// <summary>A contact whose normal is car-up is the box bottoming out on
        /// a kerb; it needs this multiple of the threshold to count as damage.</summary>
        public const float GroundSlamFactor = 3f;

        /// <summary>
        /// Contact points further apart than this belong to different impacts.
        /// A broadside touches nose and tail; averaging those into one point
        /// dented the middle of the car — a place nothing touched. 0.12 m is
        /// about a quarter of the body, so a single face's contact patch stays
        /// one source and two ends of the car never do.
        /// </summary>
        public const float ClusterRadiusM = 0.12f;

        /// <summary>Sources one collision may become. Three covers nose+tail,
        /// or a corner and a flank; past that the extra points merge into the
        /// nearest cluster rather than filling the LAN token bucket.</summary>
        public const int MaxHitSources = 3;

        /// <summary>Support beams broken before a feature channel lets go.</summary>
        public const float DetachFraction = 0.6f;

        /// <summary>Seconds between normal recalculations while denting — the
        /// visible shading catches up at 10 Hz, not 400.</summary>
        public const float NormalsPeriodS = 0.1f;

        /// <summary>Raised for every locally-sensed hit AFTER it is applied —
        /// the net layer forwards these to peers verbatim.</summary>
        public event System.Action<LatticeHit> HitApplied;

        private LatticeSolver _solver;
        private Mesh _mesh;                 // owned by CarVehicle, mutated here
        private Transform _deformedBody;
        private Rigidbody _body;            // read-only use, ever
        private Vector3 _renderScale;

        private Vector3[] _baseVerts;       // author units, pristine
        private Vector3[] _workVerts;       // scratch, reused
        private LatticeBuilder.VertexBinding[] _bindings;

        private int[][] _channelSupport;    // beam indices per submesh
        private bool[] _channelDetached;
        private Material[] _channelMats;
        private int[][] _channelTris;       // pristine triangles, for Repair
        private CarVehicle _car;

        // Contact-clustering scratch — allocated once, never inside a callback.
        private ContactPoint[] _contactBuf;
        private Vector3[] _ptBuf, _nrmBuf;
        private readonly Vector3[] _srcPoint = new Vector3[MaxHitSources];
        private readonly Vector3[] _srcNormal = new Vector3[MaxHitSources];
        private readonly int[] _srcCount = new int[MaxHitSources];

        private readonly Dictionary<Collider, float> _debounce = new Dictionary<Collider, float>();
        private float _lastGlobalHit = -1f;
        private float _lastNormals = -1f;
        private bool _meshDirty, _normalsOwed, _warnedRescale;
        private Vector3 _lastHitDirLocal = Vector3.forward;

        public LatticeSolver Solver => _solver;

        // ==================== construction ====================

        /// <summary>
        /// Called from <c>CarVehicle.BuildBodyVisual</c> on the deformed-body
        /// branch when the layout carries a lattice. The FBX-shell and primitive
        /// branches never reach it, which is what keeps shared catalogue meshes
        /// unmutated. Null when the lattice fails sanitation entirely.
        /// </summary>
        public static CarSoftLattice Attach(CarVehicle car, Transform deformedBody,
                                            Mesh mesh, VehicleLayoutData layout)
        {
            if (car == null || deformedBody == null || mesh == null
                || layout == null || !layout.HasLattice) return null;

            var nodes = layout.latticeNodes;
            var beams = layout.latticeBeams ?? System.Array.Empty<LatticeBeam>();
            int dropped = LatticeBuilder.Sanitize(nodes.Length, ref beams);
            if (dropped > 0)
                Debug.LogWarning($"[CarSoftLattice] Dropped {dropped} beams the layout " +
                                 "could not mean.");

            var lat = car.gameObject.AddComponent<CarSoftLattice>();
            lat._solver = new LatticeSolver(nodes, beams);
            // The design's damage amount — the same value the FRAME tab's
            // crash test previews, so the feel tuned there is the feel here.
            lat._solver.SetDamageScale(
                BodyEd.LatticeBuilder.DamageScale(layout.latticeDamage01));
            lat._mesh = mesh;
            lat._deformedBody = deformedBody;
            lat._body = car.GetComponent<Rigidbody>();
            lat._renderScale = deformedBody.localScale;

            if (deformedBody.localPosition.sqrMagnitude > 1e-8f)
                Debug.LogWarning("[CarSoftLattice] The deformed body is offset from the " +
                                 "car root; dents will land displaced by that offset.");

            mesh.MarkDynamic();
            lat._baseVerts = mesh.vertices;
            lat._workVerts = (Vector3[])lat._baseVerts.Clone();

            // Bindings in car-local metres, against the same vertices the mesh
            // renders — author units through the child's own render scale.
            var carLocal = new Vector3[lat._baseVerts.Length];
            for (int i = 0; i < carLocal.Length; i++)
                carLocal[i] = Vector3.Scale(lat._baseVerts[i], lat._renderScale);
            float spacing = layout.latticeSpacing > 1e-4f ? layout.latticeSpacing : 0.05f;
            lat._bindings = LatticeBuilder.BindVertices(nodes, carLocal, spacing);

            // Channel support for detachment, off the mesh's own submeshes.
            // The triangle arrays are kept — they are what Repair puts back.
            int subs = mesh.subMeshCount;
            var subTris = new int[subs][];
            for (int s = 0; s < subs; s++) subTris[s] = mesh.GetTriangles(s);
            lat._channelTris = subTris;
            lat._channelSupport = LatticeBuilder.ChannelSupport(beams, lat._bindings, subTris);
            lat._channelDetached = new bool[subs];
            var rend = deformedBody.GetComponent<MeshRenderer>();
            lat._channelMats = rend != null ? rend.sharedMaterials : new Material[subs];

            // The respawn key is also the panel beater: a reset puts the run
            // back to its start state, and a crumpled shell is run state.
            lat._car = car;
            car.VehicleReset += lat.Repair;

            // Culling bounds grow once by the whole crumple budget; never again.
            Bounds b = mesh.bounds;
            b.Expand(LatticeSolver.MaxDisplacement * 2f);
            mesh.bounds = b;
            return lat;
        }

        private void OnDestroy()
        {
            if (_car != null) _car.VehicleReset -= Repair;
            // The solver holds persistent native memory; a car leaving the
            // scene has to hand it back.
            _solver?.Dispose();
            _solver = null;
        }

        /// <summary>
        /// Undo every dent and reattach every lost chunk: solver back to
        /// pristine, vertices back to base, detached submeshes get their
        /// triangles again. Debris already flying keeps flying until its
        /// lifetime ends — it is cosmetic, not the car.
        /// </summary>
        public void Repair()
        {
            if (_solver == null) return;
            _solver.ResetToRest();
            System.Array.Copy(_baseVerts, _workVerts, _baseVerts.Length);
            _mesh.SetVertices(_workVerts);
            for (int ch = 0; ch < _channelDetached.Length; ch++)
            {
                if (!_channelDetached[ch]) continue;
                _channelDetached[ch] = false;
                _mesh.SetTriangles(_channelTris[ch], ch, false);
            }
            _mesh.RecalculateNormals();
            _meshDirty = false;
            _normalsOwed = false;
        }

        // ==================== stepping ====================

        private void FixedUpdate()
        {
            if (_solver == null) return;
            bool moved = _solver.Step(Time.fixedDeltaTime);
            if (!moved) return;

            _meshDirty = true;
            if (_solver.PlasticThisStep || _solver.BrokenThisStep > 0) _normalsOwed = true;
            if (_solver.BrokenThisStep > 0) CheckDetachments();
            if (_solver.StiffnessRescaled && !_warnedRescale)
            {
                _warnedRescale = true;
                Debug.Log("[CarSoftLattice] Frame stiffer than the step can carry — " +
                          "springs softened to stay stable.");
            }
            if (_solver.Asleep) _normalsOwed = true;   // settled dents get real shading
        }

        // ==================== contacts ====================

        private void OnCollisionEnter(Collision c)
        {
            if (_solver == null || Time.timeScale <= 0f) return;
            // Kinematic bodies are LAN followers and ghosts — they replay the
            // owner's hits through ApplyRemoteHit instead of sensing their own.
            if (_body == null || _body.isKinematic) return;

            float now = Time.time;
            if (now - _lastGlobalHit < 0.02f) return;
            if (_debounce.TryGetValue(c.collider, out float until) && now < until) return;

            int n = c.contactCount;
            if (n == 0) return;
            if (_contactBuf == null || _contactBuf.Length < n)
            {
                _contactBuf = new ContactPoint[Mathf.Max(n, 16)];
                _ptBuf = new Vector3[_contactBuf.Length];
                _nrmBuf = new Vector3[_contactBuf.Length];
            }
            c.GetContacts(_contactBuf);

            // Car-local up front: clustering distances are body distances, and
            // the hit record is car-local anyway.
            Matrix4x4 w2l = transform.worldToLocalMatrix;
            for (int i = 0; i < n; i++)
            {
                _ptBuf[i] = w2l.MultiplyPoint3x4(_contactBuf[i].point);
                _nrmBuf[i] = w2l.MultiplyVector(_contactBuf[i].normal);
            }
            int sources = Cluster(_ptBuf, _nrmBuf, n, _srcPoint, _srcNormal, _srcCount);

            float total = c.impulse.magnitude;
            if (total < 1e-6f)
            {
                Vector3 nAvg = Vector3.zero;
                for (int i = 0; i < sources; i++) nAvg += _srcNormal[i] * _srcCount[i];
                nAvg = transform.TransformDirection(nAvg);
                total = 0.5f * _body.mass
                        * Mathf.Abs(Vector3.Dot(c.relativeVelocity,
                                                nAvg.sqrMagnitude > 1e-8f
                                                    ? nAvg.normalized : Vector3.up));
            }

            // The box bottoming out on a kerb is driving, not crashing — judged
            // on the whole collision, so a broadside is never split into
            // sub-threshold crumbs by the clustering.
            Vector3 upNormal = Vector3.zero;
            for (int i = 0; i < sources; i++) upNormal += _srcNormal[i] * _srcCount[i];
            float threshold = upNormal.sqrMagnitude > 1e-8f
                              && Vector3.Dot(upNormal.normalized, Vector3.up) > 0.7f
                ? MinImpulse * GroundSlamFactor
                : MinImpulse;
            if (total < threshold) return;

            _lastGlobalHit = now;
            _debounce[c.collider] = now + DebounceS;

            // One collision, up to three hits — each its own quantized record,
            // applied and forwarded separately. The solver adds velocities, so
            // the superposition is exact; a single-contact collision produces
            // exactly one hit, as it always did.
            for (int i = 0; i < sources; i++)
            {
                float share = total * _srcCount[i] / n;
                LatticeHit hit = Quantize(_srcPoint[i], -_srcNormal[i], share);
                if (hit.impulse <= 0f) continue;
                Apply(hit);
                HitApplied?.Invoke(hit);
            }
        }

        /// <summary>
        /// Group contact points into at most <see cref="MaxHitSources"/> impact
        /// sources, in CAR-LOCAL space. Greedy and index-ordered: a point joins
        /// the first cluster whose running centroid is within
        /// <see cref="ClusterRadiusM"/>, else opens a new one; once the budget
        /// is full the remainder join
        /// the nearest existing cluster. Deterministic by construction (no
        /// sorting, no distance ties to break) and pure, so the bench pins it
        /// without a collision. Returns the cluster count; positions are the
        /// member centroids and normals the normalised member sums.
        /// </summary>
        public static int Cluster(Vector3[] points, Vector3[] normals, int count,
                                  Vector3[] outPoint, Vector3[] outNormal, int[] outCount)
        {
            int sources = 0;
            for (int i = 0; i < count; i++)
            {
                Vector3 p = points[i];
                Vector3 nrm = normals[i];

                int pick = -1;
                for (int c = 0; c < sources; c++)
                {
                    Vector3 seed = outPoint[c] / outCount[c];
                    if ((seed - p).sqrMagnitude <= ClusterRadiusM * ClusterRadiusM) { pick = c; break; }
                }
                if (pick < 0 && sources < MaxHitSources)
                {
                    pick = sources++;
                    outPoint[pick] = Vector3.zero;
                    outNormal[pick] = Vector3.zero;
                    outCount[pick] = 0;
                }
                if (pick < 0)
                {
                    // Budget full: nearest cluster, lower index wins a tie.
                    float best = float.MaxValue;
                    for (int c = 0; c < sources; c++)
                    {
                        float d = (outPoint[c] / outCount[c] - p).sqrMagnitude;
                        if (d < best) { best = d; pick = c; }
                    }
                }
                outPoint[pick] += p;
                outNormal[pick] += nrm;
                outCount[pick]++;
            }
            for (int c = 0; c < sources; c++)
            {
                outPoint[c] /= outCount[c];
                outNormal[c] = outNormal[c].sqrMagnitude > 1e-8f
                    ? outNormal[c].normalized
                    : Vector3.up;
            }
            return sources;
        }

        /// <summary>Wire precision, applied locally too — see the class note.</summary>
        public static LatticeHit Quantize(Vector3 pointLocal, Vector3 dirLocal, float impulse)
        {
            return new LatticeHit
            {
                pointLocal = new Vector3(
                    Mathf.Round(pointLocal.x * 1000f) / 1000f,
                    Mathf.Round(pointLocal.y * 1000f) / 1000f,
                    Mathf.Round(pointLocal.z * 1000f) / 1000f),
                dirLocal = new Vector3(
                    Mathf.Round(dirLocal.x * 127f) / 127f,
                    Mathf.Round(dirLocal.y * 127f) / 127f,
                    Mathf.Round(dirLocal.z * 127f) / 127f),
                impulse = Mathf.Min(Mathf.Round(impulse * 1024f) / 1024f, 64f),
            };
        }

        private void Apply(in LatticeHit hit)
        {
            _lastHitDirLocal = hit.dirLocal;
            _solver.ApplyHit(hit.pointLocal, hit.dirLocal, hit.impulse);
        }

        /// <summary>A peer's hit, already quantized by its sender.</summary>
        public void ApplyRemoteHit(in LatticeHit hit)
        {
            if (_solver == null) return;
            Apply(hit);
        }

        // ==================== the mesh ====================

        private void LateUpdate()
        {
            if (!_meshDirty || _solver == null) return;
            _meshDirty = false;

            WriteVertices(_bindings, _solver, _baseVerts, _renderScale, _workVerts);
            _mesh.SetVertices(_workVerts);

            if (_normalsOwed && Time.time - _lastNormals >= NormalsPeriodS)
            {
                _normalsOwed = false;
                _lastNormals = Time.time;
                _mesh.RecalculateNormals();
            }
        }

        /// <summary>
        /// Node displacements → vertex positions, static and allocation-free so
        /// the bench can pin it without a car: only BOUND vertices are touched
        /// (an unbound vertex stays bit-identical to base), the displacement is
        /// summed in car-local metres and converted back to author units through
        /// the render scale per axis.
        /// </summary>
        public static void WriteVertices(LatticeBuilder.VertexBinding[] bindings,
                                         LatticeSolver solver, Vector3[] baseVerts,
                                         Vector3 renderScale, Vector3[] work)
        {
            Vector3 inv = new Vector3(1f / renderScale.x, 1f / renderScale.y,
                                      1f / renderScale.z);
            foreach (LatticeBuilder.VertexBinding vb in bindings)
            {
                Vector3 d = solver.Displacement(vb.n0) * vb.w0;
                if (vb.n1 >= 0) d += solver.Displacement(vb.n1) * vb.w1;
                if (vb.n2 >= 0) d += solver.Displacement(vb.n2) * vb.w2;
                work[vb.vertex] = baseVerts[vb.vertex] + Vector3.Scale(d, inv);
            }
        }

        // ==================== chunks ====================

        /// <summary>The detach rule, pure so the bench can pin the boundary.
        /// Integer arithmetic — 3/5 IS 0.6, where <c>total * 0.6f</c> is
        /// 6.0000002 for ten beams and six broken would never detach.</summary>
        public static bool ShouldDetach(int brokenSupport, int totalSupport) =>
            totalSupport > 0 && brokenSupport * 5 >= totalSupport * 3;

        private void CheckDetachments()
        {
            for (int ch = 0; ch < _channelSupport.Length; ch++)
            {
                if (_channelDetached[ch]) continue;
                int[] support = _channelSupport[ch];
                if (support == null || support.Length == 0) continue;

                int broken = 0;
                foreach (int beam in support) if (_solver.IsBroken(beam)) broken++;
                if (!ShouldDetach(broken, support.Length)) continue;

                _channelDetached[ch] = true;
                DetachChannel(ch);
            }
        }

        /// <summary>
        /// Take one feature channel off the car: its triangles, at their CURRENT
        /// deformed positions, become a debris chunk, and the car's submesh goes
        /// empty — the exact mechanism hiding a channel uses, which is why the
        /// hole it leaves is also out of nothing (the chassis box is the
        /// collider, and it does not change).
        /// </summary>
        private void DetachChannel(int channel)
        {
            int[] tris = _mesh.GetTriangles(channel);
            if (tris.Length == 0) return;

            // Compact the used vertices into a mesh of their own.
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var newTris = new int[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                if (!remap.TryGetValue(tris[i], out int ni))
                {
                    ni = verts.Count;
                    remap[tris[i]] = ni;
                    verts.Add(_workVerts[tris[i]]);
                }
                newTris[i] = ni;
            }
            var chunk = new Mesh { name = "LatticeChunk" };
            chunk.SetVertices(verts);
            chunk.SetTriangles(newTris, 0);
            chunk.RecalculateNormals();
            chunk.RecalculateBounds();

            // Centroid velocity from the car, plus a pop along the last hit.
            Vector3 centroidLocal = chunk.bounds.center;
            Vector3 centroidWorld = _deformedBody.TransformPoint(centroidLocal);
            Vector3 vel = _body != null && !_body.isKinematic
                ? _body.GetPointVelocity(centroidWorld)
                : Vector3.zero;
            vel += transform.TransformDirection(_lastHitDirLocal).normalized * 1f;

            Material mat = channel < _channelMats.Length ? _channelMats[channel] : null;
            LatticeDebris.Spawn(chunk, mat, _deformedBody.position, _deformedBody.rotation,
                                _deformedBody.lossyScale, vel);

            _mesh.SetTriangles(System.Array.Empty<int>(), channel, false);
        }
    }
}
