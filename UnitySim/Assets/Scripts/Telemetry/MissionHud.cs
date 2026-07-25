using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// Status readout for a firmware-driven mission run. Answers the two questions
    /// you actually have while watching an autonomous car: <em>is the controller
    /// connected and armed?</em> and <em>did it go where it said it went?</em>
    ///
    /// It reads only telemetry channels, never the controller directly, so it works
    /// for any firmware that publishes the mission debug-channel set (see
    /// <c>Controllers/opus_mission</c>). When those channels are absent — any other
    /// car, or no DLL at all — it collapses to a single connection line, so it is
    /// harmless to attach unconditionally.
    ///
    /// The distance comparison is the point of the panel: <c>dbg/odo_m</c> is what
    /// the controller *believes* it travelled, from wheel encoders; the ground-truth
    /// column is measured from <c>veh/pos_x</c>/<c>veh/pos_z</c>. Agreement between
    /// them proves the control loop closed. Agreement with the *target* proves the
    /// odometry calibration is right. Those are different claims and the panel keeps
    /// them visibly separate.
    /// </summary>
    public sealed class MissionHud : MonoBehaviour
    {
        public TelemetryHub Hub;
        public SimulationRunner runner;

        private bool _visible = true;
        private GUIStyle _box, _label;

        // Ground-truth path length, integrated from the published pose. The
        // controller cannot see this; it exists purely to score the controller.
        private Vector2 _prevPos;
        private bool _hasPrev;
        private float _truePath;
        private float _truePathAtArm;
        private bool _armed;

        private static readonly string[] PhaseNames =
        {
            "BOOT", "SELF-CHECK", "ARMED", "LAUNCH", "CRUISE 14.5 m",
            "TURN 45°", "CRUISE 7.5 m", "BRAKE 1.5 m", "CREEP", "HOLD", "DONE",
        };

        private void Update()
        {
            // K toggles the panel (G is the graph overlay, J the metrics box).
            if (InputReader.MissionTogglePressed()) _visible = !_visible;

            // Integrate ground-truth path length at frame rate. Sampling the
            // committed telemetry (rather than the transform) keeps this aligned
            // with what the CSV records, so the HUD and the log always agree.
            if (Hub == null) return;
            if (!TryGet("veh/pos_x", out float x) || !TryGet("veh/pos_z", out float z)) return;
            var p = new Vector2(x, z);
            if (_hasPrev) _truePath += Vector2.Distance(_prevPos, p);
            _prevPos = p;
            _hasPrev = true;

            // Latch the datum the moment the controller reports it is armed, so
            // the ground-truth distance and the controller's odometer start from
            // the same instant rather than from scene load.
            int state = TryGet("dbg/state", out float s) ? Mathf.RoundToInt(s) : -99;
            if (!_armed && state == 2) { _armed = true; _truePathAtArm = _truePath; }
            if (state == 0 || state == 1) { _armed = false; _truePathAtArm = _truePath; }
        }

        private bool TryGet(string channel, out float value)
        {
            value = 0f;
            if (Hub == null || !Hub.TryGetChannel(channel, out var ch)) return false;
            value = ch.Latest;
            return true;
        }

        private void OnGUI()
        {
            if (!_visible || Hub == null) return;

            _box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(10, 10, 8, 8) };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };

            bool hasMission = Hub.TryGetChannel("dbg/state", out _);
            var rect = new Rect(12f, Screen.height - (hasMission ? 190f : 54f) - 12f,
                                288f, hasMission ? 190f : 54f);

            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("<b>MISSION</b>", _label);

            bool loaded = runner != null && runner.ControllerReady;
            GUILayout.Label(loaded
                ? "controller: <color=#7CFC7C>CONNECTED</color>"
                : "controller: <color=#FF7C7C>NOT LOADED</color>", _label);

            if (!hasMission)
            {
                GUILayout.EndArea();
                return;
            }

            int state = TryGet("dbg/state", out float sv) ? Mathf.RoundToInt(sv) : 0;
            int fault = TryGet("dbg/fault", out float fv) ? Mathf.RoundToInt(fv) : 0;

            string phase = state < 0 ? "<color=#FF7C7C>FAULT</color>"
                : state < PhaseNames.Length
                    ? (state == 2 ? $"<color=#7CFC7C>{PhaseNames[state]}</color>" : PhaseNames[state])
                    : state.ToString();
            GUILayout.Label($"phase: {phase}", _label);
            if (fault != 0)
                GUILayout.Label($"<color=#FF7C7C>fault bits: 0x{fault:X4}</color>", _label);

            TryGet("dbg/odo_m", out float odo);
            TryGet("dbg/leg_rem_m", out float rem);
            TryGet("veh/speed", out float spd);
            float truth = _truePath - _truePathAtArm;

            GUILayout.Label($"speed:     {spd,7:0.00} m/s", _label);
            GUILayout.Label($"odometer:  {odo,7:0.000} m   <i>(controller)</i>", _label);
            GUILayout.Label($"ground:    {truth,7:0.000} m   <i>(truth)</i>", _label);
            GUILayout.Label($"drift:     {(odo - truth) * 1000f,7:0} mm", _label);
            GUILayout.Label($"leg left:  {rem,7:0.000} m", _label);

            if (TryGet("dbg/stop_err_mm", out float err) && state >= 7)
                GUILayout.Label($"<b>stop error: {err:0.0} mm</b>", _label);

            GUILayout.EndArea();
        }
    }
}
