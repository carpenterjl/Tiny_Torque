using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>Classic step-response metrics between a setpoint channel and a
    /// measured channel, computed over the telemetry ring buffers.</summary>
    public struct StepMetricsResult
    {
        public bool found;           // a clean step was located
        public float stepTime;       // sim time of the step edge
        public float initial;        // measured value just before the step
        public float target;         // setpoint value after the step
        public float riseTime;       // 10→90 % of the step delta (s); -1 if not reached
        public float overshootPct;   // (peak − target)/delta · 100, floored at 0
        public float peakTime;       // time of peak relative to the step (s)
        public float settlingTime;   // last exit from the ±5 % band (s); -1 = never settled
        public float ssError;        // mean of the final 20 % of the window − target
    }

    /// <summary>
    /// Post-processor for controller validation: finds the most recent setpoint
    /// step (a jump ≥ 10 % of the channel's span, preceded by ≥ 0.5 s of flat
    /// setpoint) and measures the closed-loop response — rise time, overshoot,
    /// settling time, steady-state error. Pure function over the hub's rings;
    /// used by the in-game overlay (J) and stamped into the CSV run sidecar.
    /// </summary>
    public static class StepMetrics
    {
        public static StepMetricsResult Compute(TelemetryHub hub, string spChannel, string measChannel)
        {
            var r = new StepMetricsResult { riseTime = -1f, settlingTime = -1f };
            if (hub == null ||
                !hub.TryGetChannel(spChannel, out var spCh) ||
                !hub.TryGetChannel(measChannel, out var msCh))
                return r;

            int n = Mathf.Min(Mathf.Min(spCh.Count, msCh.Count), hub.SampleCount);
            if (n < 32) return r;

            int cap = spCh.Ring.Length;
            // Oldest-first accessors into the rings.
            float Sp(int i) => spCh.Ring[(spCh.Head - n + i + cap) % cap];
            float Ms(int i) => msCh.Ring[(msCh.Head - n + i + cap) % cap];
            float Tm(int i) => hub.TimeRing[(hub.TimeHead - n + i + cap) % cap];

            // Channel span → step threshold (10 %).
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < n; i++) { float v = Sp(i); if (v < lo) lo = v; if (v > hi) hi = v; }
            float span = hi - lo;
            if (span < 1e-4f) return r;
            float thresh = span * 0.10f;

            // Last step edge preceded by ≥ 0.5 s of flat setpoint.
            int edge = -1;
            for (int i = n - 1; i >= 1; i--)
            {
                if (Mathf.Abs(Sp(i) - Sp(i - 1)) < thresh) continue;
                float before = Sp(i - 1);
                float tEdge = Tm(i);
                bool flat = true;
                for (int j = i - 1; j >= 0 && tEdge - Tm(j) <= 0.5f; j--)
                    if (Mathf.Abs(Sp(j) - before) > thresh * 0.2f) { flat = false; break; }
                if (flat) { edge = i; break; }
            }
            if (edge < 1 || edge > n - 8) return r;   // need some response window

            r.found = true;
            r.stepTime = Tm(edge);
            r.initial = Ms(edge - 1);
            r.target = Sp(edge);
            float delta = r.target - r.initial;
            if (Mathf.Abs(delta) < 1e-4f) { r.found = false; return r; }
            float sign = Mathf.Sign(delta);

            // Rise time (10→90 %), peak/overshoot, settling (±5 % band), SS error.
            float t10 = -1f, t90 = -1f;
            float peak = r.initial; float peakT = r.stepTime;
            int lastOut = -1;
            for (int i = edge; i < n; i++)
            {
                float prog = (Ms(i) - r.initial) * sign;   // progress toward target
                if (t10 < 0f && prog >= 0.1f * Mathf.Abs(delta)) t10 = Tm(i);
                if (t90 < 0f && prog >= 0.9f * Mathf.Abs(delta)) t90 = Tm(i);
                if ((Ms(i) - peak) * sign > 0f) { peak = Ms(i); peakT = Tm(i); }
                if (Mathf.Abs(Ms(i) - r.target) > 0.05f * Mathf.Abs(delta)) lastOut = i;
            }
            if (t10 >= 0f && t90 >= 0f) r.riseTime = t90 - t10;
            r.overshootPct = Mathf.Max(0f, (peak - r.target) * sign / Mathf.Abs(delta) * 100f);
            r.peakTime = peakT - r.stepTime;
            r.settlingTime = lastOut < 0 ? 0f
                : (lastOut >= n - 1 ? -1f : Tm(lastOut + 1) - r.stepTime);

            int tail = Mathf.Max(1, (n - edge) / 5);   // final 20 % of the window
            float sum = 0f;
            for (int i = n - tail; i < n; i++) sum += Ms(i);
            r.ssError = sum / tail - r.target;
            return r;
        }
    }
}
