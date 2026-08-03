using System.Collections.Generic;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>Preset body silhouettes selectable in the garage (append-only —
    /// the enum value is persisted in design JSON). Each carries its own drag
    /// coefficient in <see cref="AeroDynamics.BodyCd"/>.</summary>
    public enum BodyShape
    {
        Box, Wedge, Buggy, Shell, LowRacer, Coupe, Baja, Patrol,
        Rattle, Redline, Highwing, Autopia,
        /// <summary>The 1:1 Volkswagen Tiguan — a physics reference vehicle, not
        /// game content. Debug-only: GarageUI hides it from the shape picker and
        /// it is reachable solely through DebugVehicles.</summary>
        Tiguan,
    }

    /// <summary>
    /// Per-player arcade driving assists, each 0 (off — the pure physics model)
    /// to 1 (full help). Set from the player's Options and applied host-side to
    /// whichever machine simulates that player's car. Forced off in Autonomous
    /// mode — C firmware always faces the raw physics.
    /// </summary>
    [System.Serializable]
    public struct AssistSettings
    {
        [Range(0f, 1f)] public float steer;      // speed-sensitive limit + countersteer
        [Range(0f, 1f)] public float stability;  // ESC-style yaw damping toward intent
        [Range(0f, 1f)] public float traction;   // torque cut on drive-wheel slip
        [Range(0f, 1f)] public float abs;        // brake release on lockup
        [Range(0f, 1f)] public float launch;     // standing-start wheelspin governor
    }

    /// <summary>
    /// Car built on Unity WheelColliders from an arbitrary set of placeable
    /// <see cref="CarWheelConfig"/> wheels (any count, positioned + yaw-oriented
    /// anywhere). Each wheel can steer (optionally reversed) with its own max
    /// angle; drive torque comes from per-wheel MotorParts (voltage-driven DC
    /// motors) bound by the SensorRig. Self-builds its body/wheels/visuals in
    /// Awake.
    ///
    /// Actuator convention: [motor.ActuatorIndex]=volts, [6]=steer [-1,1],
    /// [7]=brake [0,1]. Implements IControlledVehicle so a C controller drives it
    /// in Autonomous mode exactly like any other vehicle.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CarVehicle : MonoBehaviour, IControlledVehicle, ITunable
    {
        // ---- 1/10 RC scale (F1TENTH-class) ----
        // All defaults model a ~40 cm, ~1.8 kg RC/autonomous car. Constants move
        // as an ensemble: corner sprung mass ≈ 0.4 kg → spring 300 N/m sags
        // ~13 mm of the 30 mm travel (targetPosition 0.5), damper 15 ≈ 0.68·crit.
        [Header("Body (m)")]
        public BodyShape bodyShape = BodyShape.Box;

        /// <summary>
        /// The <see cref="BodyCatalog"/> key this car wears, or empty to be read
        /// off <see cref="bodyShape"/>. Set by <c>VehicleFactory</c> from the
        /// design; left empty by every car built in code (the debug spawner, the
        /// physics scenes, the flight scenes), which is why
        /// <see cref="BodyCatalog.Resolve"/> takes the pair and not the key.
        /// </summary>
        public string bodyKey = "";

        /// <summary>
        /// Everything this car's shell is: mesh, token table, render scale,
        /// paintability, drag. Read through <see cref="BodyCatalog.Resolve"/>
        /// on every access, never cached — the garage edits <c>bodyShape</c> on
        /// a live car and rebuilds, so a cache would be one rebuild stale.
        /// </summary>
        public BodyDef Body => BodyCatalog.Resolve(bodyKey, bodyShape);

        public Vector3 bodySize = new Vector3(0.20f, 0.10f, 0.42f);
        public Color bodyColor = new Color(0.20f, 0.55f, 0.95f);
        public float bodyMass = 1.6f;

        [Header("Wheels")]
        // Set by the VehicleFactory before activation. When null/empty a default
        // 4-wheel layout is built from the legacy geometry below.
        public CarWheelConfig[] wheels;
        public float wheelBase = 0.30f;      // fallback layout only
        public float trackWidth = 0.166f;    // fallback layout only
        public float wheelRadius = 0.033f;   // fallback layout only (66 mm tire)
        public float wheelYOffset = -0.045f; // fallback layout only

        [Header("Drivetrain")]
        // Drive torque comes from per-wheel MotorPart DC motors (voltage-driven).
        // Steering is a slew-limited servo; each wheel opts in with its own angle.
        public float steerRateDegPerSec = 480f;   // RC servo ≈ 60°/0.1 s (no-load)
        // Servo stall torque (N·m); 0 = ideal servo (legacy pure slew). With a
        // stall figure the available slew derates along the linear torque-speed
        // line as cornering load (tyre Fy × trail) rises — steering authority
        // collapses exactly when the tyres are working hardest, like the real
        // part. Savox SC-1251MG @6 V: 0.883 N·m stall, 667 °/s no-load.
        public float servoStallNm = 0f;
        private const float SteerTrailM = 0.004f;     // pneumatic + mechanical trail
        private const float SteerFrictionNm = 0.02f;  // linkage/kingpin friction
        // Ackermann steering: 0 = parallel (legacy), 100 = true Ackermann (inner
        // wheel steers sharper about a shared turn centre on the rear-axle line).
        public float ackermannPct = 0f;
        public float maxBrakeTorque = 0.8f;       // lock threshold ≈ 0.23 N·m
        public float handbrakeTorque = 1.2f;
        /// <summary>Distribute foot-brake torque with instantaneous wheel load
        /// (EBD / proportioning valve). False = the fixed per-wheel
        /// <c>brakeScale</c> bias, which is what every design had before this.</summary>
        public bool brakeProportioning = false;
        // Speed (m/s) at which per-tile rolling resistance reaches full strength.
        // Below this it ramps to 0 so it opposes motion instead of parking the car.
        // A numerical-regularization constant, not scale physics: it is an
        // absolute speed and means the same thing for a 2.5 kg car and a
        // 1500 kg one. Same reasoning covers TyreModel.VLow.
        private const float RollResistRampSpeed = 0.3f;
        // What "at rest" means, everywhere the question is asked (m/s). One
        // number, in metres per second, so both halves of the sticky-hold gate
        // — body speed and wheel RIM speed — agree at every scale. (The old
        // wheel gate was 0.5 rad/s, which is 0.018 m/s of rim speed on an RC
        // wheel but 0.17 m/s on a full-scale one: a Tiguan wheel could be
        // turning 8× the body-rest speed and still be called stationary.)
        private const float RestSpeed = 0.02f;

        [Header("Aero / stability")]
        // Aerodynamics: quadratic body drag (per-shape Cd × frontal area) plus
        // per-part wing/splitter forces applied at each part's position. aeroMult
        // scales all DOWNFORCE terms (a tuning aid; drag stays physical).
        public float aeroMult = 1f;
        public AeroConfig[] aeroParts;         // set by the VehicleFactory
        public float antiRoll = 8f;            // ≈ one axle weight at full Δtravel

        // ---- scale-dependent constants, authored by the design --------------
        // Every default here IS the literal it replaced, and VehicleFactory
        // copies the design's own defaults over them, so the ten shipped designs
        // reach exactly the same numbers by exactly the same code path. See
        // VehicleDesign for what each one is and why a formula could not serve.
        public float linearDampingOverride = 0.02f;
        public float angularDampingOverride = 0.5f;
        public float maxDepenetrationVelOverride = 2f;
        public float stickyPhantomNm = 0.05f;
        public float contactOffsetOverride = 0f;   // 0 = Physics.defaultContactOffset
        public float dragCdOverride = 0f;          // 0 = AeroDynamics.BodyCd(shape)
        public float frontalAreaOverride = 0f;     // 0 = the bodySize estimate
        public Vector3 centerOfMass = new Vector3(0f, -0.03f, 0f);

        [Header("Assists")]
        public AssistSettings assists;         // per-player prefs (rig build)
        public bool assistsActive = true;      // false in Autonomous mode

        // ---- Arcade layer (AIHWSim.Arcade owns these; neutral = no effect) ----
        // Written only by ArcadeDirector, which is constructed only in arcade
        // sessions. At the values below every expression that reads them reduces
        // to its pre-arcade form, so a normal race, a firmware run and the Opus
        // mission are bit-for-bit unaffected.

        /// <summary>Extra forward acceleration (m/s²) from a boost item. Maxed
        /// against the boost-pad surface value, so items and pads don't stack.</summary>
        public float arcadeBoostAccel;

        /// <summary>Tyre grip scale from an arcade effect (spin-out, oil). MUST
        /// default to 1 — a car built outside arcade would otherwise have no grip.</summary>
        public float arcadeGripMult = 1f;

        /// <summary>Yaw torque (N·m) applied while spun out by a hit.</summary>
        public float arcadeYawTorque;

        /// <summary>
        /// Master switch for the aerial moves (jump, double jump, flip, air
        /// roll) the soccer mode needs. OFF everywhere else, and the methods
        /// below refuse to do anything while it is off — so a race car, a LAN
        /// peer and the headless Opus rig behave exactly as they did before
        /// aerials existed. That claim is what the Opus regression checks.
        /// </summary>
        public bool arcadeAerial;

        /// <summary>Any wheel touching the ground, from last step's contact
        /// state. The aerial moves are gated on the opposite of this and
        /// nothing else reads it, so it costs a loop over four bools.</summary>
        public bool Grounded
        {
            get
            {
                foreach (var w in _wheels) if (w.grounded) return true;
                return false;
            }
        }

        /// <summary>
        /// Straight up out of the chassis. Through the centre of mass on
        /// purpose: a vertical impulse on a vertical lever arm has a zero cross
        /// product, which is the one case where the positioned overload and this
        /// one agree — and the one the drift hop already relies on.
        /// </summary>
        public void AerialJump(float impulse)
        {
            if (!arcadeAerial || _body == null) return;
            // Kill any residual downward velocity first, so a jump off a dip
            // gives the same height as a jump off the flat.
            var v = _body.linearVelocity;
            if (v.y < 0f) { v.y = 0f; _body.linearVelocity = v; }
            _body.AddForce(Vector3.up * impulse, ForceMode.Impulse);
        }

        /// <summary>
        /// The dodge: a flat impulse along <paramref name="worldDir"/> plus the
        /// roll that makes it read as a flip rather than a shove. The linear
        /// half MUST go through the CoM (see <see cref="ArcadeImpulse(Vector3)"/>
        /// for why); the rotation is asked for explicitly instead.
        /// </summary>
        public void AerialFlip(Vector3 worldDir, float impulse, float torque)
        {
            if (!arcadeAerial || _body == null) return;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-4f) return;
            worldDir.Normalize();
            _body.AddForce(worldDir * impulse, ForceMode.Impulse);
            // Roll about the axis perpendicular to the travel, so a side dodge
            // barrel-rolls and a forward dodge front-flips.
            _body.AddTorque(Vector3.Cross(Vector3.up, worldDir) * torque, ForceMode.Impulse);
        }

        /// <summary>Sustained air-control torque while airborne. Refused on the
        /// ground, where the tyres own the car's attitude.</summary>
        public void AerialTorque(Vector3 torque)
        {
            if (!arcadeAerial || _body == null || Grounded) return;
            _body.AddTorque(torque, ForceMode.Force);
        }

        /// <summary>Drive-torque scale while an arcade effect suppresses the
        /// motors (spun out, wrecked). MUST default to 1. Deliberately separate
        /// from <see cref="Frozen"/>, which also slams the brakes on and is owned
        /// by the race countdown — a wrecked car should coast and tumble, not
        /// stop dead.</summary>
        public float arcadeDriveMult = 1f;

        /// <summary>
        /// Scales the two SLIP-KILLING assists (steer countersteer and the
        /// stability yaw damper) while an arcade effect wants the car to slide on
        /// purpose. MUST default to 1 — at 1 both multiplies are exact and every
        /// non-arcade session stays bit-identical.
        ///
        /// It exists because those two assists are, by construction, machines
        /// that remove body slip angle, and a deliberate drift is the player
        /// asking for body slip angle. The stability assist is the worse of the
        /// pair: it damps yaw rate toward the bicycle-model intent, which is
        /// precisely the yaw the drift is generating. With assists now defaulting
        /// to Standard, leaving them on would quietly undo the slide and the
        /// mechanic would read as "the handbrake does nothing".
        ///
        /// Traction control and ABS are deliberately NOT scaled — they act on
        /// wheel slip ratio, not on body attitude, and a drift has no quarrel
        /// with either.
        /// </summary>
        public float arcadeAssistMult = 1f;

        /// <summary>
        /// Scales <see cref="handbrakeTorque"/> while an arcade drift is holding a
        /// slide. MUST default to 1 — at 1 the multiply is exact and every
        /// non-arcade session stays bit-identical.
        ///
        /// A real handbrake is a brake, and that is the right answer everywhere
        /// except inside a committed drift, where it is the reason the mechanic
        /// felt wrong: locking the rear axle for two seconds scrubs most of the
        /// speed out of the arc, so the slide dies, the exit is slow, and the
        /// reward never pays for the corner. An arcade drift button is not a
        /// brake — it is a request to slide — so during one this turns the lock
        /// down to a drag. Not to zero: a little rear brake is what keeps the
        /// back end willing rather than merely permitted.
        ///
        /// Only the torque is scaled, not the <c>_handbrake</c> flag itself, so
        /// ABS stays exempt from the deliberate lock exactly as before.
        /// </summary>
        public float arcadeHandbrakeMult = 1f;

        /// <summary>
        /// Scales the stability assist's gain AND torque clamp. MUST default to
        /// 1 — at 1 both multiplies are exact and every non-arcade session stays
        /// bit-identical.
        ///
        /// The ESC's ceiling (<see cref="AssistTuning.StabilityClamp"/>, 0.75 N·m
        /// at full stability) is sized for sim driving, where the tyres are the
        /// only thing that can rotate the car and ~2 N·m of their resisting
        /// moment is a fair fight. Arcade adds forces the tyres never see — a
        /// boost pad is 9 m/s² applied straight to the body — and against those
        /// the sim-sized nudge loses, which is why arcade cars spun out over
        /// pads and on full-throttle launches at ANY assist setting. This is the
        /// arcade layer's permission to give the same controller real authority.
        ///
        /// Stood down (set to 1) by the director during a drift, a spin-out and
        /// a wreck: the first is the player asking for yaw, the other two are
        /// the game inflicting it, and a damper strong enough to matter would
        /// erase all three.
        /// </summary>
        public float arcadeStabilityMult = 1f;

        /// <summary>
        /// Arcade "heaviness": extra downward force in newtons per (m/s)² of
        /// forward speed, applied at the centre of mass. MUST default to 0 — the
        /// application site is branch-skipped at 0, so every non-arcade session
        /// and the Opus mission stay bit-identical.
        ///
        /// Exists because honest RC-scale aero is negligible by design
        /// (AeroDynamics: ~3.5 % of weight at 10 m/s) and the arcade car reads
        /// as floaty at speed. Speed-squared load is the physically honest way
        /// to plant it without touching low-speed µ. Owned by HandlingFloor —
        /// NOT by ArcadeDirector's ApplyEffects, which re-asserts every channel
        /// it owns each frame; this one is deliberately outside that set, and
        /// its doc is the enumeration ApplyEffects' comment defers to.
        /// </summary>
        public float arcadeDownforce;

        [Header("Composite mass (factory-set)")]
        public bool useCompositeMass = false;
        public float compositeMass = 1.6f;
        public Vector3 compositeCoM = new Vector3(0f, -0.03f, 0f);
        public Vector3 compositeInertia = new Vector3(0.01f, 0.01f, 0.01f);

        [Header("Battery")]
        // Set by the VehicleFactory from the design's first battery part.
        // nominalV = 0 (no battery part / old designs) = stiff infinite rail.
        public float batteryNominalV = 0f;
        public float batteryInternalR = 0f;
        // Capacity (mAh); 0 = infinite = the legacy pinned rail (SoC stays 1).
        public float batteryCapacitymAh = 0f;

        /// <summary>Pack terminal voltage this step (0 when no battery part).</summary>
        public float BatteryTerminalV { get; private set; }
        /// <summary>Total |motor current| drawn last step (A).</summary>
        public float BatteryCurrent { get; private set; }
        /// <summary>State of charge 0..1 (pinned at 1 when capacity = 0).</summary>
        public float BatterySoc { get; private set; } = 1f;

        private float _battAhUsed;   // coulomb counter (A·h)

        /// <summary>
        /// LiPo per-cell open-circuit voltage vs state of charge — the standard
        /// discharge shape: a quick drop off the 4.20 V full charge, a long flat
        /// plateau through the middle, and a knee collapsing toward 3.0 V empty.
        /// </summary>
        private static float CellOcv(float soc)
        {
            if (soc > 0.90f) return Mathf.Lerp(4.05f, 4.20f, (soc - 0.90f) / 0.10f);
            if (soc > 0.20f) return Mathf.Lerp(3.65f, 4.05f, (soc - 0.20f) / 0.70f);
            if (soc > 0.05f) return Mathf.Lerp(3.40f, 3.65f, (soc - 0.05f) / 0.15f);
            return Mathf.Lerp(3.00f, 3.40f, soc / 0.05f);
        }

        [Header("Suspension")]
        public float suspensionDistance = 0.03f;
        public float suspensionSpring = 300f;
        public float suspensionDamper = 15f;

        public ImuSensor imu = new ImuSensor();

        [Header("Sensor realism (factory-set; 0 = clean/legacy)")]
        // Motor-vibration coupling into the IMU (unbalanced-rotor shake whose
        // frequency tracks motor speed). 0 = clean legacy IMU.
        public float imuVibration = 0f;
        // ABI wheel_vel[] corruption: Gaussian σ (rad/s) and CPR-consistent
        // velocity quantization. Both 0 = the historical perfectly-clean channel.
        public float wheelVelNoiseStd = 0f;
        public int wheelVelQuantCpr = 0;

        private readonly float[] _vibPhase = new float[8];
        private NoiseModel[] _wheelVelNoise;

        /// <summary>Runtime state for one built wheel.</summary>
        private sealed class Wheel
        {
            public WheelCollider col;
            public Transform viz;
            public CarWheelConfig cfg;
            public float servoSteer;     // steering servo's own angle (deg)
            public float currentSteer;   // total wheel angle = servo + roll steer (deg)
            public float lastMult = 1f;  // last applied combined friction multiplier (legacy path)
            public float omegaLp;        // low-passed |wheel ω| (tire ballooning)
            public float baseRadius;     // built radius r0 (ballooning reference)
            public Transform strut;      // visible coil-over (null when suspLength 0)
            public Vector3 mountLocal;   // body-side strut mount (chassis-local)

            // ---- Brush tyre model state (TyreModel.Enabled path) ----
            public float omega;          // integrated wheel spin (rad/s, forward +)
            public float spinAngle;      // accumulated roll for the visual (rad)
            public float driveTorque;    // this step's motor torque (N·m, set by MotorPart)
            public float spinInertia;    // J: bare wheel + reflected drivetrain (kg·m²)
            public float slipRatio;      // last computed κ (TC/ABS + telemetry)
            public float slipNorm;       // combined slip / peak slip; >1 = sliding (readout only)
            public float patchSpeed;     // contact-patch ground speed (m/s, readout only)
            public bool grounded;        // last step's contact state (readout only)
            public float lastFy;         // last lateral tyre force (N) — servo load

            // ---- derived linkage kinematics (SuspensionLinkage hardpoints) ----
            // Both are 0 when no linkage is authored, and both are branch-guarded
            // at their use sites so an unauthored corner takes the identical code
            // path it did before these existed.
            public float brakeShare;     // EBD load share (× the commanded torque)
            public float rollCentreH;    // roll centre height above ground (m)
            public float toeSteerPerM;   // roll steer: deg of toe per metre of bump
            public float restCompression; // suspension compression at static rest
        }

        /// <summary>Baseline forward friction stiffness (scaled by tile surfaces).</summary>
        private const float FwdStiffness = 1.6f;

        private Rigidbody _body;
        private readonly List<Wheel> _wheels = new List<Wheel>();
        private readonly List<(Wheel a, Wheel b)> _antiRollPairs = new List<(Wheel, Wheel)>();
        private readonly List<MeshRenderer> _bodyRenderers = new List<MeshRenderer>();
        private Material _bodyMat;

        /// <summary>The body shell's manifest table when it has one, else null.
        /// Held for one reason: it is the only thing that knows which SLOT of a
        /// multi-material object is the paint channel.</summary>
        private PartManifestBinding _bodyBinding;

        /// <summary>The "BodyMesh" holder, and the authored shell instantiated
        /// inside it — null on the primitive path, which is the difference
        /// between the two. Held so the appearance dump can MEASURE what the
        /// body resolved to instead of re-running <see cref="BodyMeshKey"/> and
        /// proving only that the dumper agrees with itself.</summary>
        private Transform _bodyVisual, _bodyMeshInstance;
        // Tune "Grip (side)" knob. Legacy path: sideways friction stiffness.
        // Brush path: lateral grip scale (× 0.5, so 2.0 = neutral).
        private float _gripStiffness = 2.0f;

        // Latched actuator vector (ZOH). [motor.ActuatorIndex]=volts, [6]=steer, [7]=brake.
        private readonly float[] _cmd = new float[8];
        private bool _handbrake;
        private readonly List<MotorPart> _motors = new List<MotorPart>();

        private Vector3 _spawnPos;
        private Quaternion _spawnRot;
        private bool _spawnCaptured;

        public const int SteerActuator = 6;
        public const int BrakeActuator = 7;

        public int SetpointCount => 2;
        public float ForwardSpeed => _body != null ? Vector3.Dot(_body.linearVelocity, transform.forward) : 0f;

        /// <summary>Lateral velocity in the body frame — positive to the right.
        /// Paired with <see cref="ForwardSpeed"/> it gives the body slip angle,
        /// which is how the arcade layer decides a slide is a drift and not just
        /// a fast corner.</summary>
        public float LateralSpeed => _body != null ? Vector3.Dot(_body.linearVelocity, transform.right) : 0f;

        /// <summary>Is the handbrake currently pulled? Read-only, for the arcade
        /// drift-boost charge; the physics still reads the private field.</summary>
        public bool Handbraking => _handbrake;

        /// <summary>The latched steering COMMAND [-1, 1], not the servo angle.
        /// The arcade drift reads intent — "which way did you ask to go when you
        /// pulled the handbrake" — and <see cref="CurrentSteerAngle"/> is the
        /// servo's slewed, load-limited answer to that question, which lags by up
        /// to a couple of hundred milliseconds under cornering load.</summary>
        public float SteerCommand => Mathf.Clamp(_cmd[SteerActuator], -1f, 1f);

        public int WheelCount => _wheels.Count;

        public float CurrentSteerAngle
        {
            get
            {
                foreach (var w in _wheels) if (w.cfg.allowsSteering) return w.currentSteer;
                return 0f;
            }
        }

        /// <summary>The motors driving this vehicle (assigned by the SensorRig).</summary>
        public IReadOnlyList<MotorPart> Motors => _motors;

        /// <summary>The WheelCollider at wheel-list index i, or null.</summary>
        public WheelCollider GetWheel(int i) =>
            (i >= 0 && i < _wheels.Count) ? _wheels[i].col : null;

        /// <summary>The transform of wheel i (for garage markers), or null.</summary>
        public Transform GetWheelTransform(int i) =>
            (i >= 0 && i < _wheels.Count) ? _wheels[i].col.transform : null;

        /// <summary>The wheel i visual holder (for hiding it during a drag), or null.</summary>
        public Transform GetWheelVisual(int i) =>
            (i >= 0 && i < _wheels.Count) ? _wheels[i].viz : null;

        /// <summary>The authored wheel-style index at wheel i, read back off the
        /// built car. Design data, but asked of the CAR: what a design says and
        /// what the wheel was built with are the same number only as long as
        /// nothing in between drops it.</summary>
        public int WheelStyle(int i) =>
            (i >= 0 && i < _wheels.Count) ? _wheels[i].cfg.Wheel.legacy : 0;

        /// <summary>The "BodyMesh" holder — the primitive builders' parent, and
        /// the frame the shell is measured in.</summary>
        public Transform BodyVisual => _bodyVisual;

        /// <summary>The instantiated authored shell, or null when this body
        /// fell through to the primitive builders. The distinction is not
        /// derivable from <see cref="bodyShape"/>: a shape WITH a mesh key
        /// still lands here as null on a machine where the FBX did not ship.</summary>
        public Transform BodyMeshInstance => _bodyMeshInstance;

        /// <summary>Whether wheel i steers (network ghost wheel visuals).</summary>
        public bool WheelAllowsSteering(int i) =>
            i >= 0 && i < _wheels.Count && _wheels[i].cfg.allowsSteering;

        /// <summary>Normalized suspension compression at wheel i (0 = full droop /
        /// airborne, 1 = fully bottomed). Same measure the anti-roll bar reads.</summary>
        public float GetSuspensionCompression(int i)
        {
            if (i < 0 || i >= _wheels.Count) return 0f;
            var col = _wheels[i].col;
            if (col == null || !col.GetGroundHit(out WheelHit hit)) return 0f;
            float travel = (-col.transform.InverseTransformPoint(hit.point).y - col.radius) / col.suspensionDistance;
            return Mathf.Clamp01(travel);
        }

        /// <summary>Total suspension force (N) currently reacted at wheel i's contact
        /// (0 when airborne) — what a strut load cell would read.</summary>
        public float GetSuspensionForce(int i)
        {
            if (i < 0 || i >= _wheels.Count) return 0f;
            var col = _wheels[i].col;
            if (col == null || !col.GetGroundHit(out WheelHit hit)) return 0f;
            return hit.force;
        }

        /// <summary>Configured strut tilt (deg) at wheel i, side-relative sign applied.</summary>
        public float GetSuspensionAngle(int i) =>
            (i >= 0 && i < _wheels.Count) ? StrutTiltZ(_wheels[i].cfg) : 0f;

        /// <summary>
        /// Wheel spin at wheel i (rad/s, forward-positive) — THE speed source for
        /// every consumer (encoders, motor back-EMF, IMU vibration, the ABI
        /// wheel_vel channel). Under the brush tyre model this is the vehicle's
        /// own integrated ω; under the legacy PhysX-friction path it is wc.rpm.
        /// </summary>
        public float WheelOmega(int i)
        {
            if (i < 0 || i >= _wheels.Count) return 0f;
            var w = _wheels[i];
            return TyreModel.Enabled
                ? w.omega
                : (w.col != null ? w.col.rpm * (Mathf.PI * 2f / 60f) : 0f);
        }

        // ---- read-only introspection ----
        // These exist so a validator or a telemetry channel can SEE the physics
        // without reaching into it. None of them is read by any physics
        // expression, so none can move a result: that is the point, and it is
        // why they were safe to add alongside a bit-identity requirement.

        /// <summary>Wheel i's spin inertia J (kg·m²) — the bare wheel plus any
        /// reflected drivetrain. Integrated by this class on the brush path, so
        /// it lives in no engine field and would otherwise be invisible to a
        /// dump that reads WheelColliders.</summary>
        public float WheelSpinInertia(int i) =>
            i >= 0 && i < _wheels.Count ? _wheels[i].spinInertia : 0f;

        /// <summary>Wheel i's longitudinal slip ratio κ.</summary>
        public float WheelSlipRatio(int i) =>
            i >= 0 && i < _wheels.Count ? _wheels[i].slipRatio : 0f;

        /// <summary>Whether wheel i is touching the ground this step.</summary>
        public bool WheelGrounded(int i) =>
            i >= 0 && i < _wheels.Count && _wheels[i].grounded;

        /// <summary>Wheel i's chassis-local mount position. Lets a measurement
        /// tell front from rear by GEOMETRY rather than by index — nothing
        /// guarantees a wheel order, and a weight split read off the wrong two
        /// corners is a plausible wrong answer rather than an obvious one.</summary>
        public Vector3 GetWheelLocalPos(int i) =>
            i >= 0 && i < _wheels.Count ? _wheels[i].cfg.localPos : Vector3.zero;

        /// <summary>Wheel i's hub height above the world origin — the wheel
        /// CENTRE, not the collider origin. The collider sits at the top of
        /// travel and the hub hangs compression*suspensionDistance below it, so
        /// reading the transform directly overstates ride height by most of a
        /// travel.</summary>
        public float GetWheelHubWorldY(int i)
        {
            if (i < 0 || i >= _wheels.Count) return 0f;
            var col = _wheels[i].col;
            if (col == null) return 0f;
            return col.transform.position.y
                   - GetSuspensionCompression(i) * col.suspensionDistance;
        }

        /// <summary>
        /// Deposit this step's motor torque on wheel i (called by MotorPart from
        /// StepDrive). Brush path: feeds the wheel-spin integrator. Legacy path:
        /// writes WheelCollider.motorTorque exactly as before.
        /// </summary>
        public void ApplyDriveTorque(int i, float torqueNm)
        {
            if (i < 0 || i >= _wheels.Count) return;
            var w = _wheels[i];
            if (TyreModel.Enabled) w.driveTorque = torqueNm;
            else if (w.col != null) w.col.motorTorque = torqueNm;
        }

        /// <summary>Bind the driven-wheel motors (called once by the SensorRig).</summary>
        public void BindMotors(IEnumerable<MotorPart> motors)
        {
            _motors.Clear();
            if (motors != null) _motors.AddRange(motors);
        }

        /// <summary>
        /// Apply an external impulse (missile punt, off-track drag). Exists only
        /// because the Rigidbody is private; nothing else about the body is
        /// exposed. Arcade-owned — no effect unless something calls it.
        /// </summary>
        public void ArcadeImpulse(Vector3 impulse, Vector3 worldPoint)
        {
            if (_body != null) _body.AddForceAtPosition(impulse, worldPoint, ForceMode.Impulse);
        }

        /// <summary>
        /// Impulse straight through the centre of mass, so it adds speed and no
        /// rotation whatsoever.
        ///
        /// The positioned overload above is not interchangeable with this one for
        /// a HORIZONTAL push: the composite CoM sits a few centimetres off the
        /// transform origin, and against a pitch inertia of order 0.01 kg·m² that
        /// lever arm turns a 2 N·s shove into hundreds of degrees per second of
        /// somersault. (The drift hop gets away with it only because a vertical
        /// impulse on a vertical lever arm has a zero cross product.)
        /// </summary>
        public void ArcadeImpulse(Vector3 impulse)
        {
            if (_body != null) _body.AddForce(impulse, ForceMode.Impulse);
        }

        /// <summary>
        /// Apply an angular impulse (the missile-hit tumble). Separate from the
        /// sustained <see cref="arcadeYawTorque"/>: that one is a torque held for
        /// the length of a spin-out, this is a single kick that lets the car
        /// tumble ballistically and land wherever it lands.
        /// </summary>
        public void ArcadeTorqueImpulse(Vector3 torqueImpulse)
        {
            if (_body != null) _body.AddTorque(torqueImpulse, ForceMode.Impulse);
        }

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _body.mass = bodyMass;
            _body.linearDamping = linearDampingOverride;   // real drag is modeled explicitly
            _body.angularDamping = angularDampingOverride;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            // ContinuousDynamic is invalid on a kinematic body — Unity warns and
            // falls back. Ghosts and host-side followers are kinematic and still
            // need continuous detection, because they cross the 0.25 m lap and
            // checkpoint gates in one step at racing speed.
            _body.collisionDetectionMode = _body.isKinematic
                ? CollisionDetectionMode.ContinuousSpeculative
                : CollisionDetectionMode.ContinuousDynamic;
            _body.centerOfMass = centerOfMass;
            // Composite mass model (set by the factory from MassProperties): total
            // mass, CoM, and a diagonal inertia tensor derived from the chassis box
            // + every part as a point mass. Legacy path (flag off) leaves Unity's
            // collider-derived inertia untouched — bit-compatible with old designs.
            if (useCompositeMass)
            {
                _body.mass = compositeMass;
                _body.centerOfMass = compositeCoM;
                _body.inertiaTensor = compositeInertia;
                _body.inertiaTensorRotation = Quaternion.identity;
            }
            // Small cars yaw far faster than the 7 rad/s Unity default (wheel spin
            // is internal to WheelCollider and unaffected by this body cap).
            // Left as a constant deliberately: it is a CAP, and 40 rad/s is far
            // above anything a 1500 kg car reaches (<2), so it never binds at
            // either scale and authoring it would be a knob with no effect.
            _body.maxAngularVelocity = 40f;
            _body.maxDepenetrationVelocity = maxDepenetrationVelOverride;

            // The root GameObject stays at scale (1,1,1). Body size comes from a
            // BoxCollider + a separately-scaled visual child, NOT from scaling the
            // root — otherwise the non-uniform body scale would distort the wheels
            // (and WheelColliders) into ellipsoids.
            var box = GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            box.size = bodySize;
            box.center = Vector3.zero;
            // PhysicsTuning sets a 2 mm global contact offset, which is right for
            // a 33 mm wheel and very tight on a 349 mm one — but it runs
            // BeforeSceneLoad, where no vehicle exists to ask. Overriding this
            // collider is how a full-scale car gets a sane offset without moving
            // a global that every RC car in the project reads. Never runs for
            // them: the design default is 0.
            if (contactOffsetOverride > 0f) box.contactOffset = contactOffsetOverride;

            BuildBodyVisual();
            BuildWheels();
            BuildAntiRollPairs();
            PoseWheelVisualsFromConfig();
        }

        /// <summary>
        /// Place each wheel visual at its configured pose. On the track the first
        /// <see cref="StepPhysics"/> immediately overrides this with the simulated
        /// WheelCollider pose; in the kinematic garage preview (where StepPhysics
        /// never runs) this is what makes the wheels render at the right spot.
        /// </summary>
        public void PoseWheelVisualsFromConfig()
        {
            foreach (var w in _wheels)
            {
                if (w.viz == null) continue;
                w.viz.SetPositionAndRotation(
                    transform.TransformPoint(HubLocal(w.cfg)),
                    transform.rotation * Quaternion.Euler(0f, w.cfg.yaw, StrutTiltZ(w.cfg)));
            }
            UpdateStruts();
        }

        /// <summary>
        /// Orient each visible strut to span its body mount → the wheel's current
        /// hub (the wheel viz position, already posed by StepPhysics /
        /// PoseWheelVisualsFromConfig / the network ghost). Runs in LateUpdate so it
        /// works uniformly for driven cars, the kinematic garage preview, and LAN
        /// ghosts. On track the hub moves with compression, so the strut visibly
        /// stretches and tilts — the spring working.
        /// </summary>
        private void UpdateStruts()
        {
            foreach (var w in _wheels)
            {
                if (w.strut == null || w.viz == null) continue;
                Vector3 mountWorld = transform.TransformPoint(w.mountLocal);
                Vector3 hubWorld = w.viz.position;
                Vector3 span = hubWorld - mountWorld;
                float len = span.magnitude;
                if (len < 1e-5f) { w.strut.localScale = new Vector3(1f, 1f, 1e-5f); continue; }
                w.strut.SetPositionAndRotation(mountWorld, Quaternion.LookRotation(span, transform.up));
                w.strut.localScale = new Vector3(1f, 1f, len);
            }
        }

        private void LateUpdate() => UpdateStruts();

        /// <summary>Strut tilt (deg about wheel-local Z) for a wheel config. Sign is
        /// side-relative — positive <c>suspAngleDeg</c> leans both strut tops inboard —
        /// and the magnitude is clamped so the WheelCollider's raycast stays sane.</summary>
        private static float StrutTiltZ(CarWheelConfig cfg) =>
            SuspensionGeometry.TiltZ(cfg.localPos.x, cfg.suspAngleDeg);

        /// <summary>Chassis-local wheel hub position. <c>localPos</c> is the body-side
        /// strut mount (the placement/pairing anchor); the hub hangs below/outboard of
        /// it by the strut length + tilt. With <c>suspLength == 0</c> the hub is the
        /// mount (today's build). Only x/y differ from the mount — z is invariant, so
        /// Ackermann / wheelbase / anti-roll pairing (which key off z and x-sign) stay
        /// valid against the mount.</summary>
        private static Vector3 HubLocal(CarWheelConfig cfg) =>
            cfg.localPos + SuspensionGeometry.HubOffsetLocal(cfg.localPos.x, cfg.suspAngleDeg, cfg.suspLength);

        /// <summary>Sanitized friction scalar (hand-edited JSON may leave it 0).</summary>
        private static float GripMult(CarWheelConfig cfg) => cfg.gripMult > 0f ? cfg.gripMult : 1f;

        /// <summary>
        /// Deterministic 2-D value noise in [-1,1]: integer-hash lattice values,
        /// smoothstep-bilinear blended. Purely positional — the same world point
        /// always produces the same value, so roughness forces are repeatable.
        /// </summary>
        private static float ValueNoise2(float x, float y)
        {
            static float Hash(int ix, int iy)
            {
                unchecked
                {
                    uint h = (uint)(ix * 374761393 + iy * 668265263);
                    h = (h ^ (h >> 13)) * 1274126177u;
                    h ^= h >> 16;
                    return h / 4294967295f * 2f - 1f;
                }
            }
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Mathf.Lerp(Hash(x0, y0), Hash(x0 + 1, y0), fx);
            float b = Mathf.Lerp(Hash(x0, y0 + 1), Hash(x0 + 1, y0 + 1), fx);
            return Mathf.Lerp(a, b, fy);
        }

        private float _wheelbaseEst;   // z-extent of the wheel set (stability assist)
        private float _ackZRef;        // rear-axle reference line for Ackermann geometry
        private float _cornerWeight;   // static per-wheel load Fz0 (N)

        private void BuildWheels()
        {
            _wheels.Clear();
            // null = never configured (bare component) → default 4-wheel layout.
            // An explicit (possibly empty) array from the factory is used as-is.
            CarWheelConfig[] configs = wheels ?? DefaultWheels();
            // Corner mass (sprung mass per wheel) turns a per-wheel damping ratio ζ
            // into a JointSpring damper: damper = ζ·2·√(k·m_corner).
            float cornerMass = _body.mass / Mathf.Max(1, configs.Length);
            _cornerWeight = cornerMass * 9.81f;   // static Fz0 for load-sensitive grip
            for (int i = 0; i < configs.Length; i++)
                _wheels.Add(MakeWheel($"Wheel_{i}", configs[i], cornerMass));

            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var c in configs)
            {
                if (c.localPos.z < zMin) zMin = c.localPos.z;
                if (c.localPos.z > zMax) zMax = c.localPos.z;
            }
            _wheelbaseEst = configs.Length >= 2 ? Mathf.Max(0.01f, zMax - zMin) : 0f;

            // Ackermann reference: the turn centre sits on the line through the
            // non-steering wheels (≈ rear axle). All-steer designs use the centre.
            float zSum = 0f; int zN = 0;
            foreach (var c in configs)
                if (!c.allowsSteering) { zSum += c.localPos.z; zN++; }
            if (zN > 0) _ackZRef = zSum / zN;
            else
            {
                foreach (var c in configs) zSum += c.localPos.z;
                _ackZRef = configs.Length > 0 ? zSum / configs.Length : 0f;
            }
        }

        private CarWheelConfig[] DefaultWheels()
        {
            float hx = 0.5f * trackWidth, hz = 0.5f * wheelBase;
            var front = true; var rear = false;
            CarWheelConfig W(float x, float z, bool steer)
            {
                var c = CarWheelConfig.Default(new Vector3(x, wheelYOffset, z), steer);
                c.radius = wheelRadius;
                return c;
            }
            return new[]
            {
                W(-hx, +hz, front), W(+hx, +hz, front),
                W(-hx, -hz, rear),  W(+hx, -hz, rear),
            };
        }

        // Pair each left wheel with the nearest-in-z right wheel for anti-roll.
        private void BuildAntiRollPairs()
        {
            _antiRollPairs.Clear();
            var right = new List<Wheel>();
            var left = new List<Wheel>();
            foreach (var w in _wheels)
            {
                if (w.cfg.localPos.x < -0.01f) left.Add(w);
                else if (w.cfg.localPos.x > 0.01f) right.Add(w);
            }
            var used = new HashSet<Wheel>();
            foreach (var l in left)
            {
                Wheel best = null; float bestDz = float.MaxValue;
                foreach (var r in right)
                {
                    if (used.Contains(r)) continue;
                    float dz = Mathf.Abs(l.cfg.localPos.z - r.cfg.localPos.z);
                    if (dz < bestDz) { bestDz = dz; best = r; }
                }
                if (best != null) { used.Add(best); _antiRollPairs.Add((l, best)); }
            }
        }

        private void BuildBodyVisual()
        {
            var holder = new GameObject("BodyMesh").transform;
            holder.SetParent(transform, false);
            holder.localPosition = Vector3.zero;
            _bodyVisual = holder;

            _bodyMat = new Material(Shader.Find("Standard")) { color = bodyColor };

            // Authored-mesh path (Shell / LowRacer / Buggy have FBX shells): instantiate
            // the mesh, scale it to the design's bodySize, and drive its look with the
            // body material so bodyColor + SetBodyMaterial keep working. Falls through to
            // the primitive shape builders when the asset is absent (and always for
            // Box/Wedge). The body stays on the car's own layer (not the viz layer) so
            // the on-car camera sees it exactly as with the primitive body.
            BodyDef def = Body;
            string meshKey = def.meshKey;
            if (meshKey != null)
            {
                var inst = PartMeshLibrary.TryInstantiate(meshKey, holder, gameObject.layer);
                if (inst != null)
                {
                    _bodyMeshInstance = inst.transform;
                    inst.transform.localScale = BodyRenderScale(def, bodySize);
                    // Painted livery (decoded by VehicleFactory): the texture
                    // carries the colours, so the material tint goes white.
                    if (liveryTex != null)
                    {
                        _bodyMat.mainTexture = liveryTex;
                        _bodyMat.color = Color.white;
                    }
                    BindBodyMesh(inst, def, _bodyMat, _bodyRenderers);
                    // Kept so SetBodyMaterial can repaint the right SLOT. Null for
                    // every shipped shell — none of them ships a manifest — which
                    // is what leaves the legacy repaint path untouched.
                    _bodyBinding = inst.GetComponent<PartManifestBinding>();
                    // A livery on a body with no tintable channel went onto a
                    // material nothing reads. Said out loud here rather than
                    // discovered as "the police livery does not show up".
                    if (liveryTex != null) WarnIfNothingToPaint("A livery");
                    return;
                }
            }

            // The primitive builders are GEOMETRY, not data — five hand-built
            // compounds, not five rows — so this stays a switch. It reads the
            // resolved legacy value rather than the field so that a catalogue
            // body whose FBX did not ship falls back to the primitive its own
            // row nominates, not to whatever int happened to be beside the key.
            switch (def.legacy)
            {
                case BodyShape.Wedge:    BuildWedgeBody(holder); break;
                case BodyShape.Buggy:    BuildBuggyBody(holder); break;
                case BodyShape.Shell:    BuildShellBody(holder); break;
                case BodyShape.LowRacer: BuildLowRacerBody(holder); break;
                default:                 BuildBoxBody(holder); break;
            }
        }

        /// <summary>
        /// Nominal dimensions the body FBX shells are authored at (m).
        ///
        /// <b>A divisor, not a measurement.</b> No shipped shell is 0.20 x 0.10 x
        /// 0.42: the exporter scaled each car to length 0.420 with ONE uniform
        /// factor, so the real widths land between 0.17 and 0.20 and the heights
        /// wherever the body happens to be. That is why
        /// <c>PartModelValidator</c> pins those bodies' length and leaves their
        /// width free. Comparing an authored mesh to this constant axis by axis
        /// therefore reads as distortion where there is none — the per-axis divide
        /// below exists so a DESIGN can stretch a shell by changing bodySize, not to
        /// fit a mesh to a box.
        ///
        /// Public so Asset Studio can state the contract it checks an import
        /// against rather than keeping its own copy of 0.420.
        /// </summary>
        public static readonly Vector3 BodyMeshAuthorSize = new Vector3(0.20f, 0.10f, 0.42f);

        /// <summary>
        /// The local scale an authored shell is instantiated at.
        ///
        /// Every arcade shell is exported scaled to length 0.420, so one author
        /// size served them all and bodySize/authorSize is the ratio that renders
        /// it undistorted.
        ///
        /// The Tiguan takes no scale at all, and not merely because it is already
        /// 1:1. Its renderer bounds (2.099 x 1.472, carrying the door mirrors and
        /// the roof rails) are deliberately NOT its collision box (1.839 x 1.443,
        /// the published body) — neither mirrors nor rails are solid. Routing it
        /// through this divide would force those two to be the same number and
        /// squash the car to make them agree.
        ///
        /// It was a <c>== BodyShape.Tiguan</c> test until K3a; it now reads
        /// <see cref="BodyDef.unscaled"/>, which is what makes "authored 1:1" a
        /// property an asset can declare rather than a name compiled in here.
        /// </summary>
        public static Vector3 BodyRenderScale(BodyDef def, Vector3 bodySize) =>
            def != null && def.unscaled
                ? Vector3.one
                : new Vector3(bodySize.x / BodyMeshAuthorSize.x,
                              bodySize.y / BodyMeshAuthorSize.y,
                              bodySize.z / BodyMeshAuthorSize.z);

        /// <summary>
        /// The token→material table a body shell binds against, or <c>null</c>
        /// for the shapes that flatten every renderer onto the body material.
        ///
        /// Both questions in one answer, because they were always the same
        /// question: "does this shape have accents" is just "is there a table",
        /// and <see cref="PartVisualFactory.BindByToken"/> already reads a null
        /// table as flatten-everything — which is bit-for-bit the loop the three
        /// legacy shells used to have written out beside it.
        ///
        /// The Tiguan binds against its own manifest-built table. Its pieces are
        /// named "tig*" and match nothing in <c>AccentTokens</c>, so on the
        /// shared table every renderer would miss and fall through to the body
        /// material — a correctly shaped 4.5 m grey car.
        /// </summary>
        public static (string, Material)[] BodyAccentTable(BodyDef def) =>
            def == null ? null : def.tokens switch
            {
                BodyTokens.Tiguan => PartVisualFactory.TiguanTokens,
                BodyTokens.Accent => PartVisualFactory.AccentTokens,
                _ => null,
            };

        /// <summary>
        /// Bind an instantiated body shell's renderers.
        ///
        /// On an accent-token body: "paint" panels get the tintable body
        /// material and are the only renderers registered in
        /// <paramref name="paintRenderers"/> (so bodyColor, livery and
        /// SetBodyMaterial touch nothing else); every other token gets its
        /// shared accent material; an unmatched name falls back to the body
        /// material so a renamed export shows up as tintable, not magenta. On
        /// the three legacy shells every renderer flattens onto the body
        /// material instead.
        ///
        /// <b>Static, and public, because it is the whole of what a car does to
        /// a body mesh.</b> <c>PartModelBindingDump</c> calls this rather than
        /// transcribing the branch — a dumper with its own copy of a call site
        /// keeps passing after that call site changes, which would make its
        /// empty diff worth nothing. (Asset Studio's preview deliberately does
        /// NOT come through here: it binds an arbitrary FBX against a table the
        /// author picks, so it has no BodyShape to ask about. It shares the loop
        /// below, not this decision.)
        /// </summary>
        public static MaterialBindings BindBodyMesh(GameObject inst, BodyDef def,
            Material bodyMat, ICollection<MeshRenderer> paintRenderers) =>
            PartVisualFactory.BindByToken(inst, BodyAccentTable(def),
                                          bodyMat, paintRenderers);

        /// <summary>The renderers driven by the tintable body material — the
        /// paint panels on an accent body, every body renderer otherwise. The
        /// garage painter cooks its stroke colliders from exactly these.</summary>
        public IReadOnlyList<MeshRenderer> PaintRenderers => _bodyRenderers;

        /// <summary>
        /// Whether this body shows an authored UV-mapped shell that the garage's
        /// paint mode can work on.
        ///
        /// <b>Two questions with one answer, and they stay separate here.</b>
        /// <see cref="BodyDef.paintable"/> is the design fact — does this shell
        /// have a tintable channel at all — and the rest of this function is the
        /// machine fact: did the FBX ship, and if it ships a manifest, what does
        /// the manifest say. Only the first moved into the catalogue at K3a;
        /// asking a table whether a file exists would be a table that goes stale
        /// the moment somebody deletes one.
        ///
        /// The five bodies that answer false on the design fact do so for one
        /// reason between them: their paint IS the artwork. The three TinyTorque
        /// show cars (candy crimson and stripes, acid lime and bands, the police
        /// black-and-white) and the Rattletrap's rust are baked textures, and the
        /// Tiguan's painted panel is named "tigpaint_1", which does not
        /// <c>StartsWith("paint")</c> — so every renderer binds an accent and
        /// <see cref="PaintRenderers"/> comes back empty. Offering paint mode on
        /// any of them would be a mode that silently does nothing.
        /// </summary>
        public static bool HasPaintableBody(BodyDef def)
        {
            if (def == null || !def.paintable) return false;
            string key = def.meshKey;
            if (key == null) return false;

            // A manifest asset answers this itself instead of being guessed at.
            // Paint mode works iff some material is declared the paint channel —
            // which for a baked livery is a deliberate authoring choice (the
            // design's colour MULTIPLIES the artwork) and not something a name
            // prefix can be read for. Verbatim mode is a flat no: its whole
            // premise is that the FBX's own materials are untouched, and a paint
            // mode that silently did nothing would be worse than none. No shipped
            // key ships a manifest, so every branch below this line is unreachable
            // until Asset Studio commits one.
            AssetManifest m = AssetManifests.Load(key);
            if (m != null)
            {
                if (m.IsVerbatim) return false;
                foreach (AssetMaterialDef d in m.materials)
                    if (d != null && d.paintChannel) return true;
                return false;
            }
            return PartMeshLibrary.Has(key);
        }

        /// <summary>
        /// Decoded livery texture (set by VehicleFactory before Awake; owned by
        /// this car — destroyed with it). Null = plain bodyColor.
        /// </summary>
        [System.NonSerialized] public Texture2D liveryTex;

        /// <summary>
        /// Swap the body's albedo texture live (garage paint mode). The caller
        /// keeps ownership of <paramref name="tex"/>; passing null restores the
        /// plain bodyColor look.
        /// </summary>
        public void SetBodyTexture(Texture2D tex)
        {
            if (_bodyMat == null) return;
            WarnIfNothingToPaint("SetBodyTexture");
            _bodyMat.mainTexture = tex;
            _bodyMat.color = tex != null ? Color.white : bodyColor;
        }

        /// <summary>
        /// Say so, once, when a paint operation has nowhere to land.
        ///
        /// <b>The failure this exists for is a no-op that looks like success.</b>
        /// <c>_bodyMat</c> always exists, so writing a colour or a texture onto it
        /// always "works"; whether anything RENDERS from it depends on whether the
        /// binder registered any paint target, and on a verbatim asset it
        /// deliberately did not — the FBX's own materials are the whole point of
        /// that mode. Without this the symptom is a livery that simply does not
        /// appear, with nothing anywhere saying why.
        ///
        /// Once per car, because the garage painter calls
        /// <see cref="SetBodyTexture"/> on every stroke and a warning per stroke is
        /// a warning nobody reads. <see cref="HasPaintableBody"/> answers the same
        /// question BEFORE the call and is what a caller should ask; this is for
        /// the callers that did not.
        /// </summary>
        private void WarnIfNothingToPaint(string what)
        {
            if (_noPaintWarned) return;
            if (_bodyRenderers.Count > 0) return;
            if (_bodyBinding != null && _bodyBinding.HasPaintSlots) return;
            _noPaintWarned = true;

            bool verbatim = _bodyBinding != null && _bodyBinding.Manifest != null
                            && _bodyBinding.Manifest.IsVerbatim;
            Debug.LogWarning(
                $"[CarVehicle] {what} on {name} ({Body.id}) changes nothing: " +
                (verbatim
                    ? "this body is in Verbatim material mode, so it keeps the FBX's own " +
                      "materials and has no tintable channel by design."
                    : "this shell registered no paint renderer — its finish is baked or " +
                      "token-bound.") +
                $" HasPaintableBody({Body.id}) already answers false.");
        }

        private bool _noPaintWarned;

        private void OnDestroy()
        {
            if (liveryTex != null) Destroy(liveryTex);
        }

        private void AddBodyPiece(Transform holder, Vector3 localPos, Vector3 localScale, Vector3 localEuler)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Piece";
            Destroy(go.GetComponent<Collider>()); // collision handled by the root BoxCollider
            go.transform.SetParent(holder, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(localEuler);
            go.transform.localScale = localScale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = _bodyMat;
            _bodyRenderers.Add(r);
        }

        private void BuildBoxBody(Transform holder)
        {
            AddBodyPiece(holder, Vector3.zero, bodySize, Vector3.zero);
        }

        private void BuildWedgeBody(Transform holder)
        {
            Vector3 s = bodySize;
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.22f, 0f),
                new Vector3(s.x, s.y * 0.55f, s.z), Vector3.zero);
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.18f, -s.z * 0.12f),
                new Vector3(s.x * 0.9f, s.y * 0.5f, s.z * 0.55f), new Vector3(8f, 0f, 0f));
        }

        private void BuildBuggyBody(Transform holder)
        {
            Vector3 s = bodySize;
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.05f, 0f),
                new Vector3(s.x * 0.5f, s.y * 0.6f, s.z * 0.85f), Vector3.zero);
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.45f, -s.z * 0.05f),
                new Vector3(s.x * 0.55f, s.y * 0.14f, s.z * 0.3f), Vector3.zero);
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.15f, s.z * 0.45f),
                new Vector3(s.x * 0.35f, s.y * 0.3f, s.z * 0.22f), Vector3.zero);
        }

        /// <summary>Touring-car lexan shell: low slab, tapered cabin, faired nose + tail.</summary>
        private void BuildShellBody(Transform holder)
        {
            Vector3 s = bodySize;
            // Lower hull.
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.25f, 0f),
                new Vector3(s.x, s.y * 0.5f, s.z * 0.94f), Vector3.zero);
            // Cabin: narrower, raked front and rear glass.
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.12f, -s.z * 0.06f),
                new Vector3(s.x * 0.82f, s.y * 0.42f, s.z * 0.44f), Vector3.zero);
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.02f, s.z * 0.20f),
                new Vector3(s.x * 0.80f, s.y * 0.34f, s.z * 0.24f), new Vector3(22f, 0f, 0f));
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.02f, -s.z * 0.30f),
                new Vector3(s.x * 0.80f, s.y * 0.34f, s.z * 0.22f), new Vector3(-18f, 0f, 0f));
            // Faired nose.
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.22f, s.z * 0.44f),
                new Vector3(s.x * 0.85f, s.y * 0.38f, s.z * 0.18f), new Vector3(10f, 0f, 0f));
        }

        /// <summary>F1TENTH-style low racer: flat deck, nose wedge, side pods.</summary>
        private void BuildLowRacerBody(Transform holder)
        {
            Vector3 s = bodySize;
            // Flat main deck, low.
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.28f, 0f),
                new Vector3(s.x * 0.9f, s.y * 0.44f, s.z * 0.9f), Vector3.zero);
            // Nose wedge.
            AddBodyPiece(holder, new Vector3(0f, -s.y * 0.14f, s.z * 0.38f),
                new Vector3(s.x * 0.6f, s.y * 0.3f, s.z * 0.26f), new Vector3(14f, 0f, 0f));
            // Side pods.
            for (int e = -1; e <= 1; e += 2)
                AddBodyPiece(holder, new Vector3(e * s.x * 0.42f, -s.y * 0.24f, -s.z * 0.05f),
                    new Vector3(s.x * 0.18f, s.y * 0.36f, s.z * 0.5f), Vector3.zero);
            // Electronics hump behind the (imagined) driver.
            AddBodyPiece(holder, new Vector3(0f, s.y * 0.06f, -s.z * 0.18f),
                new Vector3(s.x * 0.4f, s.y * 0.34f, s.z * 0.28f), Vector3.zero);
        }

        /// <summary>
        /// Recolour/replace the body material (used by the scene builder).
        ///
        /// <c>sharedMaterial</c> is slot 0 and nothing else, which is exactly right
        /// for every shell bound by a token table — that binder can only write slot
        /// 0 either. On a manifest asset it is wrong in a way that looks like a
        /// paint bug: <c>Police_Body</c> is dark trim in slot 0 and paint in slot 1,
        /// so writing slot 0 would erase the trim and leave the paint untouched.
        /// The manifest table knows which slot is which and is asked when there is
        /// one.
        /// </summary>
        public void SetBodyMaterial(Material mat)
        {
            if (mat == null) return;
            WarnIfNothingToPaint("SetBodyMaterial");
            _bodyMat = mat;
            if (_bodyBinding != null && _bodyBinding.HasPaintSlots)
            {
                _bodyBinding.ApplyPaint(mat);
                return;
            }
            foreach (var r in _bodyRenderers)
                if (r != null) r.sharedMaterial = mat;
        }

        private void Start()
        {
            if (!_spawnCaptured) CaptureSpawn();
        }

        /// <summary>Record the current pose as the respawn point.</summary>
        public void CaptureSpawn()
        {
            _spawnPos = transform.position;
            _spawnRot = transform.rotation;
            _spawnCaptured = true;
        }

        /// <summary>Set an explicit respawn pose (the start-line spawn point).</summary>
        public void SetSpawn(Vector3 pos, Quaternion rot)
        {
            _spawnPos = pos;
            _spawnRot = rot;
            _spawnCaptured = true;
        }

        /// <summary>Unsprung mass at a corner (kg). The legacy 0.05 is a 50 g RC
        /// wheel; a real one is ~500x that, and it is the WheelCollider's own
        /// mass, so it decides how the contact behaves as well as what it
        /// weighs.</summary>
        private static float UnsprungMass(CarWheelConfig cfg) =>
            cfg.unsprungMassKg > 0f ? cfg.unsprungMassKg : 0.05f;

        private Wheel MakeWheel(string name, CarWheelConfig cfg, float cornerMass)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            // The WheelCollider sits at the hub (mount + strut drop). With no strut
            // (suspLength 0) the hub is the mount, so this is today's placement.
            go.transform.localPosition = HubLocal(cfg);
            // Strut tilt inclines the WheelCollider mount: suspension travel (always
            // along the mount's up axis) angles inward/outward and the wheel gains a
            // camber-like lean. Steering still rotates about this tilted up (mild caster).
            go.transform.localRotation = Quaternion.Euler(0f, cfg.yaw, StrutTiltZ(cfg));

            var wc = go.AddComponent<WheelCollider>();
            wc.radius = Mathf.Max(0.01f, cfg.radius);
            // Strut length scales wheel travel via the rocker motion ratio (longer arm
            // => more wheel travel). Legacy length 0 => the raw travel, unchanged.
            float baseTravel = cfg.suspTravel > 0f ? cfg.suspTravel : suspensionDistance;
            wc.suspensionDistance = Mathf.Max(0.005f,
                SuspensionGeometry.EffectiveTravel(baseTravel, cfg.suspLength));
            wc.center = Vector3.zero;
            // Wheel spin inertia. Brush tyre path: the vehicle integrates ω itself,
            // so J lives on the Wheel (bare wheel ½·0.05·r² + the reflected
            // drivetrain J·gear², directly additive in the wheel frame) and the
            // WheelCollider keeps its plain mass. Legacy path: PhysX integrates
            // spin with I = ½·wc.mass·r², so reflected inertia is folded in as
            // equivalent mass 2·J_reflected/r² — a 540 rotor through 8:1 is ~12×
            // the bare wheel's inertia, which is why spin-up felt crisp.
            wc.mass = UnsprungMass(cfg) + (!TyreModel.Enabled && cfg.extraSpinInertia > 0f
                ? 2f * cfg.extraSpinInertia / Mathf.Max(1e-6f, wc.radius * wc.radius)
                : 0f);
            // CRITICAL at RC scale: Unity's default wheelDampingRate (0.25) would
            // apply ~75 N·m of damping at 300 rad/s against a ~0.8 N·m motor — the
            // car would never move. 0.0002 ≈ 0.06 N·m at top speed (rolling loss).
            wc.wheelDampingRate = 0.0002f;

            var spring = wc.suspensionSpring;
            // Strut length softens the wheel rate via MR² (longer arm => lower rate).
            // Legacy length 0 => the raw spring. The damper below derives from this
            // post-scaled spring, so ζ stays constant at any strut length.
            float baseSpring = cfg.suspStiffness > 0f ? cfg.suspStiffness : suspensionSpring;
            spring.spring = SuspensionGeometry.EffectiveRate(baseSpring, cfg.suspLength);
            // ζ > 0 → derive the damper from the corner mass; ζ = 0 is the legacy
            // sentinel that keeps the vehicle-wide raw damper.
            spring.damper = cfg.suspDampingRatio > 0f
                ? cfg.suspDampingRatio * 2f * Mathf.Sqrt(spring.spring * Mathf.Max(0.001f, cornerMass))
                : suspensionDamper;
            // Where the corner rests in its travel. Static ride height sits at
            // targetPosition − W/(k·D), so a heavy car on realistic travel needs
            // this above the legacy mid-point or it rides on the bump stops.
            spring.targetPosition = cfg.suspTargetPos > 0f ? cfg.suspTargetPos : 0.5f;
            wc.suspensionSpring = spring;

            wc.forceAppPointDistance = 0.02f;

            // Brush tyre path: PhysX contributes (as close as it allows to) NO tyre
            // force — the WheelCollider is suspension only, and all tyre forces come
            // from TyreModel applied at the contact point. NOTE: stiffness = 0 is
            // NOT honoured as "no friction" by PhysX's tire constraint (measured:
            // it pinned the car against 9 N of applied force), so the curve VALUES
            // are floored at a tiny epsilon instead. Legacy path: the original
            // two-segment curves.
            // The curves keep their well-formed legacy shape in BOTH paths (PhysX
            // dislikes degenerate curves — a flat epsilon curve hard-crashed the
            // editor); the brush path just scales them to nothing via stiffness.
            float grip = GripMult(cfg);
            // The residual is proportional to LOAD, so the "≤0.05 N/wheel" this
            // was justified by holds only at an RC corner weight. Authored, with
            // the legacy literal as the default, and floored so the curve can
            // never go degenerate — only this scalar moves, never the shape.
            float BrushEps = cfg.brushEps > 0f ? Mathf.Max(1e-5f, cfg.brushEps) : 0.01f;
            var fwd = wc.forwardFriction;
            fwd.extremumSlip = 0.4f; fwd.extremumValue = 1.0f;
            fwd.asymptoteSlip = 0.8f; fwd.asymptoteValue = 0.75f;
            fwd.stiffness = TyreModel.Enabled ? BrushEps : FwdStiffness * grip;
            wc.forwardFriction = fwd;

            var side = wc.sidewaysFriction;
            side.extremumSlip = 0.25f; side.extremumValue = 1.0f;
            side.asymptoteSlip = 0.5f; side.asymptoteValue = 0.8f;
            side.stiffness = TyreModel.Enabled ? BrushEps : _gripStiffness * grip;
            wc.sidewaysFriction = side;

            // Visual: a holder tracks the WheelCollider world pose; a stylized
            // compound-primitive wheel is built inside it. The motor can faces
            // inboard (toward the chassis centreline).
            var holder = new GameObject(name + "_Viz").transform;
            holder.SetParent(transform, false);
            float inboardSign = cfg.localPos.x > 0.001f ? -1f : 1f;
            PartVisualFactory.BuildWheelViz(holder, cfg.radius, cfg.powered, inboardSign, cfg.Wheel);

            // Visible strut spanning body mount → hub (only when a strut length is set).
            Transform strut = null;
            if (cfg.suspLength > 0f)
            {
                strut = new GameObject(name + "_Strut").transform;
                strut.SetParent(transform, false);
                PartVisualFactory.BuildStrutViz(strut);
            }

            // Linkage kinematics, evaluated once at the STATIC ride position — the
            // hub hangs (1 − targetPosition) of its travel below the collider
            // origin, which is where the authored hardpoints were measured. Both
            // return 0 for an unauthored linkage. (Strut tilt is ignored in
            // locating the static centre; the only design authoring hardpoints has
            // suspAngleDeg = 0, and a tilted strut's hub moves in x as well, which
            // this first-order placement does not track.)
            float restComp = 1f - spring.targetPosition;
            Vector3 staticCentre = HubLocal(cfg)
                                 - Vector3.up * (restComp * wc.suspensionDistance);
            float rcH = SuspensionGeometry.RollCentreHeight(cfg.linkage, staticCentre, wc.radius);
            float toePerM = SuspensionGeometry.ToeSteerPerMetre(cfg.linkage, staticCentre);

            return new Wheel
            {
                col = wc, viz = holder, cfg = cfg, baseRadius = wc.radius,
                strut = strut, mountLocal = cfg.localPos,
                rollCentreH = rcH,
                restCompression = restComp,
                // Stored in DEGREES per metre: currentSteer is a degree angle, and
                // Unity's y-Euler is left-handed, so the right-handed yaw the
                // kinematics return is negated here rather than at every use.
                toeSteerPerM = -toePerM * Mathf.Rad2Deg,
                // J for the brush-path spin integrator: bare wheel + reflected
                // drivetrain. Authored when the design says so, because the
                // legacy ½·m·r² models a solid DISC and a tyre is closer to a
                // ring — a coincidence that does not survive a change of scale,
                // and the single most consequential number in this method.
                spinInertia = (cfg.spinInertiaKgM2 > 0f
                                  ? cfg.spinInertiaKgM2
                                  : 0.5f * 0.05f * wc.radius * wc.radius)
                            + Mathf.Max(0f, cfg.extraSpinInertia),
            };
        }

        /// <summary>
        /// Raised after the vehicle has been teleported back to its spawn. A
        /// dead-reckoning controller has to know: the teleport moves the body but
        /// NOT the wheel spin or the encoder accumulators, so without this signal
        /// its odometer keeps integrating across the discontinuity and counts
        /// distance the car never travelled.
        /// </summary>
        public event System.Action VehicleReset;

        public void ResetVehicle()
        {
            if (!_spawnCaptured) CaptureSpawn();
            ResetVehicleTo(_spawnPos, _spawnRot);
        }

        /// <summary>
        /// <see cref="ResetVehicle"/> to an arbitrary pose: same full reset of
        /// motors, pack, wheels, encoders and IMU, different destination.
        ///
        /// Split out for the respawn key, which now puts the car back on the
        /// nearest point of the racing line instead of the start line (see
        /// <c>Core.TrackRespawn</c>). Everything that resets a RUN rather than a
        /// stuck car — SimulationRunner, the mission harness — still calls
        /// <see cref="ResetVehicle"/> and still goes to the spawn point.
        /// </summary>
        public void ResetVehicleTo(Vector3 pos, Quaternion rot)
        {
            System.Array.Clear(_cmd, 0, _cmd.Length);
            _handbrake = false;
            foreach (var m in _motors) m?.ResetMotor();
            BatteryCurrent = 0f;
            _battAhUsed = 0f;    // respawn = fresh pack (run-to-run determinism)
            BatterySoc = 1f;

            // Teleport the physics body. ContinuousDynamic collision detection
            // sweeps away a plain transform move, so switch to Discrete for it.
            var mode = _body.collisionDetectionMode;
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            _body.position = pos;
            _body.rotation = rot;
            transform.SetPositionAndRotation(pos, rot);
            Physics.SyncTransforms();
            _body.collisionDetectionMode = mode;

            // A respawn is also the one guaranteed way out of a boost-pad pin, so
            // clear the latch rather than making the player wait it out.
            _padStallTime = 0f;
            _padPinned = false;
            _launchScale = 1f;   // fresh launch, fresh governor

            foreach (var w in _wheels)
            {
                w.currentSteer = 0f;
                w.col.motorTorque = 0f;
                w.col.brakeTorque = 0f;
                w.col.steerAngle = 0f;
                w.omega = 0f;
                w.spinAngle = 0f;
                w.driveTorque = 0f;
                w.slipRatio = 0f;
                w.slipNorm = 0f;
                w.patchSpeed = 0f;
                w.grounded = false;
            }
            TyreSlip01 = 0f;
            imu.Reset();
            VehicleReset?.Invoke();
        }

        /// <summary>
        /// Teleport to a saved pose with saved velocities (session-snapshot
        /// resume). Mirrors <see cref="ResetVehicle"/>'s Discrete-mode teleport
        /// but keeps the spawn point and restores motion instead of zeroing it.
        /// </summary>
        public void RestoreState(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
        {
            var mode = _body.collisionDetectionMode;
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            _body.position = pos;
            _body.rotation = rot;
            transform.SetPositionAndRotation(pos, rot);
            Physics.SyncTransforms();
            _body.linearVelocity = vel;
            _body.angularVelocity = angVel;
            _body.collisionDetectionMode = mode;
        }

        /// <summary>
        /// Latch the actuator vector. Convention: [motor.ActuatorIndex] = motor
        /// volts, [6] = steer [-1,1], [7] = brake [0,1]. Both Manual (CarInput) and
        /// Autonomous (the DLL) produce this same layout.
        /// </summary>
        public void SetCommands(float[] actuatorCommands)
        {
            int n = Mathf.Min(actuatorCommands.Length, _cmd.Length);
            for (int i = 0; i < n; i++) _cmd[i] = actuatorCommands[i];
        }

        public void SetHandbrake(bool on) => _handbrake = on;

        /// <summary>Race-countdown hold: forces full brakes and zero drive/steer
        /// so the car can't move until the countdown reaches GO. Set by the
        /// RaceDirector; covers every drive mode (manual, bot, firmware).</summary>
        public bool Frozen;

        // ---- boost-pad pin escape ----
        // A pad pushes along transform.forward for as long as a wheel is on it,
        // which is correct while you are moving and a trap when you are not: nose
        // into a wall on a pad and the pad holds you there, out-torquing reverse
        // and pinning the car straight so steering cannot walk it off either. The
        // only way out was the respawn key.
        //
        // Rather than special-case walls, this watches the outcome — on a pad and
        // not going anywhere — and stands the pad down until the car is genuinely
        // rolling forward again. Self-clearing, needs no notion of what is in the
        // way, and works the same for a bot as for a player.
        //
        // Provably inert off a pad: the whole block is inside `padBoost > 0`, and
        // a surface with no boostAccel never sets it. Nothing about the arcade
        // boost channel (items, drift carry, slipstream) is touched — those are
        // seconds long and end on their own.

        /// <summary>Forward speed below which a pad counts as not carrying you.</summary>
        private const float PadPinSpeed = 1.0f;
        /// <summary>Forward speed that proves the car is away and re-arms the pad.
        /// Above <see cref="PadPinSpeed"/> so the pad cannot chatter on and off.</summary>
        private const float PadFreeSpeed = 1.6f;
        /// <summary>How long pinned before the pad gives up. Long enough that
        /// crawling onto a pad from a standstill still gets its shove.</summary>
        private const float PadPinSeconds = 0.7f;

        private float _padStallTime;
        private bool _padPinned;

        public void StepPhysics(float dt)
        {
            // Battery bus: terminal voltage sags with LAST step's total current
            // across the pack's internal resistance (explicit integration — stable
            // at 400 Hz). internalR/nominalV = 0 (no battery part) = stiff rail.
            // With a finite capacity a coulomb counter tracks state of charge and
            // the open-circuit voltage follows the LiPo discharge curve — a fresh
            // pack sits ABOVE nominal (4.2 V/cell) and the rail droops over a run.
            float vTerm = float.MaxValue;
            if (batteryNominalV > 0f)
            {
                float v0 = batteryNominalV;
                if (batteryCapacitymAh > 0f)
                {
                    _battAhUsed += BatteryCurrent * dt / 3600f;
                    BatterySoc = Mathf.Clamp01(1f - _battAhUsed / (batteryCapacitymAh * 0.001f));
                    int cells = Mathf.Max(1, Mathf.RoundToInt(batteryNominalV / 3.7f));
                    v0 = cells * CellOcv(BatterySoc);
                }
                vTerm = Mathf.Max(0f, v0 - batteryInternalR * BatteryCurrent);
            }
            BatteryTerminalV = batteryNominalV > 0f ? vTerm : 0f;

            // Drive: each motor turns its latched voltage into wheel torque via the
            // DC model, so the achievable torque/current emerges from the physics.
            float iTotal = 0f;
            for (int i = 0; i < _motors.Count; i++)
            {
                var m = _motors[i];
                if (m == null) continue;
                int idx = m.ActuatorIndex;
                float volts = Frozen ? 0f : ((idx >= 0 && idx < _cmd.Length) ? _cmd[idx] : 0f);
                volts *= arcadeDriveMult;                      // 1 outside arcade
                volts *= _launchScale;                         // 1 unless launch control is cutting
                if (batteryNominalV > 0f)
                    volts = Mathf.Clamp(volts, -vTerm, vTerm); // sagging rail caps the command
                m.SetVoltage(volts);
                m.StepDrive(dt);
                // Pack draw only — a shorted-winding ESC brake circulates its
                // current inside the bridge and loads the pack not at all.
                iTotal += m.PackCurrent;
            }
            BatteryCurrent = iTotal;

            float steerCmd = Mathf.Clamp(_cmd[SteerActuator], -1f, 1f);
            float brake = Mathf.Clamp01(_cmd[BrakeActuator]) * maxBrakeTorque;
            float boost = arcadeBoostAccel;   // 0 outside arcade; pads max against it
            // Pads are accumulated separately so the pin latch below can stand
            // THEM down without touching an item boost or a drift carry. Maxing
            // the two together afterwards gives the identical number the single
            // variable used to.
            float padBoost = 0f;

            // Race countdown hold: full brakes, no drive/steer until GO.
            if (Frozen) { brake = maxBrakeTorque; steerCmd = 0f; }

            // Rolling resistance must oppose MOTION, not act as a parking brake:
            // ramp it in from 0 at a standstill to full above RollResistRampSpeed.
            // At zero speed this keeps idle/unpowered wheels from locking into a
            // skid (they carry no motor torque), so the car launches freely on
            // grass/sand/mud.
            Vector3 hv = _body.linearVelocity; hv.y = 0f;
            float rollScale = Mathf.Clamp01(hv.magnitude / RollResistRampSpeed);

            // Arcade assists (0 = pure physics). Forced off in Autonomous mode.
            AssistSettings a = assistsActive ? assists : default;
            // Neutral (1) everywhere except inside an arcade drift; see the field.
            a.steer *= arcadeAssistMult;
            a.stability *= arcadeAssistMult;

            // Brush path: decide whether PhysX's low-speed "sticky tire"
            // constraints may engage. They pin a near-stationary car's contact
            // patches with a hard velocity constraint that NO friction setting
            // disables (measured: they held the car against 9 N of tyre force),
            // and they key off the PHYSX wheels' rotation — which our integrator
            // bypasses. So: whenever the vehicle should be free to move, phantom
            // torque keeps the PhysX wheels spinning (releasing the constraints
            // on all four corners); only at true rest do we brake the phantom
            // wheels so the constraints return and park the car (slope hold).
            bool stickyHold = true;
            if (TyreModel.Enabled)
            {
                if (_body.linearVelocity.sqrMagnitude > RestSpeed * RestSpeed)
                    stickyHold = false;
                else
                {
                    // Drive gate: any INTENDED drive must release the parking
                    // constraint or the car cannot move off. "Intended" is a
                    // rim force worth a 10⁻³ slice of the per-wheel weight — a
                    // dimensionless tolerance, so it lands where it should at
                    // every scale (≈0.2 mN·m on an RC wheel, ≈1.3 N·m on the
                    // Tiguan) instead of the old absolute 0.005 N·m, which was
                    // meaningful against an RC lock threshold of 0.23 N·m and
                    // noise-floor nothing against a full-scale motor. At true
                    // zero command the motor torque is exactly zero (0 V, ω 0
                    // → no current), so there is no noise for a small gate to
                    // false-trigger on.
                    float driveEpsRimForce = 0.001f * _body.mass *
                        Mathf.Abs(Physics.gravity.y) / Mathf.Max(1, _wheels.Count);
                    foreach (var w in _wheels)
                        if (Mathf.Abs(w.omega) * w.col.radius > RestSpeed
                            || Mathf.Abs(w.driveTorque) > driveEpsRimForce * w.col.radius)
                        {
                            stickyHold = false;
                            break;
                        }
                }
            }

            // Steering assist: limit lock at speed + countersteer toward the
            // velocity direction when the body slips sideways.
            if (a.steer > 0f)
            {
                float sp = Mathf.Abs(ForwardSpeed);
                steerCmd *= Mathf.Lerp(1f, Mathf.Min(1f,
                    AssistTuning.SteerLimitRef(a.steer) / Mathf.Max(AssistTuning.SteerLimitMinSpeed, sp)),
                    a.steer);
                Vector3 vLoc = transform.InverseTransformDirection(_body.linearVelocity);
                if (Mathf.Abs(vLoc.z) > AssistTuning.CounterSteerMinLongSpeed)
                {
                    float slipAngle = Mathf.Atan2(vLoc.x, Mathf.Abs(vLoc.z)); // + = sliding right
                    steerCmd = Mathf.Clamp(
                        steerCmd + a.steer * Mathf.Clamp(slipAngle * AssistTuning.CounterSteerGain,
                            -AssistTuning.CounterSteerClamp, AssistTuning.CounterSteerClamp),
                        -1f, 1f);
                }
            }

            // Servo torque-speed line: available slew collapses as cornering load
            // rises toward the stall torque (0 = ideal servo, legacy behaviour).
            // Load = last step's steered-wheel lateral forces through the trail,
            // plus a small constant linkage friction.
            float steerRate = steerRateDegPerSec;
            if (servoStallNm > 0f)
            {
                float tLoad = SteerFrictionNm;
                foreach (var w in _wheels)
                    if (w.cfg.allowsSteering) tLoad += Mathf.Abs(w.lastFy) * SteerTrailM;
                steerRate *= Mathf.Clamp01(1f - tLoad / servoStallNm);
            }

            // Brake proportioning (EBD). Without it the only way to express brake
            // balance is the fixed per-wheel brakeScale, and a fixed ratio can be
            // right at exactly one state of load transfer. Under threshold braking
            // the Tiguan's authored 0.35 rear bias left the rears far below their
            // slip peak while the fronts sat on theirs — the axle-averaged slip the
            // test servos to hid it — and the stop measured 0.788 g against a µ of
            // 1.0. Distributing with instantaneous load lets both axles peak
            // together, which is the whole job of a proportioning valve.
            //
            // Total torque is preserved: the shares are normalised to average 1, so
            // this redistributes rather than adds. The handbrake is deliberately
            // excluded below — it is a rear lock on purpose, not something to
            // balance.
            bool ebd = brakeProportioning && TyreModel.Enabled
                       && brake > 0f && _wheels.Count > 0;
            if (ebd)
            {
                float loadSum = 0f;
                foreach (var w in _wheels)
                {
                    w.brakeShare = w.col.GetGroundHit(out WheelHit bh)
                        ? Mathf.Max(0f, bh.force) : 0f;
                    loadSum += w.brakeShare;
                }
                if (loadSum > 1f)
                {
                    float norm = _wheels.Count / loadSum;
                    foreach (var w in _wheels) w.brakeShare *= norm;
                }
                else ebd = false;   // airborne: nothing to proportion against
            }

            foreach (var w in _wheels)
            {
                // Per-wheel steering servo (slew toward the commanded angle).
                if (w.cfg.allowsSteering)
                {
                    // Virtual (bicycle-model) angle, then blend toward the exact
                    // Ackermann angle for THIS wheel's position: the turn centre
                    // sits y_c = L/tan(δv) beside the rear-axle line, and the
                    // wheel steers to point at it — the inner wheel sharper.
                    float dv = steerCmd * w.cfg.steerAngle;
                    float di = dv;
                    if (ackermannPct > 0f && Mathf.Abs(dv) >= 0.25f)
                    {
                        float L = w.cfg.localPos.z - _ackZRef;
                        if (Mathf.Abs(L) > 1e-3f)
                        {
                            float yc = L / Mathf.Tan(dv * Mathf.Deg2Rad);
                            float denom = yc - w.cfg.localPos.x;
                            if (Mathf.Abs(denom) > 1e-3f)
                            {
                                float dAck = Mathf.Atan(L / denom) * Mathf.Rad2Deg;
                                di = dv + Mathf.Clamp01(ackermannPct / 100f) * (dAck - dv);
                            }
                        }
                    }
                    float sgn = w.cfg.reverseSteering ? -1f : 1f;
                    float target = di * sgn;
                    w.servoSteer = Mathf.MoveTowards(w.servoSteer, target, steerRate * dt);
                }
                else w.servoSteer = 0f;

                // Roll steer, added AFTER the servo rather than slewed toward.
                // It is geometry, not a command: the toe link swings on an arc as
                // the hub moves, so the wheel toes whether or not anything is
                // steering it. That is what lets the REAR wheels steer in roll —
                // they have no servo at all and used to be pinned to zero — and
                // it is the dominant real-world understeer term that this model
                // was missing entirely. Branch-guarded: no toe link authored
                // leaves currentSteer exactly the servo value it always was.
                float toeDeg = RollSteerDeg(w);
                w.currentSteer = toeDeg == 0f ? w.servoSteer : w.servoSteer + toeDeg;
                w.col.steerAngle = w.currentSteer;

                // Foot brake everywhere; handbrake locks the non-steering wheels.
                // arcadeHandbrakeMult is 1 outside a drift, so this is the same
                // expression it has always been.
                // brakeScale is the per-wheel bias this engine otherwise has no
                // way to express: one torque on all four locks a real car's
                // rears first and spins it. Branched rather than multiplied
                // unconditionally — x*1.0f == x is exact in IEEE-754, but the
                // branch means the bit-identity claim needs no appeal to it.
                // With EBD on, the load share REPLACES brakeScale — the fixed bias
                // exists only as a stand-in for the valve, so applying both would
                // bias an already-balanced distribution.
                float b = (ebd
                              ? brake * w.brakeShare
                              : (w.cfg.brakeScale == 1f || w.cfg.brakeScale <= 0f
                                    ? brake : brake * w.cfg.brakeScale))
                        + ((_handbrake && !w.cfg.allowsSteering)
                            ? handbrakeTorque * arcadeHandbrakeMult : 0f);

                // Per-tile surface (custom maps only): friction scaling, rolling
                // resistance, boost pads, rumble strips. Off tile maps SurfaceMap
                // returns the baseline and this whole block is a no-op.
                var surf = SurfaceInfo.Baseline;
                bool grounded = w.col.GetGroundHit(out WheelHit hit);
                if (grounded)
                {
                    surf = SurfaceMap.At(hit);
                    // Legacy path: rolling resistance rides the brake torque.
                    // Brush path: it becomes a torque in the spin integrator below.
                    if (!TyreModel.Enabled) b += surf.rollingResist * rollScale;
                    if (surf.boostAccel > padBoost) padBoost = surf.boostAccel;
                    if (surf.bumpAmp > 0f)
                    {
                        // Position-based vertical buzz: crossing the strip oscillates
                        // each wheel with a short spatial wavelength.
                        float s = Vector3.Dot(hit.point, transform.forward);
                        float f = Mathf.Sin(s * 120f) * surf.bumpAmp * rollScale *
                                  _body.mass * 9.81f / Mathf.Max(1, _wheels.Count);
                        _body.AddForceAtPosition(Vector3.up * f, hit.point, ForceMode.Force);
                    }
                    if (surf.roughAmp > 0f)
                    {
                        // Surface roughness: band-limited vertical force from a
                        // deterministic positional noise field — pebbles/seams
                        // that are identical lap after lap (repeatable runs).
                        float lambda = Mathf.Max(0.02f, surf.roughLen);
                        float n = ValueNoise2(hit.point.x / lambda, hit.point.z / lambda);
                        // Speed-gated (like rolling resistance) so a stopped car sits on
                        // an even load and always launches — no permanently-unloaded wheel
                        // at a rough grass/dirt spot.
                        float f = n * surf.roughAmp * rollScale *
                                  _body.mass * 9.81f / Mathf.Max(1, _wheels.Count);
                        _body.AddForceAtPosition(Vector3.up * f, hit.point, ForceMode.Force);
                    }
                }

                // Load-sensitive grip: real tires lose relative grip as normal load
                // rises, μ ∝ (Fz/Fz0)^-s. Quantized to 2 % steps (legacy path needs
                // WheelFrictionCurve writes to stay occasional; harmless for brush).
                float loadFactor = 1f;
                if (w.cfg.loadSensitivity > 0f && grounded && _cornerWeight > 0.01f)
                {
                    loadFactor = Mathf.Pow(Mathf.Max(0.05f, hit.force / _cornerWeight),
                                           -w.cfg.loadSensitivity);
                    loadFactor = Mathf.Clamp(loadFactor, 0.6f, 1.4f);
                    loadFactor = Mathf.Round(loadFactor / 0.02f) * 0.02f;
                }

                // Tire ballooning: centrifugal growth of the rolling radius with
                // wheel speed (foam/rubber RC tires expand several % flat out).
                if (w.cfg.balloonPct > 0f)
                {
                    float omegaAbs = TyreModel.Enabled
                        ? Mathf.Abs(w.omega)
                        : Mathf.Abs(w.col.rpm) * (Mathf.PI * 2f / 60f);
                    w.omegaLp += (omegaAbs - w.omegaLp) * Mathf.Clamp01(dt / 0.1f);
                    float k = Mathf.Min(1f, (w.omegaLp / 250f) * (w.omegaLp / 250f));
                    float rb = w.baseRadius * (1f + w.cfg.balloonPct * 0.01f * k);
                    if (Mathf.Abs(rb - w.col.radius) > 0.0005f)
                    {
                        w.col.radius = rb;
                        if (w.viz != null)
                        {
                            float sc = rb / w.baseRadius;
                            var ls = w.viz.localScale;
                            w.viz.localScale = new Vector3(ls.x, sc, sc); // radial axes
                        }
                    }
                }

                if (TyreModel.Enabled)
                {
                    // ---- Brush tyre path: slip-based forces + own spin integrator ----
                    float r = w.col.radius;
                    float fz = grounded ? Mathf.Max(0f, hit.force) : 0f;

                    // Wheel basis: the collider frame (mount yaw + strut tilt) plus
                    // the live steer angle, projected onto the contact plane.
                    Quaternion wheelRot = w.col.transform.rotation *
                                          Quaternion.Euler(0f, w.currentSteer, 0f);
                    Vector3 n = grounded ? hit.normal : transform.up;
                    Vector3 fwdW = wheelRot * Vector3.forward;
                    fwdW -= n * Vector3.Dot(fwdW, n);
                    if (fwdW.sqrMagnitude < 1e-6f) fwdW = transform.forward;
                    else fwdW.Normalize();
                    Vector3 rightW = Vector3.Cross(n, fwdW);

                    Vector3 vContact = _body.GetPointVelocity(
                        grounded ? hit.point : w.col.transform.position);
                    float vx = Vector3.Dot(vContact, fwdW);
                    float vy = Vector3.Dot(vContact, rightW);

                    w.slipRatio = TyreModel.SlipRatio(vx, w.omega, r);
                    // Readouts only — nothing downstream reads these back, they
                    // exist so TyreSlip01 can be aggregated after the loop.
                    // slipNorm is the SAME combined slip the tyre model itself
                    // uses, so 1 is exactly the peak of the force curve: below it
                    // the tyre is gripping, above it it is sliding. Recomputing a
                    // separate proxy here would just be a second opinion that
                    // disagrees with the physics.
                    {
                        float den = Mathf.Max(Mathf.Abs(vx), TyreModel.VLow);
                        float sx = ((w.omega * r - vx) / den) / TyreModel.KappaPeak;
                        // Same load-dependent peak the force path uses — a readout
                        // normalised by a different number would be a second
                        // opinion that disagrees with the physics.
                        float sy = ((-vy) / den)
                                 / TyreModel.AlphaPeakAt(fz, w.cfg.ratedLoadN);
                        w.slipNorm = Mathf.Sqrt(sx * sx + sy * sy);
                        w.patchSpeed = Mathf.Sqrt(vx * vx + vy * vy);
                    }
                    w.grounded = grounded;

                    // Traction control cuts drive on wheelspin; ABS releases the
                    // foot brake toward lockup (deliberate handbrake locks exempt).
                    if (grounded)
                    {
                        float tcOn = AssistTuning.TractionOnsetFor(a.traction);
                        float absOn = AssistTuning.AbsOnsetFor(a.abs);
                        if (a.traction > 0f && w.cfg.powered && w.slipRatio > tcOn)
                            w.driveTorque *= 1f - a.traction *
                                Mathf.Clamp01((w.slipRatio - tcOn) / AssistTuning.TractionBand);
                        if (a.abs > 0f && b > 0f && w.slipRatio < -absOn &&
                            !(_handbrake && !w.cfg.allowsSteering))
                            b *= 1f - a.abs * Mathf.Clamp01((-w.slipRatio - absOn) / AssistTuning.AbsBand);
                    }

                    // Hoisted above the tyre-force call: the SAME holding torque
                    // feeds both the stiction test inside TyreModel.Forces and
                    // the MoveTowards below — one source of truth, so the force
                    // clamp and the spin integrator can never disagree about
                    // whether the wheel is held.
                    float resist = b + (grounded ? surf.rollingResist * rollScale : 0f);

                    float fx = 0f, fy = 0f;
                    if (grounded && fz > 0f)
                    {
                        float mu = surf.frictionMult * GripMult(w.cfg) * loadFactor * arcadeGripMult;
                        // _gripStiffness is the Tune "Grip (side)" knob; its legacy
                        // neutral value 2.0 maps to a lateral µ scale of 1.
                        TyreModel.Forces(vx, vy, w.omega, r, fz, mu,
                            _gripStiffness * 0.5f, dt,
                            _wheels.Count / Mathf.Max(0.05f, _body.mass),
                            r * r / Mathf.Max(1e-9f, w.spinInertia),
                            w.cfg.ratedLoadN, w.driveTorque, resist,
                            out fx, out fy);

                        // Where the lateral force reaches the SPRUNG mass. It does
                        // not arrive at the contact patch: it travels up the
                        // linkage, which reacts it at the roll centre. Applying it
                        // at ground level (which this engine did) gives the roll
                        // moment the full CoM height as its arm instead of the
                        // height above the roll axis, and overstates body roll —
                        // measured at ~15 % on the Tiguan, whose derived roll
                        // centres are 87 mm front / 109 mm rear.
                        //
                        // The longitudinal force still acts at the patch: its
                        // analogue is the anti-dive/anti-squat pitch centre, which
                        // needs side-view geometry this does not read yet.
                        //
                        // rollCentreH == 0 (no linkage authored) MUST take the
                        // original single-call path. Two AddForceAtPosition calls
                        // summing to the same vector are not bit-identical to one
                        // in IEEE-754, and the RC designs' bit-identity should not
                        // have to appeal to that. Same reasoning as brakeScale's
                        // branch above.
                        if (w.rollCentreH == 0f)
                        {
                            _body.AddForceAtPosition(fwdW * fx + rightW * fy,
                                hit.point, ForceMode.Force);
                        }
                        else
                        {
                            _body.AddForceAtPosition(fwdW * fx, hit.point, ForceMode.Force);
                            _body.AddForceAtPosition(rightW * fy,
                                hit.point + transform.up * w.rollCentreH, ForceMode.Force);
                        }
                    }
                    w.lastFy = fy;   // next step's servo load

                    // Spin: J·ω̇ = τ_drive − Fx·r, then brake + rolling resistance
                    // as a decel toward zero. MoveTowards can never flip ω's sign
                    // in one step — that IS the static-hold clamp; a held wheel
                    // under way then skids (κ → −1) until ABS or release.
                    float J = Mathf.Max(1e-9f, w.spinInertia);
                    w.omega += (w.driveTorque - fx * r) / J * dt;
                    if (resist > 0f)
                        w.omega = Mathf.MoveTowards(w.omega, 0f, resist / J * dt);
                    w.spinAngle = Mathf.Repeat(w.spinAngle + w.omega * dt, Mathf.PI * 2f);

                    // Sticky-tire management (see stickyHold above): phantom
                    // torque spins the epsilon-friction PhysX wheel so the
                    // low-speed constraint releases; at true rest brake it so
                    // the constraint returns and parks the car.
                    // A phantom torque whose only job is to be big enough to
                    // release PhysX's static-hold constraint. What "big enough"
                    // means scales with the wheel's own inertia: 0.05 N·m is
                    // 1837 rad/s² on an RC wheel and 0.033 rad/s² on a real one,
                    // where the constraint never lets go and the car cannot move
                    // off at all. The wheel produces no force either way, so the
                    // magnitude is free.
                    w.col.motorTorque = stickyHold ? 0f : stickyPhantomNm;
                    w.col.brakeTorque = stickyHold ? handbrakeTorque : 0f;
                }
                else
                {
                    // ---- Legacy PhysX-friction path (TyreModel.Enabled = false) ----
                    // arcadeGripMult rides the change-gated product, so a constant
                    // 1 writes no friction curve and an arcade hit writes exactly once.
                    float combined = surf.frictionMult * loadFactor * arcadeGripMult;
                    if (Mathf.Abs(combined - w.lastMult) > 1e-4f)
                    {
                        float grip = GripMult(w.cfg);
                        var ff = w.col.forwardFriction;
                        ff.stiffness = FwdStiffness * combined * grip;
                        w.col.forwardFriction = ff;
                        var sf = w.col.sidewaysFriction;
                        sf.stiffness = _gripStiffness * combined * grip;
                        w.col.sidewaysFriction = sf;
                        w.lastMult = combined;
                    }

                    if (grounded)
                    {
                        float tcOn = AssistTuning.TractionOnsetFor(a.traction);
                        float absOn = AssistTuning.AbsOnsetFor(a.abs);
                        if (a.traction > 0f && w.cfg.powered && hit.forwardSlip > tcOn)
                            w.col.motorTorque *= 1f - a.traction *
                                Mathf.Clamp01((hit.forwardSlip - tcOn) / AssistTuning.TractionBand);
                        if (a.abs > 0f && b > 0f && hit.forwardSlip < -absOn &&
                            !(_handbrake && !w.cfg.allowsSteering))
                            b *= 1f - a.abs * Mathf.Clamp01((-hit.forwardSlip - absOn) / AssistTuning.AbsBand);
                    }

                    w.col.brakeTorque = b;
                }
            }

            // Pad pin latch — see the constants above. Off a pad this resets and
            // nothing else here runs, which is what keeps it invisible to any
            // surface that does not boost.
            if (padBoost > 0f)
            {
                float fwd = ForwardSpeed;
                // Signed, not absolute: reversing off a pad must count as pinned
                // too, or backing away would re-arm the pad and shove you in again.
                if (fwd < PadPinSpeed) _padStallTime += dt;
                else _padStallTime = 0f;

                if (_padStallTime >= PadPinSeconds) _padPinned = true;
                if (fwd > PadFreeSpeed) { _padPinned = false; _padStallTime = 0f; }
                if (_padPinned) padBoost = 0f;
            }
            else
            {
                _padStallTime = 0f;
                _padPinned = false;
            }
            if (padBoost > boost) boost = padBoost;

            // Boost pad: push the car forward while any wheel is on one (never during the hold).
            if (!Frozen && boost > 0f)
                _body.AddForce(transform.forward * (_body.mass * boost), ForceMode.Force);

            ApplyAerodynamics();

            // Arcade downforce (see the channel's doc). Applied at the CoM so it
            // adds tyre load without a pitch moment; branch-skipped at 0 so
            // non-arcade sessions are bit-identical.
            if (arcadeDownforce > 0f)
            {
                float fwd2 = ForwardSpeed * ForwardSpeed;
                _body.AddForce(-transform.up * (arcadeDownforce * fwd2), ForceMode.Force);
            }

            // Stability (ESC): damp the yaw-rate error against what the current
            // speed + steering angle intend (bicycle-model yaw intent).
            if (a.stability > 0f && _wheelbaseEst > 0.01f)
            {
                float yawIntent = ForwardSpeed / _wheelbaseEst *
                                  Mathf.Tan(CurrentSteerAngle * Mathf.Deg2Rad);
                float yawErr = _body.angularVelocity.y - yawIntent;
                // arcadeStabilityMult is 1 outside arcade, so gain and cap are
                // the same numbers they have always been.
                float cap = AssistTuning.StabilityClamp(a.stability) * arcadeStabilityMult;
                float tq = Mathf.Clamp(
                    -yawErr * AssistTuning.StabilityGain * a.stability * arcadeStabilityMult,
                    -cap, cap);
                _body.AddTorque(0f, tq, 0f, ForceMode.Force);
            }

            // Arcade spin-out. Deliberately not routed through Frozen, which
            // forces full brakes and is owned by the race countdown — a torque
            // plus a reduced arcadeGripMult reads as a slide, not a freeze.
            if (arcadeYawTorque != 0f)
                _body.AddTorque(0f, arcadeYawTorque, 0f, ForceMode.Force);

            foreach (var pair in _antiRollPairs) ApplyAntiRoll(pair.a.col, pair.b.col);

            foreach (var w in _wheels) UpdateVisual(w);

            UpdateLaunchControl(a, dt);
            UpdateSlipReadout();
        }

        // Launch control's voltage scale, 1 = hands off. Computed at the END of
        // a physics step from that step's fresh slip ratios and applied to the
        // motor command at the START of the next — a one-step-latency governor,
        // which at 400 Hz is as good as instantaneous and keeps the whole thing
        // out of the wheel loop.
        private float _launchScale = 1f;

        /// <summary>
        /// Standing-start wheelspin governor (the "launch control" assist).
        ///
        /// Distinct from traction control on three axes: TC is per-wheel,
        /// torque-side and stateless; this is global, voltage-side and
        /// integrating. It holds the WORST powered wheel's slip at
        /// <see cref="AssistTuning.LaunchSlipTarget"/> — just past the force
        /// peak — so a floored launch leaves at maximum force instead of
        /// lighting the tyres, and TC only has per-wheel transients left to
        /// trim. Armed below <see cref="AssistTuning.LaunchEngageSpeed"/>;
        /// above <see cref="AssistTuning.LaunchReleaseSpeed"/> the scale ramps
        /// back to 1 quickly and the driver has the whole motor again.
        ///
        /// At <c>launch == 0</c> the scale is pinned at 1 and the voltage
        /// multiply is exact, so firmware runs (assists zeroed) and Sim
        /// sessions with the slider down are bit-identical. Slip ratios come
        /// from the brush-path integrator; on the legacy PhysX path they stay
        /// 0 and the governor simply never engages.
        /// </summary>
        private void UpdateLaunchControl(in AssistSettings a, float dt)
        {
            if (a.launch <= 0f) { _launchScale = 1f; return; }

            float speed = Mathf.Abs(ForwardSpeed);
            float worst = 0f;
            foreach (var w in _wheels)
                if (w.cfg.powered && w.grounded && w.slipRatio > worst)
                    worst = w.slipRatio;

            if (speed < AssistTuning.LaunchEngageSpeed &&
                worst > AssistTuning.LaunchSlipTarget)
            {
                _launchScale -= a.launch * AssistTuning.LaunchGain *
                                (worst - AssistTuning.LaunchSlipTarget) * dt;
            }
            else
            {
                float rate = AssistTuning.LaunchReleaseRate;
                if (speed > AssistTuning.LaunchReleaseSpeed) rate *= 4f;
                _launchScale += rate * dt;
            }
            _launchScale = Mathf.Clamp(_launchScale, AssistTuning.LaunchFloor, 1f);
        }

        /// <summary>
        /// How far past the grip limit the tyres are, 0..1, worst wheel wins.
        ///
        /// Deliberately NOT "how much slip is there" — every tyre carrying load
        /// slips a little, and a readout that rises with ordinary cornering makes
        /// a car that squeals through every corner it takes cleanly. Zero until
        /// the combined slip passes the peak of the force curve (where the tyre
        /// genuinely lets go), full in a deep slide.
        ///
        /// A pure readout for the audio layer: it is assigned at the end of a
        /// physics step and never read back by any physics expression, so it
        /// cannot influence the simulation. Airborne wheels are excluded — a
        /// free-spinning wheel has enormous slip and would otherwise screech in
        /// mid-air — and so are wheels that are barely moving, because a stopped
        /// tyre cannot scrub whatever the ratio says.
        ///
        /// Zero on the legacy PhysX-friction path, which computes no slip of its
        /// own; that path is a dev A/B switch, and losing tyre noise under it is
        /// the right trade against duplicating the calculation.
        /// </summary>
        public float TyreSlip01 { get; private set; }

        /// <summary>Slip past the peak before anything is audible, and the span
        /// over which it reaches full. 1.0 is the peak itself; a little margin
        /// keeps a car balanced right on the limit from buzzing.</summary>
        private const float SlideOnset = 1.15f;
        private const float SlideSpan = 1.35f;
        /// <summary>Contact-patch speed below which a wheel is treated as still.</summary>
        private const float SlideMinPatchSpeed = 0.7f;

        private void UpdateSlipReadout()
        {
            float worst = 0f;
            foreach (var w in _wheels)
            {
                if (!w.grounded || w.patchSpeed < SlideMinPatchSpeed) continue;
                worst = Mathf.Max(worst, (w.slipNorm - SlideOnset) / SlideSpan);
            }
            TyreSlip01 = Mathf.Clamp01(worst);
        }

        /// <summary>
        /// The three body-aero numbers this car will actually fly with: the
        /// design's overrides resolved against the built-in tables.
        ///
        /// Cd and reference area are authored when the real car has published
        /// ones. The built-in <see cref="AeroDynamics.BodyCd"/> table and the
        /// width*height*0.9 estimate are reasonable guesses for a stylised
        /// arcade shell and no substitute for a measured figure — and a
        /// coastdown against a published Cd*A is the single strongest check
        /// available on this whole aero model, so the numbers have to be the
        /// real ones.
        ///
        /// <b>Public because the design dump reads it.</b> Those two branches
        /// are the entire difference between a design that authors its drag and
        /// one that inherits it from its silhouette, which makes them exactly
        /// what a shape→key migration could break — and a dumper carrying its
        /// own copy of the branch would go on agreeing with itself after the
        /// call site stopped agreeing with either. <paramref name="clA"/> is the
        /// bare table value: <c>aeroMult</c> scales it at the force, not here.
        /// </summary>
        public void EffectiveAero(out float cd, out float frontalArea, out float clA)
        {
            BodyDef def = Body;
            cd = dragCdOverride > 0f ? dragCdOverride : AeroDynamics.BodyCd(def);
            frontalArea = frontalAreaOverride > 0f
                ? frontalAreaOverride : AeroDynamics.FrontalArea(bodySize);
            clA = AeroDynamics.BodyClA(def);
        }

        /// <summary>
        /// Quadratic aero: body drag applied at the geometric body centre (a few
        /// cm above the CoM, so hard braking from speed pitches the nose a touch),
        /// body lift/downforce at the CoM, and each aero part's downforce + drag
        /// at the part's own position — a rear wing loads the rear axle.
        /// </summary>
        private void ApplyAerodynamics()
        {
            Vector3 v = _body.linearVelocity;
            float sp2 = v.sqrMagnitude;
            if (sp2 < 0.01f) return;

            Vector3 vDir = v / Mathf.Sqrt(sp2);
            float q = 0.5f * AeroDynamics.AirDensity * sp2;   // dynamic pressure

            EffectiveAero(out float cd, out float area, out float clA);
            float cdA = cd * area;
            _body.AddForceAtPosition(-vDir * (q * cdA), transform.position, ForceMode.Force);

            if (clA > 0f)
                _body.AddForce(-transform.up * (q * clA * aeroMult), ForceMode.Force);

            if (aeroParts == null) return;
            // Directional per-part model: forces come from the airflow direction
            // in body space, so part yaw, body pitch over jumps, slides, and
            // reversed mounting all matter — a backwards wing produces lift.
            Vector3 airflowLocal = transform.InverseTransformDirection(-v);
            for (int i = 0; i < aeroParts.Length; i++)
            {
                ref var ap = ref aeroParts[i];
                AeroDynamics.PartForces(ap.kind, ap.angleDeg, ap.yawDeg, ap.sizeScale,
                    airflowLocal, out Vector3 liftL, out Vector3 dragL);
                Vector3 f = transform.TransformDirection(liftL * aeroMult + dragL);
                _body.AddForceAtPosition(f, transform.TransformPoint(ap.localPos), ForceMode.Force);
            }
        }

        /// <summary>
        /// Roll-steer toe angle (deg) from this wheel's current suspension position.
        /// 0 whenever no toe link is authored, which is every design that predates
        /// <see cref="SuspensionLinkage"/>.
        ///
        /// Compression runs 1 = full droop → 0 = fully compressed (the same
        /// arithmetic <see cref="ApplyAntiRoll"/> and GetSuspensionCompression use),
        /// so bump — upward hub travel from static rest — is
        /// (rest − current) × travel, positive as the wheel compresses.
        /// </summary>
        private static float RollSteerDeg(Wheel w)
        {
            if (w.toeSteerPerM == 0f) return 0f;
            if (!w.col.GetGroundHit(out WheelHit hit)) return 0f;
            float comp = Mathf.Clamp01(
                (-w.col.transform.InverseTransformPoint(hit.point).y - w.col.radius)
                / w.col.suspensionDistance);
            return w.toeSteerPerM * ((w.restCompression - comp) * w.col.suspensionDistance);
        }

        private void ApplyAntiRoll(WheelCollider left, WheelCollider right)
        {
            float travelL = 1f, travelR = 1f;
            bool groundedL = left.GetGroundHit(out WheelHit hitL);
            if (groundedL)
                travelL = (-left.transform.InverseTransformPoint(hitL.point).y - left.radius) / left.suspensionDistance;
            bool groundedR = right.GetGroundHit(out WheelHit hitR);
            if (groundedR)
                travelR = (-right.transform.InverseTransformPoint(hitR.point).y - right.radius) / right.suspensionDistance;

            float force = (travelL - travelR) * antiRoll;
            if (groundedL) _body.AddForceAtPosition(left.transform.up * -force, left.transform.position);
            if (groundedR) _body.AddForceAtPosition(right.transform.up * force, right.transform.position);
        }

        private void UpdateVisual(Wheel w)
        {
            if (w.viz == null) return;
            // Position always comes from the suspension pose. Rotation: legacy
            // path uses the WheelCollider's own spin; brush path composes it from
            // the collider frame + live steer + the integrated spin angle (the
            // collider receives no torques any more, so its spin is meaningless).
            w.col.GetWorldPose(out Vector3 pos, out Quaternion rot);
            if (TyreModel.Enabled)
                rot = w.col.transform.rotation
                    * Quaternion.Euler(0f, w.currentSteer, 0f)
                    * Quaternion.Euler(w.spinAngle * Mathf.Rad2Deg, 0f, 0f);
            w.viz.SetPositionAndRotation(pos, rot);
        }

        public void SampleSensors(float dt, float[] wheelVel, float[] gyro, float[] accel)
        {
            // The ABI carries the first four wheels' angular velocity, optionally
            // corrupted like a real encoder path (CPR resolution + noise). Both
            // knobs 0 (legacy) keep the channel perfectly clean.
            for (int i = 0; i < 4; i++)
            {
                float v = WheelOmega(i);
                if (wheelVelQuantCpr > 0 && dt > 1e-6f)
                {
                    float tick = Mathf.PI * 2f / wheelVelQuantCpr;   // rad per count
                    v = Mathf.Round(v * dt / tick) * tick / dt;      // counts/period → rad/s
                }
                if (wheelVelNoiseStd > 0f)
                {
                    if (_wheelVelNoise == null)
                    {
                        _wheelVelNoise = new NoiseModel[4];
                        for (int k = 0; k < 4; k++)
                            _wheelVelNoise[k] = new NoiseModel { noiseStdDev = wheelVelNoiseStd };
                    }
                    v = _wheelVelNoise[i].Apply(v, dt);
                }
                wheelVel[i] = v;
            }

            // Motor-driven IMU vibration: per-motor phase-integrated tones (1× +
            // a 2.07× harmonic) whose frequency tracks each rotor's speed —
            // deterministic (no RNG), like a real unbalanced can. The gyro sees
            // ~10 % of the accel amplitude as rate ripple.
            Vector3 vibA = Vector3.zero, vibG = Vector3.zero;
            if (imuVibration > 0f && _motors.Count > 0)
            {
                float omegaSum = 0f;
                for (int k = 0; k < _motors.Count; k++)
                    if (_motors[k] != null) omegaSum += Mathf.Abs(_motors[k].MotorOmega);
                float amp = imuVibration * omegaSum / 2500f;   // m/s² scale
                for (int k = 0; k < _motors.Count && k < _vibPhase.Length; k++)
                {
                    var m = _motors[k];
                    if (m == null) continue;
                    _vibPhase[k] += m.MotorOmega * dt;
                    float s = Mathf.Sin(_vibPhase[k]) + 0.5f * Mathf.Sin(2.07f * _vibPhase[k]);
                    vibA += new Vector3(0.5f, 1.0f, 0.7f) * (amp * s);
                }
                vibG = vibA * 0.1f;
            }

            imu.Read(_body, dt, vibG, vibA, out Vector3 g, out Vector3 a);
            gyro[0] = g.x; gyro[1] = g.y; gyro[2] = g.z;
            accel[0] = a.x; accel[1] = a.y; accel[2] = a.z;
        }

        public void PublishTelemetry(TelemetryHub hub)
        {
            hub.SetValue("veh/speed", ForwardSpeed);
            hub.SetValue("veh/speed_kmh", ForwardSpeed * 3.6f);
            hub.SetValue("veh/steer_deg", CurrentSteerAngle);
            hub.SetValue("veh/yaw_rate", _body.angularVelocity.y);

            // Ground-truth pose. These are the reference a dead-reckoning
            // controller is scored against: its own odometry can only prove the
            // loop closed, not that the distance was right. pos_x/pos_z were
            // registered by SimulationRunner from the start but only the
            // diff-drive vehicle ever wrote them, so for the car they read zero.
            var p = transform.position;
            hub.SetValue("veh/pos_x", p.x);
            hub.SetValue("veh/pos_z", p.z);
            hub.SetValue("veh/yaw_deg", transform.eulerAngles.y);

            // Commanded drivetrain state (measured motor V/I/τ come from the rig).
            hub.SetValue("cmd/steer_deg", CurrentSteerAngle);
            hub.SetValue("cmd/brake", Mathf.Clamp01(_cmd[BrakeActuator]));
            for (int i = 0; i < _motors.Count; i++)
            {
                var m = _motors[i];
                if (m == null) continue;
                int idx = m.ActuatorIndex;
                float volts = (idx >= 0 && idx < _cmd.Length) ? _cmd[idx] : 0f;
                hub.SetValue($"cmd/{m.sensorName}/volt", volts);
            }
        }

        private void SetGrip(float stiffness)
        {
            _gripStiffness = stiffness;
            // Brush path consumes it live each step as the lateral grip scale
            // (× 0.5, so the legacy-neutral 2.0 maps to 1) — no collider writes.
            if (TyreModel.Enabled) return;
            foreach (var w in _wheels)
            {
                if (w.col == null) continue;
                var f = w.col.sidewaysFriction;
                f.stiffness = stiffness * w.lastMult * GripMult(w.cfg); // keep surface + per-wheel scaling
                w.col.sidewaysFriction = f;
            }
        }

        public IReadOnlyList<TunableParam> GetTunables() => new List<TunableParam>
        {
            new TunableParam("Steer rate (deg/s)",  60f, 1200f, () => steerRateDegPerSec,  v => steerRateDegPerSec = v),
            new TunableParam("Brake torque (N·m)", 0.1f,    3f, () => maxBrakeTorque,      v => maxBrakeTorque = v),
            new TunableParam("Aero mult",            0f,    3f, () => aeroMult,            v => aeroMult = v),
            new TunableParam("Grip (side)",        0.5f,    3f, () => _gripStiffness,      SetGrip),
        };
    }
}
