using System.Collections.Generic;
using AIHWSim.Sensors.Signals;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Makes every built car audible to world microphones: an
    /// <see cref="ISoundEmitter"/> whose loudness follows total motor current
    /// and whose tone follows the primary motor's shaft speed. Attached
    /// unconditionally by VehicleFactory — silent at rest, so old designs and
    /// parked cars are unaffected. Reads the motors, never writes anything;
    /// what the PLAYER hears stays VehicleAudio's business.
    /// </summary>
    public sealed class VehicleSoundEmitter : MonoBehaviour, ISoundEmitter
    {
        [Tooltip("Loudness (linear amplitude at 1 m) contributed per amp of total motor current.")]
        public float loudnessPerAmp = 0.15f;

        private readonly List<MotorPart> _motors = new List<MotorPart>();

        /// <summary>Called after SensorRig.Initialize so the motor list is final.</summary>
        public void BindMotors(IReadOnlyList<MotorPart> motors)
        {
            _motors.Clear();
            if (motors != null)
                for (int i = 0; i < motors.Count; i++)
                    if (motors[i] != null) _motors.Add(motors[i]);
        }

        // ---- ISoundEmitter -------------------------------------------------

        public bool SoundActive => isActiveAndEnabled;
        public Vector3 SoundPosition => transform.position;
        public int SoundEmitterId { get; set; }

        public float Loudness
        {
            get
            {
                float amps = 0f;
                for (int i = 0; i < _motors.Count; i++)
                    amps += _motors[i].PackCurrent;
                return amps * loudnessPerAmp;
            }
        }

        public float ToneHz
        {
            get
            {
                // Dominant tone: the fastest-spinning motor's shaft frequency.
                float best = 0f;
                for (int i = 0; i < _motors.Count; i++)
                {
                    float hz = Mathf.Abs(_motors[i].MotorOmega) / (2f * Mathf.PI);
                    if (hz > best) best = hz;
                }
                return best;
            }
        }

        private void OnEnable() => SoundField.Register(this);
        private void OnDisable() => SoundField.Unregister(this);
    }
}
