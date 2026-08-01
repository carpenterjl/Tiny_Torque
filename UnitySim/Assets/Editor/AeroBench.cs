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
