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

        /// <summary>Per-player device routing; defaults to the classic merged input.</summary>
        public IDriverInputSource source;

        private readonly float[] _setpoints = new float[4];
        private float _mouseSteer;

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
                if (source.RespawnPressed())
                {
                    car.ResetVehicle();            // back to the starting location
                    if (lapTimer != null) lapTimer.ResetTimer(car); // only this car's laps
                }
            }

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

        private float CurrentSteer()
        {
            float steer = source.Steer();
            if (enableMouseSteer) steer = Mathf.Clamp(steer + _mouseSteer, -1f, 1f);
            return steer;
        }

        public void ReadManualCommands(float[] actuatorOut)
        {
            // Manual "just moves the car": throttle drives every motor at full-scale
            // voltage through the same DC model, so Manual and Autonomous share the
            // exact drivetrain physics. The human never types volts.
            float throttle = source.Throttle();
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
