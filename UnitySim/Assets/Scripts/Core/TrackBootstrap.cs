using AIHWSim.Garage;
using AIHWSim.Persistence;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Builds the outdoor dirt-track scene at runtime: a bordered oval loop with
    /// berms, dirt jumps and a speed hump, obstacles, and a checkered finish
    /// line with lap timing — plus a drivable car in Manual Mode. Drop this on
    /// one empty GameObject (Tools ▸ AIHWSim ▸ Create Track Scene) and press Play.
    /// </summary>
    public sealed class TrackBootstrap : MonoBehaviour
    {
        [Header("Rates")]
        public int physicsRateHz = 400;   // small stiff RC suspension wants ≤2.5 ms steps
        public int controlRateHz = 100;

        [Header("Options")]
        public bool logCsv = true;
        public bool autoReloadControllerOnChange = false;

        [Header("Track geometry (m)")]
        [Tooltip("Build the classic procedural oval when nothing else supplies a track. " +
                 "Off means this scene's OWN geometry is the map: no ground plane, no " +
                 "loop, no finish line — and therefore no lap counting, so a race mode " +
                 "will say so and fall back to a free drive. Ignored when a scene track " +
                 "or a tile map is selected; those already supply a track.")]
        public bool buildDefaultOval = true;

        public float ovalRadiusX = 12f;
        public float ovalRadiusZ = 7.5f;
        public float roadWidth = 2.5f;
        public float bermWidth = 0.3f;
        public int segments = 80;

        private SimulationRunner _runner;
        private readonly List<PlayerRig> _rigs = new List<PlayerRig>();

        // Cached materials.
        private Material _dirt, _road, _bermA, _bermB, _cone, _block, _barrier, _post, _checker;

        private Vector3 _spawnPos;
        /// <summary>Identity rather than <c>default</c>: every track source assigns
        /// this, but the no-track path below may not, and a zero Quaternion is not a
        /// rotation — it is a car pointing nowhere.</summary>
        private Quaternion _spawnRot = Quaternion.identity;
        private LapTimer _lapTimer;
        private TrackEd.BuiltTrack _built;   // custom maps only; null on the classic oval
        private Arcade.ArcadeDirector _arcade;

        // Bot-race composition.
        private Vector3[] _ovalPath;                 // classic-oval centerline for bots
        private IReadOnlyList<Vector3> _botPath;     // ordered racing line (null = no path)
        private bool _botPathClosed;
        /// <summary>Half road width at each _botPath node (null on LAN paths,
        /// which never run bots). Lets BotDriver size its wander to the road.</summary>
        private List<float> _botHalfWidths;
        private bool _splitScreen;                      // 2+ local humans (not a bot race)

        /// <summary>The hand-authored track's descriptor; null on every other
        /// source. Held because the bot corridor, the sky opt-out and the surface
        /// table all live on it and are needed after the environment is built.</summary>
        private AIHWSim.Track.SceneTrackDescriptor _sceneTrack;

        /// <summary>How many local humans share the screen — the divisor the
        /// viewport table needs, and 1 in every LAN session.</summary>
        private int _localHumans = 1;
        private PlayerRig _humanRig;
        private readonly List<BotDriver> _bots = new List<BotDriver>();

        private void Awake()
        {
            // Two of these exist during a scene-track load, and only one may
            // compose a session. See StandDownAsOverlay.
            if (StandDownAsOverlay()) return;

            // Pressing Play directly in a hand-authored track scene: there is no
            // menu to have chosen it, so adopt it. This replaces the old TTA_Sandbox
            // special case, which forced a hardcoded roster and then early-returned
            // past the bot path, respawn, arena nav and race director — everything
            // that makes a track a track. Going through the real branch below costs
            // nothing and means the scene you test is the scene the game loads.
            var driving = Boot.DrivingSceneDescriptor.Find();

            // Nobody outside this scene chose this session: the menu, a
            // championship round, a LAN join and a resumed snapshot all arrive
            // through GameFlow.LoadTrack, which is the only LoadScene call in the
            // project that reaches a track. Pressing Play arrives through nothing.
            // See GameFlow.LaunchedFromMenu for why this replaced the older proxy
            // ("the scene names itself a track"), which missed every driving scene
            // whose geometry comes from a tile map or a terrain.
            bool directPlay = !GameFlow.LaunchedFromMenu && Net.NetSession.Instance == null;

            if (!GameFlow.HasSceneTrack)
            {
                var here = FindFirstObjectByType<AIHWSim.Track.SceneTrackDescriptor>();
                // Adopting the scene as the track is about geometry, and is right
                // whatever chose the session; it stays outside the directPlay gate.
                if (here != null) GameFlow.ActiveSceneTrack = here.gameObject.scene.name;
            }

            if (directPlay)
            {
                // SessionConfig.ResolvePlayers already synthesises a single
                // merged-input human from ActiveDesign when the roster is empty,
                // so no PlayerSlot needs building by hand here.
                if (SessionConfig.Players.Count == 0) SessionConfig.SetSinglePlayer();

                // THE one place a level's authored rules are applied. Order
                // matters — SetSinglePlayer above resets the rules to free-drive,
                // so the scene's own rules have to land after it.
                string wanted = driving != null ? driving.ApplyLevelDefaults() : null;

                if (GameFlow.ActiveDesign == null)
                {
                    // Resolve returns null for a name it does not know, and a null
                    // design silently becomes VehicleDesign.Default() — a typo in an
                    // Inspector field would otherwise present as "why am I driving
                    // the Stock RC".
                    var chosen = wanted != null ? VehiclePresets.Resolve(wanted) : null;
                    if (chosen == null && wanted != null)
                        Debug.LogWarning($"[TrackBootstrap] LevelSettings names preset "
                            + $"'{wanted}', which is not a VehiclePresets row. Falling "
                            + "back to TT Patrol.");
                    // A hand-authored scene has always defaulted to TT Patrol on a
                    // direct Play, and still does. TrackScene never has: a plain
                    // Play there with no LevelSettings keeps landing on
                    // VehicleDesign.Default(), as it always has.
                    if (wanted != null || GameFlow.HasSceneTrack)
                        GameFlow.ActiveDesign = chosen ?? VehiclePresets.Resolve("TT Patrol");
                }

                // Opponents, if the level asked for any. After the car above, not
                // before: the human's slot is synthesised from ActiveDesign, and a
                // roster built ahead of that would seat the player in nothing.
                // A level that asks for none — which is every level today — leaves
                // the roster exactly as SetSinglePlayer left it.
                if (driving != null) driving.ApplyLevelRoster();
            }

            // The scene's step, if it authored one. Read before any rig is built:
            // every SimulationRunner below is handed these two numbers, and the
            // first one to Start writes Time.fixedDeltaTime for the whole process.
            physicsRateHz = driving != null ? driving.PhysicsRate(physicsRateHz) : physicsRateHz;
            controlRateHz = driving != null ? driving.ControlRate(controlRateHz) : controlRateHz;

            // LAN sessions have their own composition paths (host simulates all
            // cars; clients render ghosts). Guard against a stale mode with no
            // live session (e.g. pressing Play directly in TrackScene).
            if (Net.NetSession.Instance == null &&
                (SessionConfig.Mode == SessionMode.LanHost || SessionConfig.Mode == SessionMode.LanClient))
                SessionConfig.SetSinglePlayer();

            if (SessionConfig.Mode == SessionMode.LanClient) { BuildLanClientScene(); return; }
            if (SessionConfig.Mode == SessionMode.LanHost) { BuildLanHostScene(); return; }

            var slots = SessionConfig.ResolvePlayers();
            // "Split-screen" means 2+ local HUMANS (per-viewport cameras + HUD).
            // A bot race has many cars but exactly one local human — bots must
            // NOT trigger split-screen composition.
            int localHumans = 0;
            foreach (var s in slots) if (!s.isBot && s.isLocal) localHumans++;
            _localHumans = localHumans;
            _splitScreen = localHumans > 1;

            BuildEnvironment();

            // The ordered racing line bots follow (null on finish-less maps).
            _botPath = BotPath.Build(_sceneTrack, GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed,
                out _botHalfWidths);
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);
            // The arena's answer to the racing line — spawn ring, floor bounds
            // and centre. Harmless on a circuit: nothing consults it there.
            Modes.ArenaNav.SetTrack(_built);

            for (int i = 0; i < slots.Count; i++)
                _rigs.Add(BuildPlayerRig(slots[i], i, slots.Count, _splitScreen));
            _humanRig = _rigs.Find(r => !r.slot.isBot) ?? _rigs[0];
            _runner = _humanRig.runner;

            if (_splitScreen)
            {
                if (_lapTimer != null) _lapTimer.showDefaultHud = false;
                var hud = new GameObject("SplitScreenHud").AddComponent<SplitScreenHud>();
                hud.rigs = _rigs;
            }

            var pauseGo = new GameObject("PauseMenu");
            var pause = pauseGo.AddComponent<PauseMenu>();
            pause.runner = _runner;
            pause.runners = new List<SimulationRunner>();
            foreach (var r in _rigs) pause.runners.Add(r.runner);
            pause.tunableBehaviour = _splitScreen ? null : _humanRig.car; // Tune is solo-only
            pause.rigs = _rigs;

            HookLapRecords();
            ConsumePendingSnapshot();

            // The session's rules. Race is the only one that needs a finish line;
            // the arena modes need a spawn ring instead, and both refuse to
            // compose on a map that cannot support them rather than half-running.
            var match = BuildMatchDirector();
            var race = match as RaceDirector;

            // How long after the leader finishes before the results screen appears.
            // This used to be set only inside the arcade branch below, which meant
            // every other race waited for the LAST car — and a bot that spun into
            // scenery never arrives, so those races simply never ended.
            if (race != null) race.resultsGraceSeconds = SessionConfig.ResultsWaitSeconds;

            // Arcade layer, gated exactly like the race above: item boxes and
            // positions both need a finish line, and power-ups in a free-drive
            // would be meaningless.
            if (SessionConfig.Arcade && SessionConfig.TargetLaps > 0 && _lapTimer != null)
            {
                BuildArcade(authority: true);
                if (race != null)
                {
                    race.arcade = true;
                    race.PlayerFinished += _arcade.AwardFinish;
                }
                if (_lapTimer != null) _lapTimer.showDefaultHud = false;
                var ahud = new GameObject("ArcadeHud").AddComponent<Arcade.ArcadeHud>();
                ahud.director = _arcade;
                ahud.splitScreen = _splitScreen;
                ahud.localRig = _humanRig;

                // Arcade SFX, hung off the director's event stream.
                var aaudio = new GameObject("ArcadeAudio").AddComponent<Arcade.ArcadeAudio>();
                aaudio.director = _arcade;
                aaudio.localRig = _humanRig;
            }
        }

        /// <summary>How long the on-screen composition warning stays up. Long enough
        /// to read at a standing start, gone before it can cover anything that
        /// matters; the Console copy is permanent.</summary>
        private const float ComposeWarningSeconds = 14f;

        private string _composeWarning;
        private float _composeWarningUntil;

        /// <summary>
        /// Say that a mode could not be composed — to the Console AND to the screen.
        ///
        /// Both, because the two audiences are different and neither is served by the
        /// other's channel. The Console reaches whoever is authoring the scene; the
        /// banner reaches whoever pressed Play, who would otherwise experience the
        /// refusal as "I chose a race and got a free drive" with no way to tell that
        /// from never having chosen one.
        /// </summary>
        private void WarnCompose(string message)
        {
            Debug.LogWarning($"[TrackBootstrap] {message}");
            _composeWarning = message;
            _composeWarningUntil = Time.unscaledTime + ComposeWarningSeconds;
        }

        /// <summary>
        /// Compose the rules object for <see cref="SessionConfig.Match"/>, or
        /// null for a free drive. One method per mode, called from every scene
        /// composition path, so adding a mode never means finding three places.
        /// </summary>
        private Track.MatchDirector BuildMatchDirector()
        {
            switch (SessionConfig.Match)
            {
                case MatchMode.Derby:
                case MatchMode.Ctf:
                case MatchMode.Soccer:
                    return BuildArenaDirector();
                case MatchMode.FreeRoam:
                    // The town IS the mode: no countdown, no clock, no results
                    // screen. Stated as its own case rather than left to fall
                    // through BuildRaceDirector's `TargetLaps <= 0` early-out,
                    // because a lap count surviving from a previous race would
                    // otherwise compose a RaceDirector on a map that has no
                    // finish line for it to time.
                    return null;
                default:
                    return BuildRaceDirector();
            }
        }

        /// <summary>
        /// The arena modes. All three need a spawn ring rather than a finish
        /// line, so an arena mode chosen on a race circuit composes nothing and
        /// leaves a free drive — the same way a race on a finish-less tile map
        /// already does.
        /// </summary>
        private Track.MatchDirector BuildArenaDirector()
        {
            if (!Modes.ArenaNav.Available)
            {
                WarnCompose($"{SessionConfig.Match} needs a map with spawn points, and this "
                            + "one has none. Falling back to a free drive.");
                return null;
            }

            Track.MatchDirector match = SessionConfig.Match switch
            {
                MatchMode.Derby => new GameObject("DerbyDirector").AddComponent<Modes.DerbyDirector>(),
                MatchMode.Ctf => new GameObject("CtfDirector").AddComponent<Modes.CtfDirector>(),
                _ => new GameObject("SoccerDirector").AddComponent<Modes.SoccerDirector>(),
            };
            match.players = _rigs;
            match.countdownSeconds = SessionConfig.CountdownSeconds;
            HoldGrid();
            HookCratePayout(match);
            return match;
        }

        private Track.MatchDirector BuildRaceDirector()
        {
            // 0 laps is a free drive on purpose — every legacy entry path produced
            // one, and it is what LevelSettings ships as. Not a misconfiguration,
            // so not a warning.
            if (SessionConfig.TargetLaps <= 0) return null;

            // Asking for a race on a map that cannot time a lap used to return null
            // here and say nothing, which presents as "I chose Race and got a free
            // drive" — the composition refusing, indistinguishable from it never
            // having been asked. The refusal is right; the silence was not.
            if (_lapTimer == null)
            {
                bool noTrackAtAll = !buildDefaultOval && !GameFlow.HasSceneTrack
                                    && GameFlow.ActiveTrack == null;
                WarnCompose(
                    $"Race mode wants {SessionConfig.TargetLaps} lap(s), but this map has "
                    + "no finish line, so nothing can time a lap. Falling back to a free "
                    + "drive. "
                    + (noTrackAtAll
                        ? "Build Default Oval is off, so this scene's own geometry has to "
                          + "supply the gates: add a Finish Marker (and Checkpoint Markers) "
                          + "plus a Scene Track Descriptor, or turn the oval back on."
                        : "Add a Finish Marker to the track, or set the mode to Free Roam."));
                return null;
            }

            var race = new GameObject("RaceDirector").AddComponent<RaceDirector>();
            race.targetLaps = SessionConfig.TargetLaps;
            race.timer = _lapTimer;
            race.players = _rigs;
            race.bots = _bots;                          // rubber-band targets
            race.rubberBand = SessionConfig.RubberBand;
            race.countdownSeconds = SessionConfig.CountdownSeconds;
            HoldGrid();
            HookCratePayout(race);
            return race;
        }

        /// <summary>Hold the grid immediately so nothing rolls before the
        /// director's own Start runs.</summary>
        private void HoldGrid()
        {
            if (SessionConfig.CountdownSeconds <= 0) return;
            foreach (var rig in _rigs) rig.car.Frozen = true;
        }

        /// <summary>
        /// Progression: a human placing against at least one opponent rolls a
        /// Scrap Crate for finishing and a Chrome Case for a podium, plus XP for
        /// the level badge. The award itself is UI-layer state — the results
        /// overlay draws the reveal, and the crates wait in the Showroom until
        /// the player opens them. Every mode pays the same way, because every
        /// mode raises the same event.
        /// </summary>
        private void HookCratePayout(Track.MatchDirector match)
        {
            if (SessionConfig.Players.Count <= 1) return;
            match.PlayerFinished += (rig, place) =>
            {
                if (rig?.slot == null || rig.slot.isBot) return;
                Persistence.Progression.OnRaceFinished(place, SessionConfig.Players.Count - 1);
            };
        }

        /// <summary>Create the arcade director and give every rig arcade state.
        /// Firmware rigs are refused inside Register.</summary>
        private void BuildArcade(bool authority)
        {
            _arcade = new GameObject("ArcadeDirector").AddComponent<Arcade.ArcadeDirector>();
            _arcade.IsAuthority = authority;
            _arcade.trackLimits = SessionConfig.TrackLimits;
            _arcade.lapTimer = _lapTimer;
            _arcade.SetTrack(_built, _botPath, _botPathClosed);
            // The handling floor itself is no longer applied here: every rig
            // this machine simulates carries a HandlingFloor component (see
            // AttachHandlingFloor), which steers the racer's bases per frame —
            // so the Arcade/Sim toggle works mid-session and in modes that
            // never build a director at all. Ghost/follower rigs have no
            // component and keep bases of 1, which on a kinematic car is moot.
            foreach (var rig in _rigs) _arcade.Register(rig);
        }

        /// <summary>
        /// Attach the mode-independent handling floor to a rig this machine
        /// simulates. One call site per rig builder covers humans, bots,
        /// split-screen and both LAN roles alike — a snapshot resume rebuilds
        /// the roster without visiting the menu, so a menu-side write would
        /// simply not happen for that session. Firmware rigs are excluded for
        /// the same reason ArcadeDirector.Register refuses them: C controllers
        /// face the raw physics they are validated against.
        /// </summary>
        private static void AttachHandlingFloor(PlayerRig rig)
        {
            if (rig == null || rig.car == null) return;
            if (rig.slot != null && rig.slot.control == DriveControl.Firmware) return;
            var floor = rig.car.gameObject.AddComponent<HandlingFloor>();
            floor.car = rig.car;
            floor.rig = rig;
        }

        // ================= LAN (host simulates everyone; clients render ghosts) ===

        private readonly System.Collections.Generic.Dictionary<int, Net.ClientCarView> _ghosts =
            new System.Collections.Generic.Dictionary<int, Net.ClientCarView>();
        private float _lanPollAccum;
        private readonly int[] _lastCp = new int[Net.NetSession.MaxPlayers];
        // Client only: the one car this machine simulates, and its publisher.
        private PlayerRig _ownRig;
        private Net.OwnStateSender _ownSender;
        // Host only: kinematic stand-ins for the cars its clients simulate.
        private readonly System.Collections.Generic.Dictionary<int, Net.HostCarFollower> _followers =
            new System.Collections.Generic.Dictionary<int, Net.HostCarFollower>();

        /// <summary>
        /// The three track sources, in priority order: a hand-authored scene, a
        /// user-built tile map, or the classic procedural oval. This is the whole
        /// dispatch — every composition path funnels through here so a source added
        /// once is a source every mode understands.
        ///
        /// <see cref="buildDefaultOval"/> is consulted only on the last of them. A
        /// scene track and a tile map ARE tracks; the checkbox exists for the case
        /// where the scene's own geometry is the map and the oval would be laid
        /// through the middle of it.
        /// </summary>
        private void BuildEnvironment()
        {
            if (GameFlow.HasSceneTrack) { BuildSceneEnvironment(); return; }
            if (GameFlow.ActiveTrack != null) { BuildCustomEnvironment(); return; }
            if (buildDefaultOval) { BuildOvalEnvironment(); return; }
            BuildBareEnvironment();
        }

        /// <summary>
        /// Host: a full physics rig for the host's own car, and a kinematic
        /// follower for every remote player — since protocol 4 each client
        /// simulates its own car and streams the result, so the host's copy
        /// exists to be adjudicated against (laps, checkpoints, item boxes,
        /// projectiles) rather than to be driven.
        /// </summary>
        private void BuildLanHostScene()
        {
            var session = Net.NetSession.Instance;
            BuildEnvironment();
            if (_lapTimer != null) _lapTimer.showDefaultHud = false;

            // The racing line. LAN has never run bots, so nothing built this
            // before — but the arcade layer needs it for item-box placement,
            // live positions, missile targeting and wreck recovery, so without
            // it arcade over LAN would silently do almost nothing.
            _botPath = BotPath.Build(_sceneTrack, GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed);
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);
            // The arena's answer to the racing line — spawn ring, floor bounds
            // and centre. Harmless on a circuit: nothing consults it there.
            Modes.ArenaNav.SetTrack(_built);

            foreach (var p in session.Roster)
                _rigs.Add(p.slot == session.LocalSlot ? BuildLanRig(p) : BuildLanFollower(p));
            _runner = _rigs.Count > 0 ? _rigs[0].runner : null;
            HookCarEpochs();
            session.OwnStateReceived += OnOwnState;
            session.RaceEnded += OnLanRaceEnded;   // progression fires on every machine

            var hud = new GameObject("LanHud").AddComponent<Net.LanHud>();
            hud.ownCar = _rigs.Count > 0 ? _rigs[0].car : null;
            var hostMenu = new GameObject("LanSessionMenu").AddComponent<Net.LanSessionMenu>();
            hostMenu.rigs = _rigs;   // so its settings panel can apply assists live

            HookLanLapPublish();
            session.RegisterHostRigs(_rigs);
            session.GridProvider = TeleportToGrid;
            session.PlayerJoined += OnLanPlayerJoined;
            session.PlayerLeft += OnLanPlayerLeft;
            HookLatticeNet(session);

            if (session.Arcade && _lapTimer != null) BuildLanArcade(authority: true);
        }

        /// <summary>
        /// The arcade layer in a LAN session. Identical on both sides except for
        /// the authority flag: the host decides and publishes, a client mirrors
        /// and renders. Everything above the director — HUD, feedback, sound — is
        /// the same component reading the same fields on both machines, which is
        /// the point of keeping arcade state on <c>ArcadeRacer</c> rather than in
        /// the HUD.
        /// </summary>
        private void BuildLanArcade(bool authority)
        {
            BuildArcade(authority);

            var local = _rigs.Find(r => r.slot != null && r.slot.isLocal);

            var ahud = new GameObject("ArcadeHud").AddComponent<Arcade.ArcadeHud>();
            ahud.director = _arcade;
            ahud.localRig = local;
            ahud.splitScreen = false;
            ahud.showBoard = false;   // LanHud already owns the shared board

            var aaudio = new GameObject("ArcadeAudio").AddComponent<Arcade.ArcadeAudio>();
            aaudio.director = _arcade;
            aaudio.localRig = local;

            var link = new GameObject("ArcadeNetLink").AddComponent<Net.ArcadeNetLink>();
            link.director = _arcade;
        }

        private PlayerRig BuildLanRig(Net.NetSession.NetPlayer p)
        {
            bool isLocal = p.slot == Net.NetSession.Instance.LocalSlot;
            var (pos, rot) = SpawnPose(p.slot, Net.NetSession.MaxPlayers);

            var design = !string.IsNullOrEmpty(p.vehicleJson)
                ? JsonUtility.FromJson<VehicleDesign>(p.vehicleJson)
                : (isLocal ? GameFlow.ActiveDesign : null);
            design ??= VehicleDesign.Default();

            var built = VehicleFactory.Build(design, pos, rot, previewKinematic: false);
            built.car.SetSpawn(pos, rot);
            // Assists: the host stores each joiner's prefs on the roster entry;
            // on a client, the only rig built here is our own, so our live
            // Options settings are the better source (and the roster copy of our
            // own entry is the same thing anyway).
            built.car.assists = Net.NetSession.Instance.IsHost
                ? p.assists
                : SessionConfig.P1Assists(Persistence.SettingsStore.Current);
            if (isLocal)
                AssistApplier.ApplyFloor(built.car, SessionConfig.PresetValues(
                    (SessionConfig.AssistPreset)Mathf.Clamp(
                        Persistence.SettingsStore.Current.assistPreset, 0, 3)));

            var carInput = built.car.gameObject.AddComponent<CarInput>();
            carInput.car = built.car;
            carInput.lapTimer = _lapTimer;   // null on a client: lap timing is the host's
            carInput.source = isLocal
                ? new Net.GatedInputSource(new PlayerInputSource(InputDeviceKind.MergedKeyboardGamepad))
                : (IDriverInputSource)Net.NetSession.Instance.InputSourceFor(p.slot);

            // Motor, tyre and impact sound, same as every local rig gets.
            var va = Audio.VehicleAudio.Attach(built.car.gameObject, built.car);
            if (va != null) va.hornStyle = design.hornStyle;

            Camera cam = null;
            if (isLocal) cam = BuildLanCamera(built.car.transform);

            var runnerGo = new GameObject($"SimulationRunner_S{p.slot}");
            var runner = runnerGo.AddComponent<SimulationRunner>();
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.vehicleBehaviour = built.car;
            runner.inputBehaviour = carInput;
            runner.sensorRig = built.rig;
            runner.graphProfile = SimulationRunner.GraphProfile.Car;
            runner.startInManual = true;
            runner.loadControllerDll = false;
            runner.allowModeToggle = false;
            runner.showModeBox = false;
            runner.logCsv = false;
            runner.logLabel = $"s{p.slot}";
            runner.actuationDelayTicks = Persistence.SettingsStore.Current.actuationDelayTicks;

            var rig = new PlayerRig
            {
                slot = new PlayerSlot
                {
                    name = p.name,
                    profileId = p.name,
                    design = design,
                    isLocal = isLocal,
                },
                car = built.car,
                input = carInput,
                runner = runner,
                sensorRig = built.rig,
                camera = cam,
                lapTimer = _lapTimer,
                netSlot = p.slot,
            };
            // BuildLanRig only ever builds the car this machine owns and
            // simulates — the rest of the roster becomes followers/ghosts.
            AttachHandlingFloor(rig);
            return rig;
        }

        /// <summary>
        /// Host: the local stand-in for a car a client owns. Kinematic, no
        /// input, no runner, no camera — it is driven entirely by that client's
        /// stream. It still carries the full car (colliders included) because
        /// every shared rule the host adjudicates resolves by touching one.
        /// </summary>
        private PlayerRig BuildLanFollower(Net.NetSession.NetPlayer p)
        {
            var (pos, rot) = SpawnPose(p.slot, Net.NetSession.MaxPlayers);
            var design = !string.IsNullOrEmpty(p.vehicleJson)
                ? JsonUtility.FromJson<VehicleDesign>(p.vehicleJson)
                : null;
            design ??= VehicleDesign.Default();

            var built = VehicleFactory.Build(design, pos, rot, previewKinematic: true);
            built.car.SetSpawn(pos, rot);

            var rig = new PlayerRig
            {
                slot = new PlayerSlot
                {
                    name = p.name,
                    profileId = p.name,
                    design = design,
                    isLocal = false,
                },
                car = built.car,
                sensorRig = built.rig,
                lapTimer = _lapTimer,
                netSlot = p.slot,
            };

            var follower = built.root.AddComponent<Net.HostCarFollower>();
            follower.slot = p.slot;
            follower.car = built.car;
            follower.rig = rig;
            _followers[p.slot] = follower;
            return rig;
        }

        private void OnOwnState(int slot, Net.OwnStateMsg s)
        {
            if (_followers.TryGetValue(slot, out var f) && f != null) f.Receive(s);
            // Track limits are judged by the machine with the wheels; the host's
            // copy is kinematic, so the verdict rides in with the pose.
            _arcade?.NotifyRemoteTrackLimit(slot, s.penalized, s.warned);
            // v10: the remote player's horn, audible from their kinematic copy
            // on the host's machine (clients hear it via the CarState relay).
            var rig = _rigs.Find(r => r.netSlot == slot);
            var audio = rig?.car != null ? rig.car.GetComponent<Audio.VehicleAudio>() : null;
            if (audio != null) audio.externalHorn = s.hornOn;
        }

        private Camera BuildLanCamera(Transform target)
        {
            // Shared with the local paths: a LAN camera renders the same way a
            // single-player one does. What a LAN session does differently is
            // decide which car this machine SIMULATES, and that lives in
            // BuildLanRig, which is deliberately still hand-written until a
            // two-machine test can watch it change.
            Camera cam = Boot.SceneRig.CameraOrCreate();
            cam.farClipPlane = 800f;
            // Background, far plane and (on a themed map) the sky dome's own
            // horizon colour come from the map's ambience.
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue, SceneOwnsSky());
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            AIHWSim.Rendering.CameraBloom.Attach(cam);
            Boot.SceneRig.AttachChase(cam, target);
            return cam;
        }

        /// <summary>
        /// Host: a car this machine simulates just teleported (R respawn), so
        /// tell the clients to snap it rather than lerp a 200 m/s streak across
        /// the map. Cars a client owns bump their own epoch at the source, and
        /// their followers never call ResetVehicle at all.
        /// </summary>
        private void HookCarEpochs()
        {
            var session = Net.NetSession.Instance;
            if (session == null) return;
            foreach (var rig in _rigs)
            {
                if (rig?.car == null || rig.netSlot < 0) continue;
                if (_followers.ContainsKey(rig.netSlot)) continue;
                int slot = rig.netSlot;
                rig.car.VehicleReset += () => Net.NetSession.Instance?.BumpCarEpoch(slot);
            }
        }

        /// <summary>Host: publish lap + checkpoint progress and feed the race logic.</summary>
        private void HookLanLapPublish()
        {
            if (_lapTimer == null) return;
            string trackName = GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "Classic Oval";
            _lapTimer.LapCompleted += (car, t) =>
            {
                foreach (var rig in _rigs)
                {
                    if (rig.car != car) continue;
                    var s = Net.NetSession.Instance;
                    if (s == null) break;
                    s.HostOnLapCompleted(rig.netSlot, t.LapCount, t.LastLap, t.HasBest ? t.BestLap : -1f);
                    s.HostPublishLap(rig.netSlot, t.LapCount, t.LastLap,
                        t.HasBest ? t.BestLap : -1f, t.NextCheckpoint, _lapTimer.CheckpointCount);
                    if (rig.slot.isLocal)
                        Persistence.ProfileStore.RecordLap(rig.slot.profileId, trackName, t.LastLap);
                    break;
                }
            };
        }

        /// <summary>
        /// Frames left before the one-camera check runs; -1 once it has. Deliberately
        /// not <c>Start</c>: on a two-scene load nothing guarantees that the authored
        /// scene's Start does not run before the overlay's Awake has stood down, and
        /// a diagnostic that cries wolf on a correct scene is worse than none — the
        /// same rule <c>PhysicsRateAuthority</c> is written to. One full frame later,
        /// both scenes are unambiguously settled.
        /// </summary>
        private int _sceneAuditIn = 1;

        /// <summary>
        /// Exactly one camera and one listener, said out loud when not.
        ///
        /// The composition this file does is invisible until you look at the screen,
        /// and the failure mode it had was quiet in the worst way: with a second
        /// MainCamera in the scene, <c>Camera.main</c> picked one of them and the
        /// chase camera bound to whichever that was, so the game looked "loaded but
        /// with the wrong view" rather than broken. <see cref="StandDownAsOverlay"/>
        /// is the fix; this is the thing that would have named it in one line.
        ///
        /// Split-screen is not a false positive: its second and third cameras are
        /// built untagged and carry no listener, on purpose, so counting only tagged
        /// cameras and enabled listeners is exactly the right question in every
        /// composition this file produces.
        /// </summary>
        private void AuditSceneSingletons()
        {
            int mains = 0, listeners = 0;
            foreach (var c in FindObjectsByType<Camera>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (c.enabled && c.CompareTag("MainCamera")) mains++;
            foreach (var l in FindObjectsByType<AudioListener>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (l.enabled) listeners++;

            if (mains <= 1 && listeners <= 1) return;
            Debug.LogWarning($"[SCENE] this session has {mains} live MainCamera camera(s) "
                + $"and {listeners} AudioListener(s), and there must be exactly one of "
                + "each. Two of everything is what a hand-authored track scene looks "
                + "like when the TrackScene loaded additively on top of it did not "
                + "stand down — check that the scene track carries a TrackBootstrap "
                + "and that TrackScene is still named "
                + $"'{GameFlow.TrackSceneName}'.");
        }

        private void Update()
        {
            if (_sceneAuditIn >= 0 && --_sceneAuditIn < 0) AuditSceneSingletons();

            // Host: 2 Hz checkpoint-progress refresh (laps broadcast on their own).
            if (SessionConfig.Mode != SessionMode.LanHost || _lapTimer == null ||
                Net.NetSession.Instance == null || !Net.NetSession.Instance.IsHost)
                return;
            _lanPollAccum += Time.unscaledDeltaTime;
            if (_lanPollAccum < 0.5f) return;
            _lanPollAccum = 0f;
            foreach (var rig in _rigs)
            {
                if (rig?.car == null || rig.netSlot < 0) continue;
                var t = _lapTimer.GetTracker(rig.car);
                if (t.NextCheckpoint == _lastCp[rig.netSlot]) continue;
                _lastCp[rig.netSlot] = t.NextCheckpoint;
                Net.NetSession.Instance.HostPublishLap(rig.netSlot, t.LapCount, t.LastLap,
                    t.HasBest ? t.BestLap : -1f, t.NextCheckpoint, _lapTimer.CheckpointCount);
            }
        }

        private void OnLanPlayerJoined(Net.NetSession.NetPlayer p)
        {
            // A joiner owns its own car, so we build the follower, not a rig to
            // drive. (Only ever called on the host.)
            var rig = BuildLanFollower(p);
            _rigs.Add(rig);
            if (_arcade != null) _arcade.Register(rig);
        }

        private void OnLanPlayerLeft(Net.NetSession.NetPlayer p)
        {
            _followers.Remove(p.slot);
            var rig = _rigs.Find(r => r.netSlot == p.slot);
            if (rig == null) return;
            _arcade?.Unregister(rig);
            _rigs.Remove(rig);
            if (_lapTimer != null && rig.car != null) _lapTimer.ResetTimer(rig.car);
            if (rig.car != null) Destroy(rig.car.transform.root.gameObject);
            if (rig.runner != null) Destroy(rig.runner.gameObject);
        }

        /// <summary>Host: everyone onto the 2x2 grid behind the line; laps reset.</summary>
        private Net.GridPose[] TeleportToGrid()
        {
            var poses = new System.Collections.Generic.List<Net.GridPose>();
            foreach (var rig in _rigs)
            {
                if (rig?.car == null) continue;
                var (pos, rot) = SpawnPose(rig.netSlot, Net.NetSession.MaxPlayers);
                if (_followers.TryGetValue(rig.netSlot, out var follower) && follower != null)
                {
                    // A follower is kinematic — RestoreState would try to write
                    // velocities PhysX won't accept. Park it and wait for the
                    // owner to acknowledge with a fresh epoch, so unreliable
                    // packets already in flight can't drag it back off the grid.
                    follower.SnapAwaitEpoch(pos, rot);
                    rig.car.SetSpawn(pos, rot);
                }
                else
                {
                    rig.car.RestoreState(pos, rot, Vector3.zero, Vector3.zero);
                    rig.car.SetSpawn(pos, rot);
                    Net.NetSession.Instance?.BumpCarEpoch(rig.netSlot);
                }
                poses.Add(new Net.GridPose { slot = rig.netSlot, pos = pos, rot = rot });
            }
            _lapTimer?.ResetTimer();
            for (int i = 0; i < _lastCp.Length; i++) _lastCp[i] = 0;
            // Nobody starts a race holding a missile they picked up in free roam,
            // and the clock that every arcade deadline hangs off restarts here.
            _arcade?.ResetArcade();
            return poses.ToArray();
        }

        /// <summary>
        /// Client: track from JSON, our own car simulated locally, everyone
        /// else's posed from the host's stream.
        ///
        /// The own car is a full physics rig — the same one the host builds for
        /// itself — because an interpolation delay on the car you are steering
        /// is pure latency with nothing bought for it. Lap timing, item pickups
        /// and every random draw stay host-authoritative; only the driving is
        /// ours.
        /// </summary>
        private void BuildLanClientScene()
        {
            var session = Net.NetSession.Instance;
            BuildEnvironment();

            // The racing line, built while the lap timer still exists (BotPath
            // falls back to the checkpoint order on maps with no spline). The
            // mirroring arcade director needs the same line the host used, or the
            // two would lay their item boxes out differently.
            bool timed = _lapTimer != null;
            _botPath = BotPath.Build(_sceneTrack, GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed);
            // A client owns its own car, so its respawn key is its own business —
            // it needs the line locally even though lap timing is the host's.
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);
            // The arena's answer to the racing line — spawn ring, floor bounds
            // and centre. Harmless on a circuit: nothing consults it there.
            Modes.ArenaNav.SetTrack(_built);

            // Lap timing is host-authoritative: destroy (not disable — physics
            // callbacks fire on disabled behaviours) the trigger components so
            // kinematic ghosts can't arm anything locally.
            foreach (var lt in FindObjectsByType<LapTimer>(FindObjectsSortMode.None)) Destroy(lt);
            foreach (var cp in FindObjectsByType<Checkpoint>(FindObjectsSortMode.None)) Destroy(cp);
            _lapTimer = null;

            foreach (var p in session.Roster)
            {
                if (p.slot == session.LocalSlot) _ownRig = BuildLanRig(p);
                else AddGhost(p);
            }
            if (_ownRig != null)
            {
                _rigs.Add(_ownRig);
                BuildLanCamera(_ownRig.car.transform);
            }

            new GameObject("ClientInputSender").AddComponent<Net.ClientInputSender>();
            var hud = new GameObject("LanHud").AddComponent<Net.LanHud>();
            hud.ownCar = _ownRig?.car;
            var clientMenu = new GameObject("LanSessionMenu").AddComponent<Net.LanSessionMenu>();
            // On a client this is just our own rig, which is the only car this
            // machine simulates — and therefore the only one an assist applies to.
            clientMenu.rigs = _ownRig != null ? new List<PlayerRig> { _ownRig } : null;

            session.CarStateReceived += OnCarState;
            session.RosterChanged += OnClientRosterChanged;
            session.RaceStarted += OnClientRaceStarted;
            session.RaceEnded += OnLanRaceEnded;   // progression fires on every machine
            HookLatticeNet(session);

            if (session.Arcade && timed) BuildLanArcade(authority: false);

            // Created after the arcade layer so the sender can read this rig's
            // ArcadeRacer for the track-limit flags it carries upstream.
            if (_ownRig != null)
            {
                var sender = new GameObject("OwnStateSender").AddComponent<Net.OwnStateSender>();
                sender.car = _ownRig.car;
                sender.rig = _ownRig;
                var wheels = _ownRig.slot?.design?.wheels;
                sender.wheelRadius = wheels is { Count: > 0 } ? wheels[0].radius : 0.033f;
                _ownSender = sender;
            }

            session.SendReady();
        }

        /// <summary>
        /// Client: the host called the grid. We own our car, so we place it
        /// ourselves and bump its epoch — the host's follower and the other
        /// clients then snap rather than lerping us onto the grid from wherever
        /// we were loitering.
        /// </summary>
        private void OnClientRaceStarted(Net.RaceStartMsg m)
        {
            if (_ownRig?.car == null || m?.poses == null) return;
            int slot = _ownRig.netSlot;
            foreach (var g in m.poses)
            {
                if (g.slot != slot) continue;
                _ownRig.car.RestoreState(g.pos, g.rot, Vector3.zero, Vector3.zero);
                _ownRig.car.SetSpawn(g.pos, g.rot);
                _ownSender?.BumpEpoch();
                break;
            }
        }

        private void AddGhost(Net.NetSession.NetPlayer p)
        {
            if (_ghosts.ContainsKey(p.slot)) return;
            var design = string.IsNullOrEmpty(p.vehicleJson)
                ? VehicleDesign.Default() : JsonUtility.FromJson<VehicleDesign>(p.vehicleJson);
            var (pos, rot) = SpawnPose(p.slot, Net.NetSession.MaxPlayers);
            var built = VehicleFactory.Build(design, pos, rot, previewKinematic: true);
            var view = built.root.AddComponent<Net.ClientCarView>();
            view.slot = p.slot;
            view.car = built.car;
            view.hornStyle = design.hornStyle;   // so their honk sounds like their car
            _ghosts[p.slot] = view;

            // Ghosts get rigs too, so the arcade director sees the same shape of
            // world on a client as on the host: one ArcadeRacer per car, carrying
            // the inventory and effects the sync stream fills in. Without it the
            // HUD, the shield bubble and the hit banners would all need a second,
            // client-only implementation.
            var rig = new PlayerRig
            {
                slot = new PlayerSlot
                {
                    name = p.name,
                    profileId = p.name,
                    design = design,
                    isLocal = p.slot == Net.NetSession.Instance.LocalSlot,
                },
                car = built.car,
                netSlot = p.slot,
            };
            _rigs.Add(rig);
            if (_arcade != null) _arcade.Register(rig);
        }

        private void OnCarState(byte epoch, float hostTime, Net.CarState s)
        {
            // Our own car is simulated here, not received. The host relays our
            // state back out for everyone else; taking it back would overwrite
            // the live sim with a round-trip-old copy of itself.
            if (_ownRig != null && s.slot == _ownRig.netSlot) return;
            if (_ghosts.TryGetValue(s.slot, out var view) && view != null)
                view.Receive(epoch, hostTime, s);
        }

        private void OnClientRosterChanged()
        {
            var session = Net.NetSession.Instance;
            if (session == null) return;
            // Never a ghost for our own slot — we simulate that one.
            foreach (var p in session.Roster)
                if (_ownRig == null || p.slot != _ownRig.netSlot) AddGhost(p);
            var stale = new System.Collections.Generic.List<int>();
            foreach (var kv in _ghosts)
                if (!session.Roster.Exists(p => p.slot == kv.Key)) stale.Add(kv.Key);
            foreach (int slot in stale)
            {
                if (_ghosts[slot] != null) Destroy(_ghosts[slot].gameObject);
                _ghosts.Remove(slot);
                var rig = _rigs.Find(r => r.netSlot == slot);
                if (rig == null) continue;
                _arcade?.Unregister(rig);
                _rigs.Remove(rig);
            }
        }

        private void OnDestroy()
        {
            var session = Net.NetSession.Instance;
            if (session == null) return;
            session.PlayerJoined -= OnLanPlayerJoined;
            session.PlayerLeft -= OnLanPlayerLeft;
            session.CarStateReceived -= OnCarState;
            session.RosterChanged -= OnClientRosterChanged;
            session.RaceStarted -= OnClientRaceStarted;
            session.OwnStateReceived -= OnOwnState;
            session.RaceEnded -= OnLanRaceEnded;
            session.LatticeHitReceived -= OnLatticeHitMsg;
            if (session.GridProvider == TeleportToGrid) session.GridProvider = null;
        }

        // ---- crash-frame dent sync (protocol 17) ----------------------------------

        private int _latticeSeq;

        /// <summary>
        /// Wire our own car's crash frame to the session, and the session's
        /// relays to everyone else's cars. Cars without a lattice have no
        /// <c>CarSoftLattice</c>, so a mixed grid degrades per-car: the sculpted
        /// car dents on every machine, the stock car dents nowhere.
        /// </summary>
        private void HookLatticeNet(Net.NetSession session)
        {
            session.LatticeHitReceived += OnLatticeHitMsg;

            int ownSlot = session.LocalSlot;
            var ownRig = _ownRig ?? _rigs.Find(r => r.netSlot == ownSlot);
            var own = ownRig?.car != null
                ? ownRig.car.GetComponent<Vehicles.CarSoftLattice>() : null;
            if (own == null) return;

            // The closure dies with the component and the scene; the session
            // event above is the one that needs the explicit unsubscribe.
            own.HitApplied += h => session.SendLatticeHit(new Net.LatticeHitMsg
            {
                slot = ownSlot,
                seq = ++_latticeSeq,
                pos = h.pointLocal,
                dir = h.dirLocal,
                impulse = h.impulse,
            });
        }

        /// <summary>Replay a peer's hit on their car here — the follower on the
        /// host, the ghost on a client. Our own slot is skipped: this machine
        /// applied that hit at the moment of contact, and the host's broadcast
        /// echo would double it.</summary>
        private void OnLatticeHitMsg(Net.LatticeHitMsg m)
        {
            var session = Net.NetSession.Instance;
            if (m == null || session == null || m.slot == session.LocalSlot) return;

            Vehicles.CarVehicle car = null;
            if (_ghosts.TryGetValue(m.slot, out var view) && view != null) car = view.car;
            else car = _rigs.Find(r => r.netSlot == m.slot)?.car;
            if (car == null) return;

            car.GetComponent<Vehicles.CarSoftLattice>()?.ApplyRemoteHit(
                new Vehicles.CarSoftLattice.LatticeHit
                {
                    pointLocal = m.pos,
                    dirLocal = m.dir,
                    impulse = m.impulse,
                });
        }

        /// <summary>LAN progression: the RaceEnd broadcast reaches host and
        /// clients alike, so each machine banks its own player's result.</summary>
        private void OnLanRaceEnded(Net.RaceEndMsg m)
        {
            var session = Net.NetSession.Instance;
            if (session == null || m.rows == null) return;
            foreach (var row in m.rows)
            {
                if (row.slot != session.LocalSlot) continue;
                if (row.place > 0)
                    Persistence.Progression.OnRaceFinished(row.place, m.rows.Length - 1);
                break;
            }
        }

        /// <summary>
        /// Resolve a design's <see cref="VehicleDesign.controllerDll"/> to a bare
        /// file name inside Plugins/x86_64. The value comes out of vehicle JSON,
        /// which can be hand-edited or arrive over the network, so it is confined
        /// to that one directory: no separators, no parent traversal. Empty or
        /// suspicious input falls back to the shared default.
        /// </summary>
        private static string SafeDllName(string configured)
        {
            const string fallback = "car_controller.dll";
            if (string.IsNullOrWhiteSpace(configured)) return fallback;
            string name = configured.Trim();
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains(".."))
            {
                Debug.LogWarning($"[TrackBootstrap] Ignoring controllerDll '{configured}' " +
                                 $"(path separators are not allowed); using {fallback}.");
                return fallback;
            }
            if (!name.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase)) name += ".dll";
            return name;
        }

        /// <summary>Build one player's car + input + runner + camera at their grid slot.</summary>
        private PlayerRig BuildPlayerRig(PlayerSlot slot, int index, int count, bool splitScreen)
        {
            var (pos, rot) = SpawnPose(index, count);

            var design = slot.design ?? VehicleDesign.Default();
            var built = VehicleFactory.Build(design, pos, rot, previewKinematic: false);
            built.car.SetSpawn(pos, rot);
            built.car.assists = slot.assists;
            // Universal floor. It lives here rather than in the menu path for the
            // same reason ApplyArcadeHandling does: a snapshot resume rebuilds a
            // roster without ever visiting the menu, so a menu-side write would
            // simply not happen for that session.
            AssistApplier.ApplyFloor(built.car, slot, SessionConfig.PresetValues(
                (SessionConfig.AssistPreset)Mathf.Clamp(
                    Persistence.SettingsStore.Current.assistPreset, 0, 3)));

            var carInput = built.car.gameObject.AddComponent<CarInput>();
            carInput.car = built.car;
            carInput.lapTimer = _lapTimer;

            // Motor, tyre and impact sound. Every rig gets it, bots included, so
            // the field around you is audible. Attached here rather than in
            // VehicleFactory so the garage preview and the menu attract cars stay
            // silent. Read-only, which is why it is safe on firmware rigs too.
            var vAudio = Audio.VehicleAudio.Attach(built.car.gameObject, built.car);
            if (vAudio != null) vAudio.hornStyle = design.hornStyle;

            // Input source: bot AI (opponents & the player's "autonomous (bot AI)")
            // drives via CarInput exactly like a human; C-firmware autonomous uses
            // the DLL and ignores CarInput's source.
            if (slot.control == DriveControl.BotAI)
            {
                var bot = new BotDriver(built.car, _botPath, _botPathClosed,
                    (BotDifficulty)Mathf.Clamp(slot.botDifficulty, 0, 2),
                    _botHalfWidths);
                carInput.source = bot;
                if (slot.isBot) _bots.Add(bot); // only opponents get rubber-banded
            }
            else
            {
                carInput.source = new PlayerInputSource(slot.deviceKind, slot.gamepadIndex);
            }
            carInput.enableMouseSteer = !slot.isBot && !splitScreen
                && Persistence.SettingsStore.Current.mouseSteer;

            // Bots have no camera/HUD — they're rendered by the human's camera.
            Camera cam = null;
            GraphOverlay graph = null;
            if (!slot.isBot)
            {
                if (!splitScreen) (cam, graph) = BuildCameraAndGraph(built.car.transform);
                else cam = BuildPlayerCamera(index, built.car.transform);
            }

            // The two discriminators the runner's whole configuration turns on,
            // hoisted so the shared block below can be handed its final values
            // rather than three placeholders a branch then overwrites.
            // "firmware" is asked only of the solo human, exactly as before: a
            // bot or a split-screen player never loads a DLL whatever its slot
            // says, and C-firmware autonomy is the one case that starts
            // closed-loop instead of in Manual.
            bool botOrSplit = slot.isBot || splitScreen;
            bool firmware = !botOrSplit && slot.control == DriveControl.Firmware;
            // A controller under test respawns to its spawn point, not to the
            // nearest point on the line: the run is meant to be repeatable, and
            // the reset also re-arms the DLL (CarVehicle.VehicleReset →
            // SimulationRunner.OnVehicleReset). See CarInput.respawnAtSpawnPoint.
            carInput.respawnAtSpawnPoint = firmware;
            // Telemetry logging is opt-in (Options / pause Settings → "Log
            // sensor/telemetry data"). Default OFF; can also start after the
            // menu closes via SimulationRunner.EnableLogging. Never for bots.
            bool runnerLogCsv = !botOrSplit
                && logCsv && Persistence.SettingsStore.Current.logTelemetry;

            string runnerName = slot.isBot ? $"SimulationRunner_Bot{index}"
                : splitScreen ? $"SimulationRunner_P{index + 1}" : "SimulationRunner";
            var runner = new GameObject(runnerName).AddComponent<SimulationRunner>();
            Boot.CarRunnerRig.ConfigureCarRunner(runner, built.car, carInput, built.rig, graph,
                physicsRateHz, controlRateHz,
                startInManual: !firmware, loadControllerDll: firmware, logCsv: runnerLogCsv);

            runner.autoReloadOnChange = autoReloadControllerOnChange;
            runner.dllRelativePath = "Plugins/x86_64/" + SafeDllName(design.controllerDll);
            // A car that names its own firmware is running a purpose-built mission,
            // so graph distance/heading rather than the generic ToF/camera panes.
            runner.graphProfile = string.IsNullOrWhiteSpace(design.controllerDll)
                ? SimulationRunner.GraphProfile.Car
                : SimulationRunner.GraphProfile.Mission;
            runner.actuationDelayTicks = Persistence.SettingsStore.Current.actuationDelayTicks;
            // Only the solo human may log (mid-session pause-Settings toggle honors this).
            runner.loggable = !botOrSplit;
            // Only meaningful with a controller, so both are the solo human's
            // firmware flag and both are off for everyone else.
            runner.allowModeToggle = firmware;
            runner.showModeBox = firmware;

            if (botOrSplit)
            {
                // Opponents & split-screen humans: no DLL, no mode toggle, no CSV,
                // no full-screen mode box. Bots/humans drive via CarInput.
                runner.logLabel = slot.isBot ? $"bot{index}" : $"p{index + 1}";
            }
            else
            {
                var hud = new GameObject("SensorHud").AddComponent<SensorHud>();
                hud.rig = built.rig;
                // Step-response metrics readout (J) — controller validation aid.
                // runner.Awake already ran (AddComponent above), so Hub exists.
                var metrics = new GameObject("MetricsOverlay").AddComponent<Telemetry.MetricsOverlay>();
                metrics.Hub = runner.Hub;
                // Mission firmware status + live distance vs ground truth. Draws
                // nothing unless the loaded controller publishes dbg/state, so it
                // is inert for every other car.
                var mission = new GameObject("MissionHud").AddComponent<Telemetry.MissionHud>();
                mission.Hub = runner.Hub;
                mission.runner = runner;
            }

            var rig = new PlayerRig
            {
                slot = slot,
                car = built.car,
                input = carInput,
                runner = runner,
                sensorRig = built.rig,
                camera = cam,
                lapTimer = _lapTimer,
            };
            AttachHandlingFloor(rig);
#if UNITY_EDITOR
            // The live-tuning panel, on the solo human's car only. Bots have no
            // Inspector anyone opens and split-screen has no single "the car";
            // in a player build this whole block is gone, along with the
            // component's change detection.
            if (!botOrSplit)
            {
                var tuner = built.car.gameObject.AddComponent<LiveCarTuner>();
                tuner.design = design;
                tuner.rig = rig;
            }
#endif
            return rig;
        }

        /// <summary>
        /// Grid slot per player: a 2-wide grid behind the line (columns ±2.2 m,
        /// rows 5 m apart); single file on narrow custom roads.
        /// </summary>
        private (Vector3, Quaternion) SpawnPose(int index, int count)
        {
            // An arena authors a ring of spawns instead of a start line, and in a
            // team mode each one belongs to an end. Falls through to the grid
            // below when the map has no ring — which is what happens if someone
            // starts a derby on a race circuit.
            if (SessionConfig.IsArenaMatch && Modes.ArenaNav.Available)
            {
                int team = TeamOf(index);
                // Index WITHIN the team, so two teams do not both take spawn 0.
                int within = index;
                if (team >= 0)
                {
                    within = 0;
                    for (int i = 0; i < index && i < SessionConfig.Players.Count; i++)
                        if (TeamOf(i) == team) within++;
                }
                if (Modes.ArenaNav.TrySpawn(team, within, out var apos, out var arot))
                    return (apos, arot);
            }

            // A scene track may author its whole start grid — one TrackSpawnMarker
            // per slot, gridOrder N feeding player N — so the row can follow the
            // road instead of being projected straight backwards from pole. Author
            // fewer markers than the field and the remainder falls through to the
            // procedural row below, which is also what a lone marker does.
            //
            // Gated on HasSceneTrack deliberately: a tile map's spawn list is
            // authored for arena rings, and consuming it here would silently move
            // every existing grid on every existing map.
            if (GameFlow.HasSceneTrack && _built != null &&
                index >= 0 && index < _built.spawns.Count && _built.spawns.Count > 1)
            {
                var s = _built.spawns[index];
                return (s.pos, s.rot);
            }

            if (count <= 1 || index < 0) return (_spawnPos, _spawnRot);

            Vector3 right = _spawnRot * Vector3.right;
            Vector3 back = _spawnRot * Vector3.back;

            bool narrow = GameFlow.ActiveTrack != null && GameFlow.ActiveTrack.tileSize < 0.75f;
            Vector3 offset = narrow
                ? back * (index * 1.2f)
                : right * (index % 2 == 0 ? -0.55f : 0.55f) + back * ((index / 2) * 1.25f);
            return (_spawnPos + offset, _spawnRot);
        }

        /// <summary>The roster's team for a slot index, or -1 in a free-for-all.
        /// Reads the roster rather than the rigs, because spawn poses are needed
        /// while the rigs are still being built.</summary>
        private static int TeamOf(int index)
        {
            if (!SessionConfig.IsTeamMatch) return -1;
            if (index < 0 || index >= SessionConfig.Players.Count) return -1;
            return SessionConfig.Players[index].team;
        }

        /// <summary>
        /// Viewport for local player <paramref name="index"/> of
        /// <paramref name="count"/>: full screen alone, stacked halves for two,
        /// quadrants for three or four. Three players get a quadrant each and
        /// leave the fourth cell empty rather than stretching one of them, which
        /// keeps every player's field of view identical — the thing that
        /// actually matters when they are competing.
        /// </summary>
        private static Rect ViewportFor(int index, int count)
        {
            if (count <= 1) return new Rect(0f, 0f, 1f, 1f);
            if (count == 2)
                return index == 0 ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);

            // Reading order: top-left, top-right, bottom-left, bottom-right.
            int col = index % 2, row = index / 2;
            return new Rect(col * 0.5f, row == 0 ? 0.5f : 0f, 0.5f, 0.5f);
        }

        /// <summary>Split-screen cameras: P1 keeps Camera.main (and the only AudioListener).</summary>
        private Camera BuildPlayerCamera(int index, Transform target)
        {
            Camera cam;
            if (index == 0)
            {
                cam = Boot.SceneRig.CameraOrCreate();
            }
            else
            {
                var go = new GameObject($"Camera_P{index + 1}");
                cam = go.AddComponent<Camera>(); // deliberately NO AudioListener (Unity allows one)
            }

            cam.farClipPlane = 800f;
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue, SceneOwnsSky());
            cam.rect = ViewportFor(index, _localHumans);
            // Per-viewport bloom: OnRenderImage runs on each camera's own
            // viewport-sized RT, so splits cannot bleed into each other.
            AIHWSim.Rendering.CameraBloom.Attach(cam);

            Boot.SceneRig.AttachChase(cam, target);
            return cam;
        }

        /// <summary>Resume a saved session: restore each car's pose/velocity and lap state.</summary>
        private void ConsumePendingSnapshot()
        {
            var s = GameFlow.PendingSnapshot;
            if (s == null) return;
            GameFlow.PendingSnapshot = null;

            int n = Mathf.Min(s.players.Count, _rigs.Count);
            for (int i = 0; i < n; i++)
            {
                var ps = s.players[i];
                var rig = _rigs[i];
                rig.car.RestoreState(ps.position, ps.rotation, ps.linearVelocity, ps.angularVelocity);
                if (rig.lapTimer != null) rig.lapTimer.RestoreTracker(rig.car, ps.lap);
                rig.runner.RestoreSimTime(s.simTime);
            }
        }

        /// <summary>Log completed laps into each driver's persistent profile.</summary>
        private void HookLapRecords()
        {
            if (_lapTimer == null) return;
            string trackName = GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "Classic Oval";
            _lapTimer.LapCompleted += (car, t) =>
            {
                foreach (var rig in _rigs)
                {
                    if (rig.car != car) continue;
                    if (!rig.slot.isBot) // bots don't write to persistent profiles
                        Persistence.ProfileStore.RecordLap(rig.slot.profileId, trackName, t.LastLap);
                    break;
                }
            };
        }

        /// <summary>The classic procedural oval (unchanged legacy path).</summary>
        private void BuildOvalEnvironment()
        {
            BuildMaterials();
            BuildLighting();
            BuildGround();

            var track = new GameObject("Track").transform;
            Vector3[] pts = SamplePath();
            _ovalPath = pts; // bots follow this loop on the classic oval
            BuildLoop(track, pts);
            BuildJumps(track, pts);
            BuildObstacles(track, pts);
            BuildFinishLine(track, pts);
        }

        /// <summary>
        /// No track at all: the scene's own geometry is the map.
        ///
        /// Lighting still runs, because <c>SceneRig.BuildLighting</c> only creates a
        /// sun when the scene has none — a lit scene keeps its own, an unlit one
        /// still renders. Nothing else is built. There is no ground plane, no loop
        /// and no finish line, which means no <see cref="LapTimer"/> and so no lap
        /// counting: that is the trade the checkbox offers, and
        /// <see cref="BuildRaceDirector"/> says it out loud rather than composing a
        /// race that can never be won.
        ///
        /// The spawn pose comes from a <see cref="AIHWSim.Track.TrackSpawnMarker"/>
        /// if the scene has one — the same component a scene track uses, honoured
        /// here without a descriptor because a car has to start somewhere and the
        /// author has already said where. Its authored height is used verbatim: with
        /// no descriptor there is no statement about what counts as ground, so
        /// dropping the car onto the nearest collider could just as easily settle it
        /// on a roof. Failing that, this bootstrap's own transform, which is at least
        /// a thing you can see and drag in the scene view.
        /// </summary>
        private void BuildBareEnvironment()
        {
            BuildLighting();

            var marks = FindObjectsByType<AIHWSim.Track.TrackSpawnMarker>(FindObjectsSortMode.None);
            AIHWSim.Track.TrackSpawnMarker first = null;
            foreach (var m in marks)
                if (first == null || m.gridOrder < first.gridOrder) first = m;

            if (first != null)
            {
                _spawnPos = first.transform.position;
                _spawnRot = Quaternion.Euler(0f, first.transform.eulerAngles.y, 0f);
            }
            else
            {
                _spawnPos = transform.position;
                _spawnRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            }
        }

        /// <summary>A user-built tile map via the shared TrackFactory.</summary>
        private void BuildCustomEnvironment()
        {
            BuildLighting();
            var built = TrackFactory.Build(GameFlow.ActiveTrack, interactive: true);
            _built = built;             // arcade drops item boxes onto its surfaces
            _spawnPos = built.spawnPos;
            _spawnRot = built.spawnRot;
            _lapTimer = built.lapTimer; // null when the map has no finish line
        }

        /// <summary>
        /// A hand-authored scene track. The geometry is already loaded — this scene
        /// WAS loaded first and TrackScene came in additively on top of it — so all
        /// that is built here are the gates, the surface lookup and the spawn poses.
        /// </summary>
        private void BuildSceneEnvironment()
        {
            _sceneTrack = FindFirstObjectByType<AIHWSim.Track.SceneTrackDescriptor>();
            if (_sceneTrack == null)
            {
                // The additive load landed without the track scene's objects, or
                // someone selected a scene name that carries no descriptor. Falling
                // through to the oval keeps the game playable and says why, which
                // beats a black screen and a null reference two systems later.
                Debug.LogError($"[TrackBootstrap] scene track '{GameFlow.ActiveSceneTrack}' " +
                               "has no SceneTrackDescriptor — falling back to the oval.");
                GameFlow.ActiveSceneTrack = null;
                BuildOvalEnvironment();
                return;
            }

            if (_sceneTrack.skyboxOverride != null)
            {
                RenderSettings.skybox = _sceneTrack.skyboxOverride;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
                DynamicGI.UpdateEnvironment();
            }

            // TrackScene came in additively and brought its own Directional Light.
            // The authored scene almost certainly has one too — often with baked
            // lighting matched to it — and two suns double every shadow and wash the
            // scene out. Membership tells them apart: anything not in TrackScene
            // belongs to the track the author lit.
            DisableForeignSuns();
            BuildLighting();
            var built = AIHWSim.Track.SceneTrackBuilder.Build(_sceneTrack, interactive: true);
            _built = built;
            _spawnPos = built.spawnPos;
            _spawnRot = built.spawnRot;
            _lapTimer = built.lapTimer; // null on a FreeRoam scene, as on a tile map
        }

        /// <summary>
        /// Is this object one of the ones TrackScene brought in, rather than part
        /// of the hand-authored track scene underneath it?
        ///
        /// Membership, not <c>gameObject.scene != mine</c>: the overlay is a fixed,
        /// named scene, so this answers the same way no matter which of the two
        /// bootstraps is asking. The relative form only worked from TrackScene's
        /// copy, and read exactly backwards from the authored one — it would have
        /// put out the author's sun and kept the overlay's.
        /// </summary>
        private static bool IsOverlay(GameObject go) =>
            go.scene.name == GameFlow.TrackSceneName;

        /// <summary>
        /// Whether this is TrackScene's copy sitting on top of a track scene that
        /// brought its own bootstrap — in which case it composes nothing, and takes
        /// the rest of TrackScene's furniture back out of the scene on its way past.
        ///
        /// <b>Why there are two at all.</b> <c>GameFlow.LoadTrack</c> loads a scene
        /// track Single and pulls TrackScene in additively on top for the
        /// composition; but every scene track in the project also carries a
        /// TrackBootstrap of its own, because that is what makes pressing Play in
        /// the scene build a drivable session. Nothing used to stop the second one
        /// building a whole second session — two cars, two SimulationRunners, two
        /// MainCamera-tagged cameras and two AudioListeners, with <c>Camera.main</c>
        /// arbitrating between the cameras. That is how the chase camera ended up
        /// following a car on a camera that was not the one drawing the frame.
        ///
        /// <b>The overlay yields, not the track scene.</b> The authored copy is the
        /// one a scene's author can see and configure, and the only one present when
        /// the scene is played on its own — so deferring to it means the session you
        /// test is the session the game builds. Both ship identical rates today; if
        /// they ever drift, the authored one is the one that meant it.
        ///
        /// <b>Decided by scene membership, never by Awake order.</b> Whether a
        /// Single-then-Additive pair in one frame runs the first scene's Awakes
        /// before the second scene loads is a Unity implementation detail, and both
        /// orders have to reach the same answer. The test is only "is there a
        /// bootstrap outside TrackScene", which is true from the moment both scenes
        /// are loaded, whoever woke first.
        /// </summary>
        private bool StandDownAsOverlay()
        {
            if (!IsOverlay(gameObject)) return false;

            bool authored = false;
            foreach (var b in FindObjectsByType<TrackBootstrap>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (b != this && !IsOverlay(b.gameObject)) { authored = true; break; }
            if (!authored) return false;

            WithdrawOverlayScenery();
            enabled = false;
            return true;
        }

        /// <summary>
        /// Take TrackScene's camera and sun out of a track scene that has its own.
        ///
        /// Both halves are one-directional, for the reason
        /// <see cref="DisableForeignSuns"/> gives: a track scene with no camera
        /// (UCSD_TrackScene, Arcade_Test_Scene) or no sun of its own keeps
        /// TrackScene's, because the alternative is a scene that renders nothing or
        /// renders black.
        ///
        /// The camera is DEACTIVATED rather than disabled, so the AudioListener
        /// riding on it goes with it — Unity permits exactly one, and the second was
        /// half of what this bug logged. Deactivating is also what takes it out of
        /// <c>Camera.main</c>, which is the arbitration that put the chase camera on
        /// the wrong object.
        ///
        /// The sun is handled here as well as in <see cref="DisableForeignSuns"/>
        /// because only one of the two can be relied on to run late enough: if the
        /// authored scene's Awakes run before TrackScene is loaded, its
        /// DisableForeignSuns pass sees no overlay light at all and the overlay's
        /// sun would arrive afterwards unopposed. Whichever runs second does the
        /// work; both are idempotent.
        /// </summary>
        private void WithdrawOverlayScenery()
        {
            bool authoredCamera = false;
            foreach (var c in FindObjectsByType<Camera>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (!IsOverlay(c.gameObject)) { authoredCamera = true; break; }

            if (authoredCamera)
                foreach (var c in FindObjectsByType<Camera>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (IsOverlay(c.gameObject)) c.gameObject.SetActive(false);

            DisableForeignSuns();
        }

        /// <summary>
        /// Turn off TrackScene's own directional light when the loaded track scene
        /// supplies one. Kept one-directional on purpose: a track scene with NO sun
        /// of its own keeps TrackScene's, so a half-lit authored scene still renders
        /// rather than going black.
        ///
        /// Reads TrackScene by name rather than as "the scene I am not in", so it
        /// means the same thing called from the overlay's bootstrap and from the
        /// authored scene's — see <see cref="IsOverlay"/>.
        /// </summary>
        private void DisableForeignSuns()
        {
            bool authoredSun = false;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && l.enabled && !IsOverlay(l.gameObject))
                { authoredSun = true; break; }
            if (!authoredSun) return;

            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional && IsOverlay(l.gameObject))
                    l.enabled = false;
        }

        private void BuildMaterials()
        {
            _dirt = TrackBuilder.StandardMat(new Color(0.42f, 0.32f, 0.22f));
            _road = TrackBuilder.StandardMat(new Color(0.30f, 0.26f, 0.20f));
            _bermA = TrackBuilder.StandardMat(new Color(0.80f, 0.20f, 0.15f));
            _bermB = TrackBuilder.StandardMat(new Color(0.92f, 0.92f, 0.92f));
            _cone = TrackBuilder.StandardMat(new Color(0.95f, 0.45f, 0.05f));
            _block = TrackBuilder.StandardMat(new Color(0.55f, 0.55f, 0.60f));
            _barrier = TrackBuilder.StandardMat(new Color(0.85f, 0.75f, 0.10f));
            _post = TrackBuilder.StandardMat(new Color(0.9f, 0.9f, 0.9f));

            _checker = TrackBuilder.StandardMat(Color.white);
            _checker.mainTexture = TrackBuilder.CheckerTexture(8, 16);
            _checker.mainTextureScale = new Vector2(roadWidth / 0.3f, 1.5f);
        }

        /// <summary>The outdoor background every non-themed map has always had.</summary>
        private static readonly Color SkyBlue = new Color(0.53f, 0.70f, 0.92f);

        /// <summary>This map's <see cref="TrackEd.MapAmbience"/> key ("" for the
        /// classic oval and every map authored before the TinyTorque ports).</summary>
        private string AmbienceKey()
        {
            if (_sceneTrack != null) return _sceneTrack.ambience;
            return GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.ambience : "";
        }

        /// <summary>
        /// Whether the camera's clear flags and background must be left alone.
        /// True only on a scene track that says so — a hand-authored scene has a
        /// real skybox and baked reflection probes, and MapAmbience.ApplyCamera
        /// would otherwise replace both with a flat colour.
        /// </summary>
        private bool SceneOwnsSky() => _sceneTrack != null && _sceneTrack.sceneOwnsSky;

        /// <summary>
        /// One directional key light. Colour, angle and intensity are retuned
        /// afterwards by MapAmbience when the map is themed — this only
        /// guarantees a light exists for it to retune.
        /// </summary>
        private void BuildLighting()
        {
            // A hand-authored scene brings its own sun (and often baked lighting to
            // match it), so SceneRig's any-light guard already does the right thing
            // there — the scene-name special case this used to carry has moved onto
            // SceneTrackDescriptor.skyboxOverride, where it is authored rather than
            // hardcoded.
            Boot.SceneRig.BuildLighting(1.1f, new Vector3(50f, -30f, 0f));
        }

        private void BuildGround()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(30f, 1f, 30f); // 300 m
            ground.GetComponent<Renderer>().sharedMaterial = _dirt;
        }

        private Vector3[] SamplePath()
        {
            var pts = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float t = 2f * Mathf.PI * i / segments;
                pts[i] = new Vector3(ovalRadiusX * Mathf.Cos(t), 0f, ovalRadiusZ * Mathf.Sin(t));
            }
            return pts;
        }

        private void BuildLoop(Transform parent, Vector3[] pts)
        {
            int n = pts.Length;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = pts[i];
                Vector3 pNext = pts[(i + 1) % n];
                Vector3 seg = pNext - p;
                float len = seg.magnitude;
                Vector3 dir = seg / len;
                Vector3 mid = (p + pNext) * 0.5f;
                Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
                Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

                // Road ribbon (visual only — cars ride the ground plane, no seam jitter).
                TrackBuilder.Box("Road", mid + Vector3.up * 0.02f,
                    new Vector3(roadWidth, 0.04f, len * 1.1f), rot, _road, parent, collider: false);

                // Berm walls bound the loop (with colliders).
                Material bermMat = (i % 2 == 0) ? _bermA : _bermB;
                float off = roadWidth * 0.5f + bermWidth * 0.5f;
                TrackBuilder.Box("BermOuter", mid + right * off + Vector3.up * 0.075f,
                    new Vector3(bermWidth, 0.15f, len * 1.1f), rot, bermMat, parent);
                TrackBuilder.Box("BermInner", mid - right * off + Vector3.up * 0.075f,
                    new Vector3(bermWidth, 0.15f, len * 1.1f), rot, bermMat, parent);
            }
        }

        private (Vector3 pos, Vector3 dir, float yaw) PathAt(Vector3[] pts, int index)
        {
            int n = pts.Length;
            int i = ((index % n) + n) % n;
            Vector3 p = pts[i];
            Vector3 dir = (pts[(i + 1) % n] - p).normalized;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            return (p, dir, yaw);
        }

        private void BuildJumps(Transform parent, Vector3[] pts)
        {
            int n = pts.Length;

            // Two takeoff ramps on the driving line.
            var r1 = PathAt(pts, n / 8);
            TrackBuilder.Ramp("Ramp1", r1.pos, r1.yaw, 1.5f, roadWidth * 0.8f, 0.1f, 16f, _dirt, parent);
            var r2 = PathAt(pts, n / 8 + 4);
            TrackBuilder.Ramp("Ramp2", r2.pos, r2.yaw, 1.2f, roadWidth * 0.8f, 0.1f, 20f, _dirt, parent);

            // Rounded speed hump across the track (radius 0.4, ~0.13 m proud).
            var h = PathAt(pts, 3 * n / 8);
            Vector3 right = Vector3.Cross(Vector3.up, h.dir).normalized;
            Quaternion humpRot = Quaternion.FromToRotation(Vector3.up, right);
            TrackBuilder.Cylinder("Hump", h.pos + Vector3.up * -0.27f,
                new Vector3(0.8f, roadWidth * 0.5f, 0.8f), humpRot, _dirt, parent);
        }

        private void BuildObstacles(Transform parent, Vector3[] pts)
        {
            int n = pts.Length;

            // Slalom cones alternating across the driving line.
            for (int k = 0; k < 6; k++)
            {
                var s = PathAt(pts, 5 * n / 8 + k);
                Vector3 right = Vector3.Cross(Vector3.up, s.dir).normalized;
                float side = (k % 2 == 0) ? 1f : -1f;
                Vector3 pos = s.pos + right * side * (roadWidth * 0.22f);
                TrackBuilder.Cone($"Cone{k}", pos, 0.18f, 0.07f, _cone, parent);
            }

            // A couple of blocks and a barrier off to the side of another stretch.
            var b = PathAt(pts, 7 * n / 8);
            Vector3 bRight = Vector3.Cross(Vector3.up, b.dir).normalized;
            TrackBuilder.Box("Block1", b.pos + bRight * roadWidth * 0.2f + Vector3.up * 0.1f,
                new Vector3(0.2f, 0.2f, 0.2f), Quaternion.LookRotation(b.dir), _block, parent);
            TrackBuilder.Box("Block2", b.pos - bRight * roadWidth * 0.25f + Vector3.up * 0.1f,
                new Vector3(0.2f, 0.2f, 0.2f), Quaternion.LookRotation(b.dir), _block, parent);
            TrackBuilder.Box("Barrier", b.pos + b.dir * 1.5f + Vector3.up * 0.125f,
                new Vector3(roadWidth * 0.6f, 0.25f, 0.125f), Quaternion.LookRotation(b.dir), _barrier, parent);
        }

        private void BuildFinishLine(Transform parent, Vector3[] pts)
        {
            var f = PathAt(pts, 0);
            Quaternion rot = Quaternion.LookRotation(f.dir, Vector3.up);

            // Checkered strip across the road.
            TrackBuilder.Box("FinishStrip", f.pos + Vector3.up * 0.008f,
                new Vector3(roadWidth, 0.016f, 0.4f), rot, _checker, parent, collider: false);

            // Posts either side.
            Vector3 right = Vector3.Cross(Vector3.up, f.dir).normalized;
            TrackBuilder.Cylinder("PostL", f.pos + right * (roadWidth * 0.5f) + Vector3.up * 0.4f,
                new Vector3(0.08f, 0.4f, 0.08f), Quaternion.identity, _post, parent);
            TrackBuilder.Cylinder("PostR", f.pos - right * (roadWidth * 0.5f) + Vector3.up * 0.4f,
                new Vector3(0.08f, 0.4f, 0.08f), Quaternion.identity, _post, parent);

            // Trigger volume + lap timer.
            var trig = new GameObject("FinishTrigger");
            trig.transform.SetParent(parent, false);
            trig.transform.SetPositionAndRotation(f.pos + Vector3.up * 0.5f, rot);
            var box = trig.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(roadWidth, 1f, 0.25f);
            _lapTimer = trig.AddComponent<LapTimer>();

            // Spawn the car just behind the line, facing along the track.
            _spawnPos = f.pos - f.dir * 1.5f + Vector3.up * 0.08f;
            _spawnRot = Quaternion.LookRotation(f.dir, Vector3.up);
        }

        private (Camera, GraphOverlay) BuildCameraAndGraph(Transform target)
        {
            Camera cam = Boot.SceneRig.CameraOrCreate();
            cam.farClipPlane = 800f;
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue, SceneOwnsSky());
            AIHWSim.Rendering.CameraBloom.Attach(cam);

            Boot.SceneRig.AttachChase(cam, target);

            var graph = cam.gameObject.GetComponent<GraphOverlay>() ?? cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }

        /// <summary>
        /// The banner for a mode that refused to compose. Drawn before the
        /// split-screen early-out below: the DLL box is a solo-only debug affordance,
        /// but "your race is not running" is news in every session.
        /// </summary>
        private void DrawComposeWarning()
        {
            if (_composeWarning == null || Time.unscaledTime > _composeWarningUntil) return;

            UI.UIScale.Begin();
            const float w = 620f, h = 72f;
            var area = new Rect((UI.UIScale.W - w) * 0.5f, 8f, w, h);
            var prev = GUI.color;
            GUI.color = new Color(1f, 0.82f, 0.30f);
            GUILayout.BeginArea(area, GUI.skin.box);
            GUILayout.Label(_composeWarning);
            GUILayout.EndArea();
            GUI.color = prev;
            UI.UIScale.End();
        }

        private void OnGUI()
        {
            DrawComposeWarning();
            if (_splitScreen) return; // split-screen: humans only, no DLL box
            UI.UIScale.Begin();
            const float w = 230f, h = 74f;
            var area = new Rect(UI.UIScale.W - w - 10f, 44f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            string status = _runner != null && _runner.ControllerReady
                ? "controller: LOADED" : "controller: none";
            GUILayout.Label($"{status}\nWASD/arrows or gamepad · Space=handbrake · R=respawn");
            if (GUILayout.Button("Reload Controller DLL"))
                _runner?.ReloadController();
            GUILayout.EndArea();
            UI.UIScale.End();
        }
    }
}
