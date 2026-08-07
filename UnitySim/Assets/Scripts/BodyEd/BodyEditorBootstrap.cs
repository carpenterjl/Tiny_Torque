using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Builds the body-deformation editor scene at runtime: lighting, an orbit
    /// camera, a turntable, one <see cref="DeformableBody"/> on it, and the
    /// <see cref="BodyEditorUI"/>. Drop on one empty GameObject
    /// (Tools ▸ AIHWSim ▸ Create Body Editor Scene).
    ///
    /// <b>Standalone on purpose.</b> This scene builds no <c>CarVehicle</c>, no
    /// <c>Rigidbody</c> and no <c>VehicleDesign</c>; it does not read
    /// <c>GameFlow.ActiveDesign</c> and it cannot write one. That is the point of
    /// introducing it beside the garage rather than inside it — the deformation
    /// pipeline can be built, benched and driven by hand without any risk to the
    /// assembly flow that ships. Porting it into the garage is a later, deliberate
    /// step, and the seam it will cross is <see cref="VehicleLayoutData"/>.
    ///
    /// Same shape as <c>GarageBootstrap</c> and <c>TrackBuilderBootstrap</c>: the
    /// scene asset holds one object, everything else is made in <c>Awake</c>.
    /// </summary>
    public sealed class BodyEditorBootstrap : MonoBehaviour
    {
        public Camera Cam { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public DeformableBody Body { get; private set; }
        public BodyDragReadout Readout { get; private set; }
        public BodyDef CurrentDef { get; private set; }

        private Transform _stand;

        /// <summary>Top of the turntable, in metres. The body is placed so its
        /// wheels rest here.</summary>
        private const float StandTopY = 0.01f;

        private void Awake()
        {
            Readout = new BodyDragReadout();

            BuildLighting();
            BuildCamera();

            var uiGo = new GameObject("BodyEditorUI");
            var ui = uiGo.AddComponent<BodyEditorUI>();
            ui.bootstrap = this;

            var eligible = BodyMeshSource.Eligible();
            SetBody(eligible.Count > 0 ? eligible[0] : null);
        }

        private void BuildLighting()
        {
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.50f);
            Core.Boot.SceneRig.BuildLighting(1.15f, new Vector3(48f, -28f, 0f));
        }

        private void BuildCamera()
        {
            Cam = Core.Boot.SceneRig.CameraOrCreate();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0.07f, 0.08f, 0.11f);
            Cam.nearClipPlane = 0.01f;   // a 0.42 m car inspected from 0.2 m out
            Cam.farClipPlane = 50f;

            Orbit = Cam.gameObject.GetComponent<OrbitCamera>()
                    ?? Cam.gameObject.AddComponent<OrbitCamera>();
            Orbit.yaw = 35f;
            Orbit.pitch = 18f;
        }

        // ---- the body ----------------------------------------------------------------

        /// <summary>
        /// Open a catalogue body, replacing whatever was on the stand. Rebuilds
        /// the turntable to suit and re-frames the camera, then commits once so
        /// the collider and the drag readout exist before the first frame anybody
        /// can click on.
        /// </summary>
        public void SetBody(BodyDef def)
        {
            if (Body != null)
            {
                Body.DeformCommitted -= OnDeformCommitted;
                Destroy(Body.gameObject);
                Body = null;
            }

            CurrentDef = def;
            if (def == null)
            {
                Debug.LogError("[BodyEditorBootstrap] No body to open.");
                return;
            }

            var rig = new GameObject("BodyRig");
            rig.transform.SetParent(transform, false);
            var body = rig.AddComponent<DeformableBody>();
            if (!body.Init(def))
            {
                Destroy(rig);
                return;
            }
            Body = body;
            Body.DeformCommitted += OnDeformCommitted;

            float len = body.BodyLengthM;
            rig.transform.localPosition = new Vector3(0f, StandTopY - body.WheelBottomLocalY, 0f);

            BuildStand(len);
            FrameBody(len);

            Readout.SetBody(def, body.Wheels);
            Body.CommitDeform();
        }

        /// <summary>Load a layout, switching to the body it was sculpted on first
        /// so its vertex offsets still address the mesh they were authored
        /// against.</summary>
        public bool LoadLayout(string fileName)
        {
            VehicleLayoutData d = BodyLayoutLibrary.LoadVehicleFromFile(fileName);
            if (d == null) return false;

            BodyDef want = BodyCatalog.ById(d.carBasePrefabID);
            if (want != null && (CurrentDef == null || want.id != CurrentDef.id))
                SetBody(want);
            else if (want == null && !string.IsNullOrEmpty(d.carBasePrefabID))
                Debug.LogWarning($"[BodyEditorBootstrap] Layout names body " +
                                 $"'{d.carBasePrefabID}', which this build does not have. " +
                                 "Applying to the body that is open.");

            return Body != null && Body.Apply(d);
        }

        private void OnDeformCommitted(DeformableBody body)
        {
            if (body.Collision != null) body.Collision.Rebake(body);
            Readout.Remeasure(body);
        }

        // ---- the stand ---------------------------------------------------------------

        private void BuildStand(float lengthM)
        {
            if (_stand != null) Destroy(_stand.gameObject);

            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Turntable";
            Destroy(go.GetComponent<Collider>());   // the sculpt brush must not hit it
            go.transform.SetParent(transform, false);
            // Unity's cylinder is 2 units tall, so a 0.01 y-scale is a 1 cm slab.
            go.transform.localScale = new Vector3(1.6f * lengthM, StandTopY, 1.6f * lengthM);
            go.GetComponent<MeshRenderer>().sharedMaterial = BodyEdMaterials.Stand();
            _stand = go.transform;
        }

        private void FrameBody(float lengthM)
        {
            Orbit.minDistance = 0.4f * lengthM;
            Orbit.maxDistance = 12f * lengthM;
            Orbit.FocusOn(new Vector3(0f, StandTopY + 0.35f * lengthM, 0f), 2.4f * lengthM);
        }
    }
}
