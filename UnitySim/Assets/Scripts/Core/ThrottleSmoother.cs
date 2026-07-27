using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Transmitter-style shaping for DIGITAL throttle input (keyboard keys), the
    /// direct twin of <see cref="SteerSmoother"/> — same Step signature, same
    /// Time.time integration, same toward-centre predicate.
    ///
    /// A real trigger or thumb stick is proportional; W is an instant step from
    /// nothing to full voltage. At RC scale that is most of what makes keyboard
    /// driving hard: the car is either coasting or at full power, so it spins up
    /// the rears out of every slow corner and there is no way to feed it in.
    ///
    /// Three rates rather than the steering pair, and the reasons differ:
    ///   * RISE is deliberately ~2.5× slower than steering's. Steering has to be
    ///     quick or the car will not go where you point it in time; throttle has
    ///     to NOT be quick, because being able to meter it is the entire point.
    ///   * RELEASE is fast, so lifting is immediate — a laggy lift would feel
    ///     like the brakes had gone soft.
    ///   * REVERSE rises faster than forward: reversing is a short deliberate
    ///     manoeuvre you commit to, not something you modulate through a corner.
    ///
    /// The toward-centre predicate is copied verbatim from SteerSmoother rather
    /// than reinvented, and that matters here: stabbing S from full throttle
    /// passes through zero at the RELEASE rate, so braking stays crisp instead of
    /// being delayed by the slow forward ramp.
    ///
    /// Analog inputs bypass this entirely — shaping a real trigger only removes
    /// fidelity — and firmware never reaches this path at all.
    /// </summary>
    public sealed class ThrottleSmoother
    {
        /// <summary>Forward rise at smoothing 1 (units/s): ~0.45 s to full.</summary>
        public const float RiseRate = 2.2f;
        /// <summary>Return-to-zero rate at smoothing 1 (units/s): ~0.17 s off.</summary>
        public const float ReleaseRate = 6.0f;
        /// <summary>Reverse rise at smoothing 1 (units/s): ~0.29 s to full.</summary>
        public const float ReverseRiseRate = 3.5f;

        private float _value;
        private float _lastTime = -1f;

        /// <summary>Advance the shaped axis toward <paramref name="target"/>
        /// (typically ±1 or 0).</summary>
        public float Step(float target, float now)
        {
            float amount = Mathf.Clamp01(Persistence.SettingsStore.Current.kbThrottleSmoothing);
            if (amount <= 0f)
            {
                _value = target;
                _lastTime = now;
                return _value;
            }

            float dt = _lastTime < 0f ? 0f : Mathf.Clamp(now - _lastTime, 0f, 0.1f);
            _lastTime = now;

            bool towardCenter = Mathf.Abs(target) < Mathf.Abs(_value) ||
                                (target != 0f && !Mathf.Approximately(Mathf.Sign(target), Mathf.Sign(_value)) && _value != 0f);
            float rate = towardCenter ? ReleaseRate
                                      : (target < 0f ? ReverseRiseRate : RiseRate);

            // Smoothing < 1 blends toward instant response by inflating the rate.
            rate /= Mathf.Max(amount, 0.01f);

            _value = Mathf.MoveTowards(_value, Mathf.Clamp(target, -1f, 1f), rate * dt);
            return _value;
        }

        /// <summary>Snap back to zero (respawn/teleport).</summary>
        public void Reset()
        {
            _value = 0f;
            _lastTime = -1f;
        }
    }
}
