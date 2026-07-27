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
        // Area hazards (smoke, oil) share one set of kinds and are told apart by
        // ArcadeEvent.item — the lifecycle is identical and only the effect differs,
        // so two more kinds per hazard would be three ways to say the same thing.
        HazardDropped = 15,
        HazardHit = 16,     // entered a hazard; fires on the rising edge only
        HazardExpired = 17,
    }

    /// <summary>
    /// The PHYSICS of one arcade effect, for a car this machine decided about
    /// but does not simulate (a LAN client's own car, seen from the host).
    ///
    /// It exists because every one of these carries a random draw — which way
    /// the spin throws you, how the wreck tumbles, where the recovery sets you
    /// down. The host rolls them once and ships the results; if the owner rolled
    /// its own, the two machines would disagree about the same hit.
    /// </summary>
    public struct ArcadeFx
    {
        public enum Kind { Spin = 1, Wreck = 2, Recover = 3 }

        public Kind kind;
        public int slot;
        public Vector3 impulse;
        public Vector3 torqueImpulse;
        public float spinTorqueSigned;
        public Vector3 pos;             // recovery pose
        public Quaternion rot;
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
