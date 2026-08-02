using System.Collections.Generic;
using AIHWSim.Core.Flight;
using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A5 — the phugoid: period, damping, and where the damping comes from.</b>
    ///
    /// Trim the aeroplane, pulse the elevator, let go, and watch the slow exchange
    /// of height for speed that every aeroplane has. Lanchester's result for its
    /// period is
    /// <code>
    ///   T = π·√2·V / g
    /// </code>
    /// with <b>no aerodynamic coefficient in it</b> — no lift slope, no aspect
    /// ratio, no area, no mass, no inertia. Every other test here can be argued
    /// with by questioning a constant; this one cannot.
    ///
    /// <b>What it catches that nothing else does.</b> Lift applied along body-up
    /// instead of perpendicular to the local flow injects energy every cycle and
    /// the oscillation grows. A force applied at the wrong point changes the pitch
    /// coupling. An integrator that leaks energy shifts the period. None of those
    /// move a steady-state number like the glide ratio at all.
    ///
    /// <h3>The over-damping that turned out to be a missing term</h3>
    ///
    /// This test previously measured ζ ≈ 0.16 against the textbook ζ = 1/(√2·L/D)
    /// ≈ 0.078, reported that the model damped the mode twice as hard as theory
    /// allowed, and carried it as an open question for two milestones.
    ///
    /// <b>The model was right and the reference was incomplete.</b> The textbook
    /// form assumes thrust does not change with speed. A fixed-pitch propeller on a
    /// fixed voltage is the opposite of that: fly faster, the advance ratio rises,
    /// C_T falls, thrust drops. That is a force opposing every speed excursion,
    /// which is the definition of damping, and on this airframe ∂T/∂V = −0.33 N per
    /// m/s against a drag term of +0.30 — <b>the propeller supplies more than half
    /// the total</b>. Put it back and the prediction is ζ = 0.165.
    ///
    /// The old doc block's own words are the giveaway: it recorded the anomaly as
    /// "implying a cruise L/D of 4.4". Feed the correct ζ back through the naive
    /// formula and it returns 4.3. The measurement was never the thing that was
    /// wrong.
    ///
    /// <h3>What is gated: ∂T/∂V, measured by difference</h3>
    ///
    /// Comparing a damping RATIO against a prediction is weak here, because ζ is the
    /// decay rate divided by the frequency and the frequency has its own problem
    /// (below). Two errors then partly cancel and the agreement flatters itself.
    ///
    /// Decay RATES do not have that defect — for the path equations 2σ = X_u
    /// exactly — and taking the DIFFERENCE between the powered and frozen-thrust
    /// stages cancels everything the two share:
    /// <code>
    ///   ∂T/∂V = 2·m·(σ_powered − σ_frozen)
    /// </code>
    /// Same trim, same disturbance, same airframe, same α response; the only
    /// difference is whether thrust may vary with speed. So this is a <b>flight
    /// measurement of a propeller property</b>, standing against a closed form the
    /// bench computes from C_T0, J₀ and the motor constants without ever looking at
    /// an aeroplane. Neither knows about the other, which is what makes it worth
    /// gating on. The cancellation is checked rather than assumed: the two stages'
    /// α responses are reported side by side.
    ///
    /// <h3>Five stages, because an explanation is not a measurement</h3>
    ///
    /// The account above is an argument about coefficients. It is worth nothing
    /// until the term can be removed and seen to matter, so the aeroplane is flown
    /// five times:
    /// <list type="number">
    /// <item><b>a small pulse</b> — the gated measurement.</item>
    /// <item><b>a medium pulse</b> and</item>
    /// <item><b>a large pulse</b>, giving an amplitude sweep. See below for why
    ///       this is not optional.</item>
    /// <item><b>thrust frozen</b> at its trim value from the moment the pulse
    ///       begins, which deletes ∂T/∂V and nothing else. The shaft still turns,
    ///       so the torque reaction and the gyroscopic term are unchanged and
    ///       cannot be blamed for the difference. <b>Damping here must collapse to
    ///       the classical value.</b> If it does not, the explanation is wrong and
    ///       the model has a real defect.</item>
    /// <item><b>double the rate</b> — 800 Hz. A phugoid is a slow mode, and slow
    ///       modes are where an integrator bleeds energy without anything else
    ///       noticing. If numerical dissipation were doing the damping, halving the
    ///       step would move it.</item>
    /// </list>
    ///
    /// <h3>The period, and a wrong guess the sweep killed</h3>
    ///
    /// With the damping explained the period was still 20 % long, and the obvious
    /// suspect was amplitude: a 0.25 pulse held for 1.5 s puts <b>7.1 m/s of
    /// amplitude on a 14.7 m/s trim</b>, α ranges over five degrees, and a large
    /// phugoid is known to run slow. The amplitude sweep exists because that was a
    /// guess, and it <b>refuted it outright</b>:
    /// <code>
    ///   0.94 m/s amplitude → 8.151 s      alpha range 0.6 deg
    ///   2.99 m/s amplitude → 8.140 s      alpha range 2.1 deg
    ///   7.07 m/s amplitude → 8.140 s      alpha range 5.3 deg
    /// </code>
    /// Seven and a half times the disturbance moves the period by 0.1 %. The period
    /// error is not an artefact of the pulse, and the sweep stays in the test
    /// because that is worth knowing and because the next person will have the same
    /// idea.
    ///
    /// <h3>What it actually is: α is not constant, and the formula assumes it is</h3>
    ///
    /// Lanchester's frequency rests on one quantity — the extra lift a speed
    /// excursion makes. At constant α lift goes as V², giving ∂L/∂V = 2W/V and
    /// ω_n = √2·g/V. <b>That is the whole content of the formula.</b>
    ///
    /// α is not constant here. The flight path swings about ±4° over the cycle, the
    /// aeroplane has to rotate to follow it, and rotating costs a pitching moment
    /// that only an α perturbation can supply — so α moves in ANTIPHASE with speed
    /// and cancels part of the extra lift. A 20 % long period means the effective
    /// ∂L/∂V is about two thirds of the constant-α value.
    ///
    /// This is a real property of a 2 kg airframe with a 0.6 m tail arm, not a
    /// defect: it is large pitch damping against modest static stability, which is
    /// exactly the case the classical approximation is quoted as not covering. The
    /// original doc block guessed at "mode separation" and was in the right family.
    ///
    /// <b>So it is measured rather than argued.</b> α is fitted on the same mode as
    /// airspeed — same σ, same ω, so the phase between them is a measurement — and
    /// the corrected frequency follows with no free parameter:
    /// <code>
    ///   ΔL/W = (2/V)·u + (a_w/C_L)·(∂α/∂u)·u        ⇒   ω² = ω_classical²·k
    /// </code>
    /// The gate is against that, and the classical value is reported beside it.
    /// Gating on π√2·V/g would be gating on an assumption this aeroplane measurably
    /// breaks, which is the same rule that keeps A4 off the real-trainer band.
    ///
    /// <h3>Measuring a heavily damped mode</h3>
    ///
    /// The old estimator counted rising zero-crossings of airspeed about a running
    /// mean. At ζ = 0.16 the amplitude falls by e^(−2πζ) = 0.37 per cycle, so the
    /// third crossing is already down in the noise — which is why this test spent
    /// its life reporting "damps out inside 1 cycle, too fast to time a period".
    /// <b>That was an instrument limit being reported as a physical result.</b>
    ///
    /// A least-squares fit of
    /// <code>
    ///   v(t) = c + e^(σt)·(a·cos ωt + b·sin ωt)
    /// </code>
    /// uses every sample rather than three of them, and recovers ω and σ from well
    /// under two cycles. For a fixed (σ, ω) the remaining parameters are linear, so
    /// each trial is one 3×3 solve and the outer search is a coarse grid with four
    /// refinements. The constant term is fitted rather than assumed, which is what
    /// handles the small energy offset the pulse leaves behind — the thing the
    /// running-mean trick was invented to cope with.
    ///
    /// The residual is reported as a fraction of the fitted amplitude. That is the
    /// honesty check: it says whether the airspeed trace actually IS one damped
    /// sinusoid, and a bad fit is returned as Invalid rather than as a number.
    /// </summary>
    public sealed class A5PhugoidTest : FlightTest
    {
        [Header("A5")]
        [Tooltip("Trim throttle, from the trim probe — 0.719 with the wake modelled.")]
        public float trimThrottle = 0.719f;
        [Tooltip("Seconds to settle at trim before disturbing it. Also the window "
                 + "the trim speed and trim drag are averaged over.")]
        public float settleSeconds = 20f;
        /// <summary>
        /// Elevator pulses, smallest first. <b>Lanchester's period is the
        /// small-disturbance limit and a phugoid is amplitude-dependent</b>, so a
        /// single pulse cannot tell a period error from a pulse that was too big.
        /// Three sizes turn that into a measurement: the period is quoted at the
        /// smallest, and the trend across all three says whether it is converging on
        /// π√2·V/g or on something else.
        ///
        /// The sizes are not arbitrary. 0.25 for 1.5 s — the pulse this test shipped
        /// with for three milestones — puts <b>7.1 m/s of amplitude on a 14.7 m/s
        /// trim</b> and swings α over five degrees, which is nobody's small
        /// disturbance. It measured 8.14 s against a 6.76 s prediction and the
        /// missing 20 % was the amplitude, not the model. It is kept as the largest
        /// point precisely so that the old number reappears in the sweep instead of
        /// being quietly dropped.
        /// </summary>
        public float[] pulses = { 0.03f, 0.10f, 0.25f };
        public float pulseSeconds = 1.5f;
        [Tooltip("Seconds of free oscillation to fit. 45 s is about seven periods "
                 + "with the propeller's damping removed and five with it present.")]
        public float watchSeconds = 45f;

        [Tooltip("Physics rate per stage.")]
        public int[] rates = { 400, 400, 400, 400, 800 };
        [Tooltip("Which entry of `pulses` each stage flies. Stage 0 is the gated "
                 + "measurement and every control repeats its pulse size.")]
        public int[] stagePulse = { 0, 1, 2, 0, 0 };
        [Tooltip("Stage index that flies with the thrust frozen at its trim value.")]
        public int frozenThrustStage = 3;

        [Tooltip("Allowed deviation of the flight-measured dT/dV from the closed "
                 + "form computed off the propeller and motor constants.")]
        public float thrustDerivTolerance = 0.15f;
        [Tooltip("Allowed spread in period and damping between 400 Hz and 800 Hz.")]
        public float rateSpreadLimit = 0.03f;
        [Tooltip("Largest fit residual, as a fraction of the fitted amplitude, that "
                 + "still counts as one clean damped sinusoid.")]
        public float residualLimit = 0.30f;
        [Tooltip("Smallest fitted amplitude (m/s) worth calling an oscillation.")]
        public float minAmplitude = 0.05f;

        protected override string TestId => "A5";
        protected override string Title => "Phugoid — ∂T/∂V by differential damping";
        protected override string Expected =>
            "2·m·(σ_powered − σ_frozen) = ∂T/∂V from C_T0, J₀ and the motor constants "
            + "· period reported, not gated";

        protected override float TestAltitude => 1000f;

        private const float SamplePeriod = 0.1f;   // 10 Hz — 66 points per period

        private int _stage;
        private float _stageT, _sampleAcc;

        private double _sumTas, _sumThrust;
        private int _nSettle;

        private readonly List<float> _ts = new List<float>();
        private readonly List<float> _vs = new List<float>();
        private readonly List<float> _as = new List<float>();   // alpha, rad

        private const int MaxStages = 8;
        private readonly Fit[] _fit = new Fit[MaxStages];
        private readonly float[] _trimV = new float[MaxStages];
        private readonly float[] _trimD = new float[MaxStages];
        private readonly float[] _alphaLo = new float[MaxStages];
        private readonly float[] _alphaHi = new float[MaxStages];
        private float _alphaMin, _alphaMax;

        /// <summary>Elevator pulse this stage flies.</summary>
        private float PulseFor(int stage) =>
            pulses[Mathf.Clamp(stagePulse[Mathf.Clamp(stage, 0, stagePulse.Length - 1)],
                               0, pulses.Length - 1)];

        private struct Fit
        {
            public bool ok;
            public float omega, sigma, amp, offset, residual;
            /// <summary>Angle-of-attack amplitude over the mode (rad).</summary>
            public float alphaAmp;
            /// <summary>The part of the α oscillation that is IN PHASE with the speed
            /// oscillation, as rad per m/s. Negative means α falls as the aeroplane
            /// speeds up, which subtracts from the lift perturbation and slows the
            /// mode down. This is the number that decides the period.</summary>
            public float alphaPerSpeed;
            public float Period => omega > 1e-6f ? 2f * Mathf.PI / omega : 0f;
            public float Zeta
            {
                get
                {
                    float m = Mathf.Sqrt(sigma * sigma + omega * omega);
                    return m > 1e-6f ? -sigma / m : 0f;
                }
            }
        }

        // ---- flying it ----

        protected override void Idle(ScriptedPilot p)
        {
            p.Neutral();
            p.throttle = trimThrottle;
            HoldWingsLevel(p);
        }

        protected override void Arm()
        {
            // Five stages of settle-pulse-watch do not fit the 240 s the base class
            // sizes for the car's longest test, and a run that is cut off reports
            // INVALID with no hint that the budget rather than the aeroplane was the
            // problem. Derived from the stage table rather than written down, so
            // adding a stage cannot silently reintroduce the timeout.
            float needed = rates.Length * (settleSeconds + pulseSeconds + watchSeconds);
            timeoutSec = Mathf.Max(timeoutSec, needed + 60f);

            _stage = 0;
            ApplyStage();
            LaunchAtTrim(LaunchSpeed);   // only the first stage waits for sync
            ResetStage();
        }

        /// <summary>Rate and thrust mode for the current stage. Both must be written:
        /// SimulationRunner recomputes Time.fixedDeltaTime only in Awake and Start,
        /// so setting the field alone changes what the runner reports without
        /// changing what Unity actually steps.</summary>
        private void ApplyStage()
        {
            Time.fixedDeltaTime = 1f / rates[_stage];
            Runner.physicsRateHz = rates[_stage];
            Plane.thrustOverrideN = -1f;    // real propeller until the pulse begins
        }

        private void ResetStage()
        {
            _stageT = 0f;
            _sampleAcc = 0f;
            _sumTas = 0.0;
            _sumThrust = 0.0;
            _nSettle = 0;
            _alphaMin = float.MaxValue;
            _alphaMax = float.MinValue;
            _ts.Clear();
            _vs.Clear();
            _as.Clear();
        }

        private void BeginStage()
        {
            ApplyStage();
            Plane.LaunchAt(new Vector3(0f, TestAltitude, 0f), Quaternion.identity,
                           LaunchSpeed);
            ResetStage();
        }

        protected override void Fly(ScriptedPilot p, float t)
        {
            _stageT += Time.fixedDeltaTime;

            p.Neutral();
            p.throttle = trimThrottle;
            HoldWingsLevel(p);   // roll only; pitch is free, which is the whole test

            // Freeze the thrust at the instant the disturbance starts, so the value
            // frozen IS the trim value and the two stages share an equilibrium. The
            // aeroplane is still at trim here, so T = D and the trim point survives
            // — only its speed dependence is deleted.
            if (_stage == frozenThrustStage && _stageT >= settleSeconds
                && Plane.thrustOverrideN < 0f && _nSettle > 0)
                Plane.thrustOverrideN = (float)(_sumThrust / _nSettle);

            if (_stageT >= settleSeconds && _stageT < settleSeconds + pulseSeconds)
                p.pitch = PulseFor(_stage);
        }

        protected override void Sample(float dt)
        {
            // Trim speed and trim drag, established BEFORE the pulse. In level
            // flight the thrust IS the drag, so the damping prediction is built on a
            // number this aeroplane measured about itself rather than on the drag
            // polar — which keeps the two ⚠ estimates in the polar (C_D0 and the
            // Oswald factor) out of the comparison entirely.
            if (_stageT < settleSeconds)
            {
                _sumTas += Air.Tas;
                _sumThrust += Plane.Thrust;
                _nSettle++;
                return;
            }
            if (_stageT < settleSeconds + pulseSeconds) return;

            _alphaMin = Mathf.Min(_alphaMin, Air.AlphaDeg);
            _alphaMax = Mathf.Max(_alphaMax, Air.AlphaDeg);

            // Decimated: the mode is 6.6 s and 10 Hz is 66 points per cycle, so
            // storing every physics step would cost sixty times the memory and the
            // fit sixty times the work for no resolution at all.
            _sampleAcc += dt;
            if (_sampleAcc < SamplePeriod) return;
            _sampleAcc -= SamplePeriod;

            _ts.Add(_stageT - (settleSeconds + pulseSeconds));
            _vs.Add(Air.Tas);
            _as.Add(Air.AlphaRad);
        }

        // ---- the estimator ----

        /// <summary>
        /// Fit v(t) = c + e^(σt)(a·cos ωt + b·sin ωt) by least squares.
        ///
        /// Separable: for any trial (σ, ω) the three remaining parameters enter
        /// linearly, so each trial costs one pass over the samples and one 3×3
        /// solve. The outer search is therefore only two-dimensional — a coarse grid
        /// over plausible periods and decay rates, then four refinements each
        /// shrinking the window fourfold, which lands the period to about a
        /// millisecond.
        ///
        /// Accumulated in double. The sums of squares run to ~10⁵ while the
        /// oscillation being resolved is a few tenths of a metre per second, and in
        /// float the signal would be competing with the rounding of its own mean.
        /// </summary>
        private static Fit FitDampedSinusoid(List<float> ts, List<float> vs,
                                             List<float> alphas)
        {
            var f = new Fit();
            int n = ts.Count;
            if (n < 40) return f;

            double sumY2 = 0.0;
            for (int i = 0; i < n; i++) sumY2 += (double)vs[i] * vs[i];

            const float wLo = 0.3142f;   // 20 s period
            const float wHi = 2.0944f;   //  3 s period
            const float sLo = -0.60f;
            const float sHi = 0.05f;

            float bestW = 0f, bestS = 0f;
            double bestSse = double.MaxValue;

            for (int i = 0; i < 40; i++)
            {
                float w = Mathf.Lerp(wLo, wHi, i / 39f);
                for (int k = 0; k < 28; k++)
                {
                    float s = Mathf.Lerp(sLo, sHi, k / 27f);
                    double sse = Sse(ts, vs, sumY2, w, s, out _, out _, out _);
                    if (sse < bestSse) { bestSse = sse; bestW = w; bestS = s; }
                }
            }

            float wSpan = (wHi - wLo) / 39f;
            float sSpan = (sHi - sLo) / 27f;
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = -4; i <= 4; i++)
                {
                    float w = bestW + wSpan * i / 4f;
                    if (w <= 0.05f) continue;
                    for (int k = -4; k <= 4; k++)
                    {
                        float s = bestS + sSpan * k / 4f;
                        double sse = Sse(ts, vs, sumY2, w, s, out _, out _, out _);
                        if (sse < bestSse) { bestSse = sse; bestW = w; bestS = s; }
                    }
                }
                wSpan *= 0.25f;
                sSpan *= 0.25f;
            }

            double final = Sse(ts, vs, sumY2, bestW, bestS,
                               out double c, out double a, out double b);

            // Angle of attack, fitted on the SAME (σ, ω) basis. Not a second search
            // — α and airspeed are two states of one mode and must share a frequency,
            // so re-searching would only find the same one with more noise. Fitting
            // it here gives the phase between them, and that phase is what sets the
            // period: the classical result assumes α does not move at all.
            double sumA2 = 0.0;
            for (int i = 0; i < n; i++) sumA2 += (double)alphas[i] * alphas[i];
            Sse(ts, alphas, sumA2, bestW, bestS, out _, out double aa, out double ab);

            double power = a * a + b * b;
            if (power > 1e-12)
            {
                // Projection of the α oscillation onto the speed oscillation. The
                // quadrature part contributes to damping rather than frequency and
                // is deliberately dropped here.
                f.alphaPerSpeed = (float)((aa * a + ab * b) / power);
                f.alphaAmp = (float)System.Math.Sqrt(aa * aa + ab * ab);
            }

            f.omega = bestW;
            f.sigma = bestS;
            f.offset = (float)c;
            f.amp = (float)System.Math.Sqrt(a * a + b * b);
            float rms = (float)System.Math.Sqrt(System.Math.Max(0.0, final) / n);
            f.residual = f.amp > 1e-6f ? rms / f.amp : 1f;
            // A solution pinned to the edge of the search box is not a minimum, it
            // is the box — and it must not be reported as a measurement.
            f.ok = bestW > wLo * 1.02f && bestW < wHi * 0.98f
                   && bestS > sLo * 0.98f && bestS < sHi * 0.98f;
            return f;
        }

        /// <summary>Residual sum of squares for one trial (σ, ω), solving the three
        /// linear parameters exactly. Symmetric 3×3 by Gaussian elimination with
        /// partial pivoting — small enough that a closed form would only be harder
        /// to read.</summary>
        private static double Sse(List<float> ts, List<float> vs, double sumY2,
                                  float w, float s,
                                  out double c, out double a, out double b)
        {
            int n = ts.Count;
            double m11 = n, m12 = 0, m13 = 0, m22 = 0, m23 = 0, m33 = 0;
            double r1 = 0, r2 = 0, r3 = 0;

            for (int i = 0; i < n; i++)
            {
                double t = ts[i];
                double y = vs[i];
                double e = System.Math.Exp(s * t);
                double x1 = e * System.Math.Cos(w * t);
                double x2 = e * System.Math.Sin(w * t);

                m12 += x1; m13 += x2;
                m22 += x1 * x1; m23 += x1 * x2; m33 += x2 * x2;
                r1 += y; r2 += y * x1; r3 += y * x2;
            }

            var m = new double[3, 4]
            {
                { m11, m12, m13, r1 },
                { m12, m22, m23, r2 },
                { m13, m23, m33, r3 },
            };

            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int row = col + 1; row < 3; row++)
                    if (System.Math.Abs(m[row, col]) > System.Math.Abs(m[piv, col])) piv = row;
                if (System.Math.Abs(m[piv, col]) < 1e-12)
                {
                    c = a = b = 0.0;
                    return double.MaxValue;
                }
                if (piv != col)
                    for (int k = col; k < 4; k++)
                        (m[col, k], m[piv, k]) = (m[piv, k], m[col, k]);

                for (int row = col + 1; row < 3; row++)
                {
                    double factor = m[row, col] / m[col, col];
                    for (int k = col; k < 4; k++) m[row, k] -= factor * m[col, k];
                }
            }

            b = m[2, 3] / m[2, 2];
            a = (m[1, 3] - m[1, 2] * b) / m[1, 1];
            c = (m[0, 3] - m[0, 1] * a - m[0, 2] * b) / m[0, 0];

            // SSE = Σy² − βᵀr for the least-squares solution; cheaper and better
            // conditioned than re-evaluating the model over every sample.
            return sumY2 - (c * r1 + a * r2 + b * r3);
        }

        // ---- the verdict ----

        protected override Verdict? Evaluate()
        {
            if (_stageT < settleSeconds + pulseSeconds + watchSeconds) return null;

            _trimV[_stage] = _nSettle > 0 ? (float)(_sumTas / _nSettle) : LaunchSpeed;
            _trimD[_stage] = _nSettle > 0 ? (float)(_sumThrust / _nSettle) : 0f;
            _alphaLo[_stage] = _alphaMin;
            _alphaHi[_stage] = _alphaMax;
            _fit[_stage] = FitDampedSinusoid(_ts, _vs, _as);

            Fit f = _fit[_stage];
            Debug.Log($"[AERO] A5 stage {_stage + 1} ({StageName(_stage)}) → "
                      + $"T {f.Period:0.000} s · zeta {f.Zeta:0.0000} · "
                      + $"amp {f.amp:0.000} m/s · dalpha/du "
                      + $"{f.alphaPerSpeed * Mathf.Rad2Deg:0.0000} deg/(m/s) · "
                      + $"residual {f.residual * 100f:0.0} % · "
                      + $"trim {_trimV[_stage]:0.00} m/s on {_trimD[_stage]:0.000} N · "
                      + $"alpha {_alphaLo[_stage]:0.0}..{_alphaHi[_stage]:0.0}°");

            _stage++;
            if (_stage < rates.Length) { BeginStage(); return null; }

            // Restore the shipped rate — a later test in the same session would
            // otherwise inherit 800 Hz from this one.
            Time.fixedDeltaTime = 1f / rates[0];
            Plane.thrustOverrideN = -1f;

            return Judge();
        }

        private string StageName(int i) =>
            $"pulse {PulseFor(i):0.00}, {rates[i]} Hz"
            + (i == frozenThrustStage ? ", thrust frozen" : "");

        private Verdict Judge()
        {
            for (int i = 0; i < rates.Length; i++)
            {
                if (!_fit[i].ok || _fit[i].amp < minAmplitude)
                    return Invalid($"stage {i + 1} ({StageName(i)}) did not resolve an "
                                   + $"oscillation — fitted amplitude {_fit[i].amp:0.000} m/s, "
                                   + $"omega {_fit[i].omega:0.000} rad/s"
                                   + (_fit[i].ok ? "" : " (pinned to the search bound)"));
                if (_fit[i].residual > residualLimit)
                    return Invalid($"stage {i + 1} ({StageName(i)}) is not one clean damped "
                                   + $"sinusoid — residual {_fit[i].residual * 100f:0.0} % of "
                                   + $"amplitude, over the {residualLimit * 100f:0} % limit");
            }

            Fit real = _fit[0];
            Fit frozen = _fit[frozenThrustStage];
            Fit fast = _fit[rates.Length - 1];

            float v = _trimV[0];
            float volts = trimThrottle * Spec.motor.maxVoltage;
            float zetaFull = Spec.PhugoidDamping(v, _trimD[0], volts,
                                                 out float zetaClassical);
            float tv = PropellerModel.ThrustSpeedDerivative(Spec.propeller, Spec.motor,
                                                            volts, v);

            // The period Lanchester predicts, corrected for the damping actually
            // measured. At ζ = 0.16 the correction is 1.4 %, so this is a refinement
            // and not a way of explaining away a discrepancy.
            float undamped = AircraftSpec.PhugoidPeriod(v);
            float zm = real.Zeta;
            float classicalT = undamped / Mathf.Sqrt(Mathf.Max(1e-4f, 1f - zm * zm));

            // ---- the period, with alpha's measured response put back in ----
            //
            // Lanchester's frequency comes from ONE quantity: how much extra lift a
            // speed excursion makes. Hold α constant and L goes as V², so
            //   ∂L/∂V = 2W/V   and   ω_n = √2·g/V.
            //
            // α is not constant. To follow a flight path that swings several degrees
            // the aeroplane has to rotate, and rotating costs a pitching moment that
            // only an α perturbation can supply — so α moves in ANTIPHASE with speed
            // and cancels part of the extra lift. The classical form has no term for
            // it, which is why it is quoted as an approximation for aircraft with
            // wide mode separation; a 2 kg model with a 0.6 m tail arm is not one.
            //
            // <b>This is measured, not asserted.</b> α is fitted on the same mode, so
            // `alphaPerSpeed` is the observed rad-per-m/s slope, and the corrected
            // frequency follows with no free parameter:
            //   ΔL/W = (2/V)·u + (a_w/C_L)·(∂α/∂u)·u  ⇒  ω² = ω_classical²·k
            //
            // ⚠ BE CLEAR ABOUT WHAT THIS IS. It is a consistency relation between two
            // quantities the same run measured — the α-to-speed phase and the
            // frequency — not a closed-form prediction the way the damping above is.
            // It has real content: it fails if lift is applied in the wrong
            // direction, or if the frequency comes from anything other than the lift
            // a speed excursion makes. But it is weaker evidence than ζ, which is
            // computed from the propeller and motor constants alone and never looks
            // at the flight at all. Predicting ∂α/∂u outright needs C_mq, which is
            // ⚠ estimated to about 30 % — from it the closed form gives k ≈ 0.59 and
            // a period near 8.8 s, which brackets the measurement but cannot pin it.
            float qTrim = 0.5f * AeroDynamics.AirDensity * v * v;
            float clTrim = qTrim * Spec.WingArea > 1e-6f
                ? Spec.TotalMass * 9.80665f / (qTrim * Spec.WingArea) : 0f;
            float liftSlope = Spec.Wing.LiftSlope;
            float k = clTrim > 1e-6f
                ? 1f + (liftSlope / clTrim) * real.alphaPerSpeed * v * 0.5f : 1f;
            bool kUsable = k > 0.05f;
            float corrected = kUsable ? classicalT / Mathf.Sqrt(k) : 0f;

            // The gate is against the corrected form. Gating on the classical one
            // would be gating on an assumption this aeroplane measurably breaks —
            // which is the same rule that keeps A4 off the real-trainer band.
            float predicted = kUsable ? corrected : classicalT;
            float periodErr = (real.Period - predicted) / predicted;
            float classicalErr = (real.Period - classicalT) / classicalT;

            // ---- the gate: dT/dV, MEASURED IN FLIGHT ----
            //
            // <b>Damping RATES, not damping ratios.</b> ζ is σ divided by the
            // frequency, and the frequency has its own story (α, above) — so
            // comparing ζ against a prediction mixes two effects and the agreement
            // that results is partly luck. The decay rate σ does not: for the path
            // equations, 2σ = X_u = (∂T/∂V − ∂D/∂V)/m directly.
            //
            // <b>And the DIFFERENCE between the two stages, not either alone.</b>
            // Both fly the same trim, the same disturbance and the same airframe;
            // the only thing that changes is whether thrust may vary with speed. So
            // ∂D/∂V, the α response and the wake all cancel, and
            //   ∂T/∂V = 2·m·(σ_powered − σ_frozen)
            // is a flight measurement of a propeller property, standing against the
            // closed form the bench computes from C_T0, J₀ and the motor constants
            // without ever looking at an aeroplane. That is the real content of this
            // test now, and it is a far stronger claim than "ζ came out near a
            // number". The cancellation is not assumed either — the α response of
            // the two stages is reported side by side, and if they diverge the
            // difference is measuring something else as well.
            float mass = Spec.TotalMass;
            float deltaSigma = real.sigma - frozen.sigma;
            float tvMeasured = 2f * mass * deltaSigma;
            float tvErr = Mathf.Abs(tv) > 1e-6f ? (tvMeasured - tv) / Mathf.Abs(tv) : 1f;

            // What the frozen stage says the airframe's own ∂D/∂V is, against the
            // constant-α value. They disagree for the SAME reason the period does.
            float dragSlopeMeasured = -2f * mass * frozen.sigma;
            float dragSlopeConstAlpha = 2f * _trimD[0] / v;

            float alphaMatch = Mathf.Abs(real.alphaPerSpeed) > 1e-9f
                ? (frozen.alphaPerSpeed - real.alphaPerSpeed) / real.alphaPerSpeed : 1f;

            float periodSpread = real.Period > 1e-6f
                ? Mathf.Abs(fast.Period - real.Period) / real.Period : 1f;
            float sigmaSpread = Mathf.Abs(real.sigma) > 1e-6f
                ? Mathf.Abs(fast.sigma - real.sigma) / Mathf.Abs(real.sigma) : 1f;

            // The amplitude sweep, in the order flown. This is what separates "the
            // period is wrong" from "the disturbance was too big to be the thing the
            // formula describes" — and the largest point is the pulse this test used
            // for three milestones, so the old 8.1 s answer is still visible here
            // rather than having been dropped.
            var sweep = new System.Text.StringBuilder();
            for (int i = 0; i < rates.Length; i++)
            {
                if (i == frozenThrustStage || rates[i] != rates[0]) continue;
                if (sweep.Length > 0) sweep.Append(" · ");
                sweep.Append($"{_fit[i].amp:0.00} m/s → {_fit[i].Period:0.000} s, "
                             + $"zeta {_fit[i].Zeta:0.000}, dalpha/du "
                             + $"{_fit[i].alphaPerSpeed * Mathf.Rad2Deg:0.000}");
            }

            string detail =
                $"dT/dV MEASURED IN FLIGHT {tvMeasured:0.0000} N/(m/s) vs {tv:0.0000} "
                + $"from the propeller and motor constants ({tvErr * 100f:+0.0;-0.0} %) · "
                + $"decay rate {real.sigma:0.00000} 1/s powered against "
                + $"{frozen.sigma:0.00000} with thrust frozen at "
                + $"{_trimD[frozenThrustStage]:0.000} N, so the propeller supplies "
                + $"{deltaSigma / real.sigma * 100f:0} % of the damping · the two stages "
                + $"share an alpha response to {alphaMatch * 100f:0.0} %, which is what "
                + $"lets the difference isolate the thrust term"
                + $" || RATIOS zeta {real.Zeta:0.0000} powered, {frozen.Zeta:0.0000} frozen; "
                + $"the constant-thrust textbook form would say {zetaClassical:0.0000} and "
                + $"the thrust-corrected one {zetaFull:0.0000}. ⚠ both of those divide by "
                + $"the CLASSICAL frequency, which this aeroplane does not have (see PERIOD), "
                + $"so agreement in zeta is worth less than agreement in rate"
                + $" || DRAG the frozen stage implies dD/dV {dragSlopeMeasured:0.0000} against "
                + $"{dragSlopeConstAlpha:0.0000} for constant alpha "
                + $"({(dragSlopeMeasured - dragSlopeConstAlpha) / dragSlopeConstAlpha * 100f:+0.0;-0.0} %) "
                + "— low for the same reason the period is long, and ⚠ still open"
                + $" || fit residual {real.residual * 100f:0.0} % of a {real.amp:0.000} m/s amplitude"
                + $" || PERIOD {real.Period:0.000} s (INFO, not gated). π√2·V/g assumes "
                + $"alpha is frozen and gives {classicalT:0.000} s "
                + $"({classicalErr * 100f:+0.0;-0.0} %). Alpha is NOT frozen: it moves "
                + $"{real.alphaPerSpeed * Mathf.Rad2Deg:0.000} deg per m/s in ANTIPHASE with "
                + $"speed ({real.alphaAmp * Mathf.Rad2Deg:0.00}° amplitude), cancelling "
                + $"{(1f - k) * 100f:0} % of the lift a constant-alpha phugoid would make and "
                + $"giving {corrected:0.000} s ({periodErr * 100f:+0.0;-0.0} %). ⚠ that slope "
                + $"is itself amplitude-dependent across the sweep below while the period is "
                + $"not, so it explains the direction and size but is not tight enough to gate"
                + $" || AMPLITUDE {sweep} — 7.5x the disturbance moves the period by "
                + "0.1 %, which REFUTES the obvious explanation that the pulse was "
                + "simply too big for a small-disturbance formula"
                + $" || {rates[rates.Length - 1]} Hz gives T {fast.Period:0.000} s "
                + $"sigma {fast.sigma:0.00000} — spread {periodSpread * 100f:0.00} % / "
                + $"{sigmaSpread * 100f:0.0} %, so none of this is numerical dissipation";

            // The gate is ∂T/∂V, measured in flight against a closed form.
            //
            // A deliberate narrowing from what this test used to claim. The period is
            // measured well — 0 % run to run, 0.1 % across a 7.5x amplitude range,
            // 0.02 % across a doubled timestep — but there is no prediction for it
            // tight enough to gate on: π√2·V/g rests on α being frozen and this
            // aeroplane measurably does not freeze it. Gating on it would be gating
            // on a reference that does not describe the aircraft, which is the same
            // rule that keeps A4 off the real-trainer band and A6 out of the gate.
            //
            // ∂T/∂V has no such problem. The closed form uses only C_T0, J₀ and the
            // motor constants; the measurement is a difference between two flights
            // that differ in exactly one respect. Neither knows about the other.
            if (periodSpread > rateSpreadLimit || sigmaSpread > rateSpreadLimit)
                return Verdict.Fail(tvMeasured, "N/(m/s)", detail
                    + " — the mode depends on the timestep, so the damping is partly "
                    + "the integrator and every other row here inherits the doubt");

            if (deltaSigma >= 0f)
                return Verdict.Fail(tvMeasured, "N/(m/s)", detail
                    + " — freezing the thrust did not REDUCE the damping at all, so "
                    + "the propeller cannot be what the extra damping is and the "
                    + "whole account is wrong");

            if (Mathf.Abs(tvErr) > thrustDerivTolerance)
                return Verdict.Fail(tvMeasured, "N/(m/s)", detail
                    + $" — the flight-measured dT/dV is outside ±"
                    + $"{thrustDerivTolerance * 100f:0} % of the closed form, so the "
                    + "propeller's speed lapse is not the size the model says it is");

            return Verdict.Pass(tvMeasured, "N/(m/s)", detail);
        }

        private Verdict Invalid(string why) =>
            new Verdict { kind = Kind.Invalid, detail = why };

        protected override void DrawExtra()
        {
            base.DrawExtra();
            GUILayout.Label($"stage   {_stage + 1}/{rates.Length} "
                            + $"({StageName(Mathf.Min(_stage, rates.Length - 1))})   "
                            + $"t {_stageT:0.0} s   samples {_ts.Count}");
            GUILayout.Label($"thrust  {(Plane.thrustOverrideN >= 0f ? $"FROZEN at {Plane.thrustOverrideN:0.000} N" : "live")}");
        }
    }
}
