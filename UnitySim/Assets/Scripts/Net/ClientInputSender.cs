using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Client-side input pump: samples the local devices (merged keyboard +
    /// gamepad, countdown-gated) at 30 Hz and streams them to the host, where a
    /// <see cref="NetworkInputSource"/> feeds them into this player's simulated
    /// car. Respawn presses are edge-latched between sends so none are dropped.
    /// </summary>
    public sealed class ClientInputSender : MonoBehaviour
    {
        private const float Interval = 1f / 30f;

        private IDriverInputSource _source;
        private float _accum;
        private bool _respawnLatch;

        private void Awake()
        {
            _source = new GatedInputSource(
                new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad));
        }

        private void Update()
        {
            if (NetSession.Instance == null) return;

            if (_source.RespawnPressed()) _respawnLatch = true;

            _accum += Time.unscaledDeltaTime;
            if (_accum < Interval) return;
            _accum = 0f;

            NetSession.Instance.SendInput(new InputState
            {
                throttle = _source.Throttle(),
                steer = _source.Steer(),
                brake = _source.Brake(),
                handbrake = _source.Handbrake(),
                respawnEdge = _respawnLatch,
            });
            _respawnLatch = false;
        }
    }
}
