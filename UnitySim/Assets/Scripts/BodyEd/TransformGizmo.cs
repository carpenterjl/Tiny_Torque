using UnityEngine;

namespace AIHWSim.BodyEd
{
    public enum GizmoMode { Move, Rotate, Scale }

    /// <summary>Which axes the handles line up with: the part's own, or the
    /// vehicle's.</summary>
    public enum GizmoSpace { Part, Body }

    /// <summary>
    /// The drag handles: three arrows and three plane quads to move with, three
    /// rings to turn with, three stalked cubes to stretch one axis and a centre
    /// cube to stretch them all — the thing that makes placement precise instead
    /// of approximate.
    ///
    /// <b>It edits the SPEC, never the transform.</b> A drag writes
    /// <c>PropPlacement.localPos/euler/scale</c> and raises
    /// <see cref="Changed"/>; the owner re-poses the part from the spec. So an
    /// undo, a mirror, a load and a drag all arrive at the same place by the same
    /// route, and the gizmo has no state anyone else has to keep in step.
    ///
    /// <b>Absolute from the grab, not accumulated per frame.</b> Every drag
    /// snapshots the pose and the grab point at mouse-down and computes the answer
    /// from those two and the current ray. Accumulating frame deltas would drift
    /// with the frame rate and would make snapping mean "snap the increment",
    /// which is not a thing anybody wants.
    ///
    /// <b>And the FRAME it measures in is part of that snapshot.</b> The gizmo is
    /// re-posed from the spec every frame, so its own transform chases the part it
    /// is moving. Measuring an axis drag from the live <c>transform.position</c>
    /// therefore subtracts the motion it just produced: the part lands, the gizmo
    /// follows, the next frame reads the displacement as already spent and puts the
    /// part back — a one-frame oscillation that reads as violent jitter. Origin,
    /// axis and camera normal are all taken at mouse-down and held for the drag.
    ///
    /// <b>Handles are picked by their own raycast pass, ahead of everything.</b> A
    /// handle in front of the part it moves must win the click even though the part
    /// is also under the cursor; two passes says that plainly, where one pass with
    /// a distance sort would say the opposite half the time.
    /// </summary>
    public sealed class TransformGizmo : MonoBehaviour
    {
        // ---- appearance ---------------------------------------------------------------

        /// <summary>Fraction of the viewport height the gizmo spans. Large enough
        /// to grab with a thumbstick-driven cursor, small enough not to hide a
        /// 40 cm car.</summary>
        private const float ScreenFraction = 0.16f;

        private static readonly Color[] AxisColor =
        {
            new Color(0.92f, 0.29f, 0.31f),   // X
            new Color(0.42f, 0.86f, 0.38f),   // Y
            new Color(0.32f, 0.57f, 0.96f),   // Z
        };
        private static readonly Color HotColor = new Color(0.99f, 0.86f, 0.35f);

        // ---- settings -----------------------------------------------------------------

        public GizmoMode Mode = GizmoMode.Move;
        public GizmoSpace Space = GizmoSpace.Body;

        /// <summary>Grid snapping. The steps match the garage's own 5 mm
        /// placement grid so a part placed in either tool lands on the same
        /// lattice.</summary>
        public bool Snapping;
        public float SnapPos = 0.005f;
        public float SnapDeg = 15f;
        public float SnapScale = 0.05f;

        // ---- state --------------------------------------------------------------------

        public bool Dragging { get; private set; }
        public bool Attached => _spec != null;

        /// <summary>Raised at the end of every frame in which a drag changed the
        /// spec.</summary>
        public event System.Action Changed;

        private PropPlacement _spec;
        private Transform _frame;          // the vehicle-local frame the spec lives in
        private Camera _cam;

        private readonly GameObject[] _moveArrow = new GameObject[3];
        private readonly GameObject[] _movePlane = new GameObject[3];
        private readonly GameObject[] _ring = new GameObject[3];
        private readonly GameObject[] _scaleArm = new GameObject[3];
        private GameObject _scaleCube;
        private Material[] _mats;          // 0..2 axis, 3 hot
        private readonly System.Collections.Generic.List<Material> _ringMats =
            new System.Collections.Generic.List<Material>();
        private bool _built;

