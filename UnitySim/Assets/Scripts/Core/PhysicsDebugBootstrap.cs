using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Builds the physics-verification scene from primitives at runtime: a long
    /// flat straight, a measured slope, one full-scale VW Tiguan, a chase camera
    /// and the telemetry overlays. Drop this on one empty GameObject (see
    /// <c>Tools > AIHWSim > Create Physics Debug Scene</c>) and press Play.
    ///
    /// Modelled on <see cref="SimBootstrap"/>, and it keeps that file's central
    /// insight verbatim: <b>all horizontal force comes from the vehicle's
    /// traction model, so ground contact must be frictionless</b> (normal
    /// support only). Friction lives in the tyre, not in the surface. A ground
    /// PhysicsMaterial with grip would add a second, unmodelled friction path on
    /// top of the one being measured.
    ///
    /// Three choices here are measurement decisions, not style:
    ///
    /// <b>400 Hz, not SimBootstrap's 500.</b> <see cref="TrackBootstrap"/> runs
    /// at 400 and the Opus mission is gated on it, so measuring the Tiguan at
    /// 500 would measure a different integrator from the one the project
    /// actually ships. P9 re-runs the key tests at 200/400/800 to show the
    /// answers do not depend on that choice — but they have to start from the
    /// same place.
    ///
    /// <b>No PauseMenu.</b> <c>CarVehicle.GetTunables()</c> clamps the
    /// brake-torque slider to [0.1, 3] N.m. One nudge would take a 2400 N.m
    /// brake to 3 and quietly invalidate every braking number afterwards.
    ///
    /// <b>No assists.</b> <c>SimulationRunner.SyncAssistGate</c> turns the arcade
    /// assists ON whenever the mode is Manual, and this scene runs Manual. ABS
    /// and traction control silently rewriting brake and drive torque is the
    /// difference between measuring a tyre model and measuring a driver aid.
    /// </summary>
    public sealed class PhysicsDebugBootstrap : MonoBehaviour
    {
        [Header("Rates")]
        [Tooltip("400 to match TrackBootstrap, which is what the Opus gate runs on.")]
        public int physicsRateHz = 400;
        public int controlRateHz = 100;

        [Header("Test surfaces")]
        [Tooltip("Straight length (m). P1's coastdown from 32 to 22 m/s covers ~1150 m.")]
        public float straightLength = 2400f;
        public float straightWidth = 240f;
        [Tooltip("Grade of the park-brake slope, as a fraction (0.10 = 10 %).")]
        public float slopeGrade = 0.10f;

        [Header("Options")]
        public bool logCsv = true;

        private SimulationRunner _runner;
        private CarVehicle _car;

        private void Awake()
        {
            // Normal support only — see the class comment. This is the same
            // material SimBootstrap builds, for the same reason.
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
            BuildSlope(frictionless);

            // Spawn with the wheels' rest drop already applied, so the car settles
            // rather than falling: chassis origin sits loaded-radius + drop up.
            var spawn = new Vector3(0f, DebugVehicles.TiguanChassisRestY, 0f);
            // Assists-off and the telemetry ordering live in DebugVehicleRig, so
            // this scene and DebugVehicleSpawner cannot disagree about them.
            var rig = DebugVehicleRig.BuildCar(DebugVehicles.VwTiguan(), spawn,
                                               Quaternion.identity);
            _car = rig.car;

            var (cam, graph) = BuildCameraAndGraph();
            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = _car.transform;

            DebugVehicleRig.AttachRunner(ref rig, graph, physicsRateHz, controlRateHz, logCsv);
            _runner = rig.runner;
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

        /// <summary>
        /// A scaled Cube, not SimBootstrap's Plane.
        ///
        /// A Plane primitive is a 10x10 quad mesh behind a MeshCollider; stretched
        /// to 2.4 km that is a raycast into a mesh with 240 m quads, per wheel,
        /// per step, at 400 Hz. A Box is an exact analytic collider, and — the
        /// part that actually matters — it accepts the per-collider contactOffset
        /// the Tiguan needs to pair with its own 2 cm chassis offset.
        ///
        /// Top face lands exactly at y = 0 by construction.
        /// </summary>
        private void BuildGround(PhysicsMaterial mat)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            g.name = "Ground";
            const float thick = 2f;
            g.transform.localScale = new Vector3(straightWidth, thick, straightLength);
            g.transform.position = new Vector3(0f, -thick * 0.5f, straightLength * 0.5f - 200f);
            var col = g.GetComponent<Collider>();
            col.material = mat;
            col.contactOffset = 0.02f;   // matched to the Tiguan's chassis box
            g.GetComponent<Renderer>().material.color = new Color(0.20f, 0.22f, 0.25f);
        }

        /// <summary>A measured grade for the park-brake hold test (P6b), which is
        /// the only check on whether PhysX's sticky-tyre constraint can actually
        /// hold 1500 kg. Frictionless like everything else — the holding force
        /// has to come from the tyre model, or the test proves nothing.</summary>
        private void BuildSlope(PhysicsMaterial mat)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Cube);
            s.name = "Slope";
            const float thick = 2f;
            float len = 200f, deg = Mathf.Atan(slopeGrade) * Mathf.Rad2Deg;
            s.transform.localScale = new Vector3(40f, thick, len);
            s.transform.rotation = Quaternion.Euler(-deg, 0f, 0f);
            // Lower end meets y = 0 at z = -150; the surface climbs from there.
            s.transform.position = new Vector3(0f, len * 0.5f * slopeGrade - thick * 0.5f,
                                               -150f - len * 0.5f);
            var col = s.GetComponent<Collider>();
            col.material = mat;
            col.contactOffset = 0.02f;
            s.GetComponent<Renderer>().material.color = new Color(0.26f, 0.24f, 0.22f);
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
            cam.transform.position = new Vector3(0f, 4f, -10f);
            cam.transform.rotation = Quaternion.Euler(12f, 0f, 0f);
            cam.farClipPlane = 3000f;   // the straight is 2.4 km long
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            cam.clearFlags = CameraClearFlags.SolidColor;

            var graph = cam.gameObject.GetComponent<GraphOverlay>();
            if (graph == null) graph = cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }

        private void OnGUI()
        {
            const float w = 300f, h = 108f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 10f, 10f, w, h), GUI.skin.box);
            GUILayout.Label("PHYSICS DEBUG — VW Tiguan 1.4 TSI (1:1)");
            if (_car != null)
            {
                var rb = _car.GetComponent<Rigidbody>();
                float v = rb != null ? rb.linearVelocity.magnitude : 0f;
                GUILayout.Label($"speed {v:0.00} m/s   ({v * 3.6f:0.0} km/h)");
                GUILayout.Label($"mass {(rb != null ? rb.mass : 0f):0.0} kg   "
                                + $"ride {_car.transform.position.y - 0.5615f:0.000} m");
            }
            GUILayout.Label("assists OFF · frictionless ground · tyre model only");
            GUILayout.EndArea();
        }
    }
}
