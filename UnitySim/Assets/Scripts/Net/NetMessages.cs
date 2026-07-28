using System;
using Unity.Netcode;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// LAN message catalog. Low-rate control messages are JsonUtility payloads
    /// (versionable, matches the codebase idiom); the high-rate streams (client
    /// input, client own-state, host state) are hand-packed binary. All named
    /// messages are prefixed "aihw.".
    ///
    /// Protocol 4 made the session OWNER-AUTHORITATIVE for each player's own
    /// car: a client simulates its own car and streams the result up
    /// (<see cref="OwnState"/>), the host follows it kinematically and relays it
    /// back out in <see cref="State"/>. <see cref="Input"/> survives because the
    /// host still needs the use-item and respawn edges, and because a silent
    /// input stream is the dead-man signal.
    ///
    /// Protocol 5 added the area hazards: smoke and oil ride the projectile
    /// stream (<see cref="NetPack.ProjSmoke"/>), <see cref="ArcEffect"/> widened
    /// to a ushort to fit the slick flag, and one byte per racer carries the
    /// blind's remaining time.
    ///
    /// Protocol 6 is drift visibility. Three spare <see cref="ArcEffect"/> bits
    /// carry drifting + tier down to every client; four spare
    /// <see cref="OwnStateMsg"/> flag bits carry drifting/tier/mini-turbo up
    /// from the owner, so a client's slide and its payout light up on every
    /// machine. No field moved and no byte was added — the bump exists because
    /// a v5 host would silently never show a v6 client's drift (and vice
    /// versa), and mixed-cosmetics sessions are exactly what the equality
    /// check is for.
    /// </summary>
    public static class NetMsg
    {
        public const string Hello = "aihw.hello";            // C→H  reliable-frag
        public const string Welcome = "aihw.welcome";        // H→C  reliable-frag
        public const string Roster = "aihw.roster";          // H→all reliable-frag
        public const string Ready = "aihw.ready";            // C→H  reliable
        public const string Map = "aihw.map";                // H→all reliable-frag
        public const string RaceStart = "aihw.race_start";   // H→all reliable
        public const string Lap = "aihw.lap";                // H→all reliable
        public const string RaceEnd = "aihw.race_end";       // H→all reliable
        public const string SessionState = "aihw.session";   // H→all reliable
        public const string Input = "aihw.input";            // C→H  unreliable-seq 60 Hz
        public const string OwnState = "aihw.ownstate";      // C→H  unreliable-seq 60 Hz
        public const string State = "aihw.state";            // H→all unreliable-seq 60 Hz
        public const string ArcSync = "aihw.arc_sync";       // H→all unreliable-seq 15 Hz
        public const string ArcEvt = "aihw.arc_evt";         // H→all reliable
        public const string ArcFx = "aihw.arc_fx";           // H→one   reliable
    }

    // ---- JSON control payloads ------------------------------------------------

    [Serializable]
    public class HelloMsg
    {
        public int ver;
        public string name = "";
        public string vehicleJson = "";   // "" = stock default design
        // The joiner's arcade-assist prefs — applied host-side to their car
        // (the host simulates everyone).
        public float aSteer, aStab, aTrac, aAbs;
        // v10: the joiner's progression level, purely for the roster badge.
        public int level = 1;
    }

    [Serializable]
    public class RosterEntry
    {
        public int slot;
        public string name = "";
        public string vehicleJson = "";
        public int level = 1;             // v10: shown as "Lv N" in the roster
    }

    [Serializable]
    public class RosterMsg
    {
        public RosterEntry[] entries = Array.Empty<RosterEntry>();
    }

    [Serializable]
    public class WelcomeMsg
    {
        public int yourSlot;
        public string trackJson = "";     // "" = classic oval
        public int state;                 // NetSession.LanState
        public int targetLaps;
        public RosterEntry[] roster = Array.Empty<RosterEntry>();
        // Arcade rules are the HOST's: a joiner must know them before it builds
        // the scene, because they decide whether it constructs a mirroring
        // ArcadeDirector at all.
        public bool arcade;
        public bool trackLimits;
        public bool arcadeHandling = true;
    }

    [Serializable]
    public class MapMsg
    {
        public string trackJson = "";
    }

    [Serializable]
    public class GridPose
    {
        public int slot;
        public Vector3 pos;
        public Quaternion rot;
    }

    [Serializable]
    public class RaceStartMsg
    {
        public int laps;
        public float countdownSec = 3f;
        public GridPose[] poses = Array.Empty<GridPose>();
    }

    [Serializable]
    public class LapMsg
    {
        public int slot;
        public int lapCount;
        public float lastLap;
        public float bestLap = -1f;
        public int cp;
        public int cpTotal;
    }

    [Serializable]
    public class ResultRow
    {
        public int slot;
        public int place;              // 0 = DNF
        public string name = "";
        public float totalTime;
        public float bestLap = -1f;
    }

    [Serializable]
    public class RaceEndMsg
    {
        public ResultRow[] rows = Array.Empty<ResultRow>();
    }

    [Serializable]
    public class SessionStateMsg
    {
        public int state;              // LanState
        public int targetLaps;
        public float countdownRemaining;
        public bool arcade;
        public bool trackLimits;
        public bool arcadeHandling = true;
    }

    /// <summary>
    /// One arcade happening, mirrored to every machine. Carries exactly the
    /// fields of <see cref="Arcade.ArcadeEvent"/> that survive the wire, so a
    /// client can re-raise it into its local director and the audio and feedback
    /// layers work unchanged — they are subscribers, and they cannot tell an
    /// event the host decided from one this machine decided.
    /// </summary>
    [Serializable]
    public class ArcEvtMsg
    {
        public int kind;
        public int src = -1;
        public int dst = -1;
        public int item;
        public int objId;
        public Vector3 pos;
        public Quaternion rot = Quaternion.identity;
    }

    /// <summary>
    /// One arcade effect's PHYSICS, sent to the machine that owns the victim's
    /// car. The host still rolls every random number — the spin direction, the
    /// wreck tumble, the recovery pose — and ships the results here, so the
    /// owner replays the host's decision rather than making its own. Without
    /// this an owner would spin the wrong way and land somewhere else.
    /// </summary>
    [Serializable]
    public class ArcFxMsg
    {
        public const int KindSpin = 1;
        public const int KindWreck = 2;
        public const int KindRecover = 3;

        public int kind;
        public int slot;
        public Vector3 impulse;          // ArcadeImpulse, world-space
        public Vector3 torqueImpulse;    // wreck tumble
        public float spinTorqueSigned;   // signed yaw torque held for the spin's duration
        public Vector3 pos;              // recovery pose (KindRecover)
        public Quaternion rot = Quaternion.identity;
    }

    // ---- binary 60 Hz streams --------------------------------------------------

    /// <summary>
    /// One car's streamed pose/motion. Host → clients for every car; client →
    /// host for the client's own car (which the client simulates).
    ///
    /// <c>carEpoch</c> is per car and bumped by whoever teleports it — respawn,
    /// wreck recovery, race grid. Receivers snap and flush their interpolation
    /// buffer when it changes, so a teleport reads as a teleport rather than a
    /// 120 ms streak across the map. The stream header carries a separate global
    /// epoch for scene-wide events (map change, scene rebuild).
    /// </summary>
    public struct CarState
    {
        public int slot;
        public byte carEpoch;
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public Vector3 angVel;      // lets a receiver extrapolate rotation, not just position
        public float steerDeg;
        public float wheelRadPerSec;
        /// <summary>Bit 1 = horn sounding (v10). A flags byte rather than a
        /// bool so the next boolean rides free instead of costing another
        /// protocol bump.</summary>
        public byte flags;

        public const byte FlagHorn = 1;
        public bool HornOn => (flags & FlagHorn) != 0;
    }

    /// <summary>
    /// A client's own-car state (client → host). Same payload as
    /// <see cref="CarState"/> minus the slot (the host maps sender→slot) plus
    /// the owner's clock and its own track-limit verdict — the host's copy of
    /// this car is kinematic, so its wheels never touch the surface map.
    /// </summary>
    public struct OwnStateMsg
    {
        public byte carEpoch;
        public float ownerTime;
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public Vector3 angVel;
        public float steerDeg;
        public float wheelRadPerSec;
        public bool penalized;
        public bool warned;
        /// <summary>Committed to a slide this instant. The host does not
        /// simulate this car, so without these three fields a client's drift
        /// is invisible to the whole session — this uplink is the entire v6
        /// reason-to-exist.</summary>
        public bool drifting;
        /// <summary>Drift charge tier 0-3 while drifting (the spark colour).</summary>
        public int driftTier;
        /// <summary>Mini-turbo payout currently burning (Clock &lt;
        /// driftBoostUntil). Rides up so the host can relay it as
        /// <see cref="ArcEffect.Boost"/> like any boost it awarded itself.</summary>
        public bool miniTurbo;
        /// <summary>Horn sounding right now (v10). Held-state, not an edge —
        /// the receiver gates a loop with it.</summary>
        public bool hornOn;
    }

    public struct InputState
    {
        public float throttle, steer, brake;
        public bool handbrake;
        public bool respawnEdge;
        public bool useItemEdge;   // arcade: fire the held power-up
        public bool hornHeld;      // v10: hold-to-sound, mirrored on the host rig
    }

    /// <summary>
    /// Active arcade effects on one car, as a wire ushort.
    ///
    /// Protocol 5 widened this from a byte, whose eight bits were all spoken for.
    /// The extra byte costs ~120 B/s at a full grid and buys eight bits of
    /// headroom back — cheap next to discovering later that the next effect has
    /// nowhere to go.
    /// </summary>
    [Flags]
    public enum ArcEffect : ushort
    {
        None = 0,
        Rolling = 1,
        Boost = 2,
        Shield = 4,
        Spun = 8,
        Wrecked = 16,
        Penalized = 32,
        Warned = 64,
        Incoming = 128,
        /// <summary>Standing in (or just out of) an oil slick. A boolean is enough
        /// here — the effect is a grip multiplier that is either applied this
        /// frame or not, and <c>SlickLingerSeconds</c> is short enough that a
        /// stale hold costs nothing. Blindness is NOT a flag for the opposite
        /// reason; see <see cref="ArcRacerState.blindLeftDs"/>.</summary>
        Slicked = 256,
        /// <summary>Committed to a slide right now. Owner-truth: for a remote
        /// car the host learned this from the owner's own-state uplink. A flag,
        /// not a duration — smoke and sparks are on/off visuals, and the
        /// receiver's 0.25 s liveness hold bridges a lost packet without a
        /// fade envelope to get wrong.</summary>
        Drifting = 512,
        /// <summary>Low bit of the 2-bit drift charge tier (0 = still charging,
        /// 1-3 = mini-turbo tiers, the spark colours). Meaningful only while
        /// <see cref="Drifting"/> is set. Encode <c>(tier &amp; 3) &lt;&lt; 10</c>,
        /// decode <c>((int)fx &gt;&gt; 10) &amp; 3</c>.</summary>
        DriftTier1 = 1024,
        /// <summary>High bit of the drift tier field; see <see cref="DriftTier1"/>.</summary>
        DriftTier2 = 2048,
    }

    /// <summary>One car's arcade state (host → clients).</summary>
    public struct ArcRacerState
    {
        public int slot;
        public int held;         // ItemKind
        public int charges;
        public int rollFace;     // ItemKind currently displayed while the roulette spins
        public ArcEffect effects;
        public int position;     // 1 = leader
        public int points;
        /// <summary>
        /// Smoke blindness left, in 20ths of a second (0 = not blinded).
        ///
        /// A duration rather than a flag, because the receiver has to rebuild an
        /// ENVELOPE and not just a boolean: <c>ArcadeFeedback.DrawBlind</c> ramps
        /// in, holds, and fades out over the last <c>BlindFadeSeconds</c> of the
        /// deadline. Held as a bit and re-armed to "now + one sync period" like
        /// the other effects, that fade term would read 0.25/0.9 forever and peg a
        /// client's green wash at a third of the alpha the host is drawing — an
        /// effect that looks like it is working while not costing the corner it
        /// exists to cost. One byte buys the exact same envelope on both machines.
        /// </summary>
        public byte blindLeftDs;
    }

    /// <summary>One live projectile or dropped area hazard's pose (host → clients).</summary>
    public struct ArcProjState
    {
        public int objId;
        public int kind;         // NetPack.Proj*
        public Vector3 pos;
        public Quaternion rot;
    }

    public static class NetPack
    {
        // APPEND-ONLY — the kind goes on the wire as a byte.
        public const int ProjMissile = 1;
        public const int ProjBanana = 2;
        // Area hazards ride the projectile stream rather than the event stream
        // because their EXISTENCE is state, not a happening: a cloud lives for
        // nine seconds, so streaming it self-heals a lost packet and a late join,
        // where a one-shot "dropped" event would leave a client staring through
        // a hazard the host is still catching it with.
        public const int ProjSmoke = 3;
        public const int ProjSlick = 4;

        /// <summary>Seconds → the wire's 20ths-of-a-second byte (0 … 12.75 s).</summary>
        public static byte PackDs(float seconds) =>
            (byte)Mathf.Clamp(Mathf.RoundToInt(seconds * 20f), 0, 255);

        public static float UnpackDs(byte ds) => ds * 0.05f;

        public static void WriteInput(FastBufferWriter w, in InputState s)
        {
            w.WriteValueSafe(s.throttle);
            w.WriteValueSafe(s.steer);
            w.WriteValueSafe(s.brake);
            byte flags = (byte)((s.handbrake ? 1 : 0) |
                                (s.respawnEdge ? 2 : 0) |
                                (s.useItemEdge ? 4 : 0) |
                                (s.hornHeld ? 8 : 0));
            w.WriteValueSafe(flags);
        }

        public static InputState ReadInput(FastBufferReader r)
        {
            var s = new InputState();
            r.ReadValueSafe(out s.throttle);
            r.ReadValueSafe(out s.steer);
            r.ReadValueSafe(out s.brake);
            r.ReadValueSafe(out byte flags);
            s.handbrake = (flags & 1) != 0;
            s.respawnEdge = (flags & 2) != 0;
            s.useItemEdge = (flags & 4) != 0;
            s.hornHeld = (flags & 8) != 0;
            return s;
        }

        // ---- arcade sync ------------------------------------------------------
        //
        // Three blocks, all small: inventories/effects per car, projectile poses,
        // and a bitmask of which item boxes are currently up.
        //
        // Projectiles are STREAMED rather than re-simulated on each client. A
        // client could integrate the same homing maths, but it would be running
        // it against interpolated ghost positions that are ~120 ms behind the
        // host's, so its missile would chase a car that is not where the host
        // says it is — and the one thing a missile must agree on across machines
        // is who it hit. At four players this is a dozen objects at 15 Hz.

        public static void WriteArcRacer(FastBufferWriter w, in ArcRacerState s)
        {
            w.WriteValueSafe((byte)s.slot);
            w.WriteValueSafe((byte)s.held);
            w.WriteValueSafe((byte)Mathf.Clamp(s.charges, 0, 255));
            w.WriteValueSafe((byte)s.rollFace);
            w.WriteValueSafe((ushort)s.effects);
            w.WriteValueSafe((byte)Mathf.Clamp(s.position, 0, 255));
            w.WriteValueSafe((ushort)Mathf.Clamp(s.points, 0, 65535));
            w.WriteValueSafe(s.blindLeftDs);
        }

        public static ArcRacerState ReadArcRacer(FastBufferReader r)
        {
            var s = new ArcRacerState();
            r.ReadValueSafe(out byte slot); s.slot = slot;
            r.ReadValueSafe(out byte held); s.held = held;
            r.ReadValueSafe(out byte charges); s.charges = charges;
            r.ReadValueSafe(out byte face); s.rollFace = face;
            r.ReadValueSafe(out ushort fx); s.effects = (ArcEffect)fx;
            r.ReadValueSafe(out byte pos); s.position = pos;
            r.ReadValueSafe(out ushort pts); s.points = pts;
            r.ReadValueSafe(out s.blindLeftDs);
            return s;
        }

        public static void WriteArcProj(FastBufferWriter w, in ArcProjState p)
        {
            w.WriteValueSafe((ushort)p.objId);
            w.WriteValueSafe((byte)p.kind);
            w.WriteValueSafe(p.pos);
            w.WriteValueSafe(p.rot);
        }

        public static ArcProjState ReadArcProj(FastBufferReader r)
        {
            var p = new ArcProjState();
            r.ReadValueSafe(out ushort id); p.objId = id;
            r.ReadValueSafe(out byte kind); p.kind = kind;
            r.ReadValueSafe(out p.pos);
            r.ReadValueSafe(out p.rot);
            return p;
        }

        public static void WriteStateHeader(FastBufferWriter w, byte epoch, float hostTime, byte carCount)
        {
            w.WriteValueSafe(epoch);
            w.WriteValueSafe(hostTime);
            w.WriteValueSafe(carCount);
        }

        public static void WriteCarState(FastBufferWriter w, in CarState c)
        {
            w.WriteValueSafe((byte)c.slot);
            w.WriteValueSafe(c.carEpoch);
            w.WriteValueSafe(c.pos);
            w.WriteValueSafe(c.rot);
            w.WriteValueSafe(c.vel);
            w.WriteValueSafe(c.angVel);
            w.WriteValueSafe(c.steerDeg);
            w.WriteValueSafe(c.wheelRadPerSec);
            w.WriteValueSafe(c.flags);    // appended in v10 (bit 1 = horn)
        }

        public static void WriteOwnState(FastBufferWriter w, in OwnStateMsg s)
        {
            w.WriteValueSafe(s.carEpoch);
            w.WriteValueSafe(s.ownerTime);
            w.WriteValueSafe(s.pos);
            w.WriteValueSafe(s.rot);
            w.WriteValueSafe(s.vel);
            w.WriteValueSafe(s.angVel);
            w.WriteValueSafe(s.steerDeg);
            w.WriteValueSafe(s.wheelRadPerSec);
            byte flags = (byte)((s.penalized ? 1 : 0) |
                                (s.warned ? 2 : 0) |
                                (s.drifting ? 4 : 0) |
                                ((s.driftTier & 3) << 3) |
                                (s.miniTurbo ? 32 : 0) |
                                (s.hornOn ? 64 : 0));
            w.WriteValueSafe(flags);
        }

        public static OwnStateMsg ReadOwnState(FastBufferReader r)
        {
            var s = new OwnStateMsg();
            r.ReadValueSafe(out s.carEpoch);
            r.ReadValueSafe(out s.ownerTime);
            r.ReadValueSafe(out s.pos);
            r.ReadValueSafe(out s.rot);
            r.ReadValueSafe(out s.vel);
            r.ReadValueSafe(out s.angVel);
            r.ReadValueSafe(out s.steerDeg);
            r.ReadValueSafe(out s.wheelRadPerSec);
            r.ReadValueSafe(out byte flags);
            s.penalized = (flags & 1) != 0;
            s.warned = (flags & 2) != 0;
            s.drifting = (flags & 4) != 0;
            s.driftTier = (flags >> 3) & 3;
            s.miniTurbo = (flags & 32) != 0;
            s.hornOn = (flags & 64) != 0;
            return s;
        }

        public static void ReadStateHeader(FastBufferReader r, out byte epoch, out float hostTime, out byte carCount)
        {
            r.ReadValueSafe(out epoch);
            r.ReadValueSafe(out hostTime);
            r.ReadValueSafe(out carCount);
        }

        public static CarState ReadCarState(FastBufferReader r)
        {
            var c = new CarState();
            r.ReadValueSafe(out byte slot);
            c.slot = slot;
            r.ReadValueSafe(out c.carEpoch);
            r.ReadValueSafe(out c.pos);
            r.ReadValueSafe(out c.rot);
            r.ReadValueSafe(out c.vel);
            r.ReadValueSafe(out c.angVel);
            r.ReadValueSafe(out c.steerDeg);
            r.ReadValueSafe(out c.wheelRadPerSec);
            r.ReadValueSafe(out c.flags);
            return c;
        }
    }
}
