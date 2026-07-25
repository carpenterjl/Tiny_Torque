using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Net;
using AIHWSim.Persistence;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// IMGUI main menu (dark GarageSkin): Single Player (vehicle/track pickers,
    /// Garage, Track Builder), Multiplayer (split-screen setup — enabled in a
    /// later step), Resume Drive (session snapshots — later step), Options
    /// (applied + saved live), Quit.
    /// </summary>
    public sealed class MenuUI : MonoBehaviour
    {
        private enum Page { Root, SinglePlayer, Multiplayer, Options, Resume, LanHost, LanJoin }

        private Page _page = Page.Root;
        private string _status = "";

        // Picker state (indices into the option lists; 0 = stock/oval).
        private int _vehicleIdx;
        private int _trackIdx;
        private List<string> _vehicles = new List<string>();
        private List<string> _tracks = new List<string>();

        // Multiplayer setup state. Device choice: 0 = Keyboard, 1+g = Gamepad g.
        private int _mpVeh1, _mpVeh2;
        private int _mpTrackIdx;
        private int _mpDev1 = 0, _mpDev2 = 1;
        private int _mpLaps = 3;
        private int _spLaps;

        // Single-player race setup.
        private int _spBots;        // AI opponents (0..7)
        private int _spDiff = 1;    // 0 Easy / 1 Medium / 2 Hard
        private int _spControl;     // 0 Manual / 1 Autonomous (C firmware) / 2 Autonomous (bot AI)
        private bool _spRubber;
        private int _spCountdown = 3; // race-start countdown seconds (0..60)
        private static readonly List<string> DiffNames = new List<string> { "Easy", "Medium", "Hard" };
        private static readonly List<string> ControlNames =
            new List<string> { "Manual", "Autonomous (firmware)", "Autonomous (bot AI)" };

        // LAN pages state.
        private string _joinIp = "127.0.0.1";
        private bool _connecting;
        private float _connectDeadline;
        private readonly List<LanDiscovery.DiscoveredGame> _discovered =
            new List<LanDiscovery.DiscoveredGame>();
        private Vector2 _lanScroll;

        private void Start()
        {
            RefreshLists();

            if (!string.IsNullOrEmpty(NetSession.LastDisconnectReason))
            {
                _status = NetSession.LastDisconnectReason;
                NetSession.LastDisconnectReason = "";
            }

            // Restore last-used picks.
            var s = SettingsStore.Current;
            _vehicleIdx = Mathf.Max(0, _vehicles.IndexOf(s.lastVehicle));
            _trackIdx = Mathf.Max(0, _tracks.IndexOf(s.lastTrack));
            _spLaps = Mathf.Clamp(s.lastLaps, 0, 50);
            _spBots = Mathf.Clamp(s.spBots, 0, 7);
            _spDiff = Mathf.Clamp(s.spDifficulty, 0, 2);
            _spControl = Mathf.Clamp(s.spControl, 0, 2);
            _spRubber = s.spRubberBand;
            _spCountdown = Mathf.Clamp(s.spCountdown, 0, 60);
        }

        private void RefreshLists()
        {
            _vehicles = new List<string> { "" };  // "" = stock default
            _vehicles.AddRange(VehiclePresets.DisplayNames());  // ★-prefixed built-ins
            _vehicles.AddRange(VehicleLibrary.List());
            _tracks = new List<string> { "" };    // "" = classic oval
            _tracks.AddRange(TrackPresets.DisplayNames());
            _tracks.AddRange(TrackLibrary.List());
            _vehicleIdx = Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1);
            _trackIdx = Mathf.Clamp(_trackIdx, 0, _tracks.Count - 1);
        }

        /// <summary>Resolve a picker vehicle name: "" = stock, ★-preset, or a save.</summary>
        private static VehicleDesign ResolveVehicle(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;              // stock Default()
            var preset = VehiclePresets.Resolve(name);
            return preset ?? VehicleLibrary.Load(name);
        }

        /// <summary>Resolve a picker track name: "" = classic oval, ★-preset, or a save.</summary>
        private static TrackDesign ResolveTrack(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;             // classic oval
            var preset = TrackPresets.Resolve(name);
            return preset ?? TrackLibrary.Load(name);
        }

        private void OnGUI()
        {
            GUI.skin = GarageSkin.Skin;

            float w = 380f, h = _page == Page.Options
                ? Mathf.Min(680f, Screen.height - 20f)
                : _page == Page.SinglePlayer ? Mathf.Min(560f, Screen.height - 20f) : 430f;
            var area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);

            var title = new GUIStyle(GarageSkin.Header) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            GUILayout.Label("AI HARDWARE CONTROL SIM", title);
            GUILayout.Space(10);

            switch (_page)
            {
                case Page.Root: DrawRoot(); break;
                case Page.SinglePlayer: DrawSinglePlayer(); break;
                case Page.Multiplayer: DrawMultiplayer(); break;
                case Page.Options: DrawOptions(); break;
                case Page.Resume: DrawResume(); break;
                case Page.LanHost: DrawLanHost(); break;
                case Page.LanJoin: DrawLanJoin(); break;
            }

            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, GarageSkin.StatLabel);
            GUILayout.EndArea();
        }

        // ---- pages -----------------------------------------------------------

        private void DrawRoot()
        {
            if (MenuButton("Single Player")) { _page = Page.SinglePlayer; RefreshLists(); }
            if (MenuButton("Multiplayer")) { _page = Page.Multiplayer; RefreshLists(); }

            GUI.enabled = SaveSystem.ListSnapshots().Count > 0;
            if (MenuButton("Resume Drive")) { _page = Page.Resume; RefreshSnapshots(); }
            GUI.enabled = true;

            if (MenuButton("Options")) _page = Page.Options;
            if (MenuButton("Quit")) Quit();
        }

        private void DrawSinglePlayer()
        {
            GUILayout.Label("SINGLE PLAYER", GarageSkin.Header);
            GUILayout.Space(6);

            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            _trackIdx = CyclePicker("Track", _tracks, _trackIdx, t => t == "" ? "Classic Oval" : t);

            // AI opponents.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Opponents", GUILayout.Width(70));
            if (GUILayout.Button("−", GUILayout.Width(30))) _spBots = Mathf.Max(0, _spBots - 1);
            GUILayout.Label(_spBots == 0 ? "None" : $"{_spBots} bots", GarageSkin.Header);
            if (GUILayout.Button("+", GUILayout.Width(30))) _spBots = Mathf.Min(7, _spBots + 1);
            GUILayout.EndHorizontal();

            if (_spBots > 0)
            {
                _spDiff = CyclePicker("Difficulty", DiffNames, _spDiff, x => x);
                _spRubber = GUILayout.Toggle(_spRubber, " Rubber-band (keep the pack close)");
            }

            // How the player's own car is driven.
            _spControl = CyclePicker("Driving", ControlNames, _spControl, x => x);

            // Race distance.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Race laps", GUILayout.Width(70));
            if (GUILayout.Button("−", GUILayout.Width(30))) _spLaps = Mathf.Max(0, _spLaps - 1);
            GUILayout.Label(_spLaps == 0 ? "Free drive" : $"{_spLaps} laps", GarageSkin.Header);
            if (GUILayout.Button("+", GUILayout.Width(30))) _spLaps = Mathf.Min(50, _spLaps + 1);
            GUILayout.EndHorizontal();

            if (_spBots > 0 || _spLaps > 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Countdown", GUILayout.Width(70));
                if (GUILayout.Button("−", GUILayout.Width(30))) _spCountdown = Mathf.Max(0, _spCountdown - 1);
                GUILayout.Label(_spCountdown == 0 ? "None" : $"{_spCountdown} s", GarageSkin.Header);
                if (GUILayout.Button("+", GUILayout.Width(30))) _spCountdown = Mathf.Min(60, _spCountdown + 1);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);

            string go = (_spBots > 0 || _spLaps > 0) ? "Race ▶" : "Drive ▶";
            if (MenuButton(go)) StartSinglePlayer();
            if (MenuButton("Garage")) LoadIfBuilt(GameFlow.GarageSceneName, GameFlow.LoadGarage);
            if (MenuButton("Track Builder")) LoadIfBuilt(GameFlow.TrackBuilderSceneName, GameFlow.LoadTrackBuilder);
            GUILayout.Space(8);
            if (MenuButton("← Back")) _page = Page.Root;
        }

        private void StartSinglePlayer()
        {
            string vehicle = _vehicles[_vehicleIdx];
            string track = _tracks[_trackIdx];
            var s = SettingsStore.Current;

            SessionConfig.SetSinglePlayer();                 // clears roster + rubber-band
            SessionConfig.TargetLaps = _spLaps;
            SessionConfig.RubberBand = _spBots > 0 && _spRubber;
            SessionConfig.CountdownSeconds = (_spBots > 0 || _spLaps > 0) ? _spCountdown : 0;
            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            GameFlow.ActiveTrack = ResolveTrack(track);

            // Slot 0 = the human; slots 1..N = AI opponents.
            string pname = string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name;
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = pname,
                profileId = pname,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                assists = SessionConfig.P1Assists(s),
                isBot = false,
                control = (DriveControl)Mathf.Clamp(_spControl, 0, 2),
            });
            for (int k = 1; k <= _spBots; k++)
                SessionConfig.Players.Add(MakeBotSlot(k, _spDiff));

            s.lastVehicle = vehicle;
            s.lastTrack = track;
            s.lastLaps = _spLaps;
            s.spBots = _spBots;
            s.spDifficulty = _spDiff;
            s.spControl = _spControl;
            s.spRubberBand = _spRubber;
            s.spCountdown = _spCountdown;
            SettingsStore.Save();

            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        /// <summary>Build one AI opponent: a preset car with a distinct paint colour.</summary>
        private static PlayerSlot MakeBotSlot(int k, int difficulty)
        {
            var preset = VehiclePresets.All[(k - 1) % VehiclePresets.All.Length];
            var design = preset.build();
            design.liveryPng = "";                                   // show the flat colour
            design.bodyColor = Color.HSVToRGB((k * 0.137f) % 1f, 0.65f, 0.95f);
            return new PlayerSlot
            {
                name = $"Bot {k} · {preset.name}",
                profileId = $"Bot {k}",
                design = design,
                isBot = true,
                control = DriveControl.BotAI,
                botDifficulty = difficulty,
                assists = new AIHWSim.Vehicles.AssistSettings(), // bots race on raw physics
            };
        }

        private void DrawMultiplayer()
        {
            GUILayout.Label("MULTIPLAYER — SPLIT-SCREEN", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            DrawPlayerRow(1, ref s.player1Name, ref _mpVeh1, ref _mpDev1);
            DrawPlayerRow(2, ref s.player2Name, ref _mpVeh2, ref _mpDev2);
            GUILayout.Space(4);

            _mpTrackIdx = CyclePicker("Track", _tracks, _mpTrackIdx, t => t == "" ? "Classic Oval" : t);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Race laps", GUILayout.Width(70));
            if (GUILayout.Button("−", GUILayout.Width(30))) _mpLaps = Mathf.Max(0, _mpLaps - 1);
            GUILayout.Label(_mpLaps == 0 ? "Sandbox (no race)" : _mpLaps.ToString(), GarageSkin.Header);
            if (GUILayout.Button("+", GUILayout.Width(30))) _mpLaps = Mathf.Min(50, _mpLaps + 1);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            string problem = ValidateDevices();
#if !ENABLE_INPUT_SYSTEM
            problem = "Split-screen needs the Input System package (Active Input Handling = Both).";
#endif
            GUI.enabled = problem == null;
            if (MenuButton("Start Split-Screen ▶")) StartSplitScreen();
            GUI.enabled = true;
            if (problem != null) GUILayout.Label(problem, GarageSkin.StatLabel);

            GUILayout.Space(6);
            if (MenuButton("Host LAN Game")) { _page = Page.LanHost; RefreshLists(); }
            if (MenuButton("Join LAN Game"))
            {
                _page = Page.LanJoin;
                RefreshLists();
                LanDiscovery.StartListen();
            }
            GUILayout.Space(8);
            if (MenuButton("← Back")) { SettingsStore.Save(); _page = Page.Root; }
        }

        private void DrawPlayerRow(int n, ref string name, ref int vehIdx, ref int devChoice)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"P{n}", GUILayout.Width(24));
            name = GUILayout.TextField(name, GUILayout.Width(96));
            if (GUILayout.Button("◀", GUILayout.Width(24)))
                vehIdx = (vehIdx - 1 + _vehicles.Count) % _vehicles.Count;
            string veh = _vehicles[Mathf.Clamp(vehIdx, 0, _vehicles.Count - 1)];
            GUILayout.Label(veh == "" ? "Stock" : veh, GUILayout.Width(90));
            if (GUILayout.Button("▶", GUILayout.Width(24)))
                vehIdx = (vehIdx + 1) % _vehicles.Count;
            if (GUILayout.Button(DeviceLabel(devChoice), GUILayout.Width(86)))
                devChoice = (devChoice + 1) % (1 + PadCount());
            GUILayout.EndHorizontal();
        }

        private static int PadCount()
        {
#if ENABLE_INPUT_SYSTEM
            return Mathf.Max(1, UnityEngine.InputSystem.Gamepad.all.Count);
#else
            return 1;
#endif
        }

        private static string DeviceLabel(int choice) =>
            choice == 0 ? "Keyboard" : $"Gamepad {choice}";

        private string ValidateDevices()
        {
            if (_mpDev1 == _mpDev2) return "Players can't share a device.";
#if ENABLE_INPUT_SYSTEM
            int pads = UnityEngine.InputSystem.Gamepad.all.Count;
            int needed = Mathf.Max(_mpDev1, _mpDev2);
            if (needed > pads) return $"Gamepad {needed} not connected ({pads} detected).";
#endif
            return null;
        }

        private void StartSplitScreen()
        {
            var s = SettingsStore.Current;
            string track = _tracks[Mathf.Clamp(_mpTrackIdx, 0, _tracks.Count - 1)];

            SessionConfig.Mode = SessionMode.SplitScreen;
            SessionConfig.TargetLaps = _mpLaps;
            SessionConfig.CountdownSeconds = 0; // split-screen has no countdown control (yet)
            SessionConfig.Players.Clear();
            SessionConfig.Players.Add(MakeSlot(s.player1Name, _mpVeh1, _mpDev1, SessionConfig.P1Assists(s)));
            SessionConfig.Players.Add(MakeSlot(s.player2Name, _mpVeh2, _mpDev2, SessionConfig.P2Assists(s)));

            GameFlow.ActiveTrack = track == "" ? null : TrackLibrary.Load(track);
            GameFlow.ActiveDesign = SessionConfig.Players[0].design;

            s.p1DeviceKind = _mpDev1 == 0 ? (int)InputDeviceKind.Keyboard : (int)InputDeviceKind.Gamepad;
            s.p2DeviceKind = _mpDev2 == 0 ? (int)InputDeviceKind.Keyboard : (int)InputDeviceKind.Gamepad;
            s.p2GamepadIndex = Mathf.Max(0, _mpDev2 - 1);
            SettingsStore.Save();

            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        private PlayerSlot MakeSlot(string name, int vehIdx, int devChoice,
            AIHWSim.Vehicles.AssistSettings assists)
        {
            string veh = _vehicles[Mathf.Clamp(vehIdx, 0, _vehicles.Count - 1)];
            return new PlayerSlot
            {
                name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                profileId = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                design = ResolveVehicle(veh),
                deviceKind = devChoice == 0 ? InputDeviceKind.Keyboard : InputDeviceKind.Gamepad,
                gamepadIndex = Mathf.Max(0, devChoice - 1),
                assists = assists,
            };
        }

        private Vector2 _resumeScroll;
        private readonly List<(string name, SessionSnapshot snap)> _resumeCache =
            new List<(string, SessionSnapshot)>();

        /// <summary>Read snapshot files once on entering the page (not per OnGUI frame).</summary>
        private void RefreshSnapshots()
        {
            _resumeCache.Clear();
            foreach (var name in SaveSystem.ListSnapshots())
            {
                var s = SaveSystem.LoadSnapshot(name);
                if (s != null) _resumeCache.Add((name, s));
            }
        }

        private void DrawResume()
        {
            GUILayout.Label("RESUME DRIVE", GarageSkin.Header);
            GUILayout.Space(6);

            if (_resumeCache.Count == 0) GUILayout.Label("(no saved sessions)", GarageSkin.StatLabel);

            _resumeScroll = GUILayout.BeginScrollView(_resumeScroll, GUILayout.Height(220));
            string deleted = null;
            SessionSnapshot resume = null;
            foreach (var (name, s) in _resumeCache)
            {
                string mode = (SessionMode)s.mode == SessionMode.SplitScreen ? "Split-screen" : "Single player";
                string track = string.IsNullOrEmpty(s.trackName) ? "Classic Oval" : s.trackName;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button($"{mode} · {track} · {name.Replace("snapshot_", "")}"))
                    resume = s;
                if (GUILayout.Button("✕", GUILayout.Width(28)))
                    deleted = name;
                GUILayout.EndHorizontal();
            }
            if (deleted != null)
            {
                SaveSystem.DeleteSnapshot(deleted);
                RefreshSnapshots();
            }
            if (resume != null) ResumeSnapshot(resume);
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            if (MenuButton("← Back")) _page = Page.Root;
        }

        private void ResumeSnapshot(SessionSnapshot s)
        {
            SessionConfig.Mode = (SessionMode)s.mode;
            SessionConfig.TargetLaps = s.targetLaps;
            SessionConfig.Players.Clear();
            foreach (var ps in s.players)
            {
                // Assists aren't part of saved state — re-read the current options.
                var cur = SettingsStore.Current;
                SessionConfig.Players.Add(new PlayerSlot
                {
                    name = ps.name,
                    profileId = ps.profileId,
                    design = string.IsNullOrEmpty(ps.vehicleJson)
                        ? null : JsonUtility.FromJson<VehicleDesign>(ps.vehicleJson),
                    deviceKind = (InputDeviceKind)ps.deviceKind,
                    gamepadIndex = ps.gamepadIndex,
                    assists = SessionConfig.Players.Count == 0
                        ? SessionConfig.P1Assists(cur) : SessionConfig.P2Assists(cur),
                });
            }
            GameFlow.ActiveTrack = string.IsNullOrEmpty(s.trackJson)
                ? null : JsonUtility.FromJson<TrackDesign>(s.trackJson);
            GameFlow.ActiveDesign = SessionConfig.Players.Count > 0 ? SessionConfig.Players[0].design : null;
            GameFlow.PendingSnapshot = s;
            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        // ---- LAN pages -------------------------------------------------------

        private void DrawLanHost()
        {
            GUILayout.Label("HOST LAN GAME", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(60));
            s.player1Name = GUILayout.TextField(s.player1Name, GUILayout.Width(160));
            GUILayout.EndHorizontal();

            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            _trackIdx = CyclePicker("Track", _tracks, _trackIdx, t => t == "" ? "Classic Oval" : t);
            GUILayout.Space(4);
            GUILayout.Label("Players join into free roam; you start races and\nchange maps from the in-game Esc menu.",
                GarageSkin.StatLabel);
            GUILayout.Space(6);

            if (MenuButton("Start Hosting ▶")) StartLanHost();
            GUILayout.Space(8);
            if (MenuButton("← Back")) { SettingsStore.Save(); _page = Page.Multiplayer; }
        }

        private void StartLanHost()
        {
            var s = SettingsStore.Current;
            string vehicle = _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)];
            string track = _tracks[Mathf.Clamp(_trackIdx, 0, _tracks.Count - 1)];
            SettingsStore.Save();

            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            GameFlow.ActiveTrack = ResolveTrack(track);

            SessionConfig.Mode = SessionMode.LanHost;
            SessionConfig.TargetLaps = 0; // free roam; races start in-game
            SessionConfig.Players.Clear();
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = s.player1Name,
                profileId = s.player1Name,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                isLocal = true,
                assists = SessionConfig.P1Assists(s),
            });

            var session = NetSession.Create();
            if (!session.StartHost())
            {
                _status = "Failed to start hosting (port in use?).";
                session.Leave();
                return;
            }
            LanDiscovery.SetAnnounce(new LanDiscovery.Announce
            {
                gameName = s.player1Name,
                players = 1,
                trackName = track == "" ? "Classic Oval" : track,
            });
            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        private void DrawLanJoin()
        {
            GUILayout.Label("JOIN LAN GAME", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(60));
            s.player1Name = GUILayout.TextField(s.player1Name, GUILayout.Width(160));
            GUILayout.EndHorizontal();
            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            GUILayout.Space(4);

            if (_connecting)
            {
                GUILayout.Label("Connecting…", GarageSkin.Header);
                if (Time.unscaledTime > _connectDeadline)
                {
                    _connecting = false;
                    _status = "Connection timed out.";
                    NetSession.Instance?.Leave();
                }
                if (MenuButton("Cancel"))
                {
                    _connecting = false;
                    NetSession.Instance?.Leave();
                }
                return;
            }

            LanDiscovery.Drain(_discovered);
            GUILayout.Label($"GAMES ON YOUR NETWORK ({_discovered.Count})", GarageSkin.Header);
            _lanScroll = GUILayout.BeginScrollView(_lanScroll, GUILayout.Height(120));
            foreach (var g in _discovered)
            {
                string track = string.IsNullOrEmpty(g.info.trackName) ? "Classic Oval" : g.info.trackName;
                if (GUILayout.Button($"{g.info.gameName} · {track} · {g.info.players}/{g.info.maxPlayers}"))
                    Connect(g.address, g.info.port);
            }
            if (_discovered.Count == 0)
                GUILayout.Label("(listening for hosts…)", GarageSkin.StatLabel);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(24));
            _joinIp = GUILayout.TextField(_joinIp, GUILayout.Width(150));
            if (GUILayout.Button("Connect", GUILayout.Width(80)))
                Connect(_joinIp.Trim(), NetSession.DefaultPort);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            if (MenuButton("← Back"))
            {
                LanDiscovery.StopListen();
                SettingsStore.Save();
                _page = Page.Multiplayer;
            }
        }

        private void Connect(string ip, ushort port)
        {
            SettingsStore.Save();
            string vehicle = _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)];
            GameFlow.ActiveDesign = ResolveVehicle(vehicle);

            LanDiscovery.StopListen();
            var session = NetSession.Create();
            if (!session.StartClient(ip, port))
            {
                _status = "Failed to start connecting.";
                session.Leave();
                return;
            }
            // The scene loads when aihw.welcome arrives (NetSession drives it);
            // we just show progress + a timeout here.
            _connecting = true;
            _connectDeadline = Time.unscaledTime + 10f;
        }

        private void OnDestroy()
        {
            // Scene changed (drive/garage/track): stop the join-page listener.
            LanDiscovery.StopListen();
        }

        private Vector2 _optionsScroll;

        private void DrawOptions()
        {
            GUILayout.Label("OPTIONS", GarageSkin.Header);
            GUILayout.Space(6);
            var s = SettingsStore.Current;
            bool changed = false;

            // Scroll so the page never clips in small (editor) game views.
            _optionsScroll = GUILayout.BeginScrollView(_optionsScroll);

            GUILayout.Label($"Master volume: {s.masterVolume:P0}");
            float vol = GUILayout.HorizontalSlider(s.masterVolume, 0f, 1f);
            if (!Mathf.Approximately(vol, s.masterVolume)) { s.masterVolume = vol; changed = true; }

            GUILayout.Space(4);
            string[] quality = QualitySettings.names;
            int qShown = s.qualityLevel >= 0 ? s.qualityLevel : QualitySettings.GetQualityLevel();
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Quality: {quality[Mathf.Clamp(qShown, 0, quality.Length - 1)]}");
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("◀", GUILayout.Width(30))) { s.qualityLevel = (qShown - 1 + quality.Length) % quality.Length; changed = true; }
            if (GUILayout.Button("▶", GUILayout.Width(30))) { s.qualityLevel = (qShown + 1) % quality.Length; changed = true; }
            GUILayout.EndHorizontal();

            bool fs = GUILayout.Toggle(s.fullscreen, " Fullscreen");
            if (fs != s.fullscreen) { s.fullscreen = fs; changed = true; }
            bool vs = GUILayout.Toggle(s.vSync, " VSync");
            if (vs != s.vSync) { s.vSync = vs; changed = true; }
            bool ms = GUILayout.Toggle(s.mouseSteer, " Mouse steering (single player)");
            if (ms != s.mouseSteer) { s.mouseSteer = ms; changed = true; }

            // Keyboard-only feel: ramps the digital A/D step like a transmitter
            // stick. Gamepad sticks are never shaped. 0% = old instant response.
            changed |= AssistSlider("KB steer smoothing", ref s.kbSteerSmoothing);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("P1 name", GUILayout.Width(60));
            string n1 = GUILayout.TextField(s.player1Name, GUILayout.Width(140));
            if (n1 != s.player1Name) { s.player1Name = n1; changed = true; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("P2 name", GUILayout.Width(60));
            string n2 = GUILayout.TextField(s.player2Name, GUILayout.Width(140));
            if (n2 != s.player2Name) { s.player2Name = n2; changed = true; }
            GUILayout.EndHorizontal();

            // Arcade assists — 0% is the pure physics model. Per-player; in LAN
            // your own values travel with you to the host.
            GUILayout.Space(6);
            GUILayout.Label("ASSISTS — P1  (0% = realistic)", GarageSkin.Header);
            changed |= AssistSlider("Steering help", ref s.p1AssistSteer);
            changed |= AssistSlider("Stability", ref s.p1AssistStability);
            changed |= AssistSlider("Traction ctrl", ref s.p1AssistTraction);
            changed |= AssistSlider("ABS", ref s.p1AssistAbs);
            GUILayout.Space(4);
            GUILayout.Label("ASSISTS — P2 (split-screen)", GarageSkin.Header);
            changed |= AssistSlider("Steering help", ref s.p2AssistSteer);
            changed |= AssistSlider("Stability", ref s.p2AssistStability);
            changed |= AssistSlider("Traction ctrl", ref s.p2AssistTraction);
            changed |= AssistSlider("ABS", ref s.p2AssistAbs);

            // Simulation realism — deterministic noise + control-loop latency.
            GUILayout.Space(6);
            GUILayout.Label("SIM REALISM", GarageSkin.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Noise seed (0 = random)", GUILayout.Width(150));
            string seedStr = GUILayout.TextField(s.noiseSeed.ToString(), GUILayout.Width(90));
            if (int.TryParse(seedStr, out int seed) && seed != s.noiseSeed)
            { s.noiseSeed = seed; changed = true; }
            GUILayout.EndHorizontal();
            GUILayout.Label("Seed used is stamped into the telemetry sidecar.", GarageSkin.StatLabel);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Actuation delay: {s.actuationDelayTicks} ticks", GUILayout.Width(150));
            int del = Mathf.RoundToInt(GUILayout.HorizontalSlider(s.actuationDelayTicks, 0f, 5f,
                GUILayout.ExpandWidth(true)));
            if (del != s.actuationDelayTicks) { s.actuationDelayTicks = del; changed = true; }
            GUILayout.EndHorizontal();

            // Telemetry — opt-in CSV logging (off by default). Also toggleable
            // mid-drive from pause ▸ Settings, where it starts after the menu closes.
            GUILayout.Space(6);
            GUILayout.Label("TELEMETRY", GarageSkin.Header);
            bool lg = GUILayout.Toggle(s.logTelemetry, " Log sensor/telemetry data (CSV)");
            if (lg != s.logTelemetry) { s.logTelemetry = lg; changed = true; }
            GUILayout.Label("Off by default; writes to TelemetryLogs/ on Save.", GarageSkin.StatLabel);

            GUILayout.EndScrollView();

            if (changed)
            {
                SettingsStore.Apply();
                SettingsStore.Save();
            }

            GUILayout.Space(8);
            if (MenuButton("← Back")) _page = Page.Root;
        }

        private static bool AssistSlider(string label, ref float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label}: {value:P0}", GUILayout.Width(150));
            float v = GUILayout.HorizontalSlider(value, 0f, 1f, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (Mathf.Approximately(v, value)) return false;
            value = v;
            return true;
        }

        // ---- helpers -----------------------------------------------------------

        private static bool MenuButton(string label) =>
            GUILayout.Button(label, GUILayout.Height(34));

        private int CyclePicker(string label, List<string> options, int index,
            System.Func<string, string> display)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(60));
            if (GUILayout.Button("◀", GUILayout.Width(30)))
                index = (index - 1 + options.Count) % options.Count;
            GUILayout.Label(display(options[Mathf.Clamp(index, 0, options.Count - 1)]),
                GarageSkin.Header, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(30)))
                index = (index + 1) % options.Count;
            GUILayout.EndHorizontal();
            return Mathf.Clamp(index, 0, options.Count - 1);
        }

        private void LoadIfBuilt(string sceneName, System.Action load)
        {
            if (Application.CanStreamedLevelBeLoaded(sceneName)) load();
            else _status = $"Scene '{sceneName}' missing — run Tools ▸ AIHWSim ▸ Create it first.";
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
