using System;
using Unity.Netcode;
using UnityEngine;

namespace AIHWSim.Net
{
    /// <summary>
    /// LAN message catalog. Low-rate control messages are JsonUtility payloads
    /// (versionable, matches the codebase idiom); the two 30 Hz streams
    /// (client input, host state) are hand-packed binary. All named messages
    /// are prefixed "aihw.".
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
        public const string Input = "aihw.input";            // C→H  unreliable-seq 30 Hz
        public const string State = "aihw.state";            // H→all unreliable-seq 30 Hz
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
    }

    [Serializable]
    public class RosterEntry
    {
        public int slot;
        public string name = "";
        public string vehicleJson = "";
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
    }

    // ---- binary 30 Hz streams --------------------------------------------------

    /// <summary>One car's streamed pose/motion (host → clients).</summary>
    public struct CarState
    {
        public int slot;
        public Vector3 pos;
        public Quaternion rot;
        public Vector3 vel;
        public float steerDeg;
        public float wheelRadPerSec;
    }

    public struct InputState
    {
        public float throttle, steer, brake;
        public bool handbrake;
        public bool respawnEdge;
        public bool useItemEdge;   // arcade: fire the held power-up
    }

    public static class NetPack
    {
        public static void WriteInput(FastBufferWriter w, in InputState s)
        {
            w.WriteValueSafe(s.throttle);
            w.WriteValueSafe(s.steer);
            w.WriteValueSafe(s.brake);
            byte flags = (byte)((s.handbrake ? 1 : 0) | (s.respawnEdge ? 2 : 0));
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
            return s;
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
            w.WriteValueSafe(c.pos);
            w.WriteValueSafe(c.rot);
            w.WriteValueSafe(c.vel);
            w.WriteValueSafe(c.steerDeg);
            w.WriteValueSafe(c.wheelRadPerSec);
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
            r.ReadValueSafe(out c.pos);
            r.ReadValueSafe(out c.rot);
            r.ReadValueSafe(out c.vel);
            r.ReadValueSafe(out c.steerDeg);
            r.ReadValueSafe(out c.wheelRadPerSec);
            return c;
        }
    }
}
