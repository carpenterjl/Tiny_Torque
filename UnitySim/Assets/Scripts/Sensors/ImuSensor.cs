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

        /// <summary>Mount misalignment: the die's axes in the body, FLU roll,
        /// pitch, yaw in degrees (§9.2). Zero = aligned. A pitch of 2° puts
        /// 0.34 m/s² of gravity into the forward accelerometer and 3.5 % of
        /// the yaw rate into the roll gyro, which is what it is for.</summary>
        public Vector3 mountDeg;

        private Vector3 _prevWorldVel;
        private bool _hasPrev;
        private Vector3 _mountCached = new Vector3(float.NaN, 0f, 0f);
        private readonly float[] _mt = new float[9];   // R^T in FLU: sensed = R^T truth

        /// <summary>A body-frame vector (Unity axes) as the misaligned die senses it.</summary>
        private Vector3 Misalign(Vector3 v)
        {
            if (mountDeg == Vector3.zero) return v;
            if (mountDeg != _mountCached)
            {
                float r = mountDeg.x * Mathf.Deg2Rad, p = mountDeg.y * Mathf.Deg2Rad, yw = mountDeg.z * Mathf.Deg2Rad;
                float cr = Mathf.Cos(r), sr = Mathf.Sin(r), cp = Mathf.Cos(p), sp = Mathf.Sin(p), cy = Mathf.Cos(yw), sy = Mathf.Sin(yw);
                // R = Rz(yaw) Ry(pitch) Rx(roll), the die's axes in the body (FLU).
                float r00 = cy * cp, r01 = cy * sp * sr - sy * cr, r02 = cy * sp * cr + sy * sr;
                float r10 = sy * cp, r11 = sy * sp * sr + cy * cr, r12 = sy * sp * cr - cy * sr;
                float r20 = -sp,     r21 = cp * sr,                r22 = cp * cr;
                _mt[0] = r00; _mt[1] = r10; _mt[2] = r20;
                _mt[3] = r01; _mt[4] = r11; _mt[5] = r21;
                _mt[6] = r02; _mt[7] = r12; _mt[8] = r22;
                _mountCached = mountDeg;
            }
            // Unity body (x right, y up, z forward) -> FLU (x forward, y left, z up) and back.
            float fx = v.z, fy = -v.x, fz = v.y;
            float ox = _mt[0] * fx + _mt[1] * fy + _mt[2] * fz;
            float oy = _mt[3] * fx + _mt[4] * fy + _mt[5] * fz;
            float oz = _mt[6] * fx + _mt[7] * fy + _mt[8] * fz;
            return new Vector3(-oy, oz, ox);
        }

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

            // The die's own axes (§9.2 misalignment), then what shakes it.
            bodyAngVel = Misalign(bodyAngVel) + vibGyro;
            bodyAccel = Misalign(bodyAccel) + vibAccel;

            // dt reaches the noise model now, so bias drift actually runs
            // (it was dead code without it, BUG-08) and each axis walks on its
            // own channel instead of all three sharing one walk (SEN-07).
            gyro = new Vector3(
                gyroNoise.Apply(bodyAngVel.x, dt, 0),
                gyroNoise.Apply(bodyAngVel.y, dt, 1),
                gyroNoise.Apply(bodyAngVel.z, dt, 2));

            accel = new Vector3(
                accelNoise.Apply(bodyAccel.x, dt, 0),
                accelNoise.Apply(bodyAccel.y, dt, 1),
                accelNoise.Apply(bodyAccel.z, dt, 2));
        }
    }
}
