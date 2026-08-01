using AIHWSim.Garage;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Builds the whole free-flight scene at runtime: airfield, aircraft, cameras,
    /// HUD, telemetry and the simulation loop. Drop this on one empty GameObject
    /// (Tools > AIHWSim > Create RC Plane Scene) and press Play.
    ///
    /// Modelled on <see cref="PhysicsDebugBootstrap"/>, and it keeps that file's
    /// load-bearing order: <b>aircraft → camera → runner</b>. The camera needs a
    /// transform to follow, the runner wants the graph overlay at the moment it is
    /// added, and <see cref="Telemetry.FlightTelemetry"/> has to register its
    /// channels before <c>SimulationRunner.Start</c> — <c>CsvLogger.Begin</c>
    /// snapshots the column list exactly once, and a channel that arrives later
    /// writes a row wider than its header.
    ///
    /// <b>This scene never terminates and must never be run headless.</b> It has no
    /// verdict and no exit condition, so it appears in no test array and carries no
    /// autorun component.
    /// </summary>
    public sealed class RcPlaneBootstrap : MonoBehaviour
    {
        [Header("Rates")]
        [Tooltip("400 Hz matches the car tests. The panel model is stable well "
                 + "below this; the rate is set by the propeller shaft, not the air.")]
        public int physicsRateHz = 400;
        public int controlRateHz = 100;

        [Header("Start")]
        [Tooltip("Hand-launch in the air rather than starting on the wheels. "
                 + "Ground handling is a declared simplification, so the air is the "
                 + "honest place to begin.")]
        public bool handLaunch = true;
        public float launchAltitude = 40f;
        public float launchAirspeed = 15f;

        [Header("Options")]
        public bool logCsv = true;
        [Tooltip("Keyboard elevator sense. Default matches a transmitter: pull "
                 + "back (Down arrow) to raise the nose.")]
        public bool invertElevator = false;

        private DebugPlaneRig.Rig _rig;
        private FlightCameraRig _cameras;
        private AircraftSpec _spec;

        private void Awake()
        {
            _spec = DebugPlanes.SportRc();

            var (cam, graph) = FlightTestEnvironment.Build(
                FlightTestEnvironment.EnvSpec.Airfield());

            // The runway, pylons and ring were created moments ago; anything that
            // resolves a spawn by raycast reads the physics scene, which has not
            // been told about those colliders yet.
            Physics.SyncTransforms();

            var (pos, rot) = SpawnPose();
            _rig = DebugPlaneRig.BuildPlane(_spec, pos, rot);

            _cameras = cam.gameObject.AddComponent<FlightCameraRig>();
            _cameras.target = _rig.plane.transform;
            // Stand the pilot beside the threshold, looking down the strip.
            _cameras.pilotPosition = new Vector3(-14f, 1.6f, -70f);

            DebugPlaneRig.AttachRunner(ref _rig, graph, physicsRateHz, controlRateHz, logCsv);
            _rig.runner.logLabel = "RcPlane";

            var hud = gameObject.AddComponent<FlightHud>();
            hud.Bind(_rig.plane, _cameras);

            _rig.input.ResetRequested += Respawn;
            _rig.input.ViewToggleRequested += () => _cameras.Cycle();
            if (_rig.input.Human != null) _rig.input.Human.invertElevator = invertElevator;

            Launch();
        }

        private void Start()
        {
            if (_rig.runner == null || _rig.runner.graph == null) return;
            var g = _rig.runner.graph;
            g.ClearPanes();
            g.AddPane("airspeed (m/s)", "air/tas");
            g.AddPane("alpha (deg)", "air/alpha_deg");
            g.AddPane("altitude (m)", "air/altitude_m");
            g.AddPane("load (g)", "air/load_g");
        }

        private (Vector3, Quaternion) SpawnPose()
        {
            if (handLaunch)
                return FlightTestEnvironment.LaunchPose(launchAltitude);
            return FlightTestEnvironment.RunwayPose(
                DebugPlaneRig.GearHeight(_spec) + 0.11f);   // clear of the runway slab
        }

        private void Respawn()
        {
            var (pos, rot) = SpawnPose();
            _rig.plane.SetSpawn(pos, rot);
            Launch();
        }

        private void Launch()
        {
            var (pos, rot) = SpawnPose();
            if (handLaunch) _rig.plane.LaunchAt(pos, rot, launchAirspeed);
            else _rig.plane.ResetVehicleTo(pos, rot);
        }
    }
}
