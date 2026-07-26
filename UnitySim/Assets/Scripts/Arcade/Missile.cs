using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// A homing missile: kinematic, not a Rigidbody projectile and not hitscan.
    ///
    /// Kinematic because its position is then a deterministic function of (spawn,
    /// target, elapsed) — which is what lets LAN sync a whole missile with two
    /// reliable messages instead of a per-frame pose stream. Not hitscan because
    /// zero travel time removes the entire game: you could not outrun it, shield
    /// against it, or see it coming.
    ///
    /// It moves in FixedUpdate on the same 400 Hz clock as the cars, so there is
    /// no relative tunnelling at closing speeds.
    /// </summary>
    public sealed class Missile : MonoBehaviour
    {
        public int objId;
        public int ownerSlot = -1;
        public CarVehicle ownerCar;
        public CarVehicle target;        // may go null: LAN players leave mid-flight
        public float expiresAt;

        private float _armedAt;
        private int _probe;
        private float _groundY;
        private bool _haveGround;
        private bool _authority = true;

        private void Awake()
        {
            var col = gameObject.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = ArcadeConfig.MissileRadius;

            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            _armedAt = ArcadeDirector.Clock + ArcadeConfig.MissileArmSeconds;
            _groundY = transform.position.y;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost)
            {
                _authority = false;
                Destroy(col);   // clients fly a visual copy; the host owns hits
            }
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            var pos = transform.position;

            // Home. A destroyed target (Unity's overloaded ==) degrades to flying
            // straight rather than throwing — this happens whenever a LAN player
            // leaves while a missile is chasing them.
            Vector3 fwd = transform.forward;
            if (target != null)
            {
                Vector3 want = target.transform.position - pos;
                want.y = 0f;
                if (want.sqrMagnitude > 1e-4f)
                {
                    // Commit: once this close the missile can barely steer, so a
                    // well-timed swerve makes it miss. It keeps flying and may
                    // come back around — a miss costs the shooter the shot, not
                    // the missile.
                    float rate = want.magnitude <= ArcadeConfig.MissileCommitRange
                        ? ArcadeConfig.MissileCommitTurnRate
                        : ArcadeConfig.MissileTurnRate;
                    fwd = Vector3.RotateTowards(fwd, want.normalized, rate * dt, 0f);
                }
            }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            fwd.Normalize();

            pos += fwd * (ArcadeConfig.MissileSpeed * dt);

            // Follow the terrain so it tracks over ramps and banked ribbon.
            if (++_probe >= ArcadeConfig.MissileGroundProbeEvery)
            {
                _probe = 0;
                if (GroundAt(pos, out float y)) { _groundY = y; _haveGround = true; }
            }
            pos.y = (_haveGround ? _groundY : pos.y) + ArcadeConfig.MissileHoverHeight;

            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd, Vector3.up));

            if (_authority && ArcadeDirector.Clock >= expiresAt)
                ArcadeDirector.Instance?.OnMissileExpired(this);
        }

        /// <summary>Highest surface under <paramref name="p"/> that isn't a car.</summary>
        private static bool GroundAt(Vector3 p, out float y)
        {
            y = 0f;
            var hits = Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 3f);
            bool any = false;
            foreach (var h in hits)
            {
                if (h.collider.isTrigger) continue;
                if (h.collider.GetComponentInParent<CarVehicle>() != null) continue;
                if (!any || h.point.y > y) { y = h.point.y; any = true; }
            }
            return any;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_authority) return;
            if (other.isTrigger) return;                       // gates and pickups
            if (ArcadeDirector.Clock < _armedAt) return;       // can't detonate on the launcher

            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            if (car == ownerCar) return;                       // reference identity, always
            if (ArcadeConfig.LogHits)
                Debug.Log($"[arcade] missile {objId} struck {car.name} at t={ArcadeDirector.Clock:0.00}");

            ArcadeDirector.Instance?.OnMissileHit(this, car);
        }
    }
}
