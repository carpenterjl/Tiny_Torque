using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Reusable sensor corruption model: constant bias, Gaussian white noise,
    /// optional quantization (as from a finite-resolution ADC/encoder), and a
    /// random-walk bias drift (thermal drift). Applying realistic corruption in
    /// sim is what lets filters tuned here survive contact with real hardware.
    ///
    /// White noise is given either as a per-sample σ (<see cref="noiseStdDev"/>)
    /// or, the way a datasheet states it, as a density (units/√Hz,
    /// <see cref="noiseDensity"/>): σ = density·√bandwidth. A density keeps its
    /// meaning when the sample rate changes; a per-sample σ does not (SEN-07).
    ///
    /// One instance may serve several fields of a sensor (an IMU's three axes,
    /// a colour sensor's r/g/b). Each field passes its own <c>channel</c>, so
    /// each gets its own independent drift walk advanced once per sample —
    /// not one walk shared by every field and stepped once per field.
    ///
    /// Randomness is deterministic: each instance owns a System.Random seeded
    /// from <see cref="GlobalSeed"/> + a per-instance ordinal, so two runs with
    /// the same seed produce byte-identical sensor streams (repeatable controller
    /// validation). The effective seed is stamped into the CSV run metadata.
    /// </summary>
    [System.Serializable]
    public class NoiseModel
    {
        public float bias = 0f;
        [Tooltip("Standard deviation of additive Gaussian noise, in sensor units.")]
        public float noiseStdDev = 0f;
        [Tooltip("Quantization step (sensor units). 0 disables quantization.")]
        public float quantizationStep = 0f;
        [Tooltip("Random-walk bias drift rate (sensor units per √s). 0 disables drift.")]
        public float driftRate = 0f;
        [Tooltip("White-noise density (sensor units per √Hz). When > 0 it replaces noiseStdDev: σ = density·√bandwidth.")]
        public float noiseDensity = 0f;
        [Tooltip("Noise bandwidth for noiseDensity (Hz). 0 = the Nyquist band of the sample rate, 1/(2·dt).")]
        public float bandwidthHz = 0f;

        // ---- deterministic seeding ----------------------------------------

        /// <summary>
        /// Session noise seed. Set once at session start (from GameSettings, or
        /// drawn randomly when the setting is 0) BEFORE any sensor samples; the
        /// value used is recorded in telemetry metadata for reproducibility.
        /// </summary>
        public static int GlobalSeed = 0;

        private static int _nextOrdinal;

        /// <summary>Reset the per-instance ordinal counter (call at session start
        /// alongside setting <see cref="GlobalSeed"/> so instance seeds line up
        /// run-to-run when the vehicle build order is the same).</summary>
        public static void ResetOrdinals() => _nextOrdinal = 0;

        [System.NonSerialized] private System.Random _rng;
        [System.NonSerialized] private float _walk;           // channel 0
        [System.NonSerialized] private float[] _walks;        // channels 1..n
        [System.NonSerialized] private bool _hasSpare;
        [System.NonSerialized] private float _spare;

        private System.Random Rng =>
            _rng ??= new System.Random(unchecked(GlobalSeed * 486187739 + (_nextOrdinal++ * 1000003) + 12289));

        /// <summary>Clear drift/RNG state (fresh run of the same instance).</summary>
        public void ResetState()
        {
            _walk = 0f;
            _walks = null;
            _hasSpare = false;
            _rng = null;
        }

        /// <summary>Legacy overload: bias + noise + quantization; no drift, and
        /// a noise density cannot apply without a sample period.</summary>
        public float Apply(float trueValue) => Apply(trueValue, 0f, 0);

        public float Apply(float trueValue, float dt) => Apply(trueValue, dt, 0);

        /// <summary>Corrupt one field. <paramref name="channel"/> picks the
        /// field's own drift walk when one instance serves several fields.</summary>
        public float Apply(float trueValue, float dt, int channel)
        {
            float walk = WalkFor(channel);
            if (driftRate > 0f && dt > 0f)
            {
                walk += NextGaussian() * driftRate * Mathf.Sqrt(dt);
                SetWalk(channel, walk);
            }

            float v = trueValue + bias + walk;
            float sigma = Sigma(dt);
            if (sigma > 0f)
                v += NextGaussian() * sigma;
            if (quantizationStep > 1e-9f)
                v = Mathf.Round(v / quantizationStep) * quantizationStep;
            return v;
        }

        /// <summary>Per-sample white-noise σ at sample period <paramref name="dt"/>.</summary>
        public float Sigma(float dt)
        {
            if (noiseDensity > 0f)
            {
                float bw = bandwidthHz > 0f ? bandwidthHz : (dt > 0f ? 0.5f / dt : 0f);
                if (bw > 0f) return noiseDensity * Mathf.Sqrt(bw);
            }
            return noiseStdDev;
        }

        private float WalkFor(int channel)
        {
            if (channel <= 0) return _walk;
            return _walks != null && channel - 1 < _walks.Length ? _walks[channel - 1] : 0f;
        }

        private void SetWalk(int channel, float w)
        {
            if (channel <= 0) { _walk = w; return; }
            if (_walks == null || channel - 1 >= _walks.Length)
                System.Array.Resize(ref _walks, channel);
            _walks[channel - 1] = w;
        }

        // Box–Muller transform on the instance RNG (both outputs used).
        private float NextGaussian()
        {
            if (_hasSpare) { _hasSpare = false; return _spare; }
            float u1 = Mathf.Max(1e-6f, (float)Rng.NextDouble());
            float u2 = (float)Rng.NextDouble();
            float r = Mathf.Sqrt(-2f * Mathf.Log(u1));
            _spare = r * Mathf.Sin(2f * Mathf.PI * u2);
            _hasSpare = true;
            return r * Mathf.Cos(2f * Mathf.PI * u2);
        }
    }
}
