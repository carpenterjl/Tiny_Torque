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

            // Blind first, so the flash and the banner stay legible on top of it.
            DrawBlind(view, r, clock);
            DrawFlash(view, r, clock);
            DrawDriftMeter(view, r, scale);
            DrawBanner(view, r, clock, scale);
            DrawIncoming(view, r, director, clock, scale);
        }

        /// <summary>
        /// The mini-turbo charge meter: how much boost this slide has banked so
        /// far, and which tier it has reached.
        ///
        /// It lives here rather than in <see cref="ArcadeHud"/> for the same
        /// reason everything else in this file does — solo, each split-screen
        /// viewport and a LAN client all need it, and one renderer reading one
        /// racer is the only version that cannot drift out of step with itself.
        /// The charge it reads is always locally computed (a drift is decided by
        /// whichever machine simulates the car), so it is honest on a client too.
        ///
        /// Drawn only while committed. On release the meter's job is done and the
        /// payout announces itself — a bar that lingered would be reporting a
        /// charge the player no longer has.
        /// </summary>
        private static void DrawDriftMeter(Rect view, ArcadeRacer r, float scale)
        {
            if (!r.Drifting) return;

            float w = Mathf.Clamp(view.width * 0.26f, 140f, 340f);
            float h = 11f * scale;
            float x = view.x + (view.width - w) * 0.5f;
            float y = view.y + view.height - 104f * scale;

            var prev = GUI.color;

            // Trough. Dark rather than absent so an empty meter is still a meter —
            // it has to read as "filling" from the first frame.
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(x - 2f, y - 2f, w + 4f, h + 4f), Texture2D.whiteTexture);

            float fill = Mathf.Clamp01(r.driftCharge / ArcadeConfig.DriftChargeFull);
            var c = r.driftTier <= 0
                ? ArcadeConfig.DriftChargeColor
                : ArcadeConfig.DriftTierColors[Mathf.Clamp(r.driftTier - 1, 0,
                    ArcadeConfig.DriftTierColors.Length - 1)];
            // Pulse once a tier is banked, so the promotion is visible without
            // having to watch the bar.
            c.a = r.driftTier > 0
                ? 0.80f + 0.20f * Mathf.Abs(Mathf.Sin(ArcadeDirector.Clock * 9f))
                : 0.75f;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w * fill, h), Texture2D.whiteTexture);

            // Tier gates, so the bar says where the next reward is rather than
            // only how far along it is.
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            DrawTick(x, y, w, h, ArcadeConfig.DriftTier1Seconds);
            DrawTick(x, y, w, h, ArcadeConfig.DriftTier2Seconds);
            DrawTick(x, y, w, h, ArcadeConfig.DriftTier3Seconds);

            GUI.color = prev;
        }

        private static void DrawTick(float x, float y, float w, float h, float seconds)
        {
            float t = Mathf.Clamp01(seconds / ArcadeConfig.DriftChargeFull);
            GUI.DrawTexture(new Rect(x + w * t - 1f, y, 2f, h), Texture2D.whiteTexture);
        }

        /// <summary>
        /// The smoke cloud's green wash. Deliberately NOT shaped like
        /// <see cref="DrawFlash"/>: that decays linearly from the moment of
        /// impact because it is a punch you already took, whereas this HOLDS at
        /// full strength and only fades at the end, because it is a state you are
        /// in and is supposed to cost you the corner.
        ///
        /// The envelope is anchored to <c>blindStartedAt</c> rather than to the
        /// time remaining. On a LAN client <c>blindUntil</c> is only a short
        /// liveness token that each sync packet pushes forward again, so a
        /// remaining-time envelope would sit permanently mid-fade there while
        /// looking perfectly correct in solo.
        ///
        /// Being pulled at draw time is what makes it free in split-screen and on
        /// a client: each viewport reads its own racer, and nothing had to be
        /// added to the director's per-frame loop.
        /// </summary>
        private static void DrawBlind(Rect view, ArcadeRacer r, float clock)
        {
            if (clock >= r.blindUntil) return;

            float ramp = Mathf.Clamp01((clock - r.blindStartedAt) /
                                       Mathf.Max(0.01f, ArcadeConfig.BlindRampSeconds));
            float fade = Mathf.Clamp01((r.blindUntil - clock) /
                                       Mathf.Max(0.01f, ArcadeConfig.BlindFadeSeconds));

            var prev = GUI.color;
            var c = ArcadeConfig.BlindFeedbackColor;
            c.a = ArcadeConfig.BlindTintAlpha * ramp * fade;
            GUI.color = c;
            GUI.DrawTexture(view, Texture2D.whiteTexture);
            GUI.color = prev;
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
