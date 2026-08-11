using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// Runs a driving tutorial: the steps authored under this object, in order,
    /// with the objective panel, the banners and the completion payout.
    ///
    /// <b>It is a <see cref="MatchDirector"/>, authored in the scene.</b> Two
    /// facts make that work rather than fight. A tutorial session runs as
    /// <c>MatchMode.FreeRoam</c>, which is the one mode TrackBootstrap composes
    /// NO director for — so this one is the only director alive, with no
    /// countdown it did not ask for and no second results overlay behind its own.
    /// And the base class already owns everything a tutorial needs: the big
    /// centre-screen text, the results frame with its crate reveal, and
    /// <c>EnterResults</c> — the single door that guarantees a payout is banked
    /// exactly once, off the OnGUI path where a Layout/Repaint pair would pay
    /// twice.
    ///
    /// <b>Its clock is <c>Time.deltaTime</c>, so pausing holds it.</b> A banner
    /// half-read when the player hits Escape is still half-read when they come
    /// back, and a two-second hold does not complete itself while the pause menu
    /// is up.
    ///
    /// Pressing Play directly in a tutorial scene works: TrackBootstrap adopts an
    /// open scene track, and the steps bind to whatever rig it composed. That is
    /// the whole point of authoring the steps in the scene — the edit loop is
    /// open the scene, drag a volume, press Play.
    /// </summary>
    public sealed class TutorialDirector : MatchDirector
    {
        [Tooltip("Which catalogue entry this scene is. Must match a " +
                 "TutorialCatalog id, or completing it banks nothing.")]
        public string tutorialId = "";

        [Tooltip("Freeze the car until the first step that needs it. Off by " +
                 "default: a player who cannot move while being told about " +
                 "moving is being told off.")]
        public bool holdCarAtStart = false;

        private readonly TutorialStepEngine _engine = new TutorialStepEngine();
        private Tutorials.Payout _payout;
        private bool _bankedThisRun;

        // Layout-snapshotted: which controls exist must not change between the
        // Layout and Repaint passes, and the Continue button's presence flips
        // with the step.
        private bool _continueDraw, _navDraw;
        private int _indexDraw;
        private string _titleDraw = "", _bodyDraw = "", _bannerDraw = "";
        private float _bannerAlphaDraw;

        private static GUIStyle _panelTitle, _panelBody, _panelStep, _bannerStyle;

        protected override string ResultsTitle => "TUTORIAL COMPLETE";

        /// <summary>
        /// Run even with no players. Every other mode needs cars to have rules
        /// about; a tutorial's first lesson may be about a menu, and disabling
        /// itself would take the objective panel with it.
        /// </summary>
        protected override bool CanRun => true;

        protected override void Start()
        {
            countdownSeconds = 0;      // a lesson does not start on a grid
            CollectSteps();
            base.Start();              // validates, calls OnMatchStart, releases
        }

        protected override void OnMatchStart()
        {
            BindRig();

            // Resume: come back to the step the player left off at, not to the
            // gate they already drove through. Only when the saved progress is
            // about THIS tutorial — replaying an old one starts at the top.
            if (Tutorials.Active && Tutorials.CurrentId == tutorialId && Tutorials.StepIndex > 0)
                _engine.FastForwardTo(Tutorials.StepIndex);

            _engine.StepCompleted += OnStepCompleted;
            _engine.Completed += OnStepsFinished;

            if (holdCarAtStart) FreezeCars(true);

            Debug.Log($"[TUT] {tutorialId}: {_engine.Count} steps, starting at {_engine.Index + 1}");
        }

        /// <summary>
        /// Steps are the TutorialStep children of this object, in sibling order.
        /// Inactive ones are skipped, which makes disabling a step in the
        /// inspector the way to take it out of a lesson without deleting the
        /// work.
        /// </summary>
        private void CollectSteps()
        {
            var list = new List<TutorialStepData>();
            foreach (Transform child in transform)
            {
                if (!child.gameObject.activeSelf) continue;
                var step = child.GetComponent<TutorialStep>();
                if (step != null && step.enabled) list.Add(step.ToData());
            }
            _engine.SetSteps(list);
        }

        /// <summary>
        /// Find the car being taught. TrackBootstrap composed it before this
        /// Start ran (its Awake builds the rigs), and in FreeRoam nothing else
        /// claims the list — so the human rig is simply the first non-bot one.
        ///
        /// EVERY TrackBootstrap is searched, not the first one found. A scene
        /// track loads with two of them — the authored scene's and TrackScene's —
        /// and the one that stood down has an empty <c>Rigs</c>. Taking
        /// whichever Unity returned first is a coin flip between the real
        /// session and nothing at all.
        /// </summary>
        private void BindRig()
        {
            players.Clear();
            foreach (var boot in FindObjectsByType<TrackBootstrap>(FindObjectsSortMode.None))
            {
                foreach (var rig in boot.Rigs)
                {
                    if (rig?.slot == null || rig.slot.isBot) continue;
                    players.Add(rig);
                    break;
                }
                if (players.Count > 0) break;
            }

            var mine = players.Count > 0 ? players[0] : null;
            _engine.Bind(mine?.car, mine?.input);
            TutorialProbes.Hub = mine?.runner != null ? mine.runner.Hub : null;
        }

        protected override void OnMatchTick()
        {
            if (_showResults) return;
            _engine.Tick(Time.deltaTime);   // scaled: a pause holds the lesson
        }

        private void OnStepCompleted(int index)
        {
            // The resume point is the NEXT step: the one just finished is done.
            Tutorials.SaveStep(index + 1);
            if (holdCarAtStart) FreezeCars(false);
        }

        private void OnStepsFinished() => EnterResults();

        /// <summary>
        /// The single bank-to-disk point (see <c>MatchDirector.EnterResults</c>).
        /// Guarded again here because a scene can be restarted from the pause
        /// menu, and finishing the same tutorial twice in one sitting must not
        /// pay twice.
        /// </summary>
        protected override void OnResultsEntered()
        {
            if (_bankedThisRun) return;
            _bankedThisRun = true;

            if (Tutorials.Active && Tutorials.CurrentId == tutorialId)
            {
                _payout = Tutorials.CompleteCurrent();
                Debug.Log($"[TUT] {tutorialId} complete — " +
                          (_payout.firstTime ? $"paid {_payout.scrap} scrap" : "already done") +
                          (_payout.crate ? ", plus the all-tutorials crate" : ""));
            }
            else
            {
                // Reached by pressing Play in the scene directly, or by a scene
                // whose id does not match the catalogue. Nothing to bank; the
                // lesson still ran, and the results panel still says so.
                Debug.Log($"[TUT] {tutorialId} complete (not a tracked run — nothing banked)");
            }
        }

        public override void ResetMatch()
        {
            base.ResetMatch();
            _bankedThisRun = false;
            _payout = null;
            CollectSteps();
            BindRig();
        }

        // ---- results ------------------------------------------------------------

        protected override float ResultRowsHeight => 74f;

        protected override void DrawResultRows()
        {
            EnsureStyles();
            GUILayout.Label(TutorialCatalog.LabelOf(tutorialId), _panelTitle);
            GUILayout.Space(4);
            if (_payout == null)
                GUILayout.Label("Lesson finished.", _panelBody);
            else if (_payout.firstTime)
                GUILayout.Label($"+{_payout.scrap} scrap", _panelBody);
            else
                GUILayout.Label("Already completed — no scrap this time.", _panelBody);

            if (_payout != null && _payout.crate)
                GUILayout.Label("Every tutorial done. That earned a Gold Vault.", _panelBody);
        }

        protected override float ResultButtonsHeight => Tutorials.HasNext ? 68f : 34f;

        protected override void DrawResultButtons()
        {
            if (Tutorials.HasNext)
            {
                string next = TutorialCatalog.LabelOf(Tutorials.CurrentId);
                if (MenuNav.Button($"Next: {next} ▶", GUILayout.Height(30)))
                {
                    Time.timeScale = 1f;
                    AwardReveal.Dismiss();
                    // An overlay tutorial has no map to load; it picks up in the
                    // menu, so both branches end at "leave this scene".
                    if (Tutorials.LaunchCurrent()
                        && Application.CanStreamedLevelBeLoaded(GameFlow.TrackSceneName))
                        ScreenFade.To(GameFlow.LoadTrack);
                    else
                        GoToHub();
                }
            }
            if (MenuNav.Button("Tutorial menu", GUILayout.Height(30))) GoToHub();
        }

        private static void GoToHub()
        {
            Time.timeScale = 1f;
            AwardReveal.Dismiss();
            Tutorials.PendingOpenHub = true;
            if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                ScreenFade.To(GameFlow.LoadMenu);
        }

        // ---- the live overlay -----------------------------------------------------

        protected override void OnLayoutSnapshot()
        {
            var step = _engine.Current;
            _continueDraw = _engine.WantsContinue && !_showResults;
            // Claim the pad only while the game is running. Paused, PauseMenu is
            // the UI in front and must own the gamepad — and whichever OnGUI runs
            // first in a frame is the one MenuNav gives it to, which is not an
            // order this class gets to decide. Registering a control without
            // owning the frame is worse still: it would land in the pause menu's
            // census and shift every focus index below it.
            _navDraw = _continueDraw && Time.timeScale > 0f;
            _indexDraw = _engine.Index;
            _titleDraw = step != null ? TutorialText.Expand(step.title) : "";
            _bodyDraw = step != null ? TutorialText.Expand(step.body) : "";
            _bannerDraw = TutorialText.Expand(_engine.Banner);
            _bannerAlphaDraw = _engine.BannerAlpha;
        }

        protected override void DrawLiveBanner()
        {
            EnsureStyles();
            DrawObjectivePanel();
            DrawStepBanner();
        }

        /// <summary>
        /// The corner card: what to do, and how far through. Persistent rather
        /// than a timed banner because the objective is the one thing a player
        /// who looked away needs to be able to find again.
        /// </summary>
        private void DrawObjectivePanel()
        {
            if (string.IsNullOrEmpty(_titleDraw) && string.IsNullOrEmpty(_bodyDraw)) return;

            const float w = 330f;
            float h = 58f + _panelBody.CalcHeight(new GUIContent(_bodyDraw), w - 24f)
                    + (_continueDraw ? 34f : 0f);
            if (_navDraw) MenuNav.BeginFrame("tutorial:step");

            GUILayout.BeginArea(new Rect(16f, 16f, w, h), GUI.skin.box);
            GUILayout.Label(_titleDraw, _panelTitle);
            GUILayout.Label(_bodyDraw, _panelBody);
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Step {_indexDraw + 1}/{_engine.Count}", _panelStep);
            GUILayout.FlexibleSpace();
            if (_continueDraw)
            {
                // Nav-wrapped only when this panel owns the pad; paused, it is a
                // plain button so the mouse still works and MenuNav's census
                // stays the pause menu's alone.
                var opts = new[] { GUILayout.Width(110f), GUILayout.Height(26f) };
                bool hit = _navDraw
                    ? MenuNav.Button("Continue ▶", opts)
                    : GUILayout.Button("Continue ▶", opts);
                if (hit) _engine.PressContinue();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (_navDraw) MenuNav.EndFrame();
        }

        /// <summary>The centre flash when a step lands. Fades over its last
        /// stretch, the way <c>ArcadeFeedback</c>'s banner does.</summary>
        private void DrawStepBanner()
        {
            if (string.IsNullOrEmpty(_bannerDraw)) return;
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _bannerAlphaDraw);
            GUI.Label(new Rect(0f, UIScale.H * 0.34f, UIScale.W, 70f), _bannerDraw, _bannerStyle);
            GUI.color = prev;
        }

        /// <summary>Built once. A GUIStyle per OnGUI pass is an allocation per
        /// frame per label, which is the standard trap in this codebase's UI.</summary>
        private static void EnsureStyles()
        {
            if (_panelTitle != null) return;
            _panelTitle = new GUIStyle(GarageSkin.Header) { fontSize = 15, wordWrap = true };
            _panelBody = new GUIStyle(GarageSkin.StatLabel) { fontSize = 13, wordWrap = true };
            _panelStep = new GUIStyle(GarageSkin.StatLabel)
            { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            _bannerStyle = new GUIStyle(GarageSkin.Header)
            { fontSize = 44, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        }
    }
}
