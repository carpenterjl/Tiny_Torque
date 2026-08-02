using AIHWSim.Garage;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Stands the free-flight scene up: airfield, aircraft, cameras, HUD,
    /// telemetry and the simulation loop — by adopting an authored scene when one
    /// exists, or by building everything at runtime when it does not. Create the
    /// authored scene with Tools > AIHWSim > Create RC Plane Scene and press Play.
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
    ///
    /// <b>Two ways in.</b> If a complete <see cref="FlightSceneDescriptor"/> sits
    /// beside this component, the scene was AUTHORED by the editor builder and is
    /// adopted: references come from the descriptor, and the adopt path fills
    /// nulls only — inspector values are the truth. Otherwise this Awake builds
    /// the whole scene at runtime exactly as it always has (the legacy
    /// one-GameObject scene, and the fallback when an authored scene has been
    /// half-deleted).
    ///
    /// The <see cref="DefaultExecutionOrder"/> is for the authored path: the
    /// runner's Awake must have created the telemetry hub, and the plane's Awake
    /// must have configured its rigidbody, before this Awake binds telemetry and
    /// launches. In the build path nothing else exists at Awake time, so the
    /// attribute is a no-op there.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class RcPlaneBootstrap : MonoBehaviour
    {
        public enum Aircraft { SportRc = 0, HydraVtol = 1 }

        [Header("Aircraft")]
        [Tooltip("Which factory the CODE-BUILT path flies. In an authored scene "
                 + "the plane in the hierarchy is the truth — rebuild the scene "
                 + "from the Tools menu to change aircraft there.")]
        public Aircraft aircraft = Aircraft.SportRc;

        [Header("Rates")]
        [Tooltip("400 Hz matches the car tests. The panel model is stable well "
                 + "below this; the rate is set by the propeller shaft, not the air. "
                 + "Authored scenes: the runner's own inspector values win; these "
                 + "apply to the code-built path only.")]
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
        private FlightSceneDescriptor _descriptor;
        private SasController _sas;

        private void Awake()
        {
            var d = GetComponent<FlightSceneDescriptor>();
            if (d != null && d.IsComplete)
            {
                _descriptor = d;
                AdoptAuthored(d);
            }
            else
            {
                if (d != null)
                    Debug.LogWarning("[RcPlane] FlightSceneDescriptor present but "
                        + "incomplete (a required reference is null — was part of "
                        + "the authored scene deleted?). Building from code; the "
                        + "authored leftovers stay in the hierarchy.");
                BuildFromCode();
            }

            if (_spec != null && _spec.IsJet) RigJetForFlight();

            _rig.input.ResetRequested += Respawn;
            _rig.input.ViewToggleRequested += () => _cameras.Cycle();
            if (_rig.input.Human != null) _rig.input.Human.invertElevator = invertElevator;

            Launch();
        }

        /// <summary>Everything a VTOL start needs beyond what a trainer start
        /// needs — shared by the adopt and build paths, because a jet is a jet
        /// however the scene got here. SAS engages holding attitude, the
        /// cockpit is rigged for the hover (nozzles down, throttle at the
        /// PREDICTED W/T fraction — the first prediction the scene itself
        /// tests), and the target range comes up if the scene does not already
        /// hold one.</summary>
        private void RigJetForFlight()
        {
            if (_sas != null) _sas.SetMode(SasController.SasMode.StabilityAssist);

            if (_rig.input.Human != null)
            {
                _rig.input.Human.SetNozzle(1f);
                _rig.input.Human.SetThrottle(
                    _spec.TotalMass * 9.80665f / _spec.jet.maxThrustN);
            }
            // A hand launch at trainer speed would start the jet well below its
            // own stall; the hover start is the honest one.
            launchAirspeed = 0f;

            if (Object.FindFirstObjectByType<Combat.TargetSpawner>() == null)
                new GameObject("Targets").AddComponent<Combat.TargetSpawner>();

            // The armament, attached HERE and nowhere else — the SasController
            // rule again: nothing test-reachable ever grows a weapon.
            var lockOn = _rig.root.AddComponent<Combat.LockOnController>();
            lockOn.plane = _rig.plane;

            var weapons = _rig.root.AddComponent<Combat.WeaponsController>();
            weapons.plane = _rig.plane;
            weapons.lockOn = lockOn;
            weapons.sas = _sas;

            var turret = gameObject.AddComponent<Combat.TurretController>();
            turret.plane = _rig.plane;
            turret.cameras = _cameras;
            turret.sas = _sas;

            var whud = gameObject.AddComponent<Combat.WeaponHud>();
            whud.plane = _rig.plane;
            whud.weapons = weapons;
            whud.lockOn = lockOn;
            whud.turret = turret;
        }

        /// <summary>The authored path: the scene already holds everything, wired
        /// by the editor builder. Fill NULLS only — an inspector value, hand-set
        /// or authored, is never overwritten here. The two runtime-only acts are
        /// binding telemetry (channel registration cannot be serialized) and the
        /// HUD bind.</summary>
        private void AdoptAuthored(FlightSceneDescriptor d)
        {
            _spec = d.plane.spec;
            _rig = new DebugPlaneRig.Rig
            {
                plane = d.plane,
                input = d.input,
                root = d.plane.gameObject,
                runner = d.runner,
            };
            _cameras = d.cameraRig;

            d.telemetry.Bind(d.runner, d.plane);

            _sas = d.sas;
            if (_cameras.target == null) _cameras.target = d.plane.transform;
            if (d.sas.target == null)
                d.sas.target = d.sasTarget != null ? d.sasTarget : FarPylon();
            if (d.input.sas == null) d.input.sas = d.sas;

            var hud = GetComponent<FlightHud>();
            if (hud == null) hud = gameObject.AddComponent<FlightHud>();
            hud.Bind(d.plane, _cameras, d.sas, d.sas.target);
        }

        /// <summary>The legacy path, verbatim: build the whole scene at runtime.
        /// Still the only path the scripted tests and the old one-GameObject
        /// scene ever take.</summary>
        private void BuildFromCode()
        {
            _spec = aircraft == Aircraft.HydraVtol
                ? DebugJets.HydraVtol()
                : DebugPlanes.SportRc();

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

            // Stability assist is attached HERE and nowhere else. DebugPlaneRig
            // builds the aeroplane for the scripted tests too, and an autopilot
            // quietly flying those would turn the [AERO] gate into a measurement of
            // this file.
            var sas = _rig.root.AddComponent<SasController>();
            sas.plane = _rig.plane;
            sas.target = FarPylon();
            _rig.input.sas = sas;
            _sas = sas;

            if (aircraft == Aircraft.HydraVtol)
            {
                // TUNED hover starting points, and only the rate terms: at the
                // hover there is no airflow, so the aerodynamic rate damping the
                // trainer's loops implicitly leaned on is simply absent — the
                // puffers must supply it, which wants a heavier rate gain. The
                // proportional gains carry over unchanged. Unflown until the J2
                // hover check; adjust there, not here first. (The authored jet
                // scene bakes its own values; this is the code path's copy.)
                sas.rollRateGain = 0.008f;
                sas.pitchRateGain = 0.020f;
            }

            var hud = gameObject.AddComponent<FlightHud>();
            hud.Bind(_rig.plane, _cameras, sas, sas.target);
        }

        /// <summary>The pylon at the far end of the strip, as something for the SAS
        /// target mode and the navball's target marker to point at. Found by name
        /// because the environment builder does not hand its scenery back; a miss
        /// is harmless — target mode greys itself out and the marker disappears.</summary>
        private static Transform FarPylon()
        {
            var pylons = GameObject.Find("Pylons");
            if (pylons == null) return null;

            Transform best = null;
            foreach (Transform t in pylons.transform)
                if (best == null || t.position.z > best.position.z) best = t;
            return best;
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
            // An authored spawn marker wins on position and heading. A hand
            // launch still takes its altitude from the bootstrap field — the
            // marker says WHERE over the field, not how high the throw is.
            if (_descriptor != null && _descriptor.spawnPoint != null)
            {
                Transform t = _descriptor.spawnPoint;
                Vector3 p = t.position;
                if (handLaunch) p.y = launchAltitude;
                return (p, t.rotation);
            }

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
