using System;
using System.Collections.Generic;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[ABENCH] — the aero core's bench check.</b> No scene, no rigidbody, no
    /// play mode: it builds <see cref="DebugPlanes.SportRc"/>, asks it for every
    /// derived quantity, and compares each against the value computed by hand
    /// before any of this code existed.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.AeroBench.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <b>Why a bench rather than a flight test.</b> At this stage nothing
    /// instantiates the aero core, so the only thing that can be wrong is the
    /// arithmetic — and arithmetic is exactly what a closed form checks better than
    /// a simulation. If the wing reports the wrong aspect ratio, every later result
    /// is wrong in a way that would be blamed on the flight model. Catching it here
    /// costs two seconds; catching it in the phugoid test costs an afternoon.
    ///
    /// The expected values below are the hand computation, and they are written out
    /// so that a disagreement says WHICH of the two is wrong rather than just that
    /// something is.
    /// </summary>
    public static class AeroBench
    {
        private const string Tag = "[ABENCH]";

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [ABENCH] Aero Bench", priority = 120)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            AircraftSpec spec = DebugPlanes.SportRc();

            Geometry(spec);
            MassAndBalance(spec);
            Performance(spec);
            Stability(spec);
            Panels(spec);
            Propulsion(spec);
            Wake(spec);
            Phugoid(spec);

            Debug.Log(_log.ToString().TrimEnd());

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- checks ------------------------------------------------------

        private static void Geometry(AircraftSpec spec)
        {
            LiftingSurface w = spec.surfaces[0];
            LiftingSurface h = spec.surfaces[1];
            LiftingSurface v = spec.surfaces[2];

            Check("wing area", w.Area, 0.3360f, 1e-4f, "m²", "b·c = 1.40 × 0.24");
            Check("wing span", w.Span, 1.4000f, 1e-4f, "m", "authored");
            Check("wing AR", w.AspectRatio, 5.8333f, 1e-3f, "", "b²/S = 1.96/0.336");
            Check("wing MAC", w.MeanAerodynamicChord, 0.2400f, 1e-4f, "m", "rectangular ⇒ = chord");
            Check("wing lift slope", w.LiftSlope, 4.4774f, 1e-3f, "/rad",
                  "2π/(1 + 2/(AR·e)), e = 0.85");
            Check("aileron effectiveness", w.ControlEffectiveness, 0.6087f, 1e-3f, "",
                  "τ = 1 − (θ−sinθ)/π at E = 0.25");

            Check("tailplane area", h.Area, 0.0740f, 1e-4f, "m²", "0.148 × 0.25 × 2");
            Check("tailplane AR", h.AspectRatio, 3.3784f, 1e-3f, "", "0.25/0.074");
            Check("tailplane lift slope", h.LiftSlope, 3.6110f, 1e-3f, "/rad", "e = 0.80");
            Check("elevator effectiveness", h.ControlEffectiveness, 0.7477f, 1e-3f, "",
                  "τ at E = 0.40");

            Check("fin area", v.Area, 0.0266f, 1e-4f, "m²", "0.133 × 0.20, single surface");
            Check("fin effective AR", v.AspectRatio, 2.2556f, 1e-3f, "",
                  "geometric 1.504 × 1.5 end-plate ⚠");

            // The stall angle is DERIVED from C_Lmax and the slope, so it is worth
            // seeing: it is what decides when the wing lets go.
            float stallDeg = w.airfoil.StallAlpha(w.LiftSlope) * Mathf.Rad2Deg;
            Check("wing stall alpha", stallDeg, 11.86f, 0.02f, "deg",
                  "α₀ + C_Lmax/a = −3.5 + 1.20/4.4774");
        }

        private static void MassAndBalance(AircraftSpec spec)
        {
            Check("total mass", spec.TotalMass, 2.0000f, 1e-4f, "kg", "parts list");

            Vector3 cg = spec.CentreOfMass;
            Check("CG station", cg.z, -0.0014f, 5e-4f, "m", "battery solved to place it");

            // The number a model flyer would actually check.
            float wingLe = spec.surfaces[0].rootQuarterChord.z + 0.25f * spec.MeanChord;
            float pctMac = (wingLe - cg.z) / spec.MeanChord * 100f;
            Check("CG as % MAC", pctMac, 25.6f, 0.3f, "%",
                  "target: 15 % static margin on the REAL aeroplane");

            Vector3 inertia = spec.InertiaTensorDiagonal;
            // Unity's tensor is (about X, about Y, about Z) = (pitch, yaw, roll),
            // because the body frame is +Z forward. Aircraft convention calls the
            // roll axis x; these are the same numbers under different names.
            Check("roll inertia (about Z)", inertia.z, 0.0916f, 0.004f, "kg·m²",
                  "wing spread dominates: ≈ m·b²/12");
            Check("pitch inertia (about X)", inertia.x, 0.1470f, 0.006f, "kg·m²", "nose/tail arms");
            Check("yaw inertia (about Y)", inertia.y, 0.2308f, 0.008f, "kg·m²", "both");

            Check("wing loading", spec.WingLoading, 58.37f, 0.1f, "N/m²",
                  "= 19.5 oz/ft², mid trainer band");
        }

        private static void Performance(AircraftSpec spec)
        {
            Check("C_D0 (airframe)", spec.ZeroLiftDragCoefficient, 0.03285f, 5e-4f, "",
                  "parasitic 0.0058 m² + every surface's profile drag, ÷ S");
            Check("stall speed", spec.PredictedStallSpeed, 8.912f, 0.02f, "m/s",
                  "√(2W/ρSC_Lmax) — the A3 target");
            Check("best glide L/D", spec.PredictedLiftToDragMax, 10.89f, 0.05f, "",
                  "½√(π·AR·e/C_D0) — the A4 target");
            Check("phugoid @ 16 m/s", AircraftSpec.PhugoidPeriod(16f), 7.249f, 0.01f, "s",
                  "π√2·V/g — no coefficients in it at all; the A5 target");
        }

        private static void Stability(AircraftSpec spec)
        {
            LiftingSurface w = spec.surfaces[0];
            LiftingSurface h = spec.surfaces[1];
            LiftingSurface v = spec.surfaces[2];
            Vector3 cg = spec.CentreOfMass;
            float mac = spec.MeanChord;

            float armH = cg.z - h.rootQuarterChord.z;
            float armV = cg.z - v.rootQuarterChord.z;

            float vH = h.Area * armH / (w.Area * mac);
            float vV = v.Area * armV / (w.Area * w.Span);
            Check("tail volume V_H", vH, 0.5493f, 2e-3f, "", "S_h·l_h/(S·c̄), trainer band 0.50–0.65");
            Check("fin volume V_V", vV, 0.03385f, 2e-4f, "", "S_v·l_v/(S·b), band 0.02–0.04");

            // Neutral point, front-view textbook form. eta_h is the tail's dynamic
            // pressure ratio: 0.9 here because there is no slipstream yet, and the
            // whole point of adding one later is that this number stops being
            // authored and starts moving with throttle.
            const float etaH = 0.9f;
            float aw = w.LiftSlope;
            float ah = h.LiftSlope;
            float downwash = 2f * aw / (Mathf.PI * w.AspectRatio);
            float xnp = 0.25f + (ah / aw) * vH * etaH * (1f - downwash);
            Check("downwash dε/dα", downwash, 0.4886f, 2e-3f, "", "2a/(π·AR)");
            Check("neutral point", xnp, 0.4539f, 3e-3f, "c̄",
                  "0.25 + (a_h/a_w)·V_H·η_h·(1−dε/dα)");

            float pctMac = (w.rootQuarterChord.z + 0.25f * mac - cg.z) / mac;
            float smModel = xnp - pctMac;
            Line($"static margin (model) {smModel:0.000} c̄ — no fuselage aero, so the "
                 + "model is MORE stable than the aeroplane; the ≈0.05 c̄ of neutral "
                 + "point a fuselage would remove is the declared omission, and the "
                 + "real airframe's margin is ≈0.15");
            Check("static margin (model)", smModel, 0.198f, 0.006f, "c̄", "= x_np − x_cg");
        }

        private static void Panels(AircraftSpec spec)
        {
            PanelAero aero = PanelAero.Build(spec.surfaces);
            Check("panel count", aero.PanelCount, 29f, 0.5f, "",
                  "wing 8×2 + tailplane 4×2 + fin 5");

            // The discretisation must not invent or lose area. Half-cosine edges
            // with a midpoint chord are EXACT for a linear taper, so this should
            // agree to float precision, not merely closely — and if it ever does
            // not, every force the model produces is scaled wrong.
            var area = new Dictionary<int, float>();
            foreach (AeroPanel p in aero.Panels)
            {
                area.TryGetValue(p.surface, out float a);
                area[p.surface] = a + p.area;
            }
            for (int i = 0; i < spec.surfaces.Count; i++)
            {
                area.TryGetValue(i, out float got);
                Check($"Σ panel area [{spec.surfaces[i].name}]", got, spec.surfaces[i].Area,
                      1e-5f, "m²", "discretisation must conserve area exactly");
            }

            // Frame orthonormality: a non-orthogonal panel frame silently biases
            // every angle of attack the model computes.
            float worstDot = 0f, worstLen = 0f;
            foreach (AeroPanel p in aero.Panels)
            {
                worstDot = Mathf.Max(worstDot, Mathf.Abs(Vector3.Dot(p.normal, p.chordDir)));
                worstDot = Mathf.Max(worstDot, Mathf.Abs(Vector3.Dot(p.normal, p.spanDir)));
                worstDot = Mathf.Max(worstDot, Mathf.Abs(Vector3.Dot(p.spanDir, p.chordDir)));
                worstLen = Mathf.Max(worstLen, Mathf.Abs(p.normal.magnitude - 1f));
                worstLen = Mathf.Max(worstLen, Mathf.Abs(p.spanDir.magnitude - 1f));
            }
            Check("panel frame orthogonality", worstDot, 0f, 1e-5f, "", "max |dot| over all panels");
            Check("panel frame normalisation", worstLen, 0f, 1e-5f, "", "max |‖v‖−1|");

            // Dihedral must tilt the two wings' normals in OPPOSITE lateral
            // directions, or there is no dihedral effect and the aircraft has no
            // spiral stability. Checking the sign here is cheaper than diagnosing
            // it from a flight trace.
            float leftX = 0f, rightX = 0f;
            foreach (AeroPanel p in aero.Panels)
            {
                if (p.surface != 0) continue;
                if (p.posLocal.x > 0f) rightX = p.normal.x;
                else leftX = p.normal.x;
            }
            Check("dihedral: right wing normal x", rightX, -0.0872f, 1e-3f, "", "−sin 5°");
            Check("dihedral: left wing normal x", leftX, 0.0872f, 1e-3f, "", "+sin 5°");

            // The HINGED area must not depend on how finely the wing is chopped up.
            //
            // It used to. A midpoint "is this strip hinged?" test quantises the
            // aileron to panel boundaries, so the model flew a control surface 20 %
            // too big at four panels a side and 18 % too small at eight — and test
            // A6, whose whole job is to measure discretisation error, was reading
            // that as the discretisation error. It reported 226, 166 and 216 °/s at
            // 4/8/16 panels, non-monotone, and the cause was the aileron changing
            // size rather than the aerodynamics changing resolution.
            //
            // Four panel counts, one authored number, exact agreement demanded.
            LiftingSurface wing = spec.surfaces[0];
            float tauRef = wing.ControlEffectiveness;
            float authoredAileron = (wing.controlSpanEnd - wing.controlSpanStart)
                                    * wing.semiSpan * wing.rootChord * 2f;
            foreach (int n in new[] { 4, 8, 16, 32 })
            {
                var surfaces = new List<LiftingSurface>(spec.surfaces);
                LiftingSurface w = surfaces[0];
                w.panelsPerSide = n;
                surfaces[0] = w;

                PanelAero built = PanelAero.Build(surfaces);
                float hingedArea = 0f;
                foreach (AeroPanel p in built.Panels)
                    if (p.surface == 0) hingedArea += p.area * (p.controlTau / tauRef);

                Check($"aileron area at {n} panels/side", hingedArea, authoredAileron,
                      1e-5f, "m²",
                      "0.45 of a 0.70 m semi-span × 0.24 m chord × 2 — the hinged area "
                      + "is authored geometry and must not move with the panel count");
            }
        }

        private static void Propulsion(AircraftSpec spec)
        {
            // Spin the shaft up from rest at full throttle and let it find its own
            // equilibrium. This tests the integrator AND the closed form at once:
            // the motor's torque curve and the prop's load curve cross at exactly
            // one speed, and that speed is solvable by hand.
            float omega = 0f;
            const float dt = 1f / 1000f;
            for (int i = 0; i < 8000; i++)
                omega = PropellerModel.StepShaft(spec.propeller, spec.motor,
                                                 spec.motor.maxVoltage, omega, 0f, dt,
                                                 out _, out _, out _);

            float rpm = omega * 60f / (2f * Mathf.PI);
            float thrust = PropellerModel.Thrust(spec.propeller, omega, 0f);
            float weight = spec.TotalMass * 9.80665f;

            Check("static shaft speed", omega, 850.3f, 6f, "rad/s",
                  "solves 2.51e-7·ω² + 6.09e-4·ω − 0.699 = 0");
            Check("static rpm", rpm, 8120f, 60f, "rpm", "= the same equilibrium");
            Check("static thrust", thrust, 11.21f, 0.20f, "N", "C_T0·ρ·n²·D⁴ — the A1 target");
            Check("thrust/weight", thrust / weight, 0.572f, 0.012f, "",
                  "a docile trainer, not a rocket");

            // Convergence, not just the endpoint: an unstable integrator can still
            // land near the right answer while oscillating around it.
            float again = PropellerModel.StepShaft(spec.propeller, spec.motor,
                                                   spec.motor.maxVoltage, omega, 0f, dt,
                                                   out _, out _, out _);
            Check("shaft converged", Mathf.Abs(again - omega), 0f, 1e-3f, "rad/s",
                  "one more step must not move it");

            // Thrust must fall with airspeed — that is what makes level-flight
            // speed a consequence rather than a number someone picked.
            float atCruise = PropellerModel.Thrust(spec.propeller, omega, 16f);
            Line($"thrust at 16 m/s (same shaft speed) {atCruise:0.00} N vs {thrust:0.00} N static "
                 + "— the advance-ratio falloff is what sets top speed");
            if (atCruise >= thrust)
            {
                _failed++;
                Debug.LogError($"{Tag} FAIL thrust does not fall with airspeed — "
                               + "the advance ratio is not reaching the coefficients");
            }
            _checks++;
        }

        // ---- the propeller wake -------------------------------------------

        /// <summary>
        /// <b>Slipstream, and the two things it buys.</b>
        ///
        /// Momentum theory has no free parameters: give it a thrust and it returns
        /// a velocity field. Everything below is therefore a closed form that can be
        /// checked on paper, which is exactly the kind of thing a bench is for —
        /// and the geometry half (which strips are inside the tube) is a
        /// segment-circle intersection, so it is exact rather than approximately
        /// right.
        ///
        /// The two payoffs are checked directly rather than described:
        /// <list type="number">
        /// <item>At <b>zero airspeed</b> the tailplane still has a dynamic pressure
        ///       to work against — 73.6 Pa at full power, over half its cruise
        ///       value. Without the wake it would be exactly zero and no model
        ///       aeroplane could raise its tail on the takeoff roll, which they
        ///       plainly do.</item>
        /// <item>η_h stops being an authored constant. It is 1.00 with the motor off
        ///       and 1.38 at full power, and the neutral point moves 8.7 % of chord
        ///       between them. That IS the throttle-dependent pitch trim of a
        ///       tractor-prop aeroplane, and nothing was added to produce it.</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// The aeroplane's own hands-off trim speed (m/s), as the [TRIM] probe
        /// measured it with the wake modelled: 14.66 m/s on 71.9 % throttle. It is
        /// hard-coded here on purpose — a bench compares the model against numbers
        /// written down beforehand, so reading it back out of the model at run time
        /// would make every check below compare the code with itself.
        /// </summary>
        private const float Cruise = 14.66f;

        private static void Wake(AircraftSpec spec)
        {
            PropellerSpec prop = spec.propeller;
            Vector3 disc = spec.propPosLocal;
            float r = 0.5f * prop.diameterM;

            Check("disc area", Mathf.PI * r * r, 0.050671f, 1e-5f, "m²", "πD²/4, D = 0.254");

            // ---- static: the aeroplane standing still at full power ----
            float omegaStatic = Equilibrium(spec, spec.motor.maxVoltage, 0f);
            float thrustStatic = PropellerModel.Thrust(prop, omegaStatic, 0f);
            SlipstreamState s0 = Slipstream.Solve(prop, disc, thrustStatic, 0f);

            Check("static v_i", s0.InducedVelocity, 9.50f, 0.03f, "m/s",
                  "√(T/2ρA) at T = 11.21 N — the hover form, V = 0");
            Check("static far wake", s0.FarWakeIncrement, 19.00f, 0.06f, "m/s",
                  "Δv = 2·v_i; this is what the tail flies in at rest");
            Check("static tube radius", s0.FarWakeRadius, 0.089803f, 3e-4f, "m",
                  "r∞ = R/√2 exactly when V = 0 — pure continuity");

            // Continuity must hold at EVERY station, not only at the two the theory
            // names. If the shape function and the contraction ever disagree, the
            // tube is carrying mass that did not come through the disc.
            float worstFlow = 0f;
            float refFlow = Mathf.PI * r * r * (s0.FreeStreamAxial + s0.InducedVelocity);
            for (float x = 0.05f; x <= 2.0f; x += 0.05f)
            {
                float dv = Slipstream.IncrementAt(s0, x * prop.diameterM, out float rad);
                float flow = Mathf.PI * rad * rad * (s0.FreeStreamAxial + dv);
                worstFlow = Mathf.Max(worstFlow, Mathf.Abs(flow - refFlow));
            }
            Check("wake mass flow conserved", worstFlow, 0f, 1e-6f, "m³/s",
                  "π·r²·(V+Δv) = π·R²·(V+v_i) at 40 stations — the shape function is "
                  + "⚠ estimated, the continuity that ties radius to it is not");

            // ---- which strips are inside the tube, at rest ----
            PanelAero aero = PanelAero.Build(spec.surfaces);
            Check("wing immersed area", ImmersedFraction(aero, spec, 0, s0), 0.0883f, 0.002f, "",
                  "tube reaches 0.0618 m of a 0.70 m semi-span, allowing for 5° dihedral "
                  + "lifting the wing out of it");
            Check("tailplane immersed area", ImmersedFraction(aero, spec, 1, s0), 0.3570f, 0.002f, "",
                  "√(r∞² − 0.01²) = 0.0892 m of a 0.25 m semi-span");
            Check("fin immersed area", ImmersedFraction(aero, spec, 2, s0), 0.3490f, 0.002f, "",
                  "the fin's root is 0.02 m off the shaft line, so 0.0698 m of 0.20");

            // ---- payoff 1: the tail works with the aeroplane standing still ----
            float qTailStatic = EffectiveQ(aero, spec, 1, s0, 0f);
            Check("tail q at zero airspeed", qTailStatic, 73.65f, 1.5f, "Pa",
                  "full power, aircraft stationary — WITHOUT the wake this is exactly "
                  + "zero and the elevator does nothing");
            Line($"tail q at rest is {qTailStatic / 131.64f * 100f:0} % of its {Cruise:0.00} m/s "
                 + "cruise value (131.6 Pa) — which is why a model can raise its tail "
                 + "before it has any airspeed at all");

            // ---- payoff 2: eta_h, and a neutral point that moves ----
            const float cruise = Cruise;
            float omegaFull = Equilibrium(spec, spec.motor.maxVoltage, cruise);
            float thrustFull = PropellerModel.Thrust(prop, omegaFull, cruise);
            Check("thrust at full power, cruise", thrustFull, 5.95f, 0.25f, "N",
                  "the advance-ratio falloff: 11.21 N static becomes 5.9 N at 14.66 m/s");

            SlipstreamState sCruise = Slipstream.Solve(prop, disc, thrustFull, cruise);
            float qInf = 0.5f * AeroDynamics.AirDensity * cruise * cruise;

            float etaIdle = EffectiveQ(aero, spec, 1, default, cruise) / qInf;
            float etaFull = EffectiveQ(aero, spec, 1, sCruise, cruise) / qInf;
            Check("eta_h, motor off", etaIdle, 1.000f, 1e-3f, "",
                  "no wake ⇒ the tail sees the free stream, exactly");
            Check("eta_h, full power", etaFull, 1.410f, 0.03f, "",
                  "Δv 5.50 m/s over the inboard 47 % of the tailplane");

            // The same textbook neutral point as Stability(), with eta_h now
            // measured instead of assumed.
            LiftingSurface w = spec.surfaces[0];
            LiftingSurface h = spec.surfaces[1];
            Vector3 cg = spec.CentreOfMass;
            float mac = spec.MeanChord;
            float vH = h.Area * (cg.z - h.rootQuarterChord.z) / (w.Area * mac);
            float coeff = (h.LiftSlope / w.LiftSlope) * vH
                          * (1f - 2f * w.LiftSlope / (Mathf.PI * w.AspectRatio));

            float npIdle = 0.25f + coeff * etaIdle;
            float npFull = 0.25f + coeff * etaFull;
            Check("neutral point, motor off", npIdle, 0.4765f, 0.005f, "c̄",
                  "0.25 + (a_h/a_w)·V_H·η_h·(1−dε/dα) at η_h = 1.00");
            Check("neutral point, full power", npFull, 0.5695f, 0.008f, "c̄", "the same, at η_h = 1.41");
            Check("neutral point shift with throttle", npFull - npIdle, 0.0929f, 0.005f, "c̄",
                  "9.3 % of chord between idle and full — the throttle-dependent pitch "
                  + "trim every tractor aeroplane has, and nothing was added to make it");

            Line("⚠ the model's power-off η_h is 1.00 because there is no fuselage "
                 + "boundary layer or wing wake over the tail; a real trainer's is nearer "
                 + "0.9. So the model remains slightly MORE stable power-off than the "
                 + "aeroplane, on top of the missing fuselage aerodynamics — both errors "
                 + "point the same way and both are declared");
        }

        // ---- the phugoid, and the term the textbook form leaves out -------

        /// <summary>Trim throttle from the [TRIM] probe, as A5 flies it.</summary>
        private const float TrimThrottle = 0.719f;

        /// <summary>
        /// <b>Why the phugoid looked over-damped, settled on paper before any
        /// aeroplane was flown.</b>
        ///
        /// A5 measured ζ ≈ 0.16 and compared it against ζ = 1/(√2·L/D) ≈ 0.078,
        /// concluded the model damped the mode twice as hard as theory allowed, and
        /// carried that as an open question. The formula is the thing that is
        /// incomplete: it assumes thrust does not vary with speed, which is true of
        /// a jet at constant power setting and thoroughly false of a fixed-pitch
        /// propeller on a fixed voltage.
        ///
        /// Everything below is a closed form on the model's own propeller and motor
        /// constants, so it can be checked on paper — which is what a bench is for,
        /// and the reason this arrives before the flight test rather than after it.
        ///
        /// The single most telling line is the last: the naive formula, fed the
        /// correct damping, back-solves a lift-to-drag ratio of 4.3. A5's existing
        /// doc block records the anomaly as "implying a cruise L/D of 4.4". That is
        /// not a coincidence, it is the same arithmetic run backwards, and it says
        /// the measurement was right all along.
        /// </summary>
        private static void Phugoid(AircraftSpec spec)
        {
            float volts = TrimThrottle * spec.motor.maxVoltage;
            Check("trim voltage", volts, 7.9809f, 1e-3f, "V",
                  "71.9 % of an 11.1 V 3S pack — the throttle A5 holds");

            float omega = PropellerModel.EquilibriumOmega(spec.propeller, spec.motor,
                                                          volts, Cruise);
            Check("shaft at trim", omega, 725.6f, 8f, "rad/s",
                  "motor 0.5005 − 6.089e-4·ω against prop 2.506e-7·ω² − 1.010e-4·ω, "
                  + "Coulomb kt·I0 = 7.64 mN·m included");

            float thrust = PropellerModel.Thrust(spec.propeller, omega, Cruise);
            Check("thrust at trim", thrust, 2.163f, 0.09f, "N",
                  "in level flight this IS the drag, so L/D = 19.61/2.16 = 9.07 — "
                  + "the glide test measures 9.44 and the polar predicts 9.75");

            float j = PropellerModel.AdvanceRatio(spec.propeller, omega, Cruise);
            Check("advance ratio at trim", j, 0.5001f, 0.006f, "",
                  "V/(n·D) = 14.66/(115.5 × 0.254); J₀ is 0.68, so the prop is "
                  + "three quarters of the way to making no thrust at all");

            // The whole point. dT/dV with the shaft free to re-equilibrate.
            float tv = PropellerModel.ThrustSpeedDerivative(spec.propeller, spec.motor,
                                                            volts, Cruise);
            Check("dT/dV at fixed throttle", tv, -0.3275f, 0.012f, "N/(m/s)",
                  "−(C_T0/J₀)·ρ·D³·n = −0.409, of which the shaft speeding up under "
                  + "a lighter load returns +0.082");

            // A prop that did NOT respond would be a stiffer damper, not a softer
            // one. Worth pinning: it is the difference between the derivative being
            // a property of the propeller and a property of the whole drivetrain.
            float n = PropellerModel.RevsPerSecond(omega);
            float frozen = -(spec.propeller.ct0 / spec.propeller.zeroThrustJ)
                           * AeroDynamics.AirDensity
                           * spec.propeller.diameterM * spec.propeller.diameterM
                           * spec.propeller.diameterM * n;
            Check("dT/dV, shaft held fixed", frozen, -0.4091f, 0.006f, "N/(m/s)",
                  "the closed form at constant n — the free shaft recovers 20 % of it");

            // ---- the two damping ratios ----
            float zeta = spec.PhugoidDamping(Cruise, thrust, volts, out float classical);

            Check("zeta, constant thrust (classical)", classical, 0.0780f, 0.003f, "",
                  "D/(√2·W) = 2.163/(1.4142 × 19.613) — what A5 was comparing against");
            Check("zeta, with the propeller", zeta, 0.1645f, 0.006f, "",
                  "(∂D/∂V − ∂T/∂V)/(2·m·ω_n) at ω_n = 0.9460 rad/s");

            float share = zeta > 1e-6f ? (zeta - classical) / zeta : 0f;
            Check("propeller share of the damping", share * 100f, 52.6f, 2.0f, "%",
                  "more than half the phugoid damping on this aeroplane is the "
                  + "propeller, not the airframe");

            float period = AircraftSpec.PhugoidPeriod(Cruise);
            float damped = period / Mathf.Sqrt(Mathf.Max(1e-6f, 1f - zeta * zeta));
            Check("phugoid period at trim", period, 6.6417f, 0.01f, "s",
                  "π√2·V/g at 14.66 m/s — undamped");
            Check("damped period at trim", damped, 6.7335f, 0.02f, "s",
                  "T/√(1−ζ²); even at ζ = 0.16 the period only lengthens 1.4 %, so "
                  + "damping cannot explain a period error of tens of percent");

            // ---- the period, and why the frozen-α form does not describe it ----
            //
            // Lanchester's frequency is entirely "how much extra lift does a speed
            // excursion make", evaluated at CONSTANT α. This aeroplane does not hold
            // α constant: the flight path swings several degrees over the cycle, the
            // airframe has to rotate to follow it, and rotating costs a pitching
            // moment that only an α perturbation can supply. So α moves in antiphase
            // with speed and cancels part of the extra lift.
            //
            // Quasi-static pitch balance, C_mα·Δα + C_mq·(c̄/2V)·q = 0 with q = γ̇,
            // closes into the path equations and gives a clean factor on ω²:
            //   k = 1 / [ 1 + (g/V)·(a_w/C_L)·(C_mq/C_mα)·(c̄/2V) ]
            LiftingSurface wing = spec.surfaces[0];
            LiftingSurface tail = spec.surfaces[1];
            float qInfTrim = 0.5f * AeroDynamics.AirDensity * Cruise * Cruise;
            float clTrim = spec.TotalMass * 9.80665f / (qInfTrim * spec.WingArea);
            Check("C_L at trim", clTrim, 0.4434f, 0.002f, "",
                  "W/(q·S) at 14.66 m/s — the lift coefficient the phugoid oscillates about");

            PanelAero aeroTrim = PanelAero.Build(spec.surfaces);
            SlipstreamState sTrim = Slipstream.Solve(spec.propeller, spec.propPosLocal,
                                                     thrust, Cruise);
            float etaTrim = EffectiveQ(aeroTrim, spec, 1, sTrim, Cruise) / qInfTrim;
            Check("eta_h at TRIM power", etaTrim, 1.1586f, 0.03f, "",
                  "v_i 1.104, Δv 2.209 over 48.9 % of the tailplane ⇒ 1 + 0.489×0.324. "
                  + "NOTE the first hand value here was 1.111, computed by reusing the "
                  + "FULL-POWER immersion of 35.7 %. Less thrust means less contraction, "
                  + "so the trim tube is WIDER (r∞ 0.1228 m against 0.0898 m) and wets "
                  + "more of the tail even while pushing it less hard");

            float mac = spec.MeanChord;
            Vector3 cgT = spec.CentreOfMass;
            float armH = cgT.z - tail.rootQuarterChord.z;
            float vH2 = tail.Area * armH / (wing.Area * mac);
            // ⚠ ESTIMATE. Tail contribution only, the standard textbook form. The
            // wing's own damping is omitted and is also negative, so this UNDERSTATES
            // |C_mq| and therefore understates how much the period lengthens.
            // Published values for this term spread about 30 %.
            float cmq = -2f * etaTrim * tail.LiftSlope * vH2 * (armH / mac);
            Check("C_mq (tail only, ⚠ estimate)", cmq, -11.450f, 0.5f, "/rad",
                  "−2·η_h·a_h·V_H·(l_h/c̄) = −2 × 1.1586 × 3.611 × 0.5492 × 2.4917 — "
                  + "the pitch damping that forces the alpha perturbation");

            float sm = 0.19762f;   // static margin, as Stability() measures it
            float cma = -sm * wing.LiftSlope;
            float pTerm = (cmq / cma) * (mac / (2f * Cruise));
            float kFactor = 1f / (1f + (9.80665f / Cruise) * (wing.LiftSlope / clTrim) * pTerm);
            Check("lift-perturbation factor k", kFactor, 0.5829f, 0.03f, "",
                  "1/(1 + 0.7155) — alpha's response cancels 42 % of the lift a "
                  + "frozen-alpha phugoid would make");

            float periodCorrected = damped / Mathf.Sqrt(Mathf.Max(1e-4f, kFactor));
            Check("period with alpha free (⚠)", periodCorrected, 8.819f, 0.30f, "s",
                  "T/√k. A5 MEASURES 8.15 s, so this closed form lands +8 % — inside "
                  + "the ±6 % that C_mq's own 30 % spread already implies once the "
                  + "omitted wing term is allowed for too, and a long way better than "
                  + "the 6.73 s the frozen-alpha form gives. The period is explicable "
                  + "from geometry; it is not tight enough to gate");

            // The line that closes the open question.
            float impliedLd = zeta > 1e-6f ? 0.7071f / zeta : 0f;
            Check("L/D the naive formula would infer", impliedLd, 4.30f, 0.16f, "",
                  "0.7071/ζ at the CORRECT ζ. A5's doc block records the anomaly as "
                  + "'implying a cruise L/D of 4.4' — so the number it called "
                  + "impossible is the number the corrected theory predicts, and the "
                  + "measurement was never the thing that was wrong");

            Line("⚠ this treats the shaft as quasi-static: it settles in ~40 ms "
                 + "against a 6.6 s phugoid, so it sits at its equilibrium throughout "
                 + "the mode. A5's frozen-thrust stage is the experiment that decides "
                 + "whether this whole account is right — with ∂T/∂V removed by force, "
                 + $"the damping must fall to about {classical:0.000}");
        }

        /// <summary>Spin the shaft to equilibrium at a voltage and an axial inflow.</summary>
        private static float Equilibrium(AircraftSpec spec, float volts, float vAxial) =>
            PropellerModel.EquilibriumOmega(spec.propeller, spec.motor, volts, vAxial);

        /// <summary>Area-weighted mean coverage of one surface by the wake tube.</summary>
        private static float ImmersedFraction(PanelAero aero, AircraftSpec spec,
                                              int surface, in SlipstreamState s)
        {
            float covered = 0f, total = 0f;
            foreach (AeroPanel p in aero.Panels)
            {
                if (p.surface != surface) continue;
                total += p.area;
                if (!s.Active) continue;
                float aft = s.DiscLocal.z - p.posLocal.z;
                if (aft <= 0f) continue;
                Slipstream.IncrementAt(s, aft, out float tube);
                covered += p.area * Slipstream.Coverage(s.DiscLocal, p.posLocal,
                                                        p.spanDir, p.spanWidth, tube);
            }
            return total > 1e-6f ? covered / total : 0f;
        }

        /// <summary>
        /// Area-weighted mean local dynamic pressure over one surface (Pa), computed
        /// the way <see cref="PanelAero"/> computes it — a partly immersed strip is
        /// given the matching FRACTION of the increment over its whole width.
        ///
        /// That smearing is a discretisation choice rather than a physical claim,
        /// and it is worth being explicit about which way it errs: q is convex in
        /// velocity, so a partly immersed strip is under-read. It converges to the
        /// true integral as the panel count rises, which is one of the things
        /// test A6 measures.
        /// </summary>
        private static float EffectiveQ(PanelAero aero, AircraftSpec spec, int surface,
                                        in SlipstreamState s, float freeStream)
        {
            float sum = 0f, total = 0f;
            foreach (AeroPanel p in aero.Panels)
            {
                if (p.surface != surface) continue;
                total += p.area;

                float local = freeStream;
                if (s.Active)
                {
                    float aft = s.DiscLocal.z - p.posLocal.z;
                    if (aft > 0f)
                    {
                        float dv = Slipstream.IncrementAt(s, aft, out float tube);
                        local += dv * Slipstream.Coverage(s.DiscLocal, p.posLocal,
                                                          p.spanDir, p.spanWidth, tube);
                    }
                }
                sum += p.area * 0.5f * AeroDynamics.AirDensity * local * local;
            }
            return total > 1e-6f ? sum / total : 0f;
        }

        // ---- plumbing ----------------------------------------------------

        private static void Check(string name, float got, float expect, float tol,
                                  string units, string why)
        {
            _checks++;
            float err = got - expect;
            bool ok = Mathf.Abs(err) <= tol;
            if (!ok) _failed++;

            string line = $"{(ok ? "ok  " : "FAIL")} {name,-32} {got,10:0.#####} {units,-6}"
                          + $" (expect {expect:0.#####} ±{tol:0.#####})  — {why}";
            if (ok) Line(line);
            else Debug.LogError($"{Tag} {line}");
        }

        private static void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
