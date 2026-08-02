using AIHWSim.Telemetry;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// A fixed-wing RC aircraft. A PEER of <see cref="CarVehicle"/>, not a subclass
    /// and not a user of it: no WheelColliders, no tyre model, no suspension.
    ///
    /// It implements <see cref="IControlledVehicle"/>, which is what lets the
    /// existing <c>SimulationRunner</c>, <c>ChaseCamera</c>, <c>TelemetryHub</c>,
    /// <c>CsvLogger</c> and <c>GraphOverlay</c> drive it without a single change —
    /// <c>DifferentialDriveVehicle</c> already proved that interface is not
    /// car-shaped.
    ///
    /// <b>Where the forces come from.</b> Every aerodynamic force is produced by
    /// <see cref="PanelAero"/> from the authored surface geometry; thrust and shaft
    /// torque come from <see cref="PropellerModel"/> driven by the same
    /// <c>MotorModel</c> the cars use. Nothing in this class invents a moment.
    /// There is no "pitch authority", no "roll damping", no stability augmentation
    /// — if the aeroplane resists a roll, it is because the outboard strips of a
    /// rolling wing genuinely see a different angle of attack.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PlaneVehicle : MonoBehaviour, IControlledVehicle
    {
        // ---- actuator slots -------------------------------------------------
        //
        // WARNING TO ANYONE ADDING A CONTROLLER: this layout is an internal
        // agreement between PlaneInput and this class, both of which ship in this
        // repository. It is NOT part of the versioned C ABI. There is no entry for
        // it in Docs/interface-spec.md's "Per-vehicle conventions" and none should
        // be added until an aircraft controller DLL actually exists, at which point
        // the layout is still negotiable — freezing a public contract now would be
        // a promise made to nobody.
        //
        // Slots 6 and 7 are left deliberately EMPTY. controller_api.h publishes
        // them as CTRL_STEER_ACTUATOR and CTRL_BRAKE_ACTUATOR; putting an aileron
        // in a slot whose published name says "steer" is how a convention quietly
        // becomes a lie.
        public const int ThrottleActuator = 0;   // motor terminal volts
        public const int AileronActuator = 1;    // [-1,1]
        public const int ElevatorActuator = 2;   // [-1,1]
        public const int RudderActuator = 3;     // [-1,1]
        /// <summary>[0,1] nozzle tilt TARGET: 0 = full aft (forward flight),
        /// 1 = full down-and-past (hover / viffing). Read only when
        /// <see cref="AircraftSpec.IsJet"/>; a propeller aircraft ignores it, and
        /// slots 6/7 stay empty per the note above.</summary>
        public const int NozzleActuator = 4;

        [Header("Airframe")]
        /// <summary>Set by the builder before the object is activated.</summary>
        public AircraftSpec spec;

        [Header("Debug")]
        [Tooltip("Suppress all aerodynamic forces — used by the ballistic test to "
                 + "prove the harness adds nothing to free fall.")]
        public bool aeroEnabled = true;
        [Tooltip("Suppress thrust and shaft torque (dead-stick glide).")]
        public bool propulsionEnabled = true;
        [Tooltip("Suppress the propeller wake over the tail and inboard wing. The "
                 + "aircraft then has NO pitch or yaw authority at zero airspeed — "
                 + "which is what the wake is for, and the fastest way to see it.")]
        public bool slipstreamEnabled = true;
        /// <summary>
        /// Freeze the thrust FORCE at this value (N), speed-independent. Negative —
        /// the default — leaves the propeller alone.
        ///
        /// <b>This is a control experiment, not a flight mode.</b> A fixed-pitch
        /// propeller on a fixed voltage loses thrust as the aircraft accelerates, and
        /// that lapse is a damper acting on every speed excursion — on this airframe
        /// it supplies more than half the phugoid damping. Arguing that from the
        /// coefficients is not the same as showing it, so A5 flies the identical
        /// disturbance twice and removes the term by force on the second run. If the
        /// damping does not fall to the classical value, the explanation was wrong.
        ///
        /// Only the applied force is frozen. The shaft still integrates, so the
        /// torque reaction and the gyroscopic term are unchanged and cannot be
        /// blamed for the difference, and the wake is solved from the same frozen
        /// thrust so the momentum balance still reads the same from both ends.
        /// </summary>
        [Tooltip("Freeze the thrust force at this value (N) regardless of airspeed. "
                 + "Negative = off. A5's control experiment; not a flight mode.")]
        public float thrustOverrideN = -1f;

        private Rigidbody _body;
        private PanelAero _aero;

        private float _cmdThrottle, _cmdAileron, _cmdElevator, _cmdRudder;
        private float _propOmega;
        private float _propThrust, _propLoadTorque, _propCurrent;
        private SlipstreamState _slip;
        private float _cmdNozzle;    // [0,1] target, jet only
        private float _nozzleDeg;    // actuator state, slewed toward the target
        private float _jetThrust;    // N, after the spool lag

        private Vector3 _spawnPos;
        private Quaternion _spawnRot;
        private Vector3 _lastWorldVel;

        // ---- readable state, for the HUD, telemetry and tests ----
        public AirData Air { get; private set; }
        public PanelAero.Result LastAero { get; private set; }
        public float PropOmega => _propOmega;
        public float PropRevsPerSec => PropellerModel.RevsPerSecond(_propOmega);
        public float Thrust => _propThrust;
        /// <summary>The propeller wake as momentum theory resolved it this step.</summary>
        public SlipstreamState Slipstream => _slip;
        /// <summary>Nozzle angle (deg): 0 aft, 90 straight down. Always 0 on a
        /// propeller aircraft.</summary>
        public float NozzleDeg => _nozzleDeg;
        /// <summary>Nozzle tilt as commanded, [0,1] of the travel range.</summary>
        public float NozzleCommand => _cmdNozzle;

        /// <summary>Lift from one lifting surface in the last solve (N). Index into
        /// <c>spec.surfaces</c>; the wing is <c>spec.wingIndex</c>.</summary>
        public float SurfaceLift(int i) => _aero != null ? _aero.SurfaceLift(i) : 0f;

        /// <summary>Area-weighted mean local dynamic pressure over one surface (Pa).
        /// Divided by <c>Air.Q</c> this is that surface's η — for the tailplane, the
        /// η_h a stability calculation would otherwise have to author.</summary>
        public float SurfaceDynamicPressure(int i) =>
            _aero != null ? _aero.SurfaceDynamicPressure(i) : 0f;

        /// <summary>Worst stall margin on one surface: positive with flow attached,
        /// negative once a strip has let go.</summary>
        public float SurfaceStallMargin(int i) =>
            _aero != null ? _aero.SurfaceStallMargin(i) : 1f;

        /// <summary>Span station of that surface's worst strip, 0 root to 1 tip.</summary>
        public float SurfaceWorstStation(int i) =>
            _aero != null ? _aero.SurfaceWorstStation(i) : 0f;
        /// <summary>Throttle as the pilot set it, [0,1]. The command itself is in
        /// volts (see the actuator layout note), so this divides back out by the
        /// supply rail rather than being a second stored copy that could disagree.</summary>
        public float ThrottleCommand => spec != null && spec.motor.maxVoltage > 0f
            ? Mathf.Clamp01(_cmdThrottle / spec.motor.maxVoltage) : 0f;
        public float AileronCommand => _cmdAileron;
        public float ElevatorCommand => _cmdElevator;
        public float RudderCommand => _cmdRudder;
        /// <summary>Body-frame acceleration (m/s²), gravity included, as an
        /// accelerometer bolted to the airframe would read it.</summary>
        public Vector3 BodyAcceleration { get; private set; }
        /// <summary>Load factor: what the pilot and the wing spar feel.</summary>
        public float LoadFactor { get; private set; }
        public float AltitudeAgl => transform.position.y;
        public float VerticalSpeed => _body != null ? _body.linearVelocity.y : 0f;

        public int SetpointCount => 4;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            if (spec != null) Configure(spec);
        }

        /// <summary>
        /// Apply the spec to the rigidbody. Called by the builder before activation
        /// so that mass, CG and inertia are right on the very first FixedUpdate —
        /// a first step taken with Unity's default box inertia would give the
        /// aircraft a transient nothing later could explain.
        /// </summary>
        public void Configure(AircraftSpec s)
        {
            spec = s;
            _body = _body != null ? _body : GetComponent<Rigidbody>();
            _aero = PanelAero.Build(s.surfaces);

            _body.mass = s.TotalMass;
            _body.centerOfMass = s.CentreOfMass;
            _body.inertiaTensor = s.InertiaTensorDiagonal;
            _body.inertiaTensorRotation = Quaternion.identity;

            // Unity's linear damping is a second, unmodelled drag path. The car
            // carries 0 for exactly this reason — 0.02 was worth 900 N at 30 m/s on
            // the Tiguan, more than twice its real drag — and on an aircraft it
            // would sit on top of the panel model and be measured as glide
            // performance.
            _body.linearDamping = 0f;
            // Angular damping must NOT be used as a pitch or roll damper. Rate
            // damping is the panel model's job; borrowing Unity's would mean the
            // phugoid and roll tests were measuring the integrator.
            _body.angularDamping = 0f;

            // Unity clamps angular velocity at 7 rad/s ≈ 400 deg/s by default, and
            // it does so SILENTLY. An aerobatic model rolls at 600–1000 deg/s, so
            // the default would simply refuse the roll and it would read as a
            // modelling result rather than a clamp.
            _body.maxAngularVelocity = 50f;

            _body.interpolation = RigidbodyInterpolation.Interpolate;
            // 30 m/s at 400 Hz is 75 mm per step against a 1 m airframe, so discrete
            // contacts are sufficient and cheaper than continuous.
            _body.collisionDetectionMode = CollisionDetectionMode.Discrete;

            CaptureSpawn();
        }

        public void CaptureSpawn()
        {
            _spawnPos = transform.position;
            _spawnRot = transform.rotation;
        }

        public void SetSpawn(Vector3 pos, Quaternion rot)
        {
            _spawnPos = pos;
            _spawnRot = rot;
        }

        public void ResetVehicle() => ResetVehicleTo(_spawnPos, _spawnRot);

        public void ResetVehicleTo(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);

            // MANDATORY, and it cost a day to find out why.
            //
            // Writing Transform.position does not move the physics body until the
            // next sync, so for one step transform.position is the new place while
            // Rigidbody.worldCenterOfMass is still the old one. PanelAero forms each
            // strip's moment arm as `rot·posLocal + (tr.position − cm)`, so that
            // stale cm turns a 0.7 m lever arm into the WHOLE TELEPORT DISTANCE —
            // hundreds of metres after a mid-run reposition. One step of that
            // applies an enormous spurious torque, and the aeroplane departs.
            //
            // It showed up as −3.9 g one step after Arm in the stall test, and as a
            // lateral oscillation that grew to 180° of bank in the timestep test.
            // Neither looked like a reset bug: both looked like aerodynamics.
            Physics.SyncTransforms();

            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            _propOmega = 0f;
            _propThrust = 0f;
            _jetThrust = 0f;
            _lastWorldVel = Vector3.zero;
            BodyAcceleration = Vector3.zero;
            LoadFactor = 1f;
        }

        /// <summary>Put the aircraft in the air at a given speed and attitude —
        /// how every scripted flight test starts, because a takeoff roll is not a
        /// repeatable initial condition.</summary>
        public void LaunchAt(Vector3 pos, Quaternion rot, float airspeed)
        {
            ResetVehicleTo(pos, rot);
            _body.linearVelocity = rot * Vector3.forward * airspeed;
        }

        // ---- IControlledVehicle ----

        public void SampleSensors(float dt, float[] wheelVel, float[] gyro, float[] accel)
        {
            // wheelVel has no meaning on an aeroplane and is left at zero, per the
            // interface's own contract ("unused entries should be left at zero").
            for (int i = 0; i < wheelVel.Length; i++) wheelVel[i] = 0f;

            Vector3 w = transform.InverseTransformDirection(_body.angularVelocity);
            gyro[0] = w.x; gyro[1] = w.y; gyro[2] = w.z;
            accel[0] = BodyAcceleration.x;
            accel[1] = BodyAcceleration.y;
            accel[2] = BodyAcceleration.z;
        }

        public void SetCommands(float[] a)
        {
            _cmdThrottle = a[ThrottleActuator];
            _cmdAileron = Mathf.Clamp(a[AileronActuator], -1f, 1f);
            _cmdElevator = Mathf.Clamp(a[ElevatorActuator], -1f, 1f);
            _cmdRudder = Mathf.Clamp(a[RudderActuator], -1f, 1f);
            // Guarded so the trainer path gains nothing but this branch test —
            // the [AERO] gate's byte-identity depends on that.
            if (spec != null && spec.IsJet)
                _cmdNozzle = Mathf.Clamp01(a[NozzleActuator]);
        }

        public void StepPhysics(float dt)
        {
            if (spec == null || _body == null) return;

            Air = AirData.FromBody(_body.linearVelocity, transform.rotation);

            StepPropulsion(dt);
            StepAerodynamics();
            StepParasiticDrag();
            UpdateAccelerometer(dt);
        }

        // ---- the model ----

        private void StepPropulsion(float dt)
        {
            if (!propulsionEnabled)
            {
                _propOmega = 0f;
                _propThrust = 0f;
                _jetThrust = 0f;
                _slip = default;
                return;
            }

            // A jet is a different machine from here down: no shaft to integrate,
            // no torque reaction, no gyroscopic term, and no wake over the tail —
            // all physically correct absences, and exactly why a jet needs
            // puffers at the hover where a prop aircraft still has its wake.
            // The trainer path gains only this branch test.
            if (spec.IsJet) { StepJet(dt); return; }

            // Axial inflow is the component along the shaft, so a climbing or
            // sideslipping aircraft loads the prop correctly without a special case.
            float vAxial = Air.VelocityBody.z;

            // Slot 0 carries VOLTS, exactly as the car's motor slots do and as the
            // layout note at the top of this file says. It must not be treated as a
            // normalised throttle: PlaneInput has already scaled the pilot's [0,1]
            // by the supply rail, and re-scaling here saturated everything above
            // 9 % stick to full power — which showed up as an aircraft whose speed
            // and climb rate were completely indifferent to the throttle.
            float volts = Mathf.Clamp(_cmdThrottle, 0f, spec.motor.maxVoltage);

            _propOmega = PropellerModel.StepShaft(spec.propeller, spec.motor, volts,
                                                  _propOmega, vAxial, dt,
                                                  out _, out _propLoadTorque,
                                                  out _propCurrent);
            _propThrust = PropellerModel.Thrust(spec.propeller, _propOmega, vAxial);

            // Applied BEFORE the wake is solved and before the force goes on, so a
            // frozen thrust and its slipstream stay the same momentum balance. The
            // shaft above is deliberately left running on the real load — the torque
            // reaction and the gyroscopic term must be identical between the two
            // stages, or the experiment has changed two things at once.
            if (thrustOverrideN >= 0f) _propThrust = thrustOverrideN;

            // The wake that thrust implies. Solved from THIS step's thrust and the
            // free-stream inflow, so it cannot disagree with the force being
            // applied a line below — the two are the same momentum balance read
            // from opposite ends.
            _slip = slipstreamEnabled
                ? Aero.Slipstream.Solve(spec.propeller, spec.propPosLocal, _propThrust, vAxial)
                : default;

            Vector3 thrustWorld = transform.forward * _propThrust;
            _body.AddForceAtPosition(thrustWorld, transform.TransformPoint(spec.propPosLocal),
                                     ForceMode.Force);

            // Torque reaction: the airframe rolls against the propeller. Newton's
            // third law, no coefficient — and it is the reason a model swings left
            // on a full-power takeoff roll.
            int dir = spec.propeller.rotationSign == 0 ? 1 : spec.propeller.rotationSign;
            _body.AddTorque(-transform.forward * (_propLoadTorque * dir), ForceMode.Force);

            // Gyroscopic precession: a spinning disc resists being tilted, and
            // reacts one quarter-turn onward. τ = −ω × H, straight out of Euler's
            // equations. Comparable in size to the torque reaction on this airframe.
            Vector3 hWorld = transform.rotation
                             * PropellerModel.AngularMomentumBody(spec.propeller, _propOmega);
            _body.AddTorque(-Vector3.Cross(_body.angularVelocity, hWorld), ForceMode.Force);
        }

        /// <summary>
        /// Vectored-thrust jet propulsion. Runs only when
        /// <see cref="AircraftSpec.IsJet"/>; the propeller path above is untouched.
        ///
        /// <b>What is deliberately absent.</b> No shaft integration (a turbofan's
        /// spool is the first-order lag below, not a torque balance we could
        /// honestly author), no torque reaction or gyroscopic term (the engine's
        /// angular momentum is real but its inertia is not in the parts list, and
        /// inventing one would be authoring a coefficient), and no slipstream —
        /// a jet's tail does not sit in a propeller wake, so the panels lose
        /// their zero-airspeed authority. That loss is physically correct and it
        /// is why the puffers below exist.
        ///
        /// <b>Thrust geometry.</b> The force is split 50/50 between the two
        /// authored nozzle stations and rotated about body X by the nozzle angle
        /// (0° = aft, 90° = straight down, past 90° = the Harrier's braking
        /// range). Stations symmetric about the CG make a balanced hover a
        /// property of geometry rather than of a trim constant.
        ///
        /// <b>Puffers.</b> Reaction control driven by the SAME pilot commands the
        /// panels take — an actuator, not stabilisation, so the vehicle's
        /// "no stability augmentation" declaration stands. Force = per-axis
        /// budget × command, moment = force × authored arm, and the bleed is
        /// SUBTRACTED from the lift thrust, so full stick in the hover genuinely
        /// costs height. Authority fades with nozzle angle (full at 90°, zero
        /// aft) so forward flight is pure panel control with no mode switch.
        /// </summary>
        private void StepJet(float dt)
        {
            JetSpec j = spec.jet;

            // Slot 0 carries volts by the layout note above; the jet's motor
            // stub sets maxVoltage = 1 so this is the pilot's [0,1] again. The
            // division rather than a raw read keeps a scripted pilot that writes
            // real volts honest too.
            float throttle = spec.motor.maxVoltage > 0f
                ? Mathf.Clamp01(_cmdThrottle / spec.motor.maxVoltage) : 0f;

            // Spool: exact exponential first-order lag, rate-independent — the
            // same discretisation the ESC ramp uses.
            float k = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, j.spoolTau));
            _jetThrust += (throttle * j.maxThrustN - _jetThrust) * k;

            // Nozzle: a slew-rate actuator. The slot carries the TARGET; the
            // vehicle owns the actuator dynamics, exactly as it owns the ESC's.
            float wantDeg = Mathf.Lerp(j.nozzleMinDeg, j.nozzleMaxDeg, _cmdNozzle);
            _nozzleDeg = Mathf.MoveTowards(_nozzleDeg, wantDeg, j.nozzleRateDegPerS * dt);

            // Puffer bleed first, so it can rob the lift thrust below.
            // Authority scales with nozzle angle: aft = panels' job.
            float blend = Mathf.Clamp01(_nozzleDeg / 90f);
            float budget = j.pufferBudgetFrac * _jetThrust * blend;
            float bleedUsed = 0f;
            if (budget > 0f)
            {
                Vector3 up = transform.up;
                Vector3 right = transform.right;

                // Pitch: vertical pair at nose and tail. Up-force at the nose is
                // a nose-UP moment (−x torque), matching +elevator = nose up.
                float pitchF = _cmdElevator * budget * 0.5f;
                _body.AddForceAtPosition(up * pitchF,
                    transform.TransformPoint(j.pufferNoseLocal), ForceMode.Force);
                _body.AddForceAtPosition(-up * pitchF,
                    transform.TransformPoint(j.pufferTailLocal), ForceMode.Force);

                // Roll: vertical pair at the tips. +aileron = roll RIGHT
                // (−z torque; +z-Euler is banked left in this project), so the
                // right tip takes the down-force.
                float rollF = _cmdAileron * budget * 0.5f;
                _body.AddForceAtPosition(-up * rollF,
                    transform.TransformPoint(j.pufferTipRightLocal), ForceMode.Force);
                _body.AddForceAtPosition(up * rollF,
                    transform.TransformPoint(j.pufferTipLeftLocal), ForceMode.Force);

                // Yaw: lateral pair on the same nose/tail stations. Rightward
                // force at the nose is nose-right (+y), matching +rudder.
                float yawF = _cmdRudder * budget * 0.5f;
                _body.AddForceAtPosition(right * yawF,
                    transform.TransformPoint(j.pufferNoseLocal), ForceMode.Force);
                _body.AddForceAtPosition(-right * yawF,
                    transform.TransformPoint(j.pufferTailLocal), ForceMode.Force);

                // Each axis exhausts |cmd| × budget of engine air.
                bleedUsed = (Mathf.Abs(_cmdElevator) + Mathf.Abs(_cmdAileron)
                             + Mathf.Abs(_cmdRudder)) * budget;
            }

            float applied = Mathf.Max(0f, _jetThrust - bleedUsed);

            // 0° = +Z (aft nozzles, forward force); AngleAxis(−90°, right) turns
            // +Z to +Y — nozzles down, force up. Past 90° the vector points
            // slightly forward-of-up: braking thrust, as on the real aircraft.
            Vector3 dirBody = Quaternion.AngleAxis(-_nozzleDeg, Vector3.right)
                              * Vector3.forward;
            Vector3 half = transform.rotation * (dirBody * (applied * 0.5f));
            _body.AddForceAtPosition(half,
                transform.TransformPoint(j.nozzleForeLocal), ForceMode.Force);
            _body.AddForceAtPosition(half,
                transform.TransformPoint(j.nozzleAftLocal), ForceMode.Force);

            // Read-through: the HUD's throttle/thrust instruments and telemetry
            // read _propThrust; a jet reports its jet thrust there. The shaft
            // stays at rest — there is no shaft.
            _propThrust = applied;
            _propOmega = 0f;
            _propLoadTorque = 0f;
            _propCurrent = 0f;
            _slip = default;
        }

        private void StepAerodynamics()
        {
            if (!aeroEnabled || _aero == null) { LastAero = default; return; }

            PanelAero.Result r = _aero.Solve(_body, transform, Air,
                                             _cmdAileron, _cmdElevator, _cmdRudder, _slip);
            LastAero = r;
            _body.AddForce(r.forceWorld, ForceMode.Force);
            _body.AddTorque(r.torqueWorld, ForceMode.Force);
        }

        /// <summary>
        /// Fuselage, landing gear and interference drag. Everything that is not a
        /// lifting surface — the surfaces' own profile drag is applied per strip by
        /// the panel model, so including it here as well would count it twice and
        /// halve the glide ratio.
        /// </summary>
        private void StepParasiticDrag()
        {
            // Gated by aeroEnabled too: a ballistic test that switched off the
            // panels but left the fuselage dragging would not be measuring free
            // fall, and the discrepancy would look like an integrator error.
            if (!aeroEnabled || spec.parasiticCdA <= 0f) return;
            Vector3 v = _body.linearVelocity - AirData.Wind;
            float sp2 = v.sqrMagnitude;
            if (sp2 < 1e-4f) return;
            float q = 0.5f * AeroDynamics.AirDensity * sp2;
            _body.AddForce(-v.normalized * (q * spec.parasiticCdA), ForceMode.Force);
        }

        /// <summary>
        /// Body-frame acceleration, by differencing the WORLD velocity and then
        /// rotating into the body frame.
        ///
        /// <b>Not by differencing body-frame velocity.</b> That drops the ω×v
        /// transport term, and it has already cost this project one debugging
        /// session: the car's skidpad read 0.36 g while the tyres were delivering
        /// 1 g, and every straight-line test agreed with the broken form. An
        /// aircraft in a turn is ALWAYS rotating — in a 60°-bank level turn the
        /// transport term is the entire 2 g — so here the wrong form would not be a
        /// corner case, it would be most of the flight envelope.
        /// </summary>
        private void UpdateAccelerometer(float dt)
        {
            Vector3 vWorld = _body.linearVelocity;
            Vector3 aWorld = dt > 1e-9f ? (vWorld - _lastWorldVel) / dt : Vector3.zero;
            _lastWorldVel = vWorld;

            // An accelerometer measures specific force: acceleration minus gravity.
            Vector3 specific = aWorld - Physics.gravity;
            BodyAcceleration = transform.InverseTransformDirection(specific);
            LoadFactor = Vector3.Dot(specific, transform.up) / 9.80665f;
        }

        public void PublishTelemetry(TelemetryHub hub)
        {
            // Ground speed under the shared name every vehicle uses; airspeed is a
            // separate channel and FlightTelemetry owns it.
            hub.SetValue("veh/speed", _body.linearVelocity.magnitude);
            hub.SetValue("veh/pos_x", transform.position.x);
            hub.SetValue("veh/pos_z", transform.position.z);
            hub.SetValue("veh/yaw_rate", _body.angularVelocity.y);
        }
    }
}
