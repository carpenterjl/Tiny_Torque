using System.Text;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[TTBENCH] — the tyre thermal model's bench check.</b> No scene, no
    /// rigidbody, no play mode: it calls <see cref="TyreThermal"/> directly and
    /// compares each answer against one worked by hand.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.TyreThermalBench.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <b>The check that matters most is the last one.</b> A thermal state that
    /// feeds back into grip is exactly the kind of thing that makes a physics
    /// model timestep-dependent, and <c>P9</c> re-runs every manoeuvre at 200, 400
    /// and 800 Hz demanding half a percent. Rather than discover that in a
    /// twenty-minute scene test, the integrator is swept here in milliseconds —
    /// and if it ever stops converging, this says so before a single car has been
    /// built.
    /// </summary>
    public static class TyreThermalBench
    {
        private const string Tag = "[TTBENCH]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [TTBENCH] Tyre Thermal Bench", priority = 122)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            Identities();
            GripCurve();
            PressureEffects();
            Thermal();
            TimestepConvergence();

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        /// <summary>
        /// The three places the model must be EXACTLY neutral.
        ///
        /// Tolerance zero on all of them, deliberately. A tyre sitting in the pits
        /// at the pressure someone typed in must read back that pressure and no
        /// penalty; if any of these drifts by a float ulp, then "set it to the
        /// optimum and it is unaffected" becomes "set it to the optimum and it is
        /// almost unaffected", which is not a thing anyone can tune against.
        /// </summary>
        private static void Identities()
        {
            Check("pressure at ambient", TyreThermal.RunningPressureKpa(180f, TyreThermal.AmbientC),
                  180f, 0f, "kPa", "the gas law's ratio is exactly 1 at the reference temperature");
            Check("grip penalty at the optimum", TyreThermal.GripVsPressure(TyreThermal.PressOptKpa),
                  1f, 0f, "", "a correctly inflated tyre is judged on temperature alone");
            Check("rolling scale at the optimum", TyreThermal.RollResistScale(TyreThermal.PressOptKpa),
                  1f, 0f, "", "√1");
            Check("radius scale at the optimum", TyreThermal.RadiusScale(TyreThermal.PressOptKpa),
                  1f, 0f, "", "odometry must not move for a correctly inflated tyre");
            Check("balloon damping at the optimum", TyreThermal.BalloonDamp(TyreThermal.PressOptKpa),
                  1f, 0f, "", "balloonPct was measured at this pressure");
        }

        private static void GripCurve()
        {
            Check("grip at −10 °C", TyreThermal.GripVsTemp(-10f), 0.80f, 0f, "", "frozen: the floor");
            Check("grip at 0 °C", TyreThermal.GripVsTemp(0f), 0.80f, 0f, "", "the cold end");
            Check("grip at 25 °C (ambient)", TyreThermal.GripVsTemp(25f), 0.92f, 1e-6f, "",
                  "a cold start gives up 8 %");
            Check("grip at 40 °C", TyreThermal.GripVsTemp(40f), 1.00f, 0f, "", "window opens");
            Check("grip at 55 °C", TyreThermal.GripVsTemp(55f), 1.00f, 0f, "", "mid-window");
            Check("grip at 70 °C", TyreThermal.GripVsTemp(70f), 1.00f, 0f, "", "window closes");
            Check("grip at 85 °C", TyreThermal.GripVsTemp(85f), 0.925f, 1e-6f, "",
                  "halfway down the overheat ramp");
            Check("grip at 100 °C", TyreThermal.GripVsTemp(100f), 0.85f, 1e-6f, "", "greasy");
            Check("grip at 200 °C", TyreThermal.GripVsTemp(200f), 0.70f, 0f, "", "the hot floor");

            // Monotone up to the window and monotone down after it. Stated as a
            // sweep rather than as three more points, because a curve that dips in
            // between would pass every point check and still make a car that gets
            // slower as it warms up.
            float worstUp = 0f, worstDown = 0f;
            for (float t = -20f; t < 40f; t += 0.25f)
                worstUp = Mathf.Min(worstUp, TyreThermal.GripVsTemp(t + 0.25f) - TyreThermal.GripVsTemp(t));
            for (float t = 70f; t < 160f; t += 0.25f)
                worstDown = Mathf.Max(worstDown, TyreThermal.GripVsTemp(t + 0.25f) - TyreThermal.GripVsTemp(t));
            Check("no dip while warming", worstUp, 0f, 1e-7f, "", "monotone from −20 to 40 °C");
            Check("no rise while overheating", worstDown, 0f, 1e-7f, "", "monotone from 70 to 160 °C");

            // Nothing may exceed the plateau. A thermal model that hands out MORE
            // grip than a car with no thermal model would turn opting in into a
            // performance choice rather than a realism one.
            float peak = 0f;
            for (float t = -50f; t <= 250f; t += 0.25f) peak = Mathf.Max(peak, TyreThermal.GripVsTemp(t));
            Check("peak grip multiplier", peak, 1f, 0f, "",
                  "warm equals unmodelled — never better");
        }

        private static void PressureEffects()
        {
            // 180 kPa cold, at 70 °C: 180 × 343.15/298.15.
            float hot = TyreThermal.RunningPressureKpa(180f, 70f);
            Check("pressure at 70 °C", hot, 207.16f, 0.05f, "kPa", "180 × 343.15/298.15");
            Line($"180 kPa cold reads {hot:0.0} kPa hot — {(hot / 180f - 1f) * 100f:0.0} % up");

            Check("grip at +20 % pressure", TyreThermal.GripVsPressure(216f), 0.98f, 1e-5f, "",
                  "1 − 0.5·0.2²");
            Check("grip at −20 % pressure", TyreThermal.GripVsPressure(144f), 0.98f, 1e-5f, "",
                  "symmetric — over and under are both wrong");
            Check("grip penalty floor", TyreThermal.GripVsPressure(20f), 0.85f, 0f, "",
                  "badly inflated is worse, not useless");

            Check("rolling at 120 kPa", TyreThermal.RollResistScale(120f), 1.2247f, 1e-3f, "",
                  "√(180/120): a soft tyre costs more to roll");
            Check("rolling at 240 kPa", TyreThermal.RollResistScale(240f), 0.8660f, 1e-3f, "",
                  "√(180/240)");
            Check("rolling scale floor", TyreThermal.RollResistScale(9000f), 0.7f, 0f, "", "clamped");
            Check("rolling scale ceiling", TyreThermal.RollResistScale(1f), 1.4f, 0f, "", "clamped");

            // The self-cancelling loop, checked as a direction rather than a value:
            // a soft tyre drags more, drag makes heat, heat raises pressure, and
            // the higher pressure takes some of the drag back.
            float coldSoft = TyreThermal.RunningPressureKpa(120f, TyreThermal.AmbientC);
            float hotSoft = TyreThermal.RunningPressureKpa(120f, 80f);
            Line($"120 kPa cold: rolling ×{TyreThermal.RollResistScale(coldSoft):0.000} cold, " +
                 $"×{TyreThermal.RollResistScale(hotSoft):0.000} at 80 °C");
            Greater("heat recovers some rolling loss",
                    TyreThermal.RollResistScale(coldSoft), TyreThermal.RollResistScale(hotSoft),
                    1e-4f, "pressure rises with temperature and the tyre stiffens");
        }

        private static void Thermal()
        {
            float c = TyreThermal.HeatCapacityJPerK(0.05f);
            Check("heat capacity, stock RC wheel", c, 15f, 1e-4f, "J/K", "0.4 × 0.05 kg × 750");
            Check("heat capacity, 0 sentinel", TyreThermal.HeatCapacityJPerK(0f), c, 0f, "J/K",
                  "the same 0.05 kg CarVehicle falls back to — a capacity of zero divides");

            Check("cooling at rest", TyreThermal.CoolingWPerK(0f), 0.15f, 1e-6f, "W/K", "h0");
            Check("cooling at 8 m/s", TyreThermal.CoolingWPerK(8f), 0.55f, 1e-6f, "W/K",
                  "0.15 + 0.05 × 8");

            // Equilibrium: T settles where the heat in equals the heat out, which
            // is ambient + Q/h and does NOT depend on the heat capacity. That
            // independence is worth having as a check, because it is what lets the
            // two constants be tuned one at a time — h sets where a tyre ends up, C
            // sets how long it takes to get there.
            const float q = 6f, v = 8f, dt = 1f / 400f;
            float expectEq = TyreThermal.AmbientC + q / TyreThermal.CoolingWPerK(v);
            float t = TyreThermal.AmbientC;
            for (int i = 0; i < 400 * 600; i++) t = TyreThermal.Step(t, q, v, c, dt);
            Check("equilibrium at 6 W, 8 m/s", t, expectEq, expectEq * 1e-3f, "°C",
                  "ambient + Q/h(v) — independent of heat capacity");
            Line($"6 W of slip at 8 m/s settles at {t:0.0} °C; " +
                 $"a hard drift at 22 W would reach " +
                 $"{TyreThermal.AmbientC + 22f / TyreThermal.CoolingWPerK(v):0.0} °C");

            float eqBigger = TyreThermal.AmbientC;
            float cBig = TyreThermal.HeatCapacityJPerK(0.5f);
            for (int i = 0; i < 400 * 6000; i++) eqBigger = TyreThermal.Step(eqBigger, q, v, cBig, dt);
            // Quarter of a degree, not a tenth of a percent, and the reason is
            // worth writing down: an explicit integrator in SINGLE precision parks
            // a short way below its fixed point, because the last increments fall
            // under a float ulp and stop accumulating. A ten-times heavier tyre
            // takes ten-times smaller steps and so stalls ten-times further out —
            // 0.19 °C here against 0.02 °C for a real one. Harmless (the tyre is
            // at 35.7 instead of 35.9), and NOT worth chasing with a double: the
            // whole engine runs single-precision, and a thermal state that alone
            // did not would be a strange thing to explain.
            Check("equilibrium ignores capacity", eqBigger, t, 0.25f, "°C",
                  "a ten-times heavier tyre settles in the same place, later");
            Line($"capacity {c:0} J/K settles at {t:0.000} °C, {cBig:0} J/K at {eqBigger:0.000} °C " +
                 $"(analytic {expectEq:0.000}) — the gap is float resolution, not the model");

            // Time constant: C/h, checked at the 63.2 % point of the step response.
            float tau = c / TyreThermal.CoolingWPerK(v);
            float warm = TyreThermal.AmbientC;
            int steps = Mathf.RoundToInt(tau / dt);
            for (int i = 0; i < steps; i++) warm = TyreThermal.Step(warm, q, v, c, dt);
            float reached = (warm - TyreThermal.AmbientC) / (expectEq - TyreThermal.AmbientC);
            Check("warm-up at one time constant", reached, 0.6321f, 2e-3f, "",
                  $"τ = C/h = {tau:0.0} s — a first-order lag, checked as one");

            // Cooling has to work too, or a tyre that overheats once stays hot.
            // Parked, h is only 0.15 W/K, so τ is a hundred seconds and ten
            // minutes is six of them — 0.25 % of the way from 90 °C is where a
            // first-order lag genuinely is at 6τ, and the float band above closes
            // the rest. Checked as "cooled to within a quarter degree and never
            // undershot", which is the honest claim.
            float cool = 90f;
            for (int i = 0; i < 400 * 600; i++) cool = TyreThermal.Step(cool, 0f, 0f, c, dt);
            Check("parked tyre cools to ambient", cool, TyreThermal.AmbientC, 0.25f, "°C",
                  "no heat in, so the only fixed point is ambient (10 min ≈ 6τ)");
            _checks++;
            if (cool < TyreThermal.AmbientC - 1e-4f)
            {
                _failed++;
                Debug.LogError($"{Tag} FAIL a cooling tyre went BELOW ambient ({cool:0.000} °C) " +
                               "— the integrator is overshooting its own fixed point");
            }
        }

        /// <summary>
        /// <b>P9's question, asked here where it is cheap.</b> The same twenty
        /// minutes of heating and cooling, integrated at 200, 400 and 800 Hz. If
        /// the three disagree, the thermal model has made the whole car's physics
        /// depend on the physics rate, and every timestep-sensitivity result
        /// downstream becomes a measurement of this file.
        /// </summary>
        private static void TimestepConvergence()
        {
            float c = TyreThermal.HeatCapacityJPerK(0.05f);
            float[] outs = new float[3];
            int[] rates = { 200, 400, 800 };
            for (int k = 0; k < rates.Length; k++)
            {
                float dt = 1f / rates[k];
                float t = TyreThermal.AmbientC;
                int n = rates[k] * 120;
                // A duty cycle rather than a constant: heat for a while, coast for
                // a while. A constant input converges trivially; the transitions
                // are where an integrator with a rate problem shows it.
                for (int i = 0; i < n; i++)
                {
                    bool driving = (i / (rates[k] * 10)) % 2 == 0;
                    t = TyreThermal.Step(t, driving ? 18f : 0f, driving ? 9f : 2f, c, dt);
                }
                outs[k] = t;
                Line($"duty-cycled 120 s at {rates[k]} Hz → {t:0.0000} °C");
            }

            float spread = (Mathf.Max(outs[0], Mathf.Max(outs[1], outs[2]))
                          - Mathf.Min(outs[0], Mathf.Min(outs[1], outs[2])))
                          / Mathf.Max(1e-6f, outs[1]);
            Check("timestep spread, 200/400/800 Hz", spread, 0f, 0.005f, "",
                  "P9's own tolerance, met before any car exists");
        }

        // ---- harness ----------------------------------------------------------------

        private static void Check(string name, float got, float expect, float tol,
                                  string units, string why)
        {
            _checks++;
            bool ok = Mathf.Abs(got - expect) <= tol;
            if (!ok) _failed++;

            string line = $"{(ok ? "ok  " : "FAIL")} {name,-34} {got,10:0.#####} {units,-4}"
                          + $" (expect {expect:0.#####} ±{tol:0.#####})  — {why}";
            if (ok) Line(line);
            else Debug.LogError($"{Tag} {line}");
        }

        private static void Greater(string name, float bigger, float smaller, float margin,
                                    string why)
        {
            _checks++;
            bool ok = bigger > smaller + margin;
            string line = $"{(ok ? "ok  " : "FAIL")} {name,-34} {bigger:0.#####} > " +
                          $"{smaller:0.#####}  — {why}";
            if (ok) Line(line);
            else { _failed++; Debug.LogError($"{Tag} {line}"); }
        }

        private static void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
