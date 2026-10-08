using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// A raw 6-axis MEMS IMU part (SEN-01), ABI <c>SENSOR_IMU6</c>:
    /// <c>[gx, gy, gz (rad/s), ax, ay, az (m/s²)]</c> in the CHIP's own
    /// right-handed frame — x along the part's aim, y to its left, z up out of
    /// the package. The mount pose in the v7 manifest rotates it into the body.
    ///
    /// The die is simulated on every physics step: the true rate and specific
    /// force at the mount point (lever arm included, from the body-fixed
    /// point's velocity), scale and cross-axis error, motor-tone vibration and
    /// white noise go through the digital low-pass at the physics rate; bias
    /// (turn-on, flicker, random walk) is added and the result clipped and
    /// quantized when the chip's output data rate — <see
    /// cref="SensorComponent.updateRateHz"/> on the TIM-04 sample clock — reads it.
    /// Vibration above the physics Nyquist cannot be represented, so a
    /// vibration study wants physics at 1–2 kHz.
    ///
    /// Accel is a backward difference over one physics step, so it lags the
    /// gyro by half a step (1.25 ms at 400 Hz).
    /// </summary>
    public sealed class MemsImuSensor : SensorComponent
    {
        public ImuSpec spec = new ImuSpec();

        private static readonly string[] Fields = { "gx", "gy", "gz", "ax", "ay", "az" };

        private Rigidbody _body;
        private MotorPart[] _motors = System.Array.Empty<MotorPart>();
        private float[] _vibPhase = System.Array.Empty<float>();
        private ImuTriad _gyro, _accel;
        private Vector3 _prevPointVel;
        private bool _hasPrev;

        public override SensorType Type => SensorType.Imu6;
        public override int DataCount => 6;
        public override IReadOnlyList<string> FieldNames => Fields;

        /// <summary>The true rate (rad/s) and specific force (m/s²) at the die,
        /// chip frame, before any error — for checks and telemetry only.</summary>
        public Vector3 TruthGyro { get; private set; }
        public Vector3 TruthAccel { get; private set; }

        public ImuTriad GyroErrors => _gyro;
        public ImuTriad AccelErrors => _accel;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _body = vehicleRoot != null ? vehicleRoot.GetComponent<Rigidbody>() : null;
            if (_body == null) _body = GetComponentInParent<Rigidbody>();
            _motors = vehicleRoot != null
                ? vehicleRoot.GetComponentsInChildren<MotorPart>(true)
                : System.Array.Empty<MotorPart>();
            _vibPhase = new float[_motors.Length];
            float a = spec.accelFullScaleG * ImuSpec.G0;
            rangeMin = a > 0f ? -a : 0f;
            rangeMax = a > 0f ? a : 0f;
            PowerUp();
        }

        /// <summary>Draw the part's constants (turn-on bias, scale, cross-axis)
        /// from a stream that depends only on the session seed and the name.</summary>
        public void PowerUp()
        {
            int seed;
            unchecked
            {
                uint h = 2166136261u;
                string s = sensorName ?? "";
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                seed = (int)h ^ (NoiseModel.GlobalSeed * 486187739) ^ 0x1A0;
            }
            var rng = new System.Random(seed);
            _gyro = spec.Gyro(rng);
            _accel = spec.Accel(rng);
            _hasPrev = false;
        }

        public override void PhysicsStep(long tUs, float dt)
        {
            if (_gyro == null) PowerUp();
            Rigidbody b = _body;
            Vector3 wWorld = b != null ? b.angularVelocity : Vector3.zero;
            Vector3 vPoint = b != null ? b.GetPointVelocity(transform.position) : Vector3.zero;
            Vector3 aWorld = _hasPrev && dt > 0f ? (vPoint - _prevPointVel) / dt : Vector3.zero;
            _prevPointVel = vPoint;
            _hasPrev = true;

            TruthGyro = FluFrame.Rate(transform.InverseTransformDirection(wWorld));
            TruthAccel = FluFrame.Vector(transform.InverseTransformDirection(aWorld - Physics.gravity));

            Vector3 vibA = Vector3.zero;
            if (spec.vibration > 0f)
            {
                // Per-rotor tone (1× + a 2.07× harmonic) whose frequency tracks
                // the rotor speed, like the built-in IMU's shake.
                for (int k = 0; k < _motors.Length; k++)
                {
                    var m = _motors[k];
                    if (m == null) continue;
                    float w = m.MotorOmega;
                    _vibPhase[k] += w * dt;
                    float s = Mathf.Sin(_vibPhase[k]) + 0.5f * Mathf.Sin(2.07f * _vibPhase[k]);
                    vibA += new Vector3(0.7f, 0.5f, 1.0f) * (spec.vibration * Mathf.Abs(w) / 1000f * s);
                }
            }

            _gyro.Step(TruthGyro + vibA * 0.1f, dt, spec.dlpfHz);
            _accel.Step(TruthAccel + vibA, dt, spec.dlpfHz);
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            if (_gyro == null) PowerUp();
            Vector3 g = _gyro.Output(), a = _accel.Output();
            dest[offset] = g.x; dest[offset + 1] = g.y; dest[offset + 2] = g.z;
            dest[offset + 3] = a.x; dest[offset + 4] = a.y; dest[offset + 5] = a.z;
        }
    }
}
