using System;
using System.Collections.Generic;
using System.IO;
using AIHWSim.Bridge;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Orchestrates the fixed-rate control loop.
    ///
    /// Physics runs at <see cref="physicsRateHz"/> (Unity FixedUpdate). The
    /// controller runs at <see cref="controlRateHz"/>, an integer division of
    /// the physics rate; between control ticks the actuator commands are held
    /// (zero-order hold, like a real DAC/PWM latch). The native controller call
    /// and telemetry commit happen on the control tick, so logged timestamps
    /// are uniform at the control rate.
    ///
    /// Timing (Phase 3): a sensor with its own rate, latency, phase or jitter
    /// is sampled on the PHYSICS step its clock lands on (TIM-04), and its
    /// latency is resolved to the physics step (TIM-02). A command reaches the
    /// actuators <c>computeLatencyUs</c> after its control tick, also resolved
    /// to the physics step (TIM-02), and the firmware's own time stamp can be
    /// jittered (TIM-03). All default to 0 = the legacy timing.
    /// </summary>
    public sealed class SimulationRunner : MonoBehaviour
    {
        [Header("Rates")]
        public int physicsRateHz = 500;
        public int controlRateHz = 100;

        [Header("Controller DLL")]
        [Tooltip("Path relative to Assets/ of the native controller plugin.")]
        public string dllRelativePath = "Plugins/x86_64/controller.dll";
        public bool autoReloadOnChange = false;

        [Header("Logging")]
        public bool logCsv = true;
        [Tooltip("Suffix for temp/saved CSV names; keeps multi-runner sessions from colliding.")]
        public string logLabel = "";
        [Tooltip("May this runner ever log? False for bots/split-screen so a mid-session toggle can't start their CSV.")]
        public bool loggable = true;

        [Header("Mode")]
        [Tooltip("Start in Manual (human drives directly); toggle with M at runtime.")]
        public bool startInManual = true;

        [Header("Realism")]
        [Tooltip("Delay controller→actuator by this many control ticks (models ESC/link latency). 0 = same-tick (legacy).")]
        public int actuationDelayTicks = 0;

        [Header("Multiplayer flags (defaults preserve single-player behavior)")]
        [Tooltip("Allow the M key to switch Manual/Autonomous (off in split-screen).")]
        public bool allowModeToggle = true;
        [Tooltip("Draw the top-center mode box (off in split-screen).")]
        public bool showModeBox = true;
        [Tooltip("Load the native controller DLL (off in split-screen — humans only).")]
        public bool loadControllerDll = true;

        public enum GraphProfile { DiffDrive, Car, Mission }
        public GraphProfile graphProfile = GraphProfile.DiffDrive;

        // Wired by the scene builder.
        public MonoBehaviour vehicleBehaviour;   // must implement IControlledVehicle
        public MonoBehaviour inputBehaviour;     // may implement IManualDriver and/or ISetpointSource
        public GraphOverlay graph;
        public SensorRig sensorRig;              // optional configurable-sensor loadout

        public enum DriveMode { Manual, Autonomous }
        public DriveMode Mode { get; private set; }

        public TelemetryHub Hub { get; private set; }
        public bool ControllerReady => _loader != null && _loader.IsLoaded;

        /// <summary>VAL-11 fault injection for this run (inert while empty).</summary>
        public Sensors.FaultInjector Faults { get; } = new Sensors.FaultInjector();

        private IControlledVehicle _vehicle;
        private IManualDriver _manualDriver;
        private ISetpointSource _setpointSource;
        private NativeControllerLoader _loader;
        private CsvLogger _csv;

        private const int CarSteerSlot = 6;   // CTRL_STEER_ACTUATOR

        private static readonly string[] InChannels =
        {
            "in/gyro_x", "in/gyro_y", "in/gyro_z",
            "in/accel_x", "in/accel_y", "in/accel_z",
            "in/wheel_vel_0", "in/wheel_vel_1", "in/wheel_vel_2", "in/wheel_vel_3",
            "in/setpoint_0", "in/setpoint_1", "in/setpoint_2", "in/setpoint_3",
            "out/act_0", "out/act_1", "out/act_2", "out/act_3",
            "out/act_4", "out/act_5", "out/act_6", "out/act_7",
        };
        private int _decimation = 1;
        private int _physCounter;

        // Sim clock (TIM-05). Integer: physics ticks consumed by control steps
        // since the last rebase, at the rate in force since then. time_us is
        // derived from it, so it never accumulates rounding — the old
        // `_simTime += 0.01f` drifted measurably within minutes. _simTime is
        // the float view of it, kept for every existing reader.
        private long _timeBaseUs;
        private long _ticksSinceBase;
        private int _baseRateHz;
        private uint _controlTick;
        private float _simTime;

        // Reusable managed buffers (avoid per-tick GC).
        private readonly float[] _wheelVel = new float[4];
        private readonly float[] _gyro = new float[3];
        private readonly float[] _accel = new float[3];
        private readonly float[] _actuators = new float[8];

        // VAL-04: the frame the controller actually received this tick (FLU for
        // a v7 controller, native otherwise) and the actuator vector it
        // returned, before any host-side conversion. Logged as in/* and out/*,
        // so a run can be replayed against the firmware offline.
        private readonly float[] _inGyro = new float[3];
        private readonly float[] _inAccel = new float[3];
        private readonly float[] _rawOut = new float[8];

        private string[] _debugNames = Array.Empty<string>();
        // Debug channels past the 16 of CtrlOutputs (ctrl_get_debug_ext).
        private readonly float[] _debugExt = new float[32];
        private int _debugExtN;
        // What the firmware reported last, held while it does not run (a
        // core stall or an MCU reset) — the drivers hold its last frame too.
        private CtrlOutputs _lastOutputs;
        private int _lastPhase = -99;

        // Actuation transport delay ring (actuationDelayTicks control ticks).
        private float[][] _cmdRing;
        private int _cmdRingHead;

        // TIM-02 compute latency: commands waiting for their physics step.
        private readonly CommandLatch _latch = new CommandLatch();
        private long _physTick;

        // TIM-03 control-tick jitter: the time stamp handed to the firmware.
        private System.Random _jitterRng;
        private long _lastReportedUs = long.MinValue;

        // Session noise seed: set once per process launch (before any sensor
        // samples) from GameSettings.noiseSeed, or drawn randomly when 0. Either
        // way the effective value is stamped into the CSV metadata so any run
        // can be reproduced exactly afterwards.
        //
        // The per-instance ordinals restart with every session (every frame in
        // which a runner wakes; split-screen runners wake in the same frame and
        // share one restart). Without that, instance seeds kept counting up from
        // the previous session and a stamped seed only reproduced the first run.
        private static bool _seedApplied;
        private static int _ordinalsResetFrame = -1;

        /// <summary>Non-zero overrides <c>GameSettings.noiseSeed</c> for this
        /// process without touching the saved settings. Set by the headless
        /// mission harness (<c>-opusSeed</c>) so two runs can be diffed.</summary>
        public static int NoiseSeedOverride;

        /// <summary>Non-zero makes the next controller load tick at this rate
        /// (nearest divisor of the physics rate), over the controller's own
        /// request. Set by the headless harness (<c>-opusControlHz</c>) to show a
        /// firmware is rate-independent (TIM-06).</summary>
        public static int ControlRateOverride;
        private static void ApplyNoiseSeed()
        {
            if (_ordinalsResetFrame != Time.frameCount)
            {
                _ordinalsResetFrame = Time.frameCount;
                NoiseModel.ResetOrdinals();
            }
            if (_seedApplied) return;
            _seedApplied = true;
            int configured = NoiseSeedOverride != 0
                ? NoiseSeedOverride : Persistence.SettingsStore.Current.noiseSeed;
            NoiseModel.GlobalSeed = configured != 0 ? configured : Environment.TickCount;
        }

        /// <summary>
        /// Derive the decimation and the physics step from the configured rates.
        ///
        /// This MUST run again in Start(), not only in Awake(): scene builders
        /// assign physicsRateHz/controlRateHz immediately after AddComponent,
        /// by which time Awake has already run on the default values. Computing
        /// it only once in Awake left the track scene stepping physics at the
        /// default 500 Hz instead of its configured 400, and — worse — handing
        /// controllers a control period of _decimation/physicsRateHz = 5/400 =
        /// 12.5 ms when the true period was 5 x 2 ms = 10 ms. Every dt-dependent
        /// term in a controller (integrators, derivatives, odometry) was 25 % off.
        ///
        /// <paramref name="provisional"/> says which of those two passes this is.
        /// The Awake one is reporting the component's default, which on a builder-
        /// assigned rig is a number nobody asked for — see
        /// <see cref="Boot.PhysicsRateAuthority"/> for why telling it apart is what
        /// keeps the conflict warning honest in a scene with several runners.
        /// </summary>
        private void ConfigureRates(bool provisional)
        {
            physicsRateHz = Mathf.Max(1, physicsRateHz);
            controlRateHz = Mathf.Clamp(controlRateHz, 1, physicsRateHz);
            _decimation = Mathf.Max(1, Mathf.RoundToInt((float)physicsRateHz / controlRateHz));
            // Snap control rate to the exact achievable value after decimation.
            controlRateHz = Mathf.RoundToInt((float)physicsRateHz / _decimation);
            if (physicsRateHz != _baseRateHz) RebaseClock(TimeUs);

            // Reports through PhysicsRateAuthority, which applies exactly this
            // write and additionally notices when a second runner in the same
            // session asks for a different rate — the global-fixedDeltaTime
            // hazard DebugVehicleSpawner documents and hand-works-around.
            Boot.PhysicsRateAuthority.Apply(physicsRateHz, this, provisional);
        }

        /// <summary>
        /// Change the rates on a runner that is already running.
        ///
        /// Exists for the external control bridge, which lets an application
        /// sweep the timestep the way the P9/A7 probes do from script. Passing 0
        /// for either leaves that one alone, so a client can raise the physics
        /// rate without also restating the control rate.
        ///
        /// This is a real re-derivation, not a field write: the decimation and
        /// the control period come out of the two rates together, and setting
        /// <c>physicsRateHz</c> by itself would leave a controller being stepped
        /// on a period nothing recomputed. It reports NON-provisionally, so a
        /// second rig disagreeing still raises [RATE] — a caller changing the
        /// rate in a multi-rig session is exactly who that warning is for, and
        /// the bridge applies the change to every runner for that reason.
        /// </summary>
        public void ReconfigureRates(int newPhysicsHz, int newControlHz)
        {
            if (newPhysicsHz > 0) physicsRateHz = newPhysicsHz;
            if (newControlHz > 0) controlRateHz = newControlHz;
            ConfigureRates(provisional: false);
        }

        /// <summary>
        /// Swap the behaviour that supplies manual commands and setpoints.
        ///
        /// Deliberately NOT <see cref="Rebind"/>, which is the heavier operation
        /// for a swapped CAR: Rebind also re-runs the sensor manifest and
        /// <c>RegisterChannels</c>, and re-registering channels mid-session
        /// widens CSV rows past the header <c>CsvLogger.Begin</c> already
        /// snapshotted. Only the two input casts change here, so only they are
        /// re-resolved.
        ///
        /// Used by the external control bridge to install a raw actuator driver
        /// in place of <c>CarInput</c> and to put CarInput back on release.
        /// </summary>
        public void SetInputBehaviour(MonoBehaviour behaviour)
        {
            inputBehaviour = behaviour;
            _manualDriver = behaviour as IManualDriver;
            _setpointSource = behaviour as ISetpointSource;
            SyncAssistGate();   // a raw driver turns assists off; putting CarInput back restores them
        }

        /// <summary>
        /// Force Manual or Autonomous from outside the mode-toggle key. Same
        /// assist gate the toggle applies — Autonomous means C firmware is
        /// driving, and firmware always faces the raw physics.
        /// </summary>
        public void SetMode(DriveMode mode)
        {
            if (Mode == mode) return;
            Mode = mode;
            SyncAssistGate();
        }

        private void Awake()
        {
            ConfigureRates(provisional: true);
            ApplyNoiseSeed();
            Hub = new TelemetryHub();
        }

        private void Start()
        {
            // Resolve wiring here rather than in Awake: scene builders assign
            // these fields immediately AFTER AddComponent, but AddComponent already
            // ran Awake — so reading them in Awake yields nulls/defaults.
            ConfigureRates(provisional: false);   // the builder's rates are only in place now

            _vehicle = vehicleBehaviour as IControlledVehicle;
            if (_vehicle == null)
                Debug.LogError("[SimRunner] vehicleBehaviour does not implement IControlledVehicle.");
            _manualDriver = inputBehaviour as IManualDriver;
            _setpointSource = inputBehaviour as ISetpointSource;
            Mode = startInManual ? DriveMode.Manual : DriveMode.Autonomous;
            SyncAssistGate();

            // Build the sensor manifest before loading the controller so its
            // ctrl_configure() can receive the loadout.
            if (sensorRig != null)
                sensorRig.Initialize(vehicleBehaviour as CarVehicle, vehicleBehaviour.transform);

            // A respawn teleports the body but leaves wheel spin and the encoder
            // accumulators running, so any controller integrating distance or
            // heading must be told to start over.
            if (vehicleBehaviour is CarVehicle carForReset)
                carForReset.VehicleReset += OnVehicleReset;

            if (loadControllerDll) LoadController();
            RegisterChannels();
            ConfigureGraph();

            if (logCsv) EnableLogging();

            _vehicle?.ResetVehicle();
        }

        /// <summary>
        /// Re-resolve everything <see cref="Start"/> cached, after the car this
        /// runner drives has been replaced underneath it.
        ///
        /// <b>Assigning the fields is not enough, and that is the whole reason
        /// this exists.</b> Start caches four things that outlive a field write:
        /// the three interface casts (a runner holding a cast to a destroyed
        /// component steps a corpse), the <c>VehicleReset</c> subscription (the
        /// old car's event, which will never fire again), the sensor rig's
        /// manifest, and the telemetry channels, which close over the OLD
        /// vehicle and would keep logging its last values forever.
        ///
        /// Not called by anything on the normal path — a car is built once and
        /// lives for the session. <c>CarRebuilder</c> calls it, and only in the
        /// editor-tuning case where a build-time value changed.
        ///
        /// Deliberately does NOT re-run <c>ConfigureRates</c> (the rate did not
        /// change and re-writing the global step mid-session is exactly what
        /// PhysicsRateAuthority exists to notice), <c>LoadController</c> (a
        /// native DLL holds its own state and reloading it under a running
        /// mission is a different operation with its own button), or
        /// <c>EnableLogging</c> (a CSV already open should keep its column set;
        /// CsvLogger.Begin snapshots channels once and a second Begin would
        /// start a second file mid-run).
        /// </summary>
        /// <param name="previous">The vehicle this runner was driving before the
        /// caller reassigned <see cref="vehicleBehaviour"/>, so its
        /// <c>VehicleReset</c> subscription can be dropped. Taken as an argument
        /// rather than read off the field because by the time Rebind is called
        /// the field already holds the NEW car — unsubscribing from that would
        /// be a no-op that looks like cleanup.</param>
        public void Rebind(CarVehicle previous = null)
        {
            if (previous != null) previous.VehicleReset -= OnVehicleReset;

            _vehicle = vehicleBehaviour as IControlledVehicle;
            if (_vehicle == null)
                Debug.LogError("[SimRunner] Rebind: vehicleBehaviour does not implement "
                               + "IControlledVehicle.");
            _manualDriver = inputBehaviour as IManualDriver;
            _setpointSource = inputBehaviour as ISetpointSource;
            SyncAssistGate();

            if (sensorRig != null)
                sensorRig.Initialize(vehicleBehaviour as CarVehicle, vehicleBehaviour.transform);
            if (vehicleBehaviour is CarVehicle carForReset)
                carForReset.VehicleReset += OnVehicleReset;

            RegisterChannels();
            ConfigureGraph();
        }

        /// <summary>
        /// Begin CSV logging now if it isn't already running. Safe to call after
        /// Start() — the Hub and channels already exist. The pause-menu Settings
        /// toggle calls this so logging can start after the menu closes.
        /// </summary>
        public void EnableLogging()
        {
            if (!loggable || _csv != null) return;
            _csv = new CsvLogger(Hub);
            _csv.Begin(ControllerName(), BuildMetadata(), logLabel);
        }

        public string ControllerName()
        {
            return _loader != null && !string.IsNullOrEmpty(_loader.SourcePath)
                ? Path.GetFileNameWithoutExtension(_loader.SourcePath)
                : "no_controller";
        }

        private Dictionary<string, string> BuildMetadata()
        {
            var md = new Dictionary<string, string>
            {
                { "physics_hz", physicsRateHz.ToString() },
                { "control_hz", controlRateHz.ToString() },
                { "controller_loaded", ControllerReady.ToString() },
                { "noise_seed", NoiseModel.GlobalSeed.ToString() },
                { "actuation_delay_ticks", actuationDelayTicks.ToString() },
            };
            if (Car != null)
            {
                md["compute_latency_us"] = Car.computeLatencyUs.ToString();
                md["control_jitter_us"] = Car.controlJitterUs.ToString("R");
            }
            if (_loader != null && !string.IsNullOrEmpty(_loader.SourcePath) && File.Exists(_loader.SourcePath))
                md["dll_write_utc"] = File.GetLastWriteTimeUtc(_loader.SourcePath).ToString("o");
            AddIdentity(md);
            return md;
        }

        /// <summary>
        /// VAL-04: everything needed to say which firmware, ABI, car, sensors
        /// and simulator produced a log, so two sidecars can be compared and a
        /// run reproduced. Hashes rather than timestamps: a rebuild that changed
        /// nothing keeps its hash.
        /// </summary>
        private void AddIdentity(Dictionary<string, string> md)
        {
            md["unity_version"] = Application.unityVersion;
            md["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            md["time_base"] = "integer_us";
            if (_loader != null && _loader.IsLoaded)
            {
                md["abi_version"] = _loader.AbiVersion.ToString();
                md["frame"] = _loader.IsV7 ? "FLU" : "unity";
                try { md["dll_sha256"] = Sha256(File.ReadAllBytes(_loader.SourcePath)); }
                catch (Exception) { /* the file may be mid-rebuild; leave it out */ }
            }
            var design = GameFlow.ActiveDesign;
            if (design != null)
            {
                md["design_name"] = design.name;
                md["design_sha256"] = Sha256(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(design)));
            }
            if (sensorRig != null)
            {
                var sb = new System.Text.StringBuilder();
                var sensors = sensorRig.Sensors;
                for (int i = 0; i < sensors.Count; i++)
                {
                    var sc = sensors[i];
                    if (i > 0) sb.Append("; ");
                    sb.Append(sc.sensorName).Append(':').Append(sc.Type)
                      .Append(" rate=").Append(sc.updateRateHz.ToString("0.###"))
                      .Append("Hz lat=").Append(sc.latencyMs.ToString("0.###")).Append("ms");
                    if (sc.phaseOffsetMs > 0f) sb.Append(" phase=").Append(sc.phaseOffsetMs.ToString("0.###")).Append("ms");
                    if (sc.jitterUs > 0f) sb.Append(" jitter=").Append(sc.jitterUs.ToString("0.#")).Append("us");
                }
                md["sensors"] = sb.ToString();
            }
        }

        private static string Sha256(byte[] data)
        {
            using (var h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        private string AbsoluteDllPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, dllRelativePath));

        public void LoadController()
        {
            _loader ??= new NativeControllerLoader();
            string path = AbsoluteDllPath();
            if (_loader.Load(path))
            {
                ApplyRequestedControlRate();
                int rc = _loader.Init(controlRateHz);
                if (rc != 0)
                    Debug.LogWarning($"[SimRunner] ctrl_init returned {rc}");
                _debugNames = _loader.ReadDebugNames();
                ConfigureControllerSensors();
                if (_csv != null)
                {
                    // A reload mid-session: the sidecar names what is running now.
                    var md = new Dictionary<string, string>();
                    AddIdentity(md);
                    foreach (var kv in md) _csv.SetMetadata(kv.Key, kv.Value);
                    _csv.SetMetadata("control_hz", controlRateHz.ToString());
                }
            }
            else
            {
                Debug.LogWarning("[SimRunner] Running open-loop (no controller). " +
                                 "Build controller.dll with Controllers/build.ps1.");
                _debugNames = Array.Empty<string>();
            }
        }

        /// <summary>
        /// TIM-06: a v7 controller may name the rate it wants to be ticked at
        /// (its MCU base tick). The runner takes the nearest whole divisor of
        /// the physics rate, which can be the physics rate itself.
        /// </summary>
        private void ApplyRequestedControlRate()
        {
            float want = 0f;
            if (_loader?.GetControlRate != null)
            {
                try { want = _loader.GetControlRate(); }
                catch (Exception e) { Debug.LogWarning($"[SimRunner] ctrl_get_control_rate threw: {e.Message}"); }
            }
            if (ControlRateOverride > 0) want = ControlRateOverride;
            if (!(want > 0f)) return;
            int hz = Mathf.RoundToInt(want);
            if (hz == controlRateHz) return;
            ReconfigureRates(0, hz);
            if (controlRateHz != hz)
                Debug.LogWarning($"[SimRunner] Controller asked for {want:0.#} Hz; running " +
                                 $"{controlRateHz} Hz (physics {physicsRateHz} Hz / {_decimation}).");
            else
                Debug.Log($"[SimRunner] Control rate {controlRateHz} Hz, as the controller asked.");
        }

        /// <summary>
        /// Hand the vehicle's sensor manifest to the controller: the extended
        /// one through ctrl_configure2() for a v7 controller that exports it,
        /// else the v2 manifest through ctrl_configure(). No-op for pre-v2
        /// controllers or when the vehicle has no configurable sensors.
        /// </summary>
        private unsafe void ConfigureControllerSensors()
        {
            if (_loader != null && _loader.IsV7 && _loader.Configure2 != null && sensorRig != null)
            {
                SensorInfo2[] m2 = sensorRig.BuildManifest2();
                fixed (SensorInfo2* p2 = m2)
                {
                    _loader.Configure2(p2, m2.Length);
                }
                return;
            }
            if (_loader?.Configure == null || sensorRig == null || sensorRig.SensorCount == 0)
                return;
            SensorInfo[] manifest = sensorRig.Manifest;
            fixed (SensorInfo* mp = manifest)
            {
                _loader.Configure(mp, manifest.Length);
            }
        }

        /// <summary>
        /// Re-arm the controller after the vehicle was teleported home (respawn,
        /// or a run restart). Runs ctrl_shutdown, ctrl_init + ctrl_configure again so a stateful
        /// controller drops the odometry, heading and phase it accumulated before
        /// the jump, and flushes the actuation-delay pipe so no pre-teleport
        /// command survives it. Cheaper and less disruptive than a full DLL
        /// reload: the library stays mapped and debug names are unchanged.
        /// </summary>
        private void OnVehicleReset()
        {
            _cmdRing = null;
            _cmdRingHead = 0;
            _latch.Clear();
            if (!ControllerReady) return;
            try
            {
                // ABI-05: a v7 controller that exports ctrl_reset drops its
                // estimator and mission state there and keeps its one-time
                // init — the way MCU firmware never re-inits its peripherals.
                if (_loader.Reset != null)
                {
                    _loader.Reset();
                    return;
                }
                // Close the old session first: the ABI promises init is always
                // preceded by shutdown, and a controller that allocates in init
                // would otherwise leak once per respawn.
                _loader.Shutdown?.Invoke();
                int rc = _loader.Init(controlRateHz);
                if (rc != 0) Debug.LogWarning($"[SimRunner] ctrl_init returned {rc} on re-arm");
                ConfigureControllerSensors();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SimRunner] Controller re-arm faulted: {e.Message}");
                SafeShutdown();
            }
        }

        /// <summary>File name of the DLL currently mapped, or "" if none.</summary>
        public string LoadedControllerName =>
            _loader != null && !string.IsNullOrEmpty(_loader.SourcePath)
                ? Path.GetFileName(_loader.SourcePath) : "";

        /// <summary>When the mapped DLL was last written. A reload that leaves this
        /// unchanged means CMake decided the binary was identical and skipped the
        /// copy — worth showing, because otherwise "build ok, nothing happened" is
        /// indistinguishable from a broken reload.</summary>
        public System.DateTime LoadedControllerStamp =>
            _loader != null ? _loader.LoadedStamp : System.DateTime.MinValue;

        /// <summary>
        /// Reload the controller DLL in place (hot reload). Safe during play.
        ///
        /// Callers drive this from Update, i.e. between FixedUpdates — which is the
        /// whole safety argument: it can never run re-entrantly with the
        /// <c>_loader.Step</c> call inside ControlStep. The library is unmapped and
        /// re-mapped, so a stateful controller drops whatever odometry and phase it
        /// had accumulated, exactly as <see cref="OnVehicleReset"/> already does.
        /// </summary>
        /// <returns>True if a controller is loaded and armed afterwards. False
        /// means the session is now open-loop — the car coasts rather than
        /// stopping, and the reason is in the log.</returns>
        public bool ReloadController()
        {
            if (!loadControllerDll) return false; // humans-only session (split-screen)
            if (_loader != null && _loader.IsLoaded)
                SafeShutdown();
            LoadController();
            RegisterChannels(); // debug channel set may have changed
            ConfigureGraph();
            return ControllerReady;
        }

        private void RegisterChannels()
        {
            Hub.RegisterChannel("sp/linear");
            Hub.RegisterChannel("sp/yaw");
            Hub.RegisterChannel("wheel/left_vel");
            Hub.RegisterChannel("wheel/right_vel");
            Hub.RegisterChannel("cmd/left");
            Hub.RegisterChannel("cmd/right");
            Hub.RegisterChannel("imu/gyro_z");
            foreach (string n in InChannels) Hub.RegisterChannel(n);
            Hub.RegisterChannel("cmd/steer_deg");
            Hub.RegisterChannel("cmd/brake");
            Hub.RegisterChannel("veh/speed");
            Hub.RegisterChannel("veh/speed_kmh");
            Hub.RegisterChannel("veh/steer_deg");
            Hub.RegisterChannel("veh/yaw_rate");
            Hub.RegisterChannel("veh/pos_x");
            Hub.RegisterChannel("veh/pos_z");
            // Registered explicitly rather than left to the first SetValue:
            // CsvLogger.Begin snapshots the column list once, but Commit walks the
            // live channel set, so a channel that appears later writes rows wider
            // than the header.
            Hub.RegisterChannel("veh/yaw_deg");
            Hub.RegisterChannel("veh/soc");
            Hub.RegisterChannel("veh/batt_v");
            Hub.RegisterChannel("veh/batt_a");
            Hub.RegisterChannel("mode");
            sensorRig?.RegisterChannels(Hub);
            // Per-motor commanded-voltage channels (published by CarVehicle).
            if (sensorRig != null)
                foreach (var m in sensorRig.Motors)
                    Hub.RegisterChannel($"cmd/{m.sensorName}/volt");
            foreach (var name in _debugNames)
                Hub.RegisterChannel("dbg/" + name.Trim());
        }

        /// <summary>Channel names for up to 4 motors, e.g. "cmd/&lt;name&gt;/volt".</summary>
        private string[] MotorChannels(string prefix, string suffix)
        {
            if (sensorRig == null) return Array.Empty<string>();
            var motors = sensorRig.Motors;
            int n = Mathf.Min(motors.Count, 4);
            var arr = new string[n];
            for (int i = 0; i < n; i++) arr[i] = prefix + motors[i].sensorName + suffix;
            return arr;
        }

        private void ConfigureGraph()
        {
            if (graph == null) return;
            graph.Hub = Hub;
            graph.ClearPanes();
            if (graphProfile == GraphProfile.Mission)
            {
                // A dead-reckoning mission run: what matters is distance and
                // heading against ground truth, not the ToF/camera panes the Car
                // profile draws. dbg/* come from the mission firmware.
                graph.AddPane("Speed (m/s)", "dbg/target_speed", "veh/speed", "dbg/v_meas");
                graph.AddPane("Distance (m)", "dbg/odo_m", "dbg/leg_rem_m");
                graph.AddPane("Heading (deg) & steer", "dbg/yaw_deg", "veh/yaw_deg", "dbg/steer_cmd");
                graph.AddPane("Electrical (V, A)", "dbg/motor_v", "dbg/i_cmd", "dbg/batt_v");
                graph.AddPane("Slip (%) & stop error (mm)", "dbg/slip_pct", "dbg/stop_err_mm");
            }
            else if (graphProfile == GraphProfile.Car)
            {
                graph.AddPane("Speed: target vs measured (m/s)",
                    "sp/linear", "veh/speed", "dbg/target_speed");
                // Motor voltage (commanded) and current (measured, load-dependent).
                graph.AddPane("Motor voltage (V)", MotorChannels("cmd/", "/volt"));
                graph.AddPane("Motor current (A)", MotorChannels("sens/", "/current"));
                if (sensorRig != null && sensorRig.SensorCount > 0)
                    graph.AddPane("ToF distance (m)",
                        "sens/tof_front/dist", "sens/tof_left/dist", "sens/tof_right/dist");
            }
            else
            {
                graph.AddPane("Left wheel: target vs measured (rad/s)",
                    "dbg/target_wl", "wheel/left_vel");
                graph.AddPane("Motor commands [-1,1]",
                    "cmd/left", "cmd/right");
                graph.AddPane("Body: speed (m/s) & yaw rate (rad/s)",
                    "veh/speed", "veh/yaw_rate");
            }
        }

        private void Update()
        {
            if (allowModeToggle && InputReader.ModeTogglePressed())
            {
                Mode = Mode == DriveMode.Manual ? DriveMode.Autonomous : DriveMode.Manual;
                SyncAssistGate();
                Debug.Log($"[SimRunner] Drive mode: {Mode}");
            }
        }

        /// <summary>Arcade assists help humans only — C firmware, and a program
        /// driving raw actuators over IPC, face the raw physics.</summary>
        private void SyncAssistGate()
        {
            if (vehicleBehaviour is Vehicles.CarVehicle car)
                car.assistsActive = Mode == DriveMode.Manual
                                    && !(_manualDriver is IRawActuatorDriver);
        }

        private void FixedUpdate()
        {
            if (_vehicle == null) return;

            _physCounter++;
            bool control = _physCounter >= _decimation;

            // TIM-04: sensors with a clock of their own sample on the physics
            // step their clock lands on. TimeUs already points at the NEXT
            // control tick between control steps, so this step is
            // (decimation − counter) physics ticks before it. Steps before the
            // first control tick (negative time) are skipped.
            if (sensorRig != null)
            {
                long back = control ? 0 : _decimation - _physCounter;
                long tick = _ticksSinceBase - back;
                if (tick >= 0)
                {
                    long anchor = control ? _ticksSinceBase : _ticksSinceBase - _decimation;
                    sensorRig.PhysicsTick(TicksToUs(tick), TicksToUs(System.Math.Max(0L, anchor)),
                        ControlPeriodUs, PhysPeriodUs);
                }
            }

            if (control)
            {
                _physCounter = 0;
                ControlStep();
            }

            ApplyDueCommands();
            _vehicle.StepPhysics(Time.fixedDeltaTime);
            _physTick++;
        }

        private long TicksToUs(long ticks) => _baseRateHz > 0
            ? _timeBaseUs + ticks * 1_000_000L / _baseRateHz
            : _timeBaseUs;

        private long PhysPeriodUs => 1_000_000L / Math.Max(1, physicsRateHz);
        private long ControlPeriodUs => _decimation * 1_000_000L / Math.Max(1, physicsRateHz);

        private CarVehicle Car => vehicleBehaviour as CarVehicle;

        /// <summary>
        /// Hand a command vector to the actuators: now (compute latency 0, the
        /// legacy behaviour) or queued for the physics step that lies
        /// computeLatencyUs after this control tick (TIM-02). Motors and LEDs
        /// get the same array at the same step.
        /// </summary>
        private void LatchCommands(float[] cmd)
        {
            var car = Car;
            int latUs = car != null ? car.computeLatencyUs : 0;
            long steps = latUs > 0
                ? (long)Math.Round(latUs * (double)physicsRateHz / 1e6) : 0;
            if (steps <= 0)
            {
                _vehicle.SetCommands(cmd);
                sensorRig?.ApplyActuators(cmd, _simTime);
                return;
            }
            _latch.Push(cmd, _physTick + steps);
        }

        /// <summary>Apply every queued command whose physics step has come;
        /// the newest of them stays latched (zero-order hold).</summary>
        private void ApplyDueCommands()
        {
            float[] cmd;
            while ((cmd = _latch.PopDue(_physTick)) != null)
            {
                _vehicle.SetCommands(cmd);
                sensorRig?.ApplyActuators(cmd, _simTime);
            }
        }

        /// <summary>Firmware time stamp for this control tick: the true tick
        /// time, plus Gaussian jitter when the car sets controlJitterUs
        /// (clipped to ±¼ period so ticks never reorder).</summary>
        private long ReportedTimeUs()
        {
            var car = Car;
            float sigma = car != null ? car.controlJitterUs : 0f;
            if (sigma <= 0f) return TimeUs;
            if (_jitterRng == null)
                _jitterRng = new System.Random(unchecked(NoiseModel.GlobalSeed * 486187739 ^ 0x71C4));
            double u1 = 1.0 - _jitterRng.NextDouble(), u2 = _jitterRng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double lim = 0.25 * ControlPeriodUs;
            return TimeUs + (long)Math.Round(Math.Max(-lim, Math.Min(lim, z * sigma)));
        }

        private unsafe void ControlStep()
        {
            float controlDt = _decimation / (float)physicsRateHz;

            // 0. Faults due this tick (VAL-11): parts, pack and scheduler.
            bool stalled = false;
            if (Faults.Any)
            {
                Faults.Bind(sensorRig, Car);
                Faults.Tick(TimeUs * 1e-6, _lastPhase);
                stalled = Faults.CoreStalled;
                if (sensorRig != null) sensorRig.Faults = Faults;
                if (Faults.TakeMcuReset()) PowerCycleController();
            }

            // 1. Sensors — built-in (wheel vel + IMU) and the configurable rig.
            _vehicle.SampleSensors(controlDt, _wheelVel, _gyro, _accel);
            if (Faults.ImuNan)
                for (int i = 0; i < 3; i++) { _gyro[i] = float.NaN; _accel[i] = float.NaN; }
            sensorRig?.Sample(controlDt, TimeUs);

            // The clock as the firmware reads it (TIM-03 jitter), and the dt
            // that follows from it.
            long reportedUs = ReportedTimeUs();
            float firmwareDt = _lastReportedUs == long.MinValue || reportedUs <= _lastReportedUs
                ? controlDt : (reportedUs - _lastReportedUs) * 1e-6f;
            _lastReportedUs = reportedUs;

            // 2. Operator setpoints.
            float[] sp = _setpointSource != null ? _setpointSource.Setpoints : null;

            // The IMU in the frame this controller is owed (ABI-04: FLU for v7).
            if (_loader != null && _loader.IsV7)
            {
                Vector3 g = FluFrame.Rate(new Vector3(_gyro[0], _gyro[1], _gyro[2]));
                Vector3 a = FluFrame.Vector(new Vector3(_accel[0], _accel[1], _accel[2]));
                _inGyro[0] = g.x; _inGyro[1] = g.y; _inGyro[2] = g.z;
                _inAccel[0] = a.x; _inAccel[1] = a.y; _inAccel[2] = a.z;
            }
            else
            {
                Array.Copy(_gyro, _inGyro, 3);
                Array.Copy(_accel, _inAccel, 3);
            }
            Array.Clear(_rawOut, 0, _rawOut.Length);

            // 3. Commands: Manual reads human input directly; Autonomous calls the DLL.
            Array.Clear(_actuators, 0, _actuators.Length);
            CtrlOutputs outputs = default;

            if (Mode == DriveMode.Manual)
            {
                _manualDriver?.ReadManualCommands(_actuators);
            }
            else if (ControllerReady && stalled)
            {
                // The firmware is not running: no step and no command frame.
                // The drivers hold the last one until their watchdog acts.
                outputs = _lastOutputs;
                _debugExtN = 0;
            }
            else if (ControllerReady)
            {
                bool v7 = _loader.IsV7;
                CtrlInputs inputs = default;
                inputs.time_s = (float)(reportedUs * 1e-6);
                inputs.dt_s = firmwareDt;
                for (int i = 0; i < 3; i++) { inputs.gyro[i] = _inGyro[i]; inputs.accel[i] = _inAccel[i]; }
                for (int i = 0; i < 4; i++) inputs.wheel_vel[i] = _wheelVel[i];
                if (sp != null) for (int i = 0; i < 4; i++) inputs.setpoint[i] = sp[i];

                // Pin the rig's flat data + camera frame for the duration of the
                // native call so no GC move invalidates the pointers.
                float[] flat = sensorRig == null ? null : v7 ? sensorRig.FlatDataFlu() : sensorRig.FlatData;
                byte[] cam = sensorRig != null ? sensorRig.CamPixels : null;
                SensorStamp[] stamps = v7 && sensorRig != null ? sensorRig.Stamps : null;
                fixed (float* flatPtr = flat)
                fixed (byte* camPtr = cam)
                fixed (SensorStamp* stampPtr = stamps)
                {
                    inputs.sensor_data = flatPtr;
                    inputs.sensor_data_len = flat != null ? flat.Length : 0;
                    inputs.sensor_count = sensorRig == null ? 0
                        : v7 ? sensorRig.Stamps.Length : sensorRig.SensorCount;
                    inputs.cam_pixels = camPtr;
                    inputs.cam_width = sensorRig != null ? sensorRig.CamWidth : 0;
                    inputs.cam_height = sensorRig != null ? sensorRig.CamHeight : 0;
                    if (v7)
                    {
                        inputs.tick = _controlTick;
                        inputs.flags = ControllerAbi.InFlu;
                        inputs.time_us = (ulong)reportedUs;
                        inputs.stamps = stampPtr;
                    }

                    try
                    {
                        _loader.Step(&inputs, &outputs);
                        _debugExtN = 0;
                        if (_loader.GetDebugExt != null)
                            fixed (float* ext = _debugExt)
                                _debugExtN = Math.Max(0, Math.Min(_debugExt.Length,
                                    _loader.GetDebugExt(ext, _debugExt.Length)));
                        _lastOutputs = outputs;
                        _lastPhase = (int)Math.Round(outputs.debug[0]);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[SimRunner] Controller step faulted, going open-loop: {e.Message}");
                        SafeShutdown();
                    }
                }

                for (int i = 0; i < 8; i++) _actuators[i] = _rawOut[i] = outputs.actuator[i];

                // v7 steers in road-wheel radians, + = left; the car takes a
                // servo fraction, + = right.
                if (v7 && vehicleBehaviour is Vehicles.CarVehicle car)
                {
                    float lockRad = car.MaxSteerDeg * Mathf.Deg2Rad;
                    float rad = _actuators[CarSteerSlot];
                    _actuators[CarSteerSlot] = lockRad > 1e-4f && !float.IsNaN(rad)
                        ? Mathf.Clamp(-rad / lockRad, -1f, 1f) : 0f;
                }
            }

            // Actuation transport delay: hold N control ticks of commands in a
            // ring and apply the oldest (zero commands until the pipe fills) —
            // models the controller→ESC link latency of the real vehicle.
            if (stalled && Mode != DriveMode.Manual)
            {
                // No frame leaves a stalled MCU.
            }
            else if (actuationDelayTicks > 0)
            {
                int n = Mathf.Min(actuationDelayTicks, 32);
                if (_cmdRing == null || _cmdRing.Length != n + 1)
                {
                    _cmdRing = new float[n + 1][];
                    for (int i = 0; i < _cmdRing.Length; i++) _cmdRing[i] = new float[8];
                    _cmdRingHead = 0;
                }
                Array.Copy(_actuators, _cmdRing[_cmdRingHead], 8);
                int tail = (_cmdRingHead + 1) % _cmdRing.Length; // oldest entry
                // LEDs decode from the same delayed array the motors get.
                LatchCommands(_cmdRing[tail]);
                _cmdRingHead = tail;
            }
            else
            {
                LatchCommands(_actuators);
            }

            // 4. Telemetry.
            RecordTelemetry(sp, ref outputs);
            _ticksSinceBase += _decimation;
            _controlTick++;
            _simTime = (float)(TimeUs * 1e-6);

            // 5. Optional hot reload.
            if (autoReloadOnChange && _loader != null && _loader.SourceIsNewer())
                ReloadController();
        }

        private unsafe void RecordTelemetry(float[] sp, ref CtrlOutputs outputs)
        {
            Hub.SetValue("sp/linear", sp != null ? sp[0] : 0f);
            Hub.SetValue("sp/yaw", sp != null ? sp[1] : 0f);
            Hub.SetValue("wheel/left_vel", _wheelVel[0]);
            Hub.SetValue("wheel/right_vel", _wheelVel[1]);
            Hub.SetValue("cmd/left", _actuators[0]);
            Hub.SetValue("cmd/right", _actuators[1]);
            Hub.SetValue("imu/gyro_z", _gyro[2]);
            // VAL-04: the full input frame the controller saw, and its raw output.
            Hub.SetValue(InChannels[0], _inGyro[0]);
            Hub.SetValue(InChannels[1], _inGyro[1]);
            Hub.SetValue(InChannels[2], _inGyro[2]);
            Hub.SetValue(InChannels[3], _inAccel[0]);
            Hub.SetValue(InChannels[4], _inAccel[1]);
            Hub.SetValue(InChannels[5], _inAccel[2]);
            for (int i = 0; i < 4; i++) Hub.SetValue(InChannels[6 + i], _wheelVel[i]);
            for (int i = 0; i < 4; i++) Hub.SetValue(InChannels[10 + i], sp != null ? sp[i] : 0f);
            for (int i = 0; i < 8; i++) Hub.SetValue(InChannels[14 + i], _rawOut[i]);
            Hub.SetValue("mode", Mode == DriveMode.Manual ? 0f : 1f);

            // Car command channels (cmd/steer_deg, cmd/brake, cmd/<motor>/volt) are
            // published by CarVehicle.PublishTelemetry now that drive = voltage.
            _vehicle.PublishTelemetry(Hub);
            sensorRig?.PublishTelemetry(Hub);

            for (int i = 0; i < _debugNames.Length && i < 16; i++)
                Hub.SetValue("dbg/" + _debugNames[i].Trim(), outputs.debug[i]);
            for (int i = 16; i < _debugNames.Length; i++)
                Hub.SetValue("dbg/" + _debugNames[i].Trim(), i - 16 < _debugExtN ? _debugExt[i - 16] : float.NaN);

            Hub.Commit(_simTime);
        }

        private void SafeShutdown()
        {
            try { _loader?.Shutdown?.Invoke(); }
            catch (Exception e) { Debug.LogWarning($"[SimRunner] ctrl_shutdown threw: {e.Message}"); }
            _loader?.Unload();
        }

        /// <summary>
        /// Persist the current drive session's telemetry into TelemetryLogs;
        /// returns the saved path (or null if logging is disabled / nothing to save).
        /// </summary>
        public string SaveTelemetry()
        {
            if (_csv == null) return null;

            // Stamp step-response metrics for the primary loop into the sidecar
            // (sim-vs-real comparison = diffing two sidecar JSONs).
            var m = Telemetry.StepMetrics.Compute(Hub, "sp/linear", "veh/speed");
            if (!m.found) m = Telemetry.StepMetrics.Compute(Hub, "dbg/target_speed", "veh/speed");
            if (m.found)
            {
                _csv.SetMetadata("rise_time_s", m.riseTime.ToString("R"));
                _csv.SetMetadata("overshoot_pct", m.overshootPct.ToString("R"));
                _csv.SetMetadata("settling_time_s", m.settlingTime.ToString("R"));
                _csv.SetMetadata("ss_error", m.ssError.ToString("R"));
            }
            return _csv.Save();
        }

        /// <summary>True when there is recorded telemetry not yet saved to disk.</summary>
        public bool HasUnsavedTelemetry => _csv != null && _csv.HasUnsavedData;

        /// <summary>Sim clock (control-step time), e.g. for session snapshots.</summary>
        public float SimTime => _simTime;

        /// <summary>Sim clock in microseconds: exact, no accumulated rounding.</summary>
        public long TimeUs => TicksToUs(_ticksSinceBase);

        /// <summary>Control ticks since the run started (wraps at 2^32).</summary>
        public uint ControlTick => _controlTick;

        /// <summary>Restore the sim clock when resuming a saved session.</summary>
        public void RestoreSimTime(float t) => RebaseClock((long)Math.Round(Math.Max(0.0, t) * 1e6));

        /// <summary>Restart the integer clock at <paramref name="us"/>, at the
        /// current physics rate. Called on a rate change so earlier ticks keep
        /// the period they were taken at.</summary>
        private void RebaseClock(long us)
        {
            _timeBaseUs = us;
            _ticksSinceBase = 0;
            _baseRateHz = physicsRateHz;
            _simTime = (float)(us * 1e-6);
        }

        /// <summary>
        /// VAL-11 MCU reset: the firmware boots again from power-on (shutdown,
        /// init, configure), its RAM gone, while the car carries on moving.
        /// </summary>
        private void PowerCycleController()
        {
            if (!ControllerReady) return;
            try
            {
                _loader.Shutdown?.Invoke();
                int rc = _loader.Init(controlRateHz);
                if (rc != 0) Debug.LogWarning($"[SimRunner] ctrl_init returned {rc} after an MCU reset");
                ConfigureControllerSensors();
                _lastReportedUs = long.MinValue;
                _lastOutputs = default;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SimRunner] Controller reset faulted: {e.Message}");
                SafeShutdown();
            }
        }

        /// <summary>Reset the run in place: respawn the vehicle and clear telemetry history.</summary>
        public void RestartRun()
        {
            _vehicle?.ResetVehicle();
            sensorRig?.ResetSampling();
            _lastOutputs = default;
            _lastPhase = -99;
            Hub?.Clear();
            RebaseClock(0);
            _controlTick = 0;
            _physCounter = 0;
            _latch.Clear();
            _lastReportedUs = long.MinValue;
        }

        private void OnGUI()
        {
            if (!showModeBox) return;
            var style = new GUIStyle(GUI.skin.box)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            string mode = Mode == DriveMode.Manual ? "MANUAL" : "AUTONOMOUS";
            if (Mode == DriveMode.Autonomous && !ControllerReady) mode = "AUTONOMOUS (no DLL)";
            var rect = new Rect(Screen.width * 0.5f - 130f, 8f, 260f, 26f);
            GUI.Box(rect, $"Mode: {mode}   [M] toggle", style);
        }

        private void OnDisable()
        {
            if (vehicleBehaviour is CarVehicle carForReset)
                carForReset.VehicleReset -= OnVehicleReset;
            SafeShutdown();
            _csv?.End();
        }
    }

    /// <summary>
    /// Commands waiting for their physics step (TIM-02 compute latency): a
    /// bounded FIFO of (apply-at physics tick, actuator vector). Pushes come
    /// in tick order, so the head is always the next one due. A latency longer
    /// than the queue drops the oldest command rather than growing.
    /// </summary>
    public sealed class CommandLatch
    {
        public const int Capacity = 64;
        private readonly float[][] _cmd = new float[Capacity][];
        private readonly long[] _at = new long[Capacity];
        private int _head, _count;

        public int Count => _count;

        public void Push(float[] cmd, long applyAtTick)
        {
            if (_count == Capacity) { _head = (_head + 1) % Capacity; _count--; }
            int slot = (_head + _count) % Capacity;
            _cmd[slot] ??= new float[8];
            Array.Copy(cmd, _cmd[slot], Math.Min(8, cmd.Length));
            _at[slot] = applyAtTick;
            _count++;
        }

        /// <summary>The oldest command due at or before <paramref name="tick"/>,
        /// or null. The array stays valid until Capacity more pushes.</summary>
        public float[] PopDue(long tick)
        {
            if (_count == 0 || _at[_head] > tick) return null;
            var c = _cmd[_head];
            _head = (_head + 1) % Capacity;
            _count--;
            return c;
        }

        public void Clear() { _head = 0; _count = 0; }
    }
}
