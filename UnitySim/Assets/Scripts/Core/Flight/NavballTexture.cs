using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Paints the navball's skin: an equirectangular sky/ground sphere map with a
    /// pitch ladder and a heading band, generated at runtime into a
    /// <see cref="Texture2D"/>.
    ///
    /// <b>Equirectangular, because that is what a UV sphere wants.</b> Unity's
    /// primitive sphere carries the standard latitude/longitude unwrap — v runs
    /// from the south pole to the north, u once around — so painting in
    /// (longitude, latitude) and handing the result over is the whole projection.
    /// There is no maths here beyond two linear maps.
    ///
    /// Generated rather than imported for the same reason every other texture in
    /// this project is (see <c>GarageSkin</c>, <c>TrackBuilder</c>): it ships no
    /// asset, and the tick spacing is a number in a file rather than something
    /// baked into a PNG nobody can edit. The 3x5 glyph routine is lifted from
    /// <c>TrackEd/BillboardPoster.cs</c> and extended with digits, which that one
    /// does not have.
    /// </summary>
    public static class NavballTexture
    {
        private const int W = 512;
        private const int H = 256;

        // KSP's palette, near enough: a blue that darkens toward the zenith and a
        // brown that darkens toward the nadir, so "which way is up" survives even
        // when the horizon itself is off the edge of the ball.
        private static readonly Color SkyLow = new Color(0.42f, 0.62f, 0.85f);
        private static readonly Color SkyHigh = new Color(0.16f, 0.28f, 0.52f);
        private static readonly Color GroundHigh = new Color(0.48f, 0.38f, 0.24f);
        private static readonly Color GroundLow = new Color(0.20f, 0.15f, 0.09f);
        private static readonly Color Ink = new Color(0.96f, 0.96f, 0.94f);
        private static readonly Color Faint = new Color(0.96f, 0.96f, 0.94f, 0.45f);

        public static Texture2D Build()
        {
            var px = new Color[W * H];

            // ---- base: sky above the equator, ground below ----
            for (int y = 0; y < H; y++)
            {
                float lat = y / (H - 1f) * 180f - 90f;          // −90 south .. +90 north
                float t = Mathf.Abs(lat) / 90f;
                Color c = lat >= 0f ? Color.Lerp(SkyLow, SkyHigh, t)
                                    : Color.Lerp(GroundHigh, GroundLow, t);
                for (int x = 0; x < W; x++) px[y * W + x] = c;
            }

            // ---- pitch ladder ----
            for (int d = -80; d <= 80; d += 5)
            {
                if (d == 0) continue;
                int y = LatToY(d);
                bool major = d % 10 == 0;
                // Minor lines are short dashes, major ones run the whole way round:
                // a solid line reads as "this is a decade", which is what you count.
                for (int x = 0; x < W; x++)
                {
                    if (!major && (x / 6) % 4 != 0) continue;
                    Blend(px, x, y, major ? Faint : new Color(0.96f, 0.96f, 0.94f, 0.25f));
                }
            }

            // Pitch numerals at each quadrant meridian, so one set is always in view
            // whichever way the aircraft is pointing.
            for (int lon = 0; lon < 360; lon += 90)
                for (int d = -80; d <= 80; d += 10)
                {
                    if (d == 0) continue;
                    int x = LonToX(lon) + 6;
                    int y = LatToY(d) - 3;
                    Text(px, Mathf.Abs(d).ToString(), x, y, 1, Faint);
                }

            // ---- the horizon: the one line that must be unmistakable ----
            for (int dy = -1; dy <= 1; dy++)
                for (int x = 0; x < W; x++)
                    Set(px, x, LatToY(0) + dy, Ink);

            // ---- heading band, sitting just above the horizon ----
            int bandBase = LatToY(0) + 4;
            for (int lon = 0; lon < 360; lon += 5)
            {
                int x = LonToX(lon);
                int len = lon % 10 == 0 ? 6 : 3;
                for (int dy = 0; dy < len; dy++) Blend(px, x, bandBase + dy, Faint);
            }

            for (int lon = 0; lon < 360; lon += 30)
            {
                string label = lon switch
                {
                    0 => "N", 90 => "E", 180 => "S", 270 => "W",
                    _ => (lon / 10).ToString(),
                };
                bool cardinal = lon % 90 == 0;
                int scale = cardinal ? 3 : 2;
                int wide = label.Length * 4 * scale - scale;
                Text(px, label, LonToX(lon) - wide / 2, bandBase + 10,
                     scale, cardinal ? Ink : Faint);
            }

            var tex = new Texture2D(W, H, TextureFormat.RGBA32, mipChain: true)
            {
                name = "NavballSkin",
                hideFlags = HideFlags.HideAndDontSave,
                wrapModeU = TextureWrapMode.Repeat,     // longitude wraps
                wrapModeV = TextureWrapMode.Clamp,      // latitude does not
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(px);
            // Left READABLE (the usual second argument would free the CPU copy).
            // Half a megabyte, once, in exchange for [NAVB] being able to check
            // that the sky is on top — an assertion worth more than the memory.
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            return tex;
        }

        // ---- plumbing ----

        private static int LatToY(float lat) => Mathf.RoundToInt((lat + 90f) / 180f * (H - 1));
        private static int LonToX(float lon) => Mathf.RoundToInt(lon / 360f * W) % W;

        private static void Set(Color[] px, int x, int y, Color c)
        {
            if (y < 0 || y >= H) return;
            px[y * W + ((x % W) + W) % W] = c;
        }

        private static void Blend(Color[] px, int x, int y, Color c)
        {
            if (y < 0 || y >= H) return;
            int i = y * W + ((x % W) + W) % W;
            px[i] = Color.Lerp(px[i], new Color(c.r, c.g, c.b), c.a);
        }

        // ---- 3x5 pixel font, rows top->bottom, 3 bits per row (MSB left) ----
        // Letters as in TrackEd/BillboardPoster.Glyph; digits added here, which is
        // the reason this is a copy rather than a call.
        private static int Glyph(char c) => c switch
        {
            '0' => 0b111_101_101_101_111, '1' => 0b010_110_010_010_111,
            '2' => 0b111_001_111_100_111, '3' => 0b111_001_111_001_111,
            '4' => 0b101_101_111_001_001, '5' => 0b111_100_111_001_111,
            '6' => 0b111_100_111_101_111, '7' => 0b111_001_001_001_001,
            '8' => 0b111_101_111_101_111, '9' => 0b111_101_111_001_111,
            'N' => 0b110_101_101_101_101, 'E' => 0b111_100_110_100_111,
            'S' => 0b011_100_010_001_110, 'W' => 0b101_101_111_111_101,
            _ => 0,
        };

        private static void Text(Color[] px, string s, int x0, int y0, int scale, Color ink)
        {
            int x = x0;
            foreach (char c in s)
            {
                int g = Glyph(char.ToUpperInvariant(c));
                if (g != 0)
                    for (int row = 0; row < 5; row++)
                        for (int col = 0; col < 3; col++)
                        {
                            if ((g >> ((4 - row) * 3 + (2 - col)) & 1) == 0) continue;
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                    Blend(px, x + col * scale + dx,
                                          y0 + (4 - row) * scale + dy, ink);
                        }
                x += 4 * scale;   // 3 wide + 1 gap
            }
        }
    }
}
