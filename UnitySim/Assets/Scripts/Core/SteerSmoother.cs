using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Transmitter-style shaping for DIGITAL steering input (keyboard keys).
    /// A real RC transmitter is a proportional stick; binary keys command an
    /// instant full-lock step, which at RC scale (short wheelbase, fast servo)
    /// feels twitchy. This ramps the digital axis toward its target — slower
    /// rising toward lock, faster returning to center — while analog sticks
    /// bypass it entirely.
    ///
    /// Integrates against Time.time so it behaves identically whether polled
    /// per frame or per control step; a repeated same-tick call is a no-op.
    /// The smoothing amount is scaled by <see cref="GameSettings.kbSteerSmoothing"/>
    /// (0 = passthrough, 1 = full shaping).
    /// </summary>
    public sealed class SteerSmoother
    {
        /// <summary>Full-lock rise rate at smoothing 1 (units/s): ~0.18 s to full.</summary>
        public const float RiseRate = 5.5f;
        /// <summary>Return-to-center rate at smoothing 1 (units/s): ~0.07 s back.</summary>
        public const float ReleaseRate = 14f;

        private float _value;
        private float _lastTime = -1f;

        /// <summary>Advance the shaped axis toward <paramref name="target"/> (typically ±1 or 0).</summary>
        public float Step(float target, float now)
        {
            float amount = Mathf.Clamp01(Persistence.SettingsStore.Current.kbSteerSmoothing);
            if (amount <= 0f)
            {
                _value = target;
                _lastTime = now;
                return _value;
            }

            float dt = _lastTime < 0f ? 0f : Mathf.Clamp(now - _lastTime, 0f, 0.1f);
            _lastTime = now;

            // Moving away from center (or reversing through it) uses the rise
            // rate; easing back toward center releases faster so taps stay crisp.
            bool towardCenter = Mathf.Abs(target) < Mathf.Abs(_value) ||
                                (target != 0f && !Mathf.Approximately(Mathf.Sign(target), Mathf.Sign(_value)) && _value != 0f);
            float rate = towardCenter ? ReleaseRate : RiseRate;

            // Smoothing < 1 blends toward instant response by inflating the rate.
            rate /= Mathf.Max(amount, 0.01f);

            _value = Mathf.MoveTowards(_value, Mathf.Clamp(target, -1f, 1f), rate * dt);
            return _value;
        }

        /// <summary>Snap back to center (respawn/teleport).</summary>
        public void Reset()
        {
            _value = 0f;
            _lastTime = -1f;
        }
    }
}