        // per-drag snapshot
        private GizmoHandle _grabbed;
        private Vector3 _startPos, _startEuler, _grabPoint;
        private Vector3 _startScale;
        private float _startT;
        private Quaternion _startRot;

        /// <summary>The frame the drag is measured in, frozen at mouse-down. See
        /// the note on the class: reading these live is what makes a gizmo fight
        /// the part it is moving.</summary>
        private Vector3 _startOrigin, _startAxis, _startNormal;

        // ==================== attachment ====================

        /// <summary>
        /// Point the gizmo at a part. <paramref name="frame"/> is the transform the
        /// spec's local pose is expressed in — the vehicle root, which is what
        /// makes a saved position mean the same thing on the track.
        /// </summary>
        public void Attach(PropPlacement spec, Transform frame)
        {
            Build();
            _spec = spec;
            _frame = frame;
            EndDrag();
            gameObject.SetActive(spec != null && frame != null);
        }

        public void Detach()
        {
            _spec = null;
            _frame = null;
            EndDrag();
            gameObject.SetActive(false);
        }

        /// <summary>Pose the handles and hold their screen size. Call once a frame
        /// before any picking, from the owner's Update.</summary>
        public void Refresh(Camera cam)
        {
            _cam = cam;
            if (_spec == null || _frame == null) { gameObject.SetActive(false); return; }
            gameObject.SetActive(true);

            transform.position = _frame.TransformPoint(_spec.localPos);
            transform.rotation = Space == GizmoSpace.Part
                ? _frame.rotation * Quaternion.Euler(_spec.euler)
                : _frame.rotation;

            float size = GizmoMath.ScreenConstantSize(cam, transform.position, ScreenFraction);
            transform.localScale = new Vector3(size, size, size);

            for (int a = 0; a < 3; a++)
            {
                _moveArrow[a].SetActive(Mode == GizmoMode.Move);
                _movePlane[a].SetActive(Mode == GizmoMode.Move);
                _ring[a].SetActive(Mode == GizmoMode.Rotate);
                _scaleArm[a].SetActive(Mode == GizmoMode.Scale);
            }
            _scaleCube.SetActive(Mode == GizmoMode.Scale);
        }

        // ==================== dragging ====================

        /// <summary>
        /// Try to grab a handle under the pointer. False when the ray misses every
        /// handle, which is the owner's cue to treat the click as a selection
        /// instead.
        /// </summary>
        public bool TryBeginDrag(Ray ray)
        {
            if (_spec == null || _frame == null || !gameObject.activeSelf) return false;

            GizmoHandle best = null;
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in Physics.RaycastAll(ray, 500f, ~0,
                                                        QueryTriggerInteraction.Collide))
            {
                var handle = h.collider.GetComponentInParent<GizmoHandle>();
                if (handle == null || !handle.gameObject.activeInHierarchy) continue;
                if (handle.mode != Mode) continue;
                if (h.distance >= bestDist) continue;
                bestDist = h.distance;
                best = handle;
            }
            if (best == null) return false;

            _grabbed = best;
            _startPos = _spec.localPos;
            _startEuler = _spec.euler;
            _startScale = _spec.Scale;
            _startRot = Quaternion.Euler(_spec.euler);

            // Freeze the frame for the whole drag.
            _startOrigin = transform.position;
            _startAxis = AxisWorld(best.axis);
            _startNormal = CameraNormal();

            switch (Mode)
            {
                case GizmoMode.Move:
                    if (best.plane)
                    {
                        if (!GizmoMath.RayPlane(ray, _startOrigin, _startAxis, out _grabPoint))
                            return false;
                    }
                    else
                    {
                        if (!GizmoMath.ClosestOnAxis(ray, _startOrigin, _startAxis, out _startT))
                            return false;
                    }
                    break;

                case GizmoMode.Rotate:
                    if (!GizmoMath.RayPlane(ray, _startOrigin, _startAxis, out _grabPoint))
                        return false;
                    break;

                default: // Scale
                    if (best.uniform)
                    {
                        // The uniform cube reads the radius on the plane facing the
                        // camera, so it works whichever way the gizmo is turned.
                        if (!GizmoMath.RayPlane(ray, _startOrigin, _startNormal, out _grabPoint))
                            return false;
                        if ((_grabPoint - _startOrigin).magnitude < 1e-5f) return false;
                    }
                    else
                    {
                        // An axis arm reads distance ALONG its axis, and the answer
                        // is a ratio — so a grab at the pivot, where that ratio has
                        // no value, is refused like any other degenerate drag.
                        if (!GizmoMath.ClosestOnAxis(ray, _startOrigin, _startAxis, out _startT))
                            return false;
                        if (Mathf.Abs(_startT) < 1e-4f) return false;
                    }
                    break;
            }

