using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Magnetometer / compass: absolute heading of the vehicle in degrees,
    /// 0 = world +Z, increasing clockwise (matching atan2(fwd.x, fwd.z)), with
    /// an authorable declination offset. Noise and random-walk drift come from
    /// the shared <see cref="NoiseModel"/> and are applied BEFORE wrapping so
    /// drift can carry the reading across the 360→0 seam.
    /// </summary>
    public sealed class MagSensor : SensorComponent
    {
        [Header("Magnetometer")]
        [Tooltip("Declination offset added to the true heading (deg).")]
        public float declinationDeg = 0f;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "heading_deg" };
        private Transform _root;

        public override SensorType Type => SensorType.Mag;
        public override int DataCount => 1;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _root = vehicleRoot != null ? vehicleRoot : transform;
            rangeMin = 0f;
            rangeMax = 360f;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            Transform t = _root != null ? _root : transform;
            Vector3 fwd = t.forward;
            float heading = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg + declinationDeg;
            heading = noise.Apply(heading, dt);
            dest[offset] = Mathf.Repeat(heading, 360f);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.forward * 0.15f);
        }
    }
}
