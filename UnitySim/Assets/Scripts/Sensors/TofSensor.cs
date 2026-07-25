using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Directional time-of-flight range finder. Casts one or more rays along the
    /// sensor's local +Z (forward) and reports the nearest hit distance in metres,
    /// or the configured max range when nothing is in view. A small cone of rays
    /// (odd count) approximates the finite beam width of a real ToF module.
    /// </summary>
    public sealed class TofSensor : SensorComponent
    {
        [Header("ToF")]
        public float maxRange = 4f;   // VL53L1X-class module
        [Tooltip("Number of rays across the beam (odd; 1 = single ray).")]
        public int coneRays = 1;
        [Tooltip("Full cone angle spanned by the rays (deg).")]
        public float coneAngleDeg = 8f;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "dist" };
        // Shared scratch buffer for non-allocating raycasts.
        private static readonly RaycastHit[] HitBuf = new RaycastHit[8];
        private Transform _ignoreRoot;

        public override SensorType Type => SensorType.Tof;
        public override int DataCount => 1;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _ignoreRoot = vehicleRoot;
            rangeMin = 0f;
            rangeMax = maxRange;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            float best = maxRange;
            int rays = Mathf.Max(1, coneRays);
            for (int k = 0; k < rays; k++)
            {
                // Fan the rays symmetrically about forward within the cone.
                float frac = rays == 1 ? 0f : (k / (float)(rays - 1)) - 0.5f;
                float yaw = frac * coneAngleDeg;
                Vector3 dir = Quaternion.AngleAxis(yaw, transform.up) * transform.forward;
                float d = CastRay(transform.position, dir);
                if (d < best) best = d;
            }

            float reading = Mathf.Clamp(noise.Apply(best), 0f, maxRange);
            dest[offset] = reading;
        }

        private float CastRay(Vector3 origin, Vector3 dir)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, HitBuf, maxRange, ~0, QueryTriggerInteraction.Ignore);
            float best = maxRange;
            for (int i = 0; i < n; i++)
            {
                var h = HitBuf[i];
                // Skip the vehicle's own colliders (body + wheels).
                if (_ignoreRoot != null && h.collider != null && h.collider.transform.IsChildOf(_ignoreRoot))
                    continue;
                if (h.distance < best) best = h.distance;
            }
            return best;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * maxRange);
        }
    }
}
