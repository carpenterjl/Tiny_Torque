using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Datasheet parameters of a raw 6-axis MEMS IMU (SEN-01), in the units a
    /// datasheet uses. Defaults are an ICM-42688-P in low-noise mode, the part
    /// the plan recommends (DEC-08 is still open, so swapping the part means
    /// changing these numbers, not the code). [D] = datasheet, [E] = estimate.
    /// All-zero errors with full-scale 0 give an ideal IMU.
    /// </summary>
    [System.Serializable]
    public class ImuSpec
    {
        [Tooltip("Digital low-pass corner (Hz), 2nd-order Butterworth run at the physics rate. 0 = off.")]
        public float dlpfHz = 50f;

        [Header("Gyro")]
        public float gyroNoiseDensityDps = 0.0028f;   // [D] °/s/√Hz (one-sided)
        public float gyroBiasInstabDph = 4f;          // [E] Allan floor, °/h
        public float gyroTurnOnBiasDps = 0.17f;       // [D] σ of the power-up bias (±0.5 °/s ≈ 3σ)
        public float gyroRateRandomWalkDpsRtS = 0f;   // °/s/√s; 0 = none
        public float gyroScaleErrPct = 0.17f;         // [D] σ (±0.5 % ≈ 3σ)
        public float gyroCrossAxisPct = 0.4f;         // [D] σ (±1.25 % ≈ 3σ)
        public float gyroFullScaleDps = 500f;         // 0 = no clip, no quantization

        [Header("Accelerometer")]
        public float accelNoiseDensityUg = 70f;       // [D] µg/√Hz (one-sided)
        public float accelBiasInstabUg = 40f;         // [E] Allan floor, µg
        public float accelTurnOnBiasMg = 7f;          // [D] σ (±20 mg ≈ 3σ)
        public float accelScaleErrPct = 0.17f;        // [D] σ (±0.5 % ≈ 3σ)
        public float accelCrossAxisPct = 0.33f;       // [D] σ (±1 % ≈ 3σ)
        public float accelFullScaleG = 4f;            // 0 = no clip, no quantization

        [Header("Converter and mount")]
        public int adcBits = 16;
        [Tooltip("Motor-tone vibration at the mount: accel amplitude (m/s²) per 1000 rad/s of each rotor. 0 = none.")]
        public float vibration = 0f;

        public ImuSpec Clone() => (ImuSpec)MemberwiseClone();

        /// <summary>An IMU with no errors at all (noise, bias, scale, clip, quantization).</summary>
        public static ImuSpec Ideal() => new ImuSpec
        {
            dlpfHz = 0f,
            gyroNoiseDensityDps = 0f, gyroBiasInstabDph = 0f, gyroTurnOnBiasDps = 0f,
            gyroScaleErrPct = 0f, gyroCrossAxisPct = 0f, gyroFullScaleDps = 0f,
            accelNoiseDensityUg = 0f, accelBiasInstabUg = 0f, accelTurnOnBiasMg = 0f,
            accelScaleErrPct = 0f, accelCrossAxisPct = 0f, accelFullScaleG = 0f,
        };

        public const float G0 = 9.80665f;
        public const float DegToRad = Mathf.PI / 180f;

        /// <summary>The gyro triad's errors in SI units (rad/s).</summary>
        public ImuTriad Gyro(System.Random rng) => new ImuTriad(rng,
            gyroNoiseDensityDps * DegToRad,
            gyroBiasInstabDph * DegToRad / 3600f,
            gyroTurnOnBiasDps * DegToRad,
            gyroRateRandomWalkDpsRtS * DegToRad,
            gyroScaleErrPct * 0.01f, gyroCrossAxisPct * 0.01f,
            gyroFullScaleDps * DegToRad, adcBits);

        /// <summary>The accelerometer triad's errors in SI units (m/s²).</summary>
        public ImuTriad Accel(System.Random rng) => new ImuTriad(rng,
            accelNoiseDensityUg * 1e-6f * G0,
            accelBiasInstabUg * 1e-6f * G0,
            accelTurnOnBiasMg * 1e-3f * G0,
            0f,
            accelScaleErrPct * 0.01f, accelCrossAxisPct * 0.01f,
            accelFullScaleG * G0, adcBits);
    }

    /// <summary>
    /// One triad (gyro or accel) of a MEMS IMU, SI units, stepped at the
    /// physics rate and read at the chip's output data rate:
    ///
    /// <code>
    /// truth ─► M (scale + cross-axis) ─► + white noise ─► DLPF ─┐
    ///                                                             ├─► + bias ─► clip ─► LSB
    /// turn-on bias + flicker (bias instability) + rate random walk┘
    /// </code>
    ///
    /// White noise is drawn at the physics rate with σ = density·√(fs/2), the
    /// same one-sided density convention as <see cref="NoiseModel"/>, so after
    /// the filter the output noise is density·√(noise bandwidth) and the Allan
    /// deviation of a static log is density/√(2τ) at short τ. Bias
    /// instability is flicker noise, approximated by five Gauss–Markov
    /// processes with decade-spaced time constants (0.5 s … 5000 s); their
    /// common σ is scaled so the Allan floor equals the configured value
    /// (flat to ±2 % from 1 s to 1000 s). Every random draw comes from the
    /// <see cref="System.Random"/> passed in, so a seed fixes the whole stream.
    /// </summary>
    public sealed class ImuTriad
    {
        public static readonly double[] FlickerTaus = { 0.5, 5.0, 50.0, 500.0, 5000.0 };

        public readonly float Density, BiasInstab, RateRandomWalk, FullScale, Lsb;
        private readonly double _flickerSigma;
        private readonly float[] _m = new float[9];       // row-major, sensed = M · truth
        private readonly Vector3 _turnOn;
        private readonly double[] _gm = new double[3 * 5];
        private readonly double[] _rrw = new double[3];
        private readonly Biquad[] _lpf = new Biquad[3];
        private readonly System.Random _rng;
        private float _lpfHz = -1f, _lpfDt = -1f;
        private bool _primed;
        private Vector3 _filtered;
        private bool _hasSpare;
        private double _spare;

        public ImuTriad(System.Random rng, float density, float biasInstab, float turnOnSigma,
                        float rateRandomWalk, float scaleSigma, float crossSigma,
                        float fullScale, int bits)
        {
            _rng = rng;
            Density = Mathf.Max(0f, density);
            BiasInstab = Mathf.Max(0f, biasInstab);
            RateRandomWalk = Mathf.Max(0f, rateRandomWalk);
            FullScale = Mathf.Max(0f, fullScale);
            Lsb = FullScale > 0f && bits > 1 ? FullScale / (1 << (bits - 1)) : 0f;
            _flickerSigma = BiasInstab > 0f ? BiasInstab / FlickerFloorPerUnitSigma() : 0.0;

            // Per-part constants, drawn once at "power-up".
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    _m[3 * r + c] = r == c ? 1f + (float)(Gauss() * scaleSigma)
                                           : (float)(Gauss() * crossSigma);
            _turnOn = new Vector3((float)(Gauss() * turnOnSigma), (float)(Gauss() * turnOnSigma),
                                  (float)(Gauss() * turnOnSigma));
            // Start the flicker states in their stationary distribution.
            if (_flickerSigma > 0)
                for (int i = 0; i < _gm.Length; i++) _gm[i] = Gauss() * _flickerSigma;
        }

        /// <summary>Truth after scale and cross-axis error (no noise, no bias).</summary>
        public Vector3 Misalign(Vector3 t) => new Vector3(
            _m[0] * t.x + _m[1] * t.y + _m[2] * t.z,
            _m[3] * t.x + _m[4] * t.y + _m[5] * t.z,
            _m[6] * t.x + _m[7] * t.y + _m[8] * t.z);

        /// <summary>Advance one physics step with the true signal at the die.</summary>
        public void Step(Vector3 truth, float dt, float dlpfHz)
        {
            if (dt <= 0f) return;
            Vector3 x = Misalign(truth);
            if (Density > 0f)
            {
                float sigma = Density * Mathf.Sqrt(0.5f / dt);
                x.x += (float)(Gauss() * sigma);
                x.y += (float)(Gauss() * sigma);
                x.z += (float)(Gauss() * sigma);
            }

            if (dlpfHz > 0f)
            {
                if (dlpfHz != _lpfHz || dt != _lpfDt)
                {
                    for (int k = 0; k < 3; k++) _lpf[k].LowPass(dlpfHz, 1f / dt);
                    _lpfHz = dlpfHz; _lpfDt = dt;
                    _primed = false;
                }
                if (!_primed)
                {
                    // Start at steady state: no power-on ramp from zero.
                    _lpf[0].Prime(x.x); _lpf[1].Prime(x.y); _lpf[2].Prime(x.z);
                    _primed = true;
                }
                _filtered = new Vector3(_lpf[0].Step(x.x), _lpf[1].Step(x.y), _lpf[2].Step(x.z));
            }
            else _filtered = x;

            if (_flickerSigma > 0)
            {
                for (int j = 0; j < FlickerTaus.Length; j++)
                {
                    double phi = System.Math.Exp(-dt / FlickerTaus[j]);
                    double drive = _flickerSigma * System.Math.Sqrt(1.0 - phi * phi);
                    for (int a = 0; a < 3; a++)
                    {
                        int i = 5 * a + j;
                        _gm[i] = phi * _gm[i] + drive * Gauss();
                    }
                }
            }
            if (RateRandomWalk > 0f)
            {
                double s = RateRandomWalk * System.Math.Sqrt(dt);
                for (int a = 0; a < 3; a++) _rrw[a] += s * Gauss();
            }
        }

        /// <summary>The bias right now: turn-on + flicker + rate random walk.</summary>
        public Vector3 Bias
        {
            get
            {
                var b = _turnOn;
                for (int a = 0; a < 3; a++)
                {
                    double f = _rrw[a];
                    for (int j = 0; j < 5; j++) f += _gm[5 * a + j];
                    b[a] += (float)f;
                }
                return b;
            }
        }

        /// <summary>The register value the chip would report now.</summary>
        public Vector3 Output()
        {
            Vector3 v = _filtered + Bias;
            for (int a = 0; a < 3; a++) v[a] = Digitize(v[a]);
            return v;
        }

        private float Digitize(float v)
        {
            if (FullScale <= 0f) return v;
            v = Mathf.Clamp(v, -FullScale, FullScale - Lsb);
            return Lsb > 0f ? Mathf.Round(v / Lsb) * Lsb : v;
        }

        // ---- the flicker calibration, analytic ------------------------------

        /// <summary>Allan variance of a first-order Gauss–Markov process with
        /// stationary σ = 1 and correlation time T, at cluster time τ.</summary>
        public static double GaussMarkovAvar(double tau, double T)
        {
            double a = tau / T;
            return T * T / (tau * tau) * (2.0 * a - 3.0 + 4.0 * System.Math.Exp(-a) - System.Math.Exp(-2.0 * a));
        }

        /// <summary>Allan deviation of this model's flicker part at τ.</summary>
        public double FlickerAdev(double tau)
        {
            double v = 0;
            foreach (double T in FlickerTaus) v += GaussMarkovAvar(tau, T);
            return _flickerSigma * System.Math.Sqrt(v);
        }

        /// <summary>The floor (geometric mean of the Allan deviation over
        /// 1–1000 s) of the five-process sum with σ = 1 each.</summary>
        public static double FlickerFloorPerUnitSigma()
        {
            double logSum = 0; int n = 0;
            for (double tau = 1.0; tau <= 1000.0 * 1.0001; tau *= System.Math.Sqrt(10.0))
            {
                double v = 0;
                foreach (double T in FlickerTaus) v += GaussMarkovAvar(tau, T);
                logSum += 0.5 * System.Math.Log(v); n++;
            }
            return System.Math.Exp(logSum / n);
        }

        private double Gauss()
        {
            if (_hasSpare) { _hasSpare = false; return _spare; }
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            double r = System.Math.Sqrt(-2.0 * System.Math.Log(u1));
            double th = 2.0 * System.Math.PI * u2;
            _spare = r * System.Math.Sin(th);
            _hasSpare = true;
            return r * System.Math.Cos(th);
        }
    }

    /// <summary>Second-order low-pass biquad (Butterworth, bilinear transform
    /// with pre-warping), transposed direct form II.</summary>
    public struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public void LowPass(float fc, float fs)
        {
            fc = Mathf.Min(fc, 0.45f * fs);       // keep the corner below Nyquist
            double k = System.Math.Tan(System.Math.PI * fc / fs);
            double q = System.Math.Sqrt(0.5);     // Butterworth
            double norm = 1.0 / (1.0 + k / q + k * k);
            _b0 = (float)(k * k * norm);
            _b1 = 2f * _b0;
            _b2 = _b0;
            _a1 = (float)(2.0 * (k * k - 1.0) * norm);
            _a2 = (float)((1.0 - k / q + k * k) * norm);
            _z1 = _z2 = 0f;
        }

        /// <summary>Set the state so a constant input x is already at steady state.</summary>
        public void Prime(float x)
        {
            _z1 = x - _b0 * x;
            _z2 = _b2 * x - _a2 * x;
        }

        public float Step(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
