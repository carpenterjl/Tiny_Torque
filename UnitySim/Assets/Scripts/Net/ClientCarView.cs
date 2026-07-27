using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Client-side ghost car: a kinematic visual (built via VehicleFactory with
    /// previewKinematic) posed from the host's 60 Hz state stream. Renders
    /// ~60 ms behind the estimated host clock, interpolating between buffered
    /// snapshots (short velocity extrapolation when the buffer runs dry) and
    /// hard-snapping when an epoch changes — the global one for scene-wide
    /// events, the per-car one for that car's own teleports (respawn, wreck
    /// recovery, grid). Wheel visuals spin/steer from the streamed wheel speed
    /// and steer angle.
    ///
    /// Since protocol 4 this renders OTHER players only — your own car is
    /// simulated locally and has no interpolation delay at all.
    /// </summary>
    public sealed class ClientCarView : MonoBehaviour
    {
        // One 60 Hz interval is 16.7 ms; 60 ms absorbs a dropped packet
        // (two intervals) plus LAN jitter plus a render frame. Longer than that
        // is delay nobody asked for.
        private const float RenderDelay = 0.06f;
        private const float MaxExtrapolate = 0.1f;
        // After a dry stretch the true pose is somewhere else. Easing back over
        // this window hides the step that snapping straight to it would show.
        private const float BlendBack = 0.1f;

        public int slot;
        public CarVehicle car;

        private struct Snap
        {
            public float hostTime;
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 vel;
            public Vector3 angVel;
            public float steerDeg;
            public float wheelRadPerSec;
        }

        private readonly List<Snap> _buffer = new List<Snap>(32);
        private byte _epoch;
        private byte _carEpoch;
        private bool _hasEpoch;
        private float _clockOffset;   // smoothed (hostTime - localTime)
        private bool _hasOffset;
        private float _wheelSpin;     // accumulated wheel roll (rad)
        private float _curSteer, _curWheelSpeed;
        private Audio.VehicleAudio _audio;

        // Extrapolation carry-over: where we were when the stream ran dry, and
        // how much of the blend back to the interpolated truth is left.
        private Vector3 _blendPos;
        private Quaternion _blendRot = Quaternion.identity;
        private float _blendLeft;
        private bool _wasExtrapolating;

        private void Start()
        {
            // Ghosts have no drivetrain to listen to — they are kinematic and
            // never run StepPhysics — so the audio is driven from the streamed
            // speed estimate instead. Passing a null car selects that path.
            _audio = Audio.VehicleAudio.Attach(gameObject, null);
        }

        public void Receive(byte epoch, float hostTime, in CarState s)
        {
            // Two independent snap triggers: the scene changed under everyone
            // (map load, rebuild), or this one car jumped (respawn, wreck
            // recovery, grid). Either way the history is about a pose that no
            // longer connects to this one, so lerping through it would draw a
            // streak across the map.
            if (!_hasEpoch || epoch != _epoch || s.carEpoch != _carEpoch)
            {
                _buffer.Clear();
                _epoch = epoch;
                _carEpoch = s.carEpoch;
                _hasEpoch = true;
                _blendLeft = 0f;
                _wasExtrapolating = false;
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
                angVel = s.angVel,
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
                Apply(_buffer[0], _buffer[0].pos, _buffer[0].rot);
            }
            else if (hi >= _buffer.Count)
            {
                // Buffer dry: carry on along the last known motion. Rotation
                // matters as much as position here — a car mid-corner that
                // freezes its heading while sliding forward reads as a glitch,
                // not as latency.
                var last = _buffer[_buffer.Count - 1];
                float over = Mathf.Min(MaxExtrapolate, renderTime - last.hostTime);
                Vector3 pos = last.pos + last.vel * over;
                Quaternion rot = IntegrateRotation(last.rot, last.angVel, over);
                Apply(last, pos, rot);
                _blendPos = pos;
                _blendRot = rot;
                _wasExtrapolating = true;
            }
            else
            {
                var a = _buffer[hi - 1];
                var b = _buffer[hi];
                float span = Mathf.Max(0.0001f, b.hostTime - a.hostTime);
                float t = Mathf.Clamp01((renderTime - a.hostTime) / span);
                Vector3 pos = Vector3.Lerp(a.pos, b.pos, t);
                Quaternion rot = Quaternion.Slerp(a.rot, b.rot, t);

                // Coming out of a dry stretch the guess and the truth disagree.
                // Ease across the gap instead of stepping to it.
                if (_wasExtrapolating)
                {
                    _wasExtrapolating = false;
                    _blendLeft = BlendBack;
                }
                if (_blendLeft > 0f)
                {
                    _blendLeft = Mathf.Max(0f, _blendLeft - Time.deltaTime);
                    float k = 1f - _blendLeft / BlendBack;   // 0 → 1 across the window
                    pos = Vector3.Lerp(_blendPos, pos, k);
                    rot = Quaternion.Slerp(_blendRot, rot, k);
                    _blendPos = pos;
                    _blendRot = rot;
                }

                transform.SetPositionAndRotation(pos, rot);
                _curSteer = Mathf.Lerp(a.steerDeg, b.steerDeg, t);
                _curWheelSpeed = Mathf.Lerp(a.wheelRadPerSec, b.wheelRadPerSec, t);
            }

            UpdateWheelVisuals();

            // Trim consumed history (keep one snapshot before renderTime).
            while (_buffer.Count > 2 && _buffer[1].hostTime < renderTime)
                _buffer.RemoveAt(0);
        }

        private void Apply(in Snap s, Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
            _curSteer = s.steerDeg;
            _curWheelSpeed = s.wheelRadPerSec;
        }

        /// <summary>Spin a rotation forward by an angular velocity (rad/s) for dt.</summary>
        private static Quaternion IntegrateRotation(Quaternion rot, Vector3 angVel, float dt)
        {
            float mag = angVel.magnitude;
            if (mag < 1e-4f || dt <= 0f) return rot;
            return Quaternion.AngleAxis(mag * Mathf.Rad2Deg * dt, angVel / mag) * rot;
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
