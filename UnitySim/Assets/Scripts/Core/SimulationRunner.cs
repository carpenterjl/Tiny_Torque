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
    /// (zero-order hold, like a real DAC/PWM latch). Sensor sampling, the
    /// native controller call, and telemetry commit all happen on the control
    /// tick, so logged timestamps are uniform at the control rate.
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

        private IControlledVehicle _vehicle;
        private IManualDriver _manualDriver;
        private ISetpointSource _setpointSource;
        private NativeControllerLoader _loader;
        private CsvLogger _csv;

        private int _decimation = 1;
        private int _physCounter;
        private float _simTime;

        // Reusable managed buffers (avoid per-tick GC).
        private readonly float[] _wheelVel = new float[4];
        private readonly float[] _gyro = new float[3];
        private readonly float[] _accel = new float[3];
        private readonly float[] _actuators = new float[8];

        private string[] _debugNames = Array.Empty<string>();

        // Actuation transport delay ring (actuationDelayTicks control ticks).
        private float[][] _cmdRing;
        private int _cmdRingHead;

        // Session noise seed: set once per process launch (before any sensor
        // samples) from GameSettings.noiseSeed, or drawn randomly when 0. Either
        // way the effective value is stamped into the CSV metadata so any run
        // can be reproduced exactly afterwards.
        private static bool _seedApplied;
        private static void ApplyNoiseSeed()
        {
            if (_seedApplied) return;
            _seedApplied = true;
            int configured = Persistence.SettingsStore.Current.noiseSeed;
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
        /// </summary>
        private void ConfigureRates()
        {
            physicsRateHz = Mathf.Max(1, physicsRateHz);
            controlRateHz = Mathf.Clamp(controlRateHz, 1, physicsRateHz);
            _decimation = Mathf.Max(1, Mathf.RoundToInt((float)physicsRateHz / controlRateHz));
            // Snap control rate to the exact achievable value after decimation.
            controlRateHz = Mathf.RoundToInt((float)physicsRateHz / _decimation);

            Time.fixedDeltaTime = 1f / physicsRateHz;
        }

        private void Awake()
        {
            ConfigureRates();
            ApplyNoiseSeed();
            Hub = new TelemetryHub();
        }

        private void Start()
        {
            // Resolve wiring here rather than in Awake: scene builders assign
            // these fields immediately AFTER AddComponent, but AddComponent already
            // ran Awake — so reading them in Awake yields nulls/defaults.
            ConfigureRates();   // the builder's rates are only in place now

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
            if (_loader != null && !string.IsNullOrEmpty(_loader.SourcePath) && File.Exists(_loader.SourcePath))
                md["dll_write_utc"] = File.GetLastWriteTimeUtc(_loader.SourcePath).ToString("o");
            return md;
        }

        private string AbsoluteDllPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, dllRelativePath));

        public void LoadController()
        {
            _loader ??= new NativeControllerLoader();
            string path = AbsoluteDllPath();
            if (_loader.Load(path))
            {
                int rc = _loader.Init(controlRateHz);
                if (rc != 0)
                    Debug.LogWarning($"[SimRunner] ctrl_init returned {rc}");
                _debugNames = _loader.ReadDebugNames();
                ConfigureControllerSensors();
            }
            else
            {
                Debug.LogWarning("[SimRunner] Running open-loop (no controller). " +
                                 "Build controller.dll with Controllers/build.ps1.");
                _debugNames = Array.Empty<string>();
            }
        }

        /// <summary>
        /// Hand the vehicle's sensor manifest to the controller via the optional
        /// ABI v2 ctrl_configure() export. No-op for pre-v2 controllers or when
        /// the vehicle has no configurable sensors.
        /// </summary>
        private unsafe void ConfigureControllerSensors()
        {
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
        /// or a run restart). Runs ctrl_init + ctrl_configure again so a stateful
        /// controller drops the odometry, heading and phase it accumulated before
        /// the jump, and flushes the actuation-delay pipe so no pre-teleport
        /// command survives it. Cheaper and less disruptive than a full DLL
        /// reload: the library stays mapped and debug names are unchanged.
        /// </summary>
        private void OnVehicleReset()
        {
            _cmdRing = null;
            _cmdRingHead = 0;
            if (!ControllerReady) return;
            try
            {
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

        /// <summary>Arcade assists help humans only — C firmware faces raw physics.</summary>
        private void SyncAssistGate()
        {
            if (vehicleBehaviour is Vehicles.CarVehicle car)
                car.assistsActive = Mode == DriveMode.Manual;
        }

        private void FixedUpdate()
        {
            if (_vehicle == null) return;

            _physCounter++;
            if (_physCounter >= _decimation)
            {
                _physCounter = 0;
                ControlStep();
            }

            _vehicle.StepPhysics(Time.fixedDeltaTime);
        }

        private unsafe void ControlStep()
        {
            float controlDt = _decimation / (float)physicsRateHz;

            // 1. Sensors — built-in (wheel vel + IMU) and the configurable rig.
            _vehicle.SampleSensors(controlDt, _wheelVel, _gyro, _accel);
            sensorRig?.Sample(controlDt, _simTime);

            // 2. Operator setpoints.
            float[] sp = _setpointSource != null ? _setpointSource.Setpoints : null;

            // 3. Commands: Manual reads human input directly; Autonomous calls the DLL.
            Array.Clear(_actuators, 0, _actuators.Length);
            CtrlOutputs outputs = default;

            if (Mode == DriveMode.Manual)
            {
                _manualDriver?.ReadManualCommands(_actuators);
            }
            else if (ControllerReady)
            {
                CtrlInputs inputs = default;
                inputs.time_s = _simTime;
                inputs.dt_s = controlDt;
                for (int i = 0; i < 3; i++) { inputs.gyro[i] = _gyro[i]; inputs.accel[i] = _accel[i]; }
                for (int i = 0; i < 4; i++) inputs.wheel_vel[i] = _wheelVel[i];
                if (sp != null) for (int i = 0; i < 4; i++) inputs.setpoint[i] = sp[i];

                // Pin the rig's flat data + camera frame for the duration of the
                // native call so no GC move invalidates the pointers.
                float[] flat = sensorRig != null ? sensorRig.FlatData : null;
                byte[] cam = sensorRig != null ? sensorRig.CamPixels : null;
                fixed (float* flatPtr = flat)
                fixed (byte* camPtr = cam)
                {
                    inputs.sensor_data = flatPtr;
                    inputs.sensor_data_len = flat != null ? flat.Length : 0;
                    inputs.sensor_count = sensorRig != null ? sensorRig.SensorCount : 0;
                    inputs.cam_pixels = camPtr;
                    inputs.cam_width = sensorRig != null ? sensorRig.CamWidth : 0;
                    inputs.cam_height = sensorRig != null ? sensorRig.CamHeight : 0;

                    try
                    {
                        _loader.Step(&inputs, &outputs);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[SimRunner] Controller step faulted, going open-loop: {e.Message}");
                        SafeShutdown();
                    }
                }

                for (int i = 0; i < 8; i++) _actuators[i] = outputs.actuator[i];
            }

            // Actuation transport delay: hold N control ticks of commands in a
            // ring and apply the oldest (zero commands until the pipe fills) —
            // models the controller→ESC link latency of the real vehicle.
            if (actuationDelayTicks > 0)
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
                _vehicle.SetCommands(_cmdRing[tail]);
                _cmdRingHead = tail;
            }
            else
            {
                _vehicle.SetCommands(_actuators);
            }

            // 4. Telemetry.
            RecordTelemetry(sp, ref outputs);
            _simTime += controlDt;

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
            Hub.SetValue("mode", Mode == DriveMode.Manual ? 0f : 1f);

            // Car command channels (cmd/steer_deg, cmd/brake, cmd/<motor>/volt) are
            // published by CarVehicle.PublishTelemetry now that drive = voltage.
            _vehicle.PublishTelemetry(Hub);
            sensorRig?.PublishTelemetry(Hub);

            for (int i = 0; i < _debugNames.Length && i < 16; i++)
                Hub.SetValue("dbg/" + _debugNames[i].Trim(), outputs.debug[i]);

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

        /// <summary>Restore the sim clock when resuming a saved session.</summary>
        public void RestoreSimTime(float t) => _simTime = Mathf.Max(0f, t);

        /// <summary>Reset the run in place: respawn the vehicle and clear telemetry history.</summary>
        public void RestartRun()
        {
            _vehicle?.ResetVehicle();
            Hub?.Clear();
            _simTime = 0f;
            _physCounter = 0;
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
}
