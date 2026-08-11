using System;

namespace AIHWSim.Ipc
{
    // Every control message on the wire is one of these, serialized by
    // JsonUtility — public fields, [Serializable], no properties, no
    // Dictionary, no polymorphism. That is the codebase's JSON idiom (see
    // Net/NetMessages.cs) and it is also all JsonUtility can do.
    //
    // Reading is two passes over the same line: once into IpcEnvelope to learn
    // "t", then once into the concrete type. JsonUtility ignores fields it does
    // not know about, so the first pass is cheap and the second is exact.
    //
    // PARTIAL UPDATES. Several messages (set_settings, set_solver,
    // set_session_config, the tuning setters, load_track) let a client change
    // one field and leave the rest alone. JsonUtility cannot tell an absent key
    // from a zero, so the server does not parse those with FromJson: it fills
    // the DTO with the CURRENT values first and then calls FromJsonOverwrite,
    // which only writes keys that are actually present in the text. An omitted
    // field therefore round-trips to itself. Any new partial-update message
    // must be handled the same way or it will silently zero everything the
    // client did not mention.

    /// <summary>Just enough of any message to route it.</summary>
    [Serializable]
    public class IpcEnvelope
    {
        /// <summary>Message type; one of the Msg* constants in <see cref="IpcProtocol"/>.</summary>
        public string t;
        /// <summary>Client-chosen request id, echoed on the reply so a client can
        /// match them up. Unsolicited server messages use 0.</summary>
        public int id;
    }

    /// <summary>A position/velocity triple. Deliberately not UnityEngine.Vector3:
    /// the JSON is identical, but the contract must be expressible by an
    /// application that has never heard of Unity.</summary>
    [Serializable]
    public class IpcVec3
    {
        public float x, y, z;
        public IpcVec3() { }
        public IpcVec3(UnityEngine.Vector3 v) { x = v.x; y = v.y; z = v.z; }
        public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(x, y, z);
    }

    // ══════════════════════════ client → server ══════════════════════════════

    [Serializable]
    public class HelloMsg : IpcEnvelope
    {
        public int version;
        /// <summary>Free-text client name, logged once at connect. Diagnostics
        /// only — nothing branches on it.</summary>
        public string app;
    }

    /// <summary>The shape of every message whose only argument is "which vehicle":
    /// release, reset_vehicle, restart_run, despawn_vehicle, get_tunables,
    /// list_channels, unsubscribe, unsubscribe_camera.</summary>
    [Serializable]
    public class VehicleRefMsg : IpcEnvelope
    {
        public int vehicleId;
    }

