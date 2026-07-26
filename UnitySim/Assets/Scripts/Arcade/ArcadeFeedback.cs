using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// The "you just got hit" overlay: an impact colour wash, a banner naming
    /// what happened, and a standing warning while a missile is locked onto you.
    ///
    /// A static drawn into a caller-supplied rect rather than a MonoBehaviour,
    /// because it has two homes that must look identical: <see cref="ArcadeHud"/>
    /// draws it full-screen in solo, and <c>SplitScreenHud</c> draws it inside
    /// each player's viewport. Anything stateful lives on <see cref="ArcadeRacer"/>,
    /// so both callers read the same fields and neither owns the other.
    ///
    /// Everything here reads <see cref="ArcadeDirector.Clock"/>, not Time.time, so
    /// a paused race holds the banner instead of eating it.
    /// </summary>
    public static class ArcadeFeedback
    {
        private static GUIStyle _banner;
        private static GUIStyle _warn;

        /// <summary>Built once. IMGUI styles allocated per OnGUI churn the GC
        /// every frame for no benefit.</summary>
        private static void EnsureStyles()
        {
            if (_banner != null) return;
            _banner = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
            _warn = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
        }

        /// <summary>
        /// Draw the flash, banner and incoming warning for one player.
        /// </summary>
        /// <param name="view">The player's screen area in GUI space (top-left
        /// origin). Full screen in solo, one half in split-screen.</param>
        public static void Draw(Rect view, ArcadeRacer r, ArcadeDirector director)
        {
            if (r == null) return;
            EnsureStyles();

            float clock = ArcadeDirector.Clock;
            // Scale text with the viewport so a split-screen half is not shouted
            // at in the same point size as a full screen.
            float scale = Mathf.Clamp(view.height / 900f, 0.55f, 1.25f);

            DrawFlash(view, r, clock);
            DrawBanner(view, r, clock, scale);
            DrawIncoming(view, r, director, clock, scale);
        }

        private static void DrawFlash(Rect view, ArcadeRacer r, float clock)
        {
            if (clock >= r.flashUntil) return;
            float t = Mathf.Clamp01((r.flashUntil - clock) / Mathf.Max(0.01f, ArcadeConfig.HitFlashSeconds));
            var prev = GUI.color;
            var c = r.flashColor;
            // Peak alpha is deliberately low: this is a punch you feel, not a
            // tint you have to see through mid-corner.
            c.a = t * 0.30f;
            GUI.color = c;
            GUI.DrawTexture(view, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private static void DrawBanner(Rect view, ArcadeRacer r, float clock, float scale)
        {
            if (string.IsNullOrEmpty(r.hitLabel) || clock >= r.hitUntil) return;

            // Fade the last third so it leaves rather than vanishes.
            float left = r.hitUntil - clock;
            var col = r.hitColor;
            col.a = Mathf.Clamp01(left / 0.5f);

            _banner.fontSize = Mathf.RoundToInt(30f * scale);
            _banner.normal.textColor = col;
            GUI.Label(new Rect(view.x, view.y + view.height * 0.34f, view.width, 44f * scale),
                r.hitLabel, _banner);
        }

        private static void DrawIncoming(Rect view, ArcadeRacer r, ArcadeDirector director,
            float clock, float scale)
        {
            if (director == null || r.car == null) return;
            if (!director.IncomingMissile(r.car)) return;

            // Pulse, so it reads as an alarm rather than as furniture. A missile
            // that can be dodged is only fair if you are told it is coming.
            float pulse = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(clock * 6f));
            _warn.fontSize = Mathf.RoundToInt(20f * scale);
            _warn.normal.textColor = new Color(1f, 0.30f, 0.24f, pulse);
            GUI.Label(new Rect(view.x, view.y + view.height * 0.24f, view.width, 30f * scale),
                "!  MISSILE INCOMING  !", _warn);
        }
    }
}
