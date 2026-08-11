using System;
using System.Text;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// The wire contract between the game and an external control application
    /// (the WPF GUI/scripting tool) over Windows named pipes.
    ///
    /// <b>Two pipes, two formats</b>, which is the split
    /// <c>Net/NetMessages.cs</c> already documents for LAN: low-rate control
    /// messages are <c>JsonUtility</c> payloads — versionable, greppable,
    /// readable in a log — and the high-rate streams are hand-packed binary.
    /// Here that means a duplex CONTROL pipe carrying newline-delimited JSON
    /// requests and replies, and a server→client TELEMETRY pipe carrying
    /// fixed-layout frames. A subscription at 100 Hz across several vehicles is
    /// the reason the second pipe exists: parsing that as JSON would cost more
    /// garbage per second than the simulation it is reporting on.
    ///
    /// <b>APPEND-ONLY.</b> Message-type strings, error codes and frame layouts
    /// may be added but never renumbered, renamed or reordered — an external
    /// application ships separately from the game and the two will not always
    /// be rebuilt together. <see cref="ProtocolVersion"/> is checked for exact
    /// equality at handshake (the rule <c>NetSession.ProtocolVersion</c> uses),
    /// so anything that would change the meaning of an existing field is a
    /// version bump plus an entry in the changelog below.
    ///
    /// Changelog:
    ///   v1 — initial: handshake, enumeration, per-vehicle takeover, telemetry
    ///        and sensor-camera subscriptions, tuning/physics/settings control,
    ///        session lifecycle, design push with rebuild refusals.
    ///   v1 (2026-08 additive) — world sensors: enumeration + a world telemetry
    ///        stream on FrameTelemetry frames under the <see cref="WorldStreamId"/>
    ///        sentinel vehicleId. No version bump: append-only, no existing
    ///        field changed meaning.
    ///
    /// The human-readable spec the external app is written against lives in
    /// <c>Docs/ipc-protocol.md</c> and is kept in lockstep with this file by
    /// the [IPC] validator.
    /// </summary>
    public static class IpcProtocol
    {
        public const int ProtocolVersion = 1;

        /// <summary>
        /// Pipe names, as passed to <c>NamedPipeServerStream</c> — i.e. without
        /// the <c>\\.\pipe\</c> prefix the client side needs. Not
        /// user-configurable: one game process serves one control app, and a
        /// second instance is refused rather than silently opening a pipe
        /// nobody is looking for (see <c>ErrBusy</c>).
        /// </summary>
        public const string ControlPipeName = "TinyTorque.Control";
        public const string TelemetryPipeName = "TinyTorque.Telemetry";

        /// <summary>UTF-8 without a BOM, and no CR. Every control message is one
        /// line: the reader splits on '\n' alone, so a client that writes CRLF
        /// leaves a stray CR on the end of its JSON — which JsonUtility then
        /// rejects. Stated here because it is the first thing a new client gets
        /// wrong.</summary>
        public static readonly Encoding Utf8 = new UTF8Encoding(false);

        // ---- control messages: client → server -------------------------------

        public const string MsgHello = "hello";
        public const string MsgListVehicles = "list_vehicles";
        public const string MsgListTracks = "list_tracks";
        public const string MsgListPresets = "list_presets";
        public const string MsgListChannels = "list_channels";
        public const string MsgGetSession = "get_session";
        public const string MsgGetTunables = "get_tunables";
        public const string MsgGetSettings = "get_settings";

        public const string MsgAcquire = "acquire";
        public const string MsgRelease = "release";
        public const string MsgDrive = "drive";
        public const string MsgActuate = "actuate";
        public const string MsgResetVehicle = "reset_vehicle";
        public const string MsgTeleport = "teleport";
        public const string MsgSetMode = "set_mode";

        public const string MsgSubscribe = "subscribe";
        public const string MsgUnsubscribe = "unsubscribe";
        public const string MsgSubscribeCamera = "subscribe_camera";
        public const string MsgUnsubscribeCamera = "unsubscribe_camera";
        public const string MsgListWorldSensors = "list_world_sensors";
        public const string MsgSubscribeWorld = "subscribe_world";
        public const string MsgUnsubscribeWorld = "unsubscribe_world";

        public const string MsgSetTunable = "set_tunable";
        public const string MsgSetAssists = "set_assists";
        public const string MsgSetSessionConfig = "set_session_config";
        public const string MsgSetModeTuning = "set_mode_tuning";
        public const string MsgSetArcadeTuning = "set_arcade_tuning";
        public const string MsgSetSolver = "set_solver";
        public const string MsgSetRates = "set_rates";
        public const string MsgSetSettings = "set_settings";

        public const string MsgPushDesign = "push_design";
        public const string MsgLoadTrack = "load_track";
        public const string MsgEndSession = "end_session";
        public const string MsgRestartRun = "restart_run";
        public const string MsgSpawnVehicle = "spawn_vehicle";
        public const string MsgDespawnVehicle = "despawn_vehicle";

        // ---- control messages: server → client -------------------------------

        public const string MsgWelcome = "welcome";
        public const string MsgAck = "ack";
        public const string MsgErr = "err";
        public const string MsgVehicles = "vehicles";
        public const string MsgTracks = "tracks";
        public const string MsgPresets = "presets";
        public const string MsgChannels = "channels";
        public const string MsgSession = "session";
        public const string MsgTunables = "tunables";
        public const string MsgSettings = "settings";
        public const string MsgSubscribed = "subscribed";
        public const string MsgWorldSensors = "world_sensors";
        /// <summary>Unsolicited. <c>id</c> is 0 — nobody asked. Carries a
        /// <c>kind</c> from the Evt* constants below.</summary>
        public const string MsgEvent = "event";

        // ---- event kinds -----------------------------------------------------

        public const string EvtSessionChanged = "session_changed";
        public const string EvtVehiclesChanged = "vehicles_changed";

        // ---- error codes -----------------------------------------------------
        //
        // Machine-readable and stable; the human string beside them is not. A
        // client switches on the code and shows the message.

        public const string ErrVersionMismatch = "version_mismatch";
        public const string ErrNotHandshaken = "not_handshaken";
        public const string ErrBadJson = "bad_json";
        public const string ErrUnknownMessage = "unknown_message";
        public const string ErrBusy = "busy";
        public const string ErrNoSession = "no_session";
        public const string ErrNoVehicle = "no_vehicle";
        public const string ErrNotAcquired = "not_acquired";
        public const string ErrAlreadyAcquired = "already_acquired";
        public const string ErrWrongLevel = "wrong_level";
        public const string ErrLanSession = "lan_session";
        public const string ErrRebuildRefused = "rebuild_refused";
        public const string ErrBadArgument = "bad_argument";
        public const string ErrUnknownChannel = "unknown_channel";
        public const string ErrNotSupported = "not_supported";
        public const string ErrInternal = "internal";
        public const string ErrNoWorldHub = "no_world_hub";

        // ---- takeover levels -------------------------------------------------

        /// <summary>Normalized driver input — throttle/steer/brake as a human or
        /// bot supplies them, through <c>CarInput</c>, so assists, arcade
        /// handling and the steering-rate limit all still apply.</summary>
        public const string LevelDrive = "drive";
        /// <summary>The raw actuator vector — motor volts, steer, brake — written
        /// straight into the runner, which is what a firmware-style control loop
        /// wants. Bypasses <c>CarInput</c> entirely, so nothing shapes it.</summary>
        public const string LevelRaw = "raw";

        // ---- binary telemetry framing ----------------------------------------

        /// <summary>'T''T' little-endian. A frame that does not start with this
        /// means the stream has desynchronised, and the only honest response is
        /// to drop the connection — there is no resynchronisation scheme here
        /// because a named pipe in message-blind byte mode either delivers every
        /// byte in order or faults.</summary>
        public const ushort FrameMagic = 0x5454;

        public const byte FrameTelemetry = 1;
        public const byte FrameCamera = 2;

        /// <summary>Sentinel vehicleId on <see cref="FrameTelemetry"/> frames
        /// carrying the WORLD sensor stream (speakers/mics/beacons). Payload is
        /// byte-identical to vehicle telemetry: f32 simTime then one f32 per
        /// subscribed channel in acked order. Outside the valid vehicle-id
        /// range on purpose — real ids are small positive integers.</summary>
        public const ushort WorldStreamId = 0xFFFF;

        /// <summary>magic u16, type u8, vehicleId u16, seq u32, payloadLen u32.</summary>
        public const int FrameHeaderBytes = 13;

        /// <summary>Grayscale, one byte per pixel, row-major, row 0 at the TOP —
        /// which is <c>CameraSensor.Pixels</c> verbatim, not flipped on the way
        /// out. A client that draws it bottom-up gets an upside-down image and no
        /// error.</summary>
        public const byte CamFormatGray8 = 0;

        /// <summary>
        /// Write a frame header into <paramref name="buf"/> and return the offset
        /// just past it. Little-endian by hand rather than through BitConverter:
        /// this layout is read by a separate application, and "whatever this CPU
        /// does" is not a specification.
        /// </summary>
        public static int WriteHeader(byte[] buf, byte frameType, ushort vehicleId,
                                      uint seq, uint payloadLen)
        {
            int o = 0;
            buf[o++] = (byte)(FrameMagic & 0xFF);
            buf[o++] = (byte)(FrameMagic >> 8);
            buf[o++] = frameType;
            o += WriteU16(buf, o, vehicleId);
            o += WriteU32(buf, o, seq);
            o += WriteU32(buf, o, payloadLen);
            return o;
        }

        public static int WriteU16(byte[] buf, int o, ushort v)
        {
            buf[o] = (byte)(v & 0xFF);
            buf[o + 1] = (byte)(v >> 8);
            return 2;
        }

        public static int WriteU32(byte[] buf, int o, uint v)
        {
            buf[o] = (byte)(v & 0xFF);
            buf[o + 1] = (byte)((v >> 8) & 0xFF);
            buf[o + 2] = (byte)((v >> 16) & 0xFF);
            buf[o + 3] = (byte)((v >> 24) & 0xFF);
            return 4;
        }

        /// <summary>IEEE-754 single, little-endian. <c>BitConverter.SingleToInt32Bits</c>
        /// is not in .NET Standard 2.1's surface everywhere Unity ships it, so the
        /// conversion goes through GetBytes and is then written by hand — the
        /// byte ORDER is what has to be pinned, not the bit pattern.</summary>
        public static int WriteF32(byte[] buf, int o, float v)
        {
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian)
            {
                buf[o] = b[0]; buf[o + 1] = b[1]; buf[o + 2] = b[2]; buf[o + 3] = b[3];
            }
            else
            {
                buf[o] = b[3]; buf[o + 1] = b[2]; buf[o + 2] = b[1]; buf[o + 3] = b[0];
            }
            return 4;
        }

        public static ushort ReadU16(byte[] buf, int o) =>
            (ushort)(buf[o] | (buf[o + 1] << 8));

        public static uint ReadU32(byte[] buf, int o) =>
            (uint)(buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16) | (buf[o + 3] << 24));

        public static float ReadF32(byte[] buf, int o)
        {
            if (BitConverter.IsLittleEndian) return BitConverter.ToSingle(buf, o);
            var b = new byte[4] { buf[o + 3], buf[o + 2], buf[o + 1], buf[o] };
            return BitConverter.ToSingle(b, 0);
        }
    }
}
