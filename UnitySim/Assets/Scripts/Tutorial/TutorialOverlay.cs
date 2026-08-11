using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Tutorial
{
    /// <summary>Where an overlay tutorial starts from.</summary>
    public enum TutorialOverlayEntry
    {
        Menu = 0,
        Multiplayer = 1,
        Garage = 2,
    }

    /// <summary>
    /// The objective panel for tutorials that teach a SCREEN rather than a place:
    /// building a vehicle, hosting a LAN game.
    ///
    /// These have no map, no car and no <see cref="TutorialDirector"/> — so they
    /// get the one thing a director would have given them, drawn over whatever UI
    /// they are about. Static, and deliberately so: the garage lesson starts in
    /// the menu and finishes two scene loads later, and a MonoBehaviour would
    /// have to survive that. <c>GameFlow</c>'s statics make the same trip for the
    /// same reason.
    ///
    /// It shares <see cref="TutorialStepEngine"/> with the driving director, so a
    /// step means exactly the same thing on both paths — same conditions, same
    /// banner timing, same resume.
    ///
    /// <b>Hosts must call three things</b>: <see cref="Tick"/> from Update,
    /// <see cref="Draw"/> at the end of OnGUI, and
    /// <c>TutorialSignals.NotifyScreen</c> from their Layout pass. Nothing here
    /// polls for its host — a screen that has not opted in simply does not show
    /// the panel, which is what a screen with nothing to teach should do.
    /// </summary>
    public static class TutorialOverlay
    {
        private static readonly TutorialStepEngine Engine = new TutorialStepEngine();
        private static string _id = "";
        private static bool _running;
        private static bool _hooked;

        // Layout-snapshotted, same rule the rest of this project's IMGUI follows.
        private static bool _continueDraw, _navDraw;
        private static int _indexDraw, _countDraw;
        private static string _titleDraw = "", _bodyDraw = "", _bannerDraw = "";
        private static float _bannerAlphaDraw;

        private static GUIStyle _title, _body, _step, _banner;

        public static bool Running => _running;
        public static string CurrentId => _id;

        /// <summary>Which screen this lesson opens on.</summary>
        public static TutorialOverlayEntry EntryPage(string id) => id switch
        {
            "online" => TutorialOverlayEntry.Multiplayer,
            "customize" => TutorialOverlayEntry.Garage,
            _ => TutorialOverlayEntry.Menu,
        };

        /// <summary>Start an overlay lesson. Resumes at the saved step when the
        /// player is coming back to this same one.</summary>
        public static void Begin(string id)
        {
            _id = id;
            _running = true;
            Engine.SetSteps(TutorialScripts.For(id));
            if (Tutorials.Active && Tutorials.CurrentId == id && Tutorials.StepIndex > 0)
                Engine.FastForwardTo(Tutorials.StepIndex);

            if (!_hooked)
            {
                _hooked = true;
                Engine.StepCompleted += i => Tutorials.SaveStep(i + 1);
                Engine.Completed += Finish;
            }
            Debug.Log($"[TUT] {id}: overlay lesson, {Engine.Count} steps");
        }

        /// <summary>Stop drawing, with no completion credit — the pause-menu
        /// skip and leaving the screen both land here.</summary>
        public static void Stop()
        {
            _running = false;
            _id = "";
        }

        private static void Finish()
        {
            if (!_running) return;
            _running = false;

            // Banked from the Update side, never from OnGUI: this writes
            // progress.json, and a Layout/Repaint pair would pay twice. The same
            // rule MatchDirector.EnterResults exists to enforce.
            if (Tutorials.Active && Tutorials.CurrentId == _id)
            {
                var paid = Tutorials.CompleteCurrent();
                Debug.Log($"[TUT] {_id} complete — " +
                          (paid.firstTime ? $"paid {paid.scrap} scrap" : "already done"));
            }
            _id = "";
        }

        /// <summary>Drive the lesson. Unscaled, because the menu runs at
        /// <c>timeScale</c> 0 in a paused session and this panel is a menu
        /// thing.</summary>
        public static void Tick()
        {
            if (!_running) return;
            Engine.Tick(Time.unscaledDeltaTime);
        }

        /// <summary>
        /// Draw the panel. Call LAST in the host's OnGUI, after its own
        /// <c>MenuNav.EndFrame</c> — this claims a nav frame of its own for the
        /// Continue button, and claiming before the host would take the pad off
        /// the menu the lesson is teaching.
        /// </summary>
        public static void Draw()
        {
            if (!_running) return;
            EnsureStyles();

            if (Event.current.type == EventType.Layout)
            {
                var step = Engine.Current;
                _continueDraw = Engine.WantsContinue;
                _indexDraw = Engine.Index;
                _countDraw = Engine.Count;
                _titleDraw = step != null ? TutorialText.Expand(step.title) : "";
                _bodyDraw = step != null ? TutorialText.Expand(step.body) : "";
                _bannerDraw = TutorialText.Expand(Engine.Banner);
                _bannerAlphaDraw = Engine.BannerAlpha;
                _navDraw = _continueDraw;
            }
            if (string.IsNullOrEmpty(_titleDraw) && string.IsNullOrEmpty(_bodyDraw)) return;

            const float w = 330f;
            float h = 58f + _body.CalcHeight(new GUIContent(_bodyDraw), w - 24f)
                    + (_continueDraw ? 34f : 0f);
            // Right-hand side: the menu panel is centred and the garage's tools
            // are on the left, so this is the one corner nothing else claims.
            var rect = new Rect(UIScale.W - w - 16f, 16f, w, h);

            if (_navDraw) MenuNav.BeginFrame("tutorial:overlay");
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label(_titleDraw, _title);
            GUILayout.Label(_bodyDraw, _body);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Step {_indexDraw + 1}/{_countDraw}", _step);
            GUILayout.FlexibleSpace();
            if (_continueDraw)
            {
                var opts = new[] { GUILayout.Width(110f), GUILayout.Height(26f) };
                bool hit = _navDraw
                    ? MenuNav.Button("Continue ▶", opts)
                    : GUILayout.Button("Continue ▶", opts);
                if (hit) Engine.PressContinue();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            if (_navDraw) MenuNav.EndFrame();

            if (!string.IsNullOrEmpty(_bannerDraw))
            {
                var prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, _bannerAlphaDraw);
                GUI.Label(new Rect(0f, UIScale.H * 0.34f, UIScale.W, 70f), _bannerDraw, _banner);
                GUI.color = prev;
            }
        }

        private static void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GarageSkin.Header) { fontSize = 15, wordWrap = true };
            _body = new GUIStyle(GarageSkin.StatLabel) { fontSize = 13, wordWrap = true };
            _step = new GUIStyle(GarageSkin.StatLabel)
            { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            _banner = new GUIStyle(GarageSkin.Header)
            { fontSize = 44, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        }
    }
}
