using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Core.Boot;
using AIHWSim.Core.Config;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// The command surface: one method per message type, all on the main thread,
    /// all reached from <see cref="IpcRuntime.Dispatch"/>.
    ///
    /// A partial of <see cref="IpcRuntime"/> rather than a separate service so
    /// the handlers can use its reply helpers directly; split into its own file
    /// because the transport and the vocabulary are different concerns and this
    /// half is where the vocabulary grows.
    ///
    /// House rule for every handler here: validate, act, reply exactly once.
    /// Anything that throws is caught by the dispatcher and comes back as
    /// <c>err internal</c>, so a handler never has to defend the connection.
    /// </summary>
    public sealed partial class IpcRuntime
    {
        private bool Route(IpcEnvelope env, string line)
        {
            switch (env.t)
            {
                // ---- enumeration ----
                case IpcProtocol.MsgListVehicles: ListVehicles(env.id); return true;
                case IpcProtocol.MsgListTracks: ListTracks(env.id); return true;
                case IpcProtocol.MsgListPresets: ListPresets(env.id); return true;
                case IpcProtocol.MsgListChannels: ListChannels(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgGetSession: GetSession(env.id); return true;
                case IpcProtocol.MsgGetTunables: GetTunables(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgGetSettings: GetSettings(env.id); return true;

                // ---- takeover and control ----
                case IpcProtocol.MsgAcquire: Acquire(Parse<AcquireMsg>(line)); return true;
                case IpcProtocol.MsgRelease: ReleaseVehicle(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgDrive: Drive(Parse<DriveMsg>(line)); return true;
                case IpcProtocol.MsgActuate: Actuate(Parse<ActuateMsg>(line)); return true;
                case IpcProtocol.MsgResetVehicle: ResetVehicle(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgTeleport: Teleport(Parse<TeleportMsg>(line)); return true;
                case IpcProtocol.MsgSetMode: SetDriveMode(Parse<SetModeMsg>(line)); return true;

                // ---- telemetry ----
                case IpcProtocol.MsgSubscribe: Subscribe(Parse<SubscribeMsg>(line)); return true;
                case IpcProtocol.MsgUnsubscribe: Unsubscribe(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgSubscribeCamera: SubscribeCamera(Parse<SubscribeCameraMsg>(line)); return true;
                case IpcProtocol.MsgUnsubscribeCamera: UnsubscribeCamera(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgListWorldSensors: ListWorldSensors(env.id); return true;
                case IpcProtocol.MsgSubscribeWorld: SubscribeWorld(Parse<SubscribeWorldMsg>(line)); return true;
                case IpcProtocol.MsgUnsubscribeWorld: UnsubscribeWorld(env.id); return true;

                // ---- tuning, physics, settings ----
                case IpcProtocol.MsgSetTunable: SetTunable(Parse<SetTunableMsg>(line)); return true;
                case IpcProtocol.MsgSetAssists: SetAssists(Parse<SetAssistsMsg>(line)); return true;
                case IpcProtocol.MsgSetSessionConfig: SetSessionConfig(env.id, line); return true;
                case IpcProtocol.MsgSetModeTuning: SetOverrideTuning(Parse<SetTuningMsg>(line), arcade: false); return true;
                case IpcProtocol.MsgSetArcadeTuning: SetOverrideTuning(Parse<SetTuningMsg>(line), arcade: true); return true;
                case IpcProtocol.MsgSetSolver: SetSolver(env.id, line); return true;
                case IpcProtocol.MsgSetRates: SetRates(Parse<SetRatesMsg>(line)); return true;
                case IpcProtocol.MsgSetSettings: SetSettings(env.id, line); return true;

                // ---- design and lifecycle ----
                case IpcProtocol.MsgPushDesign: PushDesign(Parse<PushDesignMsg>(line)); return true;
                case IpcProtocol.MsgLoadTrack: LoadTrack(env.id, line); return true;
                case IpcProtocol.MsgEndSession: EndSession(env.id); return true;
                case IpcProtocol.MsgRestartRun: RestartRun(Parse<VehicleRefMsg>(line)); return true;
                case IpcProtocol.MsgSpawnVehicle: SpawnVehicle(Parse<SpawnVehicleMsg>(line)); return true;
                case IpcProtocol.MsgDespawnVehicle: DespawnVehicle(Parse<VehicleRefMsg>(line)); return true;
            }
            return false;
        }

        private static T Parse<T>(string line) => JsonUtility.FromJson<T>(line);

        /// <summary>
        /// Resolve a vehicle id, replying with the error itself when it fails.
        /// Returns null when the caller should stop — the reply is already sent.
        /// </summary>
        private IpcVehicle Resolve(int id, int vehicleId)
        {
            _registry.Refresh();
            var v = _registry.Find(vehicleId);
            if (v == null)
            {
                Err(id, IpcProtocol.ErrNoVehicle,
                    $"no vehicle with id {vehicleId}; call list_vehicles");
                return null;
            }
            if (v.rig.car == null)
            {
                Err(id, IpcProtocol.ErrNoVehicle, $"vehicle {vehicleId} no longer has a car");
                return null;
            }
            return v;
        }

        /// <summary>As <see cref="Resolve"/>, and additionally requires that this
        /// client currently holds the vehicle at the given level.</summary>
        private IpcVehicle ResolveHeld(int id, int vehicleId, string level)
        {
            var v = Resolve(id, vehicleId);
            if (v == null) return null;
            if (!v.Acquired)
            {
                Err(id, IpcProtocol.ErrNotAcquired,
                    $"acquire vehicle {vehicleId} before commanding it");
                return null;
            }
            if (level != null && v.level != level)
            {
                Err(id, IpcProtocol.ErrWrongLevel,
                    $"vehicle {vehicleId} is held at level '{v.level}', not '{level}'");
                return null;
            }
            return v;
        }

        // ══════════════════════════ enumeration ══════════════════════════════

        private void ListVehicles(int id)
        {
            _registry.Refresh();
            var list = new List<VehicleInfo>();
            foreach (var v in _registry.Vehicles) list.Add(_registry.Describe(v));
            Reply(new VehiclesReply
            {
                t = IpcProtocol.MsgVehicles, id = id, vehicles = list.ToArray(),
            });
        }

        private void ListTracks(int id)
        {
            var list = new List<TrackInfo>();

            foreach (var row in SceneTrackCatalog.All)
                list.Add(new TrackInfo
                {
                    id = row.scene,
                    displayName = row.label,
                    kind = "scene",
                    hasFinish = row.kind != TrackPresets.TrackKind.FreeRoam,
                });

            foreach (var p in TrackPresets.All)
                list.Add(new TrackInfo
                {
                    id = p.name,
                    displayName = p.name,
                    kind = "tilemap",
                    hasFinish = p.kind != TrackPresets.TrackKind.FreeRoam,
                });

            // The classic procedural oval is what an empty track id selects, so
            // it is listed rather than left as folklore.
            list.Add(new TrackInfo { id = "", displayName = "Classic Oval", kind = "oval", hasFinish = true });

            Reply(new TracksReply { t = IpcProtocol.MsgTracks, id = id, tracks = list.ToArray() });
        }

        private void ListPresets(int id)
        {
            var presets = new List<string>();
            foreach (var p in VehiclePresets.All) presets.Add(p.name);
            Reply(new PresetsReply
            {
                t = IpcProtocol.MsgPresets, id = id,
                presets = presets.ToArray(),
                saved = VehicleLibrary.List().ToArray(),
            });
        }

        private void ListChannels(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            var hub = v.rig.runner != null ? v.rig.runner.Hub : null;
            var names = new List<string>();
            if (hub != null) foreach (var c in hub.Channels) names.Add(c.Name);

            Reply(new ChannelsReply
            {
                t = IpcProtocol.MsgChannels, id = m.id,
                vehicleId = m.vehicleId, channels = names.ToArray(),
            });
        }

        private void GetSession(int id)
        {
            _registry.Refresh();
            var s = IpcSessionInfo.Capture();
            Reply(new SessionReply
            {
                t = IpcProtocol.MsgSession, id = id,
                active = s.active, scene = s.scene, trackId = s.trackId, match = s.match,
                vehicleCount = _registry.Vehicles.Count, simTime = s.simTime, lan = s.lan,
                paused = Time.timeScale == 0f,
                physicsHz = s.physicsHz, controlHz = s.controlHz,
            });
        }

        private void GetTunables(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            var list = new List<TunableInfo>();
            if (v.rig.car is ITunable tunable)
                foreach (var p in tunable.GetTunables())
                    list.Add(new TunableInfo { name = p.Name, min = p.Min, max = p.Max, value = p.Get() });

            Reply(new TunablesReply
            {
                t = IpcProtocol.MsgTunables, id = m.id,
                vehicleId = m.vehicleId, tunables = list.ToArray(),
            });
        }

        // ══════════════════════════ takeover ═════════════════════════════════

        private void Acquire(AcquireMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            string level = string.IsNullOrEmpty(m.level) ? IpcProtocol.LevelDrive : m.level;
            if (level != IpcProtocol.LevelDrive && level != IpcProtocol.LevelRaw)
            {
                Err(m.id, IpcProtocol.ErrBadArgument,
                    $"level must be '{IpcProtocol.LevelDrive}' or '{IpcProtocol.LevelRaw}'");
                return;
            }

            float deadMan = m.deadManMs == 0 ? 0f : m.deadManMs / 1000f;
            string code = _registry.Acquire(v, level, deadMan, out string why);
            if (code != null) { Err(m.id, code, why); return; }

            Debug.Log($"[IPC] acquire vehicle {v.id} ('{v.rig.slot?.name}') at level '{level}'");
            Ack(m.id, $"holding vehicle {v.id} at level '{level}'");
        }

        private void ReleaseVehicle(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;
            if (!v.Acquired) { Ack(m.id, $"vehicle {v.id} was not held"); return; }

            _registry.Release(v);
            Debug.Log($"[IPC] release vehicle {v.id}");
            Ack(m.id, $"released vehicle {v.id}");
        }

        private void Drive(DriveMsg m)
        {
            var v = ResolveHeld(m.id, m.vehicleId, IpcProtocol.LevelDrive);
            if (v == null) return;
            v.driver.Receive(m);
            // No ack: drive is the hot path and a client sending it at 100 Hz does
            // not want 100 replies a second competing with its telemetry. Errors
            // still come back, so a mistake is never silent.
        }

        private void Actuate(ActuateMsg m)
        {
            var v = ResolveHeld(m.id, m.vehicleId, IpcProtocol.LevelRaw);
            if (v == null) return;
            v.actuator.Receive(m);
            // Unacked, same reason as drive.
        }

        private void ResetVehicle(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;
            v.rig.car.ResetVehicle();
            Debug.Log($"[IPC] reset vehicle {v.id}");
            Ack(m.id);
        }

        private void Teleport(TeleportMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            var car = v.rig.car;
            Vector3 pos = m.pos != null ? m.pos.ToVector3() : car.transform.position;
            Quaternion rot = m.euler != null ? Quaternion.Euler(m.euler.ToVector3())
                                             : car.transform.rotation;

            Vector3 vel, angVel;
            if (m.keepMomentum)
            {
                var rb = car.GetComponent<Rigidbody>();
                vel = rb != null ? rb.linearVelocity : Vector3.zero;
                angVel = rb != null ? rb.angularVelocity : Vector3.zero;
            }
            else
            {
                vel = m.vel != null ? m.vel.ToVector3() : Vector3.zero;
                angVel = m.angVel != null ? m.angVel.ToVector3() : Vector3.zero;
            }

            // RestoreState rather than transform writes: it also parks the wheel
            // colliders and the rigidbody consistently, which a bare transform
            // assignment does not — a car moved by transform alone keeps its old
            // suspension compression and spins its wheels against thin air.
            car.RestoreState(pos, rot, vel, angVel);
            Ack(m.id);
        }

        private void SetDriveMode(SetModeMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;
            if (v.rig.runner == null) { Err(m.id, IpcProtocol.ErrNotSupported, "no runner"); return; }

            string want = (m.mode ?? "").ToLowerInvariant();
            if (want != "manual" && want != "autonomous")
            {
                Err(m.id, IpcProtocol.ErrBadArgument, "mode must be 'manual' or 'autonomous'");
                return;
            }

            v.rig.runner.SetMode(want == "manual" ? SimulationRunner.DriveMode.Manual : SimulationRunner.DriveMode.Autonomous);
            Debug.Log($"[IPC] vehicle {v.id} mode -> {want}");
            Ack(m.id);
        }

        // ══════════════════════════ telemetry ════════════════════════════════

        private void Subscribe(SubscribeMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            var names = _streamer.Subscribe(v, m.channels, m.rateHz, out string error);
            if (names == null) { Err(m.id, IpcProtocol.ErrUnknownChannel, error); return; }

            float control = v.rig.runner.controlRateHz;
            float rate = m.rateHz <= 0f ? control : Mathf.Min(m.rateHz, control);
            Reply(new SubscribedReply
            {
                t = IpcProtocol.MsgSubscribed, id = m.id,
                vehicleId = v.id, channels = names, rateHz = rate,
            });
        }

        private void Unsubscribe(VehicleRefMsg m)
        {
            bool had = _streamer.Unsubscribe(m.vehicleId);
            Ack(m.id, had ? null : $"vehicle {m.vehicleId} had no subscription");
        }

        private void SubscribeCamera(SubscribeCameraMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;
            if (!_streamer.SubscribeCamera(v, m.sensor, out string error))
            {
                Err(m.id, IpcProtocol.ErrNotSupported, error);
                return;
            }
            Ack(m.id, $"streaming camera frames for vehicle {v.id}");
        }

        private void UnsubscribeCamera(VehicleRefMsg m)
        {
            bool had = _streamer.UnsubscribeCamera(m.vehicleId);
            Ack(m.id, had ? null : $"vehicle {m.vehicleId} had no camera subscription");
        }

        // ---- world sensors (2026-08 additive) --------------------------------

        private void ListWorldSensors(int id)
        {
            if (Telemetry.WorldTelemetry.Hub == null)
            {
                Err(id, IpcProtocol.ErrNoWorldHub, "no world hub — no track is loaded");
                return;
            }

            var sensors = Telemetry.WorldTelemetry.Sensors;
            var dtos = new WorldSensorDto[sensors.Count];
            for (int i = 0; i < sensors.Count; i++)
            {
                var s = sensors[i];
                var chans = Telemetry.WorldTelemetry.ChannelsOf(i);
                var names = new string[chans.Count];
                for (int c = 0; c < chans.Count; c++) names[c] = chans[c];
                var p = s.WorldPosition;
                dtos[i] = new WorldSensorDto
                {
                    name = s.WorldSensorName, kind = s.WorldSensorKind,
                    channels = names, px = p.x, py = p.y, pz = p.z,
                };
            }
            Reply(new WorldSensorsReply
            {
                t = IpcProtocol.MsgWorldSensors, id = id, sensors = dtos,
            });
        }

        private void SubscribeWorld(SubscribeWorldMsg m)
        {
            if (Telemetry.WorldTelemetry.Hub == null)
            {
                Err(m.id, IpcProtocol.ErrNoWorldHub, "no world hub — no track is loaded");
                return;
            }

            var names = _streamer.SubscribeWorld(m.channels, m.rateHz, out string error);
            if (names == null) { Err(m.id, IpcProtocol.ErrUnknownChannel, error); return; }

            float rate = m.rateHz <= 0f ? Telemetry.WorldTelemetry.WorldRateHz
                                        : Mathf.Min(m.rateHz, Telemetry.WorldTelemetry.WorldRateHz);
            // The binary layout contract, same as a vehicle subscribe — the
            // sentinel id tells the client which frames decode against it.
            Reply(new SubscribedReply
            {
                t = IpcProtocol.MsgSubscribed, id = m.id,
                vehicleId = IpcProtocol.WorldStreamId, channels = names, rateHz = rate,
            });
        }

        private void UnsubscribeWorld(int id)
        {
            bool had = _streamer.UnsubscribeWorld();
            Ack(id, had ? null : "there was no world subscription");
        }

        // ══════════════════════════ tuning ═══════════════════════════════════

        private void SetTunable(SetTunableMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            if (!(v.rig.car is ITunable tunable))
            {
                Err(m.id, IpcProtocol.ErrNotSupported, $"vehicle {v.id} exposes no tunables");
                return;
            }

            foreach (var p in tunable.GetTunables())
            {
                if (p.Name != m.name) continue;
                // Clamped, not rejected: the range is what the pause-menu slider
                // offers, and a script that asks for the end of the range should
                // get the end of the range rather than an error.
                p.Set(Mathf.Clamp(m.value, p.Min, p.Max));
                Debug.Log($"[IPC] vehicle {v.id} tunable '{p.Name}' -> {p.Get()}");
                Ack(m.id, $"{p.Name} = {p.Get()}");
                return;
            }

            Err(m.id, IpcProtocol.ErrBadArgument,
                $"vehicle {v.id} has no tunable named '{m.name}'; call get_tunables");
        }

        private void SetAssists(SetAssistsMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            var a = new AssistSettings
            {
                steer = Mathf.Clamp01(m.steer),
                stability = Mathf.Clamp01(m.stability),
                traction = Mathf.Clamp01(m.traction),
                abs = Mathf.Clamp01(m.abs),
                launch = Mathf.Clamp01(m.launch),
            };
            if (v.rig.slot != null) v.rig.slot.assists = a;
            v.rig.car.assists = a;

            // With arcade handling on, HandlingFloor re-asserts a per-channel
            // maximum every frame, so a value BELOW the floor snaps back. Said
            // here because a client lowering an assist and watching it return is
            // otherwise looking at a bug that is not one.
            AssistApplier.ApplyFloor(v.rig.car, v.rig.slot, a);
            Ack(m.id);
        }

        private void SetSessionConfig(int id, string line)
        {
            // Prefilled from the live values, then overwritten only where the
            // client's JSON actually has keys — see the note in IpcMessages.cs.
            var m = new SetSessionConfigMsg
            {
                targetLaps = SessionConfig.TargetLaps,
                targetScore = SessionConfig.TargetScore,
                timeLimitSec = SessionConfig.TimeLimitSec,
                rubberBand = SessionConfig.RubberBand,
                arcade = SessionConfig.Arcade,
                trackLimits = SessionConfig.TrackLimits,
                arcadeHandling = SessionConfig.ArcadeHandling,
                arcadeTyreThermal = SessionConfig.ArcadeTyreThermal,
            };
            JsonUtility.FromJsonOverwrite(line, m);

            SessionConfig.TargetLaps = m.targetLaps;
            SessionConfig.TargetScore = m.targetScore;
            SessionConfig.TimeLimitSec = m.timeLimitSec;
            SessionConfig.RubberBand = m.rubberBand;
            SessionConfig.Arcade = m.arcade;
            SessionConfig.TrackLimits = m.trackLimits;
            SessionConfig.ArcadeHandling = m.arcadeHandling;
            SessionConfig.ArcadeTyreThermal = m.arcadeTyreThermal;

            Debug.Log($"[IPC] session config: laps={m.targetLaps} arcadeHandling={m.arcadeHandling} "
                      + $"tyreThermal={m.arcadeTyreThermal}");
            Ack(id);
        }

        /// <summary>
        /// Poke one named field on the mode or arcade tuning override asset.
        ///
        /// By name and through reflection because the alternative is a wire
        /// message that mirrors a class of several dozen fields and has to be
        /// edited every time one is added. The raise afterwards is not optional:
        /// these assets carry no runtime <c>OnValidate</c>, so the three
        /// subscribers that cache from them (ArenaGravity, MatchRacer, SoccerBall)
        /// would otherwise keep the old value until something else changed.
        /// </summary>
        private void SetOverrideTuning(SetTuningMsg m, bool arcade)
        {
            var descriptor = DrivingSceneDescriptor.Find();
            ScriptableObject asset = descriptor == null ? null
                : (arcade ? (ScriptableObject)descriptor.arcade : descriptor.modes);
            if (asset == null)
            {
                Err(m.id, IpcProtocol.ErrNotSupported,
                    $"this scene has no {(arcade ? "arcade" : "mode")} tuning override asset "
                    + "(add one on its DrivingSceneDescriptor)");
                return;
            }

            var field = asset.GetType().GetField(m.name);
            if (field == null)
            {
                Err(m.id, IpcProtocol.ErrBadArgument,
                    $"'{m.name}' is not a field of {asset.GetType().Name}");
                return;
            }

            if (field.FieldType == typeof(float)) field.SetValue(asset, m.value);
            else if (field.FieldType == typeof(int)) field.SetValue(asset, Mathf.RoundToInt(m.value));
            else if (field.FieldType == typeof(bool)) field.SetValue(asset, m.value != 0f);
            else
            {
                Err(m.id, IpcProtocol.ErrNotSupported,
                    $"'{m.name}' is a {field.FieldType.Name}; only float, int and bool are settable");
                return;
            }

            TuningBus.Raise(asset);
            Debug.Log($"[IPC] {asset.GetType().Name}.{m.name} -> {m.value}");
            Ack(m.id);
        }

        private void SetSolver(int id, string line)
        {
            // Prefilled from what the engine currently has, so an omitted field is
            // genuinely a no-op. These are the same five writes
            // PhysicsSettings.ApplySolver makes; done directly because the asset a
            // scene points at is a shipped file and this must not edit it on disk.
            var m = new SetSolverMsg
            {
                defaultContactOffset = Physics.defaultContactOffset,
                defaultSolverIterations = Physics.defaultSolverIterations,
                defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations,
                defaultMaxDepenetrationVelocity = Physics.defaultMaxDepenetrationVelocity,
                maximumDeltaTime = Time.maximumDeltaTime,
            };
            JsonUtility.FromJsonOverwrite(line, m);

            Physics.defaultContactOffset = Mathf.Max(0.0001f, m.defaultContactOffset);
            Physics.defaultSolverIterations = Mathf.Max(1, m.defaultSolverIterations);
            Physics.defaultSolverVelocityIterations = Mathf.Max(1, m.defaultSolverVelocityIterations);
            Physics.defaultMaxDepenetrationVelocity = Mathf.Max(0.01f, m.defaultMaxDepenetrationVelocity);
            Time.maximumDeltaTime = Mathf.Max(0.001f, m.maximumDeltaTime);

            Debug.Log($"[IPC] solver: contactOffset={Physics.defaultContactOffset} "
                      + $"iters={Physics.defaultSolverIterations}/{Physics.defaultSolverVelocityIterations} "
                      + $"maxDepen={Physics.defaultMaxDepenetrationVelocity} maxDt={Time.maximumDeltaTime}");
            Ack(id);
        }

        private void SetRates(SetRatesMsg m)
        {
            if (m.physicsHz <= 0 && m.controlHz <= 0)
            {
                Err(m.id, IpcProtocol.ErrBadArgument, "give physicsHz, controlHz, or both");
                return;
            }

            var runners = FindObjectsByType<SimulationRunner>(FindObjectsSortMode.None);
            if (runners.Length == 0) { Err(m.id, IpcProtocol.ErrNoSession, "no runner in this scene"); return; }

            // EVERY runner, not just one. Time.fixedDeltaTime is global, so a rate
            // applied to one rig in a multi-rig session leaves the others being
            // stepped at a rate their control period was not derived from — which
            // is precisely what [RATE] warns about, and this is the caller that
            // would otherwise trip it on purpose.
            foreach (var r in runners) r.ReconfigureRates(m.physicsHz, m.controlHz);

            var first = runners[0];
            Debug.Log($"[IPC] rates -> {first.physicsRateHz} Hz physics / {first.controlRateHz} Hz control "
                      + $"across {runners.Length} runner(s)");
            Ack(m.id, $"physics {first.physicsRateHz} Hz, control {first.controlRateHz} Hz");
        }

        private void GetSettings(int id)
        {
            var s = Persistence.SettingsStore.Current;
            Reply(new SettingsReply
            {
                t = IpcProtocol.MsgSettings, id = id,
                masterVolume = s.masterVolume, sfxVolume = s.sfxVolume,
                engineVolume = s.engineVolume, musicVolume = s.musicVolume,
                bloom = s.bloom, vSync = s.vSync, fullscreen = s.fullscreen,
                qualityLevel = s.qualityLevel, logTelemetry = s.logTelemetry,
                noiseSeed = s.noiseSeed, actuationDelayTicks = s.actuationDelayTicks,
                spArcadeHandling = s.spArcadeHandling, spArcadeTyreThermal = s.spArcadeTyreThermal,
            });
        }

        private void SetSettings(int id, string line)
        {
            var s = Persistence.SettingsStore.Current;
            var m = new SetSettingsMsg
            {
                masterVolume = s.masterVolume, sfxVolume = s.sfxVolume,
                engineVolume = s.engineVolume, musicVolume = s.musicVolume,
                bloom = s.bloom, vSync = s.vSync, fullscreen = s.fullscreen,
                qualityLevel = s.qualityLevel, logTelemetry = s.logTelemetry,
                noiseSeed = s.noiseSeed, actuationDelayTicks = s.actuationDelayTicks,
                spArcadeHandling = s.spArcadeHandling, spArcadeTyreThermal = s.spArcadeTyreThermal,
            };
            JsonUtility.FromJsonOverwrite(line, m);

            s.masterVolume = Mathf.Clamp01(m.masterVolume);
            s.sfxVolume = Mathf.Clamp01(m.sfxVolume);
            s.engineVolume = Mathf.Clamp01(m.engineVolume);
            s.musicVolume = Mathf.Clamp01(m.musicVolume);
            s.bloom = m.bloom;
            s.vSync = m.vSync;
            s.fullscreen = m.fullscreen;
            s.qualityLevel = m.qualityLevel;
            s.logTelemetry = m.logTelemetry;
            s.noiseSeed = m.noiseSeed;
            s.actuationDelayTicks = Mathf.Clamp(m.actuationDelayTicks, 0, 32);
            s.spArcadeHandling = m.spArcadeHandling;
            s.spArcadeTyreThermal = m.spArcadeTyreThermal;

            // Note what does NOT take effect now: noiseSeed is read once per
            // process and actuationDelayTicks is read by each runner at build
            // time, so both apply to the NEXT session. Saved and reported anyway
            // rather than refused — setting up the next run is a normal thing for
            // a script to do.
            Persistence.SettingsStore.Save();
            Persistence.SettingsStore.Apply();
            SessionConfig.ArcadeHandling = s.spArcadeHandling;
            SessionConfig.ArcadeTyreThermal = s.spArcadeTyreThermal;

            Ack(id, "saved; noiseSeed and actuationDelayTicks apply to the next session");
        }

        // ══════════════════════════ design ═══════════════════════════════════

        private void PushDesign(PushDesignMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            if (string.IsNullOrWhiteSpace(m.designJson))
            {
                Err(m.id, IpcProtocol.ErrBadArgument, "designJson is empty");
                return;
            }

            VehicleDesign design;
            try { design = JsonUtility.FromJson<VehicleDesign>(m.designJson); }
            catch (System.Exception e)
            {
                Err(m.id, IpcProtocol.ErrBadJson, $"designJson did not parse: {e.Message}");
                return;
            }
            if (design == null) { Err(m.id, IpcProtocol.ErrBadJson, "designJson parsed to null"); return; }

            // The live-safe set first. These are exactly the fields LiveCarTuner
            // applies per step in the editor — reimplemented rather than reused
            // because that component's whole apply body is #if UNITY_EDITOR and
            // does nothing in a player build.
            var car = v.rig.car;
            car.steerRateDegPerSec = design.steerRate;
            car.servoStallNm = design.servoStallNm;
            car.ackermannPct = design.ackermannPct;
            car.maxBrakeTorque = design.maxBrakeTorque;
            car.handbrakeTorque = design.handbrakeTorque;
            car.brakeProportioning = design.brakeProportioning;
            car.antiRoll = design.antiRoll;
            car.stickyPhantomNm = design.stickyPhantomNm;
            car.dragCdOverride = design.dragCd;
            car.frontalAreaOverride = design.frontalAreaM2;

            if (m.liveOnly)
            {
                Ack(m.id, "applied the live-safe fields only");
                return;
            }

            if (!CarRebuilder.CanRebuild(v.rig, out string why))
            {
                // The refusal reason goes through verbatim. CarRebuilder already
                // words these for a human ("a LAN car is simulated on its owner's
                // machine"), and rewording them here would just make two versions
                // of the same sentence to keep in step.
                Err(m.id, IpcProtocol.ErrRebuildRefused, why);
                return;
            }

            bool wasHeld = v.Acquired;
            string heldLevel = v.level;
            if (wasHeld) _registry.Release(v);   // the takeover points at a car about to be destroyed

            var rebuilt = CarRebuilder.RebuildInPlace(v.rig, design);
            if (rebuilt == null)
            {
                Err(m.id, IpcProtocol.ErrRebuildRefused, "the rebuild was refused; see the log");
                return;
            }

            if (wasHeld) _registry.Acquire(v, heldLevel, 0f, out _);
            _registry.Invalidate();
            _streamer.Unsubscribe(v.id);   // the old hub's channels closed over the old car

            Debug.Log($"[IPC] rebuilt vehicle {v.id} from a pushed design");
            Ack(m.id, wasHeld
                ? "rebuilt; the takeover was re-established and any subscription was dropped"
                : "rebuilt; any subscription was dropped");
        }

        // ══════════════════════════ lifecycle ════════════════════════════════

        /// <summary>Refuse a lifecycle command during a LAN session. The host owns
        /// the match and every client is watching it; a local script changing the
        /// track out from under them is not a supported thing to do.</summary>
        private bool RefuseInLan(int id)
        {
            if (Net.NetSession.Instance == null) return false;
            Err(id, IpcProtocol.ErrLanSession, "not while a LAN session is running");
            return true;
        }

        private void LoadTrack(int id, string line)
        {
            if (RefuseInLan(id)) return;

            var s = Persistence.SettingsStore.Current;
            var m = new LoadTrackMsg
            {
                trackId = "",
                match = SessionConfig.Match.ToString(),
                laps = s.lastLaps,
                bots = s.spBots,
                difficulty = s.spDifficulty,
                arcade = s.spArcade,
                arcadeHandling = s.spArcadeHandling,
                trackLimits = s.spTrackLimits,
                countdown = s.spCountdown,
                vehicle = s.lastVehicle,
            };
            JsonUtility.FromJsonOverwrite(line, m);

            if (!System.Enum.TryParse<MatchMode>(m.match, true, out var mode))
            {
                Err(id, IpcProtocol.ErrBadArgument,
                    $"'{m.match}' is not a match mode (Race, Derby, Ctf, Soccer, FreeRoam)");
                return;
            }

            if (!SelectTrackById(m.trackId, out string trackError))
            {
                Err(id, IpcProtocol.ErrBadArgument, trackError);
                return;
            }

            bool roam = mode == MatchMode.FreeRoam;
            int bots = roam ? 0 : Mathf.Clamp(m.bots, 0, 7);

            SessionConfig.SetSinglePlayer();     // clears the roster and the rubber band
            SessionConfig.Match = mode;
            SessionConfig.TargetLaps = roam ? 0 : Mathf.Max(0, m.laps);
            SessionConfig.CountdownSeconds = roam ? 0 : Mathf.Clamp(m.countdown, 0, 60);
            SessionConfig.ResultsWaitSeconds = s.spResultsWait;
            SessionConfig.Arcade = m.arcade && !roam;
            SessionConfig.TrackLimits = SessionConfig.Arcade && m.trackLimits;
            SessionConfig.ArcadeHandling = m.arcadeHandling;
            SessionConfig.ArcadeTyreThermal = s.spArcadeTyreThermal;

            GameFlow.ActiveDesign = ResolveDesign(m.vehicle);

            string pname = string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name;
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = pname,
                profileId = pname,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                assists = SessionConfig.P1Assists(s),
                isBot = false,
                control = DriveControl.Human,
            });
            for (int k = 1; k <= bots; k++)
                SessionConfig.Players.Add(SessionConfig.MakeBotSlot(k, Mathf.Clamp(m.difficulty, 0, 2)));

            if (SessionConfig.IsTeamMatch)
                for (int i = 0; i < SessionConfig.Players.Count; i++)
                    SessionConfig.Players[i].team = i % 2;

            // Same guard the menu uses: a scene that is not in Build Settings
            // loads as a black screen, and saying so beats showing one.
            string scene = GameFlow.HasSceneTrack ? GameFlow.ActiveSceneTrack : GameFlow.TrackSceneName;
            if (!Application.CanStreamedLevelBeLoaded(scene))
            {
                Err(id, IpcProtocol.ErrBadArgument,
                    $"scene '{scene}' is not in Build Settings, so it cannot be loaded");
                return;
            }

            Debug.Log($"[IPC] load_track '{m.trackId}' mode={mode} laps={SessionConfig.TargetLaps} bots={bots}");
            Ack(id, $"loading '{scene}'");

            // Acked BEFORE the load: the scene change destroys and rebuilds a great
            // deal, and a reply queued behind it would reach the client late enough
            // to look like a hang. The session_changed event follows when the new
            // scene is up.
            GameFlow.LoadTrack();
        }

        /// <summary>Point GameFlow at a track. Accepts a scene name, a picker
        /// label, a tile-map preset name, or "" for the classic oval.</summary>
        private static bool SelectTrackById(string trackId, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(trackId))
            {
                GameFlow.ActiveTrack = null;      // also clears ActiveSceneTrack
                return true;
            }

            string scene = SceneTrackCatalog.Resolve(trackId);
            if (scene != null) { GameFlow.ActiveSceneTrack = scene; return true; }

            var design = TrackPresets.Resolve(trackId);
            if (design != null) { GameFlow.ActiveTrack = design; return true; }

            error = $"no track called '{trackId}'; call list_tracks";
            return false;
        }

        /// <summary>A preset name, a saved garage design, or "" for the stock car.</summary>
        private static VehicleDesign ResolveDesign(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return VehiclePresets.Resolve(name) ?? VehicleLibrary.Load(name);
        }

        private void EndSession(int id)
        {
            if (RefuseInLan(id)) return;
            _registry.ReleaseAll("the session is ending");
            _streamer.ClearAll();
            Debug.Log("[IPC] end_session");
            Ack(id, "returning to the menu");
            GameFlow.LoadMenu();
        }

        private void RestartRun(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;
            if (v.rig.runner == null) { Err(m.id, IpcProtocol.ErrNotSupported, "no runner"); return; }

            v.rig.runner.RestartRun();
            Debug.Log($"[IPC] restart run on vehicle {v.id}");
            Ack(m.id);
        }

        private void SpawnVehicle(SpawnVehicleMsg m)
        {
            if (RefuseInLan(m.id)) return;

            VehicleDesign design;
            if (!string.IsNullOrWhiteSpace(m.designJson))
            {
                try { design = JsonUtility.FromJson<VehicleDesign>(m.designJson); }
                catch (System.Exception e)
                {
                    Err(m.id, IpcProtocol.ErrBadJson, $"designJson did not parse: {e.Message}");
                    return;
                }
            }
            else
            {
                design = ResolveDesign(m.preset) ?? VehicleDesign.Default();
            }
            if (design == null) { Err(m.id, IpcProtocol.ErrBadArgument, "no usable design"); return; }

            Vector3 pos = m.pos != null ? m.pos.ToVector3() : Vector3.up;
            Quaternion rot = m.euler != null ? Quaternion.Euler(m.euler.ToVector3()) : Quaternion.identity;

            var built = DebugVehicleRig.BuildCar(design, pos, rot);
            DebugVehicleRig.AttachRunner(ref built, null,
                                         PhysicsRateOfSession(), ControlRateOfSession(), logCsv: false);

            var rig = new PlayerRig
            {
                slot = new PlayerSlot
                {
                    name = string.IsNullOrWhiteSpace(m.name) ? "IPC Vehicle" : m.name,
                    design = design,
                    isLocal = true,
                    isBot = false,
                    control = DriveControl.Human,
                },
                car = built.car,
                input = built.input,
                runner = built.runner,
                sensorRig = built.sensors,
            };

            var v = _registry.AddSpawned(rig, built.root);
            Debug.Log($"[IPC] spawned vehicle {v.id} ('{rig.slot.name}') at {pos}");

            if (!string.IsNullOrEmpty(m.acquire))
            {
                string code = _registry.Acquire(v, m.acquire, 0f, out string why);
                if (code != null) { Err(m.id, code, why); return; }
            }

            Event(IpcProtocol.EvtVehiclesChanged, v.id);
            Ack(m.id, $"spawned vehicle {v.id}");
        }

        /// <summary>
        /// A spawned car must be stepped at the rate the session already runs at,
        /// not at SimulationRunner's component default — a second runner asking for
        /// 500 Hz in a 400 Hz session would re-time every rig already driving.
        /// This is the same adoption <c>DebugVehicleSpawner</c> performs.
        /// </summary>
        private static int PhysicsRateOfSession()
        {
            var existing = FindFirstObjectByType<SimulationRunner>();
            return existing != null ? existing.physicsRateHz : 400;
        }

        private static int ControlRateOfSession()
        {
            var existing = FindFirstObjectByType<SimulationRunner>();
            return existing != null ? existing.controlRateHz : 100;
        }

        private void DespawnVehicle(VehicleRefMsg m)
        {
            var v = Resolve(m.id, m.vehicleId);
            if (v == null) return;

            if (!_registry.IsSpawned(v))
            {
                // Destroying a car TrackBootstrap built would leave the bootstrap,
                // the HUD, the pause menu and any match director holding a rig
                // whose car is gone. Only what this bridge created is its to remove.
                Err(m.id, IpcProtocol.ErrNotSupported,
                    $"vehicle {v.id} belongs to the session, not to this bridge; "
                    + "only vehicles from spawn_vehicle can be despawned");
                return;
            }

            _streamer.Unsubscribe(v.id);
            _streamer.UnsubscribeCamera(v.id);
            _registry.RemoveSpawned(v);
            Debug.Log($"[IPC] despawned vehicle {v.id}");
            Event(IpcProtocol.EvtVehiclesChanged, v.id);
            Ack(m.id);
        }
    }
}
