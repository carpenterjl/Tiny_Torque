using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AIHWSim.Vehicles;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIHWSim.Core
{
    /// <summary>
    /// Runs a <see cref="Campaign"/>: for each planned run, puts that run's
    /// car into the session, reloads the track scene, scores the mission like
    /// <see cref="MissionWatcher"/>, and moves on. At the end it writes the
    /// result JSON, a CSV (one row per run: draws, then metrics) and a text
    /// report with percentiles and the gate, and exits 0 when the gate passes.
    /// Started by <see cref="MissionAutorun"/> (OpusMissionRunner -opusCampaign N).
    /// </summary>
    // After the runner, explicitly: the score pairs each step's phase with the
    // car's pose, and an order left to chance flips between scene loads (one
    // physics step of path, 11 mm at cruise, on every measured leg).
    [DefaultExecutionOrder(1000)]
    public sealed class CampaignWatcher : MonoBehaviour
    {
        [Serializable]
        public class RunResult
        {
            public int index;
            public string label = "";
            public int noiseSeed;
            public string[] names = new string[0];
            public float[] u = new float[0];
            public MissionAutorun.Result mission = new MissionAutorun.Result();
            public float maxSlipBrake;       // |true slip|, worst wheel, in the braking leg
            public float minTurnSpeed;
            public float minPackV;           // true pack terminal voltage, lowest seen
            public float navRmse, navMax;    // EKF position error vs truth, m, once initialised
            [NonSerialized] public double navSq;
            [NonSerialized] public int navN;
        }

        [Serializable]
        public class Gate
        {
            public float complete = 1.0f;    // fraction of runs that must finish
            public float total_mm = 50f;     // p95 |total_err|
            public float stop_mm = 10f;      // p95 |controller stop error|
            public float leg_mm = 60f;       // p95 |legA|, |legB|
            public float turn_deg = 1.0f;    // p95 |turn_err|
            public int safety = 0;           // runs with a safety fault allowed
            // On a route (NAV-06) these replace total / stop / leg / turn:
            public float xte_mm = 100f;      // p95 of each run's p95 cross-track error
            public float rstop_mm = 50f;     // p95 |stop along the route vs its end|
        }

        [Serializable]
        public class CampaignResult
        {
            public bool pass;
            public string vehicle = "", note = "", route = "";
            public int seed, planned, done;
            public float width;
            public Gate gate = new Gate();
            public string[] gateFails = new string[0];
            public List<RunResult> runs = new List<RunResult>();
        }

        private MissionAutorun.Request _req;
        private List<Campaign.RunSpec> _plan;
        private readonly CampaignResult _out = new CampaignResult();
        private int _k = -1;
        private bool _finished, _loading;
        private float _wait, _elapsed;

        private SimulationRunner _runner;
        private CarVehicle _car;
        private MissionScorer _score;
        private RunResult _cur;
        private RouteTable _route;      // NAV-06: every run follows it
        private Telemetry.CsvLogger _telemetry;   // -opusTelemetry 1: every channel, each tick, per run

        public void Configure(MissionAutorun.Request req, List<Campaign.RunSpec> plan)
        {
            _req = req;
            _plan = plan;
            _out.vehicle = req.vehicle;
            _out.route = req.route ?? "";
            _route = RouteTable.Load(req.route);
            _out.seed = req.campaignSeed;
            _out.width = req.campaignWidth;
            _out.planned = plan.Count;
            _out.gate = ParseGate(req.campaignGate);
            SceneManager.sceneLoaded += (s, m) => { _loading = false; _runner = null; _car = null; };
            // MissionAutorun.Boot put run 0's car into the session already.
            _k = 0;
            Begin(_plan[0]);
        }

        private void Begin(Campaign.RunSpec spec)
        {
            _cur = new RunResult { index = spec.index, label = spec.label, noiseSeed = spec.noiseSeed,
                                   names = spec.names, u = spec.u, minPackV = float.MaxValue };
            _elapsed = 0f;
            _wait = 0f;
            _score = null;
        }

        private void FixedUpdate()
        {
            MissionAutorun.PinFrameStep();
            if (_finished || _loading) return;
            if (_runner == null)
            {
                _wait += Time.fixedDeltaTime;
                _runner = FindFirstObjectByType<SimulationRunner>();
                _car = FindFirstObjectByType<CarVehicle>();
                if (_runner == null || _car == null)
                {
                    if (_wait > 10f) { EndRun(false, "no runner/car in scene"); }
                    return;
                }
                _score = new MissionScorer(_car, _runner.Hub, _route);
                MissionAutorun.Course?.Reset();
                _score.Course = MissionAutorun.Course;
                if (_req.saveTelemetry && _runner.Hub != null)
                {
                    _telemetry = new Telemetry.CsvLogger(_runner.Hub);
                    _telemetry.Begin("mission", null, "mission_telemetry");
                }
            }

            _elapsed += Time.fixedDeltaTime;
            int phase = _score.Step(_elapsed);
            MissionAutorun.Course?.Step(_car, MissionScorer.NoseM(), _elapsed);
            if (_runner.Hub != null && _runner.Hub.TryGetChannel("veh/batt_v", out var bv) && bv.Latest > 0f)
                _cur.minPackV = Mathf.Min(_cur.minPackV, bv.Latest);
            // The EKF's position against truth, in the UWB site frame (x = world
            // z, y = -world x, as FaultSuite's trace).
            var hub = _runner.Hub;
            if (hub != null && hub.TryGetChannel("dbg/nav_ok", out var nok) && nok.Latest > 0.5f &&
                hub.TryGetChannel("dbg/nav_x", out var nx) && hub.TryGetChannel("dbg/nav_y", out var ny))
            {
                var pos = _car.transform.position;
                float ex = nx.Latest - pos.z, ey = ny.Latest + pos.x;
                float e = Mathf.Sqrt(ex * ex + ey * ey);
                _cur.navSq += e * e;
                _cur.navN++;
                _cur.navMax = Mathf.Max(_cur.navMax, e);
            }
            if (phase == 7)   // braking proper; creep speeds make slip ratios meaningless
                for (int w = 0; w < 4; w++)
                    _cur.maxSlipBrake = Mathf.Max(_cur.maxSlipBrake, Mathf.Abs(_car.WheelSlipRatio(w)));
            if (phase == 10) EndRun(true, "");
            else if (phase == -1) EndRun(false, "controller reported FAULT");
            else if (_elapsed > _req.timeoutSec) EndRun(false, "timeout");
        }

        private void EndRun(bool ok, string note)
        {
            var res = _cur.mission;
            res.completed = ok;
            res.note = note;
            res.noiseSeed = Sensors.NoiseModel.GlobalSeed;
            res.controlHz = _runner != null ? _runner.controlRateHz : 0;
            res.elapsedSec = _elapsed;
            if (_score != null) _score.Fill(res); else res.phase = -99;
            res.true_delay_ms = GameFlow.ActiveDesign != null ? GameFlow.ActiveDesign.computeLatencyUs / 1000f : 0f;
            _cur.minTurnSpeed = res.turnSpeedMin;
            if (_cur.minPackV == float.MaxValue) _cur.minPackV = 0f;
            _cur.navRmse = _cur.navN > 0 ? (float)Math.Sqrt(_cur.navSq / _cur.navN) : -1f;
            _out.runs.Add(_cur);
            // -opusTelemetry 1: each run's coarse trace, <result>.<n>-<label>.trace.csv,
            // and every telemetry channel each control tick, <result>.<n>-<label>.telemetry.csv
            // (the single run's .telemetry.csv, per run).
            if (_req.saveTelemetry && _score != null && !string.IsNullOrEmpty(_req.resultPath))
            {
                try
                {
                    string tag = _k.ToString("000", CultureInfo.InvariantCulture) + "-" + _cur.label.Replace(' ', '_');
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, "." + tag + ".trace.csv"), _score.Trace);
                    if (_telemetry != null)
                    {
                        _telemetry.Flush();
                        File.Copy(_telemetry.TempPath, Path.ChangeExtension(_req.resultPath, "." + tag + ".telemetry.csv"), true);
                    }
                }
                catch (Exception e) { Debug.LogWarning($"[CAMPAIGN] trace write failed: {e.Message}"); }
            }
            if (_telemetry != null) { _telemetry.End(); _telemetry = null; }
            if (_route != null)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[CAMPAIGN] {0,3}/{1} {2,-22} {3} xte rms={4:0.0} p95={5:0.0} max={6:0.0} mm stop={7:+0.0;-0.0}/{8:+0.0;-0.0} mm head end={9:+0.00;-0.00} {10}",
                    _k + 1, _plan.Count, _cur.label, ok ? "done" : "FAIL", res.xte_rms_mm, res.xte_p95_mm,
                    res.xte_max_mm, res.route_stop_mm, res.route_stop_lat_mm, res.head_end_deg, note));
            else
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[CAMPAIGN] {0,3}/{1} {2,-22} {3} A={4:+0.0;-0.0} turn={5:+0.00;-0.00} B={6:+0.0;-0.0} total={7:+0.0;-0.0} stop={8:+0.0;-0.0} slip={9:0.00} {10}",
                    _k + 1, _plan.Count, _cur.label, ok ? "done" : "FAIL", res.legA_err_mm, res.turn_err_deg,
                    res.legB_err_mm, res.total_err_mm, res.controllerStopErr_mm, _cur.maxSlipBrake, note));

            _k++;
            if (_k >= _plan.Count) { Finish(""); return; }
            Next(_plan[_k]);
        }

        /// <summary>Put the next run's car into the session and reload the
        /// track scene — the same load the first run had.</summary>
        private void Next(Campaign.RunSpec spec)
        {
            var design = Campaign.DesignFor(_req.vehicle, spec, _req.campaignWidth);
            MissionAutorun.ApplyDesign(_req, design);
            SimulationRunner.NoiseSeedOverride = spec.noiseSeed;
            SimulationRunner.ReseedNextSession();
            Begin(spec);
            _loading = true;
            _runner = null;
            _car = null;
            SceneManager.LoadScene(GameFlow.TrackSceneName);
        }

        // ---------------------------------------------------------- report --

        private static Gate ParseGate(string s)
        {
            var g = new Gate();
            if (string.IsNullOrEmpty(s)) return g;
            foreach (var part in s.Split(','))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                float v = float.Parse(kv[1], CultureInfo.InvariantCulture);
                switch (kv[0].Trim())
                {
                    case "complete": g.complete = v; break;
                    case "total": g.total_mm = v; break;
                    case "stop": g.stop_mm = v; break;
                    case "leg": g.leg_mm = v; break;
                    case "turn": g.turn_deg = v; break;
                    case "safety": g.safety = (int)v; break;
                    case "xte": g.xte_mm = v; break;
                    case "rstop": g.rstop_mm = v; break;
                }
            }
            return g;
        }

        /// <summary>Nearest-rank percentile of |values| (p in 0..100).</summary>
        public static float Pct(IEnumerable<float> values, float p)
        {
            var a = values.Select(Mathf.Abs).OrderBy(x => x).ToArray();
            if (a.Length == 0) return float.NaN;
            int i = Mathf.Clamp(Mathf.CeilToInt(p / 100f * a.Length) - 1, 0, a.Length - 1);
            return a[i];
        }

        private void Finish(string note)
        {
            _finished = true;
            _out.note = note;
            _out.done = _out.runs.Count;
            var mc = _out.runs.Where(r => r.label.StartsWith("mc ")).ToList();
            var judged = mc.Count > 0 ? mc : _out.runs;   // corners alone: judge them
            var ok = judged.Where(r => r.mission.completed).ToList();
            var fails = new List<string>();
            float frac = judged.Count > 0 ? ok.Count / (float)judged.Count : 0f;
            var g = _out.gate;
            if (frac < g.complete) fails.Add($"completed {ok.Count}/{judged.Count} < {g.complete:P0}");
            int safety = judged.Count(r => r.mission.safeFault != 0);
            if (safety > g.safety) fails.Add($"{safety} runs with a safety fault > {g.safety}");
            if (ok.Count > 0 && _route != null)
            {
                float x95 = Pct(ok.Select(r => r.mission.xte_p95_mm), 95f);
                float r95 = Pct(ok.Select(r => r.mission.route_stop_mm), 95f);
                if (x95 > g.xte_mm) fails.Add($"p95 of p95 cross-track {x95:0.0} mm > {g.xte_mm}");
                if (r95 > g.rstop_mm) fails.Add($"p95 |route stop| {r95:0.0} mm > {g.rstop_mm}");
            }
            else if (ok.Count > 0)
            {
                float t95 = Pct(ok.Select(r => r.mission.total_err_mm), 95f);
                float s95 = Pct(ok.Select(r => r.mission.controllerStopErr_mm), 95f);
                float l95 = Mathf.Max(Pct(ok.Select(r => r.mission.legA_err_mm), 95f),
                                      Pct(ok.Select(r => r.mission.legB_err_mm), 95f));
                float h95 = Pct(ok.Select(r => r.mission.turn_err_deg), 95f);
                if (t95 > g.total_mm) fails.Add($"p95 |total| {t95:0.0} mm > {g.total_mm}");
                if (s95 > g.stop_mm) fails.Add($"p95 |stop| {s95:0.0} mm > {g.stop_mm}");
                if (l95 > g.leg_mm) fails.Add($"p95 |leg| {l95:0.0} mm > {g.leg_mm}");
                if (h95 > g.turn_deg) fails.Add($"p95 |turn| {h95:0.00} deg > {g.turn_deg}");
            }
            _out.gateFails = fails.ToArray();
            _out.pass = note.Length == 0 && fails.Count == 0;

            string report = Report(judged);
            Debug.Log("[CAMPAIGN] RESULT " + (_out.pass ? "PASS" : "FAIL") +
                      $" — {ok.Count}/{judged.Count} completed" + (fails.Count > 0 ? "; " + string.Join("; ", fails) : ""));
            try
            {
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_req.resultPath));
                    File.WriteAllText(_req.resultPath, JsonUtility.ToJson(_out, true));
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".csv"), Csv());
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".txt"), report);
                }
            }
            catch (Exception e) { Debug.LogError($"[CAMPAIGN] write failed: {e.Message}"); }
