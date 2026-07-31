using AIHWSim.Garage;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// The one place that knows how to stand a full-scale debug vehicle up as a
    /// drivable rig. Extracted from <see cref="PhysicsDebugBootstrap"/> so that
    /// bootstrap and <see cref="DebugVehicleSpawner"/> cannot drift apart — the
    /// rules below are measurement decisions, and a second copy of them is a
    /// second copy that can be edited without the first.
    ///
    /// <b>Assists are forced off here, not by the caller.</b>
    /// <c>SimulationRunner.SyncAssistGate</c> turns the arcade assists ON
    /// whenever the mode is Manual, and every path that drives this car runs
    /// Manual. ABS and traction control silently rewriting brake and drive
    /// torque is the difference between measuring a tyre model and measuring a
    /// driver aid, so it is the rig builder's job rather than a thing each
    /// caller is trusted to remember.
    ///
    /// The build is deliberately split in two, and the split preserves an
    /// ordering that matters: the camera and its <see cref="GraphOverlay"/> need
    /// the car's transform, while <see cref="SimulationRunner"/> wants the graph
    /// at the moment it is added. So callers do
    /// <see cref="BuildCar"/> → build camera → <see cref="AttachRunner"/>.
    /// </summary>
    public static class DebugVehicleRig
    {
        public struct Rig
        {
            public CarVehicle car;
            public CarInput input;
            public SensorRig sensors;
            public GameObject root;
            public SimulationRunner runner;
        }

        /// <summary>
        /// Car + player input, assists off. No camera, no runner — see the class
        /// comment for why those are the caller's next two steps.
        /// </summary>
        public static Rig BuildCar(VehicleDesign design, Vector3 pos, Quaternion rot)
        {
            var built = VehicleFactory.Build(design, pos, rot, previewKinematic: false);
            var car = built.car;
            car.SetSpawn(pos, rot);
            ForceAssistsOff(car);

            var input = car.gameObject.AddComponent<CarInput>();
            input.car = car;

            return new Rig
            {
                car = car,
                input = input,
                sensors = built.rig,
                root = built.root,
            };
        }

        /// <summary>
        /// The simulation loop and the extra physics channels.
        ///
        /// <b>Telemetry ordering is load-bearing.</b> <c>CsvLogger.Begin</c>
        /// snapshots the channel list once while <c>Commit</c> walks the live
        /// set, so <see cref="PhysicsDebugTelemetry"/> has to be registered
        /// before <c>SimulationRunner.Start</c> enables logging — i.e. in the
        /// same Awake that added the runner, which is what this method is.
        /// </summary>
        public static void AttachRunner(ref Rig rig, GraphOverlay graph,
                                        int physicsRateHz, int controlRateHz,
                                        bool logCsv)
        {
            var runnerGo = new GameObject("SimulationRunner");
            var runner = runnerGo.AddComponent<SimulationRunner>();
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.logCsv = logCsv;
            runner.vehicleBehaviour = rig.car;
            runner.inputBehaviour = rig.input;
            runner.sensorRig = rig.sensors;
            runner.graph = graph;
            runner.startInManual = true;
            runner.loadControllerDll = false;

            var tele = runnerGo.AddComponent<PhysicsDebugTelemetry>();
            tele.Bind(runner, rig.car);

            rig.runner = runner;
        }

        /// <summary>
        /// Strip every arcade helper off a car that already exists — the case
        /// where a scene's own bootstrap built it and we are taking it over.
        ///
        /// <see cref="HandlingFloor"/> must be DESTROYED rather than have its
        /// values overwritten: it re-asserts the session's handling floor every
        /// frame, so a one-shot write to <c>car.assists</c> would be undone on
        /// the next Update and the car would quietly regain traction control.
        ///
        /// <b>Zeroing the STRENGTHS is what disables the assists</b>; the
        /// <c>assistsActive</c> flag is only a gate, and
        /// <c>SimulationRunner.SyncAssistGate</c> owns it — it sets it true every
        /// frame the mode is Manual, so the write below is cosmetic and a probe
        /// will still see the flag on. Each assist multiplies by its own strength
        /// (steer / stability / traction / abs), so with all four at zero the
        /// gate has nothing to open. The one way they come back is the pause
        /// menu's Settings panel, which applies live slider values on purpose.
        /// </summary>
        public static void ForceAssistsOff(CarVehicle car)
        {
            if (car == null) return;
            var floor = car.GetComponent<HandlingFloor>();
            if (floor != null) Object.Destroy(floor);
            car.assists = default;
            car.assistsActive = false;
        }
    }
}
