using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AIHWSim.Core
{
    /// <summary>
    /// Escape-key pause menu: Resume, Restart run, Save telemetry, Tune (live
    /// parameter sliders if the vehicle is ITunable), and Quit. Pausing sets
    /// Time.timeScale = 0, which freezes the fixed-step sim while leaving the
    /// IMGUI menu responsive.
    /// </summary>
    public sealed class PauseMenu : MonoBehaviour
    {
        public SimulationRunner runner;
        /// <summary>All session runners (split-screen); falls back to <see cref="runner"/> when unset.</summary>
        public List<SimulationRunner> runners;
        /// <summary>Per-player rigs (set by TrackBootstrap) — enables session snapshots.</summary>
        public List<PlayerRig> rigs;
        public MonoBehaviour tunableBehaviour; // optional; may implement ITunable

        private IEnumerable<SimulationRunner> AllRunners
        {
            get
            {
                if (runners != null && runners.Count > 0) return runners;
                return runner != null
                    ? new List<SimulationRunner> { runner }
                    : new List<SimulationRunner>();
            }
        }

        private enum PendingExit { None, Garage, TrackBuilder, MainMenu, Quit }

        private ITunable _tunable;
        private RaceDirector _race;
        private bool _paused;
        private bool _showTune;
        private bool _showSettings;
        private Vector2 _bodyScroll;
        private string _status = "";
        private PendingExit _pending;

        private void Start()
        {
            _tunable = tunableBehaviour as ITunable;
            _race = FindFirstObjectByType<RaceDirector>();
        }

        private void Update()
        {
            // While the settings panel is waiting for a key, Escape belongs to it:
            // cancelling a rebind must not also close the menu you were rebinding
            // from.
            if (SettingsPanel.Capturing) return;
            if (InputReader.PausePressed())
                SetPaused(!_paused);
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            Time.timeScale = paused ? 0f : 1f;
            if (!paused)
            {
                _showTune = false;
                _showSettings = false;
                SettingsPanel.Reset();
                // If telemetry logging was just enabled in Settings, start it now
                // that the menu is closing. EnableLogging is idempotent and only
                // acts on loggable runners (never bots/split-screen).
                if (Persistence.SettingsStore.Current.logTelemetry)
                    foreach (var r in AllRunners) r.EnableLogging();
            }
        }

        private void OnGUI()
        {
            if (!_paused) return;

            // Match the rest of the in-game UI. ArcadeHud and LanSessionMenu both
            // set this and either can be on screen at the same moment, so leaving
            // this menu on the default skin made it the odd one out.
            GUI.skin = GarageSkin.Skin;

            if (_pending != PendingExit.None) { DrawSavePrompt(); return; }

            // The ten-button stack alone is taller than the old 290 px, so this
            // panel was clipping inside BeginArea long before the settings panel
            // was added to it. The scroll view below is the actual fix — with it,
            // the height only has to be reasonable rather than exactly right.
            float w = _showSettings ? 460f : (_showTune ? 380f : 300f);
            float h = Mathf.Min(Screen.height - 60f,
                _showSettings ? 620f : (_showTune && _tunable != null ? 560f : 470f));
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            GUILayout.Label("PAUSED", title);
            GUILayout.Space(6);
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll);

            if (GUILayout.Button("Resume (Esc)", GUILayout.Height(30))) SetPaused(false);
            bool inRace = _race != null && _race.isActiveAndEnabled;
            if (inRace)
            {
                if (GUILayout.Button("Restart race", GUILayout.Height(30))) RestartRace();
            }
            else if (GUILayout.Button("Restart run", GUILayout.Height(30))) Restart();
            if (GUILayout.Button("Garage", GUILayout.Height(30))) RequestExit(PendingExit.Garage);
            if (GUILayout.Button("Track Builder", GUILayout.Height(30))) RequestExit(PendingExit.TrackBuilder);
            if (GUILayout.Button("Main Menu", GUILayout.Height(30)))
            {
                if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                    RequestExit(PendingExit.MainMenu);
                else
                    _status = "Menu scene missing — run Tools ▸ AIHWSim ▸ Create Menu Scene.";
            }
            if (rigs != null && rigs.Count > 0 &&
                GUILayout.Button("Save snapshot", GUILayout.Height(30))) SaveSnapshot();
            if (GUILayout.Button("Save telemetry", GUILayout.Height(30))) SaveTelemetry();
            if (_tunable != null &&
                GUILayout.Button(_showTune ? "Hide tuning" : "Tune…", GUILayout.Height(30)))
                _showTune = !_showTune;
            if (GUILayout.Button(_showSettings ? "Hide settings" : "Settings…", GUILayout.Height(30)))
                _showSettings = !_showSettings;
            if (GUILayout.Button("Quit", GUILayout.Height(30))) RequestExit(PendingExit.Quit);

            if (_showTune && _tunable != null) DrawTuning();
            if (_showSettings) DrawSettings();

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Space(4);
                GUILayout.Label(_status);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        /// <summary>Ask about unsaved telemetry before leaving the drive session.</summary>
        private void DrawSavePrompt()
        {
            float w = 340f, h = 190f;
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            var title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            GUILayout.Label("Save telemetry?", title);
            GUILayout.Space(4);
            GUILayout.Label("This drive session's telemetry hasn't been saved. " +
                            "It will be discarded if you leave without saving.");
            GUILayout.Space(6);

            string dest = _pending == PendingExit.Garage ? "Garage"
                : _pending == PendingExit.TrackBuilder ? "Track Builder"
                : _pending == PendingExit.MainMenu ? "Main Menu" : "Quit";
            if (GUILayout.Button($"Save log & go to {dest}", GUILayout.Height(30)))
            {
                string last = null;
                foreach (var r in AllRunners)
                    if (r.HasUnsavedTelemetry) last = r.SaveTelemetry() ?? last;
                _status = string.IsNullOrEmpty(last) ? "" : $"Saved: {last}";
                DoExit(TakePending());
            }
            if (GUILayout.Button($"Discard & go to {dest}", GUILayout.Height(30)))
                DoExit(TakePending());
            if (GUILayout.Button("Cancel", GUILayout.Height(28)))
                _pending = PendingExit.None;

            GUILayout.EndArea();
        }

        private PendingExit TakePending()
        {
            var p = _pending;
            _pending = PendingExit.None;
            return p;
        }

        /// <summary>Route an exit through the save prompt when there's unsaved telemetry.</summary>
        private void RequestExit(PendingExit action)
        {
            bool dirty = false;
            foreach (var r in AllRunners)
                if (r.HasUnsavedTelemetry) { dirty = true; break; }
            if (dirty) _pending = action;
            else DoExit(action);
        }

        private void DoExit(PendingExit action)
        {
            if (action == PendingExit.Garage) OpenGarage();
            else if (action == PendingExit.TrackBuilder) OpenTrackBuilder();
            else if (action == PendingExit.MainMenu) OpenMainMenu();
            else if (action == PendingExit.Quit) Quit();
        }

        private void OpenMainMenu()
        {
            Time.timeScale = 1f; // never leave the next scene frozen
            GameFlow.LoadMenu();
        }

        private void OpenTrackBuilder()
        {
            Time.timeScale = 1f;          // never leave the next scene frozen
            GameFlow.LoadTrackBuilder();  // GameFlow.ActiveTrack persists, so the builder reopens the driven map
        }

        /// <summary>One line, because the panel is shared with the LAN menu — see
        /// <see cref="SettingsPanel"/> for why it cannot live in here.</summary>
        private void DrawSettings()
        {
            GUILayout.Space(8);
            SettingsPanel.Draw(rigs, 330f);
            GUILayout.Label(Persistence.SettingsStore.Current.logTelemetry
                ? "Logging starts when you resume."
                : "Logging is off (starts next session if enabled).", GarageSkin.StatLabel);
        }

        private void DrawTuning()
        {
            GUILayout.Space(8);
            GUILayout.Label("Tuning (applies live):");
            foreach (var p in _tunable.GetTunables())
            {
                float val = p.Get();
                GUILayout.Label($"{p.Name}: {val:0.###}");
                float nv = GUILayout.HorizontalSlider(val, p.Min, p.Max);
                if (!Mathf.Approximately(nv, val)) p.Set(nv);
            }
        }

        private void Restart()
        {
            foreach (var r in AllRunners) r.RestartRun();
            var lap = FindFirstObjectByType<LapTimer>();
            if (lap != null) lap.ResetTimer();
            var race = FindFirstObjectByType<RaceDirector>();
            if (race != null) race.ResetRace();
            _status = "Run restarted.";
            SetPaused(false);
        }

        /// <summary>Restart an active race: reset cars/timers and re-run the countdown.</summary>
        private void RestartRace()
        {
            foreach (var r in AllRunners) r.RestartRun();
            var lap = FindFirstObjectByType<LapTimer>();
            if (lap != null) lap.ResetTimer();
            if (_race != null) _race.RestartRace();
            _status = "Race restarted.";
            SetPaused(false);
        }

        private void OpenGarage()
        {
            Time.timeScale = 1f;   // never leave the next scene frozen
            GameFlow.LoadGarage(); // keeps GameFlow.ActiveDesign so the garage re-opens it
        }

        /// <summary>Freeze the whole session (car poses/velocities + lap state) to disk.</summary>
        private void SaveSnapshot()
        {
            if (rigs == null || rigs.Count == 0) { _status = "Nothing to snapshot."; return; }

            var snap = new Persistence.SessionSnapshot
            {
                savedUtc = System.DateTime.UtcNow.ToString("o"),
                mode = (int)SessionConfig.Mode,
                targetLaps = SessionConfig.TargetLaps,
                simTime = runner != null ? runner.SimTime : 0f,
                trackName = GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "",
                trackJson = GameFlow.ActiveTrack != null ? JsonUtility.ToJson(GameFlow.ActiveTrack) : "",
            };

            foreach (var rig in rigs)
            {
                if (rig?.car == null) continue;
                var body = rig.car.GetComponent<Rigidbody>();
                var design = rig.slot.design ?? VehicleDesign.Default();
                snap.players.Add(new Persistence.PlayerSnapshot
                {
                    name = rig.slot.name,
                    profileId = rig.slot.profileId,
                    vehicleJson = JsonUtility.ToJson(design),
                    deviceKind = (int)rig.slot.deviceKind,
                    gamepadIndex = rig.slot.gamepadIndex,
                    position = body.position,
                    rotation = body.rotation,
                    linearVelocity = body.linearVelocity,
                    angularVelocity = body.angularVelocity,
                    lap = rig.lapTimer != null ? rig.lapTimer.GetTracker(rig.car).Clone() : new LapTracker(),
                });
            }

            string path = Persistence.SaveSystem.SaveSnapshot(snap);
            _status = string.IsNullOrEmpty(path) ? "Snapshot failed." : $"Snapshot saved: {System.IO.Path.GetFileName(path)}";
        }

        private void SaveTelemetry()
        {
            string last = null;
            foreach (var r in AllRunners)
                last = r.SaveTelemetry() ?? last;
            _status = string.IsNullOrEmpty(last) ? "No telemetry to save." : $"Saved: {last}";
        }

        private void Quit()
        {
            Time.timeScale = 1f;
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnDisable()
        {
            // Never leave the game frozen if this object is torn down while paused.
            if (_paused) Time.timeScale = 1f;
        }
    }
}
