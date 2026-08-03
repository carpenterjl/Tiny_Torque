using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Boot
{
    /// <summary>
    /// The <see cref="SimulationRunner"/> field block that every car-building
    /// path in the project wrote out by hand.
    ///
    /// This is the car-side twin of
    /// <see cref="Flight.DebugPlaneRig.ConfigureRunner"/>, which the flight work
    /// extracted for exactly this reason and the car side never did — so the
    /// race rig, the attract-loop bots and the physics-debug rig each carried
    /// their own copy, and a new runner setting had to be remembered in three
    /// places or it existed in only some of the worlds.
    ///
    /// <b>It writes the intersection and nothing else.</b> These nine fields are
    /// the ones every car call site sets, and they are all it sets. Everything a
    /// caller does differently — the DLL path and mode-toggle flags on the solo
    /// human, <c>loggable</c> on a bot, the graph profile, the actuation delay,
    /// the log label — stays at the call site, in the branch whose comment
    /// explains it. Absorbing those into a parameter list would put four
    /// unrelated decisions behind one name and make the helper's defaults a
    /// second, silent source of truth beside
    /// <see cref="SimulationRunner"/>'s own field initialisers.
    ///
    /// That last point is the trap worth naming: the physics-debug rig leaves
    /// <c>allowModeToggle</c>, <c>showModeBox</c> and <c>graphProfile</c>
    /// untouched, so they keep the component's defaults (on, on, DiffDrive). A
    /// helper that "helpfully" wrote sensible values for them would change that
    /// car's behaviour while looking like a pure move.
    ///
    /// <b>Deliberately not a caller:</b> <c>TrackBootstrap.BuildLanRig</c>, which
    /// is owner-authoritative and gated by a two-machine test this cannot run,
    /// and <c>SimBootstrap</c>, whose vehicle is the diff-drive robot rather
    /// than a car — a rig named for cars not covering it is the right answer,
    /// not a gap.
    /// </summary>
    public static class CarRunnerRig
    {
        /// <summary>
        /// Point a runner at a car. The runner must already exist: callers name
        /// its GameObject themselves (<c>SimulationRunner_Bot3</c>,
        /// <c>MenuBotRunner0</c>, <c>SimulationRunner_P2</c>) and several attach
        /// a telemetry component to the same object immediately afterwards,
        /// which has to happen before <c>SimulationRunner.Start</c> enables
        /// logging.
        ///
        /// <c>graph</c> and <c>sensors</c> are nullable and the runner
        /// null-checks both — a bot has no graph overlay, and the caller passes
        /// whatever it built.
        ///
        /// <b>These assignments must land after <c>AddComponent</c> and they do
        /// — that is the whole reason <c>SimulationRunner</c> resolves its
        /// wiring in <c>Start</c> rather than <c>Awake</c></b> (see its
        /// <c>ConfigureRates</c> note: <c>Awake</c> has already run on the
        /// defaults by the time a builder gets to write anything).
        /// </summary>
        public static SimulationRunner ConfigureCarRunner(
            SimulationRunner runner, CarVehicle car, CarInput input,
            SensorRig sensors, GraphOverlay graph,
            int physicsRateHz, int controlRateHz,
            bool startInManual, bool loadControllerDll, bool logCsv)
        {
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.logCsv = logCsv;
            runner.vehicleBehaviour = car;
            runner.inputBehaviour = input;
            runner.sensorRig = sensors;
            runner.graph = graph;
            runner.startInManual = startInManual;
            runner.loadControllerDll = loadControllerDll;
            return runner;
        }
    }
}
