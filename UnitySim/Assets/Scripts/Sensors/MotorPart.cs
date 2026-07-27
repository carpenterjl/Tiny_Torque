using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// A driven brushed-DC motor mounted on one wheel. It is both an actuator and
    /// a feedback sensor:
    ///
    ///  - Actuator: each physics step <see cref="StepDrive"/> takes the latched
    ///    commanded voltage, runs the <see cref="MotorModel"/> against the wheel's
    ///    current speed, and applies the resulting torque to the WheelCollider.
    ///    The torque/current therefore emerge from the vehicle dynamics.
    ///  - Sensor: it stays a <see cref="SensorComponent"/> (SENSOR_MOTOR) so the
    ///    rig publishes its measured voltage / current / torque and the controller
    ///    learns its actuator slot + voltage limits from the manifest.
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

        private CarVehicle _vehicle;
        private WheelCollider _wheel;

        // Latched command (volts) + cached electrical operating point for feedback.
        private float _commandedVoltage;
        private float _voltage, _current, _torque;

        // ESC pipeline state (slew-limited then first-order-lagged output voltage).
        private float _vSlew, _vFilt;

        // Drive/brake/reverse state (i22): neutral dwell timer, reverse arming,
        // and whether the last step was a shorted-winding brake (whose current
        // circulates in the bridge and draws nothing from the pack).
        private float _neutralTime;
        private bool _reverseArmed = true;   // armed at power-on in neutral
        private bool _braking;

        /// <summary>
        /// Dev A/B switch (mirrors TyreModel.Enabled): false restores the legacy
        /// smooth signed-voltage model with no brake/reverse behaviour.
        /// </summary>
        public static bool StateMachineEnabled = true;

        /// <summary>Actuator slot this motor reads from (assigned by the rig).</summary>
        public int ActuatorIndex { get; set; } = -1;
        public float MaxVoltage => motor.maxVoltage;
        /// <summary>Electrical current at the last StepDrive (A, signed).</summary>
        public float Current => _current;

        /// <summary>Current actually drawn from the pack (A, ≥0). During a
        /// shorted-winding ESC brake the current circulates inside the bridge
        /// and the pack sees none of it.</summary>
        public float PackCurrent => _braking ? 0f : Mathf.Abs(_current);

        /// <summary>
        /// Would a reverse command engage right now, or would the ESC hold
        /// neutral waiting out its lockout?
        ///
        /// Read-only, read by nothing inside this class — the state machine in
        /// <see cref="StepDrive"/> is byte-identical and stays that way, because
        /// the Opus mission's brake calibration depends on it. It exists purely
        /// so <c>CarInput</c> can tell three states apart that look identical
        /// from outside: "commanded reverse and the ESC is stalling", "reversing
        /// into a wall", and "reverse already engaged at 0.05 m/s". Any heuristic
        /// without this signal confuses them and introduces a subtler bug than
        /// the one it fixes.
        ///
        /// True when the state machine is off: there is no lockout to wait for.
        /// </summary>
        public bool ReverseReady => !StateMachineEnabled || _reverseArmed;

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

        /// <summary>Motor shaft speed (rad/s, signed) — for vibration modeling.</summary>
        public float MotorOmega =>
            _vehicle != null ? _vehicle.WheelOmega(wheelIndex) * motor.gearRatio : 0f;

        public override SensorType Type => SensorType.Motor;
        public override int DataCount => 3;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            _wheel = vehicle != null ? vehicle.GetWheel(wheelIndex) : null;
            rangeMin = -motor.maxVoltage;
            rangeMax = motor.maxVoltage;
        }

        /// <summary>Latch the commanded voltage (control rate, zero-order hold).</summary>
        public void SetVoltage(float volts) => _commandedVoltage = volts;

        public void ResetMotor()
        {
            _commandedVoltage = _voltage = _current = _torque = 0f;
            _vSlew = _vFilt = 0f;
            _neutralTime = 0f;
            _reverseArmed = true;   // fresh power-on in neutral
            _braking = false;
        }

        /// <summary>
        /// Apply the DC model for one physics step and drive the WheelCollider.
        /// Returns the geared wheel torque applied.
        /// </summary>
        public float StepDrive(float dt)
        {
            if (_wheel == null && _vehicle != null) _wheel = _vehicle.GetWheel(wheelIndex);
            if (_wheel == null || _vehicle == null) return 0f;

            // Wheel speed from the vehicle's canonical source (the brush-model
            // integrator, or wc.rpm on the legacy path) — back-EMF must see the
            // same ω the encoders and the tyre forces see.
            float wheelOmega = _vehicle.WheelOmega(wheelIndex);

            // ESC pipeline: deadband → PWM quantization → slew limit → first-order
            // lag. Every stage is off (passthrough) at its 0 default, so old designs
            // keep the legacy instant-voltage behaviour byte-for-byte.
            float v = _commandedVoltage;
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
            // acts at neutral. Forward drive is the unchanged DC model, so top
            // speed and the garage stats are unaffected.
            _braking = false;
            if (StateMachineEnabled)
            {
                float dead = Mathf.Max(0.02f, motor.escDeadbandV);
                float lockS = (motor.escReverseLockMs > 0f ? motor.escReverseLockMs : 150f) * 0.001f;
                float strength = motor.escBrakeStrengthPct > 0f
                    ? Mathf.Clamp01(motor.escBrakeStrengthPct / 100f) : 1f;
                bool moving = Mathf.Abs(wheelOmega) > 2f;   // ≈0.07 m/s at r = 33 mm

                if (Mathf.Abs(v) < dead)
                {
                    // Neutral: dwell (at rest) arms reverse; drag brake if configured.
                    _neutralTime += dt;
                    if (_neutralTime >= lockS && !moving) _reverseArmed = true;
                    if (motor.escDragBrakePct > 0f && moving)
                    {
                        _braking = true;
                        _torque = MotorModel.BrakeTorque(in motor,
                            motor.escDragBrakePct / 100f, wheelOmega, out _current);
                        _voltage = 0f;
                    }
                    else
                        _torque = MotorModel.WheelTorque(in motor, 0f, wheelOmega,
                                                         out _voltage, out _current);
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
                        _torque = MotorModel.BrakeTorque(in motor, duty, wheelOmega, out _current);
                        _voltage = 0f;
                    }
                    else if (v > 0f || _reverseArmed || wheelOmega < -2f)
                    {
                        if (v > 0f) _reverseArmed = false;   // next reverse needs a dwell
                        _torque = MotorModel.WheelTorque(in motor, v, wheelOmega,
                                                         out _voltage, out _current);
                    }
                    else
                    {
                        // Wants reverse before the lockout expires: ESC holds neutral.
                        _torque = MotorModel.WheelTorque(in motor, 0f, wheelOmega,
                                                         out _voltage, out _current);
                    }
                }
            }
            else
            {
                _torque = MotorModel.WheelTorque(in motor, v, wheelOmega,
                                                 out _voltage, out _current);
            }

            // Torque sink: the vehicle routes it to its spin integrator (brush
            // path) or to WheelCollider.motorTorque (legacy path).
            _vehicle.ApplyDriveTorque(wheelIndex, _torque);
            return _torque;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            dest[offset] = noise.Apply(_voltage);
            dest[offset + 1] = noise.Apply(_current);
            dest[offset + 2] = _torque;
        }
    }
}
