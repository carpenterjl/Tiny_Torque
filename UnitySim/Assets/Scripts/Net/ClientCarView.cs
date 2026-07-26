using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Client-side ghost car: a kinematic visual (built via VehicleFactory with
    /// previewKinematic) posed from the host's 30 Hz state stream. Renders
    /// ~120 ms behind the estimated host clock, interpolating between buffered
    /// snapshots (short velocity extrapolation when the buffer runs dry) and
    /// hard-snapping when the stream's epoch changes (teleports, grid starts).
    /// Wheel visuals spin/steer from the streamed wheel speed and steer angle.
    /// </summary>
    public sealed class ClientCarView : MonoBehaviour
    {
        private const float RenderDelay = 0.12f;
        private const float MaxExtrapolate = 0.1f;

        public int slot;
        public CarVehicle car;

        private struct Snap
        {
            public float hostTime;
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 vel;
            public float steerDeg;
            public float wheelRadPerSec;
        }

        private readonly List<Snap> _buffer = new List<Snap>(32);
        private byte _epoch;
        private bool _hasEpoch;
        private float _clockOffset;   // smoothed (hostTime - localTime)
        private bool _hasOffset;
        private float _wheelSpin;     // accumulated wheel roll (rad)
        private float _curSteer, _curWheelSpeed;
        private Audio.VehicleAudio _audio;

        private void Start()
        {
            // Ghosts have no drivetrain to listen to — they are kinematic and
            // never run StepPhysics — so the audio is driven from the streamed
            // speed estimate instead. Passing a null car selects that path.
            _audio = Audio.VehicleAudio.Attach(gameObject, null);
        }

        public void Receive(byte epoch, float hostTime, in CarState s)
        {
            if (!_hasEpoch || epoch != _epoch)
            {
                // Teleport (race grid, respawn, map change): drop history, snap.
                _buffer.Clear();
                _epoch = epoch;
                _hasEpoch = true;
                transform.SetPositionAndRotation(s.pos, s.rot);
            }

            float offset = hostTime - Time.unscaledTime;
            _clockOffset = _hasOffset ? Mathf.Lerp(_clockOffset, offset, 0.1f) : offset;
            _hasOffset = true;

            _buffer.Add(new Snap
            {
                hostTime = hostTime,
                pos = s.pos,
                rot = s.rot,
                vel = s.vel,
                steerDeg = s.steerDeg,
                wheelRadPerSec = s.wheelRadPerSec,
            });
            if (_buffer.Count > 30) _buffer.RemoveAt(0);
        }

        private void Update()
        {
            if (_audio != null) _audio.externalSpeed = SpeedEstimate;
            if (_buffer.Count == 0) return;
            float renderTime = Time.unscaledTime + _clockOffset - RenderDelay;

            // Find the two snapshots bracketing renderTime.
            int hi = 0;
            while (hi < _buffer.Count && _buffer[hi].hostTime < renderTime) hi++;

            if (hi == 0)
            {
                Apply(_buffer[0], _buffer[0].pos);
            }
            else if (hi >= _buffer.Count)
            {
                // Buffer dry: extrapolate briefly along the last velocity.
                var last = _buffer[_buffer.Count - 1];
                float over = Mathf.Min(MaxExtrapolate, renderTime - last.hostTime);
                Apply(last, last.pos + last.vel * over);
            }
            else
            {
                var a = _buffer[hi - 1];
                var b = _buffer[hi];
                float span = Mathf.Max(0.0001f, b.hostTime - a.hostTime);
                float t = Mathf.Clamp01((renderTime - a.hostTime) / span);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(a.pos, b.pos, t),
                    Quaternion.Slerp(a.rot, b.rot, t));
                _curSteer = Mathf.Lerp(a.steerDeg, b.steerDeg, t);
                _curWheelSpeed = Mathf.Lerp(a.wheelRadPerSec, b.wheelRadPerSec, t);
            }

            UpdateWheelVisuals();

            // Trim consumed history (keep one snapshot before renderTime).
            while (_buffer.Count > 2 && _buffer[1].hostTime < renderTime)
                _buffer.RemoveAt(0);
        }

        private void Apply(in Snap s, Vector3 pos)
        {
            transform.SetPositionAndRotation(pos, s.rot);
            _curSteer = s.steerDeg;
            _curWheelSpeed = s.wheelRadPerSec;
        }

        private void UpdateWheelVisuals()
        {
            if (car == null) return;
            _wheelSpin += _curWheelSpeed * Time.deltaTime;
            for (int i = 0; i < car.WheelCount; i++)
            {
                var viz = car.GetWheelVisual(i);
                var wt = car.GetWheelTransform(i);
                if (viz == null || wt == null) continue;
                // Steer yaw on steering wheels + roll around the axle.
                bool steers = car.WheelAllowsSteering(i);
                viz.localRotation =
                    Quaternion.Euler(0f, steers ? _curSteer : 0f, 0f) *
                    Quaternion.Euler(Mathf.Rad2Deg * _wheelSpin, 0f, 0f);
            }
        }

        /// <summary>Own-car velocity estimate for the client HUD's speed readout.</summary>
        public float SpeedEstimate
        {
            get
            {
                if (_buffer.Count == 0) return 0f;
                return _buffer[_buffer.Count - 1].vel.magnitude;
            }
        }
    }
}
