using System;
using System.Collections.Generic;
using System.Text;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.TrackEd;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// The LAN session hub (DontDestroyOnLoad, created by the menu's Host/Join).
    /// Uses Netcode for GameObjects purely as transport + named messaging — a
    /// runtime-created NetworkManager with ZERO NetworkObjects/prefabs (this
    /// project is 100% runtime-generated; cars are arbitrary VehicleDesigns that
    /// are transferred as JSON and rebuilt locally).
    ///
    /// Owner-authoritative for each player's own car (protocol 4): every
    /// machine simulates the car it drives and streams the result at 60 Hz, so
    /// nobody's controls wait for a round trip. The host remains authoritative
    /// over everything shared — laps, race state, item adjudication and every
    /// random draw — and relays each owner's state on to the other clients,
    /// which render it as an interpolated ghost.
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        // v4: owner-authoritative own car. A client simulates its own car and
        // streams the result up; the host follows it kinematically and relays it
        // on. CarState grew an angular velocity and a per-car epoch, and two new
        // messages carry the owner's state and the arcade effects it must
        // replay. The exact equality check in approval rejects mixed builds
        // cleanly, which matters more than ever here: a v3 client would drive a
        // car nobody else can see move.
        //
        // v5: area hazards (smoke, oil). The arcade sync grew three bytes per
        // racer — ArcEffect widened from byte to ushort for the slick flag, plus
        // one byte of remaining blind time — and hazards ride the projectile
        // stream as new kinds. Every field in that block moved, so a v4 client
        // would not mis-render the new items, it would mis-read the whole
        // packet; the equality check is doing real work here.
        //
        // v6: drift visibility. Three spare ArcEffect bits carry drifting + tier
        // down to every client; four spare OwnState flag bits carry
        // drifting/tier/mini-turbo up from the owner, so a client's slide and
        // its payout light up on every machine. No field moved and no byte was
        // added — the bump exists because a v5 host would silently never show a
        // v6 client's drift (and vice versa), and a mixed-cosmetics session is
        // exactly what the equality check exists to refuse.
        //
        // v7: TinyTorque show cars. Appearance travels as the full design JSON,
        // and this build adds three BodyShape values, three wheel styles, an
        // antenna style field and a whole Light part category to that JSON. Not
        // a byte of the wire format changed — but a v6 peer receiving a v7
        // design would render a fallback box with slick wheels and no lights,
        // and the two machines would disagree about what a car looks like.
        // Same reasoning as v6: mixed-cosmetics sessions are refused.
        //
        // v8: TinyTorque map packs. Maps travel as the full track JSON, and
        // this build adds 63 scenery item ids (dt_/toy_/ench_/haunt_) plus
        // four themed circuit presets built from them. Wire format unchanged —
        // but TrackFactory deliberately skips unknown item ids, so a v7 peer
        // receiving a v8 map would build it with every new prop silently
        // missing: no gates, no landmarks, no ghost. Refused like v6/v7.
        //
        // v9: the four themed maps rebuilt as 1:10 ports of the Blender preview
        // maps. Three new fields ride in the track JSON — PlacedItem.scale,
        // PlacedItem.pinned and TrackDesign.ambience — and the maps depend on
        // all three: a v8 peer would build 600 props at scale 1 (the layouts
        // vary nearly every placement between 0.55x and 1.9x), turn ~250 pinned
        // decorative props into live Rigidbodies, and render the map under flat
        // daylight with no sky, fog or glow. Wire format unchanged again;
        // refused for the same reason as v7 and v8.
        //
        // v10: horns + player levels (the arcade UI pass). This one is a REAL
        // wire change: CarState grew a trailing flags byte (bit 1 = horn), so a
        // v9 peer reading a v10 state stream would mis-frame every packet after
        // the first car. The horn also takes a spare bit in the input flags
        // (bit 8, client → host) and the OwnState flags (bit 64, owner → host),
        // hornStyle rides in the design JSON, and HelloMsg/RosterEntry carry a
        // player level for the roster badges. Mixed builds refused, as always.
        //
        // v11: unlockable cosmetics. NOT a message-layout change — the five
        // cosmetic ids ride the design JSON, exactly as hornStyle and liveryPng
        // do, so a v10 peer would still parse every packet. It is bumped anyway
        // because a v10 build has no Cosmetics FBX folder and no catalog: it
        // would silently drop half of what the other screen is showing, and a
        // LAN race where the cars do not match is worse than one that refuses
        // to start.
        // v13 is the four Legendary cars. The wire format is untouched — the
        // body shape and wheel style are ints inside the design JSON, exactly
        // like every shape before them — but a v12 build ships none of the new
        // FBX, so it would draw four fallback boxes where the other screen has
        // a wrecker, two race cars and a 1955 ride car.
        // v14 is the Torque Falls city pack: 35 new track props, a new floor
        // surface and a fifth MatchMode. The map itself crosses the wire as
        // JSON, so a v13 client handed a town it has no meshes for would draw
        // eleven hundred fallback boxes — and the floor id it has never heard
        // of would index past the end of its own catalog.
        // v15 adds hand-authored SCENE tracks. Those cannot be serialised — the
        // scene ships inside the build — so the map crosses the wire as a NAME and
        // each client loads its own copy. A v14 client would read no trackJson,
        // conclude "classic oval" and drive a different track from everyone else
        // while every position message still looked perfectly valid. That failure
        // is invisible, which is exactly why the version had to move.
        // v16 is the string body/wheel KEYS. The wire format is again untouched —
        // bodyKey and wheelKey ride the design JSON beside the ints they migrate
        // from — and this is the first version where a peer can send a name the
        // other end has never compiled. A v15 build drops the unknown fields and
        // reads the ints beside them, which is right for every shipped car and
        // wrong for the first one Asset Studio commits: there is no enum value to
        // write for it, so the int says Box and a v15 peer would race a box while
        // every packet stayed valid. Same invisible failure as v15, same answer.
        //
        // Bumped HERE and not at K2, where the fields were added: until presets
        // and progression started AUTHORING keys, nothing could put a key on the
        // wire that the int beside it did not already say.
        public const int ProtocolVersion = 16;

        /// <summary>Raised from 4 for 3v3 soccer. The slot goes on the wire as a
        /// byte and every MaxPlayers-sized array simply grows, so the only cost
        /// is the roster UI having two more rows to draw.</summary>
        public const int MaxPlayers = 6;
        public const ushort DefaultPort = 7777;

        public enum LanState { FreeRoam, Countdown, Racing, Results }

        public static NetSession Instance { get; private set; }

        /// <summary>Shown by the menu after an involuntary return (host quit, kick...).</summary>
        public static string LastDisconnectReason = "";

        public sealed class NetPlayer
        {
            public ulong clientId;
            public int slot;
            public string name = "";
            public string vehicleJson = "";
            public bool sceneReady;
            public Vehicles.AssistSettings assists;   // applied to the host-side rig
            /// <summary>Progression level, display-only (roster "Lv N" badge, v10).</summary>
            public int level = 1;
        }

        /// <summary>One player's lap standing, mirrored to every machine.</summary>
        [Serializable]
        public sealed class LapStanding
        {
            public int lap;
            public int cp;
            public int cpTotal;
            public float lastLap;
            public float bestLap = -1f;
            public int place;          // 0 = not finished
            public float totalTime;
            public bool finished;

            // Arcade columns. Zero/None in a non-arcade session, so the HUD can
            // read one model either way rather than branching on the mode.
            public int points;
            public int arcPos;         // live arcade position (1 = leader, 0 = unknown)
            public int held;           // ItemKind
            public int charges;
            public ArcEffect effects;
        }

        public bool IsHost { get; private set; }
        public int LocalSlot { get; private set; }
        public LanState State { get; private set; } = LanState.FreeRoam;
        public int TargetLaps { get; private set; }
        public float CountdownEndTime { get; private set; }
        public bool InputsFrozen => State == LanState.Countdown;

        public readonly List<NetPlayer> Roster = new List<NetPlayer>();
        public readonly LapStanding[] Standings = NewStandings();

        // ---- arcade rules (the host's; clients receive them) ------------------
        public bool Arcade { get; private set; }
        public bool TrackLimits { get; private set; }
        public bool ArcadeHandling { get; private set; } = true;

        /// <summary>Latest arcade sync from the host (clients only). Reused
        /// buffers — the consumer reads them inside the event.</summary>
        public readonly List<ArcRacerState> ArcRacers = new List<ArcRacerState>();
        public readonly List<ArcProjState> ArcProjectiles = new List<ArcProjState>();
        public readonly List<byte> ArcBoxMask = new List<byte>();
        public event Action ArcSyncReceived;
        public event Action<ArcEvtMsg> ArcEventReceived;

        // Scene-layer hooks (TrackBootstrap / views subscribe).
        public event Action<NetPlayer> PlayerJoined;    // host: build a rig
        public event Action<NetPlayer> PlayerLeft;      // both: tear down rig/ghost
        public event Action RosterChanged;              // client: sync ghosts
        public event Action<byte, float, CarState> CarStateReceived; // client views
        public event Action StandingsChanged;           // HUDs
        public event Action<RaceStartMsg> RaceStarted;  // scene layer: snap to grid
        public event Action<RaceEndMsg> RaceEnded;      // results overlay

        /// <summary>Host: a client's own-car state arrived (scene layer feeds its follower).</summary>
        public event Action<int, OwnStateMsg> OwnStateReceived;
        /// <summary>Client: the host handed us an arcade effect to apply to our own car.</summary>
        public event Action<ArcFxMsg> ArcFxReceived;

        private NetworkManager _nm;
        private UnityTransport _utp;
        private GameObject _nmGo;
        private byte _epoch;
        private float _stateAccum;
        private const float StreamInterval = 1f / 60f;

        // Per-car teleport counters. The global _epoch means "the whole scene
        // changed"; these mean "this one car jumped" — respawn, wreck recovery,
        // race grid — so one car snapping doesn't flush everyone's buffers.
        private readonly byte[] _carEpochs = new byte[MaxPlayers];

        /// <summary>Latest own-state from a client-owned slot, held for relay.</summary>
        private struct OwnedCar
        {
            public OwnStateMsg state;
            public float receivedAt;
        }
        private readonly Dictionary<int, OwnedCar> _ownedCars = new Dictionary<int, OwnedCar>();

        // Host-side per-slot remote input sources and the rigs being simulated.
        private readonly Dictionary<int, NetworkInputSource> _inputSources =
            new Dictionary<int, NetworkInputSource>();
        private List<PlayerRig> _hostRigs;

        private static LapStanding[] NewStandings()
        {
            var s = new LapStanding[MaxPlayers];
            for (int i = 0; i < s.Length; i++) s[i] = new LapStanding();
            return s;
        }

        // ---- lifecycle -----------------------------------------------------

        public static NetSession Create()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("NetSession");
            DontDestroyOnLoad(go);
            return go.AddComponent<NetSession>();
        }

        private void Awake()
        {
            Instance = this;

            _nmGo = new GameObject("NetworkManager");
            DontDestroyOnLoad(_nmGo);
            _nm = _nmGo.AddComponent<NetworkManager>();
            _utp = _nmGo.AddComponent<UnityTransport>();
            // Fragmented reliable sends (track JSON can be 10-100 KB) exceed the
            // 6144-byte default cap — raise it on BOTH ends.
            _utp.MaxPayloadSize = 256 * 1024;

            _nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = _utp,
                ConnectionApproval = true,
                EnableSceneManagement = false, // we drive SceneManager ourselves
                PlayerPrefab = null,
            };
            _nm.ConnectionApprovalCallback = OnApproval;
            _nm.OnClientConnectedCallback += OnClientConnected;
            _nm.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public bool StartHost(ushort port = DefaultPort)
        {
            IsHost = true;
            LocalSlot = 0;
            _utp.SetConnectionData("0.0.0.0", port, "0.0.0.0"); // listen everywhere
            _nm.NetworkConfig.ConnectionData = ApprovalPayload();
            if (!_nm.StartHost()) { Debug.LogError("[NetSession] StartHost failed"); return false; }
            RegisterHandlers();

            // The host's arcade rules become the session's; joiners are told them
            // in the welcome and never consult their own settings for it.
            Arcade = SessionConfig.Arcade;
            TrackLimits = SessionConfig.TrackLimits;
            ArcadeHandling = SessionConfig.ArcadeHandling;

            Roster.Add(new NetPlayer
            {
                clientId = _nm.LocalClientId,
                slot = 0,
                name = Persistence.SettingsStore.Current.player1Name,
                vehicleJson = GameFlow.ActiveDesign != null ? JsonUtility.ToJson(GameFlow.ActiveDesign) : "",
                sceneReady = true,
                assists = SessionConfig.P1Assists(Persistence.SettingsStore.Current),
                level = Persistence.Progression.Current.level,   // display-only badge
            });
            Debug.Log($"[NetSession] Hosting on UDP {port}");
            return true;
        }

        public bool StartClient(string ip, ushort port = DefaultPort)
        {
            IsHost = false;
            LocalSlot = -1; // assigned by welcome
            _utp.SetConnectionData(ip, port);
            _nm.NetworkConfig.ConnectionData = ApprovalPayload();
            if (!_nm.StartClient()) { Debug.LogError("[NetSession] StartClient failed"); return false; }
            RegisterHandlers();
            Debug.Log($"[NetSession] Connecting to {ip}:{port}");
            return true;
        }

        private static byte[] ApprovalPayload()
        {
            // Deliberately tiny (approval payloads have a low size cap); the
            // vehicle JSON follows post-connect in aihw.hello.
            var hello = new HelloMsg { ver = ProtocolVersion, name = Persistence.SettingsStore.Current.player1Name };
            return Encoding.UTF8.GetBytes(JsonUtility.ToJson(hello));
        }

        /// <summary>Keep the LAN beacon in sync with roster/track changes.</summary>
        private void UpdateAnnounce()
        {
            if (!IsHost) return;
            LanDiscovery.SetAnnounce(new LanDiscovery.Announce
            {
                gameName = Persistence.SettingsStore.Current.player1Name,
                players = Roster.Count,
                trackName = GameFlow.HasSceneTrack
                    ? (AIHWSim.Track.SceneTrackCatalog.LabelFor(GameFlow.ActiveSceneTrack)
                       ?? GameFlow.ActiveSceneTrack)
                    : (GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "Classic Oval"),
            });
        }

        /// <summary>Tear the session down and return to the main menu.</summary>
        public void Leave(string reason = "")
        {
            if (!string.IsNullOrEmpty(reason)) LastDisconnectReason = reason;
            if (IsHost) LanDiscovery.StopBroadcast();
            if (_nm != null && _nm.IsListening) _nm.Shutdown();
            if (Instance == this) Instance = null;
            Destroy(_nmGo);
            Destroy(gameObject);
            SessionConfig.SetSinglePlayer();
            GameFlow.ActiveTrack = null;
            if (Application.CanStreamedLevelBeLoaded(GameFlow.MenuSceneName))
                GameFlow.LoadMenu();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- connection handling (host) --------------------------------------

        private void OnApproval(NetworkManager.ConnectionApprovalRequest req,
            NetworkManager.ConnectionApprovalResponse resp)
        {
            resp.CreatePlayerObject = false;
            resp.Approved = false;

            HelloMsg hello = null;
            try { hello = JsonUtility.FromJson<HelloMsg>(Encoding.UTF8.GetString(req.Payload)); }
            catch { /* malformed → rejected */ }

            if (hello == null || hello.ver != ProtocolVersion)
            {
                resp.Reason = "Version mismatch";
                return;
            }
            if (Roster.Count >= MaxPlayers)
            {
                resp.Reason = "Session full";
                return;
            }
            resp.Approved = true;
        }

        private void OnClientConnected(ulong clientId)
        {
            if (!IsHost)
            {
                if (clientId == _nm.LocalClientId)
                {
                    // Connected: introduce ourselves fully (name + vehicle design
                    // + assist prefs — the host simulates our car).
                    var a = SessionConfig.P1Assists(Persistence.SettingsStore.Current);
                    var hello = new HelloMsg
                    {
                        ver = ProtocolVersion,
                        name = Persistence.SettingsStore.Current.player1Name,
                        vehicleJson = GameFlow.ActiveDesign != null
                            ? JsonUtility.ToJson(GameFlow.ActiveDesign) : "",
                        aSteer = a.steer, aStab = a.stability,
                        aTrac = a.traction, aAbs = a.abs,
                        // Display-only: the roster badge. Progression GATING
                        // never touches the net layer.
                        level = Persistence.Progression.Current.level,
                    };
                    SendJson(NetMsg.Hello, NetworkManager.ServerClientId, hello,
                        NetworkDelivery.ReliableFragmentedSequenced);
                }
                return;
            }
            // Host waits for aihw.hello before creating the player.
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (!IsHost)
            {
                if (clientId == _nm.LocalClientId || clientId == NetworkManager.ServerClientId)
                {
                    string reason = string.IsNullOrEmpty(_nm.DisconnectReason)
                        ? "Disconnected from host" : _nm.DisconnectReason;
                    Leave(reason);
                }
                return;
            }

            var p = Roster.Find(r => r.clientId == clientId);
            if (p == null) return;
            Roster.Remove(p);
            SessionConfig.Players.RemoveAll(s => !s.isLocal && s.name == p.name);
            _inputSources.Remove(p.slot);
            _ownedCars.Remove(p.slot);
            Standings[p.slot] = new LapStanding();
            PlayerLeft?.Invoke(p);
            BroadcastRoster();
            Debug.Log($"[NetSession] {p.name} left (slot {p.slot})");
        }

        // ---- named messages ----------------------------------------------------

        private void RegisterHandlers()
        {
            var cm = _nm.CustomMessagingManager;
            cm.RegisterNamedMessageHandler(NetMsg.Hello, OnHello);
            cm.RegisterNamedMessageHandler(NetMsg.Welcome, OnWelcome);
            cm.RegisterNamedMessageHandler(NetMsg.Roster, OnRoster);
            cm.RegisterNamedMessageHandler(NetMsg.Ready, OnReady);
            cm.RegisterNamedMessageHandler(NetMsg.Input, OnInput);
            cm.RegisterNamedMessageHandler(NetMsg.OwnState, OnOwnState);
            cm.RegisterNamedMessageHandler(NetMsg.State, OnState);
            cm.RegisterNamedMessageHandler(NetMsg.ArcFx, OnArcFx);
            cm.RegisterNamedMessageHandler(NetMsg.Lap, OnLap);
            cm.RegisterNamedMessageHandler(NetMsg.Map, OnMap);
            cm.RegisterNamedMessageHandler(NetMsg.RaceStart, OnRaceStart);
            cm.RegisterNamedMessageHandler(NetMsg.RaceEnd, OnRaceEnd);
            cm.RegisterNamedMessageHandler(NetMsg.SessionState, OnSessionState);
            cm.RegisterNamedMessageHandler(NetMsg.ArcSync, OnArcSync);
            cm.RegisterNamedMessageHandler(NetMsg.ArcEvt, OnArcEvt);
        }

        // ---- arcade sync -------------------------------------------------------

        /// <summary>Host: publish the whole arcade picture. Racer inventories and
        /// effects go straight into Standings (one model for every HUD on every
        /// machine); projectiles and item boxes are handed to the caller's
        /// consumer through <see cref="ArcSyncReceived"/> on the client side.</summary>
        public void HostBroadcastArcSync(List<ArcRacerState> racers,
            List<ArcProjState> projectiles, List<byte> boxMask)
        {
            if (!IsHost || _nm == null || !_nm.IsListening) return;

            foreach (var r in racers) ApplyArcRacer(r);

            int size = 8 + racers.Count * 12 + projectiles.Count * 36 + boxMask.Count;
            using var w = new FastBufferWriter(size, Unity.Collections.Allocator.Temp);
            w.WriteValueSafe((byte)racers.Count);
            foreach (var r in racers) NetPack.WriteArcRacer(w, r);
            w.WriteValueSafe((byte)Mathf.Min(255, projectiles.Count));
            for (int i = 0; i < projectiles.Count && i < 255; i++)
                NetPack.WriteArcProj(w, projectiles[i]);
            w.WriteValueSafe((byte)Mathf.Min(255, boxMask.Count));
            for (int i = 0; i < boxMask.Count && i < 255; i++) w.WriteValueSafe(boxMask[i]);

            _nm.CustomMessagingManager.SendNamedMessageToAll(NetMsg.ArcSync, w,
                NetworkDelivery.UnreliableSequenced);
        }

        private void OnArcSync(ulong sender, FastBufferReader r)
        {
            if (IsHost) return;
            ArcRacers.Clear();
            r.ReadValueSafe(out byte racerCount);
            for (int i = 0; i < racerCount; i++)
            {
                var a = NetPack.ReadArcRacer(r);
                ArcRacers.Add(a);
                ApplyArcRacer(a);
            }

            ArcProjectiles.Clear();
            r.ReadValueSafe(out byte projCount);
            for (int i = 0; i < projCount; i++) ArcProjectiles.Add(NetPack.ReadArcProj(r));

            ArcBoxMask.Clear();
            r.ReadValueSafe(out byte maskLen);
            for (int i = 0; i < maskLen; i++) { r.ReadValueSafe(out byte b); ArcBoxMask.Add(b); }

            ArcSyncReceived?.Invoke();
            StandingsChanged?.Invoke();
        }

        private void ApplyArcRacer(in ArcRacerState a)
        {
            if (a.slot < 0 || a.slot >= MaxPlayers) return;
            var s = Standings[a.slot];
            s.points = a.points;
            s.arcPos = a.position;
            s.held = a.held;
            s.charges = a.charges;
            s.effects = a.effects;
        }

        /// <summary>Host: mirror one arcade event to everyone.</summary>
        public void HostBroadcastArcEvent(ArcEvtMsg m)
        {
            if (!IsHost || _nm == null || !_nm.IsListening) return;
            BroadcastJson(NetMsg.ArcEvt, m);
        }

        private void OnArcEvt(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            ArcEventReceived?.Invoke(ReadJson<ArcEvtMsg>(reader));
        }

        // JSON send helpers.
        public void SendJson(string msgName, ulong clientId, object payload,
            NetworkDelivery delivery = NetworkDelivery.ReliableSequenced)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            using var w = new FastBufferWriter(bytes.Length + 8, Unity.Collections.Allocator.Temp);
            w.WriteValueSafe(bytes.Length);
            w.WriteBytesSafe(bytes);
            _nm.CustomMessagingManager.SendNamedMessage(msgName, clientId, w, delivery);
        }

        public void BroadcastJson(string msgName, object payload,
            NetworkDelivery delivery = NetworkDelivery.ReliableSequenced)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            using var w = new FastBufferWriter(bytes.Length + 8, Unity.Collections.Allocator.Temp);
            w.WriteValueSafe(bytes.Length);
            w.WriteBytesSafe(bytes);
            _nm.CustomMessagingManager.SendNamedMessageToAll(msgName, w, delivery);
        }

        private static T ReadJson<T>(FastBufferReader reader)
        {
            reader.ReadValueSafe(out int len);
            var bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            return JsonUtility.FromJson<T>(Encoding.UTF8.GetString(bytes));
        }

        // ---- handshake -----------------------------------------------------------

        private void OnHello(ulong sender, FastBufferReader reader)
        {
            if (!IsHost) return;
            var hello = ReadJson<HelloMsg>(reader);

            int slot = LowestFreeSlot();
            if (slot < 0) { _nm.DisconnectClient(sender, "Session full"); return; }

            var p = new NetPlayer
            {
                clientId = sender,
                slot = slot,
                name = string.IsNullOrWhiteSpace(hello.name) ? $"Player {slot + 1}" : hello.name,
                vehicleJson = hello.vehicleJson ?? "",
                level = Mathf.Max(1, hello.level),
                assists = new Vehicles.AssistSettings
                {
                    steer = Mathf.Clamp01(hello.aSteer), stability = Mathf.Clamp01(hello.aStab),
                    traction = Mathf.Clamp01(hello.aTrac), abs = Mathf.Clamp01(hello.aAbs),
                },
            };
            Roster.Add(p);
            SessionConfig.Players.Add(ToPlayerSlot(p, isLocal: false));
            Standings[slot] = new LapStanding();

            PlayerJoined?.Invoke(p); // host scene builds the rig

            SendJson(NetMsg.Welcome, sender, new WelcomeMsg
            {
                yourSlot = slot,
                trackJson = GameFlow.ActiveTrack != null ? JsonUtility.ToJson(GameFlow.ActiveTrack) : "",
                trackScene = GameFlow.ActiveSceneTrack ?? "",
                state = (int)State,
                targetLaps = TargetLaps,
                roster = BuildRosterEntries(),
                arcade = Arcade,
                trackLimits = TrackLimits,
                arcadeHandling = ArcadeHandling,
                match = (int)SessionConfig.Match,
                targetScore = SessionConfig.TargetScore,
                timeLimitSec = SessionConfig.TimeLimitSec,
            }, NetworkDelivery.ReliableFragmentedSequenced);
            BroadcastRoster();
            Debug.Log($"[NetSession] {p.name} joined (slot {slot})");
        }

        private void OnWelcome(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var msg = ReadJson<WelcomeMsg>(reader);
            LocalSlot = msg.yourSlot;
            State = (LanState)msg.state;
            TargetLaps = msg.targetLaps;
            ApplyRoster(msg.roster);
            ApplyArcadeRules(msg.arcade, msg.trackLimits, msg.arcadeHandling);
            ApplyMatchRules(msg.match, msg.targetScore, msg.timeLimitSec);

            if (!ApplyWireTrack(msg.trackScene, msg.trackJson)) return;
            SessionConfig.Mode = SessionMode.LanClient;
            SessionConfig.TargetLaps = 0;
            GameFlow.LoadTrack();
            Debug.Log($"[NetSession] Welcome: slot {LocalSlot}, {msg.roster.Length} players");
        }

        private void OnRoster(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            ApplyRoster(ReadJson<RosterMsg>(reader).entries);
            RosterChanged?.Invoke();
        }

        private void OnReady(ulong sender, FastBufferReader reader)
        {
            if (!IsHost) return;
            var p = Roster.Find(r => r.clientId == sender);
            if (p != null) p.sceneReady = true;
        }

        /// <summary>Client → host after its scene is built (or rebuilt on map change).</summary>
        public void SendReady()
        {
            if (IsHost || _nm == null || !_nm.IsListening) return;
            using var w = new FastBufferWriter(4, Unity.Collections.Allocator.Temp);
            w.WriteValueSafe(0);
            _nm.CustomMessagingManager.SendNamedMessage(NetMsg.Ready,
                NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        private void ApplyRoster(RosterEntry[] entries)
        {
            Roster.RemoveAll(r => true);
            SessionConfig.Players.Clear();
            foreach (var e in entries)
            {
                var np = new NetPlayer
                    { slot = e.slot, name = e.name, vehicleJson = e.vehicleJson, level = e.level };
                Roster.Add(np);
                SessionConfig.Players.Add(ToPlayerSlot(np, isLocal: e.slot == LocalSlot));
            }
            SessionConfig.Players.Sort((a, b) =>
                RosterSlotOf(a).CompareTo(RosterSlotOf(b)));
        }

        private int RosterSlotOf(PlayerSlot s)
        {
            foreach (var p in Roster)
                if (p.name == s.name) return p.slot;
            return int.MaxValue;
        }

        private RosterEntry[] BuildRosterEntries()
        {
            var arr = new RosterEntry[Roster.Count];
            for (int i = 0; i < Roster.Count; i++)
                arr[i] = new RosterEntry
                {
                    slot = Roster[i].slot,
                    name = Roster[i].name,
                    vehicleJson = Roster[i].vehicleJson,
                    level = Roster[i].level,
                };
            return arr;
        }

        private void BroadcastRoster()
        {
            BroadcastJson(NetMsg.Roster, new RosterMsg { entries = BuildRosterEntries() },
                NetworkDelivery.ReliableFragmentedSequenced);
            UpdateAnnounce();
        }

        private static PlayerSlot ToPlayerSlot(NetPlayer p, bool isLocal) => new PlayerSlot
        {
            name = p.name,
            profileId = p.name,
            design = string.IsNullOrEmpty(p.vehicleJson)
                ? null : JsonUtility.FromJson<VehicleDesign>(p.vehicleJson),
            deviceKind = InputDeviceKind.MergedKeyboardGamepad,
            isLocal = isLocal,
        };

        private int LowestFreeSlot()
        {
            for (int s = 0; s < MaxPlayers; s++)
            {
                bool used = false;
                foreach (var p in Roster) if (p.slot == s) { used = true; break; }
                if (!used) return s;
            }
            return -1;
        }

        // ---- 30 Hz streams -----------------------------------------------------

        /// <summary>Host scene layer hands over the simulated rigs (rebuilt per scene).</summary>
        public void RegisterHostRigs(List<PlayerRig> rigs)
        {
            _hostRigs = rigs;
            // Poses from the previous scene must not be relayed into this one.
            _ownedCars.Clear();
            BumpEpoch();
        }

        /// <summary>Host input source for a remote slot (created on demand).</summary>
        public NetworkInputSource InputSourceFor(int slot)
        {
            if (!_inputSources.TryGetValue(slot, out var src))
            {
                src = new NetworkInputSource();
                _inputSources[slot] = src;
            }
            return src;
        }

        /// <summary>Invalidate every client interpolation buffer (map loads, scene rebuilds).</summary>
        public void BumpEpoch() => _epoch++;

        /// <summary>
        /// One car teleported. Receivers snap it and drop their history rather
        /// than lerping across the gap — the difference between a respawn and a
        /// car streaking across the map at 200 m/s.
        /// </summary>
        public void BumpCarEpoch(int slot)
        {
            if (slot >= 0 && slot < _carEpochs.Length) _carEpochs[slot]++;
        }

        public byte CarEpoch(int slot) =>
            slot >= 0 && slot < _carEpochs.Length ? _carEpochs[slot] : (byte)0;

        public void SendInput(in InputState input)
        {
            if (IsHost || _nm == null || !_nm.IsListening || _nm.LocalClientId == NetworkManager.ServerClientId)
                return;
            using var w = new FastBufferWriter(32, Unity.Collections.Allocator.Temp);
            NetPack.WriteInput(w, input);
            _nm.CustomMessagingManager.SendNamedMessage(NetMsg.Input,
                NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
        }

        private void OnInput(ulong sender, FastBufferReader reader)
        {
            if (!IsHost) return;
            var p = Roster.Find(r => r.clientId == sender);
            if (p == null) return;
            var input = NetPack.ReadInput(reader);
            InputSourceFor(p.slot).Receive(input);
        }

        /// <summary>Client: publish the car we simulate. 60 Hz, ~66 bytes.</summary>
        public void SendOwnState(in OwnStateMsg s)
        {
            if (IsHost || _nm == null || !_nm.IsListening ||
                _nm.LocalClientId == NetworkManager.ServerClientId) return;
            using var w = new FastBufferWriter(96, Unity.Collections.Allocator.Temp);
            NetPack.WriteOwnState(w, s);
            _nm.CustomMessagingManager.SendNamedMessage(NetMsg.OwnState,
                NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
        }

        private void OnOwnState(ulong sender, FastBufferReader reader)
        {
            if (!IsHost) return;
            var p = Roster.Find(r => r.clientId == sender);
            if (p == null) return;
            var s = NetPack.ReadOwnState(reader);
            _ownedCars[p.slot] = new OwnedCar { state = s, receivedAt = Time.unscaledTime };
            OwnStateReceived?.Invoke(p.slot, s);
        }

        /// <summary>Host: hand one arcade effect to the machine that owns the car.</summary>
        public void SendArcFxTo(int slot, ArcFxMsg msg)
        {
            if (!IsHost || _nm == null || !_nm.IsListening) return;
            var p = Roster.Find(r => r.slot == slot);
            if (p == null || p.clientId == NetworkManager.ServerClientId) return;
            SendJson(NetMsg.ArcFx, p.clientId, msg);
        }

        private void OnArcFx(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<ArcFxMsg>(reader);
            if (m != null) ArcFxReceived?.Invoke(m);
        }

        private void Update()
        {
            if (State == LanState.Countdown && Time.unscaledTime >= CountdownEndTime)
                SetState(LanState.Racing, broadcast: IsHost);

            // Leader home and the stragglers out of time: call it, they're DNF.
            if (IsHost && State == LanState.Racing && _firstFinishAt >= 0f &&
                Time.unscaledTime - _firstFinishAt > DnfGraceSeconds)
                HostEndRace();

            if (!IsHost || _nm == null || !_nm.IsListening || _hostRigs == null) return;
            _stateAccum += Time.unscaledDeltaTime;
            if (_stateAccum < StreamInterval) return;
            // Subtract rather than zero: zeroing rounds the period up to the
            // next whole frame, so a 60 Hz stream silently becomes 50 Hz at
            // 100 fps. Clamp the backlog so a hitch doesn't burst.
            _stateAccum = Mathf.Min(_stateAccum - StreamInterval, StreamInterval);
            BroadcastState();
        }

        private void BroadcastState()
        {
            int n = 0;
            foreach (var rig in _hostRigs) if (rig?.car != null) n++;
            if (n == 0) return;

            using var w = new FastBufferWriter(16 + n * 80, Unity.Collections.Allocator.Temp);
            NetPack.WriteStateHeader(w, _epoch, Time.unscaledTime, (byte)n);
            foreach (var rig in _hostRigs)
            {
                if (rig?.car == null) continue;
                int slot = SlotOfRig(rig);

                // A client-owned car is RELAYED, never re-derived. Our copy of it
                // is a kinematic follower, so its rigidbody reports zero velocity
                // and its wheels never turn — reading them would hand every other
                // client a car that slides along at a dead stop.
                if (_ownedCars.TryGetValue(slot, out var owned))
                {
                    var o = owned.state;
                    NetPack.WriteCarState(w, new CarState
                    {
                        slot = slot,
                        carEpoch = o.carEpoch,
                        pos = o.pos,
                        rot = o.rot,
                        vel = o.vel,
                        angVel = o.angVel,
                        steerDeg = o.steerDeg,
                        wheelRadPerSec = o.wheelRadPerSec,
                        flags = (byte)(o.hornOn ? CarState.FlagHorn : 0),
                    });
                    continue;
                }

                var body = rig.car.GetComponent<Rigidbody>();
                float wheelSpeed = rig.car.ForwardSpeed /
                    Mathf.Max(0.05f, rig.slot.design?.wheels is { Count: > 0 } ws ? ws[0].radius : 0.3f);
                NetPack.WriteCarState(w, new CarState
                {
                    slot = slot,
                    carEpoch = CarEpoch(slot),
                    pos = body.position,
                    rot = body.rotation,
                    vel = body.linearVelocity,
                    angVel = body.angularVelocity,
                    steerDeg = rig.car.CurrentSteerAngle,
                    wheelRadPerSec = wheelSpeed,
                    // Host-simulated car (the host's own, or a remote driven
                    // through NetworkInputSource): the horn state is on the rig.
                    flags = (byte)(rig.input != null && rig.input.HornHeldNow
                        ? CarState.FlagHorn : 0),
                });
            }
            _nm.CustomMessagingManager.SendNamedMessageToAll(NetMsg.State, w,
                NetworkDelivery.UnreliableSequenced);
        }

        private static int SlotOfRig(PlayerRig rig) => Mathf.Max(0, rig.netSlot);

        private void OnState(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            NetPack.ReadStateHeader(reader, out byte epoch, out float hostTime, out byte count);
            for (int i = 0; i < count; i++)
            {
                var cs = NetPack.ReadCarState(reader);
                CarStateReceived?.Invoke(epoch, hostTime, cs);
            }
        }

        // ---- lap + session-state sync (host writes, everyone reads) ------------

        /// <summary>Host: publish one player's lap progress to everyone.</summary>
        public void HostPublishLap(int slot, int lapCount, float lastLap, float bestLap, int cp, int cpTotal)
        {
            if (!IsHost) return;
            var s = Standings[slot];
            s.lap = lapCount; s.lastLap = lastLap; s.bestLap = bestLap; s.cp = cp; s.cpTotal = cpTotal;
            StandingsChanged?.Invoke();
            BroadcastJson(NetMsg.Lap, new LapMsg
            {
                slot = slot, lapCount = lapCount, lastLap = lastLap,
                bestLap = bestLap, cp = cp, cpTotal = cpTotal,
            });
        }

        private void OnLap(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<LapMsg>(reader);
            if (m.slot < 0 || m.slot >= MaxPlayers) return;
            var s = Standings[m.slot];
            bool completedLap = m.lapCount > s.lap && m.lastLap > 0f;
            s.lap = m.lapCount; s.lastLap = m.lastLap; s.bestLap = m.bestLap;
            s.cp = m.cp; s.cpTotal = m.cpTotal;
            StandingsChanged?.Invoke();

            // Each machine records only its OWN laps into its profile.
            if (completedLap && m.slot == LocalSlot)
                Persistence.ProfileStore.RecordLap(
                    Persistence.SettingsStore.Current.player1Name,
                    GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "Classic Oval",
                    m.lastLap);
        }

        public void SetState(LanState state, bool broadcast, int targetLaps = -1, float countdown = 0f)
        {
            State = state;
            if (targetLaps >= 0) TargetLaps = targetLaps;
            if (state == LanState.Countdown) CountdownEndTime = Time.unscaledTime + countdown;
            if (broadcast && IsHost)
                BroadcastJson(NetMsg.SessionState, new SessionStateMsg
                {
                    state = (int)state,
                    targetLaps = TargetLaps,
                    countdownRemaining = Mathf.Max(0f, CountdownEndTime - Time.unscaledTime),
                    arcade = Arcade,
                    trackLimits = TrackLimits,
                    arcadeHandling = ArcadeHandling,
                    match = (int)SessionConfig.Match,
                    targetScore = SessionConfig.TargetScore,
                    timeLimitSec = SessionConfig.TimeLimitSec,
                });
        }

        private void OnSessionState(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<SessionStateMsg>(reader);
            State = (LanState)m.state;
            TargetLaps = m.targetLaps;
            ApplyArcadeRules(m.arcade, m.trackLimits, m.arcadeHandling);
            ApplyMatchRules(m.match, m.targetScore, m.timeLimitSec);
            if (State == LanState.Countdown)
                CountdownEndTime = Time.unscaledTime + m.countdownRemaining;
        }

        /// <summary>Client: adopt the host's arcade rules. Mirrored into
        /// SessionConfig as well, because a map change reloads the scene and
        /// TrackBootstrap composes from there.</summary>
        /// <summary>
        /// The host's RULES, adopted verbatim by a joiner. Same contract as
        /// ApplyArcadeRules and for the same reason: a client composes its scene
        /// from these, and a client that thinks it is racing while everyone else
        /// plays soccer has no way to recover.
        /// </summary>
        private void ApplyMatchRules(int match, int targetScore, int timeLimitSec)
        {
            SessionConfig.Match = (MatchMode)Mathf.Clamp(match, 0, (int)MatchMode.FreeRoam);
            SessionConfig.TargetScore = Mathf.Max(1, targetScore);
            SessionConfig.TimeLimitSec = Mathf.Max(0, timeLimitSec);
        }

        private void ApplyArcadeRules(bool arcade, bool limits, bool handling)
        {
            Arcade = arcade;
            TrackLimits = limits;
            ArcadeHandling = handling;
            SessionConfig.Arcade = arcade;
            SessionConfig.TrackLimits = limits;
            SessionConfig.ArcadeHandling = handling;
        }

        // ---- map / race control (full flows wired in the session-control step) ---

        private void OnMap(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<MapMsg>(reader);
            if (!ApplyWireTrack(m.trackScene, m.trackJson)) return;
            GameFlow.LoadTrack(); // scene rebuild sends aihw.ready again
        }

        /// <summary>
        /// Adopt the host's map. Returns false — having already disconnected — when
        /// the host is on a scene track this build does not contain.
        ///
        /// Refusing is the whole point. Every other mismatch in this protocol is
        /// loud: a version mismatch is rejected at Hello, an unknown item id draws
        /// a fallback box you can see. A missing scene is silent. The client would
        /// find no trackJson, take the classic-oval branch, and then exchange
        /// perfectly well-formed position updates about a track nobody else is on.
        /// </summary>
        private bool ApplyWireTrack(string trackScene, string trackJson)
        {
            if (!string.IsNullOrEmpty(trackScene))
            {
                if (!Application.CanStreamedLevelBeLoaded(trackScene))
                {
                    Debug.LogError($"[NetSession] host is on scene track '{trackScene}', " +
                                   "which this build does not contain");
                    Leave($"This host is racing \"{trackScene}\", a track your copy " +
                          "of the game does not have.");
                    return false;
                }
                GameFlow.ActiveSceneTrack = trackScene;
                return true;
            }

            GameFlow.ActiveTrack = string.IsNullOrEmpty(trackJson)
                ? null : JsonUtility.FromJson<TrackDesign>(trackJson);
            return true;
        }

        private void OnRaceStart(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<RaceStartMsg>(reader);
            TargetLaps = m.laps;
            SetState(LanState.Countdown, broadcast: false, targetLaps: m.laps, countdown: m.countdownSec);
            for (int i = 0; i < Standings.Length; i++) Standings[i] = new LapStanding();
            StandingsChanged?.Invoke();
            RaceStarted?.Invoke(m);
        }

        private void OnRaceEnd(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<RaceEndMsg>(reader);
            SetState(LanState.Results, broadcast: false);
            foreach (var row in m.rows)
            {
                if (row.slot < 0 || row.slot >= MaxPlayers) continue;
                Standings[row.slot].place = row.place;
                Standings[row.slot].totalTime = row.totalTime;
                Standings[row.slot].finished = row.place > 0;
            }
            StandingsChanged?.Invoke();
            RaceEnded?.Invoke(m);
        }

        /// <summary>Host scene layer provides grid poses (and performs the teleport).</summary>
        public Func<GridPose[]> GridProvider;

        private readonly List<int> _raceEntries = new List<int>();
        private int _nextPlace = 1;

        /// <summary>When the leader crossed. Everyone still out there has this long
        /// to finish before the race is called and they are recorded DNF —
        /// otherwise one player who parks, disconnects badly or gets stuck holds
        /// the whole lobby on the track forever. Arcade makes that likelier, not
        /// less: a well-timed missile can cost most of a lap.</summary>
        private float _firstFinishAt = -1f;

        /// <summary>
        /// The host's grace window, in seconds. Follows the same "Results wait"
        /// setting the local race uses, because two different answers to "how long
        /// after the leader?" is a difference nobody can see and everybody trips
        /// over. Host-local timing only — never on the wire, so this is not a
        /// protocol concern. A lobby set to "wait for everyone" still needs a
        /// backstop, since a disconnected client never finishes: hence the 45 s
        /// fallback rather than infinity.
        /// </summary>
        private static float DnfGraceSeconds =>
            SessionConfig.ResultsWaitSeconds > 0 ? SessionConfig.ResultsWaitSeconds : 45f;

        /// <summary>Host: teleport everyone to the grid and start the countdown.</summary>
        public void HostStartRace(int laps)
        {
            if (!IsHost || GridProvider == null) return;

            var poses = GridProvider(); // scene layer teleports host rigs + resets laps
            BumpEpoch();

            _raceEntries.Clear();
            foreach (var p in Roster) _raceEntries.Add(p.slot);
            _nextPlace = 1;
            _firstFinishAt = -1f;
            for (int i = 0; i < Standings.Length; i++) Standings[i] = new LapStanding();
            StandingsChanged?.Invoke();

            const float countdown = 3f;
            SetState(LanState.Countdown, broadcast: false, targetLaps: laps, countdown: countdown);
            BroadcastJson(NetMsg.RaceStart, new RaceStartMsg
            {
                laps = laps,
                countdownSec = countdown,
                poses = poses,
            }, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Host: race bookkeeping fed by the scene layer's LapTimer hook.</summary>
        public void HostOnLapCompleted(int slot, int lapCount, float lastLap, float bestLap)
        {
            if (!IsHost) return;
            var s = Standings[slot];
            if (State == LanState.Racing && _raceEntries.Contains(slot) && !s.finished)
            {
                s.totalTime += lastLap;
                if (lapCount >= TargetLaps)
                {
                    s.finished = true;
                    s.place = _nextPlace++;
                    if (_firstFinishAt < 0f) _firstFinishAt = Time.unscaledTime;
                    if (AllEntriesFinished()) HostEndRace();
                }
            }
        }

        private bool AllEntriesFinished()
        {
            foreach (int slot in _raceEntries)
            {
                // Leavers are removed from the roster; only count present entries.
                bool present = Roster.Exists(p => p.slot == slot);
                if (present && !Standings[slot].finished) return false;
            }
            return true;
        }

        private void HostEndRace()
        {
            SetState(LanState.Results, broadcast: false);
            var rows = new List<ResultRow>();
            foreach (int slot in _raceEntries)
            {
                var p = Roster.Find(r => r.slot == slot);
                var s = Standings[slot];
                rows.Add(new ResultRow
                {
                    slot = slot,
                    place = s.finished ? s.place : 0,
                    name = p != null ? p.name : $"Player {slot + 1}",
                    totalTime = s.totalTime,
                    bestLap = s.bestLap,
                });
            }
            BroadcastJson(NetMsg.RaceEnd, new RaceEndMsg { rows = rows.ToArray() });
            RaceEnded?.Invoke(new RaceEndMsg { rows = rows.ToArray() });
        }

        /// <summary>Host: dismiss results, everyone returns to free roam.</summary>
        public void HostEndResults()
        {
            if (!IsHost) return;
            SetState(LanState.FreeRoam, broadcast: true, targetLaps: 0);
        }

        /// <summary>Host: switch the whole session to a new map (null = classic oval).</summary>
        public void HostChangeMap(TrackDesign design)
        {
            if (!IsHost || State != LanState.FreeRoam) return;
            GameFlow.ActiveTrack = design;   // also clears any scene track
            BroadcastJson(NetMsg.Map, new MapMsg
            {
                trackJson = design != null ? JsonUtility.ToJson(design) : "",
            }, NetworkDelivery.ReliableFragmentedSequenced);
            BumpEpoch();
            _hostRigs = null; // stop streaming until the new scene registers rigs
            UpdateAnnounce();
            GameFlow.LoadTrack();
        }

        /// <summary>Host: kick a client (with a reason shown on their menu).</summary>
        public void HostKick(int slot)
        {
            if (!IsHost) return;
            var p = Roster.Find(r => r.slot == slot);
            if (p == null || p.slot == 0) return;
            _nm.DisconnectClient(p.clientId, "Kicked by host");
        }
    }
}