            Dragging = true;
            Tint(best);
            return true;
        }

        /// <summary>Continue a drag. Silently does nothing on a frame whose ray is
        /// degenerate for the handle being held — see <see cref="GizmoMath"/>.</summary>
        public void DragTo(Ray ray)
        {
            if (!Dragging || _spec == null || _frame == null) return;

            Vector3 axis = _startAxis;
            switch (Mode)
            {
                case GizmoMode.Move:
                {
                    Vector3 worldDelta;
                    if (_grabbed.plane)
                    {
                        if (!GizmoMath.RayPlane(ray, _startOrigin, axis, out Vector3 hit))
                            return;
                        worldDelta = hit - _grabPoint;
                    }
                    else
                    {
                        if (!GizmoMath.ClosestOnAxis(ray, _startOrigin, axis, out float t))
                            return;
                        worldDelta = axis.normalized * (t - _startT);
                    }
                    Vector3 local = _startPos + _frame.InverseTransformVector(worldDelta);
                    _spec.localPos = Snapping ? GizmoMath.Snap(local, SnapPos) : local;
                    break;
                }

                case GizmoMode.Rotate:
                {
                    if (!GizmoMath.RayPlane(ray, _startOrigin, axis, out Vector3 hit)) return;
                    float sweep = GizmoMath.SweepDegrees(axis, _startOrigin, _grabPoint, hit);
                    if (Snapping) sweep = GizmoMath.Snap(sweep, SnapDeg);

                    // Turn about the handle's axis expressed in the frame the euler
                    // lives in, so a body-space ring turns the part about the CAR's
                    // axis however the part is already twisted.
                    Vector3 localAxis = _frame.InverseTransformDirection(axis).normalized;
                    Quaternion rot = Quaternion.AngleAxis(sweep, localAxis) * _startRot;
                    Vector3 e = rot.eulerAngles;
                    _spec.euler = Snapping ? GizmoMath.SnapAngles(e, SnapDeg) : e;
                    break;
                }

                default:
                {
                    // The ratio the grab has been stretched by, one number either
                    // way: radially from the pivot for the uniform cube, along the
                    // axis for an arm.
                    float factor;
                    if (_grabbed.uniform)
                    {
                        if (!GizmoMath.RayPlane(ray, _startOrigin, _startNormal, out Vector3 hit))
                            return;
                        float r0 = (_grabPoint - _startOrigin).magnitude;
                        if (r0 < 1e-5f) return;
                        factor = (hit - _startOrigin).magnitude / r0;
                    }
                    else
                    {
                        if (!GizmoMath.ClosestOnAxis(ray, _startOrigin, axis, out float t)) return;
                        if (Mathf.Abs(_startT) < 1e-4f) return;
                        factor = t / _startT;
                    }

                    Vector3 v = _startScale;
                    if (_grabbed.uniform) v *= factor;
                    else v[_grabbed.axis] = _startScale[_grabbed.axis] * factor;

                    // Snapped and clamped per component, so a non-uniform part is on
                    // the grid in every direction rather than in the one just pulled.
                    for (int i = 0; i < 3; i++)
                    {
                        if (Snapping) v[i] = GizmoMath.Snap(v[i], SnapScale);
                        v[i] = Mathf.Clamp(v[i], MinScale, MaxScale);
                    }
                    _spec.Scale = v;
                    break;
                }
            }

            Changed?.Invoke();
        }

        /// <summary>Scale limits. A part at 0 is invisible and unselectable, and a
        /// part at 20× is a wall somebody has to find the handle inside of.</summary>
        public const float MinScale = 0.1f;
        public const float MaxScale = 8f;

        public void EndDrag()
        {
            Dragging = false;
            _grabbed = null;
            Tint(null);
        }

        /// <summary>Cancel a drag and put the part back where it was grabbed
        /// from — Escape during a drag.</summary>
        public void CancelDrag()
        {
            if (!Dragging || _spec == null) { EndDrag(); return; }
            _spec.localPos = _startPos;
            _spec.euler = _startEuler;
            _spec.Scale = _startScale;
            EndDrag();
            Changed?.Invoke();
        }

