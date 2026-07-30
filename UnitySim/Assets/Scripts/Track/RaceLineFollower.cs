using AIHWSim.Arcade;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Drives a car along a baked <see cref="RacingLineAsset"/>, tracking its speed
    /// profile. Used ONLY by the headless calibration run — nothing in the shipped
    /// game creates one, and <c>BotDriver</c> is untouched, so race behaviour is
    /// exactly what it was.
    ///
    /// The steering and throttle shapes are copied from BotDriver deliberately: the
    /// calibration is trying to measure what the car can do when driven the way the
    /// game drives it. A cleverer controller would measure a car nobody races.
    /// The one real difference is that it tracks the profile's per-node target
    /// speed instead of a constant divided by curvature.
    /// </summary>
    public sealed class RaceLineFollower : IDriverInputSource
    {
        private readonly CarVehicle _car;
        private readonly RacingLineAsset _line;
        private readonly TrackSpine _spine;
        private readonly float _lookAhead;
        private readonly float _lockDeg;

        private int _hint = -1;
        private float _throttle, _steer, _brake;
        private int _frame = -1;

        /// <summary>Distance along the line at the last update, for the caller's
        /// lap detection and for the stuck check.</summary>
        public float S { get; private set; }

        /// <summary>Target speed the profile asked for at the last update (m/s).</summary>
        public float TargetSpeed { get; private set; }

        public RaceLineFollower(CarVehicle car, RacingLineAsset line,
            float lookAhead = 1.1f, float lockDeg = 20f)
        {
            _car = car;
            _line = line;
            _lookAhead = lookAhead;
            _lockDeg = lockDeg;
            _spine = line != null && line.IsUsable
                ? TrackSpine.From(line.points, line.closed) : null;
        }

        public bool Ready => _spine != null && _car != null;

        private void Compute()
        {
            // Cached per frame, as BotDriver does: the four accessors below are
            // polled independently and must all describe the same instant.
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            if (!Ready) { _throttle = _steer = _brake = 0f; return; }

            var pos = _car.transform.position;
            S = _spine.Project(pos, ref _hint);

            TargetSpeed = SpeedAt(S);
            _spine.Sample(S + _lookAhead, out var aim, out _);

            var fwd = _car.transform.forward;
            fwd.y = 0f;
            var toAim = aim - pos;
            toAim.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f || toAim.sqrMagnitude < 1e-6f)
            {
                _steer = 0f;
            }
            else
            {
                float signed = Vector3.SignedAngle(fwd, toAim, Vector3.up);
                _steer = Mathf.Clamp(signed / _lockDeg, -1f, 1f);
            }

            // BotDriver's dead-banded speed controller, tracking the profile.
            float v = _car.ForwardSpeed;
            float err = TargetSpeed - v;
            if (err > 0.2f)
            {
                _throttle = Mathf.Clamp01(err / 2f) * (1f - 0.3f * Mathf.Abs(_steer));
                _brake = 0f;
            }
            else if (err < -0.5f)
            {
                _throttle = 0f;
                _brake = Mathf.Clamp01(-err / 3f);
            }
            else
            {
                _throttle = 0.1f;
                _brake = 0f;
            }
        }

        /// <summary>Profile speed at an arc length, by nearest node. The line is
        /// sampled every ~0.4 m, so interpolating between nodes would be finer than
        /// the data it came from.</summary>
        private float SpeedAt(float s)
        {
            int n = _line.points.Length;
            float total = Mathf.Max(1e-3f, _spine.TotalLength);
            float f = Mathf.Repeat(s, total) / total;
            int i = Mathf.Clamp(Mathf.RoundToInt(f * n), 0, n - 1);
            return _line.speed[i];
        }

        public float Throttle() { Compute(); return _throttle; }
        public float Steer() { Compute(); return _steer; }
        public float Brake() { Compute(); return _brake; }

        // A calibration lap must never teleport: a respawn silently replaces
        // measured physics with a scripted reset, and the fit would be of that.
        public bool RespawnPressed() => false;
        public bool Handbrake() => false;
        public bool UseItemPressed() => false;
        public bool LookBackHeld() => false;
        public bool HornHeld() => false;
        public bool JumpPressed() => false;
        public bool BoostHeld() => false;
        public float MouseSteerDelta() => 0f;
    }
}
