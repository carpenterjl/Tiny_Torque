using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Surface colour detector: raycasts along local +Z and reports the RGB of
    /// whatever it sees (0..1 each), black when nothing is in range. The fourth
    /// field is Rec.709 luminance — aimed straight down, that is the classic
    /// line-follower reflectance signal.
    ///
    /// Colour resolution chain, best answer first:
    ///  1. <see cref="ISurfaceColorProvider"/> on the hit collider or a parent
    ///     (painted line strips, coloured props — the exact answer);
    ///  2. MeshCollider hit with a readable Texture2D main texture →
    ///     GetPixelBilinear at the hit UV (floor tiles: their textures are
    ///     runtime-generated and stay CPU-readable);
    ///  3. the renderer's sharedMaterial colour tint (flat-tinted geometry).
    ///     Honest limit: RaycastHit.textureCoord is only valid for
    ///     MeshColliders, so a BOX-collider floor with a texture reads the
    ///     tint — line courses need MeshCollider strips or a provider;
    ///  4. no renderer / no hit → black.
    /// </summary>
    public sealed class ColorSensor : SensorComponent
    {
        [Header("Color detector")]
        [Tooltip("Maximum sensing distance (m). Real modules work at a few cm.")]
        public float maxRange = 0.3f;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "r", "g", "b", "reflect" };
        // Shared scratch buffer for non-allocating raycasts (TofSensor idiom).
        private static readonly RaycastHit[] HitBuf = new RaycastHit[8];
        private Transform _ignoreRoot;

        public override SensorType Type => SensorType.Color;
        public override int DataCount => 4;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _ignoreRoot = vehicleRoot;
            rangeMin = 0f;
            rangeMax = 1f;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            Color c = Color.black;
            int n = Physics.RaycastNonAlloc(transform.position, transform.forward,
                HitBuf, maxRange, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < n; i++)
            {
                var h = HitBuf[i];
                // Skip the vehicle's own colliders (body + wheels).
                if (_ignoreRoot != null && h.collider != null &&
                    h.collider.transform.IsChildOf(_ignoreRoot))
                    continue;
                if (h.distance < bestDist) { bestDist = h.distance; bestIdx = i; }
            }
            if (bestIdx >= 0) c = ResolveColor(HitBuf[bestIdx]);

            // Noise per channel in a fixed order (deterministic RNG draw count),
            // then reflectance recomputed from the NOISY rgb so the four fields
            // stay consistent with each other.
            float r = Mathf.Clamp01(noise.Apply(c.r, dt));
            float g = Mathf.Clamp01(noise.Apply(c.g, dt));
            float b = Mathf.Clamp01(noise.Apply(c.b, dt));
            dest[offset]     = r;
            dest[offset + 1] = g;
            dest[offset + 2] = b;
            dest[offset + 3] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        private static Color ResolveColor(in RaycastHit hit)
        {
            if (hit.collider == null) return Color.black;

            // 1. Exact-answer provider on the collider or a parent.
            var provider = hit.collider.GetComponentInParent<ISurfaceColorProvider>();
            if (provider != null && provider.TryGetSurfaceColor(in hit, out var exact))
                return exact;

            var rend = hit.collider.GetComponentInParent<Renderer>();
            var mat = rend != null ? rend.sharedMaterial : null;
            if (mat == null) return Color.black;

            // 2. Texel read — only meaningful when the hit UV is real, i.e. a
            //    MeshCollider, and the texture is CPU-readable.
            if (hit.collider is MeshCollider && mat.mainTexture is Texture2D tex)
            {
                try
                {
                    Vector2 uv = hit.textureCoord;
                    uv = Vector2.Scale(uv, mat.mainTextureScale) + mat.mainTextureOffset;
                    Color texel = tex.GetPixelBilinear(uv.x, uv.y);
                    return mat.HasProperty("_Color") ? texel * mat.color : texel;
                }
                catch (UnityException)
                {
                    // Non-readable texture: fall through to the tint.
                }
            }

            // 3. Material tint.
            return mat.HasProperty("_Color") ? mat.color : Color.white;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * maxRange);
        }
    }
}
