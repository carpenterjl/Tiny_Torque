using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Quadrature-style encoder on a wheel or its motor shaft. It counts the
    /// shaft angle the physics actually integrated — the vehicle advances each
    /// wheel's angle every physics step (SEN-02) — so the count is exact at any
    /// sample rate, with no integration bias of its own. It reports a wrapped
    /// tick count and the velocity re-derived from those ticks, so at low CPR
    /// the velocity shows the quantization a real encoder would.
    ///
    /// Options, all off by default: a Hall-sensor mode (6 edges per electrical
    /// revolution, the low-cost fallback), a sinusoidal per-revolution INL like a
    /// magnetic encoder's, and an absolute-angle field (a 14-bit magnetic
    /// encoder's SPI register) appended as a third channel.
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
        [Tooltip("Hall-sensor commutation as the encoder: >0 = this many pole pairs, " +
                 "6 counts per electrical revolution (overrides countsPerRev). 0 = quadrature.")]
        public int hallPolePairs = 0;
        [Tooltip("Integral non-linearity: sinusoidal angle error per shaft turn, degrees (0 = none).")]
        public float inlDeg = 0f;
        [Tooltip("Absolute-angle register width in bits (0 = no angle channel; 14 = AS5047-class).")]
        public int absAngleBits = 0;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "vel", "ticks" };
        private static readonly string[] FieldsAbs = { "vel", "ticks", "angle" };
        private CarVehicle _vehicle;
        private MotorPart _motor;        // on the motor shaft with lash: read the rotor
        private bool _hasBase;
        private double _baseAngle;       // shaft angle at the first sample: counts start at 0
        private long _totalTicks;        // monotonic (pre-wrap) tick count
        private long _prevTicks;

        public override SensorType Type => SensorType.Encoder;
        public override int DataCount => absAngleBits > 0 ? 3 : 2;
        public override IReadOnlyList<string> FieldNames => absAngleBits > 0 ? FieldsAbs : Fields;

        /// <summary>Counts per turn of the measured shaft, as the hardware counts.</summary>
        public int EffectiveCpr => hallPolePairs > 0 ? 6 * hallPolePairs : Mathf.Max(1, countsPerRev);

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            _motor = null;
            _hasBase = false;
            rangeMin = -Mathf.Infinity; // velocity range is informational only
            rangeMax = Mathf.Infinity;
        }

        /// <summary>Measured-shaft angle (rad, unwrapped): the wheel's integrated
        /// angle, plus the lash gap when this encoder sits on a motor whose
        /// rotor can move inside the free play, times the gear ratio.</summary>
        private double ShaftAngle()
        {
            if (_vehicle == null) return 0.0;
            // Found lazily: the rig binds the motors and the sensors in its own order.
            if (_motor == null && gearRatio > 1f)
                foreach (var m in _vehicle.Motors)
                    if (m != null && m.wheelIndex == wheelIndex) { _motor = m; break; }
            double wheel = _vehicle.WheelAngle(wheelIndex);
            if (_motor != null) wheel += _motor.LashGapRad;
            return wheel * Mathf.Max(0.01f, gearRatio);
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            double theta = ShaftAngle();
            if (!_hasBase) { _baseAngle = theta; _hasBase = true; }
            theta -= _baseAngle;
            // A magnetic encoder's angle error repeats once per turn of its shaft.
            if (inlDeg != 0f)
                theta += inlDeg * Mathf.Deg2Rad * System.Math.Sin(theta + _baseAngle);

            double tickAngle = (System.Math.PI * 2.0) / EffectiveCpr;
            // A counter steps when an edge passes: floor, so reversing through
            // zero counts −1 rather than sticking at 0 for a whole count.
            _totalTicks = (long)System.Math.Floor(theta / tickAngle);

            // Velocity re-derived from the quantized tick delta (shows stepping at low CPR).
            float quantVel = dt > 1e-6f ? (float)((_totalTicks - _prevTicks) * tickAngle / dt) : 0f;
            _prevTicks = _totalTicks;

            long wrapped = wrap > 0 ? ((_totalTicks % wrap) + wrap) % wrap : _totalTicks;

            dest[offset] = noise.Apply(quantVel, dt);
            dest[offset + 1] = wrapped;
            if (absAngleBits > 0)
            {
                // The absolute register: this turn's angle, at the part's
                // resolution. Referred to the shaft's own zero, not the count's.
                double full = 1L << Mathf.Clamp(absAngleBits, 1, 24);
                double a = theta + _baseAngle;
                double frac = a / (2.0 * System.Math.PI);
                frac -= System.Math.Floor(frac);
                dest[offset + 2] = (float)System.Math.Floor(frac * full);
            }
        }
    }
}
