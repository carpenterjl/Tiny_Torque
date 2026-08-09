using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// Raw-level takeover: writes the runner's actuator vector directly instead
    /// of going through <c>CarInput</c>'s throttle/steer/brake shaping. This is
    /// what a firmware-style control loop in the external application wants —
    /// motor volts per motor, a steer command, a brake command, with nothing in
    /// between adding a steering rate limit or an assist.
    ///
    /// Installed as the runner's <c>inputBehaviour</c> by
    /// <see cref="IpcVehicleRegistry.Acquire"/> and removed on release. It stands
    /// in for <c>CarInput</c> in that one role only; CarInput stays on the car and
    /// keeps doing its frame-rate jobs (handbrake, respawn, horn), which is why
    /// the acquire also displaces CarInput's input source — otherwise the local
    /// gamepad could still yank the handbrake on a car under external control.
    ///
    /// Actuator layout, from <c>CarVehicle</c>: index 0..N-1 are motor volts at
    /// each <c>MotorPart.ActuatorIndex</c>, [6] is steer -1..1, [7] is brake 0..1.
    /// The client is told each motor's index in <c>list_vehicles</c> rather than
    /// having to assume the order.
    /// </summary>
    public sealed class IpcActuatorDriver : MonoBehaviour, IManualDriver, ISetpointSource
    {
        /// <summary>The runner's actuator buffer is float[8]; a client that sends
        /// more is telling us something we cannot act on.</summary>
        public const int ActuatorCount = 8;

        private readonly float[] _actuators = new float[ActuatorCount];
        private readonly float[] _setpoints = new float[4];
        private CarVehicle _car;
        private float _staleAfter = IpcDriverSource.DefaultStaleAfter;
        private float _receivedAt = -999f;
        private bool _handbrake;

        public float[] Setpoints => _setpoints;

        public void Configure(CarVehicle car, float staleAfterSeconds)
        {
            _car = car;
            _staleAfter = staleAfterSeconds == 0f ? IpcDriverSource.DefaultStaleAfter : staleAfterSeconds;
            // A freshly installed driver has been sent nothing yet. Starting stale
            // means the car brakes until the first command rather than coasting on
            // an all-zero vector, which for this motor model is not neutral anyway.
            _receivedAt = -999f;
            System.Array.Clear(_actuators, 0, _actuators.Length);
            System.Array.Clear(_setpoints, 0, _setpoints.Length);
        }

        private bool Live => _staleAfter < 0f || Time.unscaledTime - _receivedAt < _staleAfter;

        public void Receive(ActuateMsg m)
        {
            System.Array.Clear(_actuators, 0, _actuators.Length);
            if (m.actuators != null)
            {
                int n = Mathf.Min(m.actuators.Length, ActuatorCount);
                for (int i = 0; i < n; i++) _actuators[i] = m.actuators[i];
            }

            System.Array.Clear(_setpoints, 0, _setpoints.Length);
            if (m.setpoints != null)
            {
                int n = Mathf.Min(m.setpoints.Length, _setpoints.Length);
                for (int i = 0; i < n; i++) _setpoints[i] = m.setpoints[i];
            }

            _handbrake = m.handbrake;
            _receivedAt = Time.unscaledTime;
        }

        /// <summary>
        /// Fill the runner's actuator buffer for this control tick. The runner
        /// clears the buffer before calling, so this writes rather than merges.
        ///
        /// On a dead-man timeout everything goes to zero except the brake, which
        /// goes full — the same failure posture the drive level takes, and the
        /// only one that is safe when the process on the other end has stopped
        /// answering.
        /// </summary>
        public void ReadManualCommands(float[] actuatorOut)
        {
            if (actuatorOut == null) return;

            if (!Live)
            {
                for (int i = 0; i < actuatorOut.Length; i++) actuatorOut[i] = 0f;
                if (actuatorOut.Length > 7) actuatorOut[7] = 1f;
                return;
            }

            int n = Mathf.Min(actuatorOut.Length, ActuatorCount);
            for (int i = 0; i < n; i++) actuatorOut[i] = _actuators[i];
        }

        /// <summary>Handbrake is not part of the actuator vector, so — exactly as
        /// <c>CarInput</c> does — it is applied straight to the car. At frame rate
        /// rather than control rate for the same reason CarInput does it there:
        /// it is a latch, not a continuous command.</summary>
        private void Update()
        {
            if (_car == null) return;
            _car.SetHandbrake(Live && _handbrake);
        }

        private void OnDisable()
        {
            // Releasing must not leave the car parked on its handbrake.
            if (_car != null) _car.SetHandbrake(false);
        }
    }
}
