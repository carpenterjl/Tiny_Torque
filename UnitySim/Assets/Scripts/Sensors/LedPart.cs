using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Firmware-controlled indicator LED — an actuator part, not a sensor,
    /// but it rides the sensor manifest the same way <see cref="MotorPart"/>
    /// does. CtrlOutputs.actuator[8] cannot grow, so the LED claims TWO
    /// consecutive free actuator slots after the motors:
    ///   slot A = RGB24 packed as an integer-valued float ((r&lt;&lt;16)|(g&lt;&lt;8)|b —
    ///            exact, floats represent integers ≤ 2^24),
    ///   slot B = blink rate in Hz (0 = solid).
    /// No free slot pair (≥ 5 motors) → ActuatorIndex −1, display-only with a
    /// console warning. Readback fields [r,g,b,lit] publish the post-blink-gate
    /// state to telemetry, so a graph shows exactly what the world sees.
    ///
    /// The dome visual lives on the DEFAULT layer, not the part-viz layer the
    /// sensor camera culls — an LED the camera can't see is pointless.
    /// </summary>
    public sealed class LedPart : SensorComponent
    {
        /// <summary>First of the two claimed actuator slots; −1 = display-only.</summary>
        public int ActuatorIndex { get; set; } = -1;

        private static readonly string[] Fields = { "r", "g", "b", "lit" };
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private Renderer _dome;
        private MaterialPropertyBlock _mpb;
        private Color _color = Color.black;
        private float _blinkHz;
        private bool _lit;

        public override SensorType Type => SensorType.Led;
        public override int DataCount => 4;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            rangeMin = 0f;
            rangeMax = 1f;
            if (_dome == null) _dome = GetComponentInChildren<Renderer>();
        }

        /// <summary>
        /// Decode this LED's two actuator values and drive the visual. Called
        /// by the rig each control tick with the same (possibly transport-
        /// delayed) command array the motors get; blink phase comes from
        /// deterministic sim time.
        /// </summary>
        public void ApplyActuators(float[] actuators, float simTime)
        {
            if (ActuatorIndex >= 0 && actuators != null && ActuatorIndex + 1 < actuators.Length)
            {
                int packed = Mathf.Clamp((int)actuators[ActuatorIndex], 0, 0xFFFFFF);
                _color = new Color(
                    ((packed >> 16) & 0xFF) / 255f,
                    ((packed >> 8) & 0xFF) / 255f,
                    (packed & 0xFF) / 255f);
                _blinkHz = Mathf.Max(0f, actuators[ActuatorIndex + 1]);
            }

            _lit = _blinkHz <= 0f || (simTime * _blinkHz) % 1f < 0.5f;
            Present();
        }

        /// <summary>Set the LED directly (no controller — IPC or scripts).</summary>
        public void SetState(Color color, float blinkHz, float simTime)
        {
            _color = color;
            _blinkHz = Mathf.Max(0f, blinkHz);
            _lit = _blinkHz <= 0f || (simTime * _blinkHz) % 1f < 0.5f;
            Present();
        }

        private void Present()
        {
            if (_dome == null) return;
            _mpb ??= new MaterialPropertyBlock();
            Color shown = _lit ? _color : Color.black;
            _mpb.SetColor(ColorId, shown);
            _mpb.SetColor(EmissionId, shown * (_lit ? 2f : 0f));
            _dome.SetPropertyBlock(_mpb);
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            // Readback: what the world currently sees (post blink gate).
            dest[offset]     = _lit ? _color.r : 0f;
            dest[offset + 1] = _lit ? _color.g : 0f;
            dest[offset + 2] = _lit ? _color.b : 0f;
            dest[offset + 3] = _lit ? 1f : 0f;
        }
    }
}
