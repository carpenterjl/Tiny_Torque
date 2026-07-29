using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Puts an ad on a billboard.
    ///
    /// The source kit authors its billboard faces BLANK — a cream slab under
    /// three floodlights — so this is deliberate new authoring, not a pipeline
    /// repair (the user chose in-game generated art over re-authoring the
    /// Blender kit). The poster is drawn at runtime into a small texture — the
    /// same procedural-texture family as TrackBuilder's noise/stripe floors and
    /// the attic wallpaper — with a chunky 3x5 pixel font, and shown as a
    /// double-sided quad seated just proud of the measured face plate.
    ///
    /// The variant is picked in Start from a hash of the WORLD position, which
    /// is only final after TrackFactory poses the item — and is identical on
    /// every machine that builds the same map, so LAN peers and replays agree
    /// on which corner advertises the soda. One material per variant, cached,
    /// so a town of billboards adds a handful of materials, not one per board.
    /// </summary>
    public sealed class BillboardPoster : MonoBehaviour
    {
        private const float Proud = 0.002f;      // clear of the face plate
        private const float Inset = 0.94f;       // poster margin inside the frame

        private void Start()
        {
            // The face plate is the cream piece; its thinnest axis is depth.
            Renderer face = null;
            foreach (var r in GetComponentsInChildren<Renderer>(true))
                if (r.gameObject.name.ToLowerInvariant().Contains("cream"))
                { face = r; break; }
            if (face == null) return;

            var b = Vehicles.PartVisualFactory.LocalRendererBounds(transform, face.transform);
            if (b.size.sqrMagnitude < 1e-8f) return;

            // Depth = the smallest extent; the poster spans the other two axes.
            Vector3 s = b.size;
            int depthAxis = s.x < s.y ? (s.x < s.z ? 0 : 2) : (s.y < s.z ? 1 : 2);
            if (depthAxis == 1) return;          // a flat-lying slab is not a billboard

            int wAxis = depthAxis == 0 ? 2 : 0;
            float w = s[wAxis] * Inset, h = s.y * Inset;
            float half = s[depthAxis] * 0.5f + Proud;

            int variant = Mathf.Abs(Mathf.RoundToInt(
                transform.position.x * 37f + transform.position.z * 101f)) % BillboardArt.Variants;

            var go = new GameObject("Poster") { layer = gameObject.layer };
            go.transform.SetParent(transform, false);
            go.transform.localPosition = b.center;
            // Local +Z looks along the depth axis.
            if (depthAxis == 0) go.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = DoubleSided(w, h, half);
            go.AddComponent<MeshRenderer>().sharedMaterial = BillboardArt.Mat(variant);
        }

        /// <summary>Two opposed faces either side of the plate, the back face's
        /// UVs mirrored so its text still reads left-to-right.</summary>
        private static Mesh DoubleSided(float w, float h, float half)
        {
            float x = w * 0.5f, y = h * 0.5f;
            var m = new Mesh { name = "Poster" };
            m.vertices = new[]
            {
                new Vector3(-x, -y,  half), new Vector3(x, -y,  half),
                new Vector3(-x,  y,  half), new Vector3(x,  y,  half),
                new Vector3(-x, -y, -half), new Vector3(x, -y, -half),
                new Vector3(-x,  y, -half), new Vector3(x,  y, -half),
            };
            m.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(1, 0), new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1),
            };
            m.triangles = new[]
            {
                0, 2, 1, 1, 2, 3,      // front (+Z, wound to face +Z)
                5, 7, 4, 4, 7, 6,      // back (-Z)
            };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>The poster art itself: a handful of seeded ad variants drawn
    /// once each into a shared texture + material.</summary>
    public static class BillboardArt
    {
        public const int Variants = 4;
        private const int W = 256, H = 96;

        private static readonly Material[] _mats = new Material[Variants];

        public static Material Mat(int variant)
        {
            variant = Mathf.Clamp(variant, 0, Variants - 1);
            if (_mats[variant] == null)
            {
                var m = new Material(Shader.Find("Standard")) { color = Color.white };
                m.SetFloat("_Glossiness", 0.55f);
                m.mainTexture = Draw(variant);
                // A lit board: a touch of emission so the bloom pass reads it
                // as a printed ad under floodlights, not a light source.
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0.55f, 0.55f, 0.55f));
                m.name = "Prop_poster_" + variant;
                _mats[variant] = m;
            }
            return _mats[variant];
        }

        private static Texture2D Draw(int v)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };

            (Color bg, Color band, Color ink, string big, string small)[] ads =
            {
                (new Color(0.93f, 0.34f, 0.18f), new Color(0.99f, 0.83f, 0.30f),
                 Color.white, "TINY TORQUE", "FEEL THE TORQUE"),
                (new Color(0.16f, 0.42f, 0.72f), new Color(0.88f, 0.94f, 0.99f),
                 new Color(0.99f, 0.90f, 0.35f), "SPEED SODA", "ZERO TO FIZZ"),
                (new Color(0.15f, 0.15f, 0.17f), new Color(0.88f, 0.20f, 0.16f),
                 Color.white, "OPUS OIL", "RUNS FOREVER"),
                (new Color(0.20f, 0.55f, 0.36f), new Color(0.95f, 0.95f, 0.90f),
                 new Color(0.13f, 0.13f, 0.15f), "CRATE DAY", "WIN A LEGEND"),
            };
            var ad = ads[v];

            var px = new Color[W * H];
            for (int i = 0; i < px.Length; i++) px[i] = ad.bg;
            // A diagonal accent band, the one-shape logo every discount
            // agency reaches for.
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (x + y * 2 > W + 20 && x + y * 2 < W + 92) px[y * W + x] = ad.band;

            Text(px, ad.big, 12, H - 40, 5, ad.ink);
            Text(px, ad.small, 12, 14, 2, ad.ink);

            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }

        // ---- 3x5 pixel font, rows top->bottom, 3 bits per row (MSB left) ----
        private static int Glyph(char c) => c switch
        {
            'A' => 0b010_101_111_101_101, 'B' => 0b110_101_110_101_110,
            'C' => 0b011_100_100_100_011, 'D' => 0b110_101_101_101_110,
            'E' => 0b111_100_110_100_111, 'F' => 0b111_100_110_100_100,
            'G' => 0b011_100_101_101_011, 'H' => 0b101_101_111_101_101,
            'I' => 0b111_010_010_010_111, 'J' => 0b001_001_001_101_010,
            'K' => 0b101_110_100_110_101, 'L' => 0b100_100_100_100_111,
            'M' => 0b101_111_111_101_101, 'N' => 0b110_101_101_101_101,
            'O' => 0b010_101_101_101_010, 'P' => 0b110_101_110_100_100,
            'Q' => 0b010_101_101_110_011, 'R' => 0b110_101_110_110_101,
            'S' => 0b011_100_010_001_110, 'T' => 0b111_010_010_010_010,
            'U' => 0b101_101_101_101_111, 'V' => 0b101_101_101_101_010,
            'W' => 0b101_101_111_111_101, 'X' => 0b101_101_010_101_101,
            'Y' => 0b101_101_010_010_010, 'Z' => 0b111_001_010_100_111,
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
                                {
                                    int X = x + col * scale + dx;
                                    int Y = y0 + (4 - row) * scale + dy;
                                    if (X >= 0 && X < W && Y >= 0 && Y < H)
                                        px[Y * W + X] = ink;
                                }
                        }
                x += 4 * scale;   // 3 wide + 1 gap
            }
        }
    }
}
