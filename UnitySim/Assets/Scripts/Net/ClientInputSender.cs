using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Client-side input pump: samples the local devices (merged keyboard +
    /// gamepad, countdown-gated) every frame and streams the latest values to
    /// the host at 60 Hz.
    ///
    /// Since protocol 4 the client drives its own car directly, so this stream
    /// no longer steers anything — it carries the use-item and respawn edges the
    /// host adjudicates, and its silence is the dead-man signal
    /// (<see cref="NetworkInputSource"/>). The analog channels ride along
    /// because they cost nothing and keep the host's fallback path honest.
    ///
    /// Sampling every frame rather than at send time matters: reading the
    /// keyboard only 60 times a second adds up to a whole send interval of
    /// avoidable lag to whatever still depends on it.
    /// </summary>
    public sealed class ClientInputSender : MonoBehaviour
    {
        private const float Interval = 1f / 60f;

        private IDriverInputSource _source;
        private float _accum;
        private bool _respawnLatch;
        private bool _useItemLatch;
        private float _throttle, _steer, _brake;
        private bool _handbrake;

        private void Awake()
        {
            _source = new GatedInputSource(
                new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad));
        }

        private void Update()
        {
            if (NetSession.Instance == null) return;

            if (_source.RespawnPressed()) _respawnLatch = true;
            if (_source.UseItemPressed()) _useItemLatch = true;

            _throttle = _source.Throttle();
            _steer = _source.Steer();
            _brake = _source.Brake();
            _handbrake = _source.Handbrake();

            _accum += Time.unscaledDeltaTime;
            if (_accum < Interval) return;
            // Subtract, don't zero — zeroing rounds the period up to a whole
            // frame and quietly drops the rate below 60 Hz.
            _accum = Mathf.Min(_accum - Interval, Interval);

            NetSession.Instance.SendInput(new InputState
            {
                throttle = _throttle,
                steer = _steer,
                brake = _brake,
                handbrake = _handbrake,
                respawnEdge = _respawnLatch,
                useItemEdge = _useItemLatch,
            });
            _respawnLatch = false;
            _useItemLatch = false;
        }
    }
}
