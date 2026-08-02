using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// A ground vehicle lapping a closed loop of waypoints, weaving as it goes —
    /// the "dodging" truck. The waypoints are its own CHILD transforms named
    /// <c>wp*</c>, so in the authored scene they are plain draggable handles:
    /// move one in the editor and the loop moves, no script edit, which is the
    /// entire point of authoring the scene.
    ///
    /// The path is a closed Catmull-Rom through the waypoints — the same spline
    /// family the track tooling speaks — sampled by arc-walk at a speed that
    /// wanders ±30 % on a slow sine. That wander is what makes bombing it a
    /// judgement call: the lead you set up three seconds ago is a little wrong
    /// now, the way it would be against a driver who has seen you.
    ///
    /// Kinematic for the same three reasons as <see cref="DroneCircler"/>, plus
    /// one of its own: the whole airfield is frictionless by design, so a
    /// physical wheeled vehicle here would slide like soap. Kinematic is not a
    /// shortcut — it is the only honest way to put a driving car on THIS ground.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SplineRunner : MonoBehaviour
    {
        public float baseSpeed = 12f;
        [Tooltip("±fraction of baseSpeed the weave adds and removes.")]
        public float speedJitter = 0.30f;
        [Tooltip("Period (s) of the speed weave. Slow on purpose: dodging, "
                 + "not vibrating.")]
        public float jitterPeriod = 7f;
        [Tooltip("Phase (s) so several runners on similar loops desynchronise.")]
        public float jitterPhase;

        private Rigidbody _rb;
        private WeaponTarget _target;
        private readonly List<Vector3> _pts = new List<Vector3>();
        private float _t;           // spline parameter, one unit per segment
        private float _clock;
        private bool _dead;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _target = GetComponent<WeaponTarget>();
            CollectWaypoints();
        }

        /// <summary>Read the loop off the child handles. Called at Awake and
        /// again on respawn, so an edit made during Play (drag a handle while
        /// paused) is picked up at the next lap of the lifecycle.</summary>
        public void CollectWaypoints()
        {
            _pts.Clear();
            foreach (Transform c in transform.parent != null ? transform.parent : transform)
                if (c != transform && c.name.StartsWith("wp"))
                    _pts.Add(c.position);
        }

        private void FixedUpdate()
        {
            if (_dead || _pts.Count < 4) return;
            float dt = Time.fixedDeltaTime;
            _clock += dt;

            float speed = baseSpeed * (1f + speedJitter
                * Mathf.Sin((_clock + jitterPhase) * 2f * Mathf.PI / jitterPeriod));

            // Arc-walk: advance the parameter by speed over the local tangent
            // length, so metres per second stays true through tight corners
            // where equal parameter steps would sprint.
            Vector3 here = Sample(_t);
            Vector3 ahead = Sample(_t + 0.01f);
            float tangent = (ahead - here).magnitude / 0.01f;
            _t += speed / Mathf.Max(0.5f, tangent) * dt;
            if (_t >= _pts.Count) _t -= _pts.Count;

            Vector3 pos = Sample(_t);
            Vector3 dir = Sample(_t + 0.02f) - pos;
            dir.y = 0f;

            _rb.MovePosition(pos);
            if (dir.sqrMagnitude > 1e-6f)
                _rb.MoveRotation(Quaternion.LookRotation(dir.normalized));
            if (_target != null)
                _target.reportedVelocity = dir.normalized * speed;
        }

        /// <summary>Closed Catmull-Rom through the waypoint loop.</summary>
        private Vector3 Sample(float t)
        {
            int n = _pts.Count;
            int i = Mathf.FloorToInt(t) % n;
            float u = t - Mathf.Floor(t);
            Vector3 p0 = _pts[(i - 1 + n) % n];
            Vector3 p1 = _pts[i];
            Vector3 p2 = _pts[(i + 1) % n];
            Vector3 p3 = _pts[(i + 2) % n];
            return 0.5f * ((2f * p1)
                + (p2 - p0) * u
                + (2f * p0 - 5f * p1 + 4f * p2 - p3) * (u * u)
                + (3f * p1 - p3 - 3f * p2 + p0) * (u * u * u));
        }

        /// <summary>Dead trucks stop where they are; the spawner owns the smoke
        /// and the eventual respawn.</summary>
        public void Halt()
        {
            _dead = true;
            if (_target != null) _target.reportedVelocity = Vector3.zero;
        }

        public void ResetToLoop()
        {
            _dead = false;
            _t = 0f;
            CollectWaypoints();
            if (_target != null) _target.ResetHealth();
        }
    }
}
