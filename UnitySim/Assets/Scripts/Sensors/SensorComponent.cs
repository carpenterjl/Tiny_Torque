using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Base class for a configurable sensor "part" mounted on a vehicle. Each
    /// sensor is a child GameObject, so its world position and aim come straight
    /// from the transform. The <see cref="SensorRig"/> discovers all sensors on a
    /// vehicle, lays their data out contiguously in a flat float buffer, samples
    /// them, and hands the manifest to the C controller via the ABI's
    /// ctrl_configure().
    ///
    /// Contract: <see cref="FieldNames"/>.Count must equal <see cref="DataCount"/>,
    /// and <see cref="Sample"/> must write exactly DataCount floats at [offset..].
    /// </summary>
    public abstract class SensorComponent : MonoBehaviour
    {
        [Tooltip("Unique name; becomes the manifest name and telemetry channel prefix.")]
        public string sensorName = "sensor";

        [Tooltip("Reported output range, passed to the controller manifest.")]
        public float rangeMin = 0f;
        public float rangeMax = 1f;

        /// <summary>ABI type tag for this sensor.</summary>
        public abstract SensorType Type { get; }

        /// <summary>Number of floats this sensor contributes to sensor_data.</summary>
        public abstract int DataCount { get; }

        /// <summary>Per-field labels (length == DataCount) used for telemetry channels.</summary>
        public abstract IReadOnlyList<string> FieldNames { get; }

        [Header("Realism")]
        [Tooltip("Sensor update rate (Hz). 0 = one sample per control tick (legacy).")]
        public float updateRateHz = 0f;
        [Tooltip("Reported values are delayed by this much (ms), resolved to the physics step. 0 = same-tick (legacy).")]
        public float latencyMs = 0f;
        [Tooltip("TIM-03: sample instants sit this far after the control-tick grid (ms), so sensors are not all sampled at once. 0 = aligned (legacy).")]
        public float phaseOffsetMs = 0f;
        [Tooltip("TIM-03: σ of the sample-instant jitter (µs), resolved to the physics step. 0 = none (legacy).")]
        public float jitterUs = 0f;

        /// <summary>Write exactly DataCount floats into dest starting at offset.</summary>
        public abstract void Sample(float dt, float[] dest, int offset);

        /// <summary>
        /// Called on every physics step, before any sample is taken at that
        /// step, for sensors whose signal chain runs faster than they report
        /// (an IMU's die and digital filter). Default: nothing.
        /// </summary>
        public virtual void PhysicsStep(long tUs, float dt) { }

        // ---- sample clock + latency ring (TIM-02/03/04) ----------------------

        private float[][] _ring;           // [size][DataCount]
        private long[] _ringUs;
        private uint[] _ringSeq;
        private int _ringHead = -1;
        private int _ringCount;
        private long _lastSampleUs;
        private SampleSchedule _sched;
        private uint _seq;

        /// <summary>Sequence number of the value currently reported: bumps once
        /// per FRESH sample, so a reading held by the rate gate keeps its number
        /// (TIM-01). Never reset, so a respawn cannot make an old number recur.</summary>
        public uint StampSeq { get; private set; }

        /// <summary>Sim time (µs) at which the reported value was sampled.</summary>
        public long StampUs { get; private set; }

        /// <summary>Sim time (s) at which the reported value was sampled.</summary>
        public double StampTime => StampUs * 1e-6;

        /// <summary>
        /// True when the sensor keeps its own sample clock: it is sampled on
        /// physics steps (<see cref="PhysicsTick"/>) and reported from a
        /// time-stamped ring. False = the legacy fast path, one fresh sample
        /// per control tick taken in <see cref="Output"/>.
        /// </summary>
        public bool OnPhysicsClock =>
            DataCount > 0 && (updateRateHz > 0f || latencyMs > 0f || phaseOffsetMs > 0f || jitterUs > 0f);

        /// <summary>
        /// Rig entry point, once per physics step and before any control step
        /// in the same step (TIM-04). Takes a sample when the sensor's own
        /// clock says one is due, so a 15 Hz part lands on the physics grid
        /// rather than the control grid. A sensor with no rate of its own runs
        /// at the control rate, phased so that a sample is exactly
        /// <see cref="latencyMs"/> old when the control tick reads it.
        /// </summary>
        /// <param name="tUs">Sim time of this physics step.</param>
        /// <param name="anchorUs">Time of the latest control tick at or before tUs.</param>
        public void PhysicsTick(long tUs, long anchorUs, long controlPeriodUs, long physPeriodUs)
        {
            if (!OnPhysicsClock) return;
            if (_sched == null)
            {
                double periodUs = updateRateHz > 0f ? 1e6 / updateRateHz : controlPeriodUs;
                long latUs = LatencyUs;
                long phaseUs = (long)System.Math.Round(Mathf.Max(0f, phaseOffsetMs) * 1000.0);
                // No rate of its own: sample at (control tick − latency), so
                // the control tick reads a value exactly latency old.
                if (updateRateHz <= 0f) phaseUs -= latUs;
                _sched = new SampleSchedule(periodUs, phaseUs, jitterUs, JitterSeed());
                AllocRing(System.Math.Max(periodUs, physPeriodUs), latUs);
            }
            if (!_sched.Due(tUs, anchorUs)) return;

            long dtUs = _ringHead < 0 ? (long)_sched.PeriodUs : tUs - _lastSampleUs;
            _ringHead = (_ringHead + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            Sample(System.Math.Max(1L, dtUs) * 1e-6f, _ring[_ringHead], 0);
            _ringUs[_ringHead] = tUs;
            _ringSeq[_ringHead] = ++_seq;
            _lastSampleUs = tUs;
        }

        /// <summary>
        /// Rig entry point on the control tick: write the reading the firmware
        /// sees now. Fast path = sample fresh. Otherwise the newest buffered
        /// reading at or before (now − latency); while the pipe is still
        /// filling, the oldest one; before the first sample, a fresh one.
        /// </summary>
        public void Output(float dt, long tUs, float[] dest, int offset)
        {
            int n = DataCount;
            if (n <= 0) { Sample(dt, dest, offset); return; }

            if (!OnPhysicsClock || _ringHead < 0)
            {
                Sample(dt, dest, offset);
                StampSeq = ++_seq;
                StampUs = tUs;
                return;
            }

            long cutoff = tUs - LatencyUs;
            int pick = _ringHead;
            for (int i = 0; i < _ringCount; i++)
            {
                int idx = (_ringHead - i + _ring.Length) % _ring.Length;
                pick = idx;
                if (_ringUs[idx] <= cutoff) break;
            }
            System.Array.Copy(_ring[pick], 0, dest, offset, n);
            StampSeq = _ringSeq[pick];
            StampUs = _ringUs[pick];
        }

        /// <summary>Drop buffered readings and the sample clock (vehicle reset /
        /// rig re-init); the next physics step starts a new schedule.</summary>
        public void ResetSampling()
        {
            _ringHead = -1;
            _ringCount = 0;
            _sched = null;
            _ring = null;
        }

        private long LatencyUs => (long)System.Math.Round(Mathf.Max(0f, latencyMs) * 1000.0);

        // Enough slots to cover the latency at the sample period, plus slack.
        private void AllocRing(double samplePeriodUs, long latUs)
        {
            int size = (int)System.Math.Ceiling(latUs / System.Math.Max(1.0, samplePeriodUs)) + 4;
            size = Mathf.Clamp(size, 8, 8192);
            int n = DataCount;
            _ring = new float[size][];
            for (int i = 0; i < size; i++) _ring[i] = new float[n];
            _ringUs = new long[size];
            _ringSeq = new uint[size];
            _ringHead = -1;
            _ringCount = 0;
        }

        // The jitter stream depends on the session seed and the sensor's name
        // only, so enabling jitter on one sensor cannot reshuffle the noise
        // ordinals (and the seeded streams) of every other sensor.
        private int JitterSeed()
        {
            unchecked
            {
                uint h = 2166136261u;
                string s = sensorName ?? "";
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (int)h ^ (NoiseModel.GlobalSeed * 486187739) ^ 0x5EED;
            }
        }

        /// <summary>
        /// Give the sensor references it needs to read the vehicle. Called once by
        /// the rig after the vehicle is fully built. Default: nothing needed.
        /// </summary>
        public virtual void Bind(CarVehicle vehicle, Transform vehicleRoot) { }
    }

    /// <summary>
    /// A sensor's sample clock on the integer µs time base (TIM-03/04). Nominal
    /// instants are t0 + phase + k·period, computed from k each time so a
    /// non-integer period never accumulates rounding; t0 is the control tick
    /// at which the schedule started. Each instant may be moved by Gaussian
    /// jitter (clipped to ±0.45 period, so samples never reorder). A sample is
    /// due on the first physics step at or after its instant, so jitter and
    /// phase are resolved to the physics step — run physics at 1–2 kHz when a
    /// test needs finer.
    /// </summary>
    public sealed class SampleSchedule
    {
        public readonly double PeriodUs;
        private readonly long _phaseUs;
        private readonly double _jitterSigmaUs;
        private readonly System.Random _rng;
        private bool _started;
        private long _t0;
        private long _k;
        private long _dueUs;

        public SampleSchedule(double periodUs, long phaseUs, float jitterSigmaUs, int seed)
        {
            PeriodUs = System.Math.Max(1.0, periodUs);
            // Phase is taken modulo the period: only its position in the cycle matters.
            double ph = phaseUs % PeriodUs;
            if (ph < 0) ph += PeriodUs;
            _phaseUs = (long)System.Math.Round(ph);
            _jitterSigmaUs = System.Math.Max(0f, jitterSigmaUs);
            if (_jitterSigmaUs > 0) _rng = new System.Random(seed);
        }

        /// <summary>The instant the next sample is due (µs).</summary>
        public long DueUs => _dueUs;

        /// <summary>True when a sample is due at <paramref name="tUs"/>; the
        /// schedule then advances to the next instant.</summary>
        public bool Due(long tUs, long anchorUs)
        {
            if (!_started)
            {
                _started = true;
                _t0 = anchorUs + _phaseUs;
                _k = System.Math.Max(0L, (long)System.Math.Ceiling((tUs - _t0) / PeriodUs));
                _dueUs = Nominal(_k) + Jitter();
            }
            if (tUs < _dueUs) return false;

            _k++;
            // More than a whole period behind (a stalled clock): re-grid
            // rather than burst out the missed samples.
            if (Nominal(_k) + PeriodUs <= tUs)
                _k = (long)System.Math.Ceiling((tUs - _t0) / PeriodUs);
            _dueUs = Nominal(_k) + Jitter();
            return true;
        }

        private long Nominal(long k) => _t0 + (long)System.Math.Round(k * PeriodUs);

        private long Jitter()
        {
            if (_rng == null) return 0;
            // Box–Muller; one draw per instant.
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            double z = System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
            double lim = 0.45 * PeriodUs;
            return (long)System.Math.Round(System.Math.Max(-lim, System.Math.Min(lim, z * _jitterSigmaUs)));
        }
    }
}
