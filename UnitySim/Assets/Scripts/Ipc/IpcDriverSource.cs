using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// Driving input for a vehicle an external application has taken over.
    /// Values arrive in <c>drive</c> messages and are consumed by the ordinary
    /// <c>CarInput</c>/runner pipeline through the <see cref="IDriverInputSource"/>
    /// seam — the same seam bots, scripted physics tests and LAN remote players
    /// already use, so a car driven from the pipe behaves identically to one
    /// driven by a gamepad, assists and all.
    ///
    /// <b>Dead-man.</b> After <see cref="_staleAfter"/> seconds without a command
    /// the car brakes itself, exactly as <c>NetworkInputSource</c> does for a
    /// laggy client. A control application is a separate process that can be
    /// paused in a debugger, blocked on a dialog, or killed; without this, the
    /// last command it happened to send stays latched and a car left at full
    /// throttle keeps going. Pass a negative timeout to disable it — for a
    /// client that genuinely wants to set a command and walk away.
    ///
    /// Edge-triggered buttons latch until consumed, so a single message reliably
    /// produces a single respawn even though the flags are read at frame rate and
    /// written at whatever rate the client manages.
    /// </summary>
    public sealed class IpcDriverSource : IDriverInputSource
    {
        public const float DefaultStaleAfter = 0.5f;

        private readonly float _staleAfter;

        private float _throttle, _steer, _brake;
        private bool _handbrake, _horn, _boost;
        private bool _respawnLatch, _useItemLatch, _jumpLatch;
        private float _receivedAt = -999f;

        public IpcDriverSource(float staleAfterSeconds)
        {
            _staleAfter = staleAfterSeconds == 0f ? DefaultStaleAfter : staleAfterSeconds;
        }

        /// <summary>True while the last command is still fresh. A disabled
        /// dead-man (negative timeout) is always live.</summary>
        private bool Live => _staleAfter < 0f || Time.unscaledTime - _receivedAt < _staleAfter;

        public void Receive(DriveMsg m)
        {
            _throttle = Mathf.Clamp01(m.throttle);
            _steer = Mathf.Clamp(m.steer, -1f, 1f);
            _brake = Mathf.Clamp01(m.brake);
            _handbrake = m.handbrake;
            _horn = m.horn;
            _boost = m.boost;
            if (m.respawn) _respawnLatch = true;
            if (m.useItem) _useItemLatch = true;
            if (m.jump) _jumpLatch = true;
            _receivedAt = Time.unscaledTime;
        }

        public float Throttle() => Live ? _throttle : 0f;
        public float Steer() => Live ? _steer : 0f;
        public float Brake() => Live ? _brake : 1f;
        public bool Handbrake() => Live && _handbrake;
        public bool HornHeld() => Live && _horn;
        public bool BoostHeld() => Live && _boost;

        /// <summary>Never: the chase camera belongs to whoever is sitting at this
        /// machine, and a remote script has no business swinging it around. Same
        /// call <c>NetworkInputSource</c> makes.</summary>
        public bool LookBackHeld() => false;

        /// <summary>No mouse on the other end of a pipe; steering arrives as a
        /// value, not as a delta to integrate.</summary>
        public float MouseSteerDelta() => 0f;

        public bool RespawnPressed() => Consume(ref _respawnLatch);
        public bool UseItemPressed() => Consume(ref _useItemLatch);
        public bool JumpPressed() => Consume(ref _jumpLatch);

        private static bool Consume(ref bool latch)
        {
            if (!latch) return false;
            latch = false;
            return true;
        }
    }
}
