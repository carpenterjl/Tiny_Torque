using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Quadrature-style wheel encoder on one WheelCollider. Integrates true wheel
    /// angle into discrete ticks at a configurable counts-per-revolution, then
    /// reports a wrapped tick count and the velocity re-derived from those ticks —
    /// so at low CPR the velocity shows the quantization a real encoder would.
    /// </summary>
    public sealed class WheelEncoderSensor : SensorComponent
    {
        [Header("Encoder")]
        [Tooltip("Which wheel: 0=FL, 1=FR, 2=RL, 3=RR.")]
        public int wheelIndex = 2;
        [Tooltip("Counts per revolution of the measured shaft (higher = finer).")]
        public int countsPerRev = 360;
        [Tooltip("Gear ratio from wheel to encoder shaft (1 = on the wheel; >1 = on the motor shaft).")]
        public float gearRatio = 1f;
        [Tooltip("Tick counter wraps at this value (mimics a 16-bit register).")]
        public int wrap = 65536;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "vel", "ticks" };
        private CarVehicle _vehicle;
        private double _accumAngleRad;   // integrated true angle
        private long _totalTicks;        // monotonic (pre-wrap) tick count
        private long _prevTicks;

        public override SensorType Type => SensorType.Encoder;
        public override int DataCount => 2;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            rangeMin = -Mathf.Infinity; // velocity range is informational only
            rangeMax = Mathf.Infinity;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            // Wheel rad/s from the vehicle's canonical source (the brush-model
            // spin integrator, or wc.rpm on the legacy PhysX-friction path).
            float omega = _vehicle != null ? _vehicle.WheelOmega(wheelIndex) : 0f;
            omega *= Mathf.Max(0.01f, gearRatio);  // encoder shaft (motor-side if geared)
            _accumAngleRad += omega * dt;

            float tickAngle = (Mathf.PI * 2f) / Mathf.Max(1, countsPerRev);
            _totalTicks = (long)(_accumAngleRad / tickAngle);

            // Velocity re-derived from the quantized tick delta (shows stepping at low CPR).
            float quantVel = dt > 1e-6f ? (_totalTicks - _prevTicks) * tickAngle / dt : 0f;
            _prevTicks = _totalTicks;

            long wrapped = wrap > 0 ? ((_totalTicks % wrap) + wrap) % wrap : _totalTicks;

            dest[offset] = noise.Apply(quantVel);
            dest[offset + 1] = wrapped;
        }
    }
}
