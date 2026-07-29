using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// A mine: sits on the floor, and hurts everything inside its blast when
    /// something drives over it.
    ///
    /// Modelled on <c>Banana</c> — kinematic trigger drop, an owner grace
    /// window, host-only detonation — with the one difference that a mine
    /// damages an AREA rather than spinning the single car that touched it,
    /// which is what makes it worth avoiding rather than worth eating.
    /// </summary>
    public sealed class Landmine : MonoBehaviour
    {
        public MatchRacer owner;
        public float droppedAt;

        private Collider _col;
        private float _pulse;

        public static Landmine Create(Transform parent, Vector3 pos, MatchRacer owner, float clock)
        {
            var go = new GameObject("Landmine");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;

            var body = TrackBuilder.StandardMat(new Color(0.12f, 0.12f, 0.14f));
            var spike = TrackBuilder.StandardMat(new Color(0.75f, 0.1f, 0.08f));
            TrackBuilder.Cylinder("puck", pos, new Vector3(0.14f, 0.025f, 0.14f),
                Quaternion.identity, body, go.transform, collider: false);
            for (int i = 0; i < 5; i++)
            {
                float a = i * Mathf.PI * 2f / 5f;
                TrackBuilder.Box($"spike_{i}",
                    pos + new Vector3(Mathf.Cos(a) * 0.05f, 0.035f, Mathf.Sin(a) * 0.05f),
                    new Vector3(0.02f, 0.03f, 0.02f), Quaternion.identity, spike,
                    go.transform, collider: false);
            }

            var mine = go.AddComponent<Landmine>();
            mine.owner = owner;
            mine.droppedAt = clock;

            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 0.12f;
            mine._col = col;

            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;   // required for reliable trigger callbacks
            rb.useGravity = false;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost) Destroy(col);   // visual-only on clients
            return mine;
        }

        private void Update()
        {
            // A slow throb, so a mine in shadow is still findable.
            _pulse += Time.deltaTime * 3.2f;
            float s = 1f + Mathf.Sin(_pulse) * 0.06f;
            transform.localScale = new Vector3(s, 1f, s);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.isTrigger) return;
            var dir = DerbyDirector.Instance;
            if (dir == null || !dir.IsAuthority) return;

            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            var racer = dir.RacerOf(car);
            if (racer == null || !racer.alive) return;
            if (racer == owner && dir.Clock - droppedAt < ModeConfig.MineOwnerGrace) return;

            Detonate(dir);
        }

        private void Detonate(DerbyDirector dir)
        {
            Vector3 at = transform.position;
            foreach (var r in dir.Racers)
            {
                if (!r.alive || r.car == null) continue;
                float d = Vector3.Distance(r.car.transform.position, at);
                if (d > ModeConfig.MineRadius) continue;

                // Full damage at the centre, tapering to nothing at the edge.
                float falloff = 1f - Mathf.Clamp01(d / ModeConfig.MineRadius);
                dir.Damage(r, ModeConfig.MineDamage * falloff, owner);

                // And a shove, so a survivor is at least out of position.
                Vector3 away = r.car.transform.position - at;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-4f)
                    r.car.ArcadeImpulse(away.normalized * (1.6f * falloff) + Vector3.up * 0.9f);
            }
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiDeny);
            Destroy(gameObject);
        }
    }
}
