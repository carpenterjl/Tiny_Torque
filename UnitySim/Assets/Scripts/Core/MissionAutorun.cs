using System;
using System.IO;
using AIHWSim.Garage;
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
            public string note = "";
        }

        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "opus_mission_request.json");

        private static Request _req;

        /// <summary>True when this process was launched to run a mission.</summary>
        public static bool Active => _req != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            _req = null;
            if (!File.Exists(RequestPath)) return;
            try { _req = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath)); }
            catch (Exception e) { Debug.LogError($"[MissionAutorun] bad request: {e.Message}"); return; }
            if (_req == null) return;

            // Configure the session before any scene Awake runs, which is the
            // whole reason this hooks BeforeSceneLoad.
            var design = VehiclePresets.Resolve(_req.vehicle);
            var track = TrackPresets.Resolve(_req.track);
            if (design == null || track == null)
            {
                Debug.LogError($"[MissionAutorun] unknown vehicle '{_req.vehicle}' or track '{_req.track}'");
                _req = null;
                return;
            }

            GameFlow.ActiveDesign = design;
            GameFlow.ActiveTrack = track;
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

            Debug.Log($"[MissionAutorun] armed: {_req.vehicle} on {_req.track}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
        {
            if (_req == null) return;
            var go = new GameObject("MissionWatcher");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MissionWatcher>().Configure(_req);
        }
    }

    /// <summary>Scores one mission run against ground truth, then ends the process.</summary>
    public sealed class MissionWatcher : MonoBehaviour
    {
        private MissionAutorun.Request _req;
        private readonly MissionAutorun.Result _res = new MissionAutorun.Result();

        private SimulationRunner _runner;
        private CarVehicle _car;
        private TelemetryHub _hub;

        private Vector3 _prevPos;
        private bool _hasPrev;
        private double _truth;              // ground-truth path length, integrated at physics rate
        private float _elapsed;
        private int _prevPhase = -99;
        private bool _finished;

        // Ground-truth path length at each phase entry.
        private double _mCruiseA = -1, _mTurn = -1, _mCruiseB = -1, _mBrake = -1, _mDone = -1;
        private float _yawTurnEntry, _yawTurnExit;
        private double _spdSum; private int _spdN;
        private float _spdMin = float.MaxValue, _turnSpdMin = float.MaxValue;

        // Coarse trace so a failed run can be diagnosed without a second launch.
        private readonly System.Text.StringBuilder _trace = new System.Text.StringBuilder();
        private float _nextTrace;

        public void Configure(MissionAutorun.Request req) { _req = req; }

        private void FixedUpdate()
        {
            if (_finished) return;
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
            }

            // Ground truth: chord-sum of the actual pose at 400 Hz. Unlike the
            // encoder this has no integration bias to correct — the positions are
            // exact samples, not a velocity integral.
            var p = _car.transform.position;
            if (_hasPrev) _truth += Vector3.Distance(_prevPos, p);
            _prevPos = p; _hasPrev = true;

            int phase = Dbg("dbg/state", -99);
            float yaw = _car.transform.eulerAngles.y;
            float spd = _car.ForwardSpeed;

            if (phase == 4 || phase == 5 || phase == 6)
            {
                _spdSum += spd; _spdN++;
                if (spd < _spdMin) _spdMin = spd;
                if (phase == 5 && spd < _turnSpdMin) _turnSpdMin = spd;
            }

            if (_elapsed >= _nextTrace)
            {
                _nextTrace = _elapsed + 0.05f;
                if (_trace.Length == 0)
                    _trace.Append("t,phase,fault,v_true,v_meas,v_ref,odo,truth,motor_v,brake,steer,yaw_true,yaw_ctrl,tof\n");
                _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.00},{1},{2},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.00},{12:0.00},{13:0.00}\n",
                    _elapsed, phase, Dbg("dbg/fault", 0), spd, DbgF("dbg/v_meas"), DbgF("dbg/target_speed"),
                    DbgF("dbg/odo_m"), _truth, DbgF("dbg/motor_v"), DbgF("dbg/brake_cmd"),
                    DbgF("dbg/steer_cmd"), yaw, DbgF("dbg/yaw_deg"), DbgF("sens/tof_front/dist"));
            }

            if (phase != _prevPhase)
            {
                switch (phase)
                {
                    case 4: _mCruiseA = _truth; break;
                    case 5: _mTurn = _truth; _yawTurnEntry = yaw; break;
                    case 6: _mCruiseB = _truth; _yawTurnExit = yaw; break;
                    case 7: _mBrake = _truth; break;
                    case 10: _mDone = _truth; break;
                }
                _prevPhase = phase;
            }

            if (phase == 10) { Finish(true, ""); return; }
            if (phase == -1) { Finish(false, "controller reported FAULT"); return; }
            if (_elapsed > _req.timeoutSec) { Finish(false, "timeout"); return; }
        }

        private int Dbg(string ch, int fallback) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? Mathf.RoundToInt(c.Latest) : fallback;

        private float DbgF(string ch) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? c.Latest : 0f;

        private static float AngleDelta(float from, float to)
        {
            float d = Mathf.DeltaAngle(from, to);
            return d;
        }

        private void Finish(bool ok, string note)
        {
            _finished = true;
            _res.completed = ok;
            _res.note = note;
            _res.phase = _prevPhase;
            _res.fault = Dbg("dbg/fault", 0);
            _res.elapsedSec = _elapsed;
            _res.odo_final = DbgF("dbg/odo_m");
            _res.truth_final = (float)_truth;
            _res.controllerStopErr_mm = DbgF("dbg/stop_err_mm");
            _res.cruiseSpeedMean = _spdN > 0 ? (float)(_spdSum / _spdN) : 0f;
            _res.cruiseSpeedMin = _spdMin == float.MaxValue ? 0f : _spdMin;
            _res.turnSpeedMin = _turnSpdMin == float.MaxValue ? 0f : _turnSpdMin;

            _res.legA_target = 14.5f;
            _res.turn_target_deg = 45f;
            _res.legB_target = 7.5f;
            _res.brake_target = 1.5f;
            _res.total_target = 9.0f;

            if (_mCruiseA >= 0 && _mTurn >= 0)
            {
                _res.legA_actual = (float)(_mTurn - _mCruiseA);
                _res.legA_err_mm = (_res.legA_actual - _res.legA_target) * 1000f;
            }
            if (_mTurn >= 0 && _mCruiseB >= 0)
            {
                // Left turn: Unity yaw DECREASES, so negate to report a positive
                // 45 for a correct run.
                _res.turn_actual_deg = -AngleDelta(_yawTurnEntry, _yawTurnExit);
                _res.turn_err_deg = _res.turn_actual_deg - _res.turn_target_deg;
            }
            if (_mCruiseB >= 0 && _mBrake >= 0)
            {
                _res.legB_actual = (float)(_mBrake - _mCruiseB);
                _res.legB_err_mm = (_res.legB_actual - _res.legB_target) * 1000f;
            }
            if (_mBrake >= 0 && _mDone >= 0)
            {
                _res.brake_actual = (float)(_mDone - _mBrake);
                _res.brake_err_mm = (_res.brake_actual - _res.brake_target) * 1000f;
            }
            if (_mCruiseB >= 0 && _mDone >= 0)
            {
                _res.total_actual = (float)(_mDone - _mCruiseB);
                _res.total_err_mm = (_res.total_actual - _res.total_target) * 1000f;
            }
            _res.drift_mm = (_res.odo_final - _res.truth_final) * 1000f;

            try
            {
                string json = JsonUtility.ToJson(_res, true);
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_req.resultPath));
                    File.WriteAllText(_req.resultPath, json);
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".trace.csv"), _trace.ToString());
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
