using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
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
    /// </summary>
    public sealed class RaceDirector : MonoBehaviour
    {
        public int targetLaps;
        public LapTimer timer;
        public List<PlayerRig> players = new List<PlayerRig>();

        // Optional catch-up assist (menu race option): trailing bots speed up,
        // leaders ease off. Populated by TrackBootstrap; logic in Update (Step 5).
        public List<BotDriver> bots = new List<BotDriver>();
        public bool rubberBand;

        // Race-start countdown (seconds) that holds every car until GO.
        public int countdownSeconds;
        private float _countdown;
        private bool _counting;
        private float _goTimer;

        private sealed class Entry
        {
            public PlayerRig rig;
            public bool finished;
            public float totalTime;   // sum of counted lap times
            public int place;         // 1-based finish order
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private int _nextPlace = 1;
        private bool _showResults;
        private bool _dismissed;

        private void Start()
        {
            if (timer == null || targetLaps <= 0 || players.Count == 0)
            {
                enabled = false;
                return;
            }
            foreach (var rig in players)
                _entries.Add(new Entry { rig = rig });
            timer.LapCompleted += OnLap;

            if (countdownSeconds > 0)
            {
                _countdown = countdownSeconds;
                _counting = true;
                FreezeCars(true);
            }
        }

        private void FreezeCars(bool frozen)
        {
            foreach (var e in _entries)
                if (e.rig?.car != null) e.rig.car.Frozen = frozen;
        }

        /// <summary>Pause-menu "Restart race": reset standings and re-run the countdown.</summary>
        public void RestartRace()
        {
            ResetRace();
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
            }
        }

        private void OnDestroy()
        {
            if (timer != null) timer.LapCompleted -= OnLap;
        }

        private void Update()
        {
            if (_counting)
            {
                _countdown -= Time.deltaTime; // scaled: a pause also holds the countdown
                if (_countdown <= 0f)
                {
                    _counting = false;
                    _goTimer = 0.8f;
                    FreezeCars(false);
                }
                return; // grid is held — no rubber-band yet
            }
            if (_goTimer > 0f) _goTimer -= Time.deltaTime;

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

        /// <summary>Restart the race (pause-menu Restart).</summary>
        public void ResetRace()
        {
            foreach (var e in _entries)
            {
                e.finished = false;
                e.totalTime = 0f;
                e.place = 0;
            }
            _nextPlace = 1;
            _showResults = false;
            _dismissed = false;
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
                if (AllFinished()) _showResults = true;
            }
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

        private void OnGUI()
        {
            GUI.skin = GarageSkin.Skin;
            if (_showResults && !_dismissed) { DrawResults(); return; }
            if (_counting) { DrawBig(Mathf.CeilToInt(Mathf.Max(0f, _countdown)).ToString()); return; }
            if (_goTimer > 0f) DrawBig("GO!");
            DrawBanner();
        }

        private static void DrawBig(string s)
        {
            var style = new GUIStyle(GarageSkin.Header) { fontSize = 80, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0f, Screen.height * 0.22f, Screen.width, 140f), s, style);
        }

        private void DrawBanner()
        {
            var area = new Rect((Screen.width - 260f) * 0.5f, 40f, 260f, 26f + _entries.Count * 18f);
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

        private void DrawResults()
        {
            float w = 360f, h = 170f + _entries.Count * 26f;
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            var title = new GUIStyle(GarageSkin.Header) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("RACE RESULTS", title);
            GUILayout.Space(6);

            foreach (var e in Standings())
            {
                var lap = timer.GetTracker(e.rig.car);
                string best = lap.HasBest ? Fmt(lap.BestLap) : "--:--";
                GUILayout.Label($"P{e.place}  {e.rig.slot.name}   total {Fmt(e.totalTime)}   best {best}");
            }

            GUILayout.Space(8);
            if (GUILayout.Button("Keep driving", GUILayout.Height(30))) _dismissed = true;
            if (GUILayout.Button("Rematch", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                GameFlow.LoadTrack(); // SessionConfig persists; scene rebuilds fresh
            }
            if (GUILayout.Button("Main Menu", GUILayout.Height(30)))
            {
                Time.timeScale = 1f;
                if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                    GameFlow.LoadMenu();
            }
            GUILayout.EndArea();
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

        private static string Fmt(float t) =>
            t <= 0f ? "--:--" : $"{(int)(t / 60f):00}:{t % 60f:00.0}";
    }
}
