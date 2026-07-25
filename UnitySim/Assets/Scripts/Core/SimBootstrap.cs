using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Builds the entire sim scene from primitives at runtime: ground, the
    /// differential-drive robot, a chase camera with the graph overlay, input,
    /// and the SimulationRunner. Drop this component on one empty GameObject
    /// (see Tools > AIHWSim > Create Bootstrap Scene) and press Play.
    ///
    /// Building in code avoids hand-authored scene/prefab YAML and keeps the
    /// whole setup reviewable in one place.
    /// </summary>
    public sealed class SimBootstrap : MonoBehaviour
    {
        [Header("Rates")]
        public int physicsRateHz = 500;
        public int controlRateHz = 100;

        [Header("Options")]
        public bool logCsv = true;
        public bool autoReloadControllerOnChange = false;

        private SimulationRunner _runner;

        private void Awake()
        {
            // All horizontal forces come from the vehicle's traction model, so
            // ground/chassis contact must be frictionless (normal support only).
            var frictionless = new PhysicsMaterial("Frictionless")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum,
            };

            BuildLighting();
            BuildGround(frictionless);
            var vehicle = BuildVehicle(frictionless);
            var input = BuildInput();
            var (cam, graph) = BuildCameraAndGraph();

            var runnerGo = new GameObject("SimulationRunner");
            _runner = runnerGo.AddComponent<SimulationRunner>();
            _runner.physicsRateHz = physicsRateHz;
            _runner.controlRateHz = controlRateHz;
            // Telemetry logging is opt-in (Options → "Log sensor/telemetry data").
            _runner.logCsv = logCsv && Persistence.SettingsStore.Current.logTelemetry;
            _runner.autoReloadOnChange = autoReloadControllerOnChange;
            _runner.vehicleBehaviour = vehicle;
            _runner.inputBehaviour = input;
            _runner.graph = graph;
            _runner.graphProfile = SimulationRunner.GraphProfile.DiffDrive;
            // This scene is the autonomous C-controller demo; start closed-loop
            // (press M to take over manually).
            _runner.startInManual = false;

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = vehicle.transform;

            var pauseGo = new GameObject("PauseMenu");
            var pause = pauseGo.AddComponent<PauseMenu>();
            pause.runner = _runner;
            pause.tunableBehaviour = vehicle; // diff-drive isn't ITunable yet; Tune is hidden
        }

        private static void BuildLighting()
        {
            if (FindFirstObjectByType<Light>() != null) return;
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static void BuildGround(PhysicsMaterial mat)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(10f, 1f, 10f); // 100 m plane
            ground.GetComponent<Collider>().material = mat;
            var r = ground.GetComponent<Renderer>();
            r.material.color = new Color(0.20f, 0.22f, 0.25f);
        }

        private DifferentialDriveVehicle BuildVehicle(PhysicsMaterial mat)
        {
            var chassis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chassis.name = "DiffDriveRobot";
            chassis.transform.localScale = new Vector3(0.30f, 0.10f, 0.40f);
            chassis.transform.position = new Vector3(0f, 0.10f, 0f);
            chassis.GetComponent<Collider>().material = mat;
            chassis.GetComponent<Renderer>().material.color = new Color(0.85f, 0.35f, 0.25f);

            var rb = chassis.AddComponent<Rigidbody>();
            rb.mass = 2.0f;
            rb.linearDamping = 0f;
            rb.angularDamping = 0.05f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.centerOfMass = new Vector3(0f, -0.05f, 0f); // low CoM, resists tipping

            var vehicle = chassis.AddComponent<DifferentialDriveVehicle>();

            // Visual wheels (no colliders) on axle hubs so the spin looks right.
            var leftHub = MakeWheel(chassis.transform, -0.5f * vehicle.trackWidth);
            var rightHub = MakeWheel(chassis.transform, +0.5f * vehicle.trackWidth);
            vehicle.Configure(leftHub, rightHub);

            return vehicle;
        }

        private static Transform MakeWheel(Transform parent, float xOffset)
        {
            // Hub is axle-aligned with the chassis; we spin the hub about local X.
            var hub = new GameObject(xOffset < 0 ? "WheelHub_L" : "WheelHub_R").transform;
            hub.SetParent(parent, false);
            hub.localPosition = new Vector3(xOffset, -0.05f, 0f);

            var wheel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            wheel.name = "WheelViz";
            Destroy(wheel.GetComponent<Collider>());
            wheel.transform.SetParent(hub, false);
            // Cylinder axis is local Y; rotate so it points along X (the axle).
            wheel.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            wheel.transform.localScale = new Vector3(0.10f, 0.02f, 0.10f); // dia 0.1, thin
            wheel.GetComponent<Renderer>().material.color = new Color(0.15f, 0.15f, 0.15f);
            return hub;
        }

        private static VehicleInput BuildInput()
        {
            var go = new GameObject("VehicleInput");
            return go.AddComponent<VehicleInput>();
        }

        private static (Camera, GraphOverlay) BuildCameraAndGraph()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.transform.position = new Vector3(0f, 3f, -4f);
            cam.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            var graph = cam.gameObject.GetComponent<GraphOverlay>();
            if (graph == null) graph = cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }

        private void OnGUI()
        {
            const float w = 210f, h = 54f;
            var area = new Rect(Screen.width - w - 10f, 10f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            string status = _runner != null && _runner.ControllerReady
                ? "controller: LOADED" : "controller: OPEN-LOOP";
            GUILayout.Label(status);
            if (GUILayout.Button("Reload Controller DLL"))
                _runner?.ReloadController();

            GUILayout.EndArea();
        }
    }
}
