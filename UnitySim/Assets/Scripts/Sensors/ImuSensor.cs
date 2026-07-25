using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Strap-down IMU: reports body-frame angular rate (rad/s) and specific
    /// force (m/s^2, i.e. accelerometer including reaction to gravity).
    /// Ground truth is derived from the vehicle Rigidbody each sample.
    /// </summary>
    [System.Serializable]
    public class ImuSensor
    {
        public NoiseModel gyroNoise = new NoiseModel();
        public NoiseModel accelNoise = new NoiseModel();

        private Vector3 _prevWorldVel;
        private bool _hasPrev;

        public void Reset()
        {
            _prevWorldVel = Vector3.zero;
            _hasPrev = false;
        }

        /// <summary>Sample the IMU from the vehicle body (no vibration injection).</summary>
        public void Read(Rigidbody body, float dt, out Vector3 gyro, out Vector3 accel) =>
            Read(body, dt, Vector3.zero, Vector3.zero, out gyro, out accel);

        /// <summary>
        /// Sample the IMU from the vehicle body. vibGyro/vibAccel are body-frame
        /// vibration terms (e.g. motor imbalance, supplied by the vehicle) added
        /// to the true signals BEFORE the noise model — like a real chassis
        /// shaking the physical sensor die.
        /// </summary>
        public void Read(Rigidbody body, float dt, Vector3 vibGyro, Vector3 vibAccel,
                         out Vector3 gyro, out Vector3 accel)
        {
            // Angular velocity is reported in the body frame.
            Vector3 bodyAngVel = body.transform.InverseTransformDirection(body.angularVelocity);

            // Linear acceleration in world frame from finite difference.
            Vector3 worldVel = body.linearVelocity;
            Vector3 worldAcc = Vector3.zero;
            if (_hasPrev && dt > 1e-6f)
                worldAcc = (worldVel - _prevWorldVel) / dt;
            _prevWorldVel = worldVel;
            _hasPrev = true;

            // Specific force = acceleration minus gravity, expressed in body frame.
            Vector3 specificForceWorld = worldAcc - Physics.gravity;
            Vector3 bodyAccel = body.transform.InverseTransformDirection(specificForceWorld);

            bodyAngVel += vibGyro;
            bodyAccel += vibAccel;

            gyro = new Vector3(
                gyroNoise.Apply(bodyAngVel.x),
                gyroNoise.Apply(bodyAngVel.y),
                gyroNoise.Apply(bodyAngVel.z));

            accel = new Vector3(
                accelNoise.Apply(bodyAccel.x),
                accelNoise.Apply(bodyAccel.y),
                accelNoise.Apply(bodyAccel.z));
        }
    }
}
