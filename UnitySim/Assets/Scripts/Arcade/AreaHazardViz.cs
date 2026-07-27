using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// The look of a dropped area hazard — a smoke cloud or an oil slick — and
    /// nothing else. Growth, drift, churn and fade live here; lifetime, hit
    /// detection and every gameplay effect belong to the director.
    ///
    /// The split matters because on a LAN client this is ALL there is: the client
    /// is told "a hazard exists here" and renders it, while the host decides who
    /// it caught. Keeping the viz free of gameplay state means the client path is
    /// the same object as the host path, not a parallel implementation.
    ///
    /// Modelled on <see cref="ArcadeBurst"/>, including the property-block fade —
    /// eight puffs sharing one block means N clouds do not leak 8N materials.
    /// Unlike the burst it runs on <see cref="ArcadeDirector.Clock"/> rather than
    /// Time.time: this animation is nine seconds long and mirrors a gameplay
    /// deadline, so a pause has to hold it or the cloud would evaporate while the
    /// hazard it represents is still catching people.
    /// </summary>
    public sealed class AreaHazardViz : MonoBehaviour
    {
        public ItemKind kind = ItemKind.SmokeCloud;

        /// <summary>Final world radius. The geometry is authored at radius 1, so
        /// this is simply the root's scale once fully grown.</summary>
        public float radius = ArcadeConfig.SmokeRadius;
        public float startRadius = ArcadeConfig.SmokeStartRadius;
        public float growSeconds = ArcadeConfig.SmokeGrowSeconds;
        public float fadeSeconds = ArcadeConfig.SmokeFadeSeconds;

        /// <summary>Absolute <see cref="ArcadeDirector.Clock"/> deadline. Drives
        /// the fade envelope always, and the teardown only when
        /// <see cref="selfDestruct"/> is set.</summary>
        public float expiresAt;

        /// <summary>
        /// Destroy self at <see cref="expiresAt"/>. True for a viz spawned on its
        /// own, so a hand-spawned cloud cannot become permanent scenery — but the
        /// director clears it on the hazards it owns, because both would otherwise
        /// be racing to destroy the same GameObject in the same frame and
        /// MonoBehaviour Update order is undefined. Whoever lost would either
        /// leave the hazard alive with no visual or swallow its expiry event.
        /// </summary>
        public bool selfDestruct = true;

        /// <summary>Flat world drift, m/s. Zero for a slick — oil stays put.</summary>
        public Vector3 drift;

        public Color tint = Color.white;
        /// <summary>Opacity at full strength, before the fade multiplies in.</summary>
        public float baseAlpha = 0.72f;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;
        private Transform[] _puffs;
        private Vector3[] _puffHome;
        private float _bornClock;

        /// <summary>
        /// Build one at a world point. <paramref name="objId"/> seeds the drift
        /// direction and the churn phases: every machine in a LAN session knows
        /// the id, so all of them animate the same cloud without any of it going
        /// on the wire — and without <c>UnityEngine.Random</c>, which would both
        /// diverge across machines and perturb the sequence the sim's noise
        /// models draw from.
        /// </summary>
        public static AreaHazardViz Spawn(ItemKind kind, Vector3 pos, int objId, float lifetime)
        {
            var go = new GameObject(kind == ItemKind.OilSlick ? "OilSlickViz" : "SmokeCloudViz");
            go.layer = PartVisualFactory.VizLayer;
            go.transform.position = pos;

            if (kind == ItemKind.OilSlick) ArcadeVfx.BuildSlick(go.transform);
            else ArcadeVfx.BuildSmoke(go.transform);

            var v = go.AddComponent<AreaHazardViz>();
            v.kind = kind;
            v.expiresAt = ArcadeDirector.Clock + lifetime;

            if (kind == ItemKind.OilSlick)
            {
                v.radius = ArcadeConfig.SlickRadius;
                v.tint = new Color(0.10f, 0.09f, 0.13f);
                v.baseAlpha = 0.85f;
                v.drift = Vector3.zero;
            }
            else
            {
                v.radius = ArcadeConfig.SmokeRadius;
                v.tint = new Color(0.60f, 0.72f, 0.42f);
                v.baseAlpha = 0.72f;
                // Golden-angle spread off the id, so consecutive drops never
                // drift the same way and nothing has to be seeded or shared.
                float a = objId * 137.508f * Mathf.Deg2Rad;
                v.drift = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) *
                          ArcadeConfig.SmokeDriftSpeed;
            }
            return v;
        }

        private void Start()
        {
            _bornClock = ArcadeDirector.Clock;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _mpb = new MaterialPropertyBlock();

            int n = transform.childCount;
            _puffs = new Transform[n];
            _puffHome = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                _puffs[i] = transform.GetChild(i);
                _puffHome[i] = _puffs[i].localPosition;
            }

            transform.localScale = Vector3.one * startRadius;
        }

        private void Update()
        {
            float clock = ArcadeDirector.Clock;
            if (clock >= expiresAt && selfDestruct) { Destroy(gameObject); return; }

            float age = clock - _bornClock;
            float dt = Time.deltaTime;

            // Grow in. An unexpanded puff must not look like a full cloud, for
            // the same reason the gameplay radius ramps: the two have to agree
            // about how big the thing currently is.
            float grow = growSeconds > 0f ? Mathf.Clamp01(age / growSeconds) : 1f;
            float r = Mathf.Lerp(startRadius, radius, grow);
            transform.localScale = Vector3.one * r;

            if (drift.sqrMagnitude > 0f) transform.position += drift * dt;

            // Churn: each puff wanders on its own slow phase, derived from its
            // index rather than a draw, so this stays deterministic too.
            if (_puffs != null && kind != ItemKind.OilSlick)
            {
                for (int i = 0; i < _puffs.Length; i++)
                {
                    var p = _puffs[i];
                    if (p == null) continue;
                    float ph = age * 0.8f + i * 1.7f;
                    p.localPosition = _puffHome[i] + new Vector3(
                        Mathf.Sin(ph) * 0.05f,
                        Mathf.Sin(ph * 0.7f + 1.1f) * 0.06f + age * 0.012f,   // slow rise
                        Mathf.Cos(ph * 0.9f) * 0.05f);
                }
                transform.Rotate(0f, 12f * dt, 0f, Space.World);
            }

            float left = expiresAt - clock;
            float fade = fadeSeconds > 0f ? Mathf.Clamp01(left / fadeSeconds) : 1f;

            var c = tint;
            c.a = baseAlpha * fade * Mathf.Clamp01(age / 0.15f);   // brief fade-in
            _mpb.SetColor("_Color", c);
            foreach (var rend in _renderers)
                if (rend != null) rend.SetPropertyBlock(_mpb);
        }
    }
}
