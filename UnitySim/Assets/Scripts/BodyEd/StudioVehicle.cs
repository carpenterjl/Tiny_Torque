using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The whole vehicle under construction: one deformable body and every part
    /// bolted to it, in one frame, with one layout that describes the lot.
    ///
    /// <b>This transform IS the vehicle frame.</b> Prop poses are stored in it, the
    /// gizmo drags in it, and it is the same frame <c>VehicleFactory</c> mounts a
    /// car's children in — so a part placed here lands in the same place on the
    /// track without a conversion anywhere. The body hangs off it at the origin;
    /// the ROOT is what gets lifted so the wheels stand on the turntable, which
    /// keeps the parts and the body agreeing about where the car's middle is.
    ///
    /// <b>Swapping the body keeps the parts.</b> They are placed in the vehicle's
    /// frame, not the body's, so trying a spoiler on four different shells is three
    /// clicks rather than three rebuilds.
    /// </summary>
    public sealed class StudioVehicle : MonoBehaviour
    {
        public DeformableBody Body { get; private set; }
        public BodyDef Def => Body != null ? Body.Def : null;

        private readonly List<PlacedProp> _props = new List<PlacedProp>();
        public IReadOnlyList<PlacedProp> Props => _props;

        /// <summary>The crash frame, parallel to the parts — same frame, same
        /// capture/apply seam.</summary>
        public StudioLattice Lattice { get; private set; }

        private Transform _propRoot;

        /// <summary>Raised when the set of parts changes — added, removed,
        /// duplicated, or rebuilt by a load. Not raised for a drag, which changes
        /// a part rather than the list.</summary>
        public event System.Action<StudioVehicle> PartsChanged;

        /// <summary>Forwarded from the body, so a host can subscribe once and keep
        /// its subscription across a body swap.</summary>
        public event System.Action<DeformableBody> DeformCommitted;

        // ==================== the body ====================

        /// <summary>
        /// Open a catalogue body, keeping every placed part. Returns false when the
        /// body's geometry could not be read, which leaves the previous one in
        /// place rather than emptying the stand.
        /// </summary>
        public bool SetBody(BodyDef def)
        {
            EnsureRoots();

            var go = new GameObject("Body");
            go.transform.SetParent(transform, false);
            var body = go.AddComponent<DeformableBody>();
            if (!body.Init(def))
            {
                Kill(go);
                return false;
            }

            if (Body != null)
            {
                Body.DeformCommitted -= OnDeformCommitted;
                Kill(Body.gameObject);
            }
            Body = body;
            Body.DeformCommitted += OnDeformCommitted;
            return true;
        }

        private void OnDeformCommitted(DeformableBody b) => DeformCommitted?.Invoke(b);

        // ==================== parts ====================

        /// <summary>
        /// Add a part. <paramref name="localPos"/> is in this vehicle's frame, in
        /// metres.
        ///
        /// Returns the part even when its geometry did not build — see
        /// <see cref="PlacedProp"/>. Null only for a source that is not a source.
        /// </summary>
        public PlacedProp AddProp(string source, Vector3 localPos)
        {
            if (string.IsNullOrEmpty(source)) return null;
            EnsureRoots();

            var spec = new PropPlacement
            {
                source = source,
                label = StudioPartLibrary.LabelFor(source),
                localPos = localPos,
                scale = 1f,
            };
            return Adopt(spec);
        }

        /// <summary>Build a part from a spec that already exists — a load, a
        /// duplicate, an undo.</summary>
        public PlacedProp Adopt(PropPlacement spec)
        {
            if (spec == null) return null;
            EnsureRoots();

            var go = new GameObject("prop");
            go.transform.SetParent(_propRoot, false);
            var prop = go.AddComponent<PlacedProp>();
            prop.Build(spec);
            _props.Add(prop);
            PartsChanged?.Invoke(this);
            return prop;
        }

        public bool RemoveProp(PlacedProp prop)
        {
            if (prop == null || !_props.Remove(prop)) return false;
            Kill(prop.gameObject);
            PartsChanged?.Invoke(this);
            return true;
        }

        /// <summary>Copy a part, offset far enough to be visibly a second one.</summary>
        public PlacedProp Duplicate(PlacedProp prop)
        {
            if (prop?.Spec == null) return null;
            PropPlacement spec = prop.Spec.Clone();
            spec.mirrorGroup = -1;    // a copy is its own part, not the twin's
            spec.localPos += new Vector3(0f, 0f, DuplicateNudge);
            return Adopt(spec);
        }

        /// <summary>How far a duplicate lands from its original (m). Small enough
        /// to still be where you put it, large enough not to z-fight.</summary>
        private const float DuplicateNudge = 0.02f;

        /// <summary>
        /// Give a part a mirrored twin across the car's centreline, linked by a
        /// shared group id.
        ///
        /// <b>Mirroring is a one-off copy, not a live constraint.</b> The garage
        /// links twins with the same <c>mirrorGroup</c> convention and syncs them
        /// through <c>SymmetryUtil</c> on edit; here the group id is recorded so
        /// that machinery can be adopted later, but the twin is free the moment it
        /// exists. Saying so is better than implying a link that the gizmo does not
        /// currently maintain.
        /// </summary>
        public PlacedProp MirrorProp(PlacedProp prop)
        {
            if (prop?.Spec == null) return null;
            if (Mathf.Abs(prop.Spec.localPos.x) < SymmetryUtil.CenterDeadzone)
            {
                Debug.Log("[StudioVehicle] That part is on the centreline — its mirror " +
                          "would land on top of it.");
                return null;
            }

            PropPlacement spec = prop.Spec.Clone();
            spec.localPos = new Vector3(-spec.localPos.x, spec.localPos.y, spec.localPos.z);
            // Yaw and roll flip across a mirror; pitch does not. Same decomposition
            // SymmetryUtil.MirrorInto uses for the design's own parts.
            spec.euler = new Vector3(spec.euler.x, -spec.euler.y, -spec.euler.z);

            int group = NextMirrorGroup();
            prop.Spec.mirrorGroup = group;
            spec.mirrorGroup = group;
            return Adopt(spec);
        }

        private int NextMirrorGroup()
        {
            int max = 0;
            foreach (PlacedProp p in _props)
                if (p?.Spec != null) max = Mathf.Max(max, p.Spec.mirrorGroup);
            return max + 1;
        }

        /// <summary>
        /// The part under a pointer ray, or null.
        ///
        /// Nearest wins, and the body's own collider is not a candidate — a click
        /// that lands on bare bodywork means "select nothing", which the caller
        /// reads from a null return.
        /// </summary>
        public PlacedProp Pick(Ray ray)
        {
            PlacedProp best = null;
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(ray, 500f, ~0,
                                                        QueryTriggerInteraction.Collide))
            {
                var p = h.collider.GetComponentInParent<PlacedProp>();
                if (p == null || h.distance >= bestDist) continue;
                bestDist = h.distance;
                best = p;
            }
            return best;
        }

        /// <summary>Where a pointer ray meets the body, for dropping a new part on
        /// the surface somebody is pointing at.</summary>
        public bool TrySurfacePoint(Ray ray, out Vector3 localPoint)
        {
            localPoint = Vector3.zero;
            if (Body == null || Body.Collision == null || Body.Collision.Collider == null)
                return false;
            if (!Body.Collision.Collider.Raycast(ray, out RaycastHit hit, 1000f)) return false;
            localPoint = transform.InverseTransformPoint(hit.point);
            return true;
        }

        // ==================== the layout ====================

        /// <summary>The whole vehicle as one saveable document.</summary>
        public VehicleLayoutData Capture()
        {
            VehicleLayoutData d = Body != null ? Body.Capture() : new VehicleLayoutData();
            var specs = new List<PropPlacement>(_props.Count);
            foreach (PlacedProp p in _props)
                if (p?.Spec != null) specs.Add(p.Spec.Clone());
            d.props = specs.Count > 0 ? specs.ToArray() : null;
            Lattice?.CaptureInto(d);
            return d;
        }

        /// <summary>
        /// Rebuild the vehicle from a layout: the body's shape and paint, then
        /// every part from scratch.
        ///
        /// Parts are rebuilt rather than reconciled. A layout is small, building a
        /// part is a prefab instantiate, and a reconciler would be a second
        /// description of what a part is — the one thing this file exists to have
        /// only one of.
        /// </summary>
        public bool Apply(VehicleLayoutData d)
        {
            if (d == null) return false;
            EnsureRoots();   // a load may arrive before the first part ever did

            ClearProps();
            if (d.props != null)
                foreach (PropPlacement spec in d.props)
                    if (spec != null) Adopt(spec.Clone());
            Lattice?.ApplyFrom(d);

            bool ok = Body == null || Body.Apply(d);
            PartsChanged?.Invoke(this);
            return ok;
        }

        public void ClearProps()
        {
            foreach (PlacedProp p in _props) if (p != null) Kill(p.gameObject);
            _props.Clear();
            PartsChanged?.Invoke(this);
        }

        // ==================== internals ====================

        private void EnsureRoots()
        {
            if (_propRoot != null) return;
            var go = new GameObject("Parts");
            go.transform.SetParent(transform, false);
            _propRoot = go.transform;

            var lat = new GameObject("Lattice");
            lat.transform.SetParent(transform, false);
            Lattice = lat.AddComponent<StudioLattice>();
            Lattice.Init(this);
        }

        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
