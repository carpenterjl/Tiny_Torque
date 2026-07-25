using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Reusable sensor corruption model: constant bias, Gaussian white noise,
    /// optional quantization (as from a finite-resolution ADC/encoder), and a
    /// random-walk bias drift (thermal drift). Applying realistic corruption in
    /// sim is what lets filters tuned here survive contact with real hardware.
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
        [System.NonSerialized] private float _walk;
        [System.NonSerialized] private bool _hasSpare;
        [System.NonSerialized] private float _spare;

        private System.Random Rng =>
            _rng ??= new System.Random(unchecked(GlobalSeed * 486187739 + (_nextOrdinal++ * 1000003) + 12289));

        /// <summary>Clear drift/RNG state (fresh run of the same instance).</summary>
        public void ResetState()
        {
            _walk = 0f;
            _hasSpare = false;
            _rng = null;
        }

        /// <summary>Legacy overload: bias + noise + quantization, no drift.</summary>
        public float Apply(float trueValue) => Apply(trueValue, 0f);

        public float Apply(float trueValue, float dt)
        {
            if (driftRate > 0f && dt > 0f)
                _walk += NextGaussian() * driftRate * Mathf.Sqrt(dt);

            float v = trueValue + bias + _walk;
            if (noiseStdDev > 0f)
                v += NextGaussian() * noiseStdDev;
            if (quantizationStep > 1e-9f)
                v = Mathf.Round(v / quantizationStep) * quantizationStep;
            return v;
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
