using AIHWSim.Arcade;
using AIHWSim.Audio;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// The jet's homing missile — <c>Arcade/Missile.cs</c>'s architecture
    /// (kinematic body, trigger sphere, FixedUpdate steering, arm delay, owner
    /// exclusion by reference, deadline expiry) freed of that class's two
    /// deliberate constraints: it steers in full 3D (no <c>want.y = 0</c>, no
    /// ground hug), and it hunts <see cref="WeaponTarget"/>s, not cars.
    ///
    /// <b>The Hydra's hidden speed modifier, in the open:</b> the launcher sets
    /// <see cref="speed"/> to twice the base against AIR targets. At 180 m/s
    /// and 400 Hz a step is 0.45 m — smaller than any target, but a head-on
    /// drone closes at ~200 m/s, so the trigger alone could tunnel. Hence the
    /// SEGMENT RAYCAST each step in addition to the trigger: the ray cannot
    /// miss what the discrete sphere might.
    ///
    /// <b>Guidance is lead pursuit with a commit-range let-off.</b> Aiming at
    /// the intercept point rather than the target is what makes it feel
    /// "aggressively tracking"; dropping the turn rate inside
    /// <see cref="commitRange"/> is what leaves a hard break as a real escape —
    /// the same idiom the arcade missile uses, for the same reason: a missile
    /// that cannot be defeated is a hitscan with theatrics.
    /// </summary>
    public sealed class AirMissile : MonoBehaviour
    {
        public Transform owner;          // never hits its shooter, by reference
        public WeaponTarget target;      // null = unlocked straight-runner
        public float speed = 90f;
        public float turnRateDegPerS = 120f;
        public float commitRange = 30f;
        public float commitTurnDegPerS = 35f;
        public float armSeconds = 0.15f;
        public float lifeSeconds = 8f;
        public float directDamage = 40f;
        public float splashRadius = 8f;
        public float splashDamage = 25f;

        private Rigidbody _rb;
        private float _age;

        private void Awake()
        {
            _rb = gameObject.AddComponent<Rigidbody>();
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            var trig = gameObject.AddComponent<SphereCollider>();
            trig.isTrigger = true;
            trig.radius = 0.5f;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _age += dt;
            if (_age >= lifeSeconds) { Fizzle(); return; }

            // Steer: lead pursuit toward the intercept point. Time-to-go from
            // present range at own speed — first-order, refreshed every step,
            // which converges exactly the way a proportional seeker feels.
            if (target != null && target.Alive && target.gameObject.activeInHierarchy)
            {
                Vector3 aim = target.AimPoint;
                float tGo = Vector3.Distance(transform.position, aim)
                            / Mathf.Max(1f, speed);
                Vector3 lead = aim + target.Velocity * tGo;
                Vector3 want = (lead - transform.position).normalized;

                float range = Vector3.Distance(transform.position, aim);
                float rate = (range < commitRange ? commitTurnDegPerS : turnRateDegPerS)
                             * Mathf.Deg2Rad * dt;
                Vector3 fwd = Vector3.RotateTowards(transform.forward, want, rate, 0f);
                transform.rotation = Quaternion.LookRotation(fwd);
            }

            // Advance along a RAY, not just a teleport: the segment test kills
            // tunnelling at closing speeds the trigger cannot cover.
            Vector3 from = transform.position;
            Vector3 to = from + transform.forward * (speed * dt);
            if (SegmentHit(from, to, out RaycastHit hit))
            {
                Detonate(hit.point, hit.collider.GetComponentInParent<WeaponTarget>());
                return;
            }
            _rb.MovePosition(to);
        }

        private bool SegmentHit(Vector3 from, Vector3 to, out RaycastHit best)
        {
            best = default;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len <= 1e-5f) return false;
            var hits = Physics.RaycastAll(from, d / len, len, ~0,
                                          QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (owner != null && hits[i].collider.transform.IsChildOf(owner)) continue;
                if (hits[i].collider.transform.IsChildOf(transform)) continue;
                if (hits[i].distance < nearest)
                {
                    nearest = hits[i].distance;
                    best = hits[i];
                    found = true;
                }
            }
            return found;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.isTrigger) return;
            if (_age < armSeconds) return;   // clears its own launch rack first
            if (owner != null && other.transform.IsChildOf(owner)) return;
            Detonate(transform.position, other.GetComponentInParent<WeaponTarget>());
        }

        private void Detonate(Vector3 at, WeaponTarget direct)
        {
            ArcadeBurst.Spawn(at, 6f, new Color(1f, 0.62f, 0.20f), 0.6f);
            SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.Explosion, at);

            direct?.ApplyDamage(directDamage, at);

            // Splash with radial falloff — Landmine.cs's shape, over the
            // registry rather than the arena.
            var all = WeaponTarget.All;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                WeaponTarget t = all[i];
                if (t == null || t == direct || !t.Alive) continue;
                float d = Vector3.Distance(at, t.AimPoint);
                if (d > splashRadius) continue;
                t.ApplyDamage(splashDamage * (1f - d / splashRadius), at);
            }

            Destroy(gameObject);
        }

        /// <summary>Out of time: a puff, no damage. A missile that expires with
        /// a full warhead explosion would reward missing.</summary>
        private void Fizzle()
        {
            ArcadeBurst.Spawn(transform.position, 1.5f,
                              new Color(0.6f, 0.6f, 0.62f), 0.4f);
            Destroy(gameObject);
        }
    }
}
