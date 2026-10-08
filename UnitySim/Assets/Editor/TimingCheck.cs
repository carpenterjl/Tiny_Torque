using System.Collections.Generic;
using System.Text;
using AIHWSim.Core;
using AIHWSim.Sensors;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[TIMING] — Phase 3's timing checks (TIM-02/03/04).</b> No scene; a
    /// bare <see cref="MagSensor"/> stands in for any sensor, with its heading
    /// set from the sim clock so every reading carries the time it was taken:
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.TimingCheck.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <list type="bullet">
    /// <item>A sensor's own rate lands on the physics grid, not the control
    /// grid: 15 Hz at 400 Hz physics keeps its mean period, and no sample is
    /// later than one physics step after its instant.</item>
    /// <item>Phase offsets and jitter move the sample instants by the stated
    /// amount and σ, deterministically per seed, never reordering samples.</item>
    /// <item>A latency is resolved to the physics step: 3 ms at a 100 Hz
    /// control rate reads a value exactly 3 ms old.</item>
    /// <item>The legacy settings are unchanged: no rate/latency = a fresh
    /// sample per control tick; the Opus ToF (50 Hz, 20 ms) still reads values
    /// 20 or 30 ms old, taken on the 20 ms grid.</item>
    /// <item>The compute-latency queue applies each command on its physics
    /// step, in order.</item>
    /// </list>
    /// </summary>
    public static class TimingCheck
    {
        private const string Tag = "[TIMING]";
        private const double DegPerUs = 0.05 / 1000.0;   // heading encodes time: 0.05°/ms
        private static int _checks, _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [TIMING] Sensor Timing Check", priority = 124)]
        public static void RunFromMenu() => Run(false);

        public static void Report() => Run(true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0; _failed = 0; _log = new StringBuilder();
            int savedSeed = NoiseModel.GlobalSeed;
            NoiseModel.GlobalSeed = 4242;
            try
            {
                RateLandsOnPhysicsGrid();
                PhaseOffset();
                JitterStatistics();
                LatencyResolvedToPhysicsStep();
                LegacyPathsUnchanged();
                CommandLatchOrder();
            }
            finally { NoiseModel.GlobalSeed = savedSeed; }

            Debug.Log(_log.ToString().TrimEnd());
            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);
            if (exitWhenDone && Application.isBatchMode) EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- the schedule alone ---------------------------------------------

        private static List<long> RunSchedule(SampleSchedule s, long physUs, long untilUs)
        {
            var times = new List<long>();
            for (long t = 0; t <= untilUs; t += physUs)
                if (s.Due(t, 0)) times.Add(t);
            return times;
        }

        private static void RateLandsOnPhysicsGrid()
        {
            const long phys = 2500;                         // 400 Hz
            double period = 1e6 / 15.0;
            var t = RunSchedule(new SampleSchedule(period, 0, 0f, 1), phys, 60_000_000);
            bool onGrid = true, prompt = true;
            for (int k = 0; k < t.Count; k++)
            {
                if (t[k] % phys != 0) onGrid = false;
                double late = t[k] - System.Math.Round(k * period);   // instants are whole µs
                if (late < 0 || late >= phys) prompt = false;
            }
            double mean = (t[t.Count - 1] - t[0]) / (double)(t.Count - 1);
            Check($"15 Hz at 400 Hz physics: {t.Count} samples in 60 s (want 901)", t.Count == 901);
            Check("every sample is on the physics grid", onGrid);
            Check("no sample is a whole physics step late", prompt);
            Check($"mean period {mean:0.0} us vs {period:0.0} us",
                  System.Math.Abs(mean / period - 1.0) < 1e-3);
        }

        private static void PhaseOffset()
        {
            const long phys = 500;                          // 2 kHz
            var t = RunSchedule(new SampleSchedule(20000, 7000, 0f, 1), phys, 1_000_000);
            bool exact = true;
            for (int k = 0; k < t.Count; k++) if (t[k] != 7000 + 20000L * k) exact = false;
            Check($"50 Hz with a 7 ms phase samples at 7, 27, 47 ms... ({t.Count} samples)",
                  exact && t.Count == 50);
            var w = RunSchedule(new SampleSchedule(10000, 27000, 0f, 1), phys, 100_000);
            Check("a phase longer than the period wraps (27 ms at 100 Hz = 7 ms)",
                  w.Count > 0 && w[0] == 7000);
        }

        private static void JitterStatistics()
        {
            const long phys = 50;                           // 20 kHz resolves the jitter
            const double period = 10000, sigma = 300;
            var a = RunSchedule(new SampleSchedule(period, 0, (float)sigma, 7), phys, 200_000_000);
            var b = RunSchedule(new SampleSchedule(period, 0, (float)sigma, 7), phys, 2_000_000);
            var c = RunSchedule(new SampleSchedule(period, 0, (float)sigma, 8), phys, 2_000_000);
            double sum = 0, sumSq = 0;
            bool ordered = true;
            for (int k = 0; k < a.Count; k++)
            {
                double d = a[k] - k * period;
                sum += d; sumSq += d * d;
                if (k > 0 && a[k] <= a[k - 1]) ordered = false;
            }
            double mean = sum / a.Count;
            double sd = System.Math.Sqrt(sumSq / a.Count - mean * mean);
            Check($"jitter sigma {sd:0.0} us vs {sigma:0} us", System.Math.Abs(sd / sigma - 1.0) < 0.05);
            // Sampling waits for the next 50 us step: +25 us on average.
            Check($"jitter mean {mean:+0.0;-0.0} us (half a physics step expected)",
                  System.Math.Abs(mean - phys / 2.0) < 10);
            Check("jittered samples never reorder", ordered && a.Count == 20001);
            bool same = b.Count == c.Count && b.Count > 0;
            bool equal = true, differs = false;
            for (int k = 0; k < b.Count && k < c.Count; k++) differs |= b[k] != c[k];
            var b2 = RunSchedule(new SampleSchedule(period, 0, (float)sigma, 7), phys, 2_000_000);
            for (int k = 0; k < b.Count; k++) equal &= b[k] == b2[k];
            Check("same seed, same jitter; another seed, another jitter", same && equal && differs);
        }

        // ---- a sensor through the rig's two entry points -------------------

        private sealed class Harness
        {
            public readonly MagSensor S;
            private readonly GameObject _go;
            public Harness(float rateHz, float latencyMs, float phaseMs = 0f, float jitterUs = 0f)
            {
                _go = new GameObject("timing_probe") { hideFlags = HideFlags.HideAndDontSave };
                S = _go.AddComponent<MagSensor>();
                S.sensorName = "probe";
                S.updateRateHz = rateHz;
                S.latencyMs = latencyMs;
                S.phaseOffsetMs = phaseMs;
                S.jitterUs = jitterUs;
                S.ResetSampling();
            }
            public void SetClock(long tUs) =>
                _go.transform.rotation = Quaternion.Euler(0f, (float)(tUs * DegPerUs), 0f);
            public void Destroy() => Object.DestroyImmediate(_go);
        }

        private struct Read { public long T, Stamp; public uint Seq; public double Encoded; }

        /// <summary>The runner's loop in miniature: every physics step ticks
        /// the sensor clock; every control tick reads what firmware would.</summary>
        private static List<Read> Drive(Harness h, int physHz, int decimation, long untilUs)
        {
            var reads = new List<Read>();
            long phys = 1_000_000L / physHz, ctrl = phys * decimation;
            var buf = new float[1];
            for (long n = 0; n * phys <= untilUs; n++)
            {
                long t = n * phys;
                h.SetClock(t);
                long anchor = t - t % ctrl;
                h.S.PhysicsTick(t, anchor, ctrl, phys);
                if (t % ctrl == 0)
                {
                    h.S.Output(ctrl * 1e-6f, t, buf, 0);
                    reads.Add(new Read { T = t, Stamp = h.S.StampUs, Seq = h.S.StampSeq,
                                         Encoded = buf[0] / DegPerUs });
                }
            }
            return reads;
        }

        private static bool ValueMatchesStamp(List<Read> r)
        {
            foreach (var x in r) if (System.Math.Abs(x.Encoded - x.Stamp) > 5.0) return false;
            return true;
        }

        private static void LatencyResolvedToPhysicsStep()
        {
            var h = new Harness(0f, 3f);
            var r = Drive(h, 2000, 20, 2_000_000);       // 2 kHz physics, 100 Hz control
            h.Destroy();
            bool age = true, fresh = true;
            for (int i = 1; i < r.Count; i++)
            {
                if (r[i].T - r[i].Stamp != 3000) age = false;
                if (r[i].Seq != r[i - 1].Seq + 1) fresh = false;
            }
            Check("no rate, 3 ms latency: every control tick reads a value exactly 3 ms old", age);
            Check("... one fresh sample per control period (seq +1 per tick)", fresh);
            Check("... and the value is the one taken at the stamp", ValueMatchesStamp(r));

            h = new Harness(15f, 12.5f, 0f, 2000f);
            r = Drive(h, 400, 4, 5_000_000);
            h.Destroy();
            bool grid = true, late = true;
            for (int i = 2; i < r.Count; i++)
            {
                if (r[i].Stamp % 2500 != 0) grid = false;
                if (r[i].T - r[i].Stamp < 12500) late = false;
            }
            Check("15 Hz, 12.5 ms latency, 2 ms jitter: stamps on the physics grid, never younger than the latency",
                  grid && late);
            Check("... values match their stamps", ValueMatchesStamp(r));
        }

        private static void LegacyPathsUnchanged()
        {
            var h = new Harness(0f, 0f);
            Check("no rate/latency/phase/jitter = no physics clock (legacy fast path)", !h.S.OnPhysicsClock);
            var r = Drive(h, 400, 4, 1_000_000);
            h.Destroy();
            bool fresh = true;
            for (int i = 0; i < r.Count; i++)
                if (r[i].Stamp != r[i].T || (i > 0 && r[i].Seq != r[i - 1].Seq + 1)) fresh = false;
            Check("... a fresh sample stamped with the control tick, every tick", fresh && ValueMatchesStamp(r));

            // The Opus ToF: 50 Hz, 20 ms, 400 Hz physics, 100 Hz control.
            h = new Harness(50f, 20f);
            r = Drive(h, 400, 4, 2_000_000);
            h.Destroy();
            bool grid = true, ages = true;
            for (int i = 3; i < r.Count; i++)
            {
                if (r[i].Stamp % 20000 != 0) grid = false;
                long a = r[i].T - r[i].Stamp;
                if (a != 20000 && a != 30000) ages = false;
            }
            Check("Opus ToF (50 Hz, 20 ms): samples on the 20 ms grid, read 20 or 30 ms old", grid && ages);
        }

        private static void CommandLatchOrder()
        {
            var q = new CommandLatch();
            var cmd = new float[8];
            for (int k = 0; k < 5; k++) { cmd[0] = k; q.Push(cmd, 10 + 4 * k); }
            var got = new List<float>();
            long firstAt = -1;
            for (long tick = 0; tick < 40; tick++)
            {
                float[] c;
                while ((c = q.PopDue(tick)) != null)
                {
                    if (firstAt < 0) firstAt = tick;
                    got.Add(c[0]);
                }
            }
            Check("compute-latency queue: first command applied on its tick (10)", firstAt == 10);
            Check("... all five in order", got.Count == 5 && got[0] == 0 && got[4] == 4);
            for (int k = 0; k < CommandLatch.Capacity + 3; k++) { cmd[0] = k; q.Push(cmd, k); }
            var head = q.PopDue(long.MaxValue);
            Check("... an overfull queue drops the oldest, keeps the capacity",
                  q.Count == CommandLatch.Capacity - 1 && head != null && head[0] == 3);
        }

        private static void Check(string what, bool ok)
        {
            _checks++;
            if (!ok) _failed++;
            _log.AppendLine($"{Tag} {(ok ? "ok  " : "FAIL")} {what}");
        }
    }
}
