using AIHWSim.Arcade;
using AIHWSim.Audio;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// One cannon round: a kinematic tracer with a per-step segment raycast.
    /// Kinematic rather than hitscan for the reason <c>Arcade/Missile.cs</c>
    /// states for all projectiles here — a projectile that takes time to arrive
    /// can be dodged, led and WATCHED, and the tracer is the aiming instrument.
    /// At 400 m/s the flight is short; the segment ray does the actual hitting,
    /// the stretched cube just shows where the stream is going.
    /// </summary>
    public sealed class Bullet : MonoBehaviour
    {
        public Transform owner;
        public Vector3 velocity;
        public float damage = 4f;
        public float lifeSeconds = 2.0f;

        private float _age;

        /// <summary>Build and launch one round. Gravity is deliberately ignored:
        /// over a 2-second, 800 m flight the drop is ~20 m, but the gun is a
        /// close-in weapon and the flat trajectory is the arcade honesty the
        /// rest of the arsenal keeps.</summary>
        public static Bullet Fire(Transform owner, Vector3 pos, Vector3 dir,
                                  float speed, Vector3 inheritVel)
        {
            var go = new GameObject("Bullet");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir));

            // Tracer: a bright stretched cube, no collider — the ray is the hit
            // test, and a collider here would shoot its own tracer.
            var viz = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(viz.GetComponent<Collider>());
            viz.transform.SetParent(go.transform, false);
            viz.transform.localScale = new Vector3(0.06f, 0.06f, 1.4f);
            viz.GetComponent<Renderer>().material.color = new Color(1f, 0.85f, 0.35f);

            var b = go.AddComponent<Bullet>();
            b.owner = owner;
            b.velocity = dir.normalized * speed + inheritVel;
            return b;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _age += dt;
            if (_age >= lifeSeconds) { Destroy(gameObject); return; }

            Vector3 from = transform.position;
            Vector3 to = from + velocity * dt;

            var hits = Physics.RaycastAll(from, velocity.normalized,
                                          (to - from).magnitude, ~0,
                                          QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            RaycastHit best = default;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (owner != null && hits[i].collider.transform.IsChildOf(owner)) continue;
                if (hits[i].distance < nearest)
                {
                    nearest = hits[i].distance;
                    best = hits[i];
                    found = true;
                }
            }

            if (found)
            {
                var t = best.collider.GetComponentInParent<WeaponTarget>();
                if (t != null)
                {
                    t.ApplyDamage(damage, best.point);
                    ArcadeBurst.Spawn(best.point, 0.8f, new Color(1f, 0.75f, 0.3f), 0.25f);
                }
                else
                {
                    ArcadeBurst.Spawn(best.point, 0.5f, new Color(0.7f, 0.68f, 0.6f), 0.2f);
                }
                SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.Impact, best.point, 0.5f,
                                           1.3f + Random.value * 0.3f);
                Destroy(gameObject);
                return;
            }

            transform.position = to;
        }
    }
}
