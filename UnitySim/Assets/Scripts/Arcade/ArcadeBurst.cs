using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// A short-lived expanding, fading flash — the missile explosion, and the
    /// pop when a shield is consumed.
    ///
    /// It animates itself and destroys its own GameObject, so the director can
    /// fire one and forget it: there is no burst list to keep, nothing to clean
    /// up on a race restart, and nothing that outlives the car it went off next
    /// to. Fading runs through a MaterialPropertyBlock rather than per-instance
    /// materials, so ten explosions in a lap do not leak ten materials.
    /// </summary>
    public sealed class ArcadeBurst : MonoBehaviour
    {
        public float duration = 0.6f;
        public float startScale = 0.25f;
        public float endScale = 1.6f;
        public Color tint = Color.white;

        private Renderer[] _renderers;
        private MaterialPropertyBlock _mpb;
        private float _born;

        /// <summary>Spawn a burst at a world point. Layer is VizLayer so the on-car
        /// camera sensors never see it — the same rule every part visual follows.</summary>
        public static ArcadeBurst Spawn(Vector3 pos, float radius, Color tint, float seconds)
        {
            var go = new GameObject("ArcadeBurst");
            go.layer = Vehicles.PartVisualFactory.VizLayer;
            go.transform.position = pos;

            ArcadeVfx.BuildExplosion(go.transform);

            var b = go.AddComponent<ArcadeBurst>();
            b.duration = seconds;
            b.startScale = radius * 0.35f;
            b.endScale = radius;
            b.tint = tint;
            return b;
        }

        private void Start()
        {
            _born = Time.time;
            _renderers = GetComponentsInChildren<Renderer>(true);
            _mpb = new MaterialPropertyBlock();
            transform.localScale = Vector3.one * startScale;
        }

        private void Update()
        {
            // Time.time, not ArcadeDirector.Clock: this is a cosmetic animation and
            // should stop with the pause menu exactly like everything else visual.
            float t = duration > 0f ? (Time.time - _born) / duration : 1f;
            if (t >= 1f) { Destroy(gameObject); return; }

            transform.localScale = Vector3.one * Mathf.Lerp(startScale, endScale, t);
            transform.Rotate(0f, 140f * Time.deltaTime, 0f, Space.Self);

            var c = tint;
            c.a = 1f - t;
            _mpb.SetColor("_Color", c);
            _mpb.SetColor("_EmissionColor", c * (1f - t) * 2f);
            foreach (var r in _renderers)
                if (r != null) r.SetPropertyBlock(_mpb);
        }
    }
}
