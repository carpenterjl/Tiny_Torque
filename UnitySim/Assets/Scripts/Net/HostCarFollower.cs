using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// The host's copy of a car a client simulates.
    ///
    /// Since protocol 4 the owner is right about its own car, so this is not a
    /// simulation — it is a kinematic body driven along the owner's streamed
    /// poses. It exists because the host adjudicates everything shared, and most
    /// of that adjudication is trigger-based: lap lines, checkpoints, item
    /// boxes, missiles and bananas all resolve by touching a collider. A car
    /// that is only a number in a dictionary cannot cross a finish line.
    ///
    /// It moves in FixedUpdate via MovePosition rather than snapping per packet
    /// for exactly that reason: the gates are 0.25 m thick, and at racing speed
    /// one 60 Hz packet is nearly a metre. Stepping the gap at the physics rate
    /// (2.5 ms, under 3 cm) keeps every trigger honest.
    /// </summary>
    public sealed class HostCarFollower : MonoBehaviour
    {
        /// <summary>How far behind the owner's clock to render. Enough to
        /// interpolate through jitter, small enough that adjudication happens
        /// against a pose the owner still recognises.</summary>
        private const float FollowDelay = 0.05f;
        private const float MaxExtrapolate = 0.1f;
        /// <summary>Silence past this and we stop guessing and hold still.</summary>
        private const float StaleAfter = 0.5f;

        public int slot = -1;
        public CarVehicle car;
        public Core.PlayerRig rig;

        private struct Snap
        {
            public float ownerTime;
            public Vector3 pos;
            public Quaternion rot;
            public Vector3 vel;
            public Vector3 angVel;
            public float steerDeg;
            public float wheelRadPerSec;
        }

        private readonly List<Snap> _buffer = new List<Snap>(32);
        private Rigidbody _body;
        private byte _epoch;
        private bool _hasEpoch;
        private float _clockOffset;      // smoothed (ownerTime - localTime)
        private bool _hasOffset;
        private float _lastReceived = -999f;
        private float _wheelSpin;
        private float _curSteer, _curWheelSpeed;

        // Set by SnapAwaitEpoch: ignore everything still carrying the pre-teleport
        // epoch, because those packets describe where the car used to be.
        private bool _awaitingEpoch;
        private byte _awaitedFrom;

        /// <summary>No owner state for a while — the car is frozen where it was.</summary>
        public bool IsStale => Time.unscaledTime - _lastReceived > StaleAfter;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            if (_body != null)
            {
                _body.isKinematic = true;
                // The gates this car has to trip are thinner than one step at
                // speed. Speculative is the only continuous mode kinematic
                // bodies support.
                _body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                _body.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        public void Receive(in OwnStateMsg s)
        {
            _lastReceived = Time.unscaledTime;

            if (_awaitingEpoch)
            {
                // Still hearing about the old position: drop it on the floor.
                if (s.carEpoch == _awaitedFrom) return;
                _awaitingEpoch = false;
            }

            if (!_hasEpoch || s.carEpoch != _epoch)
            {
                // The owner teleported (respawn, recovery, grid). History is
                // about a different place; interpolating through it would drag
                // the car across the map and trip every gate on the way.
                _buffer.Clear();
                _epoch = s.carEpoch;
                _hasEpoch = true;
                if (_body != null) _body.position = s.pos;
                if (_body != null) _body.rotation = s.rot;
                transform.SetPositionAndRotation(s.pos, s.rot);
            }

            float offset = s.ownerTime - Time.unscaledTime;
            _clockOffset = _hasOffset ? Mathf.Lerp(_clockOffset, offset, 0.1f) : offset;
            _hasOffset = true;

            _buffer.Add(new Snap
            {
                ownerTime = s.ownerTime,
                pos = s.pos,
                rot = s.rot,
                vel = s.vel,
                angVel = s.angVel,
                steerDeg = s.steerDeg,
                wheelRadPerSec = s.wheelRadPerSec,
            });
            if (_buffer.Count > 30) _buffer.RemoveAt(0);
        }

        /// <summary>
        /// Put the car somewhere the host decided (the race grid) and ignore
        /// in-flight owner packets until the owner acknowledges by bumping its
        /// epoch. Without the wait, unreliable state already on the wire would
        /// drag the car straight back off the grid.
        /// </summary>
        public void SnapAwaitEpoch(Vector3 pos, Quaternion rot)
        {
            _buffer.Clear();
            _awaitingEpoch = _hasEpoch;
            _awaitedFrom = _epoch;
            if (_body != null)
            {
                _body.position = pos;
                _body.rotation = rot;
            }
            transform.SetPositionAndRotation(pos, rot);
        }

        private void FixedUpdate()
        {
            if (_body == null || _buffer.Count == 0) return;
            float renderTime = Time.unscaledTime + _clockOffset - FollowDelay;

            int hi = 0;
            while (hi < _buffer.Count && _buffer[hi].ownerTime < renderTime) hi++;

            Vector3 pos;
            Quaternion rot;
            if (hi == 0)
            {
                var s = _buffer[0];
                pos = s.pos; rot = s.rot;
                _curSteer = s.steerDeg; _curWheelSpeed = s.wheelRadPerSec;
            }
            else if (hi >= _buffer.Count)
            {
                var last = _buffer[_buffer.Count - 1];
                // Stale means the owner is gone or badly lagged. Holding the last
                // pose is honest; extrapolating a car for half a second is not.
                float over = IsStale ? 0f
                    : Mathf.Min(MaxExtrapolate, renderTime - last.ownerTime);
                pos = last.pos + last.vel * over;
                rot = IntegrateRotation(last.rot, last.angVel, over);
                _curSteer = last.steerDeg; _curWheelSpeed = last.wheelRadPerSec;
            }
            else
            {
                var a = _buffer[hi - 1];
                var b = _buffer[hi];
                float span = Mathf.Max(0.0001f, b.ownerTime - a.ownerTime);
                float t = Mathf.Clamp01((renderTime - a.ownerTime) / span);
                pos = Vector3.Lerp(a.pos, b.pos, t);
                rot = Quaternion.Slerp(a.rot, b.rot, t);
                _curSteer = Mathf.Lerp(a.steerDeg, b.steerDeg, t);
                _curWheelSpeed = Mathf.Lerp(a.wheelRadPerSec, b.wheelRadPerSec, t);
            }

            // MovePosition, not a transform write: this is what makes PhysX sweep
            // the body between steps so the thin lap and checkpoint gates fire.
            _body.MovePosition(pos);
            _body.MoveRotation(rot);

            while (_buffer.Count > 2 && _buffer[1].ownerTime < renderTime)
                _buffer.RemoveAt(0);
        }

        private void Update()
        {
            UpdateWheelVisuals();
            PumpInputEdges();
        }

        /// <summary>
        /// The owner drives its own car, but the host still owns the two things
        /// its input stream carries: firing a held item, and clearing lap state
        /// on respawn. This car has no CarInput to do that, so the follower does.
        /// </summary>
        private void PumpInputEdges()
        {
            if (slot < 0 || NetSession.Instance == null || !NetSession.Instance.IsHost) return;
            var src = NetSession.Instance.InputSourceFor(slot);
            if (src == null) return;

            if (src.UseItemPressed() && car != null)
                Arcade.ArcadeDirector.Instance?.RequestUse(car);

            if (src.RespawnPressed() && car != null)
            {
                // The owner teleports itself; here we only reset what the host
                // owns for it — its lap timing and any arcade effects it was
                // carrying.
                rig?.lapTimer?.ResetTimer(car);
                rig?.arcade?.ClearAll();
            }
        }

        private void UpdateWheelVisuals()
        {
            if (car == null) return;
            _wheelSpin += _curWheelSpeed * Time.deltaTime;
            for (int i = 0; i < car.WheelCount; i++)
            {
                var viz = car.GetWheelVisual(i);
                if (viz == null) continue;
                bool steers = car.WheelAllowsSteering(i);
                viz.localRotation =
                    Quaternion.Euler(0f, steers ? _curSteer : 0f, 0f) *
                    Quaternion.Euler(Mathf.Rad2Deg * _wheelSpin, 0f, 0f);
            }
        }

        private static Quaternion IntegrateRotation(Quaternion rot, Vector3 angVel, float dt)
        {
            float mag = angVel.magnitude;
            if (mag < 1e-4f || dt <= 0f) return rot;
            return Quaternion.AngleAxis(mag * Mathf.Rad2Deg * dt, angVel / mag) * rot;
        }
    }
}
