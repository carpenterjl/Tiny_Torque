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
    public enum BodyShape { Box, Wedge, Buggy, Shell, LowRacer }

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
        // Speed (m/s) at which per-tile rolling resistance reaches full strength.
        // Below this it ramps to 0 so it opposes motion instead of parking the car.
        private const float RollResistRampSpeed = 0.3f;

        [Header("Aero / stability")]
        // Aerodynamics: quadratic body drag (per-shape Cd × frontal area) plus
        // per-part wing/splitter forces applied at each part's position. aeroMult
        // scales all DOWNFORCE terms (a tuning aid; drag stays physical).
        public float aeroMult = 1f;
        public AeroConfig[] aeroParts;         // set by the VehicleFactory
        public float antiRoll = 8f;            // ≈ one axle weight at full Δtravel
        public Vector3 centerOfMass = new Vector3(0f, -0.03f, 0f);

        [Header("Assists")]
        public AssistSettings assists;         // per-player prefs (rig build)
        public bool assistsActive = true;      // false in Autonomous mode

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
            public float currentSteer;   // current servo angle (deg)
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
            public float lastFy;         // last lateral tyre force (N) — servo load
        }

        /// <summary>Baseline forward friction stiffness (scaled by tile surfaces).</summary>
        private const float FwdStiffness = 1.6f;

        private Rigidbody _body;
        private readonly List<Wheel> _wheels = new List<Wheel>();
        private readonly List<(Wheel a, Wheel b)> _antiRollPairs = new List<(Wheel, Wheel)>();
        private readonly List<MeshRenderer> _bodyRenderers = new List<MeshRenderer>();
        private Material _bodyMat;
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

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _body.mass = bodyMass;
            _body.linearDamping = 0.02f;   // real drag is modeled explicitly
            _body.angularDamping = 0.5f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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
            _body.maxAngularVelocity = 40f;
            _body.maxDepenetrationVelocity = 2f;

            // The root GameObject stays at scale (1,1,1). Body size comes from a
            // BoxCollider + a separately-scaled visual child, NOT from scaling the
            // root — otherwise the non-uniform body scale would distort the wheels
            // (and WheelColliders) into ellipsoids.
            var box = GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            box.size = bodySize;
            box.center = Vector3.zero;

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

            _bodyMat = new Material(Shader.Find("Standard")) { color = bodyColor };

            // Authored-mesh path (Shell / LowRacer / Buggy have FBX shells): instantiate
            // the mesh, scale it to the design's bodySize, and drive its look with the
            // body material so bodyColor + SetBodyMaterial keep working. Falls through to
            // the primitive shape builders when the asset is absent (and always for
            // Box/Wedge). The body stays on the car's own layer (not the viz layer) so
            // the on-car camera sees it exactly as with the primitive body.
            string meshKey = BodyMeshKey(bodyShape);
            if (meshKey != null)
            {
                var inst = PartMeshLibrary.TryInstantiate(meshKey, holder, gameObject.layer);
                if (inst != null)
                {
                    inst.transform.localScale = new Vector3(
                        bodySize.x / BodyMeshAuthorSize.x,
                        bodySize.y / BodyMeshAuthorSize.y,
                        bodySize.z / BodyMeshAuthorSize.z);
                    // Painted livery (decoded by VehicleFactory): the texture
                    // carries the colours, so the material tint goes white.
                    if (liveryTex != null)
                    {
                        _bodyMat.mainTexture = liveryTex;
                        _bodyMat.color = Color.white;
                    }
                    foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        r.sharedMaterial = _bodyMat;
                        _bodyRenderers.Add(r);
                    }
                    return;
                }
            }

            switch (bodyShape)
            {
                case BodyShape.Wedge:    BuildWedgeBody(holder); break;
                case BodyShape.Buggy:    BuildBuggyBody(holder); break;
                case BodyShape.Shell:    BuildShellBody(holder); break;
                case BodyShape.LowRacer: BuildLowRacerBody(holder); break;
                default:                 BuildBoxBody(holder); break;
            }
        }

        /// <summary>Nominal dimensions the body FBX shells are authored at (m).</summary>
        private static readonly Vector3 BodyMeshAuthorSize = new Vector3(0.20f, 0.10f, 0.42f);

        /// <summary>FBX key for the shapes that have an authored shell, else null.</summary>
        public static string BodyMeshKey(BodyShape s) => s switch
        {
            BodyShape.Shell    => "body_shell",
            BodyShape.LowRacer => "body_lowracer",
            BodyShape.Buggy    => "body_buggy",
            _                  => null,
        };

        /// <summary>
        /// Whether this shape shows an authored UV-mapped shell — i.e. the garage
        /// paint mode can work on it (Box/Wedge compounds have no sane UVs).
        /// </summary>
        public static bool HasPaintableBody(BodyShape s)
        {
            string key = BodyMeshKey(s);
            return key != null && PartMeshLibrary.Has(key);
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
            _bodyMat.mainTexture = tex;
            _bodyMat.color = tex != null ? Color.white : bodyColor;
        }

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

        /// <summary>Recolour/replace the body material (used by the scene builder).</summary>
        public void SetBodyMaterial(Material mat)
        {
            if (mat == null) return;
            _bodyMat = mat;
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
            wc.mass = 0.05f + (!TyreModel.Enabled && cfg.extraSpinInertia > 0f
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
            spring.targetPosition = 0.5f;
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
            const float BrushEps = 0.01f; // parasitic PhysX force ≤ ~0.05 N/wheel
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
            PartVisualFactory.BuildWheelViz(holder, cfg.radius, cfg.powered, inboardSign, cfg.wheelStyle);

            // Visible strut spanning body mount → hub (only when a strut length is set).
            Transform strut = null;
            if (cfg.suspLength > 0f)
            {
                strut = new GameObject(name + "_Strut").transform;
                strut.SetParent(transform, false);
                PartVisualFactory.BuildStrutViz(strut);
            }

            return new Wheel
            {
                col = wc, viz = holder, cfg = cfg, baseRadius = wc.radius,
                strut = strut, mountLocal = cfg.localPos,
                // J for the brush-path spin integrator: bare wheel + reflected drivetrain.
                spinInertia = 0.5f * 0.05f * wc.radius * wc.radius
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
            _body.position = _spawnPos;
            _body.rotation = _spawnRot;
            transform.SetPositionAndRotation(_spawnPos, _spawnRot);
            Physics.SyncTransforms();
            _body.collisionDetectionMode = mode;

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
            }
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
            float boost = 0f;

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
                if (_body.linearVelocity.sqrMagnitude > 0.0004f) stickyHold = false;
                else
                    foreach (var w in _wheels)
                        if (Mathf.Abs(w.omega) > 0.5f || Mathf.Abs(w.driveTorque) > 0.005f)
                        {
                            stickyHold = false;
                            break;
                        }
            }

            // Steering assist: limit lock at speed + countersteer toward the
            // velocity direction when the body slips sideways.
            if (a.steer > 0f)
            {
                float sp = Mathf.Abs(ForwardSpeed);
                steerCmd *= Mathf.Lerp(1f, Mathf.Min(1f, 4f / Mathf.Max(0.5f, sp)), a.steer);
                Vector3 vLoc = transform.InverseTransformDirection(_body.linearVelocity);
                if (Mathf.Abs(vLoc.z) > 1f)
                {
                    float slipAngle = Mathf.Atan2(vLoc.x, Mathf.Abs(vLoc.z)); // + = sliding right
                    steerCmd = Mathf.Clamp(
                        steerCmd + a.steer * Mathf.Clamp(slipAngle * 0.5f, -0.35f, 0.35f),
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
                    w.currentSteer = Mathf.MoveTowards(w.currentSteer, target, steerRate * dt);
                }
                else w.currentSteer = 0f;
                w.col.steerAngle = w.currentSteer;

                // Foot brake everywhere; handbrake locks the non-steering wheels.
                float b = brake + ((_handbrake && !w.cfg.allowsSteering) ? handbrakeTorque : 0f);

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
                    if (surf.boostAccel > boost) boost = surf.boostAccel;
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

                    // Traction control cuts drive on wheelspin; ABS releases the
                    // foot brake toward lockup (deliberate handbrake locks exempt).
                    if (grounded)
                    {
                        if (a.traction > 0f && w.cfg.powered && w.slipRatio > 0.25f)
                            w.driveTorque *= 1f - a.traction *
                                Mathf.Clamp01((w.slipRatio - 0.25f) / 0.35f);
                        if (a.abs > 0f && b > 0f && w.slipRatio < -0.3f &&
                            !(_handbrake && !w.cfg.allowsSteering))
                            b *= 1f - a.abs * Mathf.Clamp01((-w.slipRatio - 0.3f) / 0.4f);
                    }

                    float fx = 0f, fy = 0f;
                    if (grounded && fz > 0f)
                    {
                        float mu = surf.frictionMult * GripMult(w.cfg) * loadFactor;
                        // _gripStiffness is the Tune "Grip (side)" knob; its legacy
                        // neutral value 2.0 maps to a lateral µ scale of 1.
                        TyreModel.Forces(vx, vy, w.omega, r, fz, mu,
                            _gripStiffness * 0.5f, dt,
                            _wheels.Count / Mathf.Max(0.05f, _body.mass),
                            r * r / Mathf.Max(1e-9f, w.spinInertia),
                            out fx, out fy);
                        _body.AddForceAtPosition(fwdW * fx + rightW * fy,
                            hit.point, ForceMode.Force);
                    }
                    w.lastFy = fy;   // next step's servo load

                    // Spin: J·ω̇ = τ_drive − Fx·r, then brake + rolling resistance
                    // as a decel toward zero. MoveTowards can never flip ω's sign
                    // in one step — that IS the static-hold clamp; a held wheel
                    // under way then skids (κ → −1) until ABS or release.
                    float J = Mathf.Max(1e-9f, w.spinInertia);
                    w.omega += (w.driveTorque - fx * r) / J * dt;
                    float resist = b + (grounded ? surf.rollingResist * rollScale : 0f);
                    if (resist > 0f)
                        w.omega = Mathf.MoveTowards(w.omega, 0f, resist / J * dt);
                    w.spinAngle = Mathf.Repeat(w.spinAngle + w.omega * dt, Mathf.PI * 2f);

                    // Sticky-tire management (see stickyHold above): phantom
                    // torque spins the epsilon-friction PhysX wheel so the
                    // low-speed constraint releases; at true rest brake it so
                    // the constraint returns and parks the car.
                    w.col.motorTorque = stickyHold ? 0f : 0.05f;
                    w.col.brakeTorque = stickyHold ? handbrakeTorque : 0f;
                }
                else
                {
                    // ---- Legacy PhysX-friction path (TyreModel.Enabled = false) ----
                    float combined = surf.frictionMult * loadFactor;
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
                        if (a.traction > 0f && w.cfg.powered && hit.forwardSlip > 0.25f)
                            w.col.motorTorque *= 1f - a.traction *
                                Mathf.Clamp01((hit.forwardSlip - 0.25f) / 0.35f);
                        if (a.abs > 0f && b > 0f && hit.forwardSlip < -0.3f &&
                            !(_handbrake && !w.cfg.allowsSteering))
                            b *= 1f - a.abs * Mathf.Clamp01((-hit.forwardSlip - 0.3f) / 0.4f);
                    }

                    w.col.brakeTorque = b;
                }
            }

            // Boost pad: push the car forward while any wheel is on one (never during the hold).
            if (!Frozen && boost > 0f)
                _body.AddForce(transform.forward * (_body.mass * boost), ForceMode.Force);

            ApplyAerodynamics();

            // Stability (ESC): damp the yaw-rate error against what the current
            // speed + steering angle intend (bicycle-model yaw intent).
            if (a.stability > 0f && _wheelbaseEst > 0.01f)
            {
                float yawIntent = ForwardSpeed / _wheelbaseEst *
                                  Mathf.Tan(CurrentSteerAngle * Mathf.Deg2Rad);
                float yawErr = _body.angularVelocity.y - yawIntent;
                float tq = Mathf.Clamp(-yawErr * 0.08f * a.stability, -0.3f, 0.3f);
                _body.AddTorque(0f, tq, 0f, ForceMode.Force);
            }

            foreach (var pair in _antiRollPairs) ApplyAntiRoll(pair.a.col, pair.b.col);

            foreach (var w in _wheels) UpdateVisual(w);
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

            float cdA = AeroDynamics.BodyCd(bodyShape) * AeroDynamics.FrontalArea(bodySize);
            _body.AddForceAtPosition(-vDir * (q * cdA), transform.position, ForceMode.Force);

            float clA = AeroDynamics.BodyClA(bodyShape);
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
