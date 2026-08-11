using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.UI;
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
        /// <summary>Solo free-roam prop placer (set by TrackBootstrap; null
        /// elsewhere) — gates the PLACE PROPS page.</summary>
        public Props.PropPlacer propPlacer;

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

        // Layout-snapshotted twins of the flags that change WHICH controls
        // exist. Pad activation lands on a Layout pass (see MenuNav), so the
        // live flags may flip mid-pass; drawing from the snapshot keeps the
        // pass's Layout and Repaint identical, and the new state owns the next
        // frame — the same timing a mouse click has.
        private bool _showTuneDraw, _showSettingsDraw, _pausedDraw;
        private PendingExit _pendingDraw;

        /// <summary>The tutorial running in this scene, or null. Found once in
        /// Start the way <see cref="_race"/> is.</summary>
        private Tutorial.TutorialDirector _tutorial;

        /// <summary>Layout-snapshotted "there is a tutorial to skip". Snapshotted
        /// for the same reason <see cref="_hasBuildDraw"/> is: it decides whether
        /// a ROW EXISTS, and a row appearing between a Layout pass and its
        /// Repaint is the one IMGUI error this UI never risks.</summary>
        private bool _isTutorialDraw;

        private bool _showBuild, _showBuildDraw;
        // PLACE PROPS page (solo free roam only). _hasPlacerDraw is the
        // row-exists snapshot, same reason as _hasBuildDraw.
        private bool _showProps, _showPropsDraw, _hasPlacerDraw;
        // Whether the "Build controller…" row exists at all. Snapshotted like the
        // rest: it is derived from live scene state, and a row appearing between a
        // Layout pass and its Repaint is the one IMGUI error this UI never risks.
        private bool _hasBuildDraw;

        /// <summary>Is any car in this session driven by a controller DLL?</summary>
        private bool HasControllerRunner
        {
            get
            {
                foreach (var r in AllRunners)
                    if (r != null && r.loadControllerDll) return true;
                return false;
            }
        }

        private void Start()
        {
            _tunable = tunableBehaviour as ITunable;
            _race = FindFirstObjectByType<RaceDirector>();
            _tutorial = FindFirstObjectByType<Tutorial.TutorialDirector>();
        }

        private void Update()
        {
            // While the settings panel is waiting for a key, Escape belongs to it:
            // cancelling a rebind must not also close the menu you were rebinding
            // from.
            if (SettingsPanel.Capturing) return;
            if (InputReader.PausePressed())
                SetPaused(!_paused);

            // Pad B steps out: sub-panel → prompt → menu, same as Esc's spirit.
            if (_paused && MenuNav.ConsumeBack())
            {
                if (_pending != PendingExit.None) _pending = PendingExit.None;
                else if (_showTune || _showSettings || _showBuild || _showProps)
                {
                    _showTune = false;
                    _showSettings = false;
                    _showBuild = false;
                    _showProps = false;
                    SettingsPanel.Reset();
                }
                else SetPaused(false);
            }
        }

        public void SetPaused(bool paused)
        {
            _paused = paused;
            Time.timeScale = paused ? 0f : 1f;
            // Music keeps playing through the freeze (AudioSources ignore
            // timeScale) but drops to half so the pause reads as a pause.
            Audio.MusicDirector.SetPaused(paused);
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
            if (Event.current.type == EventType.Layout)
            {
                _pausedDraw = _paused;
                _showTuneDraw = _showTune;
                _showSettingsDraw = _showSettings;
                _showBuildDraw = _showBuild;
                _hasBuildDraw = HasControllerRunner;
                _showPropsDraw = _showProps;
                _hasPlacerDraw = propPlacer != null;
                _isTutorialDraw = _tutorial != null;
                _pendingDraw = _pending;
            }
            if (!_pausedDraw) return;

            // Match the rest of the in-game UI. ArcadeHud and LanSessionMenu both
            // set this and either can be on screen at the same moment, so leaving
            // this menu on the default skin made it the odd one out.
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();
            MenuNav.BeginFrame(_pendingDraw != PendingExit.None ? "pause:prompt" : "pause");

            if (_pendingDraw != PendingExit.None)
            {
                DrawSavePrompt();
                MenuNav.EndFrame();
                UIScale.End();
                return;
            }

            // The ten-button stack alone is taller than the old 290 px, so this
            // panel was clipping inside BeginArea long before the settings panel
            // was added to it. The scroll view below is the actual fix — with it,
            // the height only has to be reasonable rather than exactly right.
            float w = _showSettingsDraw || _showBuildDraw ? 460f : (_showTuneDraw ? 380f : 300f);
            float h = Mathf.Min(UIScale.H - 60f,
                _showSettingsDraw || _showBuildDraw ? 620f
                    : (_showTuneDraw && _tunable != null ? 560f : 470f));
            var area = new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
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

            if (MenuNav.Button("Resume (Esc)", GUILayout.Height(30))) SetPaused(false);
            // Second, right under Resume: a player who opened this menu to get
            // out of a lesson should not have to read past six other buttons to
            // find the way out.
            if (_isTutorialDraw &&
                MenuNav.Button("Skip this tutorial", GUILayout.Height(30))) SkipTutorial();
            bool inRace = _race != null && _race.isActiveAndEnabled;
            if (inRace)
            {
                if (MenuNav.Button("Restart race", GUILayout.Height(30))) RestartRace();
            }
            else if (MenuNav.Button("Restart run", GUILayout.Height(30))) Restart();
            if (MenuNav.Button("Garage", GUILayout.Height(30))) RequestExit(PendingExit.Garage);
            if (MenuNav.Button("Track Builder", GUILayout.Height(30))) RequestExit(PendingExit.TrackBuilder);
            if (MenuNav.Button("Main Menu", GUILayout.Height(30)))
            {
                if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                    RequestExit(PendingExit.MainMenu);
                else
                    _status = "Menu scene missing — run Tools ▸ AIHWSim ▸ Create Menu Scene.";
            }
            if (rigs != null && rigs.Count > 0 &&
                MenuNav.Button("Save snapshot", GUILayout.Height(30))) SaveSnapshot();
            if (MenuNav.Button("Save telemetry", GUILayout.Height(30))) SaveTelemetry();
            if (_tunable != null &&
                MenuNav.Button(_showTuneDraw ? "Hide tuning" : "Tune…", GUILayout.Height(30)))
                _showTune = !_showTune;
            if (MenuNav.Button(_showSettingsDraw ? "Hide settings" : "Settings…", GUILayout.Height(30)))
                _showSettings = !_showSettings;
            // Rebuild the C controller and hot-swap it without leaving the drive.
            // Offered only where it means something: a session with no DLL loaded
            // has nothing to reload, and the row would be a button that does
            // nothing rather than an honest absence.
            if (_hasBuildDraw &&
                MenuNav.Button(_showBuildDraw ? "Hide controller build" : "Build controller…",
                               GUILayout.Height(30)))
                _showBuild = !_showBuild;
            // Free-roam prop placement. Offered only where a placer was
            // composed (solo Free Roam) — same honesty as the build row.
            if (_hasPlacerDraw &&
                MenuNav.Button(_showPropsDraw ? "Hide props" : "Place props…", GUILayout.Height(30)))
                _showProps = !_showProps;
            if (MenuNav.Button("Quit", GUILayout.Height(30))) RequestExit(PendingExit.Quit);

            if (_showTuneDraw && _tunable != null) DrawTuning();
            if (_showSettingsDraw) DrawSettings();
            if (_showBuildDraw) UI.ControllerBuildPanel.Draw(logHeight: 180f);
            if (_showPropsDraw && _hasPlacerDraw) DrawProps();

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Space(4);
                GUILayout.Label(_status);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
            MenuNav.EndFrame();
            UIScale.End();
        }

        /// <summary>Ask about unsaved telemetry before leaving the drive session.
        /// Drawn inside the caller's UIScale block; dispatches on the Layout
        /// snapshot so a pad activation can't desync the pass.</summary>
        private void DrawSavePrompt()
        {
            float w = 340f, h = 190f;
            var area = new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
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

            string dest = _pendingDraw == PendingExit.Garage ? "Garage"
                : _pendingDraw == PendingExit.TrackBuilder ? "Track Builder"
                : _pendingDraw == PendingExit.MainMenu ? "Main Menu" : "Quit";
            if (MenuNav.Button($"Save log & go to {dest}", GUILayout.Height(30)))
            {
                string last = null;
                foreach (var r in AllRunners)
                    if (r.HasUnsavedTelemetry) last = r.SaveTelemetry() ?? last;
                _status = string.IsNullOrEmpty(last) ? "" : $"Saved: {last}";
                DoExit(TakePending());
            }
            if (MenuNav.Button($"Discard & go to {dest}", GUILayout.Height(30)))
                DoExit(TakePending());
            if (MenuNav.Button("Cancel", GUILayout.Height(28)))
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

        /// <summary>
        /// Bail out of the lesson. No scrap and no ✓ — a tutorial somebody quit
        /// is not one they completed, and the hub's marks have to stay honest
        /// (they are also what the finish-them-all crate counts). It DOES advance
        /// a "play all" run rather than ending it: skipping one lesson is not
        /// asking to leave the whole sequence.
        ///
        /// Deliberately not routed through <see cref="RequestExit"/>: that path
        /// exists to protect unsaved TELEMETRY, and a tutorial does not log any.
        /// </summary>
        private void SkipTutorial()
        {
            Tutorials.SkipCurrent();
            Time.timeScale = 1f;
            if (Tutorials.Active && Tutorials.LaunchCurrent()
                && Application.CanStreamedLevelBeLoaded(GameFlow.TrackSceneName))
            {
                GameFlow.LoadTrack();
                return;
            }
            // Nothing (or nothing loadable) queued behind it: back to the list,
            // which is also where an overlay lesson has to be picked up from.
            Tutorials.PendingOpenHub = true;
            if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName)) GameFlow.LoadMenu();
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

        /// <summary>
        /// The PLACE PROPS page: pick something, the menu closes, and a ghost
        /// rides ahead of the car until Interact stamps it down. The row set is
        /// fixed (catalog table + two statics), so no Layout snapshot beyond
        /// the page toggle is needed.
        /// </summary>
        private void DrawProps()
        {
            GUILayout.Space(8);
            GUILayout.Label("Place a prop (it saves with this map):");
            for (int i = 0; i < Props.SpeakerCatalog.Entries.Length; i++)
            {
                if (MenuNav.Button($"Speaker — {Props.SpeakerCatalog.Entries[i].label}",
                                   GUILayout.Height(26)))
                    ArmPlacer("speaker", i);
            }
            if (MenuNav.Button("World microphone", GUILayout.Height(26)))
                ArmPlacer("mic", 0);
            if (MenuNav.Button("RF beacon", GUILayout.Height(26)))
                ArmPlacer("rf_beacon", 0);
            GUILayout.Label($"Drive to position; press {KeyBindings.Current.Key(DriveAction.Interact)} "
                + "to place. Hold it near a placed prop to remove it.", GarageSkin.StatLabel);
        }

        private void ArmPlacer(string kind, int preset)
        {
            if (propPlacer == null) return;
            propPlacer.Arm(kind, preset);
            _showProps = false;
            SetPaused(false);
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
                trackName = GameFlow.HasSceneTrack
                    ? GameFlow.ActiveSceneTrack
                    : (GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : ""),
                trackJson = GameFlow.ActiveTrack != null ? JsonUtility.ToJson(GameFlow.ActiveTrack) : "",
                // A scene track ships inside the build, so the snapshot names it
                // rather than embedding it. Without this a resumed scene track
                // would find an empty trackJson and drop the player onto the oval.
                trackScene = GameFlow.ActiveSceneTrack ?? "",
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
