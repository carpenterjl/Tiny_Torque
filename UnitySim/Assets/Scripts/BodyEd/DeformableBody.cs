using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>Which way a sculpt stroke pushes the surface.</summary>
    public enum SculptDir
    {
        /// <summary>Along the surface normal where the brush landed — inflate
        /// and deflate.</summary>
        SurfaceNormal,
        /// <summary>Straight up and down, whatever the surface is doing.</summary>
        Vertical,
        /// <summary>Across the car, for widening a flank.</summary>
        Lateral,
    }

    /// <summary>
    /// A car body the player can reshape at runtime: four procedural blendshape
    /// morphs and free-form vertex pulling, on one mesh, with the physics collider
    /// and the drag readout both taken from the same bake.
    ///
    /// <b>How the two deformation systems coexist.</b> A Unity blendshape frame is
    /// stored as a DELTA against the mesh's base vertices, and the renderer adds
    /// it at draw time. So the two systems occupy different storage and never
    /// contend:
    /// <list type="bullet">
    /// <item>free-form offsets are written into the base vertices
    /// (<c>mesh.vertices = base + offsets</c>), which leaves every blendshape
    /// frame valid because those frames were never absolute positions;</item>
    /// <item>morph weights ride on top through <c>SetBlendShapeWeight</c>;</item>
    /// <item>the shape you see is <c>base + offsets + Σ(wᵢ·Δᵢ)</c>, and
    /// <c>SkinnedMeshRenderer.BakeMesh</c> is exactly that sum — one call, feeding
    /// both the collider and the drag measurement, so those two cannot drift
    /// apart.</item>
    /// </list>
    ///
    /// <b>Why that makes save/load exact rather than approximate.</b> The morph
    /// targets are pure functions of the base mesh (see <see cref="BodyMorphs"/>),
    /// so a load can regenerate them bit-for-bit instead of storing them. What
    /// goes to disk is four weights and the vertices somebody actually pulled.
    ///
    /// <b>Why a bone-less SkinnedMeshRenderer.</b> Blendshapes are a skinned-mesh
    /// feature; a MeshRenderer cannot play one. With no bones and no bindposes the
    /// renderer draws at its own transform and applies only the blendshapes, which
    /// is the whole of what is wanted here. Nothing else in this project uses one,
    /// so it is worth saying plainly: this is a supported configuration, not a
    /// trick.
    ///
    /// <b>The commit rule.</b> Mesh work is cheap and happens live; physics work
    /// is not and happens once, when the mouse comes up. One condition in
    /// <see cref="Update"/> covers slider drags, sculpt strokes, loads and resets
    /// alike — see <see cref="DeformCommitted"/>.
    /// </summary>
    public sealed class DeformableBody : MonoBehaviour
    {
        // ---- what the reference RC car's wheels are, as fractions of body length --
        //
        // Taken from WheelSpec's own defaults at the nominal 0.42 m shell (the
        // same layout [DRAG] measures the catalogue against), expressed as ratios
        // so a body of any size gets a plausible set. They are markers and a drag
        // term, not suspension.
        private const float WheelRadiusPerLength = 0.0785714f;   // 0.033 / 0.42
        private const float HalfTrackPerLength = 0.1976190f;     // 0.083 / 0.42
        private const float HubDropPerLength = 0.1071429f;       // 0.045 / 0.42
        private const float WheelbasePerLength = 0.7238095f;     // 0.304 / 0.42
        private const float WheelWidthPerRadius = 0.5f;

        /// <summary>Raised once per edit, after the mouse comes up — the cue for
        /// the collider to re-cook and the drag readout to re-measure. Never
        /// raised mid-drag.</summary>
        public event System.Action<DeformableBody> DeformCommitted;

        public SkinnedMeshRenderer Smr { get; private set; }
        public BodyDeformCollision Collision { get; private set; }
        public BodyDef Def { get; private set; }

        /// <summary>Metres per author unit — the renderer's own scale, so a
        /// world-space displacement can be turned into a base-mesh offset.</summary>
        public Vector3 RenderScale { get; private set; }

        /// <summary>The live mesh: base vertices plus offsets, carrying the
        /// blendshape frames. Owned here and destroyed with this component.</summary>
        public Mesh WorkMesh { get; private set; }

        /// <summary>Blendshape names in <c>SetBlendShapeWeight</c> index order.
        /// These are the keys a save file matches on.</summary>
        public string[] MorphNames { get; private set; } = new string[0];

        public int VertexCount => _baseVerts != null ? _baseVerts.Length : 0;

        /// <summary>How many vertices currently carry a free-form offset.</summary>
        public int OffsetCount { get; private set; }

        public bool IsSculpting => _sculpting;

        // ---- state ----------------------------------------------------------------

        private Transform _meshT;
        private Vector3[] _baseVerts;      // pristine, author units
        private Vector3[] _offsets;        // free-form pull, author units
        private Vector3[] _composed;       // base + offsets, reused to avoid churn
        private float[] _morphWeights;     // 0..100, Unity's own scale
        private int[] _groupOf;
        private List<int>[] _members;
        private Bounds _baseBounds;
        private bool _dirty;

        // stroke
        private bool _sculpting;
        private Vector3 _grabWorld, _grabNormal;
        private Plane _dragPlane;
        private readonly List<int> _touched = new List<int>(512);
        private readonly List<float> _touchW = new List<float>(512);
        private Vector3[] _strokeStart = new Vector3[0];

        // wheel markers
        private readonly List<Transform> _wheelMarkers = new List<Transform>(4);
        private readonly List<WheelDisc> _wheels = new List<WheelDisc>(4);
        private float _wheelbase;
        private float _wheelRadius;

        /// <summary>The wheel discs the drag readout charges exposed-wheel drag
        /// for, in this object's local metres.</summary>
        public IReadOnlyList<WheelDisc> Wheels => _wheels;

        /// <summary>Axle-to-axle distance of the markers, in metres.</summary>
        public float WheelbaseM => _wheelbase;

        /// <summary>The body's own length in metres, base mesh × render scale.
        /// Every proportion in this rig — wheel size, stand size, camera framing —
        /// is a fraction of it.</summary>
        public float BodyLengthM { get; private set; } = CarVehicle.BodyMeshAuthorSize.z;

        /// <summary>Local Y of the bottom of the wheel markers. The rig is stood
        /// on the turntable by this, not by the body's floor pan — a low racer's
        /// underside sits well below its hubs and would otherwise hover.</summary>
        public float WheelBottomLocalY { get; private set; }

        // ---- construction -----------------------------------------------------------

        /// <summary>
        /// Build the rig for one catalogue body: the skinned renderer, its morph
        /// frames, the collider sibling and the wheel markers. Safe to call again
        /// to swap bodies — everything it made last time is torn down first.
        ///
        /// Returns false when the body's geometry could not be flattened at all,
        /// which leaves the rig empty rather than half-built.
        /// </summary>
        public bool Init(BodyDef def)
        {
            Teardown();

            Def = def;
            Mesh src = BodyMeshSource.Build(def, out Vector3 scale);
            RenderScale = scale;
            if (src == null)
            {
                Debug.LogError($"[DeformableBody] No readable geometry for body " +
                               $"'{(def != null ? def.id : "null")}' — nothing to sculpt.");
                return false;
            }

            WorkMesh = Instantiate(src);
            WorkMesh.name = "bodyed_work";
            WorkMesh.hideFlags = HideFlags.HideAndDontSave;
            // Tells Unity this vertex buffer will be rewritten often, so it picks
            // a CPU-writable allocation instead of re-uploading a static one.
            WorkMesh.MarkDynamic();

            _baseVerts = WorkMesh.vertices;
            _offsets = new Vector3[_baseVerts.Length];
            _composed = new Vector3[_baseVerts.Length];
            _baseBounds = BodyMorphs.BoundsOf(_baseVerts);
            OffsetCount = 0;

            MorphNames = BodyMorphs.AddTo(WorkMesh, _baseVerts);
            _morphWeights = new float[MorphNames.Length];

            DeformFalloff.BuildWeldMap(_baseVerts, DeformFalloff.WeldQuantum,
                                       out _groupOf, out _members);

            BuildRenderer();
            BuildCollision();
            BuildWheels();

            Debug.Log($"[DeformableBody] Opened '{(def != null ? def.id : "?")}' — " +
                      $"{_baseVerts.Length} verts in {_members.Length} weld groups, " +
                      $"{MorphNames.Length} morphs, scale {RenderScale.x:0.###}×" +
                      $"{RenderScale.y:0.###}×{RenderScale.z:0.###}.");

            Recompose();
            return true;
        }

        private void BuildRenderer()
        {
            var go = new GameObject("Mesh");
            _meshT = go.transform;
            _meshT.SetParent(transform, false);
            _meshT.localScale = RenderScale;

            Smr = go.AddComponent<SkinnedMeshRenderer>();
            Smr.sharedMesh = WorkMesh;
            Smr.sharedMaterial = BodyEdMaterials.Body();
            // Explicit bounds rather than updateWhenOffscreen: that flag makes
            // Unity bake the skin every frame purely to measure it, and this
            // component already knows exactly when the shape changed.
            Smr.updateWhenOffscreen = false;
            UpdateRendererBounds();
        }

        private void BuildCollision()
        {
            var go = new GameObject("Collision");
            go.transform.SetParent(transform, false);
            // Left at scale 1 on purpose: BodyDeformCollision bakes in author
            // units and applies RenderScale itself, so what it hands the collider
            // is already metres. A scaled transform here would apply it twice.
            go.transform.localScale = Vector3.one;
            Collision = go.AddComponent<BodyDeformCollision>();
        }

        private void BuildWheels()
        {
            Vector3 ext = Vector3.Scale(_baseBounds.size, RenderScale);
            float len = Mathf.Max(1e-3f, Mathf.Abs(ext.z));
            BodyLengthM = len;
            _wheelRadius = WheelRadiusPerLength * len;
            _wheelbase = WheelbasePerLength * len;

            float halfTrack = HalfTrackPerLength * len;
            float hubY = -HubDropPerLength * len;
            float width = WheelWidthPerRadius * _wheelRadius;
            WheelBottomLocalY = hubY - _wheelRadius;

            var holder = new GameObject("Wheels").transform;
            holder.SetParent(transform, false);

            for (int i = 0; i < 4; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.name = "WheelMarker" + i;
                Kill(go.GetComponent<Collider>());   // the brush must not hit a wheel
                go.transform.SetParent(holder, false);
                // Unity's cylinder is 2 units tall on its local Y with radius 0.5;
                // rolled onto its side, Y becomes the axle.
                go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                go.transform.localScale = new Vector3(2f * _wheelRadius, 0.5f * width,
                                                      2f * _wheelRadius);
                go.GetComponent<MeshRenderer>().sharedMaterial = BodyEdMaterials.Wheel();
                _wheelMarkers.Add(go.transform);
            }

            PoseWheels(halfTrack, hubY);
        }

        /// <summary>Move the markers to the current wheelbase and rebuild the disc
        /// list the readout uses. Order is fixed (front-left, front-right,
        /// rear-left, rear-right) so the exposure sampling is deterministic.</summary>
        private void PoseWheels(float halfTrack, float hubY)
        {
            _wheels.Clear();
            int i = 0;
            foreach (float z in new[] { 0.5f * _wheelbase, -0.5f * _wheelbase })
                foreach (float x in new[] { -halfTrack, halfTrack })
                {
                    var p = new Vector3(x, hubY, z);
                    if (i < _wheelMarkers.Count) _wheelMarkers[i].localPosition = p;
                    _wheels.Add(new WheelDisc(p, _wheelRadius));
                    i++;
                }
        }

        /// <summary>
        /// Set the axle-to-axle distance, in metres. Marks the body dirty so the
        /// drag readout re-measures with the wheels where they now are — moving a
        /// wheel out from under a fender genuinely changes a car's drag.
        /// </summary>
        public void SetWheelbase(float metres)
        {
            if (_baseVerts == null) return;
            float len = BodyLengthM;
            _wheelbase = Mathf.Clamp(metres, 0.15f * len, 1.4f * len);
            PoseWheels(HalfTrackPerLength * len, -HubDropPerLength * len);
            _dirty = true;
        }

        /// <summary>The wheelbase range the panel's slider spans, in metres.</summary>
        public float WheelbaseMin => 0.15f * BodyLengthM;

        /// <summary>The other end of <see cref="WheelbaseMin"/>.</summary>
        public float WheelbaseMax => 1.4f * BodyLengthM;

        // ---- morphs -----------------------------------------------------------------

        /// <summary>
        /// Set one morph's weight, on Unity's 0..100 scale.
        ///
        /// This is the thin wrapper it looks like — the value goes straight to
        /// <c>SetBlendShapeWeight</c> — with two jobs of its own: it keeps a copy
        /// so a save does not have to interrogate the renderer, and it marks the
        /// body dirty so the collider and the drag readout follow on release.
        /// </summary>
        public void UpdateVehicleMorph(int shapeIndex, float weight)
        {
            if (Smr == null || _morphWeights == null) return;
            if (shapeIndex < 0 || shapeIndex >= _morphWeights.Length) return;

            float w = Mathf.Clamp(weight, 0f, 100f);
            if (Mathf.Approximately(_morphWeights[shapeIndex], w)) return;

            _morphWeights[shapeIndex] = w;
            Smr.SetBlendShapeWeight(shapeIndex, w);
            _dirty = true;
        }

        /// <summary>Current weight of a morph, 0..100.</summary>
        public float MorphWeight(int shapeIndex) =>
            _morphWeights != null && shapeIndex >= 0 && shapeIndex < _morphWeights.Length
                ? _morphWeights[shapeIndex] : 0f;

        public void ResetMorphs()
        {
            if (_morphWeights == null) return;
            for (int i = 0; i < _morphWeights.Length; i++) UpdateVehicleMorph(i, 0f);
        }

        // ---- free-form sculpting ------------------------------------------------------

        /// <summary>
        /// Start a stroke: find where the pointer ray meets the body, and capture
        /// the vertices the brush covers along with their falloff weights.
        ///
        /// <b>The set is captured once and frozen for the stroke.</b> That is what
        /// makes a stale collider harmless — the only raycast happens here, before
        /// anything has moved. It also stops the brush from crawling across the
        /// surface as the geometry runs away from it, which is what re-picking
        /// every frame produces.
        ///
        /// <paramref name="radiusM"/> is in metres, measured on the baked body.
        /// </summary>
        public bool TryBeginSculpt(Ray ray, float radiusM)
        {
            _sculpting = false;
            if (Collision == null || Collision.Collider == null) return false;
            Vector3[] baked = Collision.BakedVertices;
            if (baked == null || baked.Length != _baseVerts.Length) return false;

            if (!Collision.Collider.Raycast(ray, out RaycastHit hit, 1000f)) return false;

            Vector3 local = Collision.transform.InverseTransformPoint(hit.point);
            int n = DeformFalloff.GatherWelded(baked, local, radiusM, _groupOf, _members,
                                               _touched, _touchW);
            if (n == 0) return false;

            _grabWorld = hit.point;
            _grabNormal = hit.normal.sqrMagnitude > 1e-6f ? hit.normal.normalized : Vector3.up;
            // The drag plane faces the camera through the grab point, so pointer
            // movement reads as world movement at the depth being worked on.
            _dragPlane = new Plane(-ray.direction.normalized, hit.point);

            if (_strokeStart.Length < n) _strokeStart = new Vector3[Mathf.NextPowerOfTwo(n)];
            for (int i = 0; i < n; i++) _strokeStart[i] = _offsets[_touched[i]];

            _sculpting = true;
            return true;
        }

        /// <summary>
        /// Continue the stroke. The offset written is always measured from where
        /// each vertex stood when the stroke began, so dragging back to the start
        /// undoes the pull exactly instead of accumulating float error.
        /// </summary>
        public void SculptTo(Ray ray, SculptDir dir, float strength)
        {
            if (!_sculpting || _meshT == null) return;
            if (!_dragPlane.Raycast(ray, out float enter)) return;

            Vector3 pointerDelta = (ray.GetPoint(enter) - _grabWorld) * strength;

            Vector3 axis;
            switch (dir)
            {
                case SculptDir.Vertical: axis = Vector3.up; break;
                case SculptDir.Lateral:  axis = transform.right; break;
                default:                 axis = _grabNormal; break;
            }
            Vector3 world = axis * Vector3.Dot(pointerDelta, axis);

            // World metres → author units. InverseTransformVector divides by the
            // renderer's scale, which is exactly the conversion the base vertices
            // live in.
            Vector3 baseDelta = _meshT.InverseTransformVector(world);

            for (int i = 0; i < _touched.Count; i++)
                _offsets[_touched[i]] = _strokeStart[i] + baseDelta * _touchW[i];

            Recompose();
            _dirty = true;
        }

        /// <summary>End the stroke. The commit (collider re-cook, drag re-measure)
        /// follows from <see cref="Update"/> once the mouse is actually up.</summary>
        public void EndSculpt()
        {
            _sculpting = false;
            _touched.Clear();
            _touchW.Clear();
        }

        public void ResetOffsets()
        {
            if (_offsets == null) return;
            System.Array.Clear(_offsets, 0, _offsets.Length);
            Recompose();
            _dirty = true;
        }

        // ---- the live mesh --------------------------------------------------------

        /// <summary>
        /// Write base + offsets back into the mesh.
        ///
        /// Normals are recalculated every time rather than left stale: a pulled
        /// surface with its old normals reads as flat, which is precisely the
        /// feedback a sculptor needs. Vertex splits survive the recalculation, so
        /// hard edges stay hard.
        /// </summary>
        private void Recompose()
        {
            if (WorkMesh == null || _baseVerts == null) return;

            int nonZero = 0;
            for (int i = 0; i < _baseVerts.Length; i++)
            {
                _composed[i] = _baseVerts[i] + _offsets[i];
                if (_offsets[i].sqrMagnitude > OffsetEpsilon) nonZero++;
            }
            OffsetCount = nonZero;

            WorkMesh.vertices = _composed;
            WorkMesh.RecalculateNormals();
            WorkMesh.RecalculateBounds();
            UpdateRendererBounds();
        }

        /// <summary>Below this an offset is float noise, not an edit. Squared —
        /// 1 µm on a 0.42 m body.</summary>
        public const float OffsetEpsilon = 1e-12f;

        private void UpdateRendererBounds()
        {
            if (Smr == null || WorkMesh == null) return;
            // Padded, because the morphs move vertices the mesh bounds have not
            // been told about — the bounds describe the base mesh, the renderer
            // draws base + blendshapes.
            Bounds b = WorkMesh.bounds;
            b.Expand(b.size * 0.25f);
            Smr.localBounds = b;
        }

        // ---- the commit rule ---------------------------------------------------------

        /// <summary>
        /// One rule covering every kind of edit: when something changed and the
        /// mouse is not down, commit.
        ///
        /// A slider drag, a sculpt stroke, a load and a reset all end with the
        /// button coming up, so none of them needs its own end-of-gesture
        /// plumbing — and none of them can re-cook the collider while it is still
        /// being dragged.
        /// </summary>
        private void Update()
        {
            if (!_dirty) return;
            if (InputReader.LeftMouseHeld()) return;
            CommitDeform();
        }

        /// <summary>Commit now, without waiting for the mouse. For the initial
        /// build and for anything that changes the shape outside a gesture.</summary>
        public void CommitDeform()
        {
            _dirty = false;
            DeformCommitted?.Invoke(this);
        }

        // ---- save / load -------------------------------------------------------------

        /// <summary>Everything needed to rebuild this body's current shape.</summary>
        public VehicleLayoutData Capture()
        {
            var d = new VehicleLayoutData
            {
                version = 1,
                carBasePrefabID = Def != null ? Def.id : "",
                wheelbaseLength = _wheelbase,
                bodySize = Def != null && Def.nominalSize.sqrMagnitude > 1e-9f
                    ? Def.nominalSize : CarVehicle.BodyMeshAuthorSize,
                blendShapeNames = MorphNames != null ? (string[])MorphNames.Clone() : new string[0],
                blendShapeWeights = _morphWeights != null
                    ? (float[])_morphWeights.Clone() : new float[0],
                baseVertexCount = VertexCount,
            };

            var idx = new List<int>();
            var val = new List<Vector3>();
            if (_offsets != null)
                for (int i = 0; i < _offsets.Length; i++)
                {
                    if (_offsets[i].sqrMagnitude <= OffsetEpsilon) continue;
                    idx.Add(i);
                    val.Add(_offsets[i]);
                }
            d.offsetIndex = idx.ToArray();
            d.offsetValue = val.ToArray();
            return d;
        }

        /// <summary>
        /// Restore a captured shape onto the body that is already open.
        ///
        /// <b>Morphs are matched by NAME, not by index</b>, so adding or
        /// reordering a morph cannot silently apply a roofline weight to a nose
        /// slider. <b>Offsets are refused outright</b> when the base mesh no longer
        /// has the same vertex count: an index into a mesh that has been
        /// re-exported does not address the vertex it used to, and applying it
        /// anyway would scatter the body rather than approximate it.
        ///
        /// Swapping to the layout's own base body is the caller's job — see
        /// <c>BodyEditorBootstrap.LoadLayout</c>. This applies to what is open.
        /// </summary>
        public bool Apply(VehicleLayoutData d)
        {
            if (d == null || _offsets == null) return false;

            if (!string.IsNullOrEmpty(d.carBasePrefabID) && Def != null &&
                d.carBasePrefabID != Def.id)
                Debug.LogWarning($"[DeformableBody] Layout was sculpted on '{d.carBasePrefabID}' " +
                                 $"but '{Def.id}' is open — morph weights will apply, vertex " +
                                 "offsets will not.");

            ResetMorphs();
            if (d.blendShapeWeights != null && MorphNames != null)
                for (int i = 0; i < d.blendShapeWeights.Length; i++)
                {
                    string name = d.blendShapeNames != null && i < d.blendShapeNames.Length
                        ? d.blendShapeNames[i] : null;
                    int target = IndexOfMorph(name, i);
                    if (target < 0)
                    {
                        Debug.LogWarning($"[DeformableBody] Layout carries an unknown morph " +
                                         $"'{name}' — dropped.");
                        continue;
                    }
                    UpdateVehicleMorph(target, d.blendShapeWeights[i]);
                }

            System.Array.Clear(_offsets, 0, _offsets.Length);
            bool sameBody = string.IsNullOrEmpty(d.carBasePrefabID) || Def == null ||
                            d.carBasePrefabID == Def.id;
            if (d.offsetIndex != null && d.offsetValue != null && sameBody)
            {
                if (d.baseVertexCount > 0 && d.baseVertexCount != _offsets.Length)
                {
                    Debug.LogError($"[DeformableBody] Layout was sculpted on a mesh with " +
                                   $"{d.baseVertexCount} vertices; this one has {_offsets.Length}. " +
                                   "The vertex offsets no longer address the same points and have " +
                                   "been dropped. Morph weights were applied.");
                }
                else
                {
                    int n = Mathf.Min(d.offsetIndex.Length, d.offsetValue.Length);
                    int dropped = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int vi = d.offsetIndex[i];
                        if (vi < 0 || vi >= _offsets.Length) { dropped++; continue; }
                        _offsets[vi] = d.offsetValue[i];
                    }
                    if (dropped > 0)
                        Debug.LogWarning($"[DeformableBody] {dropped} offset(s) pointed outside " +
                                         "the mesh and were dropped.");
                }
            }

            if (d.wheelbaseLength > 0f) SetWheelbase(d.wheelbaseLength);

            Recompose();
            _dirty = true;
            return true;
        }

        private int IndexOfMorph(string name, int fallbackIndex)
        {
            if (!string.IsNullOrEmpty(name) && MorphNames != null)
            {
                for (int i = 0; i < MorphNames.Length; i++)
                    if (MorphNames[i] == name) return i;
                return -1;   // named, and this build has no such morph
            }
            // Nameless legacy file: positional is the only reading available.
            return MorphNames != null && fallbackIndex < MorphNames.Length ? fallbackIndex : -1;
        }

        /// <summary>Capture and write, in one call. Returns the path, or null.</summary>
        public string SaveVehicleToFile(string fileName) =>
            BodyLayoutLibrary.SaveVehicleToFile(fileName, Capture());

        /// <summary>Read and apply, in one call. Returns false if nothing loaded.</summary>
        public bool LoadVehicleFromFile(string fileName)
        {
            VehicleLayoutData d = BodyLayoutLibrary.LoadVehicleFromFile(fileName);
            return d != null && Apply(d);
        }

        // ---- teardown ----------------------------------------------------------------

        /// <summary>Destroy from either a running scene or an edit-mode bench.
        /// <c>Destroy</c> logs an error outside play mode and <c>DestroyImmediate</c>
        /// is illegal inside it, and <c>[BDEF]</c> builds one of these rigs without
        /// entering play mode.</summary>
        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void Teardown()
        {
            EndSculpt();
            for (int i = transform.childCount - 1; i >= 0; i--)
                Kill(transform.GetChild(i).gameObject);
            _wheelMarkers.Clear();
            _wheels.Clear();
            Smr = null;
            Collision = null;
            _meshT = null;
            BodyMeshSource.DestroyMesh(WorkMesh);
            WorkMesh = null;
        }

        private void OnDestroy()
        {
            BodyMeshSource.DestroyMesh(WorkMesh);
            WorkMesh = null;
        }
    }
}
