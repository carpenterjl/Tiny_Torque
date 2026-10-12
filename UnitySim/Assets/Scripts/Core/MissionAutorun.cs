using System;
using System.IO;
using AIHWSim.Bridge;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Unattended mission run: configures a firmware-driven session, watches it,
    /// and writes a scored result to disk.
    ///
    /// This exists because the mission's accuracy cannot be judged by eye. The
    /// dominant error is the odometer scale factor, which is a few centimetres
    /// over a 30 m run — you cannot see that, you have to measure it against
    /// ground truth, and you have to be able to re-measure it after every change
    /// to the calibration constants. Driving the car by hand for each iteration
    /// is not a workable loop.
    ///
    /// Activated only when a request file exists (written by the editor-side
    /// runner), so it is completely inert in normal play.
    /// </summary>
    public static class MissionAutorun
    {
        [Serializable]
        public class Request
        {
            public string resultPath = "";
            public float timeoutSec = 45f;
            public string vehicle = "Opus Vector";
            public string track = "Opus Proving Ground";
            /// <summary>Firmware to run instead of the one the vehicle names.
            /// Empty — the default, and what every mission run uses — leaves the
            /// design untouched. It exists so the harness can be pointed at a
            /// different DLL without inventing a second one: running some other
            /// controller on a known car and a known track, unattended, is the
            /// same job with a different subject.</summary>
            public string controllerDll = "";
            /// <summary>Camera resolution to force on every camera the design
            /// carries. 0 (both) leaves them exactly as authored, which is what
            /// the scored mission uses.</summary>
            public int camWidth, camHeight;
            /// <summary>Sensor-noise seed for this run; 0 keeps the saved
            /// setting (random when that is 0 too). Fix it to diff two runs.</summary>
            public int noiseSeed;
            /// <summary>Control rate to tick the firmware at; 0 keeps the
            /// scene's (TIM-06: the same firmware at a different loop rate).</summary>
            public int controlHz;
            /// <summary>Compute latency (µs) to give the car; -1 keeps the
            /// design's own (TIM-02: the same firmware with a later command).</summary>
            public int computeLatencyUs = -1;
            /// <summary>Also write every telemetry channel, each control tick,
            /// to &lt;result&gt;.telemetry.csv — the input to an offline replay
            /// (Tools/nav_replay).</summary>
            public bool saveTelemetry;
            /// <summary>VAL-11: faults to inject, ';'-separated FaultSpec strings
            /// (e.g. "RcKill@p4+1"). Empty = none.</summary>
            public string faults = "";
            /// <summary>Run the whole fault-injection suite (FaultSuiteWatcher)
            /// instead of one scored mission.</summary>
            public bool faultSuite;
            /// <summary>Comma-separated suite scenarios to run; empty = all.</summary>
            public string faultOnly = "";
            /// <summary>HIL-01/04: run the firmware through the lockstep bridge —
            /// "dll", "exe", "serial:COM5" (ControllerLinkOptions.Parse). Empty =
            /// in-process, the default.</summary>
            public string link = "";
            /// <summary>The bridge's per-tick wait for the firmware, ms; 0 = its default.</summary>
            public int linkTimeoutMs;
            /// <summary>Wire capture of the linked run (tools/tt_replay).</summary>
            public string linkRecord = "";
            /// <summary>§9.2 domain randomization (<see cref="Campaign"/>): this
            /// many Monte-Carlo runs; 0 = a single mission.</summary>
            public int campaignRuns;
            /// <summary>The campaign's seed: every run's draws and noise seed.</summary>
            public int campaignSeed = 1;
            /// <summary>Also run each parameter alone at both ends of its range.</summary>
            public bool campaignCorners;
            /// <summary>Comma-separated run indices to run; empty = all.</summary>
            public string campaignOnly = "";
            /// <summary>Range width: 1 = the §9.2 ranges, 0.5 = half as wide.</summary>
            public float campaignWidth = 1f;
            /// <summary>Gate overrides, "total=50,stop=10,leg=60,turn=1,complete=1,safety=0"
            /// (on a route: "xte=100,rstop=50,complete=1,safety=0").</summary>
            public string campaignGate = "";
            /// <summary>NAV-06: follow this route ("oval", or "oval@2" for a lower
            /// top speed) instead of flying the fixed manoeuvre. The firmware is
            /// told through the TT_ROUTE environment variable; the run is scored
            /// against the route (MissionScorer). Empty = the manoeuvre.</summary>
            public string route = "";
        }

        [Serializable]
        public class Result
        {
            public bool completed;
            public int phase;
            public int fault;
            public float elapsedSec;

            public float legA_target, legA_actual, legA_err_mm;
            public float turn_target_deg, turn_actual_deg, turn_err_deg;
            public float legB_target, legB_actual, legB_err_mm;
            public float brake_target, brake_actual, brake_err_mm;
            public float total_target, total_actual, total_err_mm;

            public float odo_final, truth_final, drift_mm;
            public float controllerStopErr_mm;
            public float cruiseSpeedMean, cruiseSpeedMin, turnSpeedMin;
            public int noiseSeed;
            public int controlHz;
            public int safeFault, safeState;
            public string note = "";
            /// <summary>The lockstep link's statistics; "" in-process.</summary>
            public string link = "";

            // NAV-06: a route run, scored against the route ("" otherwise).
            public string route = "";
            public int route_id;
            public float xte_rms_mm, xte_p95_mm, xte_max_mm;   // true position vs the route
            public float head_rms_deg, head_end_deg;
            public float v_err_rms, v_err_p95;                 // true speed vs the firmware's plan, following
            public float lat_acc_max;                          // m/s^2, true
            public float lap_s;                                // launch to done
            public float route_stop_mm, route_stop_lat_mm;     // where it stopped vs the route's end
        }

        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "opus_mission_request.json");

        private static Request _req;
        private static System.Collections.Generic.List<Campaign.RunSpec> _plan;

        /// <summary>True when this process was launched to run a mission.</summary>
        public static bool Active => _req != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            _req = null;
            if (!File.Exists(RequestPath)) return;

            // Consume the request before acting on it. The editor-side runner also
            // deletes it, but only when play mode exits cleanly — an aborted or
            // killed run leaves it behind, and then the next launch of anything
            // (including a normal game session sitting in the menu) arms itself and
            // quits on the timeout. One reader, one run.
            string json;
            try
            {
                json = File.ReadAllText(RequestPath);
                File.Delete(RequestPath);
            }
            catch (Exception e) { Debug.LogError($"[MissionAutorun] unreadable request: {e.Message}"); return; }

            try { _req = JsonUtility.FromJson<Request>(json); }
            catch (Exception e) { Debug.LogError($"[MissionAutorun] bad request: {e.Message}"); return; }
            if (_req == null) return;

            // Configure the session before any scene Awake runs, which is the
            // whole reason this hooks BeforeSceneLoad. A campaign starts on
            // its first run's car.
            _plan = null;
            VehicleDesign design;
            if (_req.campaignRuns > 0 || _req.campaignCorners)
            {
                _plan = Campaign.Plan(_req.campaignRuns, _req.campaignSeed, _req.campaignCorners, _req.campaignOnly,
                                      _req.noiseSeed);   // -opusSeed: the corners' noise
                if (_plan.Count == 0) { Debug.LogError("[MissionAutorun] the campaign plan is empty"); _req = null; return; }
                design = Campaign.DesignFor(_req.vehicle, _plan[0], _req.campaignWidth);
                _req.noiseSeed = _plan[0].noiseSeed;
                Debug.Log($"[MissionAutorun] campaign: {_plan.Count} runs, seed {_req.campaignSeed}, width {_req.campaignWidth}");
            }
            else
            {
                design = VehiclePresets.Resolve(_req.vehicle);
            }
            // Two track sources, the same two the menu offers: a tile-map preset
            // and a hand-authored scene. They are mutually exclusive on GameFlow
            // by construction, so this picks one and assigns only that one.
            var track = TrackPresets.Resolve(_req.track);
            string sceneTrack = track == null ? SceneTrackCatalog.Resolve(_req.track) : null;
            if (design == null || (track == null && sceneTrack == null))
            {
                Debug.LogError($"[MissionAutorun] unknown vehicle '{_req.vehicle}' or track '{_req.track}'");
                _req = null;
                return;
            }

            // The firmware's route (NAV-06): set or cleared, so an editor
            // process never carries one run's route into the next.
            Environment.SetEnvironmentVariable("TT_ROUTE", string.IsNullOrEmpty(_req.route) ? null : _req.route);
            if (!string.IsNullOrEmpty(_req.route))
            {
                if (RouteTable.Load(_req.route) == null)
                {
                    Debug.LogError($"[MissionAutorun] no route '{_req.route}' in Resources/Routes (node Tools/gen_route.js)");
                    _req = null;
                    return;
                }
                Debug.Log($"[MissionAutorun] route {_req.route}");
            }

            SimulationRunner.NoiseSeedOverride = _req.noiseSeed;
            PinFrameStep();
            SimulationRunner.ControlRateOverride = _req.controlHz;
            SimulationRunner.LinkOverride = null;
            if (!string.IsNullOrEmpty(_req.link))
            {
                try
                {
                    var lo = ControllerLinkOptions.Parse(_req.link);
                    if (_req.linkTimeoutMs > 0) lo.timeoutMs = _req.linkTimeoutMs;
                    lo.record = _req.linkRecord ?? "";
                    SimulationRunner.LinkOverride = lo;
                    Debug.Log($"[MissionAutorun] firmware through the lockstep link: {lo}");
                }
                catch (ArgumentException e)
                {
                    Debug.LogError($"[MissionAutorun] {e.Message}");
                    _req = null;
                    return;
                }
            }
            if (track != null)
            {
                GameFlow.ActiveTrack = track;
            }
            else
            {
                if (_plan != null) { Debug.LogError("[MissionAutorun] a campaign needs a tile-map track"); _req = null; return; }
                GameFlow.ActiveSceneTrack = sceneTrack;
            }
            ApplyDesign(_req, design);

            Debug.Log($"[MissionAutorun] armed: {_req.vehicle} on {_req.track}");
        }

        /// <summary>
        /// Every rendered frame advances the game clock by exactly one physics
        /// step, however long it really took. Something in the scene still
        /// reads frame-interpolated poses, so with wall-clock frames a slow
        /// frame (a GC pause, a scene load) shifted a run by a few mm — and a
        /// campaign's later runs differed from the same runs alone. Called at
        /// boot and every step (the physics rate is set after boot).
        /// </summary>
        public static void PinFrameStep()
        {
            float step = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 1f / 400f;
            if (Time.captureDeltaTime != step) Time.captureDeltaTime = step;
        }

        /// <summary>
        /// Put <paramref name="design"/> into the session as the one firmware-
        /// driven player, with the request's overrides applied. Used for the
        /// first load and, by a campaign, before each reload.
        /// </summary>
        public static void ApplyDesign(Request req, VehicleDesign design)
        {
            // Resolve hands back a freshly built design every call, so overwriting
            // these cannot leak into anything else.
            if (!string.IsNullOrEmpty(req.controllerDll))
                design.controllerDll = req.controllerDll;
            // 0 leaves the design's own camera alone, which is what every mission
            // run does. Set both to run the same car at a different resolution —
            // a perception loop is judged on the frame it is given, so the frame
            // has to be something the harness can vary. CameraSensor clamps to
            // 8..128 on both axes whatever arrives here.
            if (req.camWidth > 0 && req.camHeight > 0)
                foreach (var s in design.sensors)
                    if (s.kind == SensorType.Camera)
                    {
                        s.camWidth = req.camWidth;
                        s.camHeight = req.camHeight;
                    }

            if (req.computeLatencyUs >= 0)
            {
                design.computeLatencyUs = req.computeLatencyUs;
                Debug.Log($"[MissionAutorun] compute latency {req.computeLatencyUs} us");
            }
            GameFlow.ActiveDesign = design;
            SessionConfig.Mode = SessionMode.SinglePlayer;
            SessionConfig.TargetLaps = 0;
            SessionConfig.Players.Clear();
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = "Opus",
                design = design,
                control = DriveControl.Firmware,   // run the DLL, start in Autonomous
                isLocal = true,
                isBot = false,
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            if (_req == null) return;

            // A scene track needs TrackScene on top of it: that is where the one
            // inspector-authored TrackBootstrap lives, and without it the scene is
            // scenery with nothing to compose a session. The runner opened the
            // track scene itself, so this is LoadTrack's second half only — the
            // flag it sets matters as much as the load, or the scene would apply
            // its own DrivingSceneDescriptor rules over the session configured in
            // Boot. Also sets LaunchedFromMenu, which the tile-map mission has
            // always run without: its numbers are a baseline, so it is left alone.
            if (GameFlow.HasSceneTrack) GameFlow.AdoptOpenSceneTrack();

            var go = new GameObject("MissionWatcher");
            UnityEngine.Object.DontDestroyOnLoad(go);
            if (_plan != null) go.AddComponent<CampaignWatcher>().Configure(_req, _plan);
            else if (_req.faultSuite) go.AddComponent<FaultSuiteWatcher>().Configure(_req);
            else go.AddComponent<MissionWatcher>().Configure(_req);
        }
    }

    /// <summary>Scores one mission run against ground truth, then ends the process.</summary>
    public sealed class MissionWatcher : MonoBehaviour
    {
        private MissionAutorun.Request _req;
        private readonly MissionAutorun.Result _res = new MissionAutorun.Result();

        private SimulationRunner _runner;
        private Telemetry.CsvLogger _telemetry;
        private CarVehicle _car;
        private TelemetryHub _hub;
        private MissionScorer _score;

        private float _elapsed;
        private bool _finished;

        public void Configure(MissionAutorun.Request req) { _req = req; }

        private void FixedUpdate()
        {
            if (_finished) return;
            MissionAutorun.PinFrameStep();
            _elapsed += Time.fixedDeltaTime;

            if (_runner == null)
            {
                _runner = FindFirstObjectByType<SimulationRunner>();
                _car = FindFirstObjectByType<CarVehicle>();
                if (_runner == null || _car == null)
                {
                    if (_elapsed > 10f) Finish(false, "no runner/car in scene");
                    return;
                }
                _hub = _runner.Hub;
                _score = new MissionScorer(_car, _hub, RouteTable.Load(_req.route));
                if (!string.IsNullOrEmpty(_req.faults))
                {
                    var specs = new System.Collections.Generic.List<Sensors.FaultSpec>();
                    foreach (var f in _req.faults.Split(';'))
                        if (f.Trim().Length > 0) specs.Add(Sensors.FaultSpec.Parse(f));
                    _runner.Faults.Set(specs);
                    Debug.Log("[MissionAutorun] faults: " + string.Join("; ", specs));
                }
                if (_req.saveTelemetry)
                {
                    _telemetry = new Telemetry.CsvLogger(_hub);
                    _telemetry.Begin("mission", null, "mission_telemetry");
                }
            }

            int phase = _score.Step(_elapsed);
            if (phase == 10) { Finish(true, ""); return; }
            if (phase == -1) { Finish(false, "controller reported FAULT"); return; }
            if (_elapsed > _req.timeoutSec) { Finish(false, "timeout"); return; }
        }

        private void Finish(bool ok, string note)
        {
            _finished = true;
            _res.completed = ok;
            _res.note = note;
            _res.noiseSeed = Sensors.NoiseModel.GlobalSeed;
            _res.controlHz = _runner != null ? _runner.controlRateHz : 0;
            _res.link = _runner != null ? _runner.LinkStats : "";
            _res.elapsedSec = _elapsed;
            if (_score != null) _score.Fill(_res);
            else _res.phase = -99;

            try
            {
                string json = JsonUtility.ToJson(_res, true);
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_req.resultPath));
                    File.WriteAllText(_req.resultPath, json);
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".trace.csv"), _score != null ? _score.Trace : "");
                    if (_telemetry != null)
                    {
                        _telemetry.Flush();
                        File.Copy(_telemetry.TempPath, Path.ChangeExtension(_req.resultPath, ".telemetry.csv"), true);
                        _telemetry.End();
                        _telemetry = null;
                    }
                }
                Debug.Log("[MissionAutorun] RESULT " + json.Replace("\n", " "));
            }
            catch (Exception e) { Debug.LogError($"[MissionAutorun] write failed: {e.Message}"); }

#if UNITY_EDITOR
            // Exit the process outright rather than easing out of play mode: in
            // batchmode the play-mode teardown does not reliably hand control
            // back, and the run's only product (the result file) is already on
            // disk. Interactive runs stop play mode normally instead.
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(ok ? 0 : 2);
            else UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(ok ? 0 : 2);
#endif
        }
    }
}
