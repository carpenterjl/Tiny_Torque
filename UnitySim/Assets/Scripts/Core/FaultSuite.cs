using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIHWSim.Sensors;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// The fault-injection suite (VAL-11) against the firmware's safety layer
    /// (FW-08), end to end: the Opus FOC twin runs its mission once per
    /// scenario in one session (the run restarted in between), each with
    /// faults injected at their physical source by the <see cref="FaultInjector"/>,
    /// and each run is graded on ground truth:
    ///
    ///  - the expected TT_SF_* bit raised within the scenario's time;
    ///  - category 0: no motor current from 50 ms after the trip on (the true
    ///    Iq of every driver, not what the firmware asked for);
    ///  - category 1: the car brought to rest on its motors, at no less than
    ///    1.5 m/s², and no current once it is;
    ///  - scenarios that must not trip: the mission completes, no safety bit;
    ///  - a stalled or reset MCU: the drivers' own watchdog cuts the current
    ///    within its window, and a rebooted firmware never re-arms a moving car.
    ///
    /// Started by <see cref="MissionAutorun"/> when the request asks for it
    /// (OpusMissionRunner -opusFaultSuite 1). Writes the result JSON and a
    /// <c>.txt</c> summary next to it; the process exits 0 when every run
    /// passes, 1 otherwise. The C side of VAL-11 is CTest's tt_safety_faults.
    /// </summary>
    public sealed class FaultSuiteWatcher : MonoBehaviour
    {
        // Safety-layer fault bits (core/tt_safety.h).
        private const int KILL = 0x0001, RC_LOST = 0x0002, RC_DISARM = 0x0004, OVERRUN = 0x0010,
                          DRV_TRIP = 0x0040, DRV_HOT = 0x0080, DRV_STALE = 0x0100, ENC = 0x0200,
                          IMU = 0x0400, BATT = 0x0800, UNDERVOLT = 0x1000, TIPOVER = 0x2000,
                          POS_LOST = 0x8000;
        private const int CRUISE_A = 4, DONE = 10;

        private sealed class Scenario
        {
            public string name;
            public string[] faults;
            public int expect;          // bits, any of which counts; 0 = must not trip
            public int cat = -1;        // 0, 1, or -1 = no trip expected
            public float detectS;       // from injection to the bit
            public float cutByS;        // > 0: true current ~0 this long after injection
            public bool noRearm;        // a rebooted firmware must never re-arm
        }

        private static Scenario S(string name, string fault, int expect, int cat, float detectS) =>
            new Scenario { name = name, faults = string.IsNullOrEmpty(fault) ? new string[0] : fault.Split(';'),
                           expect = expect, cat = cat, detectS = detectS };

        // Faults start 1 s into the first measured leg (4.5 m/s cruise) unless
        // a scenario needs the navigation filter running first.
        private static readonly Scenario[] Suite =
        {
            S("clean",            "",                                   0, -1, 0f),
            S("rc_kill",          "RcKill@p4+1",                        KILL, 0, 0.04f),
            S("rc_link_lost",     "RcLink@p4+1",                        RC_LOST, 1, 0.25f),
            S("rc_silent",        "Stale:rc@p4+1",                      RC_LOST, 1, 0.15f),
            S("rc_disarm",        "RcDisarm@p4+1",                      RC_DISARM, 1, 0.04f),
            new Scenario { name = "core_stall", faults = new[] { "CoreStall@p4+1/0.3" },
                           expect = OVERRUN | DRV_TRIP, cat = 0, detectS = 0.32f, cutByS = 0.07f },
            new Scenario { name = "mcu_reset", faults = new[] { "McuReset@p4+1/0.1" },
                           expect = 0, cat = -1, cutByS = 0.07f, noRearm = true },
            S("drv_overvolt",     "DrvTrip:motor_wheel_rl@p4+1 1",      DRV_TRIP, 0, 0.02f),
            S("drv_overcurrent",  "DrvTrip:motor_wheel_fr@p4+1 64",     DRV_TRIP, 0, 0.02f),
            S("drv_hot",          "DrvHot:motor_wheel_rr@p4+1 100",     DRV_HOT, 1, 0.02f),
            S("drv_silent",       "Stale:motor_wheel_fl@p4+1",          DRV_STALE, 0, 0.07f),
            S("enc_stuck",        "Stuck:enc_fr@p4+1",                  ENC, 1, 0.25f),
            S("enc_glitch_once",  "EncGlitch:enc_fl@p4+1 5000",         0, -1, 0f),
            S("enc_glitching",    "EncGlitch:enc_fl@p4+1 5000 every 0.2", ENC, 1, 0.42f),
            S("imu_nan",          "ImuNan@p4+1",                        IMU, 1, 0.02f),
            S("brownout_short",   "Brownout@p4+1/0.3 5.5",              0, -1, 0f),
            S("brownout_long",    "Brownout@p4+1/2 5.5",                UNDERVOLT, 1, 0.55f),
            S("pack_silent",      "Stale:pack_2s@p4+1",                 BATT, 1, 0.55f),
            S("uwb_one_anchor",   "UwbLoss:0@p4+1",                     0, -1, 0f),
            S("uwb_all_anchors",  "UwbLoss:all@p4+2",                   POS_LOST, 1, 1.1f),
            // Upside down: a low, wide car laid on its side rolls back onto its
            // wheels (it does, in the sim as on the bench); on its roof it stays.
            S("tip_over",         "TipOver@p4+1 180",                   TIPOVER, 0, 0.8f),
        };

        [Serializable]
        public class Outcome
        {
            public string name, faults, note = "";
            public bool pass;
            public int expect, bits, phaseEnd, safeStateEnd;
            public float tInject = -1f, latencyS = -1f, vAtTrip, stopDistM = -1f, stopTimeS = -1f, decel;
            public float maxIqAfterA, maxIqStallA = -1f;
            public bool completed, rearmed;
        }

        [Serializable]
        public class SuiteResult
        {
            public bool pass;
            public int passed, total;
            public string vehicle = "";
            public List<Outcome> runs = new List<Outcome>();
        }

        private MissionAutorun.Request _req;
        private SimulationRunner _runner;
        private CarVehicle _car;
        private Telemetry.TelemetryHub _hub;
        private readonly SuiteResult _suite = new SuiteResult();
        private int _k = -1;
        private bool _finished;
        private float _wait;

        // Per-run state.
        private Scenario _sc;
        private Outcome _o;
        private double _tInj = double.NaN, _tDet = double.NaN, _tRest = double.NaN;
        private double _path, _pathDet;
        private Vector3 _prev;
        private bool _hasPrev;
        private float _runStart;
        private StringBuilder _trace;
        private List<Scenario> _list;

        public void Configure(MissionAutorun.Request req) { _req = req; }

        private void FixedUpdate()
        {
            if (_finished) return;
            if (_runner == null)
            {
                _wait += Time.fixedDeltaTime;
                _runner = FindFirstObjectByType<SimulationRunner>();
                _car = FindFirstObjectByType<CarVehicle>();
                if (_runner == null || _car == null)
                {
                    if (_wait > 10f) Finish("no runner/car in scene");
                    return;
                }
                _hub = _runner.Hub;
                _suite.vehicle = _req.vehicle;
                // -opusFaultOnly a,b runs just those scenarios.
                _list = new List<Scenario>();
                var only = string.IsNullOrEmpty(_req.faultOnly) ? null
                         : new HashSet<string>(_req.faultOnly.Split(','));
                foreach (var sc in Suite) if (only == null || only.Contains(sc.name)) _list.Add(sc);
                Next(first: true);
                return;
            }

            double t = _runner.SimTime;
            int phase = Dbg("dbg/state", -99);
            int bits = Dbg("dbg/safe_fault", 0);
            int sstate = Dbg("dbg/safe_state", 0);
            float spd = Mathf.Abs(_car.ForwardSpeed);
            float iq = 0f;
            if (_runner.sensorRig != null)
                foreach (var m in _runner.sensorRig.Motors) iq += Mathf.Abs(m.Current);

            var p = _car.transform.position;
            if (_hasPrev) _path += Vector3.Distance(_prev, p);
            _prev = p; _hasPrev = true;

            if (double.IsNaN(_tInj)) { double fs = _runner.Faults.FirstStart; if (!double.IsNaN(fs)) _tInj = fs; }
            _o.bits |= bits;
            _o.phaseEnd = phase;
            _o.safeStateEnd = sstate;
            if (phase == DONE) _o.completed = true;
            if (_trace != null)
            {
                _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.0000},{1},{2},{3},{4:0.000},{5:0.0000},{6:0.000},{7:0.000},{8:0.000},{9:0.000},{10:0.000},{11:0.000},{12:0.000},{13},{14:0.00},{15:0.00},{16:0.0000},{17:0.00}",
                    t, phase, sstate, bits, spd, iq, DbgF("dbg/nav_ok"), DbgF("dbg/nav_x"), DbgF("dbg/nav_y"),
                    p.z, -p.x, DbgF("dbg/odo_m"), DbgF("dbg/v_meas"), Dbg("dbg/fault", 0),
                    DbgF("dbg/yaw_deg"), -_car.transform.eulerAngles.y, DbgF("dbg/steer_cmd"), DbgF("veh/steer_deg"));
                if (_runner.sensorRig != null)
                    foreach (var m in _runner.sensorRig.Motors)
                        _trace.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, ",{0:0.0000}", m.Current);
                _trace.Append('\n');
            }

            if (!double.IsNaN(_tInj))
            {
                double since = t - _tInj;
                if (_sc.cutByS > 0f && since >= _sc.cutByS && since < _sc.cutByS + 0.2)
                    _o.maxIqStallA = Mathf.Max(_o.maxIqStallA, iq);
                if (_sc.noRearm && since > 0.2 && (sstate == 1 || sstate == 2)) _o.rearmed = true;
                if (_sc.expect != 0 && double.IsNaN(_tDet) && (bits & _sc.expect) != 0)
                {
                    _tDet = t;
                    _o.latencyS = (float)since;
                    _o.vAtTrip = spd;
                    _pathDet = _path;
                }
            }
            if (!double.IsNaN(_tDet))
            {
                double after = t - _tDet;
                if (_sc.cat == 0 && after >= 0.05) _o.maxIqAfterA = Mathf.Max(_o.maxIqAfterA, iq);
                if (_sc.cat == 1)
                {
                    if (double.IsNaN(_tRest) && spd < 0.05f)
                    {
                        _tRest = t;
                        _o.stopTimeS = (float)after;
                        _o.stopDistM = (float)(_path - _pathDet);
                        _o.decel = after > 0.0 ? _o.vAtTrip / (float)after : 0f;
                    }
                    if (!double.IsNaN(_tRest) && t - _tRest >= 0.6) _o.maxIqAfterA = Mathf.Max(_o.maxIqAfterA, iq);
                }
            }

            // When the run has shown what it can.
            bool end;
            if (_sc.expect == 0 && !_sc.noRearm)
                end = phase == DONE || phase == -1 || t - _runStart > 45f;
            else if (_sc.noRearm)
                end = !double.IsNaN(_tInj) && t - _tInj > 6.0;
            else if (!double.IsNaN(_tDet))
                end = _sc.cat == 0 ? t - _tDet > 1.5 : (!double.IsNaN(_tRest) && t - _tRest > 1.2) || t - _tDet > 8.0;
            else
                end = (!double.IsNaN(_tInj) && t - _tInj > 4.0) || t - _runStart > 45f;
            if (end) { Grade(); Next(first: false); }
        }

        private void Grade()
        {
            var o = _o;
            var sc = _sc;
            var why = new List<string>();
            if (sc.expect == 0 && !sc.noRearm)
            {
                if (!o.completed) why.Add($"mission did not complete (phase {o.phaseEnd})");
                if (o.bits != 0) why.Add($"false trip 0x{o.bits:X4}");
            }
            if (sc.expect != 0)
            {
                if (double.IsNaN(_tDet)) why.Add($"0x{sc.expect:X4} never raised (bits 0x{o.bits:X4})");
                else if (o.latencyS > sc.detectS + 1e-3f) why.Add($"raised after {o.latencyS * 1000f:0} ms > {sc.detectS * 1000f:0}");
                // A category-0 fault latches: FAULT to the end. A category-1
                // one may drop back to DISARMED once its condition has gone;
                // either way the mission stays faulted and never resumes.
                bool stateOk = o.safeStateEnd == 3 || (sc.cat == 1 && o.safeStateEnd == 0);
                if (!stateOk) why.Add($"safety state {o.safeStateEnd} at the end");
                if (o.phaseEnd != -1) why.Add($"mission phase {o.phaseEnd}, not FAULT");
            }
            if (sc.cat == 0 && !double.IsNaN(_tDet) && o.maxIqAfterA > 0.02f)
                why.Add($"motor current {o.maxIqAfterA:0.000} A after the cut");
            if (sc.cat == 1 && !double.IsNaN(_tDet))
            {
                if (double.IsNaN(_tRest)) why.Add("never came to rest");
                else
                {
                    float vmax = o.vAtTrip / 1.5f + 1.0f;
                    if (o.stopTimeS > vmax) why.Add($"rest after {o.stopTimeS:0.00} s > {vmax:0.00}");
                    if (o.vAtTrip > 1f && o.decel < 1.5f) why.Add($"mean decel {o.decel:0.00} m/s² < 1.5");
                    if (o.maxIqAfterA > 0.02f) why.Add($"motor current {o.maxIqAfterA:0.000} A once stopped");
                }
            }
            if (sc.cutByS > 0f && !(o.maxIqStallA >= 0f && o.maxIqStallA <= 0.02f))
                why.Add($"drivers still at {o.maxIqStallA:0.000} A {sc.cutByS * 1000f:0} ms into the stall");
            if (sc.noRearm && o.rearmed) why.Add("the rebooted firmware re-armed");
            o.pass = why.Count == 0;
            o.note = string.Join("; ", why);
            _suite.runs.Add(o);
            if (o.pass) _suite.passed++;

            string detail = sc.expect != 0
                ? $"bit 0x{sc.expect:X4} at {o.latencyS * 1000f:0} ms, {(sc.cat == 0 ? $"Iq after {o.maxIqAfterA:0.000} A" : $"stop {o.stopDistM:0.00} m in {o.stopTimeS:0.00} s from {o.vAtTrip:0.00} m/s ({o.decel:0.00} m/s²)")}"
                : sc.noRearm ? $"Iq in stall {o.maxIqStallA:0.000} A, re-armed {o.rearmed}"
                : $"completed {o.completed}, bits 0x{o.bits:X4}";
            if (sc.cutByS > 0f && sc.expect != 0) detail += $", Iq in stall {o.maxIqStallA:0.000} A";
            if (_trace != null && !string.IsNullOrEmpty(_req.resultPath))
            {
                try { File.WriteAllText(Path.ChangeExtension(_req.resultPath, "." + sc.name + ".trace.csv"), _trace.ToString()); }
                catch (Exception e) { Debug.LogWarning($"[FAULT] trace write failed: {e.Message}"); }
            }
            Debug.Log($"[FAULT] {(o.pass ? "PASS" : "FAIL")} {sc.name,-17} {string.Join(";", sc.faults),-36} {detail}" +
                      (o.pass ? "" : "  — " + o.note));
        }

        private void Next(bool first)
        {
            _k++;
            if (_k >= _list.Count) { Finish(""); return; }
            if (!first) _runner.RestartRun();
            _sc = _list[_k];
            _trace = _req.saveTelemetry
                ? new StringBuilder("t,phase,safe_state,safe_fault,speed,iq_sum,nav_ok,nav_x,nav_y,true_x,true_y,odo_m,v_meas,fault,yaw_fw,yaw_true,steer_cmd,steer_deg,iq_fl,iq_fr,iq_rl,iq_rr\n")
                : null;
            var specs = new List<FaultSpec>();
            foreach (var f in _sc.faults) specs.Add(FaultSpec.Parse(f));
            _runner.Faults.Set(specs);
            _o = new Outcome { name = _sc.name, faults = string.Join(";", _sc.faults), expect = _sc.expect };
            _tInj = _tDet = _tRest = double.NaN;
            _path = _pathDet = 0.0;
            _hasPrev = false;
            _runStart = _runner.SimTime;
        }

        private float DbgF(string ch) =>
            _hub != null && _hub.TryGetChannel(ch, out var c) ? c.Latest : float.NaN;

        private int Dbg(string ch, int fallback)
        {
            if (_hub == null || !_hub.TryGetChannel(ch, out var c)) return fallback;
            float v = c.Latest;
            return float.IsNaN(v) ? fallback : Mathf.RoundToInt(v);
        }

        private void Finish(string note)
        {
            _finished = true;
            _runner?.Faults.Set(null);
            _suite.total = _list != null ? _list.Count : Suite.Length;
            _suite.pass = note.Length == 0 && _suite.passed == _suite.total;
            var sb = new StringBuilder();
            foreach (var o in _suite.runs)
                sb.AppendLine($"{(o.pass ? "PASS" : "FAIL")} {o.name,-17} {o.faults,-36} {o.note}");
            string head = $"[FAULT] RESULT {_suite.passed}/{_suite.total} passed{(note.Length > 0 ? " — " + note : "")}";
            sb.AppendLine(head);
            Debug.Log(head);
            try
            {
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_req.resultPath));
                    File.WriteAllText(_req.resultPath, JsonUtility.ToJson(_suite, true));
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".txt"), sb.ToString());
                }
            }
            catch (Exception e) { Debug.LogError($"[FAULT] write failed: {e.Message}"); }
#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(_suite.pass ? 0 : 1);
            else UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(_suite.pass ? 0 : 1);
#endif
        }
    }
}
