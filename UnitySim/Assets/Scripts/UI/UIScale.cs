using UnityEngine;
using AIHWSim.Core;

namespace AIHWSim.UI
{
    /// <summary>
    /// Global IMGUI scale. The whole UI was authored in fixed pixels against a
    /// ~1080p window, so at 4K it rendered half-size and at 720p it overflowed.
    /// Every game-facing OnGUI now wraps its body in <see cref="Begin"/> /
    /// <see cref="End"/>, which scales GUI.matrix so one UI unit is one 1080p
    /// pixel, and measures the screen through <see cref="W"/>/<see cref="H"/>
    /// instead of Screen.width/height.
    ///
    /// GUI.matrix transforms IMGUI's own event coordinates automatically, so
    /// buttons keep hit-testing correctly for free. The two things it does NOT
    /// transform are (a) hand-rolled pointer-vs-rect tests that start from
    /// InputReader.PointerPosition() — those must use <see cref="GuiPointer"/> —
    /// and (b) Camera.ScreenPointToRay picking, which must stay in raw screen
    /// pixels. Developer overlays (graph/metrics/mission/sensor HUDs) are
    /// deliberately left unscaled: they are tools, and their density is a
    /// feature.
    /// </summary>
    public static class UIScale
    {
        /// <summary>UI scale factor (1 at 1080p, 2 at 4K), clamped so extreme
        /// windows stay usable.</summary>
        public static float S => Mathf.Clamp(Screen.height / 1080f, 0.6f, 2.5f);

        /// <summary>Screen size in UI units — use instead of Screen.width/height
        /// inside a Begin/End block.</summary>
        public static float W => Screen.width / S;
        public static float H => Screen.height / S;

        private static Matrix4x4 _saved;

        public static void Begin()
        {
            _saved = GUI.matrix;
            float s = S;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f)) * _saved;
        }

        public static void End()
        {
            GUI.matrix = _saved;
        }

        /// <summary>Pointer position in UI units, GUI convention (origin
        /// top-left, y down) — for the hand-rolled panel-rect hover tests.</summary>
        public static Vector2 GuiPointer()
        {
            var p = InputReader.PointerPosition();          // screen px, origin bottom-left
            float s = S;
            return new Vector2(p.x / s, (Screen.height - p.y) / s);
        }

        /// <summary>Convert a rect measured in real screen pixels (e.g. a
        /// camera pixelRect) into UI units for drawing under the scaled matrix.</summary>
        public static Rect FromScreen(Rect screenRect)
        {
            float s = S;
            return new Rect(screenRect.x / s, screenRect.y / s, screenRect.width / s, screenRect.height / s);
        }
    }
}
