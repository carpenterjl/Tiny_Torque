using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// In-game step-response metrics readout (toggle with J). Cycles through
    /// preset setpoint↔measured channel pairs and shows rise time, overshoot,
    /// settling time, and steady-state error for the most recent setpoint step —
    /// the live half of the controller-validation loop (the same numbers are
    /// stamped into the CSV sidecar on save).
    /// </summary>
    public sealed class MetricsOverlay : MonoBehaviour
    {
        public TelemetryHub Hub;

        private static readonly (string sp, string meas, string label)[] Pairs =
        {
            ("sp/linear", "veh/speed", "sp/linear → veh/speed"),
            ("dbg/target_speed", "veh/speed", "dbg/target_speed → veh/speed"),
            ("cmd/steer_deg", "veh/steer_deg", "cmd/steer_deg → veh/steer_deg"),
        };

        private bool _visible;
        private int _pair;
        private float _nextCompute;
        private StepMetricsResult _last;

        private void Update()
        {
            if (InputReader.MetricsTogglePressed()) _visible = !_visible;
            if (!_visible || Hub == null || Time.unscaledTime < _nextCompute) return;
            _nextCompute = Time.unscaledTime + 0.5f;   // recompute 2×/s
            _last = StepMetrics.Compute(Hub, Pairs[_pair].sp, Pairs[_pair].meas);
        }

        private void OnGUI()
        {
            if (!_visible) return;
            var rect = new Rect(8f, 8f, 250f, 148f);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("STEP METRICS [J]", GUI.skin.label);
            if (GUILayout.Button("▸", GUILayout.Width(24)))
            {
                _pair = (_pair + 1) % Pairs.Length;
                _nextCompute = 0f;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Pairs[_pair].label);
            if (!_last.found)
            {
                GUILayout.Label("no step found —\nstep the setpoint and hold it");
            }
            else
            {
                GUILayout.Label($"step {_last.initial:0.00} → {_last.target:0.00} @ t={_last.stepTime:0.0}s");
                GUILayout.Label($"rise (10-90%): {(_last.riseTime >= 0f ? _last.riseTime.ToString("0.000") + " s" : "—")}");
                GUILayout.Label($"overshoot: {_last.overshootPct:0.0} %  (peak @ {_last.peakTime:0.00} s)");
                GUILayout.Label($"settling ±5%: {(_last.settlingTime >= 0f ? _last.settlingTime.ToString("0.00") + " s" : "not settled")}");
                GUILayout.Label($"ss error: {_last.ssError:+0.000;-0.000}");
            }
            GUILayout.EndArea();
        }
    }
}
