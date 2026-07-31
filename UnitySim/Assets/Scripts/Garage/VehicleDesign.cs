using System;
using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// One configurable sensor on a design. Flat fields (JsonUtility-friendly);
    /// only the fields relevant to <see cref="kind"/> are used, the rest ignored.
    /// </summary>
    [Serializable]
    public class SensorSpec
    {
        public string name = "sensor";
        public SensorType kind = SensorType.Tof;
        public Vector3 localPos = new Vector3(0f, 0.05f, 0.18f);
        public Vector3 aimEuler = Vector3.zero;   // yaw/pitch of the mount
        public int mirrorGroup = -1;              // shared id links mirror twins; -1 = unlinked

        // ToF (default range models a VL53L1X-class module)
        public float range = 4f;
        public int coneRays = 1;
        public float coneAngle = 8f;

        // Encoder: which wheel (index into the design's wheels list) it reads.
        public int wheelIndex = 0;
        public int cprTicks = 360;                // encoder counts/rev
        public float encoderGearRatio = 1f;       // 1 = on wheel; >1 = on motor shaft

        public float massKg = 0f;                 // part mass; 0 = auto by kind

        // Signal realism (all 0 = clean/legacy)
        public float noiseStd = 0f;               // Gaussian σ (sensor units)
        public float noiseQuant = 0f;             // quantization step (sensor units)
        public float driftRate = 0f;              // random-walk bias drift (units/√s)
        public float updateRateHz = 0f;           // 0 = fresh sample every control tick
        public float latencyMs = 0f;              // reported values delayed this much

        // Camera
        public int camWidth = 64;
        public int camHeight = 48;
        public float camFov = 60f;
        public float camRateHz = 10f;

        public SensorSpec Clone()
        {
            return (SensorSpec)MemberwiseClone();
        }
    }

    /// <summary>
    /// One placeable wheel. Position + heading (yaw), a per-wheel steer opt-in
    /// (optionally reversed) with its own max angle, and an optional on-board DC
    /// motor (the "powered wheel" model). Flat fields keep JsonUtility happy.
    /// </summary>
    [Serializable]
    public class WheelSpec
    {
        public string name = "wheel";
        public Vector3 localPos = new Vector3(0.083f, -0.045f, 0.152f);
        public float yaw = 0f;                // heading (deg about up)
        public float radius = 0.033f;         // 66 mm RC tire
        public int wheelStyle = 0;            // cosmetic tyre mesh: 0 slick / 1 knobby / 2 rally
        public int mirrorGroup = -1;          // shared id links mirror twins; -1 = unlinked

        // Steering
        public bool allowsSteering = false;
        public bool reverseSteering = false;
        public float steerAngle = 28f;        // this wheel's max steer angle (deg)

        // Suspension (per-wheel). Field initializers reproduce today's vehicle-wide
        // behaviour on old JSON, so pre-suspension designs load unchanged.
        public float suspStiffness = 300f;    // spring rate (N/m)
        public float suspDampingRatio = 0f;   // damping ratio ζ; 0 = legacy raw damper (15)
        public float suspTravel = 0.03f;      // suspension distance (m)
        public float suspAngleDeg = 0f;       // strut tilt about wheel-local Z; + leans top inboard
        public float suspLength = 0f;         // visible strut length (m); 0 = rigid mount / no strut (legacy)
        public float gripMult = 1f;           // friction stiffness scalar (fwd+side); <=0 treated as 1
        public float loadSensitivity = 0f;    // tire load sensitivity exponent; 0 = off (legacy)
        public float balloonPct = 0f;         // tire ballooning: max radius growth %; 0 = off

        public float massKg = 0f;             // wheel assembly mass; 0 = auto (30/190 g)

        // ---- scale-dependent constants, made authorable -------------------
        // Each of these was a hard-coded literal inside CarVehicle sized for a
        // ~40 cm, ~1.8 kg RC car. A full-scale vehicle needs different values,
        // and no continuous formula can supply them: the ten shipped designs sit
        // at eight different wheel radii, so any expression that reproduces the
        // old literal at one of them moves all the others. Since the Opus
        // mission is a bit-identical gate, "close enough" is a failure.
        //
        // So they are AUTHORED, and every initialiser here IS the literal it
        // replaced. JsonUtility leaves absent keys at their initialiser, so old
        // saved designs and all ten code presets keep exactly what they had —
        // bit-identity becomes a property of the code's shape rather than a
        // numerical coincidence. The 0 sentinels mean "use the old expression
        // verbatim" for cases where 0 is not a legal authored value.

        /// <summary>Unsprung mass at this corner (kg). Was a hard-coded 0.05 in
        /// MakeWheel — 50 g, right for an RC wheel, and about 1/500th of a real
        /// one.</summary>
        public float unsprungMassKg = 0.05f;

        /// <summary>Wheel spin inertia J (kg·m²); 0 = the legacy ½·0.05·r².
        /// The most consequential number here: at RC scale J is 2.7e-5, so a
        /// full-scale wheel left on that derivation spins up in microseconds and
        /// every drive or brake measurement becomes a wheelspin measurement.</summary>
        public float spinInertiaKgM2 = 0f;

        /// <summary>Floor on the PhysX tyre-curve stiffness; 0 = the legacy 0.01.
        /// It exists because PhysX does not honour stiffness 0 as "no friction",
        /// so the curves are scaled to nearly nothing instead. The residual is
        /// proportional to load: ≤0.05 N/wheel at a 4.4 N RC corner, but ~40 N at
        /// a 3.7 kN one — which would be 38 % of a real car's aero drag and would
        /// wreck a coastdown. NEVER flatten the curve SHAPE to fix this; a
        /// degenerate curve is recorded in CarVehicle as having hard-crashed the
        /// editor.</summary>
        public float brushEps = 0f;

        /// <summary>Suspension rest point in travel; 0 = the legacy 0.5 (mid).
        /// Couples spring rate to travel: static ride sits at
        /// targetPosition − W/(k·D), so a heavy car on realistic travel needs
        /// this above 0.5 or it rides on the bump stops.</summary>
        public float suspTargetPos = 0f;

        /// <summary>Per-wheel brake torque multiplier. The engine has no brake
        /// bias, so one torque on all four locks a real car's rears first and
        /// spins it. 1 = unchanged, and IEEE-754 guarantees x·1.0f == x exactly
        /// for every finite x, so this cannot move an existing result.</summary>
        public float brakeScale = 1f;

        // Drive motor (only used when powered)
        public bool powered = false;
        public MotorParams motor = MotorParams.Default();
        public MotorDatasheet motorDatasheet;
        public int motorEntryMode = 0;        // 0=Constants, 1=Datasheet

        public WheelSpec Clone() => (WheelSpec)MemberwiseClone();
    }

    /// <summary>
    /// One placeable aerodynamic part (wing / splitter / side dam / canard).
    /// Position + heading like a sensor; wings and canards carry an attack-angle
    /// that trades downforce against drag. Flat fields keep JsonUtility happy.
    /// </summary>
    [Serializable]
    public class AeroSpec
    {
        public string name = "wing";
        public AeroKind kind = AeroKind.Wing;
        public Vector3 localPos = new Vector3(0f, 0.08f, -0.20f);
        public float yawDeg = 0f;
        public int mirrorGroup = -1;          // shared id links mirror twins; -1 = unlinked
        public float angleDeg = 8f;           // attack angle (Wing/Canard only)
        public float sizeScale = 1f;          // 0.6..1.6; forces scale by sizeScale²
        public float massKg = 0f;             // part mass; 0 = auto by kind

        public AeroSpec Clone() => (AeroSpec)MemberwiseClone();
    }

    /// <summary>
    /// One placeable antenna — a purely cosmetic part (no sensor, no ABI, no
    /// physics). Position + heading like an aero part, plus a lean (tilt) for the
    /// rubber-duck look. Old designs have an empty list. Mirrorable like aero.
    /// </summary>
    [Serializable]
    public class AntennaSpec
    {
        public string name = "antenna";
        public Vector3 localPos = new Vector3(0f, 0.09f, -0.14f);
        public float yawDeg = 0f;
        public float tiltDeg = 15f;           // lean back from vertical (deg)
        public int antennaStyle = 0;          // 0 stub / 1 whip+tip / 2 flag / 3 twin
        public float sizeScale = 1f;          // 0.6..1.6
        public int mirrorGroup = -1;          // shared id links mirror twins; -1 = unlinked
        public float massKg = 0f;             // part mass; 0 = auto

        public AntennaSpec Clone() => (AntennaSpec)MemberwiseClone();
    }

    /// <summary>
    /// One placeable light cluster — purely cosmetic like an antenna (no
    /// physics, no sensor, on the viz layer so the on-car camera never sees
    /// it). Style picks the authored mesh: 0 = police roof light bar (its
    /// red/blue lenses strobe at runtime), 1 = off-road pod cluster (steady
    /// glow). Old designs have an empty list. Mirrorable like aero.
    /// </summary>
    [Serializable]
    public class LightSpec
    {
        public string name = "light";
        public Vector3 localPos = new Vector3(0f, 0.08f, 0f);
        public float yawDeg = 0f;
        public int style = 0;                 // 0 bar / 1 pods
        public float sizeScale = 1f;          // 0.6..1.6
        public int mirrorGroup = -1;          // shared id links mirror twins; -1 = unlinked
        public float massKg = 0f;             // part mass; 0 = auto

        public LightSpec Clone() => (LightSpec)MemberwiseClone();
    }

    /// <summary>
    /// One placeable battery pack. The first battery in the list powers the motor
    /// bus (terminal voltage sags with total current across its internal
    /// resistance); extra batteries only add mass. Old designs have an empty list
    /// = stiff infinite supply (legacy behaviour). Centerline part — no mirroring.
    /// </summary>
    [Serializable]
    public class BatterySpec
    {
        public string name = "battery";
        public Vector3 localPos = new Vector3(0f, -0.02f, -0.05f);
        public int mirrorGroup = -1;          // kept for schema symmetry; unused
        public float massKg = 0.18f;          // 2S 1300 mAh LiPo
        public float nominalV = 7.4f;
        public float internalR = 0.03f;       // pack + leads + connector (Ω)
        public float capacitymAh = 0f;        // reserved: 0 = infinite (SoC deferred)

        public BatterySpec Clone() => (BatterySpec)MemberwiseClone();
    }

    /// <summary>
    /// A user-assembled vehicle: preset body, a list of placeable wheels (some
    /// powered, some steering), and a list of sensor parts. Serialized to JSON
    /// (via JsonUtility) in the garage and rebuilt into a live vehicle by
    /// <see cref="VehicleFactory"/> for both the preview and the track spawn.
    /// </summary>
    [Serializable]
    public class VehicleDesign
    {
        public string name = "New Vehicle";
        public BodyShape bodyShape = BodyShape.Box;
        public Vector3 bodySize = new Vector3(0.20f, 0.10f, 0.42f);
        public Color bodyColor = new Color(0.20f, 0.55f, 0.95f);
        // Painted livery: base64 PNG (256×256) stamped onto the body shell's UVs
        // by the garage PAINT tab; "" = plain bodyColor. Rides the design JSON so
        // save/load, snapshots, and LAN vehicle transfer carry it automatically.
        public string liveryPng = "";
        // Which horn this car carries: 0 normal / 1 police siren / 2 air horn /
        // 3 musical / 4 clown (see ProceduralAudio.HornKey). Initializer = the
        // usual JsonUtility back-compat: old designs read as 0 and keep the
        // normal horn. Rides the design JSON, so LAN peers hear the right one.
        public int hornStyle = 0;
        public float mass = 1.6f;
        // Composite mass model: when true, total mass / CoM / inertia are computed
        // from the chassis + every part (MassProperties); false = legacy scalar.
        public bool useCompositeMass = false;
        public float steerRate = 480f;                       // steering servo slew (deg/s, no-load)
        public float servoStallNm = 0f;                      // servo stall torque; 0 = ideal (legacy)
        public float ackermannPct = 0f;                      // 0 = parallel (legacy), 100 = true Ackermann
        // Firmware this vehicle ships with: the DLL file name inside
        // Plugins/x86_64 that Autonomous mode loads. "" = the shared default
        // (car_controller.dll), which is what every pre-existing design gets
        // since JsonUtility leaves absent strings empty.
        public string controllerDll = "";
        // Vehicle-level sensor realism (0 = clean/legacy)
        public float imuVibration = 0f;                      // motor-vibration coupling into the IMU
        public float wheelVelNoiseStd = 0f;                  // ABI wheel_vel Gaussian σ (rad/s)
        public int wheelVelQuantCpr = 0;                     // ABI wheel_vel CPR quantization; 0 = ideal
        public List<WheelSpec> wheels = new List<WheelSpec>();
        public List<SensorSpec> sensors = new List<SensorSpec>();
        public List<AeroSpec> aero = new List<AeroSpec>();   // old JSON → stays empty
        public List<BatterySpec> batteries = new List<BatterySpec>(); // old JSON → empty = infinite rail
        public List<AntennaSpec> antennas = new List<AntennaSpec>();  // old JSON → empty (cosmetic)
        public List<LightSpec> lights = new List<LightSpec>();        // old JSON → empty (cosmetic)

        // Unlockable cosmetics (CosmeticCatalog ids; "" = none). Five slots, one
        // item each, rims applying to every wheel. They live on the DESIGN rather
        // than beside it so they ride the existing JSON into races, split-screen,
        // snapshots and LAN peers with no extra plumbing — the same trick
        // hornStyle and liveryPng use. Old designs read as empty strings and wear
        // nothing. Purely visual: no mass, no aero, no collider, so
        // MassProperties and every controller see an unchanged car.
        public string cosTopper = "";
        public string cosRim = "";
        public string cosOrnament = "";
        public string cosBobble = "";
        public string cosWing = "";

        // ---- vehicle-level scale-dependent constants ----------------------
        // Same contract as the WheelSpec block above: every initialiser IS the
        // literal it replaced, so absent JSON and all ten code presets are
        // unchanged by construction.
        //
        // Note the first four were ALREADY public fields on CarVehicle that
        // VehicleFactory never assigned — so every car in the game has been
        // sharing one 0.8 N·m foot brake, one 1.2 N·m handbrake and one 8 N
        // anti-roll bar regardless of what it weighs. Plumbing them is a fix in
        // its own right, and the defaults keep today's behaviour exactly.

        /// <summary>Foot-brake torque per wheel (N·m).</summary>
        public float maxBrakeTorque = 0.8f;

        /// <summary>Handbrake torque (N·m). Also the park brake the sticky-tyre
        /// hold applies at rest.</summary>
        public float handbrakeTorque = 1.2f;

        /// <summary>Anti-roll bar, in newtons per unit of NORMALISED travel
        /// difference — not a roll rate, and not physical. A full-scale car
        /// should generally set 0: tyre forces are applied at the contact point,
        /// so this model has no roll centre and already over-states the roll
        /// moment; a bar on top would double-count.</summary>
        public float antiRoll = 8f;

        /// <summary>Rigidbody linear damping (1/s). Units matter here: force
        /// scales with MASS, so 0.02 is ~0.03 N on an RC car and ~900 N at 30 m/s
        /// on 1500 kg — more than twice a real car's aero drag, and enough on its
        /// own to make a coastdown measure nothing but this number.</summary>
        public float linearDamping = 0.02f;

        /// <summary>Rigidbody angular damping (1/s). A fake yaw/roll/pitch
        /// damper; Unity's own default is 0.05.</summary>
        public float angularDamping = 0.5f;

        /// <summary>Rigidbody max depenetration velocity (m/s).</summary>
        public float maxDepenetrationVel = 2f;

        /// <summary>Phantom motor torque the sticky-tyre hold releases against
        /// (N·m). It has to overcome the wheel's own inertia to let the car move
        /// off: 0.05 gives 1837 rad/s² on an RC wheel but 0.033 rad/s² on a real
        /// one, where the constraint never releases and the car is pinned at
        /// rest.</summary>
        public float stickyPhantomNm = 0.05f;

        /// <summary>Chassis-box contact offset (m); 0 = Physics.defaultContactOffset.
        /// The global is 2 mm, tuned for a 33 mm wheel and set BeforeSceneLoad,
        /// where it cannot see a vehicle — so a full-scale car overrides its own
        /// collider rather than moving a global that every RC car reads.</summary>
        public float contactOffset = 0f;

        /// <summary>Drag coefficient; 0 = AeroDynamics.BodyCd(bodyShape).</summary>
        public float dragCd = 0f;

        /// <summary>Reference frontal area (m²); 0 = the bodySize estimate. The
        /// built-in estimate is width·height·0.9, which is a reasonable guess and
        /// no substitute for a published figure when one exists.</summary>
        public float frontalAreaM2 = 0f;

        /// <summary>Chassis centre of mass in body-local metres (composite mass
        /// model only). Was a hard-coded 30 mm drop — right for a 40 cm car,
        /// meaningless on a 4.5 m one.</summary>
        public Vector3 chassisCoM = new Vector3(0f, -0.03f, 0f);

        /// <summary>
        /// The stock car: a 1/10-scale RC (F1TENTH-style) — four wheels (steered
        /// fronts, powered rears) plus the default sensor loadout (forward camera,
        /// three ToF, a wheel encoder per wheel).
        /// </summary>
        public static VehicleDesign Default()
        {
            var d = new VehicleDesign
            {
                name = "Stock RC",
                bodyShape = BodyShape.LowRacer,
                bodySize = new Vector3(0.20f, 0.09f, 0.42f),
                ackermannPct = 100f,   // real RC front ends are near-true Ackermann
                useCompositeMass = true,
                mass = 1.0f,           // bare chassis; parts + battery bring it to ~1.8 kg
                imuVibration = 0.1f,   // mild motor shake on the IMU
            };

            // Wheels: indices 0..3 = FL, FR, RL, RR (fronts steer, rears drive).
            // Strut length = NominalArm (motion ratio 1 → identical rate/travel) with
            // the mount raised by the same 0.03 m so the hubs stay at y −0.045: the
            // stock car drives exactly as before but now shows visible coil-overs.
            d.wheels.Add(new WheelSpec { name = "wheel_fl", localPos = new Vector3(-0.083f, -0.015f, 0.152f), suspLength = 0.03f, allowsSteering = true });
            d.wheels.Add(new WheelSpec { name = "wheel_fr", localPos = new Vector3(0.083f, -0.015f, 0.152f), suspLength = 0.03f, allowsSteering = true });
            d.wheels.Add(new WheelSpec { name = "wheel_rl", localPos = new Vector3(-0.083f, -0.015f, -0.152f), suspLength = 0.03f, powered = true });
            d.wheels.Add(new WheelSpec { name = "wheel_rr", localPos = new Vector3(0.083f, -0.015f, -0.152f), suspLength = 0.03f, powered = true });

            d.sensors.Add(new SensorSpec
            {
                name = "cam_front", kind = SensorType.Camera,
                localPos = new Vector3(0f, 0.09f, 0.05f), aimEuler = new Vector3(8f, 0f, 0f),
                camWidth = 64, camHeight = 48, camFov = 62f, camRateHz = 10f,
            });
            d.sensors.Add(new SensorSpec
            {
                name = "tof_front", kind = SensorType.Tof,
                localPos = new Vector3(0f, 0.03f, 0.21f), aimEuler = Vector3.zero,
                range = 4f, coneRays = 3, coneAngle = 6f,
            });
            d.sensors.Add(new SensorSpec
            {
                name = "tof_left", kind = SensorType.Tof,
                localPos = new Vector3(-0.06f, 0.03f, 0.19f), aimEuler = new Vector3(0f, -32f, 0f),
                range = 4f,
            });
            d.sensors.Add(new SensorSpec
            {
                name = "tof_right", kind = SensorType.Tof,
                localPos = new Vector3(0.06f, 0.03f, 0.19f), aimEuler = new Vector3(0f, 32f, 0f),
                range = 4f,
            });
            for (int i = 0; i < 4; i++)
            {
                string[] tag = { "fl", "fr", "rl", "rr" };
                d.sensors.Add(new SensorSpec { name = "enc_" + tag[i], kind = SensorType.Encoder, wheelIndex = i });
            }
            d.batteries.Add(new BatterySpec());   // 2S pack in the centre tray
            // Two rubber-duck antennas on the rear deck (the F1TENTH/JetRacer look).
            d.antennas.Add(new AntennaSpec { name = "ant_l", localPos = new Vector3(-0.05f, 0.09f, -0.15f), yawDeg = -12f, tiltDeg = 16f, mirrorGroup = 1 });
            d.antennas.Add(new AntennaSpec { name = "ant_r", localPos = new Vector3(0.05f, 0.09f, -0.15f), yawDeg = 12f, tiltDeg = 16f, mirrorGroup = 1 });
            return d;
        }

        public VehicleDesign Clone()
        {
            // JSON round-trip = deep copy (handles the nested list/objects).
            return JsonUtility.FromJson<VehicleDesign>(JsonUtility.ToJson(this));
        }
    }
}
