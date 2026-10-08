using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// UWB two-way-ranging tag (SEN-05), DW3000 class, ABI <c>SENSOR_UWB</c>:
    /// <c>[anchor_id, range_m, nlos_db, anchor_x, anchor_y, anchor_z]</c>.
    ///
    /// One range per sample on a TDMA schedule: each read ranges the next
    /// anchor in id order, so <see cref="SensorComponent.updateRateHz"/> is the
    /// slot rate (anchors × per-anchor rate). The anchor's surveyed position
    /// rides along (world frame, <see cref="UwbAnchor.WorldFlu"/>) — on the
    /// car it comes from the survey file, here from the scene.
    ///
    /// Errors: a per-tag antenna-delay bias; LOS σ; non-line-of-sight (a
    /// raycast from tag to anchor hits something) adds a positive exponential
    /// bias, inflates σ and drops more packets; rare heavy-tailed outliers;
    /// nothing beyond the maximum range. A dropped slot reads anchor_id −1
    /// and zeros. nlos_db is the first-path-to-total power gap a DW3000
    /// reports: ~2 dB in line of sight, ~9 dB blocked (> 6 dB suggests NLOS).
    /// </summary>
    public sealed class UwbSensor : SensorComponent
    {
        [Header("UWB")]
        public float maxRangeM = 40f;
        public float losSigmaM = 0.05f;          // [D] DW3000 ±10 cm class
        public float antennaBiasSigmaM = 0.05f;  // [E] uncalibrated antenna delay
        public float nlosBiasMeanM = 0.3f;       // [E] exponential mean
        public float nlosSigmaScale = 3f;        // [E]
        public float losDropout = 0.02f;         // [E]
        public float nlosDropout = 0.2f;         // [E]
        public float outlierProb = 0.01f;        // [E]
        public float outlierMaxM = 3f;           // [E] uniform 0.5 … this

        private static readonly string[] Fields = { "id", "range", "nlos_db", "ax", "ay", "az" };
        private static readonly RaycastHit[] HitBuf = new RaycastHit[16];
        private Transform _ignoreRoot;
        private System.Random _rng;
        private float _antennaBias;
        private int _slot;

        public override SensorType Type => SensorType.Uwb;
        public override int DataCount => 6;
        public override IReadOnlyList<string> FieldNames => Fields;

        /// <summary>The last slot: its anchor, true range and whether the
        /// path was blocked — for checks only.</summary>
        public int TruthAnchor { get; private set; } = -1;
        public float TruthRange { get; private set; }
        public bool TruthNlos { get; private set; }
        public float AntennaBias => _antennaBias;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _ignoreRoot = vehicleRoot;
            rangeMin = 0f;
            rangeMax = maxRangeM;
            _rng = null;
            _slot = 0;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            if (_rng == null)
            {
                _rng = SensorRealism.Rng(sensorName, 16);
                _antennaBias = (float)(Gauss() * antennaBiasSigmaM);
            }
            for (int i = 0; i < 6; i++) dest[offset + i] = 0f;
            dest[offset] = -1f;
            var anchors = UwbAnchor.All;
            TruthAnchor = -1;
            if (anchors.Count == 0) return;

            var a = anchors[_slot % anchors.Count];
            _slot = (_slot + 1) % anchors.Count;
            Vector3 p = transform.position, q = a.transform.position;
            float truth = Vector3.Distance(p, q);
            bool nlos = Blocked(p, q, a.transform);
            TruthAnchor = a.anchorId; TruthRange = truth; TruthNlos = nlos;

            if (truth > maxRangeM) return;
            if (_rng.NextDouble() < (nlos ? nlosDropout : losDropout)) return;

            double r = truth + _antennaBias;
            if (nlos)
            {
                r += -nlosBiasMeanM * System.Math.Log(1.0 - _rng.NextDouble());
                r += Gauss() * losSigmaM * nlosSigmaScale;
            }
            else r += Gauss() * losSigmaM;
            if (_rng.NextDouble() < outlierProb)
                r += 0.5 + _rng.NextDouble() * (outlierMaxM - 0.5);

            Vector3 aw = UwbAnchor.WorldFlu(q);
            dest[offset] = a.anchorId;
            dest[offset + 1] = (float)System.Math.Max(0.0, r);
            dest[offset + 2] = (float)((nlos ? 9.0 : 2.0) + Gauss() * (nlos ? 3.0 : 1.0));
            dest[offset + 3] = aw.x; dest[offset + 4] = aw.y; dest[offset + 5] = aw.z;
        }

        private bool Blocked(Vector3 p, Vector3 q, Transform anchor)
        {
            Vector3 d = q - p;
            float len = d.magnitude;
            if (len < 1e-4f) return false;
            int k = Physics.RaycastNonAlloc(p, d / len, HitBuf, len, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < k; i++)
            {
                var c = HitBuf[i].collider;
                if (c == null) continue;
                if (_ignoreRoot != null && c.transform.IsChildOf(_ignoreRoot)) continue;
                if (c.transform.IsChildOf(anchor)) continue;
                return true;
            }
            return false;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }
    }
}
