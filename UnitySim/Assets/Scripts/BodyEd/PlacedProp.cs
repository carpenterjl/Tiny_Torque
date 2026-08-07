using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// One part on the vehicle: the <see cref="PropPlacement"/> that describes it,
    /// the geometry built from it, and the pick collider the gizmo selects it by.
    ///
    /// <b>The spec is the truth and the transform is a view of it.</b> Every edit —
    /// a gizmo drag, a nudge, a mirror, a load — writes the spec and calls
    /// <see cref="ApplyPose"/>; nothing reads the pose back out of the transform.
    /// That is what makes save, undo and mirroring one code path each instead of
    /// three, and it is why a prop whose geometry failed to build still moves,
    /// saves and reloads correctly.
    ///
    /// <b>The pick collider is a box, and is on the parts layer.</b> Sculpting
    /// raycasts against the body's MeshCollider and part-picking raycasts against
    /// these; keeping them on separate layers is what stops a click meant for the
    /// spoiler from putting a dent in the roof behind it.
    /// </summary>
    public sealed class PlacedProp : MonoBehaviour
    {
        public PropPlacement Spec { get; private set; }

        /// <summary>The part's paintable channels; never null after
        /// <see cref="Build"/>, empty when the geometry did not build.</summary>
        public FeatureChannels.Binding Channels { get; private set; }

        /// <summary>True when the source key named geometry this build could
        /// produce. A false one is still a real, movable, saveable part — see the
        /// class note.</summary>
        public bool HasGeometry { get; private set; }

        /// <summary>Local-space bounds of the built geometry, for the gizmo's
        /// screen size and the selection box.</summary>
        public Bounds LocalBounds { get; private set; }

        /// <summary>Layer the pick collider lives on — Default, the one the
        /// studio's own picking raycasts search.</summary>
        public const int PickLayer = 0;

        private BoxCollider _pick;
        private Transform _viz;
        private GameObject _selection;
        private Material _selectionMat;

        /// <summary>
        /// Build (or rebuild) this prop from a spec. The spec is adopted, not
        /// copied: the caller owns it, and the layout it belongs to is the thing
        /// that gets saved.
        /// </summary>
        public bool Build(PropPlacement spec, int vizLayer = PartVisualFactory.VizLayer)
        {
            Clear();
            Spec = spec;
            if (spec == null) return false;

            name = string.IsNullOrEmpty(spec.label) ? "prop" : spec.label;
            // The ROOT carries the pick collider and therefore has to be on a layer
            // scene raycasts reach. The geometry underneath stays on the viz layer
            // (which is Ignore Raycast) exactly as it is on a real car, so the
            // sculpt brush still reaches the body straight through a part sitting
            // in front of it.
            gameObject.layer = PickLayer;

            var holder = new GameObject("viz").transform;
            holder.SetParent(transform, false);
            holder.gameObject.layer = vizLayer;
            _viz = holder;

            GameObject built = StudioPartLibrary.Build(holder, spec.source, vizLayer);
            HasGeometry = built != null;
            if (!HasGeometry)
                Debug.LogWarning($"[PlacedProp] '{spec.source}' built nothing — this build " +
                                 "does not have that part. It stays on the vehicle and in the " +
                                 "layout so a build that has it can still show it.");

            Channels = FeatureChannels.ForHierarchy(holder);
            LocalBounds = MeasureLocal(holder);

            BuildPick(vizLayer);
            ApplyPose();
            ApplyPaint();
            return HasGeometry;
        }

        /// <summary>Push the spec's pose onto the transform. The only writer of
        /// this object's position, rotation and scale.</summary>
        public void ApplyPose()
        {
            if (Spec == null) return;
            transform.localPosition = Spec.localPos;
            transform.localRotation = Quaternion.Euler(Spec.euler);
            transform.localScale = Spec.Scale;
        }

        /// <summary>Re-apply the spec's tints and hidden channels.</summary>
        public void ApplyPaint()
        {
            if (Spec == null || Channels == null) return;
            Channels.Apply(Spec.tints);
        }

        /// <summary>Show or hide the selection box.</summary>
        public void SetSelected(bool on)
        {
            if (_selection != null) _selection.SetActive(on);
        }

        // ---- internals ---------------------------------------------------------------

        private void BuildPick(int layer)
        {
            _pick = gameObject.GetComponent<BoxCollider>();
            if (_pick == null) _pick = gameObject.AddComponent<BoxCollider>();
            _pick.center = LocalBounds.center;
            // A floor on the pick box: a flat decal is a millimetre thick and would
            // otherwise be a target nobody can hit.
            _pick.size = Vector3.Max(LocalBounds.size, new Vector3(0.01f, 0.01f, 0.01f));
            _pick.isTrigger = true;

            _selection = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _selection.name = "selection";
            Kill(_selection.GetComponent<Collider>());   // the pick box above is the target
            _selection.transform.SetParent(transform, false);
            _selection.transform.localPosition = _pick.center;
            _selection.transform.localScale = _pick.size * 1.06f;
            _selection.layer = layer;
            _selectionMat = PartVisualFactory.MakeGhostMat(new Color(0.98f, 0.82f, 0.35f, 0.16f));
            _selection.GetComponent<Renderer>().sharedMaterial = _selectionMat;
            _selection.SetActive(false);
        }

        private static Bounds MeasureLocal(Transform holder)
        {
            bool any = false;
            var result = new Bounds();
            Matrix4x4 toLocal = holder.parent != null
                ? holder.parent.worldToLocalMatrix : Matrix4x4.identity;

            foreach (Renderer r in holder.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                       : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (m == null) continue;
                Bounds lb = m.bounds;
                Matrix4x4 mtx = toLocal * r.transform.localToWorldMatrix;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? lb.min.x : lb.max.x,
                        (c & 2) == 0 ? lb.min.y : lb.max.y,
                        (c & 4) == 0 ? lb.min.z : lb.max.z);
                    Vector3 p = mtx.MultiplyPoint3x4(corner);
                    if (!any) { result = new Bounds(p, Vector3.zero); any = true; }
                    else result.Encapsulate(p);
                }
            }
            // A part with no geometry still needs a handle to grab.
            if (!any) result = new Bounds(Vector3.zero, new Vector3(0.02f, 0.02f, 0.02f));
            return result;
        }

        private void Clear()
        {
            Channels?.Dispose();
            Channels = null;
            if (_viz != null) Kill(_viz.gameObject);
            _viz = null;
            if (_selection != null) Kill(_selection);
            _selection = null;
            Kill(_selectionMat);
            _selectionMat = null;
        }

        private void OnDestroy() => Clear();

        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
