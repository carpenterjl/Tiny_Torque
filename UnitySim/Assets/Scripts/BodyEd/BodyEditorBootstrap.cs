using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Builds Vehicle Studio at runtime: lighting, an orbit camera, a turntable, a
    /// <see cref="StudioVehicle"/> on it, the transform gizmo, and the UI. Drop on
    /// one empty GameObject (Tools ▸ AIHWSim ▸ Create Body Editor Scene).
    ///
    /// <b>It builds no car and starts no physics.</b> The studio shows a body, its
    /// parts and its paint; the moment a real vehicle is wanted — to drive, to save
    /// — the layout goes through <see cref="StudioSession"/> and the existing
    /// <c>VehicleFactory</c> builds it. That keeps one construction path for cars
    /// and leaves this scene free to be about shape.
    ///
    /// Same shape as <c>GarageBootstrap</c> and <c>TrackBuilderBootstrap</c>: the
    /// scene asset holds one object, everything else is made in <c>Awake</c>.
    /// </summary>
    public sealed class BodyEditorBootstrap : MonoBehaviour
    {
        public Camera Cam { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public StudioVehicle Vehicle { get; private set; }
        public TransformGizmo Gizmo { get; private set; }
        public BodyDragReadout Readout { get; private set; }

        public DeformableBody Body => Vehicle != null ? Vehicle.Body : null;
        public BodyDef CurrentDef => Vehicle != null ? Vehicle.Def : null;

        private Transform _stand;

        /// <summary>Top of the turntable, in metres. The vehicle is placed so its
        /// wheels rest here.</summary>
        private const float StandTopY = 0.01f;

        private void Awake()
        {
            Readout = new BodyDragReadout();

            BuildLighting();
            BuildCamera();
            BuildVehicle();
            BuildGizmo();

            var uiGo = new GameObject("BodyEditorUI");
            var ui = uiGo.AddComponent<BodyEditorUI>();
            ui.bootstrap = this;

            // Coming back from a test drive: reopen the car exactly as it left.
            VehicleLayoutData pending = StudioSession.TakePending();
            if (pending != null)
            {
                Debug.Log("[BodyEditorBootstrap] Reopening the vehicle that was test driven.");
                LoadLayout(pending);
            }
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

        private void BuildVehicle()
        {
            var go = new GameObject("Vehicle");
            go.transform.SetParent(transform, false);
            Vehicle = go.AddComponent<StudioVehicle>();
            Vehicle.DeformCommitted += OnDeformCommitted;

            var eligible = BodyMeshSource.Eligible();
            SetBody(eligible.Count > 0 ? eligible[0] : null);
        }

        private void BuildGizmo()
        {
            var go = new GameObject("Gizmo");
            go.transform.SetParent(transform, false);
            Gizmo = go.AddComponent<TransformGizmo>();
            Gizmo.Detach();
        }

        // ---- the body ----------------------------------------------------------------

        /// <summary>
        /// Open a catalogue body, keeping every placed part. Rebuilds the turntable
        /// to suit and re-frames the camera, then commits once so the collider and
        /// the drag readout exist before the first frame anybody can click on.
        /// </summary>
        public void SetBody(BodyDef def)
        {
            if (def == null)
            {
                Debug.LogError("[BodyEditorBootstrap] No body to open.");
                return;
            }
            if (Vehicle == null || !Vehicle.SetBody(def)) return;

            float len = Vehicle.Body.BodyLengthM;
            // The VEHICLE root is what gets lifted, not the body — so the parts and
            // the body agree about where the middle of the car is, and a part's
            // saved position means the same thing here and on the track.
            Vehicle.transform.localPosition =
                new Vector3(0f, StandTopY - Vehicle.Body.WheelBottomLocalY, 0f);

            BuildStand(len);
            FrameBody(len);

            Readout.SetBody(def, Vehicle.Body.Wheels);
            Vehicle.Body.CommitDeform();
        }

        /// <summary>Load a layout by file name, switching to the body it was
        /// sculpted on first so its vertex offsets still address the mesh they were
        /// authored against.</summary>
        public bool LoadLayout(string fileName)
        {
            VehicleLayoutData d = BodyLayoutLibrary.LoadVehicleFromFile(fileName);
            return d != null && LoadLayout(d);
        }

        /// <summary>Apply a layout that is already in hand.</summary>
        public bool LoadLayout(VehicleLayoutData d)
        {
            if (d == null || Vehicle == null) return false;

            BodyDef want = BodyCatalog.ById(d.carBasePrefabID);
            if (want != null && (CurrentDef == null || want.id != CurrentDef.id))
                SetBody(want);
            else if (want == null && !string.IsNullOrEmpty(d.carBasePrefabID))
                Debug.LogWarning($"[BodyEditorBootstrap] Layout names body " +
                                 $"'{d.carBasePrefabID}', which this build does not have. " +
                                 "Applying to the body that is open.");

            return Vehicle.Apply(d);
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
