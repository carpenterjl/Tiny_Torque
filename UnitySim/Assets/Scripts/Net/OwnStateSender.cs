using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// Client-side publisher for the one car this machine actually simulates.
    ///
    /// Since protocol 4 a LAN client owns its own car: it runs the full physics
    /// rig locally, so its controls answer immediately, and streams the result
    /// here at 60 Hz. The host follows that stream with a kinematic copy (which
    /// is what keeps trigger-based lap timing and item pickups working) and
    /// relays it on to the other clients, who render it as an ordinary ghost.
    ///
    /// The owner also owns its car's <em>epoch</em>. Every teleport it performs —
    /// respawn, a recovery the host handed it, the race grid — bumps the counter,
    /// which is how every other machine learns to snap rather than lerp a streak
    /// across the map.
    /// </summary>
    public sealed class OwnStateSender : MonoBehaviour
    {
        private const float Interval = 1f / 60f;

        public CarVehicle car;
        public Core.PlayerRig rig;
        public float wheelRadius = 0.033f;

        private Rigidbody _body;
        private float _accum;
        private byte _epoch;

        /// <summary>The car jumped. Tell everyone to snap it, not to drive it there.</summary>
        public void BumpEpoch() => _epoch++;

        public byte Epoch => _epoch;

        private void Start()
        {
            if (car == null) { enabled = false; return; }
            _body = car.GetComponent<Rigidbody>();
            // Every teleport raises VehicleReset — the start-line reset and the
            // respawn key's put-me-back-on-the-line alike — so this covers them
            // all without the input path knowing anything about the network.
            car.VehicleReset += BumpEpoch;
        }

        private void OnDestroy()
        {
            if (car != null) car.VehicleReset -= BumpEpoch;
        }

        private void Update()
        {
            if (NetSession.Instance == null || _body == null) return;

            _accum += Time.unscaledDeltaTime;
            if (_accum < Interval) return;
            _accum = Mathf.Min(_accum - Interval, Interval);

            // Track limits are judged by whoever has the wheels. The host's copy
            // of this car is kinematic — its WheelColliders never touch the
            // surface map — so the verdict rides up with the pose.
            var arc = rig?.arcade;
            NetSession.Instance.SendOwnState(new OwnStateMsg
            {
                carEpoch = _epoch,
                ownerTime = Time.unscaledTime,
                pos = _body.position,
                rot = _body.rotation,
                vel = _body.linearVelocity,
                angVel = _body.angularVelocity,
                steerDeg = car.CurrentSteerAngle,
                wheelRadPerSec = car.ForwardSpeed / Mathf.Max(0.005f, wheelRadius),
                penalized = arc != null && arc.penalized,
                warned = arc != null && arc.warned,
            });
        }
    }
}
