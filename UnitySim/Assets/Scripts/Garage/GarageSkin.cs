using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// A lazily-built dark "VAB" GUISkin for the garage, plus a few shared styles.
    /// All backgrounds are tiny runtime-generated textures (no imported assets), so
    /// the garage gets a consistent KSP-ish look while staying IMGUI. Rebuilt
    /// automatically if Unity tears the textures down on a play-mode transition.
    /// </summary>
    public static class GarageSkin
    {
        public static readonly Color Bg        = new Color(0.10f, 0.11f, 0.13f, 0.96f);
        public static readonly Color Panel     = new Color(0.14f, 0.15f, 0.18f, 0.98f);
        public static readonly Color Btn       = new Color(0.20f, 0.22f, 0.26f, 1f);
        public static readonly Color BtnHover  = new Color(0.26f, 0.29f, 0.34f, 1f);
        public static readonly Color Accent    = new Color(1.00f, 0.62f, 0.20f, 1f); // KSP orange
        public static readonly Color AccentDim = new Color(0.55f, 0.36f, 0.14f, 1f);
        public static readonly Color Text      = new Color(0.86f, 0.88f, 0.92f, 1f);

        private static GUISkin _skin;
        private static readonly Dictionary<Color, Texture2D> _solids = new Dictionary<Color, Texture2D>();

        public static GUIStyle Header, StatLabel, TabActive;

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

        private static void Build()
        {
            _solids.Clear();
            _skin = ScriptableObject.CreateInstance<GUISkin>();

            var baseSkin = GUI.skin;
            // Start from sensible defaults, then override the pieces we care about.
            _skin.font = baseSkin != null ? baseSkin.font : null;

            _skin.box = Style(new GUIStyle(baseSkin.box), Panel, Text, Panel);
            _skin.box.border = new RectOffset(2, 2, 2, 2);

            _skin.label = new GUIStyle(baseSkin.label) { normal = { textColor = Text } };

            _skin.button = Style(new GUIStyle(baseSkin.button), Btn, Text, BtnHover);
            _skin.button.active.background = Solid(Accent);
            _skin.button.active.textColor = Color.black;
            _skin.button.onNormal.background = Solid(AccentDim);
            _skin.button.onNormal.textColor = Color.white;
            _skin.button.border = new RectOffset(2, 2, 2, 2);
            _skin.button.margin = new RectOffset(3, 3, 3, 3);
            _skin.button.padding = new RectOffset(6, 6, 4, 4);

            _skin.toggle = new GUIStyle(baseSkin.toggle) { normal = { textColor = Text }, onNormal = { textColor = Accent } };

            _skin.textField = Style(new GUIStyle(baseSkin.textField), new Color(0.06f, 0.07f, 0.09f, 1f), Text, new Color(0.06f, 0.07f, 0.09f, 1f));

            _skin.horizontalSlider = new GUIStyle(baseSkin.horizontalSlider);
            _skin.horizontalSlider.normal.background = Solid(new Color(0.06f, 0.07f, 0.09f, 1f));
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
            TabActive.normal.background = Solid(AccentDim);
            TabActive.normal.textColor = Color.white;
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
