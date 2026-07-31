using UnityEngine;
using AIHWSim.Vehicles;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Full-scale reference vehicles, for validating the physics engine against
    /// a real car with published numbers. <b>Not game content.</b>
    ///
    /// Deliberately NOT in <see cref="VehiclePresets.All"/>: that array is what
    /// <c>DisplayNames()</c> enumerates, and it feeds every picker in the game.
    /// Nothing here is drivable from a menu, saveable through
    /// <see cref="VehicleLibrary"/> (which hides anything over 50 kg anyway), or
    /// reachable from the garage. The only consumers are the physics-debug
    /// bootstrap and its headless validator, and they call this directly rather
    /// than by name lookup.
    ///
    /// <b>Provenance is recorded per field, and that is the whole point.</b> The
    /// exercise is comparing simulated behaviour against reality, so a number
    /// whose origin is unrecorded is worse than no number: it invites a
    /// measurement to be read as a verdict on the engine when it is really a
    /// verdict on a guess. Four categories are used throughout:
    ///   PUBLISHED — Volkswagen or a road test
    ///   MEASURED  — off the 1:1 Blender model, via build_tiguan_part.py
    ///   ESTIMATE  — engineering judgement, in a defensible band
    ///   FICTION   — chosen to make the model work; do not measure against it
    /// </summary>
    public static class DebugVehicles
    {
        // ==================== VW Tiguan 1.4 TSI ACT SEL (Mk2 AD1, 2017) =====
        //
        // PUBLISHED: length 4.486, width 1.839, height 1.632, wheelbase 2.681,
        //            clearance 0.189, tracks 1.585/1.576, kerb 1500 kg,
        //            Cd 0.31, frontal area 2.47 m2, 110 kW.
        // MEASURED : every dimension above reproduced by the model to <0.5 mm;
        //            loaded wheel centre 0.349, free tyre radius 0.3588.
        //
        // GEOMETRY, and why the numbers look the way they do:
        //
        //   bodySize.y = 1.443 = roof 1.632 - clearance 0.189. A single box
        //   cannot be both "1.632 tall" and "starts 0.189 up", so it is the
        //   SOLID body: the box bottom lands at exactly 0.189 above ground by
        //   construction, and the roof rails (+0.041) and door mirrors (width
        //   2.099 vs 1.839) stick out of it because neither is solid.
        //
        //   localPos.y = -0.5615 is the chassis origin above the wheel-centre
        //   plane: (0.189 + 1.632)/2 - 0.349. It is PASTED from the exporter's
        //   VEHJSON block, which bakes the same offset into the FBX. If the two
        //   ever disagree the car floats or sinks and nothing reports it.
        //
        //   The axles are symmetric at z = +-1.3405 (sum 2.681, the published
        //   wheelbase exactly) because the export is centred on the wheel set,
        //   not on the body. The body is what sits off-centre: front overhang
        //   0.894 against rear 0.911.

        // TWO DIFFERENT OFFSETS, and collapsing them into one is a mistake the
        // static probe caught rather than a subtlety worth trusting yourself on:
        //
        //   BodyDrop  0.5615  chassis origin above the WHEEL-CENTRE PLANE. This
        //                     is what build_tiguan_part.py bakes into the FBX,
        //                     and it is what makes the mesh sit right.
        //   HubY     -0.4115  chassis origin to the WHEELCOLLIDER ORIGIN, which
        //                     is the TOP OF TRAVEL, not the wheel centre. The hub
        //                     hangs half a travel below it at static ride:
        //                       -0.5615 + 0.5 * 0.30 = -0.4115
        //
        // Set the collider to BodyDrop and the car rides 150 mm high on a
        // suspension already at its stop. Measured: it did.
        private const float BodyDrop = 0.5615f;   // pasted from VEHJSON
        // Derived, not guessed: the hub rests (1 - targetPosition) * Travel below
        // the collider origin (see TargetF), so putting the hub at the measured
        // loaded radius means placing the origin that far above it.
        private const float HubY = -BodyDrop + (1f - TargetF) * Travel;
        private const float AxleZ = 1.3405f;   // = wheelbase/2, pasted from VEHJSON
        private const float HalfTrackF = 0.7925f;
        private const float HalfTrackR = 0.7880f;
        private const float LoadedRadius = 0.349f;

        /// <summary>
        /// Chassis-origin height above the GROUND at rest: (0.189 + 1.632)/2, the
        /// centre of the solid body box. Spawn the car here and it settles;
        /// spawn it at 0 and it starts a metre underground.
        ///
        /// Public because every scene that spawns this car needs it and the
        /// alternative is the literal 0.9105 appearing in three files, one of
        /// which would eventually be wrong.
        /// </summary>
        public const float TiguanChassisRestY = 0.9105f;

        // Sprung mass per corner at a 60/40 split, used to derive the springs.
        // 1400 kg sprung (1500 total - 4x25 unsprung): front 420 kg / 4120 N,
        // rear 280 kg / 2747 N.
        private const float SpringF = 30000f;  // N/m -> 1.345 Hz heave
        private const float SpringR = 23000f;  // N/m -> 1.443 Hz heave
        private const float Travel = 0.30f;    // FICTION, see Corner()

        // Where each corner rests. MEASURED, and it took two wrong answers to
        // get here — which is exactly why the static probe runs before anything
        // dynamic is believed.
        //
        // What the probe established, on all four corners and to within 0.002:
        //
        //     rest_compression = 1 - targetPosition
        //
        // where rest_compression is CarVehicle.GetSuspensionCompression
        // (0 = fully compressed, 1 = full droop) — the OPPOSITE sense to Unity's
        // targetPosition (0 = extended, 1 = compressed).
        //
        // The important part is what that relationship does NOT contain: SPRING
        // RATE or LOAD. Front and rear were measured at 30 kN/m and 23 kN/m
        // carrying 4120 N and 2747 N, and both landed on 1 - targetPosition.
        // So this model has NO LOAD-DEPENDENT STATIC SAG: the rest height is
        // whatever targetPosition says, and the spring rate governs the response
        // to disturbance rather than the ride height. A real car's ride height
        // falls when you load it; this one's does not. Declare it rather than
        // deriving spring numbers from a sag that is not there — the first
        // attempt computed targetPosition = 1 - (0.5 + W/(k*D)) and put the car
        // 150 mm high on a bottomed suspension.
        //
        // Both ends therefore share one value, so the car sits LEVEL. The 60/40
        // load split does not come from here — it comes from where the centre of
        // mass is, which is why it survives this change (measured 60.19 %).
        //
        // 0.0422 leaves ~0.288 m of bump travel and ~0.012 m of droop, and puts
        // the hub at exactly the measured loaded radius. Bump is the direction
        // that matters; droop only matters over a crest.
        private const float TargetF = 0.0422f;
        private const float TargetR = 0.0422f;

        public static VehicleDesign VwTiguan()
        {
            var d = new VehicleDesign
            {
                name = "DEBUG VW Tiguan 1.4 TSI",
                bodyShape = BodyShape.Tiguan,
                bodySize = new Vector3(1.839f, 1.443f, 4.486f),   // PUBLISHED/derived
                bodyColor = new Color(0.05f, 0.07f, 0.15f),       // unused: paint is not tintable
                mass = 1400f,                                     // chassis; +4x25 wheels = 1500 kerb
                useCompositeMass = true,

                // ESTIMATE. Back-solved so the composite lands at 60 % front and
                // h_cg 0.62 m — neither of which Volkswagen publishes, and both of
                // which set load transfer, so P2 measures them rather than trusting.
                chassisCoM = new Vector3(0f, -0.2819f, 0.2957f),

                // Steering. 35 deg road-wheel lock is an ESTIMATE from the ~5.0 m
                // kerb turning circle; 60 deg/s is a fast hand rate through a
                // ~14.5:1 rack. servoStallNm = 0 means an ideal actuator, which
                // also makes CarVehicle's SteerTrailM/SteerFrictionNm provably
                // unreachable — a real car has power steering, and leaving them
                // live would add an RC servo's friction to a 1500 kg car.
                steerRate = 60f,
                servoStallNm = 0f,
                ackermannPct = 100f,

                // PUBLISHED. Cd.A = 0.31 x 2.47 = 0.7657 m2, and it has an
                // independent cross-check: at the published 200 km/h it implies
                // 89.6 kW at the wheels from a 110 kW crank, i.e. an 81 % driveline.
                // That self-consistency is what makes the coastdown the strongest
                // single test available here.
                dragCd = 0.31f,
                frontalAreaM2 = 2.47f,

                // Zero, deliberately. Tyre forces are applied at the contact
                // point, so this model has NO ROLL CENTRE and already overstates
                // the roll moment by ~25 %; an anti-roll bar on top would
                // double-count. Spring-only roll stiffness is 66 240 N.m/rad.
                antiRoll = 0f,

                // Zero, deliberately: this is the number that lets a coastdown
                // measure aerodynamics instead of measuring itself. Unity's
                // linear damping is per-second and therefore scales with MASS —
                // the RC default of 0.02 is 900 N at 30 m/s on 1500 kg, more than
                // twice the car's true drag.
                linearDamping = 0f,
                angularDamping = 0.05f,      // Unity's own default; 0.5 is a fake yaw damper
                maxDepenetrationVel = 5f,

                // 2 cm — the same fraction of a 0.349 m wheel that the global 2 mm
                // is of a 0.033 m one. Applied to this car's chassis box only;
                // PhysicsTuning's global is untouched.
                contactOffset = 0.02f,

                // Sized against the wheel's own inertia, which is what the PhysX
                // static-hold constraint actually releases against. At the RC
                // default the constraint never lets go and the car cannot move.
                stickyPhantomNm = 2800f,

                // FICTION, declared. Brakes: 2400 N.m is roughly full-pedal wheel
                // torque; the 0.35 rear bias exists because this engine has no
                // proportioning valve and an equal torque locks the rears first
                // and spins the car.
                maxBrakeTorque = 2400f,
                handbrakeTorque = 1000f,

                // No sensors: nothing here needs a camera, and not having one
                // keeps the debug scene cheap. No batteries = a stiff infinite
                // rail, i.e. no voltage sag confounding a drive measurement.
                imuVibration = 0f,
                wheelVelNoiseStd = 0f,
                wheelVelQuantCpr = 0,
            };

            // FWD: the front wheels both steer and drive. CarVehicle treats
            // `powered` and `allowsSteering` independently, so this needs no
            // special case.
            d.wheels.Add(Corner("FL", -HalfTrackF, +AxleZ, true, true, SpringF, TargetF, 0.30f, 1.00f));
            d.wheels.Add(Corner("FR", +HalfTrackF, +AxleZ, true, true, SpringF, TargetF, 0.30f, 1.00f));
            d.wheels.Add(Corner("RL", -HalfTrackR, -AxleZ, false, false, SpringR, TargetR, 0.32f, 0.35f));
            d.wheels.Add(Corner("RR", +HalfTrackR, -AxleZ, false, false, SpringR, TargetR, 0.32f, 0.35f));
            return d;
        }

        /// <param name="targetPos">
        /// Rest point in travel. Static ride sits at targetPos - W/(k*D), so
        /// 0.958 front / 0.898 rear put BOTH ends at mid-travel under their own
        /// weight — i.e. the car sits level with a 60/40 split by geometry.
        ///
        /// <b>The 0.30 m travel is FICTION and this is where it comes from.</b>
        /// targetPosition is capped at 1, so a mid-travel static ride demands
        /// k*D >= 2*W. Real travel (0.19 m) would force a 43 kN/m spring and a
        /// 1.62 Hz heave frequency — a sports car, not an SUV. The cause is that
        /// this model has no progressive bump stop, where a real car carries
        /// 0.10-0.15 m of static deflection and lets a rising rate take the last
        /// third. Given the choice, make the TRAVEL fictional rather than the
        /// FREQUENCY: 1.3-1.4 Hz is a published, falsifiable, measurable property
        /// and P3 measures it, whereas no test here measures bump travel.
        /// </param>
        private static WheelSpec Corner(string name, float x, float z,
                                        bool steers, bool powered,
                                        float spring, float targetPos,
                                        float zeta, float brakeScale)
        {
            var w = new WheelSpec
            {
                name = name,
                localPos = new Vector3(x, HubY, z),
                radius = LoadedRadius,      // MEASURED loaded centre height
                allowsSteering = steers,
                steerAngle = steers ? 35f : 0f,
                powered = powered,
                wheelStyle = z > 0f ? 13 : 14,   // tiguan / tiguan_r (rear disc)

                suspStiffness = spring,
                suspDampingRatio = zeta,
                suspTravel = Travel,
                suspTargetPos = targetPos,
                // MUST stay 0. SuspensionGeometry clamps the spring to
                // [50, 4000] N/m and travel to [0.005, 0.12] — but only on the
                // suspLength > 0 branch. A 30 kN/m spring survives solely because
                // this takes the identity path. It is an authoring constraint,
                // not a preference.
                suspLength = 0f,
                suspAngleDeg = 0f,

                // ESTIMATE: 19" alloy ~12 kg + 235/50R19 ~11 kg + hub ~2 kg.
                unsprungMassKg = 25f,
                // ESTIMATE: a tyre is closer to a ring than to a disc, so this is
                // authored rather than derived. 1.5-1.8 is the usual band.
                spinInertiaKgM2 = 1.60f,
                // 0.01 x (4.41 / 3679): holds the parasitic PhysX residual at the
                // same ABSOLUTE force it has on an RC car, instead of letting it
                // scale with a 3.7 kN corner load into ~40 N of phantom drag.
                brushEps = 1.2e-5f,

                // gripMult 1.0 is THE ONE FITTED PARAMETER in this whole vehicle;
                // everything else is predicted. loadSensitivity 0.15 is the only
                // thing in the tyre model that makes a nose-heavy car understeer
                // at all — see the P5 note in the plan.
                gripMult = 1.0f,
                loadSensitivity = 0.15f,
                // A passenger radial grows <0.5 % at speed. Setting 0 makes the
                // whole ballooning block (normalised to a hard-coded 250 rad/s,
                // i.e. an RC rim speed) provably unreachable rather than
                // approximately harmless.
                balloonPct = 0f,
                brakeScale = brakeScale,
                massKg = 25f,
            };

            if (powered) w.motor = TiguanMotor();
            return w;
        }

        /// <summary>
        /// <b>Entirely FICTION, and it has to be.</b> The rig models one DC motor
        /// per driven wheel: no gearbox, no torque curve, no clutch. A 1.4 TSI
        /// through six ratios is not that shape and cannot be made into it by
        /// choosing constants.
        ///
        /// So the numbers are picked for one property — that the TYRES, not the
        /// motor, are the limit at every speed the tests use — and the honest
        /// consequence is stated rather than papered over: <b>acceleration is not
        /// a physics test on this vehicle.</b> A 0-100 km/h time here measures
        /// this made-up motor. What IS a valid test is a traction-limited launch,
        /// which measures front-axle grip under rearward load transfer.
        /// </summary>
        private static MotorParams TiguanMotor()
        {
            var m = MotorParams.Default();
            m.maxVoltage = 400f;
            m.gearRatio = 10f;
            m.efficiency = 0.95f;
            m.kt = 0.2538f;          // no-load wheel omega 157.6 rad/s = 55 m/s
            m.resistance = 0.386f;   // stall wheel torque 2500 N.m, ~2x traction limit
            m.maxCurrent = 500f;
            m.noLoadCurrent = 2.0f;
            m.rotorInertia = 0.002f; // reflected 0.2 kg.m2 against the wheel's 1.6
            m.viscousDamping = 0f;
            m.coulombScale = 0f;
            m.escPwmSteps = 0;
            m.escDeadbandV = 0f;
            m.escTimeConstMs = 0f;
            m.escSlewVPerS = 0f;
            m.escDragBrakePct = 0f;
            m.escBrakeStrengthPct = 100f;
            m.escReverseLockMs = 150f;
            // 0.2 rad/s = 0.07 m/s at this radius: the same GROUND speed the
            // legacy 2 rad/s encodes at 33 mm. Without it the ESC calls the car
            // stationary below 0.7 m/s and drives when it should brake.
            m.escMovingOmega = 0.2f;
            return m;
        }
    }
}
