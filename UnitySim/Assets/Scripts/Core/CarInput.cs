using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Human input for the car. In Manual Mode it fills the actuator buffer
    /// directly (throttle/steer/brake) via <see cref="IManualDriver"/>; in
    /// Autonomous Mode it exposes high-level setpoints (target speed, steer) via
    /// <see cref="ISetpointSource"/>. Handbrake and respawn are applied directly
    /// to the car here since they aren't part of the actuator vector.
    /// </summary>
    public sealed class CarInput : MonoBehaviour, IManualDriver, ISetpointSource
    {
        [Tooltip("Speed (m/s) commanded at full throttle when in Autonomous mode.")]
        public float maxSpeed = 12f;

        [Header("Optional mouse steering")]
        public bool enableMouseSteer = false;
        public float mouseSteerSensitivity = 0.01f;
        public float mouseSteerReturn = 3f;

        public CarVehicle car;
        public LapTimer lapTimer;

        /// <summary>This player's chase camera, for the look-back key. Optional:
        /// null in split-screen viewports that use their own rig, in LAN ghosts,
        /// and on every bot — all of which is why it is a null check rather than
        /// a requirement.</summary>
        public ChaseCamera chase;

        /// <summary>Per-player device routing; defaults to the classic merged input.</summary>
        public IDriverInputSource source;

        /// <summary>This frame's horn state — read by the LAN senders so the
        /// wire carries exactly what the audio plays.</summary>
        public bool HornHeldNow { get; private set; }

        private readonly float[] _setpoints = new float[4];
        private Audio.VehicleAudio _audio;
        private float _mouseSteer;

        /// <summary>Spine segment this car was last near, seeding the respawn
        /// projection. Kept per-CarInput so a hairpin resolves to the side of the
        /// track the car actually came down, not whichever side is nearer in a
        /// straight line.</summary>
        private int _respawnHint = -1;

        /// <summary>
        /// State of the automatic reverse blip. Per-CarInput, so split-screen
        /// players are independent of each other.
        /// </summary>
        private enum RevState { Idle, Blipping, Passthrough }
        private RevState _revState;
        private float _blipUntil;

        /// <summary>Below this the car counts as stopped for reverse purposes
        /// (m/s). Well under the ESC's own ≈0.07 m/s "moving" threshold, so the
        /// blip only ever starts once the brake phase has genuinely finished.</summary>
        private const float ReverseRestSpeed = 0.25f;

        private void Awake()
        {
            source ??= new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad);
        }

        private void Update()
        {
            source ??= new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad);
            if (car != null)
            {
                car.SetHandbrake(source.Handbrake());
                if (source.RespawnPressed()) Respawn();
                // Arcade: fire the held power-up. Null outside arcade sessions, so
                // this costs one null check in a normal race.
                if (source.UseItemPressed())
                    Arcade.ArcadeDirector.Instance?.RequestUse(car);

                // Horn: hold-to-sound, routed to this car's audio voice. The
                // component is attached by TrackBootstrap after the rig builds,
                // hence the lazy lookup.
                HornHeldNow = source.HornHeld();
                if (_audio == null) _audio = car.GetComponent<Audio.VehicleAudio>();
                if (_audio != null) _audio.hornHeld = HornHeldNow;
            }

            if (chase != null) chase.lookBack = source.LookBackHeld();

            if (enableMouseSteer)
            {
                _mouseSteer += source.MouseSteerDelta() * mouseSteerSensitivity;
                _mouseSteer = Mathf.MoveTowards(_mouseSteer, 0f, mouseSteerReturn * Time.deltaTime);
                _mouseSteer = Mathf.Clamp(_mouseSteer, -1f, 1f);
            }

            // Keep setpoints fresh for Autonomous consumers.
            float throttle = source.Throttle();
            _setpoints[0] = throttle * maxSpeed;   // target speed (m/s)
            _setpoints[1] = CurrentSteer();        // target steer [-1, 1]
        }

        /// <summary>
        /// Put this car back on the nearest point of the racing line, facing
        /// down-track — not back to the start line, which on a 100–140 m circuit
        /// cost most of a lap and made getting wedged against a wall a heavier
        /// punishment than taking a missile.
        ///
        /// The lap still dies. <c>ResetTimer</c> drops this car's tracker, so it
        /// has to cross the line again to arm a new lap: keeping both the free
        /// position correction AND the lap in progress would make the respawn key
        /// a shortcut past any corner you were about to lose time in.
        ///
        /// Falls back to the spawn point when there is no racing line at all — a
        /// finish-less tile map, where there is nothing better to aim at.
        /// </summary>
        private void Respawn()
        {
            if (TrackRespawn.TryPose(car.transform.position, ref _respawnHint,
                    out var pos, out var rot))
                car.ResetVehicleTo(pos, rot);
            else
                car.ResetVehicle();

            if (lapTimer != null) lapTimer.ResetTimer(car);   // only this car's laps
        }

        private float CurrentSteer()
        {
            float steer = source.Steer();
            if (enableMouseSteer) steer = Mathf.Clamp(steer + _mouseSteer, -1f, 1f);
            return steer;
        }

        /// <summary>
        /// Automate the neutral blip a player currently has to perform by hand
        /// before the car will reverse.
        ///
        /// The bug is in the ESC, not the input: <c>MotorPart.StepDrive</c>
        /// resets its neutral dwell on ANY non-zero command, including a reverse
        /// command that is acting as a brake. So holding S from speed brakes,
        /// stops, and then sits in the "reverse not armed yet" branch for as long
        /// as S stays held — the dwell can never elapse. Releasing for ~170 ms
        /// and pressing again is the only thing that works, which is exactly what
        /// this does automatically.
        ///
        /// It is fixed HERE rather than in the state machine because the machine
        /// is physically right and the Opus mission's brake calibration depends
        /// on it byte-for-byte. This path is provably invisible to firmware:
        /// <c>SimulationRunner.ControlStep</c> only calls
        /// <see cref="ReadManualCommands"/> when the mode is Manual.
        ///
        /// Three states, and the gating matters in each:
        ///   * it engages only when the driver wants reverse, the car is at rest,
        ///     AND no motor reports <c>ReverseReady</c> — so holding S at speed
        ///     still runs the proportional brake, which is correct behaviour and
        ///     must not be cancelled;
        ///   * once reverse engages and the car is moving it returns to Idle, so
        ///     there is no oscillation;
        ///   * if the car then backs into a wall with S still held, the motors
        ///     are still armed (arming is only cleared by a FORWARD command), the
        ///     gate fails, and no second blip fires.
        ///
        /// Rolling backwards down a hill needs nothing from this — the state
        /// machine already accepts reverse whenever the wheels are turning
        /// backwards, regardless of arming.
        /// </summary>
        private float ShapeReverse(float throttle)
        {
            if (car == null) return throttle;

            bool wantsReverse = throttle < -0.01f;
            bool atRest = Mathf.Abs(car.ForwardSpeed) < ReverseRestSpeed;

            switch (_revState)
            {
                case RevState.Blipping:
                    if (!wantsReverse) { _revState = RevState.Idle; return throttle; }
                    if (Time.time < _blipUntil) return 0f;      // hold neutral: let it arm
                    _revState = RevState.Passthrough;
                    return throttle;

                case RevState.Passthrough:
                    // Reverse is engaged. Once it is actually rolling (or the key
                    // is released) go back to Idle and re-arm the whole check.
                    if (!wantsReverse || !atRest) _revState = RevState.Idle;
                    return throttle;

                default:
                    if (wantsReverse && atRest && !AnyMotorReverseReady())
                    {
                        _revState = RevState.Blipping;
                        _blipUntil = Time.time + BlipSeconds();
                        return 0f;
                    }
                    return throttle;
            }
        }

        private bool AnyMotorReverseReady()
        {
            var motors = car.Motors;
            for (int i = 0; i < motors.Count; i++)
                if (motors[i] != null && motors[i].ReverseReady) return true;
            return false;
        }

        /// <summary>Longest blip any of this car's motors needs — a mixed set
        /// must satisfy the slowest one or that motor alone stays in neutral.</summary>
        private float BlipSeconds()
        {
            float longest = 0.2f;
            var motors = car.Motors;
            for (int i = 0; i < motors.Count; i++)
                if (motors[i] != null) longest = Mathf.Max(longest, motors[i].ReverseBlipSeconds);
            return longest;
        }

        public void ReadManualCommands(float[] actuatorOut)
        {
            // Manual "just moves the car": throttle drives every motor at full-scale
            // voltage through the same DC model, so Manual and Autonomous share the
            // exact drivetrain physics. The human never types volts.
            float throttle = ShapeReverse(source.Throttle());
            if (car != null)
            {
                var motors = car.Motors;
                for (int i = 0; i < motors.Count; i++)
                {
                    var m = motors[i];
                    if (m == null) continue;
                    int idx = m.ActuatorIndex;
                    if (idx >= 0 && idx < actuatorOut.Length)
                        actuatorOut[idx] = throttle * m.MaxVoltage;
                }
            }
            if (CarVehicle.SteerActuator < actuatorOut.Length)
                actuatorOut[CarVehicle.SteerActuator] = CurrentSteer();
            if (CarVehicle.BrakeActuator < actuatorOut.Length)
                actuatorOut[CarVehicle.BrakeActuator] = source.Brake();
        }

        public float[] Setpoints => _setpoints;
    }
}
