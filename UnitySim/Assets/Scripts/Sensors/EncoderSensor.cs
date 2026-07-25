using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Wheel encoder: reports angular velocity (rad/s) with configurable noise.
    /// Ground truth comes from the motor model's angular velocity.
    /// </summary>
    [System.Serializable]
    public class EncoderSensor
    {
        public NoiseModel noise = new NoiseModel();

        public float Read(float trueAngularVelocity)
        {
            return noise.Apply(trueAngularVelocity);
        }
    }
}
