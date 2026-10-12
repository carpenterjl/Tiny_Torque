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
    ///
    /// Given a route (NAV-06) it also scores the run against the route: the
    /// car's true position projected onto it every physics step — cross-track
    /// error (RMS, p95, max) and heading error while the car drives, speed
    /// against the firmware's plan while it follows, the peak lateral
    /// acceleration, the lap time, and where it stopped along and across the
    /// route against the route's end.
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

        // Route mode (NAV-06).
        private readonly RouteTable _route;
        private int _rIdx;
        private RouteTable.Proj _rProj;
        private readonly System.Collections.Generic.List<float> _xte = new System.Collections.Generic.List<float>();
        private double _xteSq, _headSq, _vErrSq;
        private int _headN, _vErrN;
        private readonly System.Collections.Generic.List<float> _vErr = new System.Collections.Generic.List<float>();
        private float _xteMax, _latMax, _rateF, _psiPrev = float.NaN, _tLaunch = -1f, _tDone = -1f, _elapsed;

        public MissionScorer(CarVehicle car, TelemetryHub hub, RouteTable route = null)
        {
            _car = car;
            _hub = hub;
            _route = route != null && route.Count >= 2 ? route : null;
        }

        /// <summary>The route this run is scored against, or null.</summary>
        public RouteTable Route => _route;

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
            _elapsed = elapsed;
            if (_route != null) RouteStep(phase, spd);

            if (phase == 4 || phase == 5 || phase == 6)
            {
                _spdSum += spd; _spdN++;
                if (spd < _spdMin) _spdMin = spd;
                if (phase == 5 && spd < _turnSpdMin) _turnSpdMin = spd;
            }

            if (_route != null && elapsed >= _nextTrace)
            {
                _nextTrace = elapsed + 0.05f;
                var q = RouteTable.Site(_car.transform.position);
                if (_trace.Length == 0)
                    _trace.Append("t,phase,fault,v_true,v_meas,v_ref,v_plan,x,y,s_true,ey_true,head_err_deg,trk_s,trk_ey,trk_kappa,steer,nav_x,nav_y,odo,truth\n");
                _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.00},{1},{2},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7:0.0000},{8:0.0000},{9:0.000},{10:0.0000},{11:0.000},{12:0.000},{13:0.0000},{14:0.0000},{15:0.0000},{16:0.0000},{17:0.0000},{18:0.000},{19:0.000}\n",
                    elapsed, phase, Dbg("dbg/fault", 0), spd, DbgF("dbg/v_meas"), DbgF("dbg/target_speed"), DbgF("dbg/v_plan"),
                    q.x, q.y, _rProj.s, _rProj.ey,
                    RouteTable.Wrap(RouteTable.SiteHeading(_car.transform.forward) - _rProj.psi) * Mathf.Rad2Deg,
                    DbgF("dbg/trk_s"), DbgF("dbg/trk_ey"), DbgF("dbg/trk_kappa"), DbgF("dbg/steer_cmd"),
                    DbgF("dbg/nav_x"), DbgF("dbg/nav_y"), DbgF("dbg/odo_m"), _truth);
            }
            if (_route == null && elapsed >= _nextTrace)
            {
                _nextTrace = elapsed + 0.05f;
                if (_trace.Length == 0)
                    _trace.Append("t,phase,fault,v_true,v_meas,v_ref,odo,truth,motor_v,brake,steer,yaw_true,yaw_ctrl,tof,slip_front,slip_rear,mu_est,f_gain,odo_scale,steer_trim,drive_slip,launch_slip\n");
                // True slip ratios (front pair = the odometry wheels, rear pair):
                // ground truth for what an encoder-based odometer cannot see.
                float slipF = 0.5f * (_car.WheelSlipRatio(0) + _car.WheelSlipRatio(1));
                float slipR = 0.5f * (_car.WheelSlipRatio(2) + _car.WheelSlipRatio(3));
                _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.00},{1},{2},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.00},{12:0.00},{13:0.00},{14:0.00000},{15:0.00000},{16:0.000},{17:0.0000},{18:0.0000},{19:0.0000},{20:0.0000},{21:0.0000}\n",
                    elapsed, phase, Dbg("dbg/fault", 0), spd, DbgF("dbg/v_meas"), DbgF("dbg/target_speed"),
                    DbgF("dbg/odo_m"), _truth, DbgF("dbg/motor_v"), DbgF("dbg/brake_cmd"),
                    DbgF("dbg/steer_cmd"), yaw, DbgF("dbg/yaw_deg"), DbgF("sens/tof_front/dist"),
                    slipF, slipR, DbgF("dbg/mu_est"), DbgF("dbg/f_gain"), DbgF("dbg/odo_scale"), DbgF("dbg/steer_trim"), DbgF("dbg/drive_slip"), DbgF("dbg/launch_slip"));
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

        /// <summary>The route part of one physics step: project the true pose
        /// and keep the statistics, while the car drives (launch, follow, brake,
        /// creep).</summary>
        private void RouteStep(int phase, float spd)
        {
            var q = RouteTable.Site(_car.transform.position);
            float psi = RouteTable.SiteHeading(_car.transform.forward);
            _rProj = _route.Project(q.x, q.y, _rIdx);
            _rIdx = _rProj.i;
            float dt = Time.fixedDeltaTime;
            bool drive = phase == 3 || phase == 11 || phase == 7 || phase == 8;
            if (phase == 3 && _tLaunch < 0f) _tLaunch = _elapsed;
            if (phase == 10 && _tDone < 0f) _tDone = _elapsed;
            if (drive)
            {
                float e = Mathf.Abs(_rProj.ey);
                _xte.Add(e);
                _xteSq += e * e;
                _xteMax = Mathf.Max(_xteMax, e);
                float he = RouteTable.Wrap(psi - _rProj.psi);
                _headSq += he * he;
                _headN++;
                // The heading differenced at the physics rate is noisy: the
                // yaw rate through 50 ms first (the accelerometer's own
                // bandwidth, near enough).
                if (!float.IsNaN(_psiPrev) && dt > 0f)
                {
                    _rateF += (RouteTable.Wrap(psi - _psiPrev) / dt - _rateF) * (1f - Mathf.Exp(-dt / 0.05f));
                    _latMax = Mathf.Max(_latMax, Mathf.Abs(spd * _rateF));
                }
            }
            if (phase == 11)
            {
                float ev = spd - DbgF("dbg/target_speed");
                _vErr.Add(Mathf.Abs(ev));
                _vErrSq += ev * ev;
                _vErrN++;
            }
            _psiPrev = psi;
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
            res.cmd_delay_ms = DbgF("dbg/cmd_delay_ms");
            res.probe_mu = DbgF("dbg/probe_mu");

            if (_route != null)
            {
                var q = RouteTable.Site(_car.transform.position);
                var pr = _route.Project(q.x, q.y, _rIdx);
                res.route = _route.name;
                res.route_id = _route.id;
                res.xte_rms_mm = _xte.Count > 0 ? (float)System.Math.Sqrt(_xteSq / _xte.Count) * 1000f : -1f;
                res.xte_p95_mm = _xte.Count > 0 ? CampaignWatcher.Pct(_xte, 95f) * 1000f : -1f;
                res.xte_max_mm = _xteMax * 1000f;
                res.head_rms_deg = _headN > 0 ? (float)System.Math.Sqrt(_headSq / _headN) * Mathf.Rad2Deg : -1f;
                res.head_end_deg = RouteTable.Wrap(RouteTable.SiteHeading(_car.transform.forward) -
                                                   _route.psi[_route.Count - 1]) * Mathf.Rad2Deg;
                res.v_err_rms = _vErrN > 0 ? (float)System.Math.Sqrt(_vErrSq / _vErrN) : -1f;
                res.v_err_p95 = _vErr.Count > 0 ? CampaignWatcher.Pct(_vErr, 95f) : -1f;
                res.lat_acc_max = _latMax;
                res.lap_s = _tLaunch >= 0f && _tDone >= 0f ? _tDone - _tLaunch : -1f;
                res.route_stop_mm = (pr.s - _route.s[_route.Count - 1]) * 1000f;
                res.route_stop_lat_mm = pr.ey * 1000f;
            }
        }

        private int Dbg(string ch, int fallback) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? Mathf.RoundToInt(c.Latest) : fallback;

        private float DbgF(string ch) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? c.Latest : 0f;
    }
}
