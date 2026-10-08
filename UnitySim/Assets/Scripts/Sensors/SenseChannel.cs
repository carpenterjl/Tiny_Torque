namespace AIHWSim.Sensors
{
    /// <summary>The error budget of one measured analog channel: gain and
    /// offset error (σ, drawn once per part), white noise (σ per sample) and
    /// the converter's LSB (0 = continuous).</summary>
    [System.Serializable]
    public struct SenseSpec
    {
        public float gainSigma, offsetSigma, noiseSigma, lsb;

        public SenseSpec(float gainSigma, float offsetSigma, float noiseSigma, float lsb)
        {
            this.gainSigma = gainSigma; this.offsetSigma = offsetSigma;
            this.noiseSigma = noiseSigma; this.lsb = lsb;
        }
    }

    /// <summary>One measured channel of a real part: value·(1 + gain error)
    /// + offset + noise, rounded to the LSB.</summary>
    public sealed class SenseChannel
    {
        public readonly float Gain, Offset, Sigma, Lsb;
        private readonly System.Random _rng;

        public SenseChannel(SenseSpec s, System.Random rng)
        {
            _rng = rng;
            Gain = 1f + (float)(Gauss() * s.gainSigma);
            Offset = (float)(Gauss() * s.offsetSigma);
            Sigma = s.noiseSigma;
            Lsb = s.lsb;
        }

        public float Apply(float v)
        {
            v = v * Gain + Offset;
            if (Sigma > 0f) v += (float)(Gauss() * Sigma);
            if (Lsb > 0f) v = (float)System.Math.Round(v / Lsb) * Lsb;
            return v;
        }

        private double Gauss()
        {
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
        }
    }

    /// <summary>
    /// SEN-06 / SEN-09: what the realistic sensor profile
    /// (<c>VehicleDesign.sensorRealism</c> = 1) puts on channels a design has
    /// no spec fields for. [D] = datasheet, [E] = estimate.
    /// </summary>
    public static class SensorRealism
    {
        /// <summary>INA228-class pack monitor, bus voltage [D]: 195.3 µV LSB, 0.05 % gain.</summary>
        public static readonly SenseSpec BatteryVolt = new SenseSpec(0.0005f, 0.001f, 0.0005f, 195.3e-6f);
        /// <summary>INA228 across a 2 mΩ shunt: 156 µA LSB [D]; the shunt's 0.3 % tolerance and a 2 mA offset [E].</summary>
        public static readonly SenseSpec BatteryAmp = new SenseSpec(0.003f, 0.002f, 0.005f, 156e-6f);
        /// <summary>FOC driver phase-current ADC, 12-bit over ±30 A [E].</summary>
        public static readonly SenseSpec PhaseAmp = new SenseSpec(0.01f, 0.02f, 0.03f, 0.0146f);
        /// <summary>FOC driver bus-voltage divider into a 12-bit ADC [E].</summary>
        public static readonly SenseSpec BusVolt = new SenseSpec(0.005f, 0.01f, 0.02f, 0.0073f);
        /// <summary>Winding NTC [E]: ±1 °C absolute, 0.3 °C noise.</summary>
        public static readonly SenseSpec WindingC = new SenseSpec(0f, 1f, 0.3f, 0.1f);
        /// <summary>Calibrated magnetometer heading [E]: 2° residual iron, 0.5° noise.</summary>
        public static readonly SenseSpec Heading = new SenseSpec(0f, 2f, 0.5f, 0.1f);

        /// <summary>A stream that depends only on the session seed, the part's
        /// name and a salt, so profile errors never shift other sensors' noise.</summary>
        public static System.Random Rng(string name, int salt)
        {
            unchecked
            {
                uint h = 2166136261u;
                string s = name ?? "";
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return new System.Random((int)h ^ (NoiseModel.GlobalSeed * 486187739) ^ (salt * 7919));
            }
        }
    }
}
