using System.Collections.Generic;
using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Debug-only aircraft, the way <see cref="DebugVehicles"/> is the debug-only
    /// car: a plain static factory that NOTHING enumerates. It is absent from
    /// <c>VehiclePresets.All</c>, from the garage pickers and from the shop, and it
    /// is not a <see cref="VehicleDesign"/> at all — so there is no path by which a
    /// race, a save file or a LAN session could reach it. That is structural, not a
    /// filter someone has to remember to keep.
    ///
    /// <b>Provenance tags used below</b>, same discipline as the Tiguan:
    /// <list type="bullet">
    /// <item>DERIVED — computed from other authored geometry; cannot disagree.</item>
    /// <item>CONVENTION — a standard design value for this class of model.</item>
    /// <item>⚠ ESTIMATE — an empirical number with no source in hand. Each one
    ///       names what would retire it.</item>
    /// </list>
    ///
    /// <b>Scale.</b> The world is real metres and RC vehicles here are true size —
    /// a car in this project is a genuine 40 cm object. So this is a genuine
    /// 1.4 m-span, 2 kg model aeroplane, not a scaled-down airliner.
    /// </summary>
    public static class DebugPlanes
    {
        // ---- authored geometry, all in metres ----------------------------
        // The wing quarter-chord is the origin of the whole layout: it is the
        // reference every longitudinal number is quoted against, so putting it at
        // z = 0 means a station IS its arm.

        public const float WingSpan = 1.40f;      // CONVENTION: .40-size trainer
        public const float WingChord = 0.24f;     // CONVENTION: gives AR 5.83
        public const float WingY = 0.06f;         // high wing, above the fuselage
        public const float TailArm = 0.60f;       // wing AC to tail AC
        public const float TargetMass = 2.00f;    // kg, all-up

        /// <summary>
        /// Battery station (m ahead of the wing quarter-chord). <b>This is the only
        /// number here chosen to hit a target rather than measured or derived</b>,
        /// and that is deliberate: it is the balance adjustment a real model has —
        /// you slide the pack until the aeroplane balances on your fingertips.
        ///
        /// Everything except the battery sums to Σm·z = −0.0098 kg·m over 1.75 kg.
        /// Wanting the CG at 25.6 % of a 0.24 m chord — i.e. z = −0.0014 m, since
        /// the wing leading edge is at z = +0.06 — needs
        /// (−0.0098 + 0.25·B)/2.00 = −0.0014, so B = 0.028 m.
        ///
        /// That puts the pack just under the wing leading edge, which is where a
        /// trainer's battery hatch actually is. <b>Move any other mass and this
        /// number changes</b>; it is solved from the parts list, not chosen.
        /// </summary>
        public const float BatteryZ = 0.028f;

        /// <summary>
        /// A .40-size sport trainer: high wing, tricycle gear, direct-drive
        /// outrunner on a 10×6.
        ///
        /// <b>Predictions this spec makes, before anything is run:</b>
        /// <list type="bullet">
        /// <item>Stall speed 8.91 m/s — √(2W/ρSC_Lmax), W 19.61 N, S 0.336 m², C_Lmax 1.20</item>
        /// <item>Best glide 10.9 — ½√(π·AR·e/C_D0), AR 5.83, e 0.85, C_D0 0.0329</item>
        /// <item>Static thrust 11.2 N at 8 120 rpm — the motor/prop equilibrium,
        ///       giving thrust-to-weight 0.57, which is a trainer and not a rocket</item>
        /// <item>Top speed ≈ 21 m/s level, where prop thrust meets airframe drag</item>
        /// <item>Phugoid 7.25 s at a 16 m/s cruise — π√2·V/g, no coefficients in it</item>
        /// </list>
        /// Every one of those is a closed form the flight tests measure against.
        /// </summary>
        public static AircraftSpec SportRc()
        {
            var spec = new AircraftSpec
            {
                name = "RC Sport Trainer",
                propeller = PropellerSpec.Trainer10x6(),
                motor = TrainerMotor(),
                // Prop disc on the thrust line, ahead of everything. It sits on the
                // fuselage centreline rather than the CG, so the small nose-up
                // couple from a thrust line above the CG appears on its own.
                propPosLocal = new Vector3(0f, 0f, 0.50f),

                // ⚠ ESTIMATE 0.0058 m². Component build-up: fuselage 0.0022,
                // unfaired gear and wheels 0.0026, interference 0.0010. Retired by
                // a measured coastdown, exactly as the car's Cd·A was.
                // Wing and tail PROFILE drag is NOT in here — the panels apply it
                // per strip, and counting it twice would quietly halve the glide.
                parasiticCdA = 0.0058f,

                fuselageSize = new Vector3(0.09f, 0.11f, 1.05f),
                fuselageCentre = new Vector3(0f, 0f, -0.05f),
                gearRadius = 0.030f,
                wingIndex = 0,
            };

            // ---- lifting surfaces ----
            spec.surfaces.Add(Wing());
            spec.surfaces.Add(Tailplane());
            spec.surfaces.Add(Fin());

            // ---- mass table: the CG and the inertia both come from here ----
            AddMasses(spec);

            // ---- tricycle gear; mains BEHIND the CG or it will not rotate ----
            spec.gearLocal.Add(new Vector3(0f, -0.16f, 0.36f));      // nose
            spec.gearLocal.Add(new Vector3(-0.13f, -0.16f, -0.06f)); // main L
            spec.gearLocal.Add(new Vector3(0.13f, -0.16f, -0.06f));  // main R

            return spec;
        }

        // ---- surfaces ----------------------------------------------------

        private static Airfoil TrainerSection()
        {
            var a = Airfoil.Default();
            // ⚠ ESTIMATE, Re ≈ 2×10⁵, flat-bottom / Clark-Y-like section.
            a.clMax = 1.20f;
            a.profileCd = 0.012f;
            // ⚠ ESTIMATE −3.5°. This is the field that makes the whole layout
            // work: a cambered section already carries lift at zero geometric
            // incidence, which is WHY a trainer can use 2.5° of wing incidence and
            // still fly with the fuselage level. With a symmetric section the same
            // aeroplane would need about 5.6°, which no one builds.
            a.alphaZeroLift = -3.5f * Mathf.Deg2Rad;
            a.oswald = 0.85f;   // ⚠ ESTIMATE, straight rectangular wing
            return a;
        }

        private static LiftingSurface Wing() => new LiftingSurface
        {
            name = "wing",
            rootQuarterChord = new Vector3(0f, WingY, 0f),
            semiSpan = 0.5f * WingSpan,
            rootChord = WingChord,
            tipChord = WingChord,          // rectangular: root stalls first
            dihedralDeg = 5f,              // CONVENTION: aileron trainer band 3–6°
            sweepDeg = 0f,
            incidenceDeg = 2.5f,           // CONVENTION, and see TrainerSection()
            washoutDeg = -2.0f,            // tip stalls last — this is what keeps a
                                           // wing drop from becoming a spin
            mirrored = true,
            vertical = false,
            control = ControlAxis.Aileron,
            controlChordFrac = 0.25f,      // τ = 0.609 by thin-airfoil flap theory
            controlSpanStart = 0.50f,
            controlSpanEnd = 0.95f,
            controlMaxDeg = 15f,
            aspectRatioScale = 1f,
            panelsPerSide = 8,
            airfoil = TrainerSection(),
        };

        private static LiftingSurface Tailplane() => new LiftingSurface
        {
            name = "tailplane",
            rootQuarterChord = new Vector3(0f, 0.01f, -TailArm),
            semiSpan = 0.25f,              // 0.50 m span
            rootChord = 0.148f,            // S_h = 0.0740 m², 22 % of wing area
            tipChord = 0.148f,             // DERIVED V_H ≈ 0.55 at this arm
            dihedralDeg = 0f,
            incidenceDeg = 0f,
            washoutDeg = 0f,
            mirrored = true,
            vertical = false,
            control = ControlAxis.Elevator,
            controlChordFrac = 0.40f,
            controlSpanStart = 0f,
            controlSpanEnd = 1f,
            controlMaxDeg = 25f,
            aspectRatioScale = 1f,
            panelsPerSide = 4,
            airfoil = TailSection(),
        };

        private static LiftingSurface Fin() => new LiftingSurface
        {
            name = "fin",
            rootQuarterChord = new Vector3(0f, 0.02f, -TailArm),
            semiSpan = 0.20f,              // height
            rootChord = 0.133f,            // S_v = 0.0266 m², DERIVED V_V ≈ 0.034
            tipChord = 0.133f,
            mirrored = false,
            vertical = true,
            control = ControlAxis.Rudder,
            controlChordFrac = 0.40f,
            controlSpanStart = 0f,
            controlSpanEnd = 1f,
            controlMaxDeg = 25f,
            // ⚠ ESTIMATE 1.5. The fuselage and tailplane act as end plates and
            // raise the fin's effective aspect ratio, but by how much cannot be
            // derived inside a strip model. EVERY directional-axis result inherits
            // this uncertainty, which is why the yaw tests report Info and do not
            // gate. A lifting-line solve with the tailplane in the same system
            // would retire it.
            aspectRatioScale = 1.5f,
            panelsPerSide = 5,
            airfoil = TailSection(),
        };

        /// <summary>Tail surfaces are flat plates — symmetric, so α₀ = 0, which is
        /// not an estimate but a consequence of the section having no camber. They
        /// stall earlier than the wing because a flat plate does.</summary>
        private static Airfoil TailSection()
        {
            var a = Airfoil.Default();
            a.clMax = 0.90f;               // ⚠ ESTIMATE, thin flat plate at low Re
            a.profileCd = 0.012f;
            a.alphaZeroLift = 0f;          // symmetric section: exact
            a.oswald = 0.80f;              // ⚠ ESTIMATE, low aspect ratio
            return a;
        }

        // ---- mass table ---------------------------------------------------

        /// <summary>
        /// Where the mass actually is. Two things depend on getting this right and
        /// neither is negotiable: the CG (hence the static margin, hence whether
        /// the aeroplane is stable at all) and the inertia tensor (hence every
        /// rotation rate the tests measure).
        ///
        /// <b>The wing is six lumps, not one.</b> Roll inertia is almost entirely
        /// about how far the wing mass sits from the centreline: a single lump at
        /// the root would report I_xx ≈ 0 and the model would snap-roll like
        /// nothing that has ever flown. The three stations per side sit at the
        /// centroids of equal thirds of the semi-span, which reproduces a uniform
        /// spar's I = m·b²/12 to within 3 %.
        /// </summary>
        private static void AddMasses(AircraftSpec spec)
        {
            var m = spec.masses;

            m.Add(new MassComponent("motor+prop", new Vector3(0f, 0f, 0.46f), 0.20f));
            m.Add(new MassComponent("battery", new Vector3(0f, -0.01f, BatteryZ), 0.25f));

            m.Add(new MassComponent("fuselage fwd", new Vector3(0f, 0f, 0.30f), 0.18f));
            m.Add(new MassComponent("fuselage mid", new Vector3(0f, 0f, 0.00f), 0.19f));
            m.Add(new MassComponent("fuselage aft", new Vector3(0f, 0f, -0.35f), 0.18f));

            // Wing structure, spread along the span. 0.55 kg over six stations.
            const float wingMass = 0.55f;
            float[] stations = { 0.117f, 0.350f, 0.583f };   // centroids of thirds
            float per = wingMass / (stations.Length * 2);
            foreach (float x in stations)
            {
                m.Add(new MassComponent("wing", new Vector3(x, WingY, 0f), per));
                m.Add(new MassComponent("wing", new Vector3(-x, WingY, 0f), per));
            }

            m.Add(new MassComponent("tail surfaces", new Vector3(0f, 0.04f, -TailArm), 0.15f));
            m.Add(new MassComponent("servos+rx", new Vector3(0f, -0.02f, -0.10f), 0.15f));

            m.Add(new MassComponent("nose gear", new Vector3(0f, -0.12f, 0.36f), 0.05f));
            m.Add(new MassComponent("main gear", new Vector3(0f, -0.12f, -0.06f), 0.10f));
        }

        // ---- powerplant ----------------------------------------------------

        /// <summary>
        /// A 1000 kV outrunner on a 3S pack — the standard .40-size trainer setup.
        /// Kt follows from Kv exactly: Kt = 60/(2π·Kv), because Kv and Kt are the
        /// same constant in different units, not two measurements.
        ///
        /// <b>Two fields differ from the car's defaults and both matter.</b>
        /// <c>gearRatio = 1</c> because a prop is direct drive — there is no
        /// reduction. <c>efficiency = 1</c> for the same reason: that field is
        /// GEARBOX efficiency, and the car's 0.85 represents a real gear train this
        /// aircraft does not have. The motor's own losses already live in
        /// <c>resistance</c> and <c>noLoadCurrent</c>, so carrying 0.85 here would
        /// invent a 15 % loss that does not exist and would make every thrust,
        /// climb and speed number quietly low.
        /// </summary>
        private static MotorParams TrainerMotor()
        {
            var p = MotorParams.Default();
            p.maxVoltage = 11.1f;               // 3S LiPo nominal
            p.kt = 60f / (2f * Mathf.PI * 1000f); // DERIVED from 1000 kV = 0.009549
            p.resistance = 0.15f;               // ⚠ ESTIMATE, 35 mm outrunner
            p.gearRatio = 1f;                   // direct drive
            p.efficiency = 1f;                  // no gearbox — see above
            p.noLoadCurrent = 0.8f;             // ⚠ ESTIMATE
            p.viscousDamping = 1e-6f;
            p.maxCurrent = 30f;                 // ESC limit; the equilibrium draws
                                                // 19.3 A, so it is a guard not a cap
            p.rotorInertia = 1.5e-5f;           // ⚠ ESTIMATE; the prop's 9.14e-5
                                                // dominates the shaft anyway
            p.coulombScale = 1f;
            p.escPwmSteps = 2048;
            p.escDeadbandV = 0.05f;
            p.escTimeConstMs = 20f;             // aircraft ESCs ramp softer than car
            p.escSlewVPerS = 0f;
            return p;
        }
    }
}
