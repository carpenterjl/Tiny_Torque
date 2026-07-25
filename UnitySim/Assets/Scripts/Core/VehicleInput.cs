using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Operator input for the differential-drive robot.
    ///
    /// Autonomous (ISetpointSource): forward velocity + yaw-rate setpoints the
    /// C controller tracks. Manual (IManualDriver): direct left/right motor
    /// commands mixed from throttle and steer. Reads via <see cref="InputReader"/>
    /// so keyboard and gamepad both work regardless of input backend.
    /// </summary>
    public sealed class VehicleInput : MonoBehaviour, IManualDriver, ISetpointSource
    {
        public float maxLinear = 3.0f;   // m/s at full stick
        public float maxYawRate = 2.5f;  // rad/s at full stick

        private readonly float[] _setpoints = new float[4];

        public float[] Setpoints => _setpoints;

        private void Update()
        {
            _setpoints[0] = InputReader.Throttle() * maxLinear;
            _setpoints[1] = InputReader.Steer() * maxYawRate;
        }

        public void ReadManualCommands(float[] actuatorOut)
        {
            float throttle = InputReader.Throttle();
            float steer = InputReader.Steer();
            actuatorOut[0] = Mathf.Clamp(throttle + steer, -1f, 1f); // left
            actuatorOut[1] = Mathf.Clamp(throttle - steer, -1f, 1f); // right
        }
    }
}
