using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>Optional per-surface texture for optical flow (0 = featureless,
    /// 1 = ideal). Put it on a collider's GameObject; absent = the sensor's
    /// default texture.</summary>
    public interface IFlowTexture
    {
        float FlowTexture { get; }
    }

    /// <summary>
    /// Optical-flow sensor (SEN-04), PAA5100JE / PMW3901 class, ABI
    /// <c>SENSOR_FLOW</c>: <c>[dx_counts, dy_counts, squal]</c>.
    ///
    /// The chip looks along the part's aim (mount it facing the ground). Its
    /// image axes are x = the part's local UP and y = its local LEFT, so a
    /// part aimed straight down with its up toward the nose reads x forward,
    /// y left. Counts are the vehicle's motion over the ground (driver sign
    /// convention), accumulated since the last read, integers with the
    /// remainder carried — what the motion registers return.
    ///
    /// Per physics step the apparent angular motion of the patch is
    /// m = v⊥/h + ω × â (â the aim, h the range to the ground along it), so
    /// rotation shows up as flow just as it does on the real chip; firmware
    /// removes it with the gyro (v = h·(m − ω × â)). Counts = K·∫m dt.
    /// squal (0–255) falls with texture and away from the middle of the
    /// working height; it is 0, and the step is lost, outside the working
    /// height or above the maximum rate — the reading is then invalid, as on
    /// the real part.
    /// </summary>
    public sealed class FlowSensor : SensorComponent
    {
        [Header("Optical flow")]
        [Tooltip("Counts per radian of apparent motion [E].")]
        public float countsPerRad = 47.7f;
        [Tooltip("Maximum apparent rate (rad/s); PAA5100JE ≈ 45 (1.14 m/s at 25 mm), PMW3901 ≈ 7.4.")]
        public float maxRateRadS = 45.6f;
        [Tooltip("Working height along the aim (m); PAA5100JE 15–35 mm, PMW3901 ≥ 80 mm.")]
        public float minHeightM = 0.015f;
        public float maxHeightM = 0.035f;
        [Tooltip("Texture of surfaces without an IFlowTexture (0–1).")]
        public float defaultTexture = 0.8f;
        [Tooltip("Count noise: σ = floor + frac·|counts| per read.")]
        public float noiseFloorCounts = 0.5f;
        public float noiseFrac = 0.02f;

        private static readonly string[] Fields = { "dx", "dy", "squal" };
        private static readonly RaycastHit[] HitBuf = new RaycastHit[8];
        private Rigidbody _body;
        private Transform _ignoreRoot;
        private System.Random _rng;
        private double _accX, _accY;
        private float _squalSum;
        private int _squalSteps;
        private bool _lost;

        public override SensorType Type => SensorType.Flow;
        public override int DataCount => 3;
        public override IReadOnlyList<string> FieldNames => Fields;

        /// <summary>Last step's true apparent motion (rad/s, image axes) and
        /// range (m) — for checks only.</summary>
        public Vector2 TruthRate { get; private set; }
        public float TruthHeight { get; private set; }

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _ignoreRoot = vehicleRoot;
            _body = vehicleRoot != null ? vehicleRoot.GetComponent<Rigidbody>() : null;
            if (_body == null) _body = GetComponentInParent<Rigidbody>();
            rangeMin = -maxRateRadS * countsPerRad;
            rangeMax = maxRateRadS * countsPerRad;
            _rng = null;
            _accX = _accY = 0; _squalSum = 0; _squalSteps = 0; _lost = false;
        }

        public override void PhysicsStep(long tUs, float dt)
        {
            Vector3 aim = transform.forward;
            float h = 0f, tex = defaultTexture;
            if (Physics.RaycastNonAlloc(transform.position, aim, HitBuf, 3f * maxHeightM, ~0,
                                        QueryTriggerInteraction.Ignore) is int k && k > 0)
            {
                float best = float.MaxValue; Collider bestC = null;
                for (int i = 0; i < k; i++)
                {
                    var hit = HitBuf[i];
                    if (_ignoreRoot != null && hit.collider != null && hit.collider.transform.IsChildOf(_ignoreRoot))
                        continue;
                    if (hit.distance < best) { best = hit.distance; bestC = hit.collider; }
                }
                if (bestC != null)
                {
                    h = best;
                    if (bestC.TryGetComponent(out IFlowTexture ft)) tex = ft.FlowTexture;
                }
            }
            TruthHeight = h;

            Vector3 v = _body != null ? _body.GetPointVelocity(transform.position) : Vector3.zero;
            Vector3 w = _body != null ? _body.angularVelocity : Vector3.zero;
            Vector3 m = Vector3.zero;
            if (h > 1e-4f)
            {
                Vector3 vPerp = v - Vector3.Dot(v, aim) * aim;
                // The aim swings at ω × â (PhysX's own convention, the one
                // GetPointVelocity uses); a forward swing reads as forward motion.
                m = vPerp / h + Vector3.Cross(w, aim);
            }
            float mx = Vector3.Dot(m, transform.up);
            float my = Vector3.Dot(m, -transform.right);
            TruthRate = new Vector2(mx, my);

            bool inRange = h >= minHeightM && h <= maxHeightM;
            bool tooFast = Mathf.Sqrt(mx * mx + my * my) > maxRateRadS;
            if (!inRange || tooFast) { _lost = true; _squalSteps++; return; }

            _accX += countsPerRad * mx * dt;
            _accY += countsPerRad * my * dt;
            float mid = 0.5f * (minHeightM + maxHeightM), half = 0.5f * (maxHeightM - minHeightM);
            float x = half > 1e-6f ? (h - mid) / half : 0f;
            _squalSum += 255f * Mathf.Clamp01(tex) * (1f - 0.5f * x * x);
            _squalSteps++;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            _rng ??= SensorRealism.Rng(sensorName, 15);
            if (_lost || _squalSteps == 0)
            {
                // Lost track this read: the registers carry nothing usable.
                dest[offset] = 0f; dest[offset + 1] = 0f; dest[offset + 2] = 0f;
                _accX = _accY = 0;
            }
            else
            {
                double nx = _accX + Gauss() * (noiseFloorCounts + noiseFrac * System.Math.Abs(_accX));
                double ny = _accY + Gauss() * (noiseFloorCounts + noiseFrac * System.Math.Abs(_accY));
                double cx = System.Math.Round(nx), cy = System.Math.Round(ny);
                // Carry the fraction the register has not counted yet.
                _accX = nx - cx; _accY = ny - cy;
                dest[offset] = (float)cx;
                dest[offset + 1] = (float)cy;
                dest[offset + 2] = Mathf.Round(_squalSum / _squalSteps);
            }
            _squalSum = 0; _squalSteps = 0; _lost = false;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }
    }
}
