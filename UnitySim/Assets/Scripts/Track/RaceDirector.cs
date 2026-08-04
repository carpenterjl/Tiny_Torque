using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Simple first-to-N-laps race over the shared <see cref="LapTimer"/>.
    /// Inert in sandbox sessions (targetLaps 0) or on finish-less maps. Shows a
    /// live standings banner while racing and a results overlay when every
    /// player has finished, with Keep driving / Rematch / Main Menu.
    /// Works in single-player too (a solo time-attack to N laps).
    ///
    /// The countdown, the finish event, the results frame and the crate reveal
    /// live in <see cref="MatchDirector"/>, shared with the arena modes. What is
    /// here is what a RACE is: laps, checkpoint progress, and rubber-banding.
    /// </summary>
    public sealed class RaceDirector : MatchDirector
    {
        public int targetLaps;
        public LapTimer timer;

        // Optional catch-up assist (menu race option): trailing bots speed up,
        // leaders ease off. Populated by TrackBootstrap.
        public List<BotDriver> bots = new List<BotDriver>();
        public bool rubberBand;

        /// <summary>Arcade session: the arcade HUD owns the live board, and the
        /// results table gains a points column.</summary>
        public bool arcade;

        /// <summary>
        /// Once the FIRST car finishes, show results after this long whether or not
        /// the rest have. 0 = classic behaviour (wait for everyone), which is what
        /// every non-arcade race keeps.
        ///
        /// This exists because "everyone must finish" is a real hang in arcade: a
        /// bot that keeps getting spun out — its own stuck recovery takes ~4 s a go
        /// — can hold the results screen hostage indefinitely.
        /// </summary>
        public float resultsGraceSeconds;
        private float _graceUntil;
        private bool _graceRunning;

        private sealed class Entry
        {
            public PlayerRig rig;
            public bool finished;
            public float totalTime;   // sum of counted lap times
            public int place;         // 1-based finish order
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private int _nextPlace = 1;

        protected override bool CanRun => timer != null && targetLaps > 0 && players.Count > 0;

        protected override void OnMatchStart()
        {
            foreach (var rig in players)
                _entries.Add(new Entry { rig = rig });
            timer.LapCompleted += OnLap;
        }

        private void OnDestroy()
        {
            if (timer != null) timer.LapCompleted -= OnLap;
        }

        /// <summary>
        /// On a point-to-point course the run starts here rather than at a line,
        /// so every car's clock starts together when the grid is released. On a
        /// circuit this is a no-op: the timer's flag is off and arming stays what
        /// it has always been, the first crossing of the start/finish gate.
        /// </summary>
        protected override void OnGridReleased()
        {
            if (timer != null && timer.armAtStart) timer.ArmAll();
        }

        public override void RestartMatch()
        {
            base.RestartMatch();
            _graceRunning = false;
        }

        /// <summary>Pause-menu "Restart race" — kept under its old name because
        /// PauseMenu calls it by that name.</summary>
        public void RestartRace() => RestartMatch();

        public override void ResetMatch()
        {
            base.ResetMatch();
            foreach (var e in _entries)
            {
                e.finished = false;
                e.totalTime = 0f;
                e.place = 0;
            }
            _nextPlace = 1;
            _graceRunning = false;
        }

        public void ResetRace() => ResetMatch();

        protected override void OnMatchTick()
        {
            // Leader's grace expired: everyone still out there is a DNF.
            if (_graceRunning && !ShowingResults && Time.time >= _graceUntil)
            {
                _graceRunning = false;
                foreach (var e in _entries) if (!e.finished) e.place = 0;
                EnterResults();
            }

            if (!rubberBand || bots == null || bots.Count == 0 || timer == null) return;

            // Reference progress = the human's (first non-bot entry).
            float human = 0f;
            bool haveHuman = false;
            foreach (var e in _entries)
                if (!e.rig.slot.isBot) { human = Progress(e.rig); haveHuman = true; break; }
            if (!haveHuman) return;

            // Trailing bots speed up, leaders ease off — keeps the pack together.
            foreach (var e in _entries)
            {
                if (!e.rig.slot.isBot) continue;
                var bot = e.rig.input != null ? e.rig.input.source as AIHWSim.Core.BotDriver : null;
                if (bot == null) continue;
                float gap = human - Progress(e.rig); // >0 = bot behind
                bot.SpeedScale = Mathf.Clamp(1f + gap * 0.15f, 0.85f, 1.25f);
            }
        }

        /// <summary>Coarse race progress in laps (lap count + checkpoint fraction).</summary>
        private float Progress(PlayerRig rig)
        {
            var t = timer.GetTracker(rig.car);
            int cps = Mathf.Max(1, timer.CheckpointCount);
            return t.LapCount + (float)t.NextCheckpoint / cps;
        }

        private void OnLap(CarVehicle car, LapTracker t)
        {
            var e = Find(car);
            if (e == null || e.finished) return;
            e.totalTime += t.LastLap;
            if (t.LapCount >= targetLaps)
            {
                e.finished = true;
                e.place = _nextPlace++;
                RaiseFinished(e.rig, e.place);
                if (AllFinished()) EnterResults();
                else if (resultsGraceSeconds > 0f && !_graceRunning)
                {
                    _graceRunning = true;
                    _graceUntil = Time.time + resultsGraceSeconds;
                }
            }
        }

        /// <summary>
        /// A championship round banks its points exactly once and no matter how
        /// it ended, because the base calls this from the single door into the
        /// results overlay rather than from OnGUI.
        /// </summary>
        protected override void OnResultsEntered()
        {
            if (!SessionConfig.ChampionshipRound || !Core.Championship.Active) return;
            var rows = new List<(string, int)>(_entries.Count);
            foreach (var e in _entries)
                if (e.rig?.slot != null) rows.Add((e.rig.slot.name, e.place));
            Core.Championship.RecordRound(rows);
        }

        private Entry Find(CarVehicle car)
        {
            foreach (var e in _entries)
                if (e.rig.car == car) return e;
            return null;
        }

        private bool AllFinished()
        {
            foreach (var e in _entries)
                if (!e.finished) return false;
            return true;
        }

        // ---- overlay ---------------------------------------------------------

        // Which BUTTONS the results panel offers ("Next round" vs "Rematch")
        // must be decided once per frame, like the overlay itself.
        private bool _champDraw;

        protected override void OnLayoutSnapshot() =>
            _champDraw = SessionConfig.ChampionshipRound && Core.Championship.Active;

        protected override void DrawLiveBanner()
        {
            // In arcade the ArcadeHud draws the live board; two banners centred
            // at the same y would just overlap.
            if (arcade) return;
            var area = new Rect((UIScale.W - 260f) * 0.5f, 40f, 260f, 26f + _entries.Count * 18f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label($"RACE — first to {targetLaps} laps", GarageSkin.Header);
            foreach (var e in Standings())
            {
                var lap = timer.GetTracker(e.rig.car);
                string state = e.finished ? $"FINISHED P{e.place}" : $"lap {lap.LapCount}/{targetLaps}";
                GUILayout.Label($"{e.rig.slot.name}: {state}", GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }

        protected override string ResultsTitle
        {
            get
            {
                if (!_champDraw) return "RACE RESULTS";
                var series = Core.Championship.Series;
                return Core.Championship.Complete
                    ? $"{series?.label} — FINAL"
                    : $"{series?.label} — ROUND {Core.Championship.State.round} of {Core.Championship.Rounds}";
            }
        }

        protected override float ResultRowsHeight =>
            _entries.Count * 26f + (_champDraw ? 46f + _entries.Count * 18f : 0f);

        protected override float ResultButtonsHeight => 30f;

        protected override void DrawResultRows()
        {
            foreach (var e in Standings())
            {
                var lap = timer.GetTracker(e.rig.car);
                string best = lap.HasBest ? Fmt(lap.BestLap) : "--:--";
                string place = e.place > 0 ? $"P{e.place}" : "DNF";
                string pts = arcade && e.rig.arcade != null ? $"   {e.rig.arcade.points} pts" : "";
                GUILayout.Label($"{place}  {e.rig.slot.name}   total {Fmt(e.totalTime)}   best {best}{pts}");
            }
            if (_champDraw) DrawChampionshipTable(Core.Championship.Complete);
        }

        protected override void DrawResultButtons()
        {
            bool champ = _champDraw;
            bool finale = champ && Core.Championship.Complete;

            if (!champ && MenuNav.Button("Keep driving", GUILayout.Height(30)))
                DismissResults();
            // A championship swaps the rematch for the next round: re-running a
            // round you already scored would be a cheat, and the standings only
            // move forward.
            if (champ && !finale && MenuNav.Button("Next round ▶", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                AwardReveal.Dismiss();
                Core.Championship.LoadNextRoundTrack();
                ScreenFade.To(GameFlow.LoadTrack); // SessionConfig persists; scene rebuilds fresh
            }
            if (!champ && MenuNav.Button("Rematch", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                AwardReveal.Dismiss();
                ScreenFade.To(GameFlow.LoadTrack); // SessionConfig persists; scene rebuilds fresh
            }
        }

        /// <summary>The series points table under the race result, and the
        /// trophy line once the last round has been scored.</summary>
        private void DrawChampionshipTable(bool finale)
        {
            GUILayout.Space(6);
            var head = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(finale ? "FINAL STANDINGS" : "CHAMPIONSHIP", head);

            int pos = 1;
            foreach (var (name, points, isBot) in Core.Championship.Standings())
                GUILayout.Label($"{pos++}.  {name}{(isBot ? "" : "  (you)")}   {points} pts",
                    GarageSkin.StatLabel);

            if (!finale) return;
            var big = new GUIStyle(GarageSkin.Title) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label(Core.Championship.PlayerWon()
                ? "CHAMPION — Gold Vault earned"
                : "Series over — better luck next season", big);
        }

        private List<Entry> Standings()
        {
            var sorted = new List<Entry>(_entries);
            sorted.Sort((a, b) =>
            {
                if (a.finished != b.finished) return a.finished ? -1 : 1;
                if (a.finished) return a.place.CompareTo(b.place);
                int lapA = timer.GetTracker(a.rig.car).LapCount;
                int lapB = timer.GetTracker(b.rig.car).LapCount;
                return lapB.CompareTo(lapA);
            });
            return sorted;
        }
    }
}
