using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Whisker / touch switch: reports contact (0/1) and peak contact force (N)
    /// when the vehicle touches something near the sensor's mount point and
    /// roughly in the direction it faces. Fed by the shared
    /// <see cref="VehicleContactBus"/> on the vehicle root — no extra
    /// colliders, and PhysX contact impulses stay out of any bit-identity
    /// assertions (they are the least deterministic input in the sim).
    /// </summary>
    public sealed class BumpSensor : SensorComponent
    {
        [Header("Bump switch")]
        [Tooltip("Contacts within this distance of the mount count (m).")]
        public float activationRadius = 0.06f;
        [Tooltip("Full acceptance cone about the sensor's forward axis (deg).")]
        public float coneAngleDeg = 120f;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "contact", "force_n" };
        private static readonly VehicleContactBus.ContactRecord[] Scratch =
            new VehicleContactBus.ContactRecord[32];

        private VehicleContactBus _bus;
        private long _cursor;

        public override SensorType Type => SensorType.Bump;
        public override int DataCount => 2;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _bus = VehicleContactBus.Ensure(vehicleRoot);
            _cursor = 0;
            rangeMin = 0f;
            rangeMax = 1f;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            float maxImpulse = 0f;
            if (_bus != null)
            {
                int n = _bus.CopySince(_cursor, Scratch);
                _cursor = _bus.LatestSeq;
                float cosHalf = Mathf.Cos(0.5f * coneAngleDeg * Mathf.Deg2Rad);
                Vector3 pos = transform.position;
                Vector3 fwd = transform.forward;
                for (int i = 0; i < n; i++)
                {
                    var c = Scratch[i];
                    if ((c.point - pos).sqrMagnitude > activationRadius * activationRadius)
                        continue;
                    // The contact normal points away from the other body, so a
                    // head-on hit has normal ≈ -forward.
                    if (Vector3.Dot(-c.normal, fwd) < cosHalf) continue;
                    if (c.impulse > maxImpulse) maxImpulse = c.impulse;
                }
            }

            float physicsDt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            float force = noise.Apply(maxImpulse / physicsDt, dt);
            dest[offset]     = maxImpulse > 0f ? 1f : 0f;
            dest[offset + 1] = Mathf.Max(0f, force);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, activationRadius);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.1f);
        }
    }
}