        // ==================== geometry ====================

        private Vector3 AxisWorld(int axis) => axis switch
        {
            0 => transform.right,
            1 => transform.up,
            _ => transform.forward,
        };

        private Vector3 CameraNormal() =>
            _cam != null ? -_cam.transform.forward : Vector3.up;

        private void Build()
        {
            if (_built) return;
            _built = true;

            _mats = new Material[4];
            for (int a = 0; a < 3; a++) _mats[a] = Unlit(AxisColor[a]);
            _mats[3] = Unlit(HotColor);

            for (int a = 0; a < 3; a++)
            {
                _moveArrow[a] = BuildArrow(a);
                _movePlane[a] = BuildPlane(a);
                _ring[a] = BuildRing(a);
                _scaleArm[a] = BuildScaleArm(a);
            }
            _scaleCube = BuildScaleCube();
        }

        /// <summary>
        /// An unlit-looking material: Standard with full emission and no
        /// specular, so a handle reads the same whichever way the scene light
        /// happens to be pointing. The project ships no unlit shader of its own
        /// and this needs no new one.
        /// </summary>
        private static Material Unlit(Color c)
        {
            var m = new Material(Shader.Find("Standard")) { color = c };
            m.SetFloat("_Glossiness", 0f);
            m.SetFloat("_Metallic", 0f);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            m.SetColor("_EmissionColor", c * 0.85f);
            return m;
        }

        private static Vector3 Unit(int axis) => axis switch
        {
            0 => Vector3.right,
            1 => Vector3.up,
            _ => Vector3.forward,
        };

