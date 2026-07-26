using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// One discrete arcade happening. Deliberately a single type shared by two
    /// consumers: the LAN layer broadcasts it so clients can spawn visuals and
    /// play hit effects, and the (deferred) replay recorder logs the same stream
    /// as its highlight index. Stamped with <see cref="ArcadeDirector.Clock"/>,
    /// not Time.time, so pauses and restarts don't corrupt the timeline.
    /// APPEND-ONLY — the kind goes on the wire as a byte.
    /// </summary>
    public enum ArcadeEventKind : byte
    {
        ItemGranted = 0,
        ItemUsed = 1,
        MissileFired = 2,
        MissileHit = 3,
        MissileExpired = 4,
        BananaDropped = 5,
        BananaHit = 6,
        BananaExpired = 7,
        BoostStarted = 8,
        ShieldBlocked = 9,
        OffTrackWarning = 10,
        OffTrackPenalty = 11,
        Finished = 12,
        Wrecked = 13,       // missile hit: destroyed, about to be recovered
        Recovered = 14,     // lifted back onto the racing line
    }

    public struct ArcadeEvent
    {
        public float t;                 // ArcadeDirector.Clock at the moment it happened
        public ArcadeEventKind kind;
        public int srcSlot;             // who caused it (-1 = nobody)
        public int dstSlot;             // who it happened to (-1 = nobody)
        public ItemKind item;
        public int objId;               // projectile/banana instance id (0 = n/a)
        public Vector3 pos;
        public Quaternion rot;
    }
}