    [Serializable]
    public class AcquireMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary><see cref="IpcProtocol.LevelDrive"/> or <see cref="IpcProtocol.LevelRaw"/>.</summary>
        public string level = IpcProtocol.LevelDrive;
        /// <summary>Milliseconds of silence after which the car brakes itself.
        /// 0 takes the default; negative disables the dead-man, which is only
        /// sane for a client that parks the car before it stops talking.</summary>
        public int deadManMs;
    }

    [Serializable]
    public class DriveMsg : IpcEnvelope
    {
        public int vehicleId;
        public float throttle;   // 0..1
        public float steer;      // -1..1
        public float brake;      // 0..1
        public bool handbrake;
        public bool respawn;     // edge, consumed once
        public bool useItem;     // edge
        public bool jump;        // edge
        public bool horn;        // held
        public bool boost;       // held
    }

    [Serializable]
    public class ActuateMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>The runner's actuator vector: motor volts at each motor's
        /// <c>ActuatorIndex</c>, [6] steer -1..1, [7] brake 0..1. Short arrays
        /// are accepted and zero-extended; anything past index 7 is ignored.</summary>
        public float[] actuators;
        /// <summary>Optional telemetry setpoints (logged, not acted on):
        /// [0] linear m/s, [1] yaw. Matches <c>ISetpointSource</c>.</summary>
        public float[] setpoints;
        public bool handbrake;
    }

    [Serializable]
    public class TeleportMsg : IpcEnvelope
    {
        public int vehicleId;
        public IpcVec3 pos;
        /// <summary>Euler degrees. Absent (null) keeps the current rotation.</summary>
        public IpcVec3 euler;
        /// <summary>Linear and angular velocity to restore. Absent means zero —
        /// a teleport that keeps the old velocity is asked for explicitly with
        /// <see cref="keepMomentum"/>.</summary>
        public IpcVec3 vel;
        public IpcVec3 angVel;
        public bool keepMomentum;
    }

    [Serializable]
    public class SetModeMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>"manual" or "autonomous" (the runner's native controller).</summary>
        public string mode;
    }

    [Serializable]
    public class SubscribeMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>Telemetry-hub channel names. Empty or null means every
        /// channel the vehicle has, in registration order.</summary>
        public string[] channels;
        /// <summary>Requested frames per second. Clamped to the control rate —
        /// the hub commits once per control tick and there is nothing between
        /// ticks to send. 0 takes the control rate.</summary>
        public float rateHz;
    }

    [Serializable]
    public class SubscribeCameraMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>Sensor name; empty takes the vehicle's primary camera.</summary>
        public string sensor;
    }

    [Serializable]
    public class SetTunableMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>Name exactly as <c>get_tunables</c> reported it.</summary>
        public string name;
        public float value;
    }

    [Serializable]
    public class SetAssistsMsg : IpcEnvelope
    {
        public int vehicleId;
        public float steer, stability, traction, abs, launch;
    }

    [Serializable]
    public class SetRatesMsg : IpcEnvelope
    {
        /// <summary>Physics steps per second. 0 leaves it alone.</summary>
        public int physicsHz;
        /// <summary>Control ticks per second; snapped to an integer division of
        /// the physics rate, exactly as the runner does it. 0 leaves it alone.</summary>
        public int controlHz;
    }

    [Serializable]
    public class PushDesignMsg : IpcEnvelope
    {
        public int vehicleId;
        /// <summary>A whole <c>VehicleDesign</c> as JSON — the same text
        /// <c>VehicleLibrary</c> saves. A string rather than a nested object so
        /// the design schema stays owned by Garage and versioned with the
        /// garage, not frozen into this protocol.</summary>
        public string designJson;
        /// <summary>Refuse anything that would need a full rebuild instead of
        /// attempting one. For a client that is sweeping a live parameter and
        /// must not have the car disappear under it.</summary>
        public bool liveOnly;
    }

    [Serializable]
    public class SpawnVehicleMsg : IpcEnvelope
    {
        /// <summary>Preset name from <c>list_presets</c>. Ignored when
        /// <see cref="designJson"/> is given.</summary>
        public string preset;
        public string designJson;
        public string name;
        public IpcVec3 pos;
        public IpcVec3 euler;
        /// <summary>Acquire it immediately at this level, so a spawn-then-drive
        /// script needs one round trip instead of two. Empty means don't.</summary>
        public string acquire;
    }

    /// <summary>Partial update — see the FromJsonOverwrite note at the top.</summary>
    [Serializable]
    public class LoadTrackMsg : IpcEnvelope
    {
        /// <summary>Track id from <c>list_tracks</c>.</summary>
        public string trackId;
        /// <summary>Match mode name: Race, Derby, Ctf, Soccer, FreeRoam.</summary>
        public string match;
        public int laps;
        public int bots;
        public int difficulty;
        public bool arcade;
        public bool arcadeHandling;
        public bool trackLimits;
        public int countdown;
        /// <summary>Vehicle design/preset name for the player car; empty keeps
        /// whatever the menu last selected.</summary>
        public string vehicle;
    }

    /// <summary>Partial update. Mirrors the live rules in <c>SessionConfig</c>.</summary>
    [Serializable]
    public class SetSessionConfigMsg : IpcEnvelope
    {
        public int targetLaps;
        public int targetScore;
        public int timeLimitSec;
        public bool rubberBand;
        public bool arcade;
        public bool trackLimits;
        public bool arcadeHandling;
        public bool arcadeTyreThermal;
    }

    /// <summary>Partial update. Solver settings from <c>PhysicsSettings</c>;
    /// the rates are their own message because they are not solver state.</summary>
    [Serializable]
    public class SetSolverMsg : IpcEnvelope
    {
        public float defaultContactOffset;
        public int defaultSolverIterations;
        public int defaultSolverVelocityIterations;
        public float defaultMaxDepenetrationVelocity;
        public float maximumDeltaTime;
    }

    /// <summary>Partial update over the whitelisted <c>GameSettings</c> fields.</summary>
    [Serializable]
    public class SetSettingsMsg : IpcEnvelope
    {
        public float masterVolume, sfxVolume, engineVolume, musicVolume;
        public bool bloom, vSync, fullscreen;
        public int qualityLevel;
        public bool logTelemetry;
        public int noiseSeed;
        public int actuationDelayTicks;
        public bool spArcadeHandling, spArcadeTyreThermal;
    }

    /// <summary>Partial update over <c>ModeConfigOverride</c>'s fields, applied
    /// by name so the message does not have to mirror a class that grows.</summary>
    [Serializable]
    public class SetTuningMsg : IpcEnvelope
    {
        public string name;
        public float value;
    }

    // ══════════════════════════ server → client ══════════════════════════════

    [Serializable]
    public class WelcomeMsg : IpcEnvelope
    {
        public int version;
        public string game;
        public string unityVersion;
        public string scene;
        public bool sessionActive;
        /// <summary>True when a LAN session owns this process. Most lifecycle
        /// commands are refused while it is set.</summary>
        public bool lan;
    }

    [Serializable]
    public class AckMsg : IpcEnvelope
    {
        /// <summary>Optional human note; never parsed.</summary>
        public string note;
    }

    [Serializable]
    public class ErrMsg : IpcEnvelope
    {
        /// <summary>One of the Err* constants — this is the field to branch on.</summary>
        public string code;
        /// <summary>Human wording, including the verbatim refusal reason from
        /// <c>CarRebuilder.CanRebuild</c> when the code is rebuild_refused.</summary>
        public string message;
    }

    [Serializable]
    public class MotorInfo
    {
        public string name;
        /// <summary>Index into the actuator vector — where this motor's volts go
        /// in an <c>actuate</c> message.</summary>
        public int actuatorIndex;
        public float maxVoltage;
    }

    [Serializable]
    public class SensorInfoDto
    {
        public string name;
        /// <summary>Tof, Encoder, Motor, Camera, Suspension, Battery, Color,
        /// Rf, Mag, Bump, Led. The wire value is the C# SensorType enum name
        /// verbatim, so an appended sensor type appears here automatically.</summary>
        public string kind;
        /// <summary>Channel names this sensor publishes, already fully qualified
        /// (<c>sens/&lt;name&gt;/&lt;field&gt;</c>) so they can be handed straight
        /// back in a subscribe.</summary>
        public string[] channels;
        public int camWidth, camHeight;
    }

    [Serializable]
    public class VehicleInfo
    {
        public int id;
        public string name;
        public string designName;
        public bool isBot;
        /// <summary>Human, BotAI or Firmware — the slot's drive control.</summary>
        public string control;
        public int netSlot;
        public bool acquired;
        /// <summary>Takeover level if acquired, else empty.</summary>
        public string level;
        public int wheelCount;
        public MotorInfo[] motors;
        public SensorInfoDto[] sensors;
        public bool hasCamera;
    }

    [Serializable]
    public class VehiclesReply : IpcEnvelope
    {
        public VehicleInfo[] vehicles;
    }

    [Serializable]
    public class TrackInfo
    {
        /// <summary>Stable id to hand back to <c>load_track</c>.</summary>
        public string id;
        public string displayName;
        /// <summary>"scene" for a hand-authored scene track, "tilemap" for a
        /// generated one.</summary>
        public string kind;
        /// <summary>True when the map has a finish line, i.e. can host a race.</summary>
        public bool hasFinish;
    }

    [Serializable]
    public class TracksReply : IpcEnvelope
    {
        public TrackInfo[] tracks;
    }

    [Serializable]
    public class PresetsReply : IpcEnvelope
    {
        /// <summary>Built-in preset names.</summary>
        public string[] presets;
        /// <summary>Player-saved designs from the garage library.</summary>
        public string[] saved;
    }

    [Serializable]
    public class ChannelsReply : IpcEnvelope
    {
        public int vehicleId;
        public string[] channels;
    }

    [Serializable]
    public class SessionReply : IpcEnvelope
    {
        public bool active;
        public string scene;
        public string trackId;
        public string match;
        public int vehicleCount;
        public float simTime;
        public bool lan;
        public bool paused;
        public int physicsHz;
        public int controlHz;
    }

    [Serializable]
    public class TunableInfo
    {
        public string name;
        public float min, max, value;
    }

    [Serializable]
    public class TunablesReply : IpcEnvelope
    {
        public int vehicleId;
        public TunableInfo[] tunables;
    }

    /// <summary>Reply to get_settings. Same field names as
    /// <see cref="SetSettingsMsg"/> so a client can round-trip one into the
    /// other.</summary>
    [Serializable]
    public class SettingsReply : IpcEnvelope
    {
        public float masterVolume, sfxVolume, engineVolume, musicVolume;
        public bool bloom, vSync, fullscreen;
        public int qualityLevel;
        public bool logTelemetry;
        public int noiseSeed;
        public int actuationDelayTicks;
        public bool spArcadeHandling, spArcadeTyreThermal;
    }

    /// <summary>
    /// Reply to subscribe. <see cref="channels"/> is the resolved, ordered list,
    /// and that order IS the binary layout: telemetry frames for this vehicle
    /// carry one float per entry, in this order, after the frame's simTime. A
    /// client must read this rather than assume its request order survived —
    /// unknown names are dropped, and an empty request expands to everything.
    /// </summary>
    [Serializable]
    public class SubscribedReply : IpcEnvelope
    {
        public int vehicleId;
        public string[] channels;
        public float rateHz;
    }

    [Serializable]
    public class EventMsg : IpcEnvelope
    {
        public string kind;
        public int vehicleId;
        public string note;
    }

    // ---- world sensors (2026-08 additive) ----------------------------------

    [Serializable]
    public class WorldSensorDto
    {
        public string name;
        /// <summary>"mic", "speaker" or "beacon".</summary>
        public string kind;
        /// <summary>Fully qualified world channel names
        /// (<c>world/&lt;kind&gt;/&lt;name&gt;/&lt;field&gt;</c>), ready to hand
        /// back in a <c>subscribe_world</c>.</summary>
        public string[] channels;
        /// <summary>World position — the ground truth a triangulation exercise
        /// checks itself against.</summary>
        public float px, py, pz;
    }

    [Serializable]
    public class WorldSensorsReply : IpcEnvelope
    {
        public WorldSensorDto[] sensors;
    }

    [Serializable]
    public class SubscribeWorldMsg : IpcEnvelope
    {
        /// <summary>World channel names. Empty or null = every world channel,
        /// in registration order.</summary>
        public string[] channels;
        /// <summary>Requested frames per second; clamped to the world rate
        /// (50 Hz). 0 takes the world rate.</summary>
        public float rateHz;
    }
}
