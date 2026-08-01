using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// Everything in a physics test that is specifically about a CAR: the world it
    /// measures against, the Tiguan itself, the scripted driver, and the handful of
    /// helpers that only mean anything when the vehicle has wheels.
    ///
    /// <b>Why this layer exists.</b> <see cref="PhysicsTest"/> owns the parts that
    /// are about MEASURING — the settle/arm/sync/run machine, the verdict, the
    /// result JSON, the HUD, the headless plumbing — and none of that has an
    /// opinion about wheels. Pushing the car-shaped members down here lets an
    /// aircraft reuse the measurement machinery without inheriting a
    /// <c>WheelsSynced</c> it can never satisfy, and it does it without touching a
    /// single number in the ten results that already exist. It is the same layering
    /// <see cref="SteadyCorneringTest"/> already uses one level further down.
    ///
    /// <b>Nothing in here changed on the way in.</b> Every method below is the
    /// original body, moved. The extraction adds virtual dispatch and moves class
    /// membership around; it reorders no arithmetic and alters no literal. A
    /// virtual call cannot change a float — which is why the ten car results are
    /// expected to come back byte-identical rather than merely close.
    /// </summary>
    public abstract class CarPhysicsTest : PhysicsTest
    {
        // ---- what a car test declares ----

        /// <summary>The world this test needs. Default is the plain straight.</summary>
        protected virtual PhysicsTestEnvironment.EnvSpec Environment =>
            PhysicsTestEnvironment.EnvSpec.Default();

        /// <summary>Where the car starts. Flat ground at the measured rest height
        /// by default; <see cref="OnSurfaceAt"/> puts it on the slope instead.</summary>
        protected virtual (Vector3 pos, Quaternion rot) SpawnPose() =>
            (new Vector3(0f, DebugVehicles.TiguanChassisRestY, 0f), Quaternion.identity);

        /// <summary>What the driver holds before the run begins. Neutral for
        /// almost everything — but a test that parks on a slope has to hold the
        /// handbrake through its own settle, or it slides away before it arms.</summary>
        protected virtual void Idle(ScriptedDriver d) => d.Neutral();

        /// <summary>Write this tick's inputs. <paramref name="t"/> is seconds
        /// since the run phase began.</summary>
        protected abstract void Drive(ScriptedDriver d, float t);

        // ---- state ----

        protected CarVehicle Car { get; private set; }
        protected ScriptedDriver Driver { get; private set; }

        private CarInput _input;
        private float _syncTargetSpeed = -1f;
        private bool _syncNeedsSpeed;
        private float[] _motorResistance;

        // ---- the hooks the base calls ----

        /// <summary>
        /// World, car, camera, runner — in that order, and the order is
        /// load-bearing. <see cref="DebugVehicleRig"/>'s class comment explains
        /// why: <c>CsvLogger</c> snapshots its column list once, so the telemetry
        /// component must exist before the runner's Start enables logging.
        /// </summary>
        protected override void BuildSubject()
        {
            var (cam, graph) = PhysicsTestEnvironment.Build(Environment);

            // Colliders were created moments ago in Build(); queries read the
            // physics scene, which has not been told about them yet.
            Physics.SyncTransforms();
            var (spawn, spawnRot) = SpawnPose();
            var rig = DebugVehicleRig.BuildCar(DebugVehicles.VwTiguan(), spawn, spawnRot);
            Car = rig.car;
            Body = Car.GetComponent<Rigidbody>();
            _input = rig.input;

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = Car.transform;

            DebugVehicleRig.AttachRunner(ref rig, graph, physicsRateHz, controlRateHz, logCsv);
            Runner = rig.runner;
        }

        protected override void InstallScriptedInput()
        {
            Driver = new ScriptedDriver();
            _input.source = Driver;
        }

        protected override void HandControlsToHuman() =>
            _input.source = new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad);

        protected override void IdleInputs() => Idle(Driver);

        protected override void DriveInputs(float t) => Drive(Driver, t);

        /// <summary>
        /// Wheels rolling true, and — only when the caller launched deliberately
        /// fast — back down to the speed the window is defined at. Waiting for a
        /// speed that was never overshot would hang forever in exactly the tests
        /// that coast without losing any (P0 runs with drag disabled on purpose).
        /// </summary>
        protected override bool SyncReady(out string why)
        {
            why = "wheels never synced after launch — " + WheelStateText();
            return WheelsSynced() && (!_syncNeedsSpeed || Speed <= _syncTargetSpeed);
        }

        /// <summary>Graph panes for this test. Default shows speed and the three
        /// body-frame accelerations.</summary>
        protected override void ConfigureGraph(GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("accel (m/s²)", "veh/a_long", "veh/a_lat", "veh/a_vert");
        }

        // ---- launching at speed ----

        /// <summary>
        /// Start the run already moving, without driving up to speed.
        ///
        /// The powertrain on this vehicle is declared fiction — no gearbox, no
        /// torque curve — so a test that accelerates first is measuring the
        /// fiction. Setting the body's velocity alone is not enough either: the
        /// wheels would still be stationary, the tyres would see slip ratio −1,
        /// and the run would open with a locked-wheel skid.
        ///
        /// So set the body's velocity and let the tyres spin the wheels up
        /// naturally, then start measuring. Against J ≈ 1.8 kg·m² the tyre torque
        /// is ~700 rad/s², so they sync in a fraction of a second.
        ///
        /// <paramref name="overshootMps"/> is for tests whose window is defined
        /// at an exact speed (P1 measures 32 → 22): launch above it and the run
        /// begins when the car falls back through the target with the wheels
        /// already true. Leave it at zero when the test simply needs to be
        /// moving — waiting for a speed that was never overshot never arrives in
        /// a test that coasts without losing speed, which is precisely what P0
        /// is built to do.
        /// </summary>
        protected void LaunchAt(float targetMps, float overshootMps = 0f)
        {
            _syncTargetSpeed = targetMps;
            _syncNeedsSpeed = overshootMps > 0f;
            // The base enters the sync phase on this flag. It reproduces the old
            // condition exactly: sync was entered when _syncTargetSpeed > 0.
            WantsSync = targetMps > 0f;
            Body.linearVelocity = Car.transform.forward * (targetMps + overshootMps);
            Body.angularVelocity = Vector3.zero;
        }

        /// <summary>Per-wheel slip / ω / contact, for a diagnostic that says what
        /// went wrong rather than only that something did.</summary>
        protected string WheelStateText()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"v {Speed:0.000} ");
            for (int i = 0; i < Car.WheelCount; i++)
                sb.Append($"[{i} slip {Car.WheelSlipRatio(i):0.0000} "
                          + $"ω {Car.WheelOmega(i):0.00} "
                          + $"{(Car.WheelGrounded(i) ? "gnd" : "AIR")}] ");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Disconnect the driveline — the model's equivalent of coasting in
        /// neutral. <b>Any coasting test needs this, and the reason is not
        /// obvious.</b>
        ///
        /// At zero throttle the ESC state machine falls through to
        /// <c>MotorModel.WheelTorque(0 V)</c>, and a DC motor at zero volts is
        /// not disconnected — it is a SHORT. Back-EMF drives
        /// <c>I = −Kt·ω/R</c> through the winding and the motor becomes a brake.
        /// For an RC car that is right: a brushed ESC at neutral really does
        /// brake. For a 1500 kg car it is wrong, because a real one has a clutch
        /// or a torque converter and coasts freely.
        ///
        /// Measured here, it is worth about <b>1.7 m/s² at 10 m/s</b> — six times
        /// the whole aerodynamic drag at that speed. A coastdown run with the
        /// driveline live measures the motor's winding resistance and reports it
        /// as Cd·A.
        ///
        /// The disconnect is physical rather than a flag: raising the winding
        /// resistance is an open circuit, so the current, and with it the torque,
        /// goes to zero through the model's own equation instead of around it.
        /// </summary>
        protected void SetFreewheel(bool on)
        {
            var motors = Car.Motors;
            if (_motorResistance == null)
            {
                _motorResistance = new float[motors.Count];
                for (int i = 0; i < motors.Count; i++)
                    _motorResistance[i] = motors[i] != null ? motors[i].motor.resistance : 0f;
            }
            for (int i = 0; i < motors.Count; i++)
            {
                var m = motors[i];
                if (m == null) continue;
                var p = m.motor;
                p.resistance = on ? 1e6f : _motorResistance[i];
                m.motor = p;
            }
        }

        /// <summary>
        /// A spawn pose sitting on whatever surface is under (x, z), aligned to
        /// it. Found by raycast rather than by arithmetic: the slope's height at
        /// a given z is a function of its rotation, position and scale, and three
        /// chances to get a sign wrong is three too many for a placement whose
        /// failure mode is a car quietly starting 10 cm inside the ground.
        /// </summary>
        protected static (Vector3 pos, Quaternion rot) OnSurfaceAt(
            float x, float z, Vector3 facing)
        {
            var origin = new Vector3(x, 500f, z);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f,
                                 ~0, QueryTriggerInteraction.Ignore))
                return (new Vector3(x, DebugVehicles.TiguanChassisRestY, z),
                        Quaternion.identity);

            // Rest height is measured perpendicular to the ground, so it goes
            // along the surface normal, not along world up.
            var pos = hit.point + hit.normal * DebugVehicles.TiguanChassisRestY;
            var fwd = Vector3.ProjectOnPlane(facing, hit.normal).normalized;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            return (pos, Quaternion.LookRotation(fwd, hit.normal));
        }

        /// <summary>
        /// Put the car at a speed and start measuring immediately, with no wait
        /// for the wheels to spin up.
        ///
        /// For a test that drives — rather than coasts — the launch transient is
        /// harmless and the wait is actively wrong: a driven wheel carries real
        /// motor torque, so it holds a steady slip of a couple of percent
        /// forever, and a sync gate gets stuck waiting for a zero that a working
        /// car never reaches.
        /// </summary>
        protected void SetSpeedNow(float mps)
        {
            Body.linearVelocity = Car.transform.forward * mps;
            Body.angularVelocity = Vector3.zero;
        }

        /// <summary>Every grounded wheel rolling within 1 % of the road speed.</summary>
        private bool WheelsSynced()
        {
            for (int i = 0; i < Car.WheelCount; i++)
            {
                if (!Car.WheelGrounded(i)) return false;
                if (Mathf.Abs(Car.WheelSlipRatio(i)) > 0.01f) return false;
            }
            return true;
        }
    }
}
