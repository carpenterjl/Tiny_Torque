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
    /// them at the control rate, and hands the manifest to the C controller via
    /// the ABI's ctrl_configure().
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
        [Tooltip("Sensor update rate (Hz). 0 = fresh sample every control tick (legacy).")]
        public float updateRateHz = 0f;
        [Tooltip("Reported values are delayed by this much (ms). 0 = same-tick (legacy).")]
        public float latencyMs = 0f;

        /// <summary>Write exactly DataCount floats into dest starting at offset.</summary>
        public abstract void Sample(float dt, float[] dest, int offset);

        // ---- rate gate + latency ring -------------------------------------

        private const int RingSize = 64;   // 64 control ticks ≈ 640 ms at 100 Hz
        private float[][] _ring;           // [RingSize][DataCount]
        private double[] _ringTime;
        private int _ringHead = -1;
        private int _ringCount;
        private double _nextSample = double.NegativeInfinity;
        private double _lastSampleTime;

        /// <summary>
        /// Rig entry point: samples fresh only when the sensor's own update rate
        /// says a new reading is due (dt_effective covers the ticks held over, so
        /// integrating sensors like encoders stay correct), pushes readings into
        /// a time-stamped ring, and outputs the newest reading old enough to
        /// satisfy the configured latency. Both features off (0) = exactly the
        /// legacy fresh-sample-every-tick behaviour.
        /// </summary>
        public void SampleGated(float dt, double simTime, float[] dest, int offset)
        {
            int n = DataCount;
            if (n <= 0) { Sample(dt, dest, offset); return; }

            // Fast path: no gating, no latency → sample straight into dest.
            if (updateRateHz <= 0f && latencyMs <= 0f)
            {
                Sample(dt, dest, offset);
                return;
            }

            if (_ring == null)
            {
                _ring = new float[RingSize][];
                for (int i = 0; i < RingSize; i++) _ring[i] = new float[n];
                _ringTime = new double[RingSize];
                _lastSampleTime = simTime - dt;
            }

            bool due = updateRateHz <= 0f || simTime >= _nextSample;
            if (due)
            {
                float dtEff = Mathf.Max(dt, (float)(simTime - _lastSampleTime));
                _ringHead = (_ringHead + 1) % RingSize;
                if (_ringCount < RingSize) _ringCount++;
                Sample(dtEff, _ring[_ringHead], 0);
                _ringTime[_ringHead] = simTime;
                _lastSampleTime = simTime;
                if (updateRateHz > 0f)
                {
                    double period = 1.0 / updateRateHz;
                    if (double.IsNegativeInfinity(_nextSample)) _nextSample = simTime + period;
                    else
                    {
                        _nextSample += period;
                        if (_nextSample < simTime) _nextSample = simTime + period; // catch up
                    }
                }
            }

            if (_ringHead < 0) { Sample(dt, dest, offset); return; }

            // Newest entry at or before (simTime − latency); fall back to the
            // oldest buffered reading while the pipe is still filling.
            double cutoff = simTime - Mathf.Max(0f, latencyMs) / 1000.0;
            int pick = _ringHead;
            for (int i = 0; i < _ringCount; i++)
            {
                int idx = (_ringHead - i + RingSize) % RingSize;
                pick = idx;
                if (_ringTime[idx] <= cutoff) break;
            }
            System.Array.Copy(_ring[pick], 0, dest, offset, n);
        }

        /// <summary>Drop buffered readings (vehicle reset / rig re-init).</summary>
        public void ResetSampling()
        {
            _ringHead = -1;
            _ringCount = 0;
            _nextSample = double.NegativeInfinity;
        }

        /// <summary>
        /// Give the sensor references it needs to read the vehicle. Called once by
        /// the rig after the vehicle is fully built. Default: nothing needed.
        /// </summary>
        public virtual void Bind(CarVehicle vehicle, Transform vehicleRoot) { }
    }
}
