using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// The lazily-built GUISkin every OnGUI class in the game draws with — one
    /// styling authority, so changing it here reskins every screen at once.
    /// Since the arcade pass it wears the TinyTorque showroom look from the
    /// title art: deep navy panels, champagne-gold accents, rounded corners,
    /// and an OS-loaded display font (Bahnschrift on any Windows 10+ box, no
    /// TTF asset shipped). All backgrounds are tiny runtime-generated textures
    /// (no imported assets). Rebuilt automatically if Unity tears the textures
    /// down on a play-mode transition.
    /// </summary>
    public static class GarageSkin
    {
        public static readonly Color Bg        = new Color(0.055f, 0.075f, 0.13f, 0.97f); // deep navy
        public static readonly Color Panel     = new Color(0.09f, 0.12f, 0.19f, 0.98f);
        public static readonly Color Btn       = new Color(0.13f, 0.17f, 0.26f, 1f);
        public static readonly Color BtnHover  = new Color(0.19f, 0.24f, 0.35f, 1f);
        public static readonly Color Accent    = new Color(0.94f, 0.78f, 0.36f, 1f); // champagne gold
        public static readonly Color AccentDim = new Color(0.55f, 0.43f, 0.18f, 1f);
        public static readonly Color Text      = new Color(0.93f, 0.91f, 0.86f, 1f); // warm white

        private static GUISkin _skin;
        private static Font _font;
        private static readonly Dictionary<Color, Texture2D> _solids = new Dictionary<Color, Texture2D>();
        private static readonly Dictionary<(Color, int), Texture2D> _rounded = new Dictionary<(Color, int), Texture2D>();

        public static GUIStyle Header, StatLabel, TabActive, Title, FocusRing;

        public static GUISkin Skin
        {
            get
            {
                // The stored skin references Texture2Ds; if Unity destroyed them
                // (play-mode change) rebuild everything.
                if (_skin == null || Solid(Bg) == null) Build();
                return _skin;
            }
        }

        public static Texture2D Solid(Color c)
        {
            if (_solids.TryGetValue(c, out var t) && t != null) return t;
            t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels(px); t.Apply(false);
            _solids[c] = t;
            return t;
        }

        /// <summary>
        /// A solid fill with alpha-rounded corners, for 9-slicing (set the
        /// style's border to <c>radius + 2</c> on every side). Cached per
        /// (color, radius); 1 px anti-aliased edge so corners don't shimmer.
        /// </summary>
        public static Texture2D Rounded(Color c, int radius)
        {
            var key = (c, radius);
            if (_rounded.TryGetValue(key, out var t) && t != null) return t;
            t = RoundedTex(c, radius, -1);
            _rounded[key] = t;
            return t;
        }

        /// <summary>
        /// Rounded-rect outline (transparent centre) — the gamepad focus ring.
        /// Distinguished in the cache by a negated radius key so it never
        /// collides with the filled variant of the same color.
        /// </summary>
        public static Texture2D RoundedOutline(Color c, int radius, int thickness)
        {
            var key = (c, -radius);
            if (_rounded.TryGetValue(key, out var t) && t != null) return t;
            t = RoundedTex(c, radius, thickness);
            _rounded[key] = t;
            return t;
        }

        // thickness < 0 = filled. Distance field against the corner circles;
        // pixels outside fade over ~1px, outline keeps only a band of pixels
        // within `thickness` of the edge.
        private static Texture2D RoundedTex(Color c, int radius, int thickness)
        {
            int size = radius * 2 + 8;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color[size * size];
            float r = radius;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance outside the rounded rect (0 inside).
                float dx = Mathf.Max(0f, Mathf.Max(r - 0.5f - x, x - (size - 1 - (r - 0.5f))));
                float dy = Mathf.Max(0f, Mathf.Max(r - 0.5f - y, y - (size - 1 - (r - 0.5f))));
                float dist = Mathf.Sqrt(dx * dx + dy * dy) - r;
                float alpha = Mathf.Clamp01(-dist);           // 1 inside, AA ramp at the edge
                if (thickness >= 0)                            // outline: keep the edge band only
                    alpha = Mathf.Min(alpha, Mathf.Clamp01(dist + thickness + 1f));
                px[y * size + x] = new Color(c.r, c.g, c.b, c.a * alpha);
            }
            t.SetPixels(px); t.Apply(false);
            return t;
        }

        private const int CornerR = 6;   // button corner radius (border = CornerR + 2)
        private const int PanelR  = 8;   // box/panel corner radius

        private static void Build()
        {
            _solids.Clear();
            _rounded.Clear();
            _skin = ScriptableObject.CreateInstance<GUISkin>();

            var baseSkin = GUI.skin;
            // Display font from the OS (Windows-only target) — no TTF asset to
            // ship or strip. Bahnschrift ships with Windows 10+; the fallbacks
            // exist everywhere. If the whole chain fails, keep Unity's default.
            if (_font == null)
                _font = Font.CreateDynamicFontFromOSFont(new[] { "Bahnschrift", "Impact", "Arial" }, 14);
            _skin.font = _font != null ? _font : (baseSkin != null ? baseSkin.font : null);

            var dark = new Color(0.03f, 0.045f, 0.08f, 1f);   // input wells / slider tracks

            _skin.box = new GUIStyle(baseSkin.box);
            _skin.box.normal.background = Rounded(Panel, PanelR);
            _skin.box.normal.textColor = Text;
            _skin.box.hover.background = _skin.box.normal.background;
            _skin.box.hover.textColor = Text;
            _skin.box.border = new RectOffset(PanelR + 2, PanelR + 2, PanelR + 2, PanelR + 2);

            _skin.label = new GUIStyle(baseSkin.label) { normal = { textColor = Text } };

            _skin.button = new GUIStyle(baseSkin.button);
            _skin.button.normal.background = Rounded(Btn, CornerR);
            _skin.button.normal.textColor = Text;
            _skin.button.hover.background = Rounded(BtnHover, CornerR);
            _skin.button.hover.textColor = Text;
            _skin.button.focused.background = _skin.button.normal.background;
            _skin.button.focused.textColor = Text;
            _skin.button.active.background = Rounded(Accent, CornerR);
            _skin.button.active.textColor = Color.black;
            _skin.button.onNormal.background = Rounded(AccentDim, CornerR);
            _skin.button.onNormal.textColor = Color.white;
            _skin.button.border = new RectOffset(CornerR + 2, CornerR + 2, CornerR + 2, CornerR + 2);
            _skin.button.margin = new RectOffset(3, 3, 3, 3);
            _skin.button.padding = new RectOffset(8, 8, 5, 5);

            _skin.toggle = new GUIStyle(baseSkin.toggle) { normal = { textColor = Text }, onNormal = { textColor = Accent } };

            _skin.textField = Style(new GUIStyle(baseSkin.textField), dark, Text, dark);

            _skin.horizontalSlider = new GUIStyle(baseSkin.horizontalSlider);
            _skin.horizontalSlider.normal.background = Solid(dark);
            _skin.horizontalSliderThumb = new GUIStyle(baseSkin.horizontalSliderThumb);
            _skin.horizontalSliderThumb.normal.background = Solid(Accent);
            _skin.horizontalSliderThumb.active.background = Solid(Accent);

            _skin.scrollView = new GUIStyle(baseSkin.scrollView);

            // Copy remaining default styles so unset controls still render.
            _skin.window = new GUIStyle(baseSkin.window);
            _skin.verticalSlider = new GUIStyle(baseSkin.verticalSlider);
            _skin.verticalSliderThumb = new GUIStyle(baseSkin.verticalSliderThumb);
            _skin.textArea = new GUIStyle(baseSkin.textArea);

            Header = new GUIStyle(_skin.label) { fontStyle = FontStyle.Bold, normal = { textColor = Accent } };
            StatLabel = new GUIStyle(_skin.label) { fontSize = 11 };
            TabActive = new GUIStyle(_skin.button);
            TabActive.normal.background = Rounded(AccentDim, CornerR);
            TabActive.normal.textColor = Color.white;

            // Big gold page title, and the gamepad focus ring (an outline box
            // drawn over the focused control on Repaint — explicit rect only,
            // so it never participates in layout).
            Title = new GUIStyle(_skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 26,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Accent },
            };
            FocusRing = new GUIStyle(GUIStyle.none);
            FocusRing.normal.background = RoundedOutline(Accent, CornerR + 2, 2);
            FocusRing.border = new RectOffset(CornerR + 4, CornerR + 4, CornerR + 4, CornerR + 4);
        }

        /// <summary>
        /// A labelled 0..1 slider with the value shown as a percentage, returning
        /// true when it moved.
        ///
        /// Named Slider01 rather than Slider because <c>GarageUI</c> already has a
        /// differently-shaped Slider of its own, and two overloads that took the
        /// same first argument and meant different things would be a trap. It
        /// lives here rather than in a menu because three separate screens draw
        /// this exact row — the options page, the pause panel and the LAN panel —
        /// and a private copy in each is how they drift apart.
        /// </summary>
        public static bool Slider01(string label, ref float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {value:P0}", GUILayout.Width(150));
            float v = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (Mathf.Approximately(v, value)) return false;
            value = v;
            return true;
        }

        private static GUIStyle Style(GUIStyle s, Color normal, Color text, Color hover)
        {
            s.normal.background = Solid(normal);
            s.normal.textColor = text;
            s.hover.background = Solid(hover);
            s.hover.textColor = text;
            s.focused.background = Solid(normal);
            s.focused.textColor = text;
            return s;
        }
    }
}
