using UnityEngine;

namespace AIHWSim.Actuators
{
    /// <summary>
    /// Simple geared DC-motor + wheel model.
    ///
    /// A normalized command in [-1, 1] maps to a target wheel angular velocity;
    /// the wheel speed follows it through a first-order lag (the lumped
    /// electrical + mechanical time constant). A load-dependent droop term
    /// pulls the achievable speed down as the wheel is asked to push harder,
    /// approximating a torque/speed curve without a stiff current loop.
    ///
    /// State (angular velocity) is what a wheel encoder measures, so this is the
    /// ground truth the EncoderSensor samples.
    /// </summary>
    [System.Serializable]
    public class DcMotorModel
    {
        [Tooltip("No-load wheel speed at full command (rad/s).")]
        public float maxSpeedRadS = 60f;

        [Tooltip("First-order response time constant (s). Lower = snappier.")]
        public float timeConstant = 0.06f;

        [Tooltip("Speed droop per unit reaction torque (rad/s per N·m). Models a soft torque/speed curve.")]
        public float loadDroop = 4f;

        /// <summary>Current wheel angular velocity (rad/s). This is 'ground truth'.</summary>
        public float AngularVelocity { get; private set; }

        /// <summary>Last applied normalized command, clamped to [-1, 1].</summary>
        public float Command { get; private set; }

        public void Reset()
        {
            AngularVelocity = 0f;
            Command = 0f;
        }

        /// <summary>
        /// Advance the motor state.
        /// </summary>
        /// <param name="command">Normalized command in [-1, 1].</param>
        /// <param name="reactionTorque">Magnitude of load torque opposing motion (N·m).</param>
        /// <param name="dt">Timestep (s).</param>
        public void Step(float command, float reactionTorque, float dt)
        {
            Command = Mathf.Clamp(command, -1f, 1f);

            float target = Command * maxSpeedRadS;
            // Droop always reduces the magnitude of the achievable target.
            float droop = loadDroop * Mathf.Max(0f, reactionTorque);
            float mag = Mathf.Max(0f, Mathf.Abs(target) - droop);
            target = Mathf.Sign(target) * mag;

            float alpha = (timeConstant > 1e-5f) ? (dt / timeConstant) : 1f;
            alpha = Mathf.Clamp01(alpha);
            AngularVelocity += (target - AngularVelocity) * alpha;
        }
    }
}