        private GameObject Root(string name, int axis, GizmoMode mode, bool plane, bool uniform)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var h = go.AddComponent<GizmoHandle>();
            h.axis = axis; h.mode = mode; h.plane = plane; h.uniform = uniform;
            return go;
        }

        private GameObject Piece(PrimitiveType type, Transform parent, Material mat,
                                 Vector3 pos, Quaternion rot, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            Kill(go.GetComponent<Collider>());   // one collider per handle, added by the caller
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        private GameObject BuildArrow(int axis)
        {
            GameObject root = Root("arrow_" + axis, axis, GizmoMode.Move, false, false);
            Vector3 u = Unit(axis);
            Quaternion along = Quaternion.FromToRotation(Vector3.up, u);

            // Unity's cylinder and cone-ish cylinder are 2 units tall, hence the
            // halves: a 0.5 scale is a 1-unit shaft.
            Piece(PrimitiveType.Cylinder, root.transform, _mats[axis],
                  u * 0.30f, along, new Vector3(0.022f, 0.30f, 0.022f));
            Piece(PrimitiveType.Cylinder, root.transform, _mats[axis],
                  u * 0.68f, along, new Vector3(0.075f, 0.09f, 0.075f));

            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = u * 0.38f;
            // Fat in the two off-axis directions so the arrow is grabbable at a
            // glancing camera angle, where its drawn width is a few pixels.
            col.size = u * 0.78f + (Vector3.one - u) * 0.14f;
            return root;
        }

        private GameObject BuildPlane(int axis)
        {
            GameObject root = Root("plane_" + axis, axis, GizmoMode.Move, true, false);
            Vector3 n = Unit(axis);
            Vector3 a = Unit((axis + 1) % 3), b = Unit((axis + 2) % 3);
            Vector3 centre = (a + b) * 0.22f;

            // The quad is drawn as a flattened cube so it is visible from both
            // sides; a Unity Quad is single-sided and vanishes from underneath.
            Piece(PrimitiveType.Cube, root.transform, _mats[axis], centre, Quaternion.identity,
                  (a + b) * 0.20f + n * 0.004f);

            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = centre;
            col.size = (a + b) * 0.20f + n * 0.03f;
            return root;
        }

        private GameObject BuildRing(int axis)
        {
            GameObject root = Root("ring_" + axis, axis, GizmoMode.Rotate, false, false);
            Vector3 u = Unit(axis);
            Quaternion along = Quaternion.FromToRotation(Vector3.up, u);

            // A thin disc rather than a torus: the project has no torus primitive,
            // and a ring built out of segments would be dozens of objects for a
            // shape whose whole job is to say "turn about this axis". The disc is
            // drawn at 30 % alpha so the part stays visible through it.
            var mat = new Material(_mats[axis]);
            Color c = mat.color; c.a = 0.30f; mat.color = c;
            Fade(mat);
            _ringMats.Add(mat);
            Piece(PrimitiveType.Cylinder, root.transform, mat,
                  Vector3.zero, along, new Vector3(1.0f, 0.006f, 1.0f));

            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = (Vector3.one - u) * 1.0f + u * 0.05f;
            return root;
        }

        /// <summary>
        /// One axis of the scale gizmo: a stalk with a cube on the end, the shape
        /// every 3D tool uses for "stretch this way only" — and deliberately not
        /// the arrow the move mode wears, so the two modes cannot be confused at a
        /// glance. Shorter than the move arrow so the two never occupy the same
        /// pixels in a screenshot taken mid-mode-switch.
        /// </summary>
        private GameObject BuildScaleArm(int axis)
        {
            GameObject root = Root("scalearm_" + axis, axis, GizmoMode.Scale, false, false);
            Vector3 u = Unit(axis);
            Quaternion along = Quaternion.FromToRotation(Vector3.up, u);

            Piece(PrimitiveType.Cylinder, root.transform, _mats[axis],
                  u * 0.28f, along, new Vector3(0.022f, 0.28f, 0.022f));
            Piece(PrimitiveType.Cube, root.transform, _mats[axis],
                  u * 0.62f, Quaternion.identity, Vector3.one * 0.10f);

            // <b>Starts clear of the centre cube</b> — from 0.16 out, not from 0.
            // An arm whose collider spans the origin swallows the uniform handle
            // sitting there, and a click meant for it lands on an arm grabbed at
            // the pivot, where the scale ratio has no value and the drag is
            // refused. The user sees a dead centre cube. [BDEF] caught it.
            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.center = u * 0.44f;
            col.size = u * 0.56f + (Vector3.one - u) * 0.14f;
            return root;
        }

        private GameObject BuildScaleCube()
        {
            GameObject root = Root("scale", 1, GizmoMode.Scale, false, true);
            Piece(PrimitiveType.Cube, root.transform, _mats[3],
                  Vector3.zero, Quaternion.identity, Vector3.one * 0.16f);
            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = Vector3.one * 0.22f;
            return root;
        }

        /// <summary>Standard shader → Fade, the transparent setup the rings need.
        /// The same flags <c>CosmeticCatalog.MakeFade</c> writes.</summary>
        private static void Fade(Material m)
        {
            m.SetFloat("_Mode", 2f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>
        /// Light the grabbed handle and put every other one back.
        ///
        /// The opaque handles only. A ring wears a transparent material of its own,
        /// and swapping that for the opaque hot one mid-drag would hide the part
        /// behind the very handle being used to turn it. The uniform scale cube is
        /// already drawn in the hot colour, so there is nothing to change on it.
        /// </summary>
        private void Tint(GizmoHandle hot)
        {
            // Nothing to light before Build() has made the handles, and nothing
            // after OnDestroy has taken the materials away. Detach() reaches here
            // through EndDrag() on a gizmo that has never been attached — which is
            // how the owner puts it away on the frame it is created.
            if (_mats == null) return;

            for (int a = 0; a < 3; a++)
            {
                SetTint(_moveArrow[a], hot != null && hot.mode == GizmoMode.Move
                                       && !hot.plane && hot.axis == a ? _mats[3] : _mats[a]);
                SetTint(_movePlane[a], hot != null && hot.mode == GizmoMode.Move
                                       && hot.plane && hot.axis == a ? _mats[3] : _mats[a]);
                SetTint(_scaleArm[a], hot != null && hot.mode == GizmoMode.Scale
                                      && !hot.uniform && hot.axis == a ? _mats[3] : _mats[a]);
            }
        }

        private static void SetTint(GameObject handle, Material mat)
        {
            if (handle == null || mat == null) return;
            foreach (Renderer r in handle.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterial = mat;
        }

        private void OnDestroy()
        {
            if (_mats != null) foreach (Material m in _mats) Kill(m);
            _mats = null;
            foreach (Material m in _ringMats) Kill(m);
            _ringMats.Clear();
        }

        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
