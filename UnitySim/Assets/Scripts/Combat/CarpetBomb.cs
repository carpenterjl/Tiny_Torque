using AIHWSim.Arcade;
using AIHWSim.Audio;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// One bomb of a carpet stick: pure ballistic integration — <c>v += g·dt</c>
    /// and nothing else — seeded with the aircraft's velocity at release, which
    /// is ALL of carpet bombing: the bombs inherit your speed, so the line they
    /// draw on the ground is the line you were flying. No guidance, no drag
    /// (a bomb's terminal ballistics at these speeds and heights change the
    /// impact point by metres the splash radius forgives).
    ///
    /// Detonation is by segment raycast — the same anti-tunnel test the missile
    /// uses, and here it doubles as the fuze: the first non-owner thing the
    /// fall line crosses, including the ground, is the impact.
    /// </summary>
    public sealed class CarpetBomb : MonoBehaviour
    {
        public Transform owner;
        public Vector3 velocity;
        public float splashRadius = 12f;
        public float splashDamage = 45f;
        public float lifeSeconds = 15f;

        private float _age;

        public static CarpetBomb Drop(Transform owner, Vector3 pos, Vector3 inheritVel)
        {
            var go = new GameObject("CarpetBomb");
            go.transform.position = pos;

            var viz = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(viz.GetComponent<Collider>());
            viz.transform.SetParent(go.transform, false);
            viz.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            viz.transform.localScale = new Vector3(0.30f, 0.55f, 0.30f);
            viz.GetComponent<Renderer>().material.color = new Color(0.20f, 0.22f, 0.20f);

            var b = go.AddComponent<CarpetBomb>();
            b.owner = owner;
            b.velocity = inheritVel;
            return b;
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _age += dt;
            if (_age >= lifeSeconds) { Destroy(gameObject); return; }

            velocity += Physics.gravity * dt;
            Vector3 from = transform.position;
            Vector3 to = from + velocity * dt;

            var hits = Physics.RaycastAll(from, velocity.normalized,
                                          (to - from).magnitude, ~0,
                                          QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            Vector3 impact = Vector3.zero;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                if (owner != null && hits[i].collider.transform.IsChildOf(owner)) continue;
                if (hits[i].distance < nearest)
                {
                    nearest = hits[i].distance;
                    impact = hits[i].point;
                    found = true;
                }
            }

            if (found) { Detonate(impact); return; }

            transform.position = to;
            // Nose into the fall — a bomb that stays level all the way down
            // reads as floating, and the fix is one LookRotation.
            if (velocity.sqrMagnitude > 1f)
                transform.rotation = Quaternion.LookRotation(velocity.normalized);
        }

        private void Detonate(Vector3 at)
        {
            ArcadeBurst.Spawn(at, splashRadius * 0.8f, new Color(1f, 0.55f, 0.15f), 0.9f);
            SfxPlayer.Ensure()?.PlayAt(ProceduralAudio.Explosion, at, 1f, 0.85f);

            // Landmine.cs's radial falloff, over the registry.
            var all = WeaponTarget.All;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                WeaponTarget t = all[i];
                if (t == null || !t.Alive) continue;
                float d = Vector3.Distance(at, t.AimPoint);
                if (d > splashRadius) continue;
                t.ApplyDamage(splashDamage * (1f - d / splashRadius), at);
            }

            Destroy(gameObject);
        }
    }
}
