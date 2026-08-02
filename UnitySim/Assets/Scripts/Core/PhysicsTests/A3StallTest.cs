using AIHWSim.Core.Flight;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A3 — stall speed, and where on the span the wing lets go first.</b>
    ///
    /// <b>This is the row that justifies the whole model.</b> Every other test here
    /// could be passed by a wing represented as one lift coefficient: a single
    /// C_L(α) curve trims, glides, phugoids and pulls 2 g in a turn perfectly well.
    /// What it cannot do is have a <i>place</i> where it stalls. The only reason
    /// <see cref="Vehicles.Aero.PanelAero"/> cuts the wing into strips at all is so
    /// that different parts of it can be at different angles of attack — and the
    /// visible consequence of that, the one every builder cares about, is whether
    /// the root gives up before the tip.
    ///
    /// <b>Why it matters on a real aeroplane.</b> A wing that stalls at the root
    /// first drops its nose and mushes, and the ailerons — which live outboard —
    /// are still in attached flow and still work. A wing that stalls at the tip
    /// first drops that tip, with no roll authority to stop it, and the mush becomes
    /// a spin. Trainers are built with washout precisely to guarantee the first
    /// case, and this airframe carries −2° of it.
    ///
    /// So the ordering is not a soft expectation; it is a direct consequence of the
    /// authored twist, at the level of a sign. The tip sits 2° lower in incidence
    /// than the root, so the root must reach its stall angle first. If it does not,
    /// either the washout is not reaching the panels or the spanwise α is wrong, and
    /// both would be invisible to every other test in this suite.
    ///
    /// <b>What is gated and what is only reported.</b>
    /// <list type="bullet">
    /// <item><b>Gate — root before tip.</b> The wing's worst strip at the moment of
    ///       first stall must lie in the inboard half of the semi-span.</item>
    /// <item><b>Gate — the wing reaches the lift its sections claim.</b> Peak wing
    ///       C_L must land near C_Lmax less what the washout costs. Too low means
    ///       the panel model is losing lift somewhere; above the section maximum
    ///       means it is inventing it.</item>
    /// <item><b>Info — the stall speed itself</b>, against √(2W/ρSC_Lmax) = 8.91 m/s.
    ///       Two corrections stand between the two numbers and both are predictions
    ///       rather than excuses. Washout means the span cannot reach C_Lmax at once,
    ///       which pushes the model's stall speed UP to about 9.2 m/s; and the
    ///       propeller wake blows the inboard span, which pushes it back DOWN. The
    ///       measured figure is therefore a POWER-ON stall speed and is labelled as
    ///       one.</item>
    /// </list>
    ///
    /// <b>Blown lift, which the first run found rather than being told.</b> Measured
    /// against the free stream the wing reached C_L 1.266 — above the section's own
    /// 1.20 maximum, which reads as impossible. It is not: the propeller wake carries
    /// 1.07× the free-stream dynamic pressure averaged over the whole wing at the
    /// stall (about 1.8× over the immersed inboard span, which is roughly 9 % of it),
    /// and that extra 7 % of lift is real but does not belong to the free stream. So
    /// the gate is on C_L referenced to the wing's OWN local dynamic pressure, which
    /// is the number that describes what the aerofoil is doing, and the free-stream
    /// figure is reported beside it as the blowing.
    ///
    /// <b>The two corrections close the gap almost exactly.</b> Washout raises the
    /// power-off stall speed from 8.91 to 9.22 m/s; blowing lowers it again by
    /// √1.07 to 8.92; the measurement is 8.89. That is a 0.4 % accounting for a
    /// number nothing was tuned to hit, and it is the same effect that puts a lower
    /// power-on stall speed than power-off in every pilot's handbook.
    ///
    /// <b>How the stall is flown, and why not the obvious way.</b> The obvious entry
    /// is to hold the height with elevator and bleed the throttle away. It does not
    /// work, and the first run proved it: with the power gone the aeroplane simply
    /// glides, and a glide sits at TRIM angle of attack rather than at C_Lmax. That
    /// run held level down to 13.4 m/s, never loaded the wing past C_L 0.53, and
    /// correctly reported that it had found no stall at all.
    ///
    /// So the elevator is fed in open-loop instead, over 35 s, on partial power. Full
    /// up elevator is worth far more pitching moment than a 0.20 c̄ static margin
    /// needs, so the trim angle of attack is driven past C_Lmax with certainty rather
    /// than by hoping a controller gets there. The ramp is slow enough that the
    /// aeroplane walks through a sequence of nearly steady trims, so the C_L that
    /// appears is the wing's and not an inertia artefact, and partial power keeps the
    /// flight path shallow so it arrives near 1 g. The load factor at the stall is
    /// recorded anyway and used to reduce the measured speed to its 1 g equivalent,
    /// because a stall speed quoted at 1.1 g is not a stall speed.
    /// </summary>
    public sealed class A3StallTest : FlightTest
    {
        [Tooltip("Seconds of steady trimmed flight, height held, before the entry "
                 + "begins. This is the only closed-loop part of the run.")]
        public float holdSeconds = 6f;
        [Tooltip("Seconds over which the elevator is fed in. SLOW, so the aeroplane "
                 + "walks through a sequence of very nearly trimmed states and the "
                 + "C_L that appears is the wing's rather than the manoeuvre's.")]
        public float rampSeconds = 35f;
        [Tooltip("Trim throttle to start from, from the trim probe.")]
        public float trimThrottle = 0.719f;
        [Tooltip("Throttle held through the entry. Partial power rather than none, "
                 + "so the flight path stays shallow and the stall happens near 1 g "
                 + "instead of at the bottom of a glide.")]
        public float entryThrottle = 0.45f;
        [Tooltip("Seconds to keep watching after the wing first stalls.")]
        public float postStallSeconds = 4f;
        [Tooltip("Span fraction the first stalled strip must be inboard of. 0.5 is "
                 + "the inboard half — a genuine root stall on this planform lands "
                 + "in the innermost strip.")]
        public float rootBand = 0.5f;
        [Tooltip("Allowed error on peak wing C_L against the washout-corrected "
                 + "prediction.")]
        public float clTolerance = 0.12f;
        [Tooltip("Largest aileron deflection the wings-level loop may use. Small, "
                 + "because the ailerons live where the stall margin is thinnest.")]
        public float levellerAuthority = 0.10f;
        [Tooltip("Fraction of the predicted peak C_L the wing must be carrying before "
                 + "a stalled strip counts as THE stall. Below this the aeroplane has "
                 + "not run out of lift; something else has happened.")]
        public float loadedFraction = 0.75f;
        [Tooltip("Angle of attack (deg) below which a stalled strip is a pushover, "
                 + "not a stall. The section model stalls symmetrically about its "
                 + "zero-lift angle, so the negative branch is real — it is simply "
                 + "not what a stall speed means.")]
        public float minStallAlphaDeg = 2f;

        protected override string TestId => "A3";
        protected override string Title => "Stall speed and stall progression";
        protected override string Expected =>
            "root stalls before tip · peak section C_L ≈ 1.12 (C_Lmax less washout) "
            + "· speed is a POWER-ON stall";

        protected override float TestAltitude => 900f;

        private float _t;
        private int _wing;
        private float _clPredicted;

        // Peak wing lift coefficient seen anywhere in the run, and the state at the
        // moment the wing first let go.
        private float _peakCl;        // referenced to the wing's own local q
        private float _peakClFree;    // referenced to the free stream
        private float _stallBlow;     // q_local / q_freestream at the stall
        private bool _stalled;
        private float _stallTas, _stallStation, _stallLoad, _stallCl, _stallAlpha;
        private float _stallTime;
        private bool _tailFirst;
        private float _minLevelTas = float.MaxValue;

        protected override void Idle(ScriptedPilot p)
        {
            p.Neutral();
            p.throttle = trimThrottle;
            HoldWingsLevel(p);
        }

        protected override void Arm()
        {
            _wing = Spec.wingIndex;
            LiftingSurfaceFacts(out float clMax, out float slope, out float washoutDeg);
            _clPredicted = clMax - slope * Mathf.Abs(washoutDeg) * 0.5f * Mathf.Deg2Rad;
            LaunchAtTrim(LaunchSpeed);
        }

        protected override void Fly(ScriptedPilot p, float t)
        {
            _t = t;
            p.Neutral();

            // Aileron authority deliberately limited. The ailerons sit at 50–95 % of
            // the semi-span, which is exactly where the washout has left the least
            // stall margin — a leveller allowed full deflection can stall the tip it
            // is holding up, and then A3 measures the autopilot rather than the wing.
            // A real pilot is taught the same thing: near the stall you do not pick up
            // a wing with aileron.
            HoldWingsLevel(p, 0f, levellerAuthority);

            if (t < holdSeconds)
            {
                // Standardise the entry: trimmed, level, hands settled. Closed-loop
                // here only, so every run starts the deceleration from the same
                // place.
                p.throttle = trimThrottle;
                HoldVerticalSpeed(p, 0f);
                return;
            }

            // From here the aeroplane flies OPEN-LOOP, and that is the point.
            //
            // The first design held the height with elevator and bled the throttle
            // away, expecting the aircraft to slow into the stall. It does not: with
            // the power gone it simply glides, and a glide sits at trim angle of
            // attack rather than at C_Lmax. That run held level down to 13.4 m/s,
            // never loaded the wing past C_L 0.53, and correctly reported that it
            // had found no stall speed at all.
            //
            // So the elevator is fed in directly instead. Full up elevator is worth
            // far more pitching moment than a 0.20 c̄ static margin needs, so the
            // trim angle of attack is driven past C_Lmax with certainty rather than
            // by hoping a controller gets there — and because the ramp takes 35 s,
            // the aeroplane walks through a sequence of nearly steady trims and
            // arrives at the stall near 1 g. Partial power keeps the flight path
            // shallow so it stays that way.
            p.throttle = entryThrottle;
            p.pitch = Mathf.Clamp01((t - holdSeconds) / Mathf.Max(0.1f, rampSeconds));
        }

        protected override void Sample(float dt)
        {
            // TWO wing lift coefficients, and the difference between them is a real
            // aeroplane behaviour rather than bookkeeping.
            //
            // Referenced to the FREE STREAM, this is the number that goes with
            // √(2W/ρSC_Lmax) — and with the propeller running it can legitimately
            // exceed the section C_Lmax, because the inboard strips are flying in
            // air the propeller accelerated. Referenced to the wing's own LOCAL
            // dynamic pressure, it is a statement about what the sections are doing,
            // and that is the one that must not exceed what the aerofoil can carry.
            //
            // The first run measured 1.266 against a 1.20 section maximum and failed
            // for it. Nothing was wrong: at 8.9 m/s on partial power the wake is
            // 2.6× the free-stream dynamic pressure over the inboard span, which is
            // worth about 14 % more lift than the free stream can account for. That
            // is blown lift, it is why every handbook quotes a lower power-on stall
            // speed than power-off, and the model produced it without being told to.
            float lift = Plane.SurfaceLift(_wing);
            float qFree = Air.Q;
            float qLocal = Plane.SurfaceDynamicPressure(_wing);
            float cl = qFree > 1e-4f ? lift / (qFree * Spec.WingArea) : 0f;
            float clSection = qLocal > 1e-4f ? lift / (qLocal * Spec.WingArea) : 0f;

            float margin = Plane.SurfaceStallMargin(_wing);
            float tailMargin = Plane.SurfaceStallMargin(1);

            // Only count states that are actually part of an approach to the stall.
            //
            // Two things had to be excluded, and both were found by the first run
            // producing a "stall" at −7.1° and 0.20 g:
            //   · The section model stalls symmetrically about its zero-lift angle,
            //     so a hard PUSHOVER stalls the wing just as truly as a pull does.
            //     That is correct physics and completely irrelevant to a stall speed.
            //   · A strip can let go during a transient while the wing as a whole is
            //     carrying very little. Requiring most of the predicted peak C_L is
            //     what makes this "the aeroplane ran out of lift" rather than "a
            //     strip briefly went past its angle".
            bool approach = _t >= holdSeconds
                            && Air.AlphaDeg >= minStallAlphaDeg
                            && clSection >= loadedFraction * _clPredicted;

            if (!_stalled)
            {
                // The peak is tracked only at positive angle of attack, so a
                // pushover's large negative C_L cannot masquerade as the wing's best.
                if (Air.AlphaDeg > 0f)
                {
                    if (clSection > _peakCl) _peakCl = clSection;
                    if (cl > _peakClFree) _peakClFree = cl;
                }
                if (Mathf.Abs(Plane.VerticalSpeed) < 0.5f && Air.Tas < _minLevelTas)
                    _minLevelTas = Air.Tas;

                if (approach && margin < 0f)
                {
                    _stalled = true;
                    _stallTime = _t;
                    _stallTas = Air.Tas;
                    _stallStation = Plane.SurfaceWorstStation(_wing);
                    _stallLoad = Plane.LoadFactor;
                    _stallCl = cl;
                    _stallAlpha = Air.AlphaDeg;
                    _stallBlow = qFree > 1e-4f ? qLocal / qFree : 1f;
                    // Worth knowing, and a different fault if it happens: the
                    // elevator being held hard over can stall the TAILPLANE first,
                    // which is a pitch departure rather than a wing stall and would
                    // make everything below a measurement of the wrong surface.
                    _tailFirst = tailMargin < margin;
                }
            }
            else
            {
                // The peak can arrive a fraction of a second after the first strip
                // lets go, while the rest of the span is still loading up.
                if (clSection > _peakCl) _peakCl = clSection;
                if (cl > _peakClFree) _peakClFree = cl;
            }
        }

        protected override Verdict? Evaluate()
        {
            if (!_stalled)
            {
                // Ran the whole ramp without stalling — a real outcome and a bad
                // one, so it must not be silently waited out until the timeout.
                if (_t < holdSeconds + rampSeconds + 2f) return null;
                return new Verdict
                {
                    kind = Kind.Invalid,
                    value = _minLevelTas < float.MaxValue ? _minLevelTas : 0f,
                    units = "m/s",
                    detail = "the elevator reached full travel without the wing "
                             + $"reaching {loadedFraction:0.00}× its predicted peak C_L at a "
                             + "positive angle of attack — so there is no stall speed "
                             + "here to report. Either the elevator cannot trim this "
                             + "aeroplane past C_Lmax, or the entry is not slow enough "
                             + "to be quasi-steady",
                };
            }
            if (_t < _stallTime + postStallSeconds) return null;

            LiftingSurfaceFacts(out float clMax, out float slope, out float washoutDeg);

            // What a washed-out wing can actually carry. The root reaches C_Lmax
            // first; every station outboard of it is lower by a·Δα, and the twist is
            // linear, so the span mean loses a·(washout/2).
            float washoutLoss = slope * Mathf.Abs(washoutDeg) * 0.5f * Mathf.Deg2Rad;
            float clPredicted = clMax - washoutLoss;

            // Reduce the measured speed to 1 g. A stall entered at 1.05 g happens
            // faster than a stall entered at 1.00, and quoting the raw number would
            // report the manoeuvre rather than the wing.
            float load = Mathf.Max(0.2f, _stallLoad);
            float vs1g = _stallTas / Mathf.Sqrt(load);
            float vsIdeal = Spec.PredictedStallSpeed;
            float vsWashout = vsIdeal * Mathf.Sqrt(clMax / Mathf.Max(0.01f, clPredicted));
            // Blowing works the other way: the wake carries part of the lift, so the
            // free stream needs less speed to supply the rest. Lift goes as q, so
            // the speed correction is the square root of the dynamic-pressure ratio.
            float vsBlown = vsWashout / Mathf.Sqrt(Mathf.Max(0.01f, _stallBlow));

            bool rootFirst = _stallStation <= rootBand;
            bool clOk = Mathf.Abs(_peakCl - clPredicted) <= clTolerance;

            string detail =
                $"first stall at {_stallStation * 100f:0} % semi-span "
                + $"({(rootFirst ? "ROOT — as −2° of washout requires" : "OUTBOARD — washout is not reaching the panels")})"
                + $" · peak wing C_L {_peakCl:0.000} on the wing's own local q, vs "
                + $"{clPredicted:0.000} predicted (C_Lmax {clMax:0.00} less "
                + $"{washoutLoss:0.000} for washout)"
                + $" · stalled at {_stallTas:0.00} m/s and {load:0.00} g → "
                + $"{vs1g:0.00} m/s at 1 g, α {_stallAlpha:0.0}°"
                + $" · BLOWN: the propeller wake is {_stallBlow:0.00}× free-stream q over "
                + $"the wing at the stall, so against the free stream the same lift reads "
                + $"C_L {_peakClFree:0.000} — above the section maximum, which is what "
                + $"blown lift means and why this POWER-ON stall speed sits below the "
                + $"power-off figures below"
                + $" · ⚠ INFO: √(2W/ρSC_Lmax) = {vsIdeal:0.00} m/s assumes the whole span "
                + $"reaches C_Lmax together AND no propeller. Washout makes the first "
                + $"impossible and raises it to {vsWashout:0.00} m/s; the wake's "
                + $"{_stallBlow:0.00}× blowing lowers it again by √ to {vsBlown:0.00}; "
                + $"measured {vs1g:0.00}, which is "
                + $"{(vs1g - vsBlown) / vsBlown * 100f:+0.0;-0.0} % from an accounting "
                + $"nothing was tuned to hit"
                + (_tailFirst
                   ? " · ⚠ the TAILPLANE was more stalled than the wing at that instant — "
                     + "this is a pitch departure, not a wing stall, and the numbers above "
                     + "describe the wrong surface"
                   : "");

            if (_tailFirst)
                return new Verdict
                {
                    kind = Kind.Invalid, value = vs1g, units = "m/s", detail = detail,
                };

            return rootFirst && clOk
                ? Verdict.Pass(vs1g, "m/s", detail)
                : Verdict.Fail(vs1g, "m/s",
                    detail + $" (C_L tol ±{clTolerance:0.00}, root band {rootBand:0.00})");
        }

        /// <summary>Pull the wing's section facts out of the spec, so the prediction
        /// above is computed from the authored geometry rather than restated as a
        /// literal that could drift away from it.</summary>
        private void LiftingSurfaceFacts(out float clMax, out float slope, out float washoutDeg)
        {
            Vehicles.Aero.LiftingSurface w = Spec.surfaces[_wing];
            clMax = w.airfoil.clMax;
            slope = w.LiftSlope;
            washoutDeg = w.washoutDeg;
        }

        protected override void DrawExtra()
        {
            base.DrawExtra();
            GUILayout.Label($"peak wing CL {_peakCl:0.000}   "
                            + (_stalled
                               ? $"stalled at {_stallTas:0.00} m/s, {_stallStation * 100f:0} % span"
                               : $"margin {Plane.SurfaceStallMargin(_wing) * 100f:0}%"));
        }
    }
}
