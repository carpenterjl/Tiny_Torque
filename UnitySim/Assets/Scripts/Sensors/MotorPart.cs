using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// A driven motor mounted on one wheel. It is both an actuator and a
    /// feedback sensor:
    ///
    ///  - Actuator: each physics step <see cref="StepDrive"/> takes the latched
    ///    command, runs it through this motor's drive (see
    ///    <see cref="MotorDriveMode"/>) against the wheel's current speed, and
    ///    applies the resulting torque to the wheel. The torque/current
    ///    therefore emerge from the vehicle dynamics.
    ///  - Sensor: it stays a <see cref="SensorComponent"/> so the rig publishes
    ///    its feedback and the controller learns its actuator slot, units and
    ///    limits from the manifest. A voltage drive reports SENSOR_MOTOR
    ///    [V, I, torque]; an FOC drive reports SENSOR_FOC_FB
    ///    [Iq, Id, ω_m, V_bus, T_winding, faults] — what a real driver can
    ///    measure, which does not include torque (ACT-10).
    ///
    /// A wheel is driven iff it has a MotorPart; wheels without one free-roll.
    /// </summary>
    public sealed class MotorPart : SensorComponent
    {
        [Header("Motor")]
        [Tooltip("Which wheel this motor drives: 0=FL, 1=FR, 2=RL, 3=RR.")]
        public int wheelIndex = 2;
        public MotorParams motor = MotorParams.Default();

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "voltage", "current", "torque" };
        private static readonly string[] FocFields = { "iq", "id", "omega_m", "vbus", "temp_c", "fault" };

        /// <summary>SENSOR_FOC_FB fault bits (field 5). The trips (over-voltage,
        /// command timeout, rotor sensor, over-current) switch the bridge off.</summary>
        public const int FaultOverVoltage = 1, FaultOverTemp = 2,
                         FaultCurrentLimit = 4, FaultVoltageLimit = 8,
                         FaultCmdTimeout = 16, FaultRotorSensor = 32, FaultOverCurrent = 64;
        private const int BridgeOffFaults = FaultOverVoltage | FaultCmdTimeout |
                                            FaultRotorSensor | FaultOverCurrent;

        /// <summary>Time since the firmware's last command frame (s), set by the
        /// car each physics step; the driver's watchdog compares it with
        /// <c>cmdTimeoutMs</c>.</summary>
        public float CommandAgeS { get; set; }

        /// <summary>VAL-11: driver trips forced on (the <c>Fault*</c> trip bits;
        /// they act as the real trip would), and an offset on the reported
        /// winding temperature (°C). Set by the FaultInjector.</summary>
        public int InjectedFaults { get; set; }
        public float InjectedTempOffsetC { get; set; }

        private const float AmbientC = 25f;
        private const float CopperTempco = 0.0039f;   // 1/K
        private const float MagnetTempco = 0.001f;    // kt loss per K
        private const float PllHz = 200f;             // driver speed estimate bandwidth
        private const float BusFilterS = 0.005f;      // driver's bus-voltage filter (derate input)

        private CarVehicle _vehicle;
        private WheelCollider _wheel;

        // Latched command (volts or amps) + cached electrical operating point.
        private float _command;
        private float _voltage, _current, _torque;
        private float _busCurrent;

        // ESC pipeline state (slew-limited then first-order-lagged output voltage).
        private float _vSlew, _vFilt;

        // Drive/brake/reverse state (i22): neutral dwell timer, reverse arming,
        // and whether the last step was a shorted-winding brake (whose current
        // circulates in the bridge and draws nothing from the pack).
        private float _neutralTime;
        private bool _reverseArmed = true;   // armed at power-on in neutral
        private bool _braking;

        // FOC drive state: command sample-and-hold, transport delay line,
        // actual Iq (current-loop lag), latched faults, the driver's speed
        // estimate.
        private double _holdT;
        private float _held;
        private readonly float[] _delay = new float[64];
        private int _delayHead;
        private float _iq;
        private int _fault;
        private float _omegaPll;
        private float _vBusFilt;

        // Thermal state (ACT-05).
        private float _tWinding = AmbientC, _tCase = AmbientC;

        // Lash state (ACT-07): rotor speed referred to the wheel, the free-play
        // position (rotor angle − wheel angle, wheel side) and whether the
        // teeth are in contact.
        private float _omegaR;
        private float _gap;
        private bool _engaged;

        /// <summary>
        /// Dev A/B switch (mirrors TyreModel.Enabled): false restores the legacy
        /// smooth signed-voltage model with no brake/reverse behaviour.
        /// </summary>
        public static bool StateMachineEnabled = true;

        /// <summary>Actuator slot this motor reads from (assigned by the rig).</summary>
        public int ActuatorIndex { get; set; } = -1;
        public float MaxVoltage => motor.maxVoltage;

        public MotorDriveMode DriveMode => (MotorDriveMode)motor.driveMode;
        public bool IsFoc => motor.driveMode == (int)MotorDriveMode.CurrentFoc;

        /// <summary>Units of this motor's actuator slot.</summary>
        public ActuatorUnits Units => IsFoc ? ActuatorUnits.AmpsIq : ActuatorUnits.Volts;

        /// <summary>Full-scale command: volts for a voltage drive, amps for FOC.</summary>
        public float MaxCommand => IsFoc ? FocMaxCurrent : motor.maxVoltage;

        private float FocMaxCurrent => motor.maxCurrent > 0f
            ? motor.maxCurrent
            : Mathf.Max(0.01f, motor.maxVoltage) / Mathf.Max(1e-3f, motor.resistance);

        /// <summary>Electrical current at the last StepDrive (A, signed).</summary>
        public float Current => _current;

        /// <summary>
        /// Current this motor draws from the pack (A, signed: negative charges
        /// it). From the power balance V_motor·I_motor / V_bus, so a part-duty
        /// cruise draws less than the winding current, and regen is negative
        /// (ACT-02 / BUG-06). A shorted-winding ESC brake, or an open circuit,
        /// draws nothing: its current circulates inside the bridge.
        /// </summary>
        public float PackCurrent => _busCurrent;

        /// <summary>Torque this motor produced at the wheel last step (N·m) —
        /// truth for validators, never on the ABI.</summary>
        public float WheelTorqueOut => _torque;

        /// <summary>Gear teeth in contact (always true without lash).</summary>
        public bool LashEngaged => !HasLash || _engaged;

        /// <summary>Winding temperature (°C); ambient when the thermal model is off.</summary>
        public float WindingTempC => _tWinding;

        /// <summary>Latched driver faults (FOC drive), the <c>Fault*</c> bits.</summary>
        public int Faults => _fault;

        /// <summary>
        /// Would a reverse command engage right now, or would the ESC hold
        /// neutral waiting out its lockout?
        ///
        /// Read-only, read by nothing inside this class. It exists so
        /// <c>CarInput</c> can tell three states apart that look identical
        /// from outside: "commanded reverse and the ESC is stalling", "reversing
        /// into a wall", and "reverse already engaged at 0.05 m/s". Any heuristic
        /// without this signal confuses them and introduces a subtler bug than
        /// the one it fixes.
        ///
        /// True when there is no state machine (switched off, or a drive that
        /// has none): there is no lockout to wait for.
        /// </summary>
        public bool ReverseReady =>
            !StateMachineEnabled || DriveMode != MotorDriveMode.HobbyEsc || _reverseArmed;

        /// <summary>
        /// How long a neutral blip has to be held for this ESC to arm reverse:
        /// the configured dwell plus a margin for the output lag ahead of it.
        ///
        /// Derived rather than hardcoded because <c>GarageUI</c> puts
        /// <c>escReverseLockMs</c> on a slider — a fixed 200 ms would silently
        /// stop working on a tuned ESC, which is exactly the kind of failure
        /// nobody would connect back to a garage setting.
        /// </summary>
        public float ReverseBlipSeconds
        {
            get
            {
                float lockMs = motor.escReverseLockMs > 0f ? motor.escReverseLockMs : 150f;
                float lagMs = Mathf.Max(40f, motor.escTimeConstMs * 3f);
                return (lockMs + lagMs) * 0.001f;
            }
        }

        /// <summary>Motor shaft speed (rad/s, signed) — for vibration modeling.
        /// With gear lash the rotor turns on its own inside the free play.</summary>
        public float MotorOmega =>
            _vehicle == null ? 0f
            : (HasLash ? _omegaR : _vehicle.WheelOmega(wheelIndex)) * motor.gearRatio;

        /// <summary>Gear lash modelled: the rotor is its own body inside the
        /// free play rather than inertia riding on the wheel.</summary>
        public bool HasLash => motor.lashRad > 0f && motor.rotorInertia > 0f;

        /// <summary>Rotor angle minus wheel angle (wheel side, rad): where the
        /// motor sits inside the free play. 0 without lash. A motor-shaft
        /// encoder reads the wheel angle plus this.</summary>
        public float LashGapRad => HasLash ? _gap : 0f;

        public override SensorType Type => IsFoc ? SensorType.FocFb : SensorType.Motor;
        public override int DataCount => IsFoc ? 6 : 3;
        public override IReadOnlyList<string> FieldNames => IsFoc ? FocFields : Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            _wheel = vehicle != null ? vehicle.GetWheel(wheelIndex) : null;
            rangeMin = -MaxCommand;
            rangeMax = MaxCommand;
        }

        /// <summary>Latch the command (control rate, zero-order hold): volts for
        /// a voltage drive, Iq in amps for an FOC drive.</summary>
        public void SetCommand(float value) => _command = value;

        /// <summary>Old name for <see cref="SetCommand"/>.</summary>
        public void SetVoltage(float volts) => _command = volts;

        /// <summary>
        /// Live bus voltage from the vehicle's battery model; 0 = no battery
        /// (the nominal <c>maxVoltage</c> is the rail). Set every physics step
        /// before <see cref="StepDrive"/> so a fresh pack above nominal is not
        /// clamped down to it.
        /// </summary>
        public float BusVoltage { get; set; }

        public void ResetMotor()
        {
            _command = _voltage = _current = _torque = _busCurrent = 0f;
            _vSlew = _vFilt = 0f;
            _neutralTime = 0f;
            _reverseArmed = true;   // fresh power-on in neutral
            _braking = false;
            _holdT = 0.0;
            _held = 0f;
            System.Array.Clear(_delay, 0, _delay.Length);
            _delayHead = 0;
            _iq = 0f;
            _fault = 0;
            _omegaPll = 0f;
            _vBusFilt = 0f;
            _tWinding = _tCase = AmbientC;
            _omegaR = 0f;
            _gap = 0f;
            _engaged = false;
        }

        /// <summary>
        /// Run the drive for one physics step and put its torque on the wheel.
        /// Returns the geared wheel torque produced.
        /// </summary>
        public float StepDrive(float dt)
        {
            if (_wheel == null && _vehicle != null) _wheel = _vehicle.GetWheel(wheelIndex);
            if (_wheel == null || _vehicle == null) return 0f;

            // Wheel speed from the vehicle's canonical source (the brush-model
            // integrator, or wc.rpm on the legacy path) — back-EMF must see the
            // same ω the encoders and the tyre forces see. With lash it is the
            // rotor's own speed, which only equals the wheel's in contact.
            float wheelOmega = _vehicle.WheelOmega(wheelIndex);
            if (HasLash && _engaged) _omegaR = wheelOmega;
            float shaftOmega = HasLash ? _omegaR : wheelOmega;

            // Thermal: R and kt drift with the winding temperature. Off (the
            // default) uses the parameters verbatim.
            MotorParams p = motor;
            bool thermal = motor.thermalRwcKPerW > 0f;
            if (thermal)
            {
                float dT = _tWinding - AmbientC;
                p.resistance = motor.resistance * (1f + CopperTempco * dT);
                p.kt = motor.kt * Mathf.Max(0.5f, 1f - MagnetTempco * dT);
            }

            float torque = IsFoc ? StepFoc(in p, shaftOmega, dt) : StepVoltage(in p, shaftOmega, dt);
            torque += Cogging(dt);

            if (thermal) StepThermal(in p, dt);
            _omegaPll += (shaftOmega * motor.gearRatio - _omegaPll) *
                         (1f - Mathf.Exp(-dt * 2f * Mathf.PI * PllHz));

            _torque = torque;
            if (!HasLash)
            {
                // Torque sink: the vehicle routes it to its spin integrator
                // (brush path) or to WheelCollider.motorTorque (legacy path).
                _vehicle.ApplyDriveTorque(wheelIndex, _torque);
            }
            else
            {
                StepLashBefore(_torque, wheelOmega, dt);
            }
            return _torque;
        }

        // ---------------------------------------------------- voltage drives --

        private float StepVoltage(in MotorParams p, float wheelOmega, float dt)
        {
            // ESC pipeline: deadband → PWM quantization → slew limit → first-order
            // lag. Every stage is off (passthrough) at its 0 default, so old designs
            // keep the legacy instant-voltage behaviour byte-for-byte.
            float v = _command;
            if (motor.escDeadbandV > 0f && Mathf.Abs(v) < motor.escDeadbandV) v = 0f;
            if (motor.escPwmSteps > 0)
            {
                float vmax = Mathf.Max(0.01f, motor.maxVoltage);
                v = Mathf.Round(v / vmax * motor.escPwmSteps) / motor.escPwmSteps * vmax;
            }
            if (motor.escSlewVPerS > 0f)
                v = _vSlew = Mathf.MoveTowards(_vSlew, v, motor.escSlewVPerS * dt);
            if (motor.escTimeConstMs > 0f)
                v = _vFilt += (v - _vFilt) * (1f - Mathf.Exp(-dt * 1000f / motor.escTimeConstMs));
            else
                _vFilt = v;

            // ---- Hobby-ESC drive/brake/reverse state machine (i22) ----
            // A real car ESC never drives against rotation: opposite-sign command
            // while moving is a proportional shorted-winding brake; reverse only
            // engages after a dwell in neutral at rest; an optional drag brake
            // acts at neutral, and without one neutral is an open circuit — the
            // car coasts (ACT-03: this used to drive 0 V into the winding, a full
            // short-circuit brake). Forward drive is the unchanged DC model, so
            // top speed and the garage stats are unaffected.
            float torque;
            bool open = false;
            _braking = false;
            if (StateMachineEnabled && DriveMode == MotorDriveMode.HobbyEsc)
            {
                float dead = Mathf.Max(0.02f, motor.escDeadbandV);
                float lockS = (motor.escReverseLockMs > 0f ? motor.escReverseLockMs : 150f) * 0.001f;
                float strength = motor.escBrakeStrengthPct > 0f
                    ? Mathf.Clamp01(motor.escBrakeStrengthPct / 100f) : 1f;
                // The literal encodes a GROUND speed, not a wheel speed: 2 rad/s
                // is ≈0.07 m/s at r = 33 mm but ≈0.70 m/s at r = 349 mm, where
                // the ESC would still call a car moving at walking pace
                // "stationary" and drive when it should brake — which breaks the
                // last few metres of every braking measurement.
                float movingOmega = motor.escMovingOmega > 0f ? motor.escMovingOmega : 2f;
                bool moving = Mathf.Abs(wheelOmega) > movingOmega;

                if (Mathf.Abs(v) < dead)
                {
                    // Neutral: dwell (at rest) arms reverse; drag brake if configured.
                    _neutralTime += dt;
                    if (_neutralTime >= lockS && !moving) _reverseArmed = true;
                    if (motor.escDragBrakePct > 0f && moving)
                    {
                        _braking = true;
                        torque = MotorModel.BrakeTorque(in p,
                            motor.escDragBrakePct / 100f, wheelOmega, out _current, BusVoltage);
                        _voltage = 0f;
                    }
                    else
                    {
                        open = true;
                        torque = MotorModel.CoastTorque(in p, wheelOmega);
                        _voltage = 0f;
                        _current = 0f;
                    }
                }
                else
                {
                    _neutralTime = 0f;
                    if (moving && v * wheelOmega < 0f)
                    {
                        // Command opposes rotation → proportional brake. The duty
                        // is the command fraction; torque = Kt·(duty·Kt·ω/R) always
                        // opposing rotation — it fades to nothing at rest and can
                        // never spin the wheel backwards.
                        _braking = true;
                        float duty = Mathf.Abs(v) / Mathf.Max(0.01f, motor.maxVoltage) * strength;
                        torque = MotorModel.BrakeTorque(in p, duty, wheelOmega, out _current, BusVoltage);
                        _voltage = 0f;
                    }
                    else if (v > 0f || _reverseArmed || wheelOmega < -movingOmega)
                    {
                        if (v > 0f) _reverseArmed = false;   // next reverse needs a dwell
                        torque = MotorModel.WheelTorque(in p, v, wheelOmega,
                                                        out _voltage, out _current, BusVoltage);
                    }
                    else
                    {
                        // Wants reverse before the lockout expires: the ESC holds
                        // neutral, which is open circuit.
                        open = true;
                        torque = MotorModel.CoastTorque(in p, wheelOmega);
                        _voltage = 0f;
                        _current = 0f;
                    }
                }
            }
            else
            {
                torque = MotorModel.WheelTorque(in p, v, wheelOmega,
                                                out _voltage, out _current, BusVoltage);
            }

            // Pack draw from the power balance. A shorted-winding brake and an
            // open circuit draw nothing.
            float vBus = BusVoltage > 0f ? BusVoltage : Mathf.Max(0.01f, motor.maxVoltage);
            _busCurrent = (_braking || open) ? 0f : _voltage * _current / vBus;
            return torque;
        }

        // --------------------------------------------------------- FOC drive --

        /// <summary>
        /// One physics step of an FOC driver with a closed current loop
        /// (ACT-01; equations in SIM_TO_REAL_PLAN §6.1). The FOC algorithm
        /// itself never runs here — its closed-loop behaviour does: command
        /// period and transport delay, current limits (separate for regen),
        /// thermal and over-voltage derating, the voltage ceiling that rolls
        /// torque off at speed, a first-order current loop, and a signed bus
        /// current from the power balance. Zero current coasts.
        /// </summary>
        private float StepFoc(in MotorParams p, float wheelOmega, float dt)
        {
            float gear = Mathf.Max(1e-3f, motor.gearRatio);
            float omegaM = wheelOmega * gear;
            float vBus = BusVoltage > 0f ? BusVoltage : Mathf.Max(0.01f, motor.maxVoltage);
            int pp = motor.polePairs > 0 ? motor.polePairs : 7;

            // Command path: sample-and-hold at the driver's command period, then
            // the transport delay (MCU frame → driver), in whole physics steps.
            float period = motor.cmdPeriodMs * 0.001f;
            if (period <= 0f) _held = _command;
            else
            {
                _holdT += dt;
                if (_holdT >= period - 1e-9) { _holdT -= period; _held = _command; }
            }
            // A delay that is not a whole number of steps blends the two steps
            // either side of it, so the torque impulse a command delivers is
            // right at any physics rate (rounding made 1 ms read as 0 ms at
            // 400 Hz and 1.25 ms at 800 — a stopping distance that moved with dt).
            float lagSteps = motor.cmdLatencyMs > 0f
                ? Mathf.Clamp(motor.cmdLatencyMs * 0.001f / Mathf.Max(1e-6f, dt), 0f, _delay.Length - 2)
                : 0f;
            int lag = (int)lagSteps;
            float frac = lagSteps - lag;
            _delay[_delayHead] = _held;
            int n = _delay.Length;
            float iqCmd = _delay[(_delayHead - lag + n) % n];
            if (frac > 0f)
                iqCmd += (_delay[(_delayHead - lag - 1 + n) % n] - iqCmd) * frac;
            _delayHead = (_delayHead + 1) % n;

            // The derate works from the driver's filtered bus measurement — a
            // raw per-step value through the pack's resistance would make it a
            // relay. The trip itself is a comparator on the instantaneous bus.
            _vBusFilt = _vBusFilt <= 0f ? vBus
                      : _vBusFilt + (vBus - _vBusFilt) * (1f - Mathf.Exp(-dt / BusFilterS));

            // Faults. Over-voltage latches the bridge off (coast) until the bus
            // falls back below the derate threshold.
            float ovTrip = motor.busOvTripV;
            float ovStart = motor.busOvDerateV > 0f ? motor.busOvDerateV : ovTrip - 0.3f;
            if (ovTrip > 0f)
            {
                if (vBus >= ovTrip) _fault |= FaultOverVoltage;
                else if (vBus < ovStart) _fault &= ~FaultOverVoltage;
            }
            _fault &= ~(FaultCurrentLimit | FaultVoltageLimit | FaultOverTemp |
                        FaultCmdTimeout | FaultRotorSensor | FaultOverCurrent);
            // Command watchdog: no frame from the MCU inside the window and
            // the bridge goes off until one arrives (CAN-timeout behaviour).
            if (motor.cmdTimeoutMs > 0f && CommandAgeS * 1000f > motor.cmdTimeoutMs)
            {
                // ...and forgets the setpoint it held, so the first frame
                // after the gap is obeyed rather than the last one before it.
                _fault |= FaultCmdTimeout;
                _held = 0f;
                System.Array.Clear(_delay, 0, _delay.Length);
            }
            _fault |= InjectedFaults & BridgeOffFaults;

            // Limits: motoring and regen separately, both derated by temperature;
            // regen also fades as the bus approaches the over-voltage trip.
            float iMot = FocMaxCurrent;
            float iRegen = motor.maxRegenCurrent > 0f ? motor.maxRegenCurrent : iMot;
            if (motor.tempLimitC > 0f)
            {
                float start = motor.tempDerateStartC > 0f ? motor.tempDerateStartC : motor.tempLimitC - 20f;
                float s = Mathf.Clamp01((motor.tempLimitC - _tWinding) / Mathf.Max(1e-3f, motor.tempLimitC - start));
                if (s < 1f) _fault |= FaultOverTemp;
                iMot *= s;
                iRegen *= s;
            }
            if (ovTrip > 0f && ovTrip > ovStart)
                iRegen *= Mathf.Clamp01((ovTrip - _vBusFilt) / (ovTrip - ovStart));

            float iqT = iqCmd;
            bool regen = iqT * omegaM < 0f;
            float lim = regen ? iRegen : iMot;
            if (Mathf.Abs(iqT) > lim) { iqT = Mathf.Sign(iqT) * lim; _fault |= FaultCurrentLimit; }
            if ((_fault & BridgeOffFaults) != 0) iqT = 0f;

            // Voltage ceiling, then the current loop, then the ceiling again
            // (the speed may have moved the window under the lagged current).
            float l = Mathf.Max(0f, motor.inductance);
            float m = motor.modulationMax > 0f ? motor.modulationMax : 0.95f;
            MotorModel.FocIqWindow(Mathf.Max(1e-3f, p.resistance), p.kt, l, pp, omegaM,
                                   m * vBus, out float lo, out float hi);
            if (iqT < lo || iqT > hi) { iqT = Mathf.Clamp(iqT, lo, hi); _fault |= FaultVoltageLimit; }
            float a = motor.currentLoopHz > 0f ? 1f - Mathf.Exp(-dt * 2f * Mathf.PI * motor.currentLoopHz) : 1f;
            _iq += (iqT - _iq) * a;
            if ((_fault & BridgeOffFaults) != 0) _iq = 0f;    // bridge off: no current at all
            _iq = Mathf.Clamp(_iq, lo, hi);

            // Torque: electromagnetic (with 6th-harmonic ripple), then friction,
            // which still acts at Iq = 0 — so the car coasts rather than brakes.
            float ripple = 0f;
            if (motor.rippleFrac != 0f)
            {
                double thetaE = pp * MotorAngle();
                ripple = motor.rippleFrac * (float)System.Math.Sin(6.0 * thetaE) *
                         AliasFade(6f * pp * Mathf.Abs(omegaM), dt);
            }
            float tauEm = p.kt * _iq * (1f + ripple);
            float torqueMotor = MotorModel.WithFriction(tauEm, motor.viscousDamping * omegaM,
                                                        MotorModel.CoulombTorque(in p), omegaM);

            // Electrical operating point and the pack draw it implies (Id = 0).
            float vq = Mathf.Max(1e-3f, p.resistance) * _iq + p.kt * omegaM;
            float vd = -pp * omegaM * l * _iq;
            _voltage = Mathf.Sign(vq) * Mathf.Sqrt(vq * vq + vd * vd);
            _current = _iq;
            _busCurrent = vq * _iq / vBus;
            _braking = false;

            return MotorModel.FocGearToWheel(in p, torqueMotor, omegaM);
        }

        // ----------------------------------------- cogging, thermal, lash --

        /// <summary>Motor-shaft angle (rad, unwrapped): the wheel's integrated
        /// angle plus any lash gap, times the gear ratio.</summary>
        private double MotorAngle() =>
            (_vehicle.WheelAngle(wheelIndex) + LashGapRad) * motor.gearRatio;

        /// <summary>A periodic torque at frequency f cannot be represented at
        /// the physics rate near Nyquist; fade it out between f_phys/8 and
        /// f_phys/4 instead of letting it alias into a slow beat.</summary>
        private static float AliasFade(float omegaPeriodic, float dt)
        {
            float f = omegaPeriodic / (2f * Mathf.PI);
            float fPhys = 1f / Mathf.Max(1e-6f, dt);
            return Mathf.Clamp01((fPhys * 0.25f - f) / (fPhys * 0.125f));
        }

        /// <summary>Cogging (ACT-06): a detent torque on the motor shaft that
        /// depends on rotor position only — present with the drive off.</summary>
        private float Cogging(float dt)
        {
            if (motor.coggingNm == 0f || motor.coggingPerRev <= 0) return 0f;
            double theta = MotorAngle();
            float omegaM = MotorOmega;
            float tau = motor.coggingNm * (float)System.Math.Sin(motor.coggingPerRev * theta) *
                        AliasFade(motor.coggingPerRev * Mathf.Abs(omegaM), dt);
            return IsFoc ? MotorModel.FocGearToWheel(in motor, tau, omegaM)
                         : MotorModel.GearToWheel(in motor, tau, omegaM);
        }

        /// <summary>
        /// Two-node thermal network (ACT-05): copper loss heats the winding,
        /// which conducts to the case, which convects to ambient.
        /// </summary>
        private void StepThermal(in MotorParams p, float dt)
        {
            float pLoss = Mathf.Max(1e-3f, p.resistance) * _current * _current;
            float rwc = Mathf.Max(1e-3f, motor.thermalRwcKPerW);
            float rca = motor.thermalRcaKPerW > 0f ? motor.thermalRcaKPerW : 10f;
            float cw = motor.thermalCwJPerK > 0f ? motor.thermalCwJPerK : 10f;
            float cc = motor.thermalCcJPerK > 0f ? motor.thermalCcJPerK : 50f;
            // Explicit Euler is fine for second-scale constants; sub-step if a
            // design makes one comparable to the physics step.
            float tauMin = Mathf.Min(rwc * cw, Mathf.Min(rwc * cc, rca * cc));
            int n = Mathf.Clamp(Mathf.CeilToInt(dt / Mathf.Max(1e-6f, 0.1f * tauMin)), 1, 64);
            float h = dt / n;
            for (int k = 0; k < n; k++)
            {
                float qwc = (_tWinding - _tCase) / rwc;
                float qca = (_tCase - AmbientC) / rca;
                _tWinding += (pLoss - qwc) / cw * h;
                _tCase += (qwc - qca) / cc * h;
            }
        }

        /// <summary>
        /// Gear lash (ACT-07), before the wheel integrates. In contact the teeth
        /// are rigid: the motor torque goes to the wheel and the rotor's
        /// reflected inertia rides on it. Out of contact the wheel is on its own
        /// and the rotor turns freely inside the free play, which is when a
        /// motor-shaft encoder counts while the wheel stands still.
        /// </summary>
        private void StepLashBefore(float motorTorqueAtWheel, float wheelOmega, float dt)
        {
            float jR = motor.rotorInertia * motor.gearRatio * motor.gearRatio;
            float jW = _vehicle.WheelSpinInertia(wheelIndex);

            if (_engaged)
            {
                // Would the teeth stay pressed together this step? Solve the rigid
                // pair against last step's external wheel torque; contact can only
                // push, so a coupling torque against the contact side separates.
                float side = _gap > 0f ? 1f : -1f;
                float tExt = _vehicle.WheelLastExternalTorque(wheelIndex);
                float tCouple = (jW * motorTorqueAtWheel - jR * tExt) / Mathf.Max(1e-12f, jR + jW);
                if (tCouple * side < 0f) _engaged = false;
            }

            if (_engaged)
            {
                _vehicle.ApplyDriveTorque(wheelIndex, motorTorqueAtWheel);
                _vehicle.SetCoupledInertia(wheelIndex, jR);
            }
            else
            {
                _vehicle.ApplyDriveTorque(wheelIndex, 0f);
                _vehicle.SetCoupledInertia(wheelIndex, 0f);
                _omegaR += motorTorqueAtWheel / Mathf.Max(1e-12f, jR) * dt;
            }
        }

        /// <summary>
        /// Gear lash, after the wheel integrated (called by the vehicle). Out of
        /// contact the free play closes or opens at the speed difference; when
        /// it closes the teeth meet inelastically, conserving momentum.
        /// </summary>
        public void AfterWheelStep(float dt)
        {
            if (!HasLash || _vehicle == null) return;
            float wW = _vehicle.WheelOmega(wheelIndex);
            if (_engaged) { _omegaR = wW; return; }

            float half = 0.5f * motor.lashRad;
            _gap += (_omegaR - wW) * dt;
            if (Mathf.Abs(_gap) >= half)
            {
                _gap = Mathf.Sign(_gap) * half;
                float jR = motor.rotorInertia * motor.gearRatio * motor.gearRatio;
                float jW = _vehicle.WheelSpinInertia(wheelIndex);
                float w = (jR * _omegaR + jW * wW) / Mathf.Max(1e-12f, jR + jW);
                _vehicle.SetWheelOmega(wheelIndex, w);
                _omegaR = w;
                _engaged = true;
            }
        }

        // --------------------------------------------------------- feedback --

        // Realistic profile (SEN-06): the driver's own converters.
        private SenseChannel _senseA, _senseB, _senseBus, _senseT;

        public override void Sample(float dt, float[] dest, int offset)
        {
            if (Realistic && _senseA == null)
            {
                var rng = SensorRealism.Rng(sensorName, 2);
                _senseA = new SenseChannel(SensorRealism.PhaseAmp, rng);
                _senseB = new SenseChannel(SensorRealism.PhaseAmp, rng);
                _senseBus = new SenseChannel(SensorRealism.BusVolt, rng);
                _senseT = new SenseChannel(SensorRealism.WindingC, rng);
            }
            if (!IsFoc)
            {
                // Realistic: a brushed ESC senses current and its bus, and
                // nothing measures torque.
                dest[offset] = noise.Apply(_voltage, dt, 0);
                dest[offset + 1] = noise.Apply(_current, dt, 1);
                dest[offset + 2] = _torque;
                if (Realistic)
                {
                    dest[offset + 1] = _senseA.Apply(dest[offset + 1]);
                    dest[offset + 2] = float.NaN;
                }
                return;
            }
            // What an FOC driver can measure: phase currents through its ADC
            // (Id should be 0 and reads as offset + noise), the speed from its
            // PLL, the bus, the winding temperature, and its fault bits.
            float vBus = BusVoltage > 0f ? BusVoltage : motor.maxVoltage;
            dest[offset] = noise.Apply(_iq, dt, 0);
            dest[offset + 1] = noise.Apply(0f, dt, 1);
            dest[offset + 2] = _omegaPll;
            dest[offset + 3] = noise.Apply(vBus, dt, 2);
            dest[offset + 4] = _tWinding + InjectedTempOffsetC;
            dest[offset + 5] = _fault;
            if (Realistic)
            {
                dest[offset] = _senseA.Apply(dest[offset]);
                dest[offset + 1] = _senseB.Apply(dest[offset + 1]);
                dest[offset + 3] = _senseBus.Apply(dest[offset + 3]);
                dest[offset + 4] = _senseT.Apply(dest[offset + 4]);
            }
        }
    }
}
