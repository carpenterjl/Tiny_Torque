using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Multizone time-of-flight ranger (SEN-03), VL53L5CX / VL53L7CX class,
    /// ABI <c>SENSOR_TOF_MZ</c>: <c>[d_0 … d_{N²−1} (m), status_0 … status_{N²−1}]</c>.
    ///
    /// The square field of view is split into N×N zones; zone index = row·N +
    /// col, row 0 at the TOP and col 0 at the LEFT as seen from the sensor
    /// looking along its aim. Each zone casts sub-rays across its own patch
    /// and reports the closest return. Status follows the ST convention:
    /// 5 = valid, 4 = valid but weak (σ doubled), 0 = no target (distance =
    /// range_max). A return is weak or absent by the signal it would carry,
    /// cos(incidence)·(range/d)², so an oblique wall drops out before a
    /// square-on one. Range noise σ = √(floor² + (frac·d)²).
    /// The ST parts run 8×8 at up to 15 Hz and 4×4 at up to 60 Hz: set
    /// <see cref="SensorComponent.updateRateHz"/> to match the zone count.
    /// </summary>
    public sealed class MultizoneTofSensor : SensorComponent
    {
        public const int StatusNone = 0, StatusWeak = 4, StatusValid = 5;

        [Header("Multizone ToF")]
        [Tooltip("Zones per side: 4 (4×4) or 8 (8×8).")]
        public int zonesPerSide = 8;
        [Tooltip("Square field of view (deg): 45 for the VL53L5CX, 60 for the VL53L7CX.")]
        public float fovDeg = 45f;
        public float maxRange = 4f;
        [Tooltip("Sub-rays per zone side (2 = 2×2 per zone).")]
        public int subRays = 2;
        public float sigmaFloorM = 0.005f;   // [E] short-range floor
        public float sigmaFrac = 0.015f;     // [E] grows with distance

        private static readonly RaycastHit[] HitBuf = new RaycastHit[8];
        private string[] _fields = System.Array.Empty<string>();
        private int _fieldsFor = -1;
        private Transform _ignoreRoot;
        private System.Random _rng;

        public int Zones => Mathf.Clamp(zonesPerSide, 1, 8) * Mathf.Clamp(zonesPerSide, 1, 8);
        public override SensorType Type => SensorType.TofMz;
        public override int DataCount => 2 * Zones;

        public override IReadOnlyList<string> FieldNames
        {
            get
            {
                if (_fieldsFor != Zones)
                {
                    int z = Zones;
                    _fields = new string[2 * z];
                    for (int i = 0; i < z; i++) { _fields[i] = $"d{i}"; _fields[z + i] = $"st{i}"; }
                    _fieldsFor = z;
                }
                return _fields;
            }
        }

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _ignoreRoot = vehicleRoot;
            rangeMin = 0f;
            rangeMax = maxRange;
            _rng = null;
        }

        /// <summary>Unit direction of the centre of zone (row, col), world frame.</summary>
        public Vector3 ZoneDirection(int row, int col, float subU = 0.5f, float subV = 0.5f)
        {
            int n = Mathf.Clamp(zonesPerSide, 1, 8);
            float zone = fovDeg / n;
            float yaw = -0.5f * fovDeg + (col + subU) * zone;     // + = right
            float pitch = -0.5f * fovDeg + (row + subV) * zone;   // + = down (row 0 on top)
            return transform.rotation * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            _rng ??= SensorRealism.Rng(sensorName, 14);
            int n = Mathf.Clamp(zonesPerSide, 1, 8), z = n * n;
            int s = Mathf.Max(1, subRays);
            Vector3 origin = transform.position;
            for (int row = 0; row < n; row++)
                for (int col = 0; col < n; col++)
                {
                    float best = float.MaxValue, bestSignal = 0f;
                    for (int a = 0; a < s; a++)
                        for (int b = 0; b < s; b++)
                        {
                            Vector3 dir = ZoneDirection(row, col, (a + 0.5f) / s, (b + 0.5f) / s);
                            if (!Cast(origin, dir, out float d, out Vector3 normal)) continue;
                            float cosInc = Mathf.Abs(Vector3.Dot(normal, -dir));
                            float signal = cosInc * (maxRange / Mathf.Max(0.01f, d)) * (maxRange / Mathf.Max(0.01f, d));
                            if (signal < 1f) continue;   // too faint to register
                            if (d < best) { best = d; bestSignal = signal; }
                        }
                    int i = row * n + col;
                    if (best == float.MaxValue)
                    {
                        dest[offset + i] = maxRange;
                        dest[offset + z + i] = StatusNone;
                        continue;
                    }
                    bool weak = bestSignal < 2f;
                    float sigma = Mathf.Sqrt(sigmaFloorM * sigmaFloorM + sigmaFrac * best * sigmaFrac * best);
                    if (weak) sigma *= 2f;
                    dest[offset + i] = Mathf.Clamp(best + (float)(Gauss() * sigma), 0f, maxRange);
                    dest[offset + z + i] = weak ? StatusWeak : StatusValid;
                }
        }

        private bool Cast(Vector3 origin, Vector3 dir, out float dist, out Vector3 normal)
        {
            int k = Physics.RaycastNonAlloc(origin, dir, HitBuf, maxRange, ~0, QueryTriggerInteraction.Ignore);
            dist = float.MaxValue; normal = Vector3.zero;
            for (int i = 0; i < k; i++)
            {
                var h = HitBuf[i];
                if (_ignoreRoot != null && h.collider != null && h.collider.transform.IsChildOf(_ignoreRoot))
                    continue;
                if (h.distance < dist) { dist = h.distance; normal = h.normal; }
            }
            return dist < float.MaxValue;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }
    }
}
