using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Sensors;
using AIHWSim.Telemetry;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
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
        private Quaternion _spawnRot;
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
        private bool _splitScreen;                   // 2+ local humans (not a bot race)
        private PlayerRig _humanRig;
        private readonly List<BotDriver> _bots = new List<BotDriver>();

        private void Awake()
        {
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
            _splitScreen = localHumans > 1;

            if (GameFlow.ActiveTrack != null) BuildCustomEnvironment();
            else BuildOvalEnvironment();

            // The ordered racing line bots follow (null on finish-less maps).
            _botPath = BotPath.Build(GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed,
                out _botHalfWidths);
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);

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

            // Race mode (first to N laps) when configured and the map can time laps.
            RaceDirector race = null;
            if (SessionConfig.TargetLaps > 0 && _lapTimer != null)
            {
                race = new GameObject("RaceDirector").AddComponent<RaceDirector>();
                race.targetLaps = SessionConfig.TargetLaps;
                race.timer = _lapTimer;
                race.players = _rigs;
                race.bots = _bots;                          // rubber-band targets
                race.rubberBand = SessionConfig.RubberBand;
                race.countdownSeconds = SessionConfig.CountdownSeconds;
                // Hold the grid immediately so nothing rolls before RaceDirector.Start.
                if (SessionConfig.CountdownSeconds > 0)
                    foreach (var rig in _rigs) rig.car.Frozen = true;
            }

            // Arcade layer, gated exactly like the race above: item boxes and
            // positions both need a finish line, and power-ups in a free-drive
            // would be meaningless.
            if (SessionConfig.Arcade && SessionConfig.TargetLaps > 0 && _lapTimer != null)
            {
                BuildArcade(authority: true);
                if (race != null)
                {
                    race.arcade = true;
                    // A repeatedly spun-out bot must not hold the results screen
                    // hostage forever.
                    race.resultsGraceSeconds = 30f;
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

        /// <summary>Create the arcade director and give every rig arcade state.
        /// Firmware rigs are refused inside Register.</summary>
        private void BuildArcade(bool authority)
        {
            _arcade = new GameObject("ArcadeDirector").AddComponent<Arcade.ArcadeDirector>();
            _arcade.IsAuthority = authority;
            _arcade.trackLimits = SessionConfig.TrackLimits;
            _arcade.lapTimer = _lapTimer;
            _arcade.SetTrack(_built, _botPath, _botPathClosed);
            foreach (var rig in _rigs)
            {
                var racer = _arcade.Register(rig);
                // The handling floor is a physics change, so it belongs on
                // machines that actually simulate the car: every rig on the
                // host, and on a client the one car it owns. The rest of a
                // client's cars are ghosts posed from the stream — there is no
                // grip or drive on them to raise.
                if (authority || rig == _ownRig) ApplyArcadeHandling(racer);
            }
        }

        /// <summary>
        /// Raise one arcade car to the handling floor.
        ///
        /// Applied here — after every rig exists — rather than where the menu
        /// builds its slots, because the slot path is not the only way a session
        /// starts: a snapshot resume rebuilds the roster without ever visiting
        /// the menu, and bots are constructed with a zeroed AssistSettings by
        /// design. One call site covers humans, bots, split-screen and the LAN
        /// host alike.
        ///
        /// Assists are a per-channel MAX, so a player who dialled in higher
        /// values in Options keeps them. Firmware rigs never reach here — Register
        /// refuses them — so C controllers still face the raw physics they are
        /// meant to be validated against.
        /// </summary>
        private void ApplyArcadeHandling(Arcade.ArcadeRacer racer)
        {
            if (racer == null || racer.car == null) return;
            if (!SessionConfig.ArcadeHandling) return;   // Sim handling: leave it alone

            var floor = Arcade.ArcadeConfig.HandlingAssists;
            var a = racer.car.assists;
            a.steer = Mathf.Max(a.steer, floor.steer);
            a.stability = Mathf.Max(a.stability, floor.stability);
            a.traction = Mathf.Max(a.traction, floor.traction);
            a.abs = Mathf.Max(a.abs, floor.abs);
            racer.car.assists = a;

            racer.gripBase = Arcade.ArcadeConfig.HandlingGripBonus;
            racer.driveBase = Arcade.ArcadeConfig.HandlingDriveScale;
            racer.stabilityBase = Arcade.ArcadeConfig.HandlingStabilityBoost;
            racer.RestoreCar();   // push the new baselines onto the car immediately
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

        private void BuildEnvironment()
        {
            if (GameFlow.ActiveTrack != null) BuildCustomEnvironment();
            else BuildOvalEnvironment();
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
            _botPath = BotPath.Build(GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed);
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);

            foreach (var p in session.Roster)
                _rigs.Add(p.slot == session.LocalSlot ? BuildLanRig(p) : BuildLanFollower(p));
            _runner = _rigs.Count > 0 ? _rigs[0].runner : null;
            HookCarEpochs();
            session.OwnStateReceived += OnOwnState;

            var hud = new GameObject("LanHud").AddComponent<Net.LanHud>();
            hud.ownCar = _rigs.Count > 0 ? _rigs[0].car : null;
            var hostMenu = new GameObject("LanSessionMenu").AddComponent<Net.LanSessionMenu>();
            hostMenu.rigs = _rigs;   // so its settings panel can apply assists live

            HookLanLapPublish();
            session.RegisterHostRigs(_rigs);
            session.GridProvider = TeleportToGrid;
            session.PlayerJoined += OnLanPlayerJoined;
            session.PlayerLeft += OnLanPlayerLeft;

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
            Audio.VehicleAudio.Attach(built.car.gameObject, built.car);

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

            return new PlayerRig
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
        }

        private Camera BuildLanCamera(Transform target)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.farClipPlane = 800f;
            // Background, far plane and (on a themed map) the sky dome's own
            // horizon colour come from the map's ambience.
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue);
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            var follow = cam.gameObject.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = target;
            follow.offset = new Vector3(0f, 1.1f, -2.2f);
            follow.followLerp = 5f;
            // Look-back key. Bound here rather than at every call site because
            // the camera already knows which car it is following, and in
            // split-screen that pairing is the only thing that gets it right.
            var lookBackOwner = target != null ? target.GetComponent<CarInput>() : null;
            if (lookBackOwner != null) lookBackOwner.chase = follow;
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

        private void Update()
        {
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
            _botPath = BotPath.Build(GameFlow.ActiveTrack, _lapTimer, _ovalPath,
                _spawnPos, _spawnRot * Vector3.forward, out _botPathClosed);
            // A client owns its own car, so its respawn key is its own business —
            // it needs the line locally even though lap timing is the host's.
            TrackRespawn.SetTrack(_built, _botPath, _botPathClosed);

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
            if (session.GridProvider == TeleportToGrid) session.GridProvider = null;
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
            Audio.VehicleAudio.Attach(built.car.gameObject, built.car);

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

            string runnerName = slot.isBot ? $"SimulationRunner_Bot{index}"
                : splitScreen ? $"SimulationRunner_P{index + 1}" : "SimulationRunner";
            var runner = new GameObject(runnerName).AddComponent<SimulationRunner>();
            runner.physicsRateHz = physicsRateHz;
            runner.controlRateHz = controlRateHz;
            runner.autoReloadOnChange = autoReloadControllerOnChange;
            runner.dllRelativePath = "Plugins/x86_64/" + SafeDllName(design.controllerDll);
            runner.vehicleBehaviour = built.car;
            runner.inputBehaviour = carInput;
            runner.graph = graph;
            runner.sensorRig = built.rig;
            // A car that names its own firmware is running a purpose-built mission,
            // so graph distance/heading rather than the generic ToF/camera panes.
            runner.graphProfile = string.IsNullOrWhiteSpace(design.controllerDll)
                ? SimulationRunner.GraphProfile.Car
                : SimulationRunner.GraphProfile.Mission;
            runner.actuationDelayTicks = Persistence.SettingsStore.Current.actuationDelayTicks;
            // Only the solo human may log (mid-session pause-Settings toggle honors this).
            runner.loggable = !slot.isBot && !splitScreen;

            if (slot.isBot || splitScreen)
            {
                // Opponents & split-screen humans: no DLL, no mode toggle, no CSV,
                // no full-screen mode box. Bots/humans drive via CarInput.
                runner.loadControllerDll = false;
                runner.allowModeToggle = false;
                runner.showModeBox = false;
                runner.logCsv = false;
                runner.startInManual = true;
                runner.logLabel = slot.isBot ? $"bot{index}" : $"p{index + 1}";
            }
            else
            {
                // The solo human. Manual and bot-AI autonomy drive via CarInput
                // (Manual mode); C-firmware autonomy runs the DLL (start closed-loop).
                bool firmware = slot.control == DriveControl.Firmware;
                runner.loadControllerDll = firmware;
                runner.allowModeToggle = firmware;  // only meaningful with a controller
                runner.showModeBox = firmware;
                runner.startInManual = !firmware;
                // Telemetry logging is opt-in (Options / pause Settings → "Log
                // sensor/telemetry data"). Default OFF; can also start after the
                // menu closes via SimulationRunner.EnableLogging.
                runner.logCsv = logCsv && Persistence.SettingsStore.Current.logTelemetry;

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

            return new PlayerRig
            {
                slot = slot,
                car = built.car,
                input = carInput,
                runner = runner,
                sensorRig = built.rig,
                camera = cam,
                lapTimer = _lapTimer,
            };
        }

        /// <summary>
        /// Grid slot per player: a 2-wide grid behind the line (columns ±2.2 m,
        /// rows 5 m apart); single file on narrow custom roads.
        /// </summary>
        private (Vector3, Quaternion) SpawnPose(int index, int count)
        {
            if (count <= 1 || index < 0) return (_spawnPos, _spawnRot);

            Vector3 right = _spawnRot * Vector3.right;
            Vector3 back = _spawnRot * Vector3.back;

            bool narrow = GameFlow.ActiveTrack != null && GameFlow.ActiveTrack.tileSize < 0.75f;
            Vector3 offset = narrow
                ? back * (index * 1.2f)
                : right * (index % 2 == 0 ? -0.55f : 0.55f) + back * ((index / 2) * 1.25f);
            return (_spawnPos + offset, _spawnRot);
        }

        /// <summary>Split-screen cameras: P1 keeps Camera.main (and the only AudioListener), top half; P2 bottom half.</summary>
        private Camera BuildPlayerCamera(int index, Transform target)
        {
            Camera cam;
            if (index == 0)
            {
                cam = Camera.main;
                if (cam == null)
                {
                    var go = new GameObject("Main Camera") { tag = "MainCamera" };
                    cam = go.AddComponent<Camera>();
                    go.AddComponent<AudioListener>();
                }
            }
            else
            {
                var go = new GameObject($"Camera_P{index + 1}");
                cam = go.AddComponent<Camera>(); // deliberately NO AudioListener (Unity allows one)
            }

            cam.farClipPlane = 800f;
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue);
            cam.rect = index == 0 ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);

            var follow = cam.gameObject.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = target;
            follow.offset = new Vector3(0f, 1.1f, -2.2f);
            follow.followLerp = 5f;
            // Look-back key. Bound here rather than at every call site because
            // the camera already knows which car it is following, and in
            // split-screen that pairing is the only thing that gets it right.
            var lookBackOwner = target != null ? target.GetComponent<CarInput>() : null;
            if (lookBackOwner != null) lookBackOwner.chase = follow;
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
        private static string AmbienceKey() =>
            GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.ambience : "";

        /// <summary>
        /// One directional key light. Colour, angle and intensity are retuned
        /// afterwards by MapAmbience when the map is themed — this only
        /// guarantees a light exists for it to retune.
        /// </summary>
        private void BuildLighting()
        {
            if (FindFirstObjectByType<Light>() != null) return;
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
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
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.farClipPlane = 800f;
            TrackEd.MapAmbience.ApplyCamera(cam, AmbienceKey(), SkyBlue);

            var follow = cam.gameObject.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = target;
            follow.offset = new Vector3(0f, 1.1f, -2.2f);
            follow.followLerp = 5f;
            // Look-back key. Bound here rather than at every call site because
            // the camera already knows which car it is following, and in
            // split-screen that pairing is the only thing that gets it right.
            var lookBackOwner = target != null ? target.GetComponent<CarInput>() : null;
            if (lookBackOwner != null) lookBackOwner.chase = follow;

            var graph = cam.gameObject.GetComponent<GraphOverlay>() ?? cam.gameObject.AddComponent<GraphOverlay>();
            return (cam, graph);
        }

        private void OnGUI()
        {
            if (_splitScreen) return; // split-screen: humans only, no DLL box
            const float w = 230f, h = 74f;
            var area = new Rect(Screen.width - w - 10f, 44f, w, h);
            GUILayout.BeginArea(area, GUI.skin.box);
            string status = _runner != null && _runner.ControllerReady
                ? "controller: LOADED" : "controller: none";
            GUILayout.Label($"{status}\nWASD/arrows or gamepad · Space=handbrake · R=respawn");
            if (GUILayout.Button("Reload Controller DLL"))
                _runner?.ReloadController();
            GUILayout.EndArea();
        }
    }
}
