using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Everything a set of rules needs that is not the rules: the start
    /// countdown that holds the grid, the "this player is done" event other
    /// systems hang crate payouts off, the one-way door into the results
    /// overlay, and the overlay's frame.
    ///
    /// Extracted from <see cref="RaceDirector"/> when the arena modes arrived,
    /// because all four of them want exactly this and none of them wants laps.
    /// The split is deliberately shallow — a race's lap rules did not move, and
    /// a subclass overrides only the handful of members below rather than
    /// implementing a lifecycle.
    ///
    /// Lives beside RaceDirector rather than in AIHWSim.Modes so the dependency
    /// runs one way: Modes → Track → Core.
    /// </summary>
    public abstract class MatchDirector : MonoBehaviour
    {
        public List<PlayerRig> players = new List<PlayerRig>();

        /// <summary>Start countdown in seconds that holds every car until GO.</summary>
        public int countdownSeconds;

        /// <summary>
        /// Raised when a car is finished with the match, with its 1-based place.
        /// The arcade layer awards points from it and TrackBootstrap pays crates;
        /// a derby raises it as cars are eliminated, a race as they cross the
        /// line, and both mean the same thing to a listener.
        /// </summary>
        public event System.Action<PlayerRig, int> PlayerFinished;

        protected void RaiseFinished(PlayerRig rig, int place) => PlayerFinished?.Invoke(rig, place);

        protected float _countdown;
        protected bool _counting;
        protected float _goTimer;
        protected bool _showResults;
        protected bool _dismissed;

        /// <summary>Layout-snapshotted "the results overlay is up". Which
        /// controls exist must not change between Layout and Repaint, and a
        /// dismiss button flips <see cref="_dismissed"/> mid-pass.</summary>
        private bool _resultsDraw;

        public bool ShowingResults => _showResults;

        // ---- what a mode has to supply ---------------------------------------

        /// <summary>Refuse to run at all (a race with no laps set, a mode with no
        /// players). The component disables itself rather than half-existing.</summary>
        protected virtual bool CanRun => players.Count > 0;

        /// <summary>Heading on the results panel.</summary>
        protected abstract string ResultsTitle { get; }

        /// <summary>One label per participant, best first.</summary>
        protected abstract void DrawResultRows();

        /// <summary>Panel height the rows need, so the box fits its contents.</summary>
        protected virtual float ResultRowsHeight => players.Count * 26f;

        /// <summary>The live overlay while the match runs. Optional: arcade
        /// sessions let the ArcadeHud own the board instead.</summary>
        protected virtual void DrawLiveBanner() { }

        /// <summary>Buttons between the rows and Main Menu — rematch, next round,
        /// keep driving, whatever the mode offers.</summary>
        protected virtual void DrawResultButtons() { }

        protected virtual float ResultButtonsHeight => 0f;

        /// <summary>Hook for one-time setup after the base has validated.</summary>
        protected virtual void OnMatchStart() { }

        /// <summary>Per-frame rules, once the countdown has released the grid.</summary>
        protected virtual void OnMatchTick() { }

        /// <summary>Called exactly once, the moment results are entered — the
        /// place to bank anything that writes to disk.</summary>
        protected virtual void OnResultsEntered() { }

        // ---- lifecycle --------------------------------------------------------

        protected virtual void Start()
        {
            if (!CanRun) { enabled = false; return; }
            OnMatchStart();
            if (countdownSeconds > 0)
            {
                _countdown = countdownSeconds;
                _counting = true;
                FreezeCars(true);
                return;
            }
            // No countdown means the grid was never held, so the moment of release
            // is now. Said out loud rather than left implicit, because a mode that
            // starts a clock at GO has to start it in BOTH shapes of start — and
            // "0 seconds of countdown" is the shape every legacy entry path uses.
            OnGridReleased();
        }

        /// <summary>
        /// The grid is free and the rules are live. Called exactly once per start
        /// or restart, from all three places a start can happen: a countdown
        /// reaching zero, a match with no countdown at all, and a restart.
        ///
        /// Empty here on purpose — the base class has nothing that happens at GO.
        /// It exists so a mode that does (a point-to-point race, whose clock
        /// starts at GO rather than at a line) has one place to say so instead of
        /// three.
        /// </summary>
        protected virtual void OnGridReleased() { }

        protected void FreezeCars(bool frozen)
        {
            foreach (var rig in players)
                if (rig?.car != null) rig.car.Frozen = frozen;
        }

        protected virtual void Update()
        {
            // Music mood, pushed per-frame (idempotent): the countdown ducks the
            // track theme, the results screen brings in the victory theme.
            Audio.MusicDirector.RaceMood(_counting, _showResults && !_dismissed);

            if (_counting)
            {
                _countdown -= Time.deltaTime; // scaled: a pause also holds the countdown
                if (_countdown <= 0f)
                {
                    _counting = false;
                    _goTimer = 0.8f;
                    FreezeCars(false);
                    OnGridReleased();
                }
                return;                       // grid is held — no rules yet
            }
            if (_goTimer > 0f) _goTimer -= Time.deltaTime;
            OnMatchTick();
        }

        /// <summary>
        /// The single door into the results overlay, so whatever ends a match
        /// banks its consequences exactly once. Deliberately NOT called from
        /// OnGUI: this writes to progress.json, and a Layout/Repaint pair would
        /// do it twice.
        /// </summary>
        protected void EnterResults()
        {
            if (_showResults) return;
            _showResults = true;
            OnResultsEntered();
        }

        /// <summary>Pause-menu "Restart": clear the standings and re-run the
        /// countdown.</summary>
        public virtual void RestartMatch()
        {
            ResetMatch();
            if (countdownSeconds > 0)
            {
                _countdown = countdownSeconds;
                _counting = true;
                _goTimer = 0f;
                FreezeCars(true);
            }
            else
            {
                _counting = false;
                FreezeCars(false);
                OnGridReleased();
            }
        }

        public virtual void ResetMatch()
        {
            _showResults = false;
            _dismissed = false;
            Arcade.ArcadeDirector.Instance?.ResetArcade();
        }

        // ---- overlay ----------------------------------------------------------

        protected virtual void OnGUI()
        {
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();
            if (Event.current.type == EventType.Layout)
            {
                _resultsDraw = _showResults && !_dismissed;
                OnLayoutSnapshot();
            }
            if (_resultsDraw)
            {
                MenuNav.BeginFrame("match:results");
                DrawResults();
                MenuNav.EndFrame();
                UIScale.End();
                return;
            }
            if (_counting)
            {
                DrawBig(Mathf.CeilToInt(Mathf.Max(0f, _countdown)).ToString());
                UIScale.End();
                return;
            }
            if (_goTimer > 0f) DrawBig("GO!");
            DrawLiveBanner();
            UIScale.End();
        }

        /// <summary>Extra Layout-pass snapshots a subclass needs.</summary>
        protected virtual void OnLayoutSnapshot() { }

        protected static void DrawBig(string s)
        {
            var style = new GUIStyle(GarageSkin.Header) { fontSize = 80, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0f, UIScale.H * 0.22f, UIScale.W, 140f), s, style);
        }

        private void DrawResults()
        {
            float w = 360f, h = 170f + ResultRowsHeight
                + (AwardReveal.Pending ? 84f : 0f) + ResultButtonsHeight;
            var area = new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            var title = new GUIStyle(GarageSkin.Header) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(ResultsTitle, title);
            GUILayout.Space(6);

            DrawResultRows();

            // The crates this match paid out announce themselves right here.
            AwardReveal.Draw();

            GUILayout.Space(8);
            DrawResultButtons();
            if (MenuNav.Button("Main Menu", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                AwardReveal.Dismiss();
                if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                    ScreenFade.To(GameFlow.LoadMenu);
            }
            GUILayout.EndArea();
        }

        /// <summary>Leave the overlay up but stop blocking the view.</summary>
        protected void DismissResults()
        {
            _dismissed = true;
            AwardReveal.Dismiss();
        }

        protected static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
