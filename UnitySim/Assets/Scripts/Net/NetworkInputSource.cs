using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Host-side driving input for a REMOTE player's car: values arrive via
    /// "aihw.input" messages and are consumed by the normal CarInput/runner
    /// pipeline through the <see cref="IDriverInputSource"/> seam. Inputs zero
    /// out during the race countdown and after 0.5 s of silence (dead-man brake
    /// on disconnect/lag); the respawn flag is edge-latched until consumed.
    /// </summary>
    public sealed class NetworkInputSource : IDriverInputSource
    {
        private const float StaleAfter = 0.5f;

        private InputState _last;
        private float _receivedAt = -999f;
        private bool _respawnLatch;

        public void Receive(in InputState s)
        {
            _last = s;
            _receivedAt = Time.unscaledTime;
            if (s.respawnEdge) _respawnLatch = true;
        }

        private bool Live =>
            Time.unscaledTime - _receivedAt < StaleAfter &&
            (NetSession.Instance == null || !NetSession.Instance.InputsFrozen);

        public float Throttle() => Live ? _last.throttle : 0f;
        public float Steer() => Live ? _last.steer : 0f;
        public float Brake() => Live ? _last.brake : (Time.unscaledTime - _receivedAt >= StaleAfter ? 1f : 0f);
        public bool Handbrake() => Live && _last.handbrake;
        public float MouseSteerDelta() => 0f;

        public bool RespawnPressed()
        {
            if (!_respawnLatch || (NetSession.Instance != null && NetSession.Instance.InputsFrozen))
                return false;
            _respawnLatch = false;
            return true;
        }
    }

    /// <summary>
    /// Wraps any local input source and zeroes it while the session's inputs are
    /// frozen (race countdown) — the host's own input and each client's local
    /// sampler get the same treatment as the networked sources.
    /// </summary>
    public sealed class GatedInputSource : IDriverInputSource
    {
        private readonly IDriverInputSource _inner;

        public GatedInputSource(IDriverInputSource inner) => _inner = inner;

        private static bool Frozen => NetSession.Instance != null && NetSession.Instance.InputsFrozen;

        public float Throttle() => Frozen ? 0f : _inner.Throttle();
        public float Steer() => Frozen ? 0f : _inner.Steer();
        public float Brake() => Frozen ? 1f : _inner.Brake();
        public bool Handbrake() => !Frozen && _inner.Handbrake();
        public bool RespawnPressed() => !Frozen && _inner.RespawnPressed();
        public float MouseSteerDelta() => Frozen ? 0f : _inner.MouseSteerDelta();
    }
}