#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(_out.pass ? 0 : 1);
            else UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(_out.pass ? 0 : 1);
#endif
        }

        private static readonly string[] Metrics =
            { "completed", "legA_err_mm", "turn_err_deg", "legB_err_mm", "brake_err_mm", "total_err_mm",
              "stop_err_mm", "drift_mm", "max_slip_brake", "turn_speed_min", "min_pack_v", "nav_rmse_m", "nav_max_m",
              "xte_rms_mm", "xte_p95_mm", "xte_max_mm", "head_end_deg", "v_err_rms", "lat_acc_max", "lap_s",
              "route_stop_mm", "route_stop_lat_mm", "cmd_delay_ms", "true_delay_ms", "probe_mu", "stop_early_mm",
              "obstacle_gap_mm", "obstacle_removed", "clear_min_mm",
              "mission_fault", "safe_fault", "elapsed_s", "note" };

        private string Csv()
        {
            var sb = new StringBuilder();
            var names = _out.runs.Count > 0 ? _out.runs[0].names : new string[0];
            sb.Append("index,label,noise_seed");
            foreach (var n in names) sb.Append(",u_").Append(n);
            foreach (var n in names) sb.Append(",").Append(n);
            foreach (var m in Metrics) sb.Append(",").Append(m);
            sb.Append('\n');
            var knobs = Campaign.Describe(_out.width);
            foreach (var r in _out.runs)
            {
                var c = r.mission;
                sb.Append(r.index).Append(',').Append(r.label).Append(',').Append(r.noiseSeed);
                foreach (var u in r.u) sb.Append(',').Append(u.ToString("0.0000", CultureInfo.InvariantCulture));
                for (int j = 0; j < r.names.Length; j++)
                    sb.Append(',').Append(knobs.TryGetValue(r.names[j], out var f)
                        ? f(r.u[j]).ToString("0.######", CultureInfo.InvariantCulture) : "");
                sb.Append(',').Append(c.completed ? 1 : 0);
                foreach (float v in new[] { c.legA_err_mm, c.turn_err_deg, c.legB_err_mm, c.brake_err_mm, c.total_err_mm,
                                            c.controllerStopErr_mm, c.drift_mm, r.maxSlipBrake, r.minTurnSpeed, r.minPackV, r.navRmse, r.navMax,
                                            c.xte_rms_mm, c.xte_p95_mm, c.xte_max_mm, c.head_end_deg, c.v_err_rms, c.lat_acc_max, c.lap_s,
                                            c.route_stop_mm, c.route_stop_lat_mm, c.cmd_delay_ms, c.true_delay_ms, c.probe_mu, c.stop_early_mm,
                                            c.obstacle_gap_mm, c.obstacle_removed, c.clear_min_mm })
                    sb.Append(',').Append(v.ToString("0.###", CultureInfo.InvariantCulture));
                sb.Append(',').Append(c.fault).Append(',').Append(c.safeFault).Append(',').Append(c.elapsedSec.ToString("0.00", CultureInfo.InvariantCulture));
                sb.Append(',').Append((c.note ?? "").Replace(',', ';')).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Which draws go with failing and with a long stop: Pearson
        /// correlation of each draw (and, per wheel, the front left-right
        /// difference) with failure (0/1) and with |total| over finished runs.
        /// Only meaningful for Monte-Carlo runs, and only roughly below ~50.</summary>
        private static void Sensitivity(StringBuilder sb, List<RunResult> runs)
        {
            runs = runs.Where(r => r.label.StartsWith("mc ")).ToList();
            if (runs.Count < 8) return;
            var names = runs[0].names;
            var feats = new List<(string name, Func<RunResult, float> f)>();
            for (int j = 0; j < names.Length; j++) { int jj = j; feats.Add((names[j], r => r.u[jj])); }
            for (int j = 0; j + 1 < names.Length; j++)
                if (names[j].EndsWith("_0") && names[j + 1].EndsWith("_1"))
                {
                    int jj = j;
                    feats.Add((names[j].Substring(0, names[j].Length - 2) + "_front_l-r", r => r.u[jj] - r.u[jj + 1]));
                }
            float Corr(IList<float> x, IList<float> y)
            {
                int n = x.Count;
                if (n < 3) return 0f;
                double mx = x.Average(), my = y.Average(), sxy = 0, sxx = 0, syy = 0;
                for (int i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); syy += (y[i] - my) * (y[i] - my); }
                return sxx > 0 && syy > 0 ? (float)(sxy / Math.Sqrt(sxx * syy)) : 0f;
            }
            var fail = runs.Select(r => r.mission.completed ? 0f : 1f).ToList();
            var ok = runs.Where(r => r.mission.completed).ToList();
            var tot = ok.Select(r => Mathf.Abs(r.mission.total_err_mm)).ToList();
            var rows = feats.Select(f => (f.name,
                                          cf: Corr(runs.Select(f.f).ToList(), fail),
                                          ct: Corr(ok.Select(f.f).ToList(), tot)))
                            .OrderByDescending(t => Mathf.Max(Mathf.Abs(t.cf), Mathf.Abs(t.ct))).Take(10);
            var ic = CultureInfo.InvariantCulture;
            sb.AppendLine("Sensitivity (correlation of the draw with failing / with |total| when finished):");
            foreach (var t in rows)
                sb.AppendLine(string.Format(ic, "  {0,-24} fail {1,6:+0.00;-0.00}   |total| {2,6:+0.00;-0.00}", t.name, t.cf, t.ct));
            sb.AppendLine();
        }

        private string Report(List<RunResult> judged)
        {
            var sb = new StringBuilder();
            var ic = CultureInfo.InvariantCulture;
            sb.AppendLine($"Opus mission campaign — {_out.vehicle}" + (_route != null ? $", route {_out.route}" : "") +
                          $", seed {_out.seed}, width {_out.width.ToString("0.##", ic)}, {_out.runs.Count} runs");
            sb.AppendLine();
            var ok = judged.Where(r => r.mission.completed).ToList();
            sb.AppendLine($"completed {ok.Count}/{judged.Count}, safety faults {judged.Count(r => r.mission.safeFault != 0)}");
            sb.AppendLine();
            sb.AppendLine(string.Format(ic, "{0,-16}{1,10}{2,10}{3,10}{4,10}", "|metric|", "p50", "p95", "max", "gate"));
            void Row(string name, Func<RunResult, float> f, string gate, Func<RunResult, bool> has = null)
            {
                var v = ok.Where(r => has == null || has(r)).Select(f).ToList();
                if (v.Count == 0) return;
                sb.AppendLine(string.Format(ic, "{0,-16}{1,10:0.0}{2,10:0.0}{3,10:0.0}{4,10}", name,
                    Pct(v, 50f), Pct(v, 95f), Pct(v, 100f), gate));
            }
            var g = _out.gate;
            if (_route != null)
            {
                Row("xte rms mm", r => r.mission.xte_rms_mm, "");
                Row("xte p95 mm", r => r.mission.xte_p95_mm, g.xte_mm.ToString(ic));
                Row("xte max mm", r => r.mission.xte_max_mm, "");
                Row("stop along mm", r => r.mission.route_stop_mm, g.rstop_mm.ToString(ic));
                Row("stop across mm", r => r.mission.route_stop_lat_mm, "");
                Row("head end deg", r => r.mission.head_end_deg, "");
                Row("v err rms m/s", r => r.mission.v_err_rms, "");
                Row("lat acc max", r => r.mission.lat_acc_max, "");
                Row("lap s", r => r.mission.lap_s, "");
                Row("EKF rmse cm", r => r.navRmse * 100f, "", r => r.navRmse >= 0f);
                Row("EKF max cm", r => r.navMax * 100f, "", r => r.navRmse >= 0f);
            }
            else {
            Row("leg A mm", r => r.mission.legA_err_mm, g.leg_mm.ToString(ic));
            Row("turn deg", r => r.mission.turn_err_deg, g.turn_deg.ToString(ic));
            Row("leg B mm", r => r.mission.legB_err_mm, g.leg_mm.ToString(ic));
            Row("brake mm", r => r.mission.brake_err_mm, "");
            Row("total mm", r => r.mission.total_err_mm, g.total_mm.ToString(ic));
            Row("stop err mm", r => r.mission.controllerStopErr_mm, g.stop_mm.ToString(ic));
            Row("odo drift mm", r => r.mission.drift_mm, "");
            Row("brake slip", r => r.maxSlipBrake, "");
            Row("EKF rmse cm", r => r.navRmse * 100f, "", r => r.navRmse >= 0f);
            Row("EKF max cm", r => r.navMax * 100f, "", r => r.navRmse >= 0f);
            }
            // FW-17: what the firmware predicted over against what the car had
            Row("delay err ms", r => r.mission.cmd_delay_ms - r.mission.true_delay_ms, "");
            sb.AppendLine();
            sb.AppendLine(_out.gateFails.Length == 0 ? "GATE: PASS" : "GATE: FAIL — " + string.Join("; ", _out.gateFails));
            sb.AppendLine();
            if (_route != null)
            {
                sb.AppendLine("Worst runs by p95 cross-track (failures first):");
                foreach (var r in _out.runs.OrderBy(r => r.mission.completed ? 1 : 0)
                                           .ThenByDescending(r => r.mission.xte_p95_mm).Take(8))
                    sb.AppendLine(string.Format(ic, "  {0,-28} {1,-5} xte p95 {2,6:0.0} max {3,6:0.0} stop {4,7:0.0} head {5,6:0.00} {6}",
                        r.label, r.mission.completed ? "done" : "FAIL", r.mission.xte_p95_mm, r.mission.xte_max_mm,
                        r.mission.route_stop_mm, r.mission.head_end_deg,
                        r.mission.completed ? "" : $"{r.mission.note} (mission 0x{r.mission.fault:X4}, safety 0x{r.mission.safeFault:X4})"));
            }
            else
            {
            sb.AppendLine("Worst runs by |total| (failures first):");
            foreach (var r in _out.runs.OrderBy(r => r.mission.completed ? 1 : 0)
                                       .ThenByDescending(r => Mathf.Abs(r.mission.total_err_mm)).Take(8))
                sb.AppendLine(string.Format(ic, "  {0,-28} {1,-5} total {2,8:0.0} stop {3,7:0.0} turn {4,6:0.00} slip {5:0.00} {6}",
                    r.label, r.mission.completed ? "done" : "FAIL", r.mission.total_err_mm,
                    r.mission.controllerStopErr_mm, r.mission.turn_err_deg, r.maxSlipBrake,
                    r.mission.completed ? "" : $"{r.mission.note} (mission 0x{r.mission.fault:X4}, safety 0x{r.mission.safeFault:X4})"));
            }
            sb.AppendLine();
            Sensitivity(sb, judged);
            sb.AppendLine("Parameters (uniform within the range):");
            foreach (var k in Campaign.Knobs())
                sb.AppendLine($"  {k.name,-18}{(k.perWheel ? " per wheel" : "          ")}  {k.Range}");
            sb.AppendLine();
            sb.AppendLine("Not sampled yet: " + Campaign.NotYet);
            return sb.ToString();
        }
    }
}
