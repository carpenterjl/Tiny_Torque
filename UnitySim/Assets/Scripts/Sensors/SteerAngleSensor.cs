using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Steering-angle feedback (ACT-09), ABI <c>SENSOR_STEER_ANGLE</c>:
    /// <c>[angle_rad]</c>, the bicycle-model road-wheel angle, + = LEFT (FLU),
    /// as a pot or magnetic encoder on the steering knuckle would read it —
    /// after the servo's lag and the linkage backlash, so firmware can close
    /// its own loop around the real steering instead of trusting the command.
    /// Noise and resolution come from the shared <see cref="NoiseModel"/>
    /// (radians).
    /// </summary>
    public sealed class SteerAngleSensor : SensorComponent
    {
        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "angle_rad" };
        private CarVehicle _vehicle;

        public override SensorType Type => SensorType.SteerAngle;
        public override int DataCount => 1;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            float lockRad = vehicle != null ? vehicle.MaxSteerDeg * Mathf.Deg2Rad : 0f;
            rangeMin = -lockRad;
            rangeMax = lockRad;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            float deg = _vehicle != null ? _vehicle.SteerAngleVirtualDeg : 0f;
            dest[offset] = noise.Apply(-deg * Mathf.Deg2Rad, dt);   // Unity + right → FLU + left
        }
    }
}
