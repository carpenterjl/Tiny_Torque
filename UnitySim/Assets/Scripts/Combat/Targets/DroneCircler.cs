using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// A slow target aircraft on a circular circuit — the flying tomato can.
    /// Kinematic on purpose, three times over: a second <c>PlaneVehicle</c>
    /// would want a second <c>SimulationRunner</c>, and every runner writes the
    /// GLOBAL <c>Time.fixedDeltaTime</c>; a physical drone would need flying
    /// (it has no pilot); and a target's job is to be predictable enough to
    /// lead and honest enough to hit — a kinematic body with a real collider is
    /// both. Moved with <c>MovePosition</c> in FixedUpdate so interpolation and
    /// trigger contacts work exactly as they do for the arcade missiles.
    ///
    /// Death is a two-act piece the spawner choreographs: this component only
    /// flies the circle and, when told, the tumble.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class DroneCircler : MonoBehaviour
    {
        [Tooltip("Centre of the circuit, world space.")]
        public Vector3 centre = new Vector3(0f, 60f, 0f);
        public float circuitRadius = 180f;
        public float altitude = 60f;
        public float speed = 18f;
        [Tooltip("Start phase (deg) so three drones on one circle spread out.")]
        public float phaseDeg;
        [Tooltip("+1 anticlockwise seen from above, -1 clockwise.")]
        public int direction = 1;

        private Rigidbody _rb;
        private WeaponTarget _target;
        private float _angle;       // rad along the circle
        private bool _tumbling;
        private Vector3 _tumbleVel;
        private Vector3 _tumbleAxis;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _target = GetComponent<WeaponTarget>();
            _angle = phaseDeg * Mathf.Deg2Rad;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            if (_tumbling)
            {
                // Ballistic fall with a slow tumble — dead is dead, and the
                // spawner's timer ends the performance.
                _tumbleVel += Physics.gravity * dt;
                _rb.MovePosition(_rb.position + _tumbleVel * dt);
                _rb.MoveRotation(Quaternion.AngleAxis(240f * dt, _tumbleAxis) * _rb.rotation);
                if (_target != null) _target.reportedVelocity = _tumbleVel;
                return;
            }

            _angle += direction * (speed / Mathf.Max(1f, circuitRadius)) * dt;

            Vector3 pos = centre + new Vector3(Mathf.Cos(_angle), 0f, Mathf.Sin(_angle))
                          * circuitRadius;
            pos.y = altitude;
            // Velocity is the analytic tangent, not a difference — the missiles
            // lead off this number, so it should be exact.
            Vector3 vel = direction * speed
                          * new Vector3(-Mathf.Sin(_angle), 0f, Mathf.Cos(_angle));

            // Face along the path, banked into the turn the way an aircraft
            // holding this circle would be. tan φ = v²/(g·r) — the same
            // coordinated-turn relation A2 gates on, used in reverse.
            float bank = Mathf.Atan2(speed * speed,
                                     9.80665f * circuitRadius) * Mathf.Rad2Deg;
            Quaternion rot = Quaternion.LookRotation(vel)
                             * Quaternion.Euler(0f, 0f, direction * bank);

            _rb.MovePosition(pos);
            _rb.MoveRotation(rot);
            if (_target != null) _target.reportedVelocity = vel;
        }

        /// <summary>Switch from the circuit to a dying fall, seeded with the
        /// current path velocity so the wreck keeps its momentum.</summary>
        public void BeginTumble()
        {
            _tumbling = true;
            _tumbleVel = _target != null ? _target.reportedVelocity : Vector3.zero;
            _tumbleAxis = Random.onUnitSphere;
        }

        /// <summary>Back onto the circle, alive — the respawn path.</summary>
        public void ResetToCircuit()
        {
            _tumbling = false;
            _angle = phaseDeg * Mathf.Deg2Rad;
            if (_target != null) _target.ResetHealth();
        }
    }
}
