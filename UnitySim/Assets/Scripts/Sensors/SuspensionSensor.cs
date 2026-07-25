using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Strut load/travel sensor on one WheelCollider. Reports the spring force the
    /// suspension is currently reacting (N, what a load cell in the strut reads),
    /// the normalized compression (0 = full droop / airborne, 1 = bottomed), and
    /// the configured strut tilt angle (deg). Bound to a wheel by index like the
    /// wheel encoder; the readings come straight from the vehicle's per-wheel
    /// suspension so a controller can watch weight transfer and ride height.
    /// </summary>
    public sealed class SuspensionSensor : SensorComponent
    {
        [Header("Suspension")]
        [Tooltip("Which wheel: 0=FL, 1=FR, 2=RL, 3=RR.")]
        public int wheelIndex = 0;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "force", "comp", "angle" };
        private CarVehicle _vehicle;

        public override SensorType Type => SensorType.Suspension;
        public override int DataCount => 3;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            rangeMin = 0f;
            rangeMax = Mathf.Infinity; // force range is informational
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            float force = _vehicle != null ? _vehicle.GetSuspensionForce(wheelIndex) : 0f;
            float comp  = _vehicle != null ? _vehicle.GetSuspensionCompression(wheelIndex) : 0f;
            float angle = _vehicle != null ? _vehicle.GetSuspensionAngle(wheelIndex) : 0f;

            dest[offset]     = noise.Apply(force);
            dest[offset + 1] = comp;
            dest[offset + 2] = angle;
        }
    }
}
