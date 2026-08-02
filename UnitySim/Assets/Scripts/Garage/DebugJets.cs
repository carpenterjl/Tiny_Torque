using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// The debug-only VTOL jet, a sibling of <see cref="DebugPlanes"/> with the
    /// same standing: a plain static factory that NOTHING enumerates — absent
    /// from presets, garage, shop, saves and LAN, structurally rather than by
    /// filter. Same provenance tags: DERIVED / CONVENTION / ⚠ ESTIMATE.
    ///
    /// <b>What this is.</b> A Harrier-class vectored-thrust jet at roughly half
    /// scale — 4.6 m span, 600 kg — flying on the SAME measured panel model as
    /// the trainer. Nothing aerodynamic is authored here that is not geometry or
    /// a section property; the VTOL behaviour comes from <see cref="JetSpec"/>'s
    /// nozzle and puffer stations, which are positions, not coefficients.
    ///
    /// <b>Declared validity edge.</b> <c>PanelAero</c> is incompressible strip
    /// theory and <c>AirData</c> carries no Mach number, so this model is honest
    /// to about M 0.3 ≈ 100 m/s and silent about everything faster. The engine
    /// (9 kN, no ram or altitude lapse modelled) could push the airframe far past
    /// that; the model does not stop it, it just stops being right. That is the
    /// same class of declared omission as the trainer's missing fuselage aero —
    /// stated, bounded, and not papered over with a fake drag rise.
    ///
    /// <b>Predictions this spec makes, before anything is run:</b>
    /// <list type="bullet">
    /// <item>Stall (power-off, 1 g) 41.8 m/s — √(2W/ρSC_Lmax), W 5 884 N,
    ///       S 4.99 m², C_Lmax 1.10</item>
    /// <item>Hover throttle 0.654 — W/T_max, from the mass table and the thrust
    ///       rating and nothing else. The first hover measures this.</item>
    /// <item>Thrust-to-weight 1.53 — CONVENTION, the Pegasus/Harrier class</item>
    /// <item>Best glide ≈ 9.1 — ½√(π·AR·e/C_D0), AR 4.24, e 0.80, C_D0 0.032</item>
    /// <item>Phugoid 31.7 s at a 70 m/s cruise — π√2·V/g, coefficient-free</item>
    /// </list>
    /// </summary>
    public static class DebugJets
    {
        // ---- authored geometry, metres. Wing ROOT quarter-chord is the origin
        // of the layout, as on the trainer: a station IS its arm. The swept
        // wing's mean-aerodynamic-chord quarter-point then lands at z = −0.68
        // (DERIVED: ȳ = (b/6)(1+2λ)/(1+λ) = 1.007 m out, times tan 34°).

        public const float WingSpan = 4.60f;    // CONVENTION: half the Harrier's 9.25
        public const float RootChord = 1.49f;   // with tip 0.68 → S 4.99 m², AR 4.24
        public const float TipChord = 0.68f;    // taper 2.2:1, the fast-jet band
        public const float SweepDeg = 34f;      // CONVENTION: Harrier-class
        public const float WingY = 0.30f;       // shoulder wing
        public const float TailArm = 3.40f;     // wing root QC to tail QC
        public const float TargetMass = 600f;   // kg all-up

        /// <summary>
        /// Fuel station (m). <b>The balance term, solved not chosen</b> — the
        /// jet's version of the trainer's battery tray. Everything except fuel
        /// sums to Σm·z = −218.3 kg·m over 515 kg; wanting the CG at z = −0.62
        /// (5 % of the 1.135 m MAC ahead of the wing's mean quarter-chord at
        /// −0.68 — a fast-jet margin, CONVENTION; the tail then adds its own
        /// stability on top, and the missing-fuselage caveat from
        /// <see cref="AircraftSpec"/> applies here too) needs
        /// (−218.3 + 85·F)/600 = −0.62, so F = −1.81 m: a tank in the aft wing
        /// box, which is where the real aircraft keeps much of its fuel.
        /// <b>Move any other mass and this number changes.</b>
        /// </summary>
        public const float FuelZ = -1.81f;

        public static AircraftSpec HydraVtol()
        {
            var spec = new AircraftSpec
            {
                name = "Hydra VTOL",

                // The motor is a STUB: a jet has no shaft in this model, and
                // StepJet never consults it — except maxVoltage, which
                // PlaneInput multiplies the pilot's [0,1] throttle by on its way
                // into slot 0. Setting it to 1 makes the slot carry the plain
                // fraction back out.
                motor = JetThrottleStub(),
                propeller = default,            // no propeller — see spec.jet
                propPosLocal = Vector3.zero,    // unused on the jet path

                jet = new JetSpec
                {
                    maxThrustN = 9000f,         // CONVENTION: T/W 1.53, Pegasus class
                    spoolTau = 0.35f,           // ⚠ ESTIMATE — see JetSpec
                    // Symmetric about the CG (z −0.62), under the wing roots:
                    // balanced hover by geometry.
                    nozzleForeLocal = new Vector3(0f, -0.35f, 0.28f),
                    nozzleAftLocal = new Vector3(0f, -0.35f, -1.52f),
                    nozzleMinDeg = 0f,
                    nozzleMaxDeg = 98.5f,       // CONVENTION: the Harrier's travel
                    nozzleRateDegPerS = 50f,
                    // Extremities, where a puffer belongs: moment is force × arm.
                    pufferNoseLocal = new Vector3(0f, 0f, 3.10f),
                    pufferTailLocal = new Vector3(0f, 0f, -3.30f),
                    pufferTipLeftLocal = new Vector3(-2.2f, 0f, -1.48f),
                    pufferTipRightLocal = new Vector3(2.2f, 0f, -1.48f),
                    pufferBudgetFrac = 0.08f,   // CONVENTION — see JetSpec
                },

                // ⚠ ESTIMATE 0.11 m². Component build-up: fuselage ~0.9 m²
                // frontal at Cd 0.07 → 0.063, intakes and canopy 0.030,
                // pylons/probes/interference 0.017. Retired by a coastdown.
                // Surface PROFILE drag is per-strip in PanelAero, as always.
                parasiticCdA = 0.11f,

                fuselageSize = new Vector3(0.95f, 1.05f, 6.90f),
                fuselageCentre = new Vector3(0f, 0f, -0.35f),
                gearRadius = 0.16f,
                wingIndex = 0,
            };

            spec.surfaces.Add(Wing());
            spec.surfaces.Add(Tailplane());
            spec.surfaces.Add(Fin());

            AddMasses(spec);

            // Bicycle gear plus outriggers, the Harrier's arrangement. All feet
            // reach the same depth so the stance is level; mains behind the CG.
            spec.gearLocal.Add(new Vector3(0f, -1.00f, 2.20f));    // nose
            spec.gearLocal.Add(new Vector3(0f, -1.00f, -1.30f));   // main tandem
            spec.gearLocal.Add(new Vector3(-2.10f, -1.00f, -1.48f)); // outrigger L
            spec.gearLocal.Add(new Vector3(2.10f, -1.00f, -1.48f));  // outrigger R
            return spec;
        }

        // ---- surfaces ----------------------------------------------------

        /// <summary>⚠ ESTIMATEs throughout, Re ≈ 10⁷, thin fast-jet section:
        /// C_Lmax 1.10 (thin sections stall lower than a trainer's Clark-Y),
        /// profile C_d 0.008, α₀ −1° (mild camber), Oswald 0.80 for the swept
        /// tapered planform. Each retired by section data at matching Re.</summary>
        private static Airfoil JetSection()
        {
            var a = Airfoil.Default();
            a.clMax = 1.10f;
            a.profileCd = 0.008f;
            a.alphaZeroLift = -1.0f * Mathf.Deg2Rad;
            a.oswald = 0.80f;
            return a;
        }

        private static LiftingSurface Wing() => new LiftingSurface
        {
            name = "wing",
            rootQuarterChord = new Vector3(0f, WingY, 0f),
            semiSpan = 0.5f * WingSpan,
            rootChord = RootChord,
            tipChord = TipChord,
            dihedralDeg = -8f,             // CONVENTION: Harrier anhedral — a
                                           // shoulder wing is already stable in
                                           // roll and the anhedral takes some back
            sweepDeg = SweepDeg,
            incidenceDeg = 1.5f,           // CONVENTION, low for a fast wing
            washoutDeg = -2.0f,            // root stalls first, same as the trainer
            mirrored = true,
            vertical = false,
            control = ControlAxis.Aileron,
            controlChordFrac = 0.30f,
            controlSpanStart = 0.55f,
            controlSpanEnd = 0.95f,
            controlMaxDeg = 20f,
            aspectRatioScale = 1f,
            panelsPerSide = 8,
            airfoil = JetSection(),
        };

        private static LiftingSurface Tailplane() => new LiftingSurface
        {
            name = "tailplane",
            rootQuarterChord = new Vector3(0f, 0.10f, -TailArm),
            semiSpan = 1.05f,
            rootChord = 0.50f,             // S_h 0.84 m² → DERIVED V_H ≈ 0.40
            tipChord = 0.30f,              // against the wing MAC arm of 2.72 m —
                                           // low by trainer standards, normal for
                                           // a fast jet
            dihedralDeg = -12f,            // CONVENTION: the Harrier's drooped tail
            sweepDeg = 30f,
            incidenceDeg = 0f,
            washoutDeg = 0f,
            mirrored = true,
            vertical = false,
            control = ControlAxis.Elevator,
            controlChordFrac = 0.45f,
            controlSpanStart = 0f,
            controlSpanEnd = 1f,
            controlMaxDeg = 20f,
            aspectRatioScale = 1f,
            panelsPerSide = 4,
            airfoil = JetTailSection(),
        };

        private static LiftingSurface Fin() => new LiftingSurface
        {
            name = "fin",
            rootQuarterChord = new Vector3(0f, 0.40f, -3.30f),
            semiSpan = 0.95f,              // height
            rootChord = 0.85f,             // S_v 0.62 m² → DERIVED V_V ≈ 0.070
            tipChord = 0.45f,
            mirrored = false,
            vertical = true,
            control = ControlAxis.Rudder,
            controlChordFrac = 0.35f,
            controlSpanStart = 0f,
            controlSpanEnd = 1f,
            controlMaxDeg = 25f,
            // ⚠ ESTIMATE 1.5 — the same end-plate argument, and the same
            // unretired uncertainty, as the trainer's fin. Every yaw result
            // inherits it.
            aspectRatioScale = 1.5f,
            panelsPerSide = 5,
            airfoil = JetTailSection(),
        };

        /// <summary>Symmetric tail sections: α₀ = 0 exactly (no camber to have),
        /// C_Lmax 0.90 ⚠ ESTIMATE, thin section.</summary>
        private static Airfoil JetTailSection()
        {
            var a = Airfoil.Default();
            a.clMax = 0.90f;
            a.profileCd = 0.008f;
            a.alphaZeroLift = 0f;
            a.oswald = 0.80f;
            return a;
        }

        // ---- mass table ---------------------------------------------------

        /// <summary>
        /// 600 kg as lumps, the CG and inertia both derived from it. The wing is
        /// six stations along the span — same reason as the trainer: roll
        /// inertia IS spanwise separation — and each station sits at its own
        /// local quarter-chord, so the sweep moves wing mass aft exactly as far
        /// as it moves wing area aft. Fuel is the balance-solved term
        /// (<see cref="FuelZ"/>).
        /// </summary>
        private static void AddMasses(AircraftSpec spec)
        {
            var m = spec.masses;

            // Engine mid-fuselage, wrapped around the CG — which is WHY the
            // Pegasus layout can hover: the heavy thing is between the nozzles.
            m.Add(new MassComponent("engine", new Vector3(0f, -0.05f, -0.60f), 180f));
            m.Add(new MassComponent("fuel", new Vector3(0f, -0.10f, FuelZ), 85f));

            m.Add(new MassComponent("fuselage fwd", new Vector3(0f, 0f, 2.00f), 55f));
            m.Add(new MassComponent("fuselage mid", new Vector3(0f, 0f, -0.30f), 50f));
            m.Add(new MassComponent("fuselage aft", new Vector3(0f, 0f, -2.80f), 45f));
            m.Add(new MassComponent("cockpit+avionics", new Vector3(0f, 0.10f, 2.60f), 40f));

            // Wing structure: 70 kg over six stations at the centroids of equal
            // thirds of each semi-span, each at its local quarter-chord (the
            // sweep and anhedral applied to the STATION, not just the surface).
            const float wingMass = 70f;
            float[] stations = { 2.3f / 6f, 2.3f / 2f, 2.3f * 5f / 6f };
            float per = wingMass / (stations.Length * 2);
            foreach (float x in stations)
            {
                float z = -x * Mathf.Tan(SweepDeg * Mathf.Deg2Rad);
                float y = WingY - x * Mathf.Tan(8f * Mathf.Deg2Rad);
                m.Add(new MassComponent("wing", new Vector3(x, y, z), per));
                m.Add(new MassComponent("wing", new Vector3(-x, y, z), per));
            }

            m.Add(new MassComponent("tail surfaces", new Vector3(0f, 0.25f, -TailArm), 35f));
            m.Add(new MassComponent("nose gear", new Vector3(0f, -0.60f, 2.20f), 12f));
            m.Add(new MassComponent("main+outrigger gear", new Vector3(0f, -0.60f, -1.30f), 28f));
        }

        // ---- powerplant stub ----------------------------------------------

        /// <summary>Not an engine — a unit scale for the throttle slot. Slot 0
        /// carries volts on a propeller aircraft; with maxVoltage = 1 the same
        /// plumbing carries the pilot's plain [0,1], and <c>StepJet</c> divides
        /// it back out so a scripted pilot writing real volts stays honest too.
        /// Every other field is the default and nothing reads it.</summary>
        private static MotorParams JetThrottleStub()
        {
            var p = MotorParams.Default();
            p.maxVoltage = 1f;
            return p;
        }
    }
}
