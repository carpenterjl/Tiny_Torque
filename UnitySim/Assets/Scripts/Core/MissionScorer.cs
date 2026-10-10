using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Scores one Opus mission run against ground truth: the true path length
    /// between the firmware's phase changes, the true heading change through
    /// the turn, cruise speeds, and a coarse trace. Fed once per physics step
    /// by whoever is watching the run (<see cref="MissionWatcher"/> for a single
    /// run, <see cref="CampaignWatcher"/> for each run of a campaign).
    /// </summary>
    public sealed class MissionScorer
    {
        private readonly CarVehicle _car;
        private readonly TelemetryHub _hub;

        private Vector3 _prevPos;
        private bool _hasPrev;
        private double _truth;              // ground-truth path length, integrated at physics rate
        private int _prevPhase = -99;

        // Ground-truth path length at each phase entry.
        private double _mCruiseA = -1, _mTurn = -1, _mCruiseB = -1, _mBrake = -1, _mDone = -1;
        private float _yawTurnEntry, _yawTurnExit;
        private double _spdSum; private int _spdN;
        private float _spdMin = float.MaxValue, _turnSpdMin = float.MaxValue;

        // Coarse trace so a failed run can be diagnosed without a second launch.
        private readonly System.Text.StringBuilder _trace = new System.Text.StringBuilder();
        private float _nextTrace;

        public MissionScorer(CarVehicle car, TelemetryHub hub)
        {
            _car = car;
            _hub = hub;
        }

        /// <summary>The firmware's mission phase at the last step (-99 = none yet).</summary>
        public int Phase => _prevPhase;
        public string Trace => _trace.ToString();

        /// <summary>One physics step; <paramref name="elapsed"/> is the run's
        /// clock (s). Returns the firmware's phase this step.</summary>
        public int Step(float elapsed)
        {
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

            if (elapsed >= _nextTrace)
            {
                _nextTrace = elapsed + 0.05f;
                if (_trace.Length == 0)
                    _trace.Append("t,phase,fault,v_true,v_meas,v_ref,odo,truth,motor_v,brake,steer,yaw_true,yaw_ctrl,tof,slip_front,slip_rear\n");
                // True slip ratios (front pair = the odometry wheels, rear pair):
                // ground truth for what an encoder-based odometer cannot see.
                float slipF = 0.5f * (_car.WheelSlipRatio(0) + _car.WheelSlipRatio(1));
                float slipR = 0.5f * (_car.WheelSlipRatio(2) + _car.WheelSlipRatio(3));
                _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.00},{1},{2},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.00},{12:0.00},{13:0.00},{14:0.00000},{15:0.00000}\n",
                    elapsed, phase, Dbg("dbg/fault", 0), spd, DbgF("dbg/v_meas"), DbgF("dbg/target_speed"),
                    DbgF("dbg/odo_m"), _truth, DbgF("dbg/motor_v"), DbgF("dbg/brake_cmd"),
                    DbgF("dbg/steer_cmd"), yaw, DbgF("dbg/yaw_deg"), DbgF("sens/tof_front/dist"),
                    slipF, slipR);
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
            return phase;
        }

        /// <summary>Fill the scored fields of <paramref name="res"/> (everything
        /// but completed, note, seed, rate, link and elapsed time).</summary>
        public void Fill(MissionAutorun.Result res)
        {
            res.phase = _prevPhase;
            res.fault = Dbg("dbg/fault", 0);
            res.safeFault = Dbg("dbg/safe_fault", 0);
            res.safeState = Dbg("dbg/safe_state", 0);
            res.odo_final = DbgF("dbg/odo_m");
            res.truth_final = (float)_truth;
            res.controllerStopErr_mm = DbgF("dbg/stop_err_mm");
            res.cruiseSpeedMean = _spdN > 0 ? (float)(_spdSum / _spdN) : 0f;
            res.cruiseSpeedMin = _spdMin == float.MaxValue ? 0f : _spdMin;
            res.turnSpeedMin = _turnSpdMin == float.MaxValue ? 0f : _turnSpdMin;

            res.legA_target = 14.5f;
            res.turn_target_deg = 45f;
            res.legB_target = 7.5f;
            res.brake_target = 1.5f;
            res.total_target = 9.0f;

            if (_mCruiseA >= 0 && _mTurn >= 0)
            {
                res.legA_actual = (float)(_mTurn - _mCruiseA);
                res.legA_err_mm = (res.legA_actual - res.legA_target) * 1000f;
            }
            if (_mTurn >= 0 && _mCruiseB >= 0)
            {
                // Left turn: Unity yaw DECREASES, so negate to report a positive
                // 45 for a correct run.
                res.turn_actual_deg = -Mathf.DeltaAngle(_yawTurnEntry, _yawTurnExit);
                res.turn_err_deg = res.turn_actual_deg - res.turn_target_deg;
            }
            if (_mCruiseB >= 0 && _mBrake >= 0)
            {
                res.legB_actual = (float)(_mBrake - _mCruiseB);
                res.legB_err_mm = (res.legB_actual - res.legB_target) * 1000f;
            }
            if (_mBrake >= 0 && _mDone >= 0)
            {
                res.brake_actual = (float)(_mDone - _mBrake);
                res.brake_err_mm = (res.brake_actual - res.brake_target) * 1000f;
            }
            if (_mCruiseB >= 0 && _mDone >= 0)
            {
                res.total_actual = (float)(_mDone - _mCruiseB);
                res.total_err_mm = (res.total_actual - res.total_target) * 1000f;
            }
            res.drift_mm = (res.odo_final - res.truth_final) * 1000f;
        }

        private int Dbg(string ch, int fallback) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? Mathf.RoundToInt(c.Latest) : fallback;

        private float DbgF(string ch) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? c.Latest : 0f;
    }
}
