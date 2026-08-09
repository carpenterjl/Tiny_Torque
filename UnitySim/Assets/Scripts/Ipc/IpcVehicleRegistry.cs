using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Sensors;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// One vehicle as the bridge sees it: the rig, the id an external client
    /// knows it by, and — while the client has taken it over — the input sources
    /// that were displaced to make room.
    /// </summary>
    internal sealed class IpcVehicle
    {
        public int id;
        public PlayerRig rig;

        /// <summary>"" when nobody holds it, else <c>drive</c> or <c>raw</c>.</summary>
        public string level = "";

        public IpcDriverSource driver;
        public IpcActuatorDriver actuator;

        /// <summary>What <c>CarInput.source</c> held before the takeover, put back
        /// on release. Null is a legitimate value — <c>CarInput</c> lazily fills
        /// its own default with <c>??=</c>, so restoring null restores exactly the
        /// behaviour a never-touched car had.</summary>
        public IDriverInputSource previousSource;
        public bool hadPreviousSource;

        /// <summary>What the runner's <c>inputBehaviour</c> held before a raw
        /// takeover.</summary>
        public MonoBehaviour previousInputBehaviour;

        public bool Acquired => level.Length > 0;

        /// <summary>Set when the bridge created this car itself, with the root
        /// GameObject to destroy on despawn. A session rig has none — those belong
        /// to <c>TrackBootstrap</c> and are not ours to remove.</summary>
        public GameObject spawnedRoot;
    }

    /// <summary>
    /// Maps live <see cref="PlayerRig"/>s to the small integer ids the protocol
    /// uses, and owns the acquire/release of external control.
    ///
    /// <b>Ids are stable for as long as the car is.</b> A client that acquired
    /// vehicle 2 and then asked for the list again must still find its car at 2,
    /// so ids come from a counter and are remembered per rig — never from the
    /// rig's index, which shifts the moment a LAN player leaves or a spawned car
    /// is removed.
    ///
    /// The rig list itself is not ours: <c>TrackBootstrap</c> builds and owns it.
    /// This class re-reads it when something says it changed
    /// (<see cref="Invalidate"/>) and otherwise leaves it alone, so the common
    /// frame costs a null check.
    /// </summary>
    internal sealed class IpcVehicleRegistry
    {
        private readonly List<IpcVehicle> _vehicles = new List<IpcVehicle>();
        private readonly Dictionary<PlayerRig, int> _ids = new Dictionary<PlayerRig, int>();
        private int _nextId = 1;
        private bool _dirty = true;
        private int _spawnedCount;
        private TrackBootstrap _bootstrap;

        public IReadOnlyList<IpcVehicle> Vehicles => _vehicles;

        /// <summary>Re-read the rig list on the next <see cref="Refresh"/>.</summary>
        public void Invalidate() => _dirty = true;

        /// <summary>
        /// Bring the vehicle list up to date. Cheap and idempotent: it only does
        /// work when something invalidated it or the bootstrap it was reading
        /// went away with a scene load.
        /// </summary>
        public void Refresh()
        {
            // Cheap steady-state path. With a bootstrap in hand, "did the rig list
            // change" is a count comparison — which also catches a LAN player
            // joining or leaving without either of those paths having to know this
            // class exists. Without one there is nothing to compare, so the dirty
            // flag (set by the scene-load hook) is the only trigger; scanning for a
            // bootstrap every frame in the menu would be a FindObjectsByType per
            // frame to answer "still no session".
            if (!_dirty)
            {
                if (_bootstrap == null) return;
                var live = _bootstrap.Rigs;
                if (live != null && live.Count + _spawnedCount == _vehicles.Count) return;
            }
            _dirty = false;

            _bootstrap = FindBootstrap();
            var rigs = _bootstrap != null ? _bootstrap.Rigs : null;

            // Drop entries whose car is gone, keeping the takeover bookkeeping of
            // the ones that survive. A rig whose car was destroyed cannot be
            // released cleanly — there is nothing left to hand back to — so the
            // entry simply goes.
            for (int i = _vehicles.Count - 1; i >= 0; i--)
            {
                var v = _vehicles[i];
                bool alive = v.rig != null && v.rig.car != null
                             && (v.spawnedRoot != null || Holds(rigs, v.rig));
                if (!alive)
                {
                    if (v.spawnedRoot != null) _spawnedCount--;
                    _ids.Remove(v.rig);
                    _vehicles.RemoveAt(i);
                }
            }

            if (rigs == null) return;

            foreach (var rig in rigs)
            {
                if (rig == null || rig.car == null) continue;
                if (_ids.ContainsKey(rig)) continue;
                int id = _nextId++;
                _ids[rig] = id;
                _vehicles.Add(new IpcVehicle { id = id, rig = rig });
            }
        }

        /// <summary>Membership test over the read-only view. <c>IReadOnlyList</c>
        /// has no Contains, and this project does not use LINQ.</summary>
        private static bool Holds(IReadOnlyList<PlayerRig> rigs, PlayerRig rig)
        {
            if (rigs == null) return false;
            for (int i = 0; i < rigs.Count; i++)
                if (ReferenceEquals(rigs[i], rig)) return true;
            return false;
        }

        /// <summary>
        /// The bootstrap that actually composed this session.
        ///
        /// There can be two in the scene during a scene-track load — the authored
        /// track carries one so pressing Play works, and TrackScene brings its own
        /// — and the overlay copy stands down with no rigs at all. Picking the one
        /// that has rigs is therefore the test, not "the first one found".
        /// </summary>
        private static TrackBootstrap FindBootstrap()
        {
            var all = Object.FindObjectsByType<TrackBootstrap>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            TrackBootstrap fallback = null;
            foreach (var b in all)
            {
                if (b.Rigs != null && b.Rigs.Count > 0) return b;
                fallback ??= b;
            }
            return fallback;
        }

        public IpcVehicle Find(int id)
        {
            foreach (var v in _vehicles)
                if (v.id == id) return v;
            return null;
        }

        // ---- vehicles this bridge created ------------------------------------

        /// <summary>
        /// Adopt a car the bridge built itself.
        ///
        /// Deliberately NOT added to <c>TrackBootstrap</c>'s rig list, even
        /// though there is nothing stopping it: a MatchDirector aliases that same
        /// List, so appending to it mid-race hands the director a rig with no
        /// MatchRacer and no lap tracker. A spawned car is therefore a free agent
        /// — drivable, streamable, tunable, but not scored and not lap-timed —
        /// which is what a scripting spawn wants anyway.
        /// </summary>
        public IpcVehicle AddSpawned(PlayerRig rig, GameObject root)
        {
            int id = _nextId++;
            _ids[rig] = id;
            var v = new IpcVehicle { id = id, rig = rig, spawnedRoot = root };
            _vehicles.Add(v);
            _spawnedCount++;
            return v;
        }

        public bool IsSpawned(IpcVehicle v) => v != null && v.spawnedRoot != null;

        public void RemoveSpawned(IpcVehicle v)
        {
            if (!IsSpawned(v)) return;
            Release(v);

            // The runner lives on its own GameObject beside the car (AttachRunner
            // makes it), so destroying the car's root alone would leave a runner
            // stepping a corpse.
            if (v.rig.runner != null) Object.Destroy(v.rig.runner.gameObject);
            Object.Destroy(v.spawnedRoot);

            _ids.Remove(v.rig);
            _vehicles.Remove(v);
            _spawnedCount--;
        }

        // ---- takeover --------------------------------------------------------

        /// <summary>
        /// Take control of a vehicle. Returns null on success, else the error code
        /// to send back.
        ///
        /// Both levels displace local input, which is the whole point of an
        /// explicit acquire: while a client holds a car, the gamepad must not also
        /// be steering it. Raw additionally displaces the runner's input behaviour
        /// — and still installs the driver source, because <c>CarInput.Update</c>
        /// keeps reading its source for handbrake, respawn and horn even when the
        /// actuator vector is coming from somewhere else.
        /// </summary>
        public string Acquire(IpcVehicle v, string level, float deadManSeconds, out string why)
        {
            why = "";
            if (v.Acquired)
            {
                why = $"vehicle {v.id} is already held at level '{v.level}'";
                return IpcProtocol.ErrAlreadyAcquired;
            }
            if (v.rig.input == null || v.rig.runner == null)
            {
                why = $"vehicle {v.id} has no input or runner to take over";
                return IpcProtocol.ErrNotSupported;
            }

            v.driver = new IpcDriverSource(deadManSeconds);
            v.previousSource = v.rig.input.source;
            v.hadPreviousSource = true;
            v.rig.input.source = v.driver;

            if (level == IpcProtocol.LevelRaw)
            {
                // Always a fresh component, never GetComponent-or-add. Destroy is
                // deferred to the end of the frame, so a release followed by an
                // acquire in the same frame would find the OLD driver still
                // attached and about to be destroyed — and `??` does not see
                // Unity's fake-null, so it would hand it straight back.
                var act = v.rig.runner.gameObject.AddComponent<IpcActuatorDriver>();
                act.Configure(v.rig.car, deadManSeconds);
                v.actuator = act;
                v.previousInputBehaviour = v.rig.runner.inputBehaviour;
                v.rig.runner.SetInputBehaviour(act);
            }

            v.level = level;
            return null;
        }

        /// <summary>Hand a vehicle back. Safe to call on one that is not held.</summary>
        public void Release(IpcVehicle v)
        {
            if (v == null || !v.Acquired) return;

            if (v.actuator != null)
            {
                if (v.rig.runner != null) v.rig.runner.SetInputBehaviour(v.previousInputBehaviour);
                Object.Destroy(v.actuator);
                v.actuator = null;
                v.previousInputBehaviour = null;
            }

            if (v.hadPreviousSource && v.rig.input != null)
                v.rig.input.source = v.previousSource;

            // Leaving the handbrake latched on a car handed back to a human is the
            // kind of thing that reads as a physics bug.
            if (v.rig.car != null) v.rig.car.SetHandbrake(false);

            v.driver = null;
            v.previousSource = null;
            v.hadPreviousSource = false;
            v.level = "";
        }

        /// <summary>Release everything — a client vanished, or the bridge is being
        /// switched off.</summary>
        public void ReleaseAll(string why)
        {
            int n = 0;
            foreach (var v in _vehicles)
            {
                if (!v.Acquired) continue;
                Release(v);
                n++;
            }
            if (n > 0) Debug.Log($"[IPC] released {n} vehicle(s): {why}");
        }

        // ---- description -----------------------------------------------------

        public VehicleInfo Describe(IpcVehicle v)
        {
            var rig = v.rig;
            var info = new VehicleInfo
            {
                id = v.id,
                name = rig.slot != null ? rig.slot.name : rig.car.name,
                designName = rig.slot?.design != null ? rig.slot.design.name : "",
                isBot = rig.slot != null && rig.slot.isBot,
                control = rig.slot != null ? rig.slot.control.ToString() : "Human",
                netSlot = rig.netSlot,
                acquired = v.Acquired,
                level = v.level,
                wheelCount = rig.car.WheelCount,
                motors = DescribeMotors(rig.sensorRig),
                sensors = DescribeSensors(rig.sensorRig),
                hasCamera = rig.sensorRig != null && rig.sensorRig.PrimaryCamera != null,
            };
            return info;
        }

        private static MotorInfo[] DescribeMotors(SensorRig rig)
        {
            if (rig == null) return new MotorInfo[0];
            var motors = rig.Motors;
            var outp = new MotorInfo[motors.Count];
            for (int i = 0; i < motors.Count; i++)
                outp[i] = new MotorInfo
                {
                    name = motors[i].sensorName,
                    actuatorIndex = motors[i].ActuatorIndex,
                    maxVoltage = motors[i].MaxVoltage,
                };
            return outp;
        }

        private static SensorInfoDto[] DescribeSensors(SensorRig rig)
        {
            if (rig == null) return new SensorInfoDto[0];
            var sensors = rig.Sensors;
            var outp = new SensorInfoDto[sensors.Count];
            for (int i = 0; i < sensors.Count; i++)
            {
                var s = sensors[i];
                var fields = s.FieldNames;
                var channels = new string[fields.Count];
                for (int f = 0; f < fields.Count; f++)
                    channels[f] = $"sens/{s.sensorName}/{fields[f]}";

                var dto = new SensorInfoDto
                {
                    name = s.sensorName,
                    kind = s.Type.ToString(),
                    channels = channels,
                };
                if (s is CameraSensor cam) { dto.camWidth = cam.Width; dto.camHeight = cam.Height; }
                outp[i] = dto;
            }
            return outp;
        }
    }

    /// <summary>A snapshot of "what is going on right now", for the handshake and
    /// for <c>get_session</c>.</summary>
    internal struct IpcSessionInfo
    {
        public bool active;
        public string scene;
        public string trackId;
        public string match;
        public bool lan;
        public int physicsHz;
        public int controlHz;
        public float simTime;

        public static IpcSessionInfo Capture()
        {
            var s = new IpcSessionInfo
            {
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                trackId = GameFlow.HasSceneTrack ? GameFlow.ActiveSceneTrack
                                                 : (GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.name : ""),
                match = SessionConfig.Match.ToString(),
                lan = Net.NetSession.Instance != null,
            };

            var runner = Object.FindFirstObjectByType<SimulationRunner>();
            if (runner != null)
            {
                s.active = true;
                s.physicsHz = runner.physicsRateHz;
                s.controlHz = runner.controlRateHz;
                s.simTime = runner.SimTime;
            }
            return s;
        }
    }
}
