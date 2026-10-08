using System.Text;
using AIHWSim.Sensors;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[NOISE] — SEN-07's checks on <see cref="NoiseModel"/>.</b> Pure
    /// statistics, no scene:
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.NoiseModelCheck.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <list type="bullet">
    /// <item>A noise DENSITY means the same thing at any sample rate: the mean
    /// over one second has the same spread at 100 Hz and at 1 kHz (σ of a
    /// 1 s mean = density/√2), while the per-sample σ grows as √(fs/2).</item>
    /// <item>Each channel of a shared instance drifts on its own walk, at the
    /// stated rate (std after T seconds = rate·√T), uncorrelated with the
    /// others — not one walk stepped once per field.</item>
    /// <item>A legacy per-sample σ with no density produces exactly the same
    /// stream as before, so existing designs do not change.</item>
    /// </list>
    /// </summary>
    public static class NoiseModelCheck
    {
        private const string Tag = "[NOISE]";
        private static int _checks, _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [NOISE] Noise Model Check", priority = 123)]
        public static void RunFromMenu() => Run(false);

        public static void Report() => Run(true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0; _failed = 0; _log = new StringBuilder();
            int savedSeed = NoiseModel.GlobalSeed;
            NoiseModel.GlobalSeed = 4242;
            try
            {
                DensityIsRateInvariant();
                ChannelsDriftIndependently();
                LegacyStreamUnchanged();
            }
            finally { NoiseModel.GlobalSeed = savedSeed; }

            Debug.Log(_log.ToString().TrimEnd());
            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);
            if (exitWhenDone && Application.isBatchMode) EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        private static void DensityIsRateInvariant()
        {
            const float density = 0.01f;   // units/√Hz
            foreach (float fs in new[] { 100f, 1000f })
            {
                float dt = 1f / fs;
                var nm = new NoiseModel { noiseDensity = density };
                NoiseModel.ResetOrdinals();
                int perSecond = Mathf.RoundToInt(fs);
                const int seconds = 4000;
                double sumMeanSq = 0, sumSq = 0;
                for (int s = 0; s < seconds; s++)
                {
                    double acc = 0;
                    for (int i = 0; i < perSecond; i++)
                    {
                        float v = nm.Apply(0f, dt);
                        acc += v;
                        sumSq += (double)v * v;
                    }
                    double mean = acc / perSecond;
                    sumMeanSq += mean * mean;
                }
                double sigmaSample = System.Math.Sqrt(sumSq / (seconds * (double)perSecond));
                double sigmaMean = System.Math.Sqrt(sumMeanSq / seconds);
                double wantSample = density * System.Math.Sqrt(fs / 2.0);
                double wantMean = density / System.Math.Sqrt(2.0);
                Check($"{fs,5:0} Hz: per-sample sigma {sigmaSample:0.00000} vs density*sqrt(fs/2) {wantSample:0.00000}",
                      System.Math.Abs(sigmaSample / wantSample - 1.0) < 0.02);
                Check($"{fs,5:0} Hz: sigma of a 1 s mean {sigmaMean:0.00000} vs density/sqrt(2) {wantMean:0.00000} (rate-free)",
                      System.Math.Abs(sigmaMean / wantMean - 1.0) < 0.05);
            }
        }

        private static void ChannelsDriftIndependently()
        {
            const float rate = 0.02f, dt = 0.01f, T = 10f;
            const int trials = 600;
            int steps = Mathf.RoundToInt(T / dt);
            double[] sumSq = new double[3];
            double sumXY = 0;
            NoiseModel.ResetOrdinals();
            for (int k = 0; k < trials; k++)
            {
                var nm = new NoiseModel { driftRate = rate };
                float x = 0, y = 0, z = 0;
                for (int i = 0; i < steps; i++)
                {
                    x = nm.Apply(0f, dt, 0);
                    y = nm.Apply(0f, dt, 1);
                    z = nm.Apply(0f, dt, 2);
                }
                sumSq[0] += x * x; sumSq[1] += y * y; sumSq[2] += z * z;
                sumXY += x * y;
            }
            double want = rate * System.Math.Sqrt(T);
            for (int c = 0; c < 3; c++)
            {
                double got = System.Math.Sqrt(sumSq[c] / trials);
                Check($"channel {c}: drift std after {T:0} s {got:0.0000} vs rate*sqrt(T) {want:0.0000}",
                      System.Math.Abs(got / want - 1.0) < 0.12);
            }
            double corr = sumXY / System.Math.Sqrt(sumSq[0] * sumSq[1]);
            Check($"channels 0 and 1 drift independently (correlation {corr:+0.000;-0.000})",
                  System.Math.Abs(corr) < 0.12);
        }

        private static void LegacyStreamUnchanged()
        {
            // The pre-SEN-07 arithmetic, re-implemented: v = x + bias + N(0,1)*sigma
            // from the same seeded Box-Muller stream. A σ-only model must match it.
            // Seeds are handed out on first draw, so draw each stream whole after
            // its own ordinal reset: both instances then get ordinal 0.
            const int n = 1000;
            var got = new float[n];
            NoiseModel.ResetOrdinals();
            var nm = new NoiseModel { noiseStdDev = 0.05f, bias = 0.1f };
            for (int i = 0; i < n; i++) got[i] = nm.Apply(1f, 0.01f);
            NoiseModel.ResetOrdinals();
            var reference = new NoiseModel { noiseStdDev = 1f };
            bool same = true;
            for (int i = 0; i < n && same; i++)
            {
                float want = 1f + 0.1f + reference.Apply(0f) * 0.05f;
                if (Mathf.Abs(got[i] - want) > 1e-5f) same = false;
            }
            Check("a sigma-only model draws the same stream with or without dt", same);
        }

        private static void Check(string what, bool ok)
        {
            _checks++;
            if (!ok) _failed++;
            _log.AppendLine($"{Tag} {(ok ? "ok  " : "FAIL")} {what}");
        }
    }
}
