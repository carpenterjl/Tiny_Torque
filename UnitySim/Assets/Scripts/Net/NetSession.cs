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
    /// Host-authoritative: the host simulates every car; clients stream inputs
    /// (30 Hz) and render interpolated ghosts from the host's state stream.
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        public const int ProtocolVersion = 2;   // v2: RC-scale physics + assist prefs
        public const int MaxPlayers = 4;
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
        }

        public bool IsHost { get; private set; }
        public int LocalSlot { get; private set; }
        public LanState State { get; private set; } = LanState.FreeRoam;
        public int TargetLaps { get; private set; }
        public float CountdownEndTime { get; private set; }
        public bool InputsFrozen => State == LanState.Countdown;

        public readonly List<NetPlayer> Roster = new List<NetPlayer>();
        public readonly LapStanding[] Standings = NewStandings();

        // Scene-layer hooks (TrackBootstrap / views subscribe).
        public event Action<NetPlayer> PlayerJoined;    // host: build a rig
        public event Action<NetPlayer> PlayerLeft;      // both: tear down rig/ghost
        public event Action RosterChanged;              // client: sync ghosts
        public event Action<byte, float, CarState> CarStateReceived; // client views
        public event Action StandingsChanged;           // HUDs
        public event Action<RaceStartMsg> RaceStarted;  // scene layer: snap to grid
        public event Action<RaceEndMsg> RaceEnded;      // results overlay

        private NetworkManager _nm;
        private UnityTransport _utp;
        private GameObject _nmGo;
        private byte _epoch;
        private float _stateAccum;
        private const float StreamInterval = 1f / 30f;

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

            Roster.Add(new NetPlayer
            {
                clientId = _nm.LocalClientId,
                slot = 0,
                name = Persistence.SettingsStore.Current.player1Name,
                vehicleJson = GameFlow.ActiveDesign != null ? JsonUtility.ToJson(GameFlow.ActiveDesign) : "",
                sceneReady = true,
                assists = SessionConfig.P1Assists(Persistence.SettingsStore.Current),
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
                trackName = GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : "Classic Oval",
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
            cm.RegisterNamedMessageHandler(NetMsg.State, OnState);
            cm.RegisterNamedMessageHandler(NetMsg.Lap, OnLap);
            cm.RegisterNamedMessageHandler(NetMsg.Map, OnMap);
            cm.RegisterNamedMessageHandler(NetMsg.RaceStart, OnRaceStart);
            cm.RegisterNamedMessageHandler(NetMsg.RaceEnd, OnRaceEnd);
            cm.RegisterNamedMessageHandler(NetMsg.SessionState, OnSessionState);
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
                state = (int)State,
                targetLaps = TargetLaps,
                roster = BuildRosterEntries(),
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

            GameFlow.ActiveTrack = string.IsNullOrEmpty(msg.trackJson)
                ? null : JsonUtility.FromJson<TrackDesign>(msg.trackJson);
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
                Roster.Add(new NetPlayer { slot = e.slot, name = e.name, vehicleJson = e.vehicleJson });
                var ps = ToPlayerSlot(new NetPlayer { slot = e.slot, name = e.name, vehicleJson = e.vehicleJson },
                    isLocal: e.slot == LocalSlot);
                SessionConfig.Players.Add(ps);
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

        /// <summary>Invalidate client interpolation buffers (teleports, map loads).</summary>
        public void BumpEpoch() => _epoch++;

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

        private void Update()
        {
            if (State == LanState.Countdown && Time.unscaledTime >= CountdownEndTime)
                SetState(LanState.Racing, broadcast: IsHost);

            if (!IsHost || _nm == null || !_nm.IsListening || _hostRigs == null) return;
            _stateAccum += Time.unscaledDeltaTime;
            if (_stateAccum < StreamInterval) return;
            _stateAccum = 0f;
            BroadcastState();
        }

        private void BroadcastState()
        {
            int n = 0;
            foreach (var rig in _hostRigs) if (rig?.car != null) n++;
            if (n == 0) return;

            using var w = new FastBufferWriter(16 + n * 64, Unity.Collections.Allocator.Temp);
            NetPack.WriteStateHeader(w, _epoch, Time.unscaledTime, (byte)n);
            foreach (var rig in _hostRigs)
            {
                if (rig?.car == null) continue;
                var body = rig.car.GetComponent<Rigidbody>();
                float wheelSpeed = rig.car.ForwardSpeed /
                    Mathf.Max(0.05f, rig.slot.design?.wheels is { Count: > 0 } ws ? ws[0].radius : 0.3f);
                NetPack.WriteCarState(w, new CarState
                {
                    slot = SlotOfRig(rig),
                    pos = body.position,
                    rot = body.rotation,
                    vel = body.linearVelocity,
                    steerDeg = rig.car.CurrentSteerAngle,
                    wheelRadPerSec = wheelSpeed,
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
                });
        }

        private void OnSessionState(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<SessionStateMsg>(reader);
            State = (LanState)m.state;
            TargetLaps = m.targetLaps;
            if (State == LanState.Countdown)
                CountdownEndTime = Time.unscaledTime + m.countdownRemaining;
        }

        // ---- map / race control (full flows wired in the session-control step) ---

        private void OnMap(ulong sender, FastBufferReader reader)
        {
            if (IsHost) return;
            var m = ReadJson<MapMsg>(reader);
            GameFlow.ActiveTrack = string.IsNullOrEmpty(m.trackJson)
                ? null : JsonUtility.FromJson<TrackDesign>(m.trackJson);
            GameFlow.LoadTrack(); // scene rebuild sends aihw.ready again
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

        /// <summary>Host: teleport everyone to the grid and start the countdown.</summary>
        public void HostStartRace(int laps)
        {
            if (!IsHost || GridProvider == null) return;

            var poses = GridProvider(); // scene layer teleports host rigs + resets laps
            BumpEpoch();

            _raceEntries.Clear();
            foreach (var p in Roster) _raceEntries.Add(p.slot);
            _nextPlace = 1;
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
            GameFlow.ActiveTrack = design;
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
