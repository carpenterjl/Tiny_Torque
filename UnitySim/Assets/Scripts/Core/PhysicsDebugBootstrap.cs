using AIHWSim.Core.PhysicsTests;
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
            // The world — frictionless ground, the slope, lighting, camera and
            // graph — lives in PhysicsTestEnvironment, so this scene and the ten
            // measurement scenes cannot drift apart on the surface they measure
            // against. Assists-off and the telemetry ordering live in
            // DebugVehicleRig for the same reason.
            // A descriptor in this scene owns the solver and the step; without
            // one the fields above stand, which is what every existing copy of
            // this scene has.
            var driving = Boot.DrivingSceneDescriptor.Find();
            if (driving != null)
            {
                physicsRateHz = driving.PhysicsRate(physicsRateHz);
                controlRateHz = driving.ControlRate(controlRateHz);
            }

            var env = PhysicsTestEnvironment.EnvSpec.Default();
            env.straightLength = straightLength;
            env.straightWidth = straightWidth;
            env.buildSlope = true;
            env.slopeGrade = slopeGrade;
            var (cam, graph) = PhysicsTestEnvironment.Build(env);

            // Spawn with the wheels' rest drop already applied, so the car settles
            // rather than falling: chassis origin sits loaded-radius + drop up.
            var spawn = new Vector3(0f, DebugVehicles.TiguanChassisRestY, 0f);
            var rig = DebugVehicleRig.BuildCar(DebugVehicles.VwTiguan(), spawn,
                                               Quaternion.identity);
            _car = rig.car;

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = _car.transform;

            DebugVehicleRig.AttachRunner(ref rig, graph, physicsRateHz, controlRateHz, logCsv);
            _runner = rig.runner;
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
