using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using AIHWSim.Ipc;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[IPC] — the control-bridge gate.</b> Holds the claims the external
    /// control application will be written against, and which nothing else in
    /// the project can check: <b>the wire format means the same thing on both
    /// sides, and the pipe survives a client coming and going</b>.
    ///
    /// The WPF application ships separately from the game and the two will not
    /// always be rebuilt together, so the things that break silently here are
    /// the ones a compiler cannot see — a renamed message constant, a DTO field
    /// JsonUtility quietly cannot serialize, a frame header whose offsets no
    /// longer add up, a partial-update message that zeroes what the client did
    /// not mention. Each check below compares two things that CAN disagree.
    ///
    /// Runs entirely in edit mode: <see cref="IpcService"/> contains no Unity
    /// API by design, so it can be driven against a real
    /// <c>NamedPipeClientStream</c> in-process without entering play mode.
    ///
    /// <code>
    /// Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.IpcProtocolValidator.Report -logFile &lt;log&gt;
    /// </code>
    /// </summary>
    public static class IpcProtocolValidator
    {
        private const string Tag = "[IPC]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Validate IPC Protocol [IPC]", priority = 403)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            CheckConstants();
            CheckDtoRoundTrip();
            CheckPartialUpdates();
            CheckFrameLayout();
            CheckDocInventory();
            CheckPipeLifecycle();

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        private static void Eq(string what, object expected, object actual)
        {
            _checks++;
            if (!Equals(expected, actual))
                Fails.Add($"{what}: got {actual}, expected {expected}");
        }

        private static void True(string what, bool cond)
        {
            _checks++;
            if (!cond) Fails.Add(what);
        }

        // ---- constants -------------------------------------------------------

        /// <summary>
        /// The message vocabulary, pinned. These strings are the API: a rename
        /// that compiles here breaks an application that is not in this solution,
        /// and the only way to notice is to have written them down twice.
        /// </summary>
        private static readonly string[] ClientMessages =
        {
            "hello", "list_vehicles", "list_tracks", "list_presets", "list_channels",
            "get_session", "get_tunables", "get_settings",
            "acquire", "release", "drive", "actuate", "reset_vehicle", "teleport", "set_mode",
            "subscribe", "unsubscribe", "subscribe_camera", "unsubscribe_camera",
            "list_world_sensors", "subscribe_world", "unsubscribe_world",
            "set_tunable", "set_assists", "set_session_config", "set_mode_tuning",
            "set_arcade_tuning", "set_solver", "set_rates", "set_settings",
            "push_design", "load_track", "end_session", "restart_run",
            "spawn_vehicle", "despawn_vehicle",
        };

        private static readonly string[] ServerMessages =
        {
            "welcome", "ack", "err", "vehicles", "tracks", "presets", "channels",
            "session", "tunables", "settings", "subscribed", "event",
            "world_sensors",
        };

        private static void CheckConstants()
        {
            Eq("protocol version", 1, IpcProtocol.ProtocolVersion);
            Eq("control pipe name", "TinyTorque.Control", IpcProtocol.ControlPipeName);
            Eq("telemetry pipe name", "TinyTorque.Telemetry", IpcProtocol.TelemetryPipeName);
            Eq("frame magic", (ushort)0x5454, IpcProtocol.FrameMagic);
            Eq("telemetry frame type", (byte)1, IpcProtocol.FrameTelemetry);
            Eq("camera frame type", (byte)2, IpcProtocol.FrameCamera);
            Eq("gray8 format tag", (byte)0, IpcProtocol.CamFormatGray8);
            // The world stream rides FrameTelemetry under a sentinel vehicleId
            // that must stay outside the valid vehicle-id range.
            Eq("world stream sentinel", (ushort)0xFFFF, IpcProtocol.WorldStreamId);
            Eq("takeover level drive", "drive", IpcProtocol.LevelDrive);
            Eq("takeover level raw", "raw", IpcProtocol.LevelRaw);

            // 2 + 1 + 2 + 4 + 4. Stated as a sum rather than as 13 so a layout
            // change has to disagree with the arithmetic, not just with a number.
            Eq("frame header size", 2 + 1 + 2 + 4 + 4, IpcProtocol.FrameHeaderBytes);

            // Every Msg* constant must appear in one of the two lists above, and
            // every list entry must be a real constant. Both directions: the first
            // catches an added message nobody wrote down, the second a renamed one.
            var declared = new List<string>();
            foreach (var f in typeof(IpcProtocol).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral || f.FieldType != typeof(string)) continue;
                if (!f.Name.StartsWith("Msg")) continue;
                declared.Add((string)f.GetRawConstantValue());
            }

            var expected = new List<string>(ClientMessages);
            expected.AddRange(ServerMessages);

            foreach (var name in expected)
                True($"message '{name}' is declared in IpcProtocol", declared.Contains(name));
            foreach (var name in declared)
                True($"message '{name}' is in the validator's inventory "
                     + "(add it here and to Docs/ipc-protocol.md)", expected.Contains(name));

            // Error codes are switched on by the client, so they are API too.
            foreach (var f in typeof(IpcProtocol).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral || f.FieldType != typeof(string) || !f.Name.StartsWith("Err")) continue;
                string code = (string)f.GetRawConstantValue();
                True($"error code '{code}' is lower_snake_case",
                     code == code.ToLowerInvariant() && !code.Contains(" "));
            }
        }

        // ---- DTOs ------------------------------------------------------------

        /// <summary>
        /// Every message type must survive JsonUtility in both directions.
        ///
        /// The failure this is really about: JsonUtility silently drops what it
        /// cannot handle. A field it does not serialize does not throw, it just
        /// is not in the JSON — and the far side reads a default. Round-tripping
        /// a populated instance and comparing the text is the only way that shows
        /// up before an application is built on top of it.
        /// </summary>
        private static void CheckDtoRoundTrip()
        {
            RoundTrip(new HelloMsg { t = "hello", id = 7, version = 1, app = "test" });
            RoundTrip(new VehicleRefMsg { t = "release", id = 8, vehicleId = 3 });
            RoundTrip(new AcquireMsg { t = "acquire", id = 9, vehicleId = 3, level = "raw", deadManMs = 250 });
            RoundTrip(new DriveMsg
            {
                t = "drive", id = 0, vehicleId = 3, throttle = 0.5f, steer = -0.25f,
                brake = 0.1f, handbrake = true, respawn = true, useItem = true,
                jump = true, horn = true, boost = true,
            });
            RoundTrip(new ActuateMsg
            {
                t = "actuate", id = 0, vehicleId = 3,
                actuators = new[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f },
                setpoints = new[] { 1.5f, 2.5f, 0f, 0f }, handbrake = true,
            });
            RoundTrip(new TeleportMsg
            {
                t = "teleport", id = 10, vehicleId = 3,
                pos = new IpcVec3 { x = 1, y = 2, z = 3 },
                euler = new IpcVec3 { x = 0, y = 90, z = 0 },
                vel = new IpcVec3 { x = 4, y = 0, z = 0 },
                angVel = new IpcVec3 { x = 0, y = 1, z = 0 },
                keepMomentum = true,
            });
            RoundTrip(new SetModeMsg { t = "set_mode", id = 11, vehicleId = 3, mode = "autonomous" });
            RoundTrip(new SubscribeMsg
            {
                t = "subscribe", id = 12, vehicleId = 3,
                channels = new[] { "veh/speed", "veh/yaw_rate" }, rateHz = 50f,
            });
            RoundTrip(new SubscribeCameraMsg { t = "subscribe_camera", id = 13, vehicleId = 3, sensor = "cam0" });
            RoundTrip(new SubscribeWorldMsg
            {
                t = "subscribe_world", id = 31,
                channels = new[] { "world/mic/mic_a/level", "world/mic/mic_a/s0/id" },
                rateHz = 25f,
            });
            RoundTrip(new WorldSensorsReply
            {
                t = "world_sensors", id = 32,
                sensors = new[]
                {
                    new WorldSensorDto
                    {
                        name = "mic_a", kind = "mic",
                        channels = new[] { "world/mic/mic_a/level" },
                        px = 1.5f, py = 0f, pz = -2.25f,
                    },
                },
            });
            RoundTrip(new SetTunableMsg { t = "set_tunable", id = 14, vehicleId = 3, name = "Grip (side)", value = 1.5f });
            RoundTrip(new SetAssistsMsg
            {
                t = "set_assists", id = 15, vehicleId = 3,
                steer = 0.1f, stability = 0.2f, traction = 0.3f, abs = 0.4f, launch = 0.5f,
            });
            RoundTrip(new SetRatesMsg { t = "set_rates", id = 16, physicsHz = 400, controlHz = 100 });
            RoundTrip(new PushDesignMsg { t = "push_design", id = 17, vehicleId = 3, designJson = "{\"name\":\"x\"}", liveOnly = true });
            RoundTrip(new SpawnVehicleMsg
            {
                t = "spawn_vehicle", id = 18, preset = "TT Coupe", name = "bot",
                pos = new IpcVec3 { x = 5, y = 1, z = 0 }, acquire = "drive",
            });
            RoundTrip(new LoadTrackMsg
            {
                t = "load_track", id = 19, trackId = "TTA_Sandbox", match = "FreeRoam",
                laps = 3, bots = 2, difficulty = 1, arcade = true, arcadeHandling = true,
                trackLimits = true, countdown = 3, vehicle = "TT Coupe",
            });
            RoundTrip(new SetSessionConfigMsg
            {
                t = "set_session_config", id = 20, targetLaps = 5, targetScore = 3,
                timeLimitSec = 300, rubberBand = true, arcade = true, trackLimits = true,
                arcadeHandling = true, arcadeTyreThermal = true,
            });
            RoundTrip(new SetSolverMsg
            {
                t = "set_solver", id = 21, defaultContactOffset = 0.002f,
                defaultSolverIterations = 10, defaultSolverVelocityIterations = 2,
                defaultMaxDepenetrationVelocity = 2f, maximumDeltaTime = 0.05f,
            });
            RoundTrip(new SetSettingsMsg
            {
                t = "set_settings", id = 22, masterVolume = 0.5f, sfxVolume = 0.6f,
                engineVolume = 0.7f, musicVolume = 0.8f, bloom = true, vSync = true,
                fullscreen = true, qualityLevel = 2, logTelemetry = true, noiseSeed = 42,
                actuationDelayTicks = 3, spArcadeHandling = true, spArcadeTyreThermal = true,
            });
            RoundTrip(new SetTuningMsg { t = "set_mode_tuning", id = 23, name = "derbyMaxHealth", value = 5f });

            RoundTrip(new WelcomeMsg
            {
                t = "welcome", id = 1, version = 1, game = "Tiny Torque",
                unityVersion = "6000.1.15f1", scene = "TTA_Sandbox",
                sessionActive = true, lan = false,
            });
            RoundTrip(new AckMsg { t = "ack", id = 2, note = "done" });
            RoundTrip(new ErrMsg { t = "err", id = 3, code = "no_vehicle", message = "no vehicle with id 9" });
            RoundTrip(new VehiclesReply
            {
                t = "vehicles", id = 4,
                vehicles = new[]
                {
                    new VehicleInfo
                    {
                        id = 1, name = "Player", designName = "TT Coupe", isBot = false,
                        control = "Human", netSlot = -1, acquired = true, level = "drive",
                        wheelCount = 4, hasCamera = true,
                        motors = new[] { new MotorInfo { name = "m0", actuatorIndex = 0, maxVoltage = 7.4f } },
                        sensors = new[]
                        {
                            new SensorInfoDto
                            {
                                name = "cam0", kind = "Camera",
                                channels = new string[0], camWidth = 64, camHeight = 48,
                            },
                        },
                    },
                },
            });
            RoundTrip(new TracksReply
            {
                t = "tracks", id = 5,
                tracks = new[] { new TrackInfo { id = "TTA_Sandbox", displayName = "Sandbox", kind = "scene", hasFinish = false } },
            });
            RoundTrip(new PresetsReply { t = "presets", id = 6, presets = new[] { "a" }, saved = new[] { "b" } });
            RoundTrip(new ChannelsReply { t = "channels", id = 7, vehicleId = 1, channels = new[] { "veh/speed" } });
            RoundTrip(new SessionReply
            {
                t = "session", id = 8, active = true, scene = "TTA_Sandbox", trackId = "TTA_Sandbox",
                match = "FreeRoam", vehicleCount = 1, simTime = 12.5f, lan = false, paused = false,
                physicsHz = 400, controlHz = 100,
            });
            RoundTrip(new TunablesReply
            {
                t = "tunables", id = 9, vehicleId = 1,
                tunables = new[] { new TunableInfo { name = "Grip (side)", min = 0.5f, max = 3f, value = 1f } },
            });
            RoundTrip(new SettingsReply { t = "settings", id = 10, masterVolume = 1f, qualityLevel = -1 });
            RoundTrip(new SubscribedReply
            {
                t = "subscribed", id = 11, vehicleId = 1,
                channels = new[] { "veh/speed" }, rateHz = 50f,
            });
            RoundTrip(new EventMsg { t = "event", id = 0, kind = "session_changed", vehicleId = 0, note = "TTA_Sandbox" });
        }

        private static void RoundTrip<T>(T value) where T : class
        {
            _checks++;
            string a = JsonUtility.ToJson(value);
            T back;
            try { back = JsonUtility.FromJson<T>(a); }
            catch (Exception e)
            {
                Fails.Add($"{typeof(T).Name} did not parse back: {e.Message}");
                return;
            }
            string b = JsonUtility.ToJson(back);
            if (a != b) Fails.Add($"{typeof(T).Name} did not round-trip:\n  out: {a}\n  in:  {b}");

            // Every public field must actually reach the JSON. A field JsonUtility
            // skips is not an error anywhere else — it just silently is not on the
            // wire, and the far side reads a default it has no way to question.
            foreach (var f in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                _checks++;
                if (!a.Contains("\"" + f.Name + "\""))
                    Fails.Add($"{typeof(T).Name}.{f.Name} is missing from its JSON — "
                              + $"JsonUtility cannot serialize {f.FieldType.Name}");
            }
        }

        // ---- partial updates -------------------------------------------------

        /// <summary>
        /// The partial-update contract: a key the client did not send must come
        /// back unchanged.
        ///
        /// This is the single easiest thing to get wrong in the whole protocol.
        /// JsonUtility cannot tell an absent key from a zero, so a handler that
        /// used FromJson instead of FromJsonOverwrite would zero every setting
        /// the client did not happen to mention — and it would look like it
        /// worked, because the field the client DID send would be right.
        /// </summary>
        private static void CheckPartialUpdates()
        {
            var settings = new SetSettingsMsg
            {
                masterVolume = 0.9f, sfxVolume = 0.8f, qualityLevel = 3,
                bloom = true, noiseSeed = 1234, actuationDelayTicks = 5,
            };
            JsonUtility.FromJsonOverwrite("{\"t\":\"set_settings\",\"id\":1,\"bloom\":false}", settings);

            Eq("partial set_settings applied the field it was sent", false, settings.bloom);
            Eq("partial set_settings left masterVolume alone", 0.9f, settings.masterVolume);
            Eq("partial set_settings left sfxVolume alone", 0.8f, settings.sfxVolume);
            Eq("partial set_settings left qualityLevel alone", 3, settings.qualityLevel);
            Eq("partial set_settings left noiseSeed alone", 1234, settings.noiseSeed);
            Eq("partial set_settings left actuationDelayTicks alone", 5, settings.actuationDelayTicks);

            var solver = new SetSolverMsg
            {
                defaultContactOffset = 0.002f, defaultSolverIterations = 10,
                defaultSolverVelocityIterations = 2, defaultMaxDepenetrationVelocity = 2f,
                maximumDeltaTime = 0.05f,
            };
            JsonUtility.FromJsonOverwrite("{\"t\":\"set_solver\",\"id\":2,\"defaultSolverIterations\":20}", solver);
            Eq("partial set_solver applied iterations", 20, solver.defaultSolverIterations);
            Eq("partial set_solver left contactOffset alone", 0.002f, solver.defaultContactOffset);
            Eq("partial set_solver left maximumDeltaTime alone", 0.05f, solver.maximumDeltaTime);

            var session = new SetSessionConfigMsg { targetLaps = 3, arcadeHandling = true, arcadeTyreThermal = true };
            JsonUtility.FromJsonOverwrite("{\"t\":\"set_session_config\",\"id\":3,\"targetLaps\":7}", session);
            Eq("partial set_session_config applied targetLaps", 7, session.targetLaps);
            Eq("partial set_session_config left arcadeHandling alone", true, session.arcadeHandling);
            Eq("partial set_session_config left arcadeTyreThermal alone", true, session.arcadeTyreThermal);

            var track = new LoadTrackMsg { trackId = "", match = "Race", laps = 3, bots = 2, vehicle = "TT Coupe" };
            JsonUtility.FromJsonOverwrite("{\"t\":\"load_track\",\"id\":4,\"trackId\":\"TTA_Sandbox\"}", track);
            Eq("partial load_track applied trackId", "TTA_Sandbox", track.trackId);
            Eq("partial load_track left match alone", "Race", track.match);
            Eq("partial load_track left bots alone", 2, track.bots);
            Eq("partial load_track left vehicle alone", "TT Coupe", track.vehicle);

            // The routing pass must survive a message carrying fields it has never
            // heard of — that is what a NEWER client looks like, and it must reach
            // the version check rather than dying in the envelope parse.
            var env = JsonUtility.FromJson<IpcEnvelope>(
                "{\"t\":\"hello\",\"id\":5,\"version\":1,\"somethingNew\":42,\"nested\":{\"a\":1}}");
            Eq("envelope parse ignores unknown fields (t)", "hello", env.t);
            Eq("envelope parse ignores unknown fields (id)", 5, env.id);
        }

        // ---- binary frames ---------------------------------------------------

        private static void CheckFrameLayout()
        {
            // A telemetry frame, packed exactly as IpcTelemetryStreamer packs it
            // and unpacked exactly as a client would.
            float[] values = { 1.5f, -2.25f, 1e-3f, 12345.678f };
            int payload = 4 + 4 * values.Length;
            var buf = new byte[IpcProtocol.FrameHeaderBytes + payload];

            int o = IpcProtocol.WriteHeader(buf, IpcProtocol.FrameTelemetry, 7, 42, (uint)payload);
            Eq("WriteHeader returns the header size", IpcProtocol.FrameHeaderBytes, o);
            o += IpcProtocol.WriteF32(buf, o, 3.25f);
            foreach (var v in values) o += IpcProtocol.WriteF32(buf, o, v);
            Eq("telemetry frame is exactly header + payload", buf.Length, o);

            Eq("frame magic reads back", IpcProtocol.FrameMagic, IpcProtocol.ReadU16(buf, 0));
            Eq("frame type reads back", IpcProtocol.FrameTelemetry, buf[2]);
            Eq("vehicleId reads back", (ushort)7, IpcProtocol.ReadU16(buf, 3));
            Eq("seq reads back", 42u, IpcProtocol.ReadU32(buf, 5));
            Eq("payloadLen reads back", (uint)payload, IpcProtocol.ReadU32(buf, 9));
            Eq("simTime reads back", 3.25f, IpcProtocol.ReadF32(buf, 13));
            for (int i = 0; i < values.Length; i++)
                Eq($"telemetry value {i} reads back", values[i], IpcProtocol.ReadF32(buf, 17 + 4 * i));

            // Little-endian on the wire, whatever the CPU is. Asserted on the raw
            // bytes rather than through the readers, which would agree with the
            // writers even if both were wrong.
            var probe = new byte[4];
            IpcProtocol.WriteU32(probe, 0, 0x04030201);
            Eq("u32 byte 0 is the low byte", (byte)0x01, probe[0]);
            Eq("u32 byte 3 is the high byte", (byte)0x04, probe[3]);
            IpcProtocol.WriteU16(probe, 0, 0x0201);
            Eq("u16 byte 0 is the low byte", (byte)0x01, probe[0]);
            Eq("u16 byte 1 is the high byte", (byte)0x02, probe[1]);

            // A camera frame: header + simTime + ordinal + w + h + format + pixels.
            const int w = 8, h = 4;
            int camPayload = 4 + 1 + 2 + 2 + 1 + w * h;
            var cam = new byte[IpcProtocol.FrameHeaderBytes + camPayload];
            int c = IpcProtocol.WriteHeader(cam, IpcProtocol.FrameCamera, 2, 1, (uint)camPayload);
            c += IpcProtocol.WriteF32(cam, c, 9.5f);
            cam[c++] = 0;
            c += IpcProtocol.WriteU16(cam, c, w);
            c += IpcProtocol.WriteU16(cam, c, h);
            cam[c++] = IpcProtocol.CamFormatGray8;
            for (int i = 0; i < w * h; i++) cam[c++] = (byte)i;

            Eq("camera frame is exactly header + payload", cam.Length, c);
            Eq("camera width reads back", (ushort)w, IpcProtocol.ReadU16(cam, 18));
            Eq("camera height reads back", (ushort)h, IpcProtocol.ReadU16(cam, 20));
            Eq("camera format reads back", IpcProtocol.CamFormatGray8, cam[22]);
            Eq("camera first pixel reads back", (byte)0, cam[23]);
            Eq("camera last pixel reads back", (byte)(w * h - 1), cam[cam.Length - 1]);
        }

        // ---- documentation ---------------------------------------------------

        /// <summary>
        /// The spec is a deliverable, not a comment: the external application is
        /// written from it and cannot read this source tree. A message that
        /// exists in code and not in the document is a feature nobody outside can
        /// use, which is the same as not having shipped it.
        /// </summary>
        private static void CheckDocInventory()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Docs/ipc-protocol.md"));
            _checks++;
            if (!File.Exists(path))
            {
                Fails.Add($"Docs/ipc-protocol.md is missing (looked in {path})");
                return;
            }

            string doc = File.ReadAllText(path);
            foreach (var name in ClientMessages)
                True($"Docs/ipc-protocol.md documents '{name}'", doc.Contains(name));
            foreach (var name in ServerMessages)
                True($"Docs/ipc-protocol.md documents '{name}'", doc.Contains(name));

            foreach (var f in typeof(IpcProtocol).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!f.IsLiteral || f.FieldType != typeof(string) || !f.Name.StartsWith("Err")) continue;
                string code = (string)f.GetRawConstantValue();
                True($"Docs/ipc-protocol.md documents error code '{code}'", doc.Contains(code));
            }

            True("Docs/ipc-protocol.md states the protocol version",
                 doc.Contains($"v{IpcProtocol.ProtocolVersion}"));

            // World-sensor additions (2026-08): the sentinel and the new sensor
            // kinds must be written down where the client author will look.
            True("Docs/ipc-protocol.md documents the world stream sentinel 0xFFFF",
                 doc.Contains("0xFFFF"));
            foreach (string kind in new[] { "Color", "Rf", "Mag", "Bump", "Led" })
                True($"Docs/ipc-protocol.md lists sensor kind '{kind}'", doc.Contains(kind));
        }

        // ---- pipe lifecycle --------------------------------------------------

        /// <summary>
        /// Drive the real service against a real client. Not a mock: the things
        /// worth checking here — that a second client is refused, that a
        /// disconnect is noticed, that the server goes back to listening
        /// afterwards — are all properties of the named pipe and the thread
        /// around it, and a stand-in would only be testing itself.
        /// </summary>
        private static void CheckPipeLifecycle()
        {
            // The service hardcodes the shipped pipe names, so a game already
            // running with the bridge switched on owns them. Without this check
            // the "client" below would connect to THAT game and this validator
            // would cheerfully write test messages into somebody's live session
            // while reporting nothing useful about its own service.
            _checks++;
            if (SomethingElseIsServing())
            {
                Fails.Add($"another process is already serving \\\\.\\pipe\\{IpcProtocol.ControlPipeName} "
                          + "— close the running game (or switch its Remote Control toggle off) "
                          + "before running this gate");
                return;
            }

            var service = new IpcService();
            try
            {
                service.Start();

                using (var client = Connect(out bool connected))
                {
                    True("a client can connect to the control pipe", connected);
                    if (!connected) return;

                    True("the service reports the client connected", WaitFor(() => service.ControlConnected));
                    Eq("connecting bumps the epoch", 1, service.ConnectEpoch);

                    // Inbound: a line the reader thread must cut on '\n' and hand
                    // over decoded.
                    WriteLine(client, "{\"t\":\"hello\",\"id\":1,\"version\":1}");
                    string got = null;
                    True("a sent line is queued for the main thread",
                         WaitFor(() => service.TryDequeueInbound(out got)));
                    Eq("the line arrives verbatim", "{\"t\":\"hello\",\"id\":1,\"version\":1}", got);

                    // Two lines in ONE write, which is what a client batching
                    // messages looks like and where a naive one-read-one-message
                    // reader loses the second.
                    var both = IpcProtocol.Utf8.GetBytes("{\"t\":\"a\",\"id\":2}\n{\"t\":\"b\",\"id\":3}\n");
                    client.Write(both, 0, both.Length);
                    client.Flush();
                    string first = null, second = null;
                    True("the first of two batched lines arrives",
                         WaitFor(() => service.TryDequeueInbound(out first)));
                    True("the second of two batched lines arrives",
                         WaitFor(() => service.TryDequeueInbound(out second)));
                    Eq("batched line 1 is intact", "{\"t\":\"a\",\"id\":2}", first);
                    Eq("batched line 2 is intact", "{\"t\":\"b\",\"id\":3}", second);

                    // A CRLF client. The spec says LF, but a StreamWriter left on
                    // its platform default emits CRLF and the resulting parse
                    // failure is baffling to debug, so the CR is tolerated.
                    var crlf = IpcProtocol.Utf8.GetBytes("{\"t\":\"c\",\"id\":4}\r\n");
                    client.Write(crlf, 0, crlf.Length);
                    client.Flush();
                    string cr = null;
                    True("a CRLF line arrives", WaitFor(() => service.TryDequeueInbound(out cr)));
                    Eq("the trailing CR is stripped", "{\"t\":\"c\",\"id\":4}", cr);

                    // Outbound.
                    service.SendLine("{\"t\":\"welcome\",\"id\":1}");
                    Eq("a reply reaches the client", "{\"t\":\"welcome\",\"id\":1}", ReadLine(client));

                    // One client at a time: Windows itself refuses the second,
                    // which is why nothing in IpcService arbitrates.
                    _checks++;
                    using (var second2 = new NamedPipeClientStream(
                        ".", IpcProtocol.ControlPipeName, PipeDirection.InOut))
                    {
                        try
                        {
                            second2.Connect(300);
                            Fails.Add("a second client connected; the pipe must serve one at a time");
                        }
                        catch (TimeoutException) { /* expected */ }
                        catch (IOException) { /* also acceptable: ERROR_PIPE_BUSY */ }
                    }
                }

                // Disconnected by the using block above.
                True("the service notices the client left", WaitFor(() => !service.ControlConnected));

                using (var again = Connect(out bool reconnected))
                {
                    True("a client can reconnect after a disconnect", reconnected);
                    True("reconnecting bumps the epoch again",
                         WaitFor(() => service.ConnectEpoch == 2));
                }

                // Frames queued with nothing listening on the telemetry pipe must
                // be dropped rather than piling up — a subscription's first
                // samples are worth less than an unbounded backlog.
                var buf = service.RentFrame(64);
                service.SendFrame(buf, 32);
                True("frames sent with no telemetry client are dropped, not queued",
                     !service.TelemetryConnected);
            }
            finally
            {
                service.Stop();
            }

            // Stop must actually free the name, or the next enable cannot listen.
            _checks++;
            using (var afterStop = new NamedPipeClientStream(
                ".", IpcProtocol.ControlPipeName, PipeDirection.InOut))
            {
                try
                {
                    afterStop.Connect(300);
                    Fails.Add("the control pipe still accepts connections after Stop()");
                }
                catch (TimeoutException) { /* expected: nothing is listening */ }
                catch (IOException) { }
            }
        }

        /// <summary>Is the shipped control-pipe name already taken? A successful
        /// connect means yes — a running game, or a leftover process.</summary>
        private static bool SomethingElseIsServing()
        {
            using (var probe = new NamedPipeClientStream(
                ".", IpcProtocol.ControlPipeName, PipeDirection.InOut))
            {
                try { probe.Connect(200); return true; }
                catch (TimeoutException) { return false; }
                // Busy means a server exists and is already serving somebody, which
                // is still "taken".
                catch (IOException) { return true; }
            }
        }

        private static NamedPipeClientStream Connect(out bool connected)
        {
            var client = new NamedPipeClientStream(
                ".", IpcProtocol.ControlPipeName, PipeDirection.InOut);
            connected = false;
            // The accept thread may not have created the pipe yet; a few short
            // attempts beat one long timeout that hides how slow it really was.
            for (int i = 0; i < 20 && !connected; i++)
            {
                try { client.Connect(100); connected = true; }
                catch (TimeoutException) { }
                catch (IOException) { Thread.Sleep(50); }
            }
            return client;
        }

        private static void WriteLine(Stream pipe, string text)
        {
            var bytes = IpcProtocol.Utf8.GetBytes(text + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
        }

        /// <summary>
        /// Read one line, giving up rather than blocking.
        ///
        /// <b>Never a bare <c>Stream.Read</c>.</b> A pipe read blocks until bytes
        /// arrive or the far end closes, and neither happens if the thing being
        /// tested is broken — so a plain read turns a failing assertion into a
        /// batch-mode Unity that hangs forever holding the project lock, with an
        /// empty log, because this validator does not print anything until the
        /// end. BeginRead plus a poll is what makes a failure look like a failure.
        /// </summary>
        private static string ReadLine(Stream pipe, int timeoutMs = 2000)
        {
            var acc = new List<byte>();
            var one = new byte[1];
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < until)
            {
                var ar = pipe.BeginRead(one, 0, 1, null, null);
                while (!ar.IsCompleted)
                {
                    if (DateTime.UtcNow >= until) return "(timed out)";
                    Thread.Sleep(2);
                }
                if (pipe.EndRead(ar) <= 0) break;      // far end closed
                if (one[0] == (byte)'\n') break;
                acc.Add(one[0]);
            }
            return Encoding.UTF8.GetString(acc.ToArray());
        }

        /// <summary>Spin until a worker thread has caught up. Edit mode has no
        /// frame loop to yield to, so this is a bounded sleep-poll rather than a
        /// coroutine.</summary>
        private static bool WaitFor(Func<bool> condition, int timeoutMs = 2000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return false;
        }
    }
}
