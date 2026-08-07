using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The finishes a channel can wear, generated rather than shipped.
    ///
    /// <b>Generated for the same reason every other texture in this project is.</b>
    /// The asset pack is editor-only, the arcade shells carry no UV artwork beyond
    /// three baked liveries, and a finish that exists as code is a finish that
    /// cannot go missing from a build. Each pattern is authored as a greyscale
    /// MASK and multiplied by the channel's colour at bind time, so one 128×128
    /// texture serves every colour anybody picks instead of one per (pattern,
    /// colour) pair.
    ///
    /// The patterns are deliberately geometric. A photographic finish would need
    /// UVs that mean something, and on a body whose vertices somebody has just
    /// dragged about, they do not — which is exactly what the triplanar material
    /// exists to work around. A grid, a stripe and a weave read correctly under
    /// projection; a photograph of leather does not.
    /// </summary>
    public static class StudioTextures
    {
        /// <summary>Key + label + the tiling that suits it, in the palette's
        /// order. The empty key is first and means a flat colour — it is a real
        /// choice, not a null, so the cycle control has something to show.</summary>
        public static readonly (string key, string label, float tiling)[] All =
        {
            ("", "Flat", 1f),
            ("checker", "Checker", 6f),
            ("stripe", "Stripes", 8f),
            ("carbon", "Carbon", 14f),
            ("grid", "Grid", 10f),
            ("dots", "Dots", 9f),
            ("noise", "Speckle", 5f),
        };

        private static readonly Dictionary<string, Texture2D> _cache =
            new Dictionary<string, Texture2D>();

        public static string LabelOf(string key)
        {
            foreach (var e in All) if (e.key == key) return e.label;
            return key;
        }

        /// <summary>The tiling a finish is authored for. A <see cref="FeatureTint"/>
        /// storing 0 means "whatever this pattern wants", which is why an absent
        /// JSON key cannot collapse the UVs.</summary>
        public static float DefaultTiling(string key)
        {
            foreach (var e in All) if (e.key == key) return e.tiling;
            return 1f;
        }

        public static int IndexOf(string key)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].key == key) return i;
            return 0;
        }

        /// <summary>
        /// The mask for a finish, or null for the flat one. Cached; the cache
        /// survives a play-mode exit no better than any other runtime object, so
        /// every entry is re-checked for having been destroyed under us — the
        /// same guard <c>GarageSkin</c> puts on its own textures.
        /// </summary>
        public static Texture2D Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out var t) && t != null) return t;

            t = Build(key);
            if (t == null) return null;
            _cache[key] = t;
            return t;
        }

        private const int N = 128;

        private static Texture2D Build(string key)
        {
            var px = new Color[N * N];
            switch (key)
            {
                case "checker": Checker(px); break;
                case "stripe": Stripe(px); break;
                case "carbon": Carbon(px); break;
                case "grid": Grid(px); break;
                case "dots": Dots(px); break;
                case "noise": Speckle(px); break;
                default:
                    Debug.LogWarning($"[StudioTextures] No finish named '{key}' — drawing flat.");
                    return null;
            }

            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true)
            {
                name = "Studio_" + key,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        private static void Set(Color[] px, int x, int y, float v) =>
            px[y * N + x] = new Color(v, v, v, 1f);

        private static void Checker(Color[] px)
        {
            const int cell = N / 4;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    Set(px, x, y, ((x / cell + y / cell) & 1) == 0 ? 1f : 0.62f);
        }

        private static void Stripe(Color[] px)
        {
            const int band = N / 8;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    // A 45° diagonal, so a stripe still reads as a stripe on a
                    // panel the projection happens to hit edge-on.
                    int d = (x + y) % (band * 2);
                    Set(px, x, y, d < band ? 1f : 0.55f);
                }
        }

        private static void Carbon(Color[] px)
        {
            // A 2×2 twill: alternating tiles of horizontal and vertical fibre,
            // each shaded across its width so the weave catches the light.
            const int tile = N / 4;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    bool horizontal = ((x / tile + y / tile) & 1) == 0;
                    int across = horizontal ? y % tile : x % tile;
                    float t = across / (float)(tile - 1);
                    float shade = 0.45f + 0.55f * Mathf.Sin(Mathf.PI * t);
                    Set(px, x, y, shade * (horizontal ? 1f : 0.86f));
                }
        }

        private static void Grid(Color[] px)
        {
            const int cell = N / 8;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    bool line = x % cell < 2 || y % cell < 2;
                    Set(px, x, y, line ? 1f : 0.42f);
                }
        }

        private static void Dots(Color[] px)
        {
            const int cell = N / 6;
            float r = cell * 0.32f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = x % cell - cell * 0.5f;
                    float dy = y % cell - cell * 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // One pixel of feather, so the dots do not crawl at distance.
                    Set(px, x, y, Mathf.Lerp(1f, 0.45f, Mathf.Clamp01(d - r + 1f)));
                }
        }

        private static void Speckle(Color[] px)
        {
            // Deterministic: an integer hash, not Random, so the same finish is the
            // same finish in every session and on every machine on a LAN.
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int h = x * 374761393 + y * 668265263;
                    h = (h ^ (h >> 13)) * 1274126177;
                    float v = ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
                    Set(px, x, y, 0.65f + 0.35f * v);
                }
        }
    }
}
