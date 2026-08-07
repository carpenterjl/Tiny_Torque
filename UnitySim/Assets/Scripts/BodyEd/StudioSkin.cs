using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Vehicle Studio's look: the garage's skin, one size up.
    ///
    /// <b>A derived skin rather than a bigger <c>GarageSkin</c>.</b> That one is
    /// the styling authority for every screen in the game — the menu, the pause
    /// panel, the HUD's own boxes — and enlarging its font to suit a tool panel
    /// would enlarge all of them. This copies the styles, raises the type and the
    /// hit targets, and leaves the shared skin exactly as it was, so the studio can
    /// be as chunky as it wants to be at nobody else's expense.
    ///
    /// The colours, the rounded backgrounds and the focus ring all come from
    /// <c>GarageSkin</c> untouched, so the studio still looks like the same game.
    /// </summary>
    public static class StudioSkin
    {
        /// <summary>Body text size. The shared skin's is the Unity default 12;
        /// this is the "larger text" the tool asks for, in one number.</summary>
        public const int FontSize = 15;

        /// <summary>Minimum height of a clickable row. Big enough to hit with a
        /// stick-driven cursor without aiming.</summary>
        public const float RowHeight = 30f;

        /// <summary>Side of a palette icon button.</summary>
        public const float IconSize = 62f;

        private static GUISkin _skin;
        private static GUISkin _source;

        public static GUIStyle Header, Sub, Hint, Tab, TabActive, IconButton, Swatch, Danger;

        /// <summary>
        /// The studio's skin, rebuilt whenever the shared one is — which happens
        /// when Unity tears its textures down on a play-mode transition. Comparing
        /// against the SOURCE instance is what detects that: a stale copy of a
        /// rebuilt skin references textures that no longer exist.
        /// </summary>
        public static GUISkin Skin
        {
            get
            {
                GUISkin src = GarageSkin.Skin;
                if (_skin == null || _source != src) Build(src);
                return _skin;
            }
        }

        private static void Build(GUISkin src)
        {
            _source = src;
            _skin = Object.Instantiate(src);
            _skin.name = "StudioSkin";

            Bump(_skin.label);
            Bump(_skin.button);
            Bump(_skin.box);
            Bump(_skin.toggle);
            Bump(_skin.textField);
            Bump(_skin.textArea);

            _skin.button.padding = new RectOffset(10, 10, 7, 7);
            _skin.button.margin = new RectOffset(3, 4, 3, 4);
            _skin.textField.padding = new RectOffset(8, 8, 6, 6);

            // Sliders get a taller track and a wider thumb: the default 12 px thumb
            // is a hard target with a gamepad cursor and a fiddly one with a mouse
            // when the value being set is a body panel's shape.
            _skin.horizontalSlider.fixedHeight = 18f;
            _skin.horizontalSliderThumb.fixedHeight = 18f;
            _skin.horizontalSliderThumb.fixedWidth = 18f;

            Header = new GUIStyle(_skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = FontSize + 2,
                normal = { textColor = GarageSkin.Accent },
                margin = new RectOffset(2, 2, 8, 2),
            };
            Sub = new GUIStyle(_skin.label)
            {
                fontSize = FontSize - 2,
                normal = { textColor = new Color(0.72f, 0.74f, 0.78f) },
                wordWrap = true,
            };
            Hint = new GUIStyle(_skin.label)
            {
                fontSize = FontSize - 1,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.80f, 0.83f, 0.88f) },
            };
            Tab = new GUIStyle(_skin.button) { fontSize = FontSize, fixedHeight = RowHeight };
            TabActive = new GUIStyle(GarageSkin.TabActive)
            {
                fontSize = FontSize, fixedHeight = RowHeight,
            };
            IconButton = new GUIStyle(_skin.button)
            {
                fontSize = FontSize - 3,
                alignment = TextAnchor.LowerCenter,
                imagePosition = ImagePosition.ImageAbove,
                fixedWidth = IconSize,
                fixedHeight = IconSize,
                padding = new RectOffset(2, 2, 3, 3),
            };
            Swatch = new GUIStyle(_skin.box) { fixedHeight = 22f };
            Danger = new GUIStyle(_skin.button)
            {
                fontSize = FontSize,
                normal = { textColor = new Color(1f, 0.62f, 0.58f) },
            };
        }

        private static void Bump(GUIStyle s)
        {
            if (s != null) s.fontSize = FontSize;
        }
    }
}
