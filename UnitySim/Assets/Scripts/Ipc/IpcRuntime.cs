using System;
using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// The main-thread half of the control bridge: owns the
    /// <see cref="IpcService"/>, drains its inbound queue every frame, parses
    /// and dispatches messages, and holds the session-facing state (which
    /// vehicles an external app has taken over, what it is subscribed to).
    ///
    /// Everything Unity-adjacent happens here, in <c>Update</c>, on the main
    /// thread — including the JSON parse. That is deliberate: a worker thread
    /// that parsed messages would then be holding objects it is not allowed to
    /// look up, and the parse itself is nothing next to a physics step.
    ///
    /// <b>Off by default.</b> The bridge only exists while
    /// <c>GameSettings.ipcEnabled</c> is set, which is a toggle in Options and
    /// in the in-race Settings panel. A game that nobody has opted in on opens
    /// no pipe and starts no thread. <see cref="EnsureState"/> is the one entry
    /// point for that decision and is safe to call as often as you like.
    ///
    /// Lifetime is the process, not the scene: the object is
    /// <c>DontDestroyOnLoad</c> so a client can hold a connection across
    /// <c>load_track</c>. That is the same idiom <c>NetSession.Create</c> uses.
    /// </summary>
    public sealed partial class IpcRuntime : MonoBehaviour
    {
        private const int MaxMessagesPerFrame = 256;

        private static IpcRuntime _instance;
        public static IpcRuntime Instance => _instance;

        private IpcService _service;
        private IpcVehicleRegistry _registry;
        private IpcTelemetryStreamer _streamer;

        private bool _handshaken;
        private int _seenEpoch;
        private long _reportedDrops;

        /// <summary>The registry is public so the command handlers (a partial of
        /// this class, in another file) can reach it without a locator.</summary>
        internal IpcVehicleRegistry Registry => _registry;
        internal IpcTelemetryStreamer Streamer => _streamer;
        internal IpcService Service => _service;

        // ---- lifecycle -------------------------------------------------------

        /// <summary>
        /// Start or stop the bridge to match the saved setting. Called at boot
        /// and from <c>SettingsStore.Apply()</c>, so flipping the toggle in the
        /// menu takes effect immediately rather than at the next launch.
        /// </summary>
        public static void EnsureState()
        {
            bool want = SettingsStore.Current.ipcEnabled;

            // Batch mode gets no bridge, for the same reason UiRuntime skips
            // itself there: the validators and the Opus regression want a world
            // with nothing extra in it, and a headless run has nobody to connect.
            if (Application.isBatchMode) want = false;

            if (want && _instance == null)
            {
                var go = new GameObject("TinyTorqueIpc");
                DontDestroyOnLoad(go);
                go.AddComponent<IpcRuntime>();
            }
            else if (!want && _instance != null)
            {
                // _instance is cleared by OnDestroy, deliberately not here: that
                // method's first line is "am I the live instance", and clearing
                // the field first makes it answer no — so the pipes and their
                // threads would be left running with nothing owning them.
                Destroy(_instance.gameObject);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureOnBoot() => EnsureState();

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;

            // The whole point of an external control app is that it keeps working
            // while the user is looking at it instead of at the game. Without this
            // the sim freezes the moment the WPF window takes focus, and every
            // command appears to hang. MenuBootstrap sets the same flag for LAN
            // hosting, but only if the menu scene was entered — asserted here so a
            // direct-to-track launch is covered too.
            //
            // Never set back to false on teardown: LAN hosting relies on it as
            // well, and this bridge does not know whether it was the one that
            // turned it on.
            Application.runInBackground = true;

            _registry = new IpcVehicleRegistry();
            _streamer = new IpcTelemetryStreamer(this);

            // Every session change reaches us through the scene load, whoever asked
            // for it — the menu, a championship round, a LAN join, or this bridge's
            // own load_track. Hooking the load rather than the callers is what keeps
            // TrackBootstrap from needing to know the bridge exists.
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;

            _service = new IpcService();
            _service.Start();
            _seenEpoch = _service.ConnectEpoch;

            Debug.Log($"[IPC] listening on \\\\.\\pipe\\{IpcProtocol.ControlPipeName} "
                      + $"and \\\\.\\pipe\\{IpcProtocol.TelemetryPipeName} (protocol v{IpcProtocol.ProtocolVersion})");
        }

        /// <summary>
        /// A scene load invalidates everything the bridge is holding: the rig
        /// list, and every telemetry hub a subscription was attached to. Note the
        /// order — subscriptions are dropped BEFORE the registry is rebuilt,
        /// because a subscription still holding a destroyed hub would otherwise be
        /// re-armed against it.
        ///
        /// Takeovers are not released here on purpose: the cars they referred to
        /// are gone, and <see cref="IpcVehicleRegistry.Refresh"/> drops those
        /// entries wholesale. There is nothing to hand back to.
        /// </summary>
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
                                   UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            _streamer.ClearAll();
            _registry.Invalidate();
            Event(IpcProtocol.EvtSessionChanged, 0, scene.name);
        }

        private void OnDestroy()
        {
            if (_instance != this) return;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            ResetClientState("the bridge was switched off");
            _service?.Stop();
            _service = null;
            _instance = null;
            Debug.Log("[IPC] stopped");
        }

        // ---- frame loop ------------------------------------------------------

        private void Update()
        {
            if (_service == null) return;

            int epoch = _service.ConnectEpoch;
            if (epoch != _seenEpoch)
            {
                // A new client. Whatever the last one was holding is not theirs.
                _seenEpoch = epoch;
                ResetClientState("a new client connected");
                Debug.Log("[IPC] client connected");
            }

            if (!_service.ControlConnected && _handshaken)
            {
                ResetClientState("the client disconnected");
                Debug.Log("[IPC] client disconnected");
            }

            int n = 0;
            while (n++ < MaxMessagesPerFrame && _service.TryDequeueInbound(out var line))
                Dispatch(line);

            _registry.Refresh();
            _streamer.Tick();

            ReportDrops();
        }

        /// <summary>Surface dropped telemetry frames rather than letting a client
        /// silently see gaps. Rate-limited to one line per 500 drops so a client
        /// that cannot keep up does not also flood the log.</summary>
        private void ReportDrops()
        {
            long dropped = _service.FramesDropped;
            if (dropped < _reportedDrops + 500) return;
            _reportedDrops = dropped;
            Debug.LogWarning($"[IPC] telemetry backlog: {dropped} frames dropped so far. "
                             + "The client is not reading fast enough, or is subscribed to "
                             + "more than it can consume — lower rateHz or narrow the channel list.");
        }

        /// <summary>
        /// Hand every taken-over vehicle back to its local input, clear
        /// subscriptions, and require a fresh handshake. Called on connect,
        /// disconnect and teardown — a car left under the control of a process
        /// that is no longer there would sit at whatever its last command was
        /// (or, with the dead-man, brake forever).
        /// </summary>
        private void ResetClientState(string why)
        {
            _handshaken = false;
            _streamer?.ClearAll();
            _registry?.ReleaseAll(why);
        }

        // ---- dispatch --------------------------------------------------------

        private void Dispatch(string line)
        {
            IpcEnvelope env;
            try { env = JsonUtility.FromJson<IpcEnvelope>(line); }
            catch (Exception e) { Err(0, IpcProtocol.ErrBadJson, e.Message); return; }

            if (env == null || string.IsNullOrEmpty(env.t))
            {
                Err(0, IpcProtocol.ErrBadJson, "message has no 't' field");
                return;
            }

            if (env.t == IpcProtocol.MsgHello) { HandleHello(line); return; }

            if (!_handshaken)
            {
                Err(env.id, IpcProtocol.ErrNotHandshaken,
                    $"send '{IpcProtocol.MsgHello}' with version {IpcProtocol.ProtocolVersion} first");
                return;
            }

            try
            {
                if (!Route(env, line))
                    Err(env.id, IpcProtocol.ErrUnknownMessage, $"unknown message type '{env.t}'");
            }
            catch (Exception e)
            {
                // A handler that throws must not take the bridge down with it —
                // the client gets a structured error and the connection survives.
                Err(env.id, IpcProtocol.ErrInternal, e.Message);
                Debug.LogWarning($"[IPC] handler for '{env.t}' threw: {e}");
            }
        }

        private void HandleHello(string line)
        {
            var hello = JsonUtility.FromJson<HelloMsg>(line);
            if (hello.version != IpcProtocol.ProtocolVersion)
            {
                // Exact equality, the rule NetSession uses. A near-miss version is
                // more dangerous than none: the field names still parse and the
                // meanings have moved.
                Err(hello.id, IpcProtocol.ErrVersionMismatch,
                    $"this game speaks protocol v{IpcProtocol.ProtocolVersion}, the client speaks v{hello.version}");
                Debug.LogWarning($"[IPC] refused a client speaking protocol v{hello.version}; "
                                 + $"this build speaks v{IpcProtocol.ProtocolVersion}");
                return;
            }

            _handshaken = true;
            _registry.Refresh();

            var session = IpcSessionInfo.Capture();
            Reply(new WelcomeMsg
            {
                t = IpcProtocol.MsgWelcome,
                id = hello.id,
                version = IpcProtocol.ProtocolVersion,
                game = Application.productName,
                unityVersion = Application.unityVersion,
                scene = session.scene,
                sessionActive = session.active,
                lan = session.lan,
            });
            Debug.Log($"[IPC] handshake ok with '{hello.app}' (protocol v{hello.version})");
        }

        // ---- reply helpers ---------------------------------------------------

        internal void Reply(IpcEnvelope msg) => _service?.SendLine(JsonUtility.ToJson(msg));

        internal void Ack(int id, string note = null) =>
            Reply(new AckMsg { t = IpcProtocol.MsgAck, id = id, note = note });

        internal void Err(int id, string code, string message)
        {
            Reply(new ErrMsg { t = IpcProtocol.MsgErr, id = id, code = code, message = message });
        }

        /// <summary>Unsolicited notification. Id 0 — nobody asked for it.</summary>
        internal void Event(string kind, int vehicleId = 0, string note = null)
        {
            if (!_handshaken) return;
            Reply(new EventMsg
            {
                t = IpcProtocol.MsgEvent, id = 0,
                kind = kind, vehicleId = vehicleId, note = note,
            });
        }

        /// <summary>
        /// Tell a connected client that the vehicle list changed. Called by
        /// <c>TrackBootstrap</c> once its rigs exist and after a spawn or
        /// despawn. A no-op when the bridge is off, which is what lets the call
        /// site be unconditional.
        /// </summary>
        public static void NotifyVehiclesChanged()
        {
            if (_instance == null) return;
            _instance._registry.Invalidate();
            _instance.Event(IpcProtocol.EvtVehiclesChanged);
        }

        /// <summary>As above, for a scene/session change.</summary>
        public static void NotifySessionChanged()
        {
            if (_instance == null) return;
            _instance._registry.Invalidate();
            _instance._streamer.ClearAll();
            _instance.Event(IpcProtocol.EvtSessionChanged);
        }
    }
}
