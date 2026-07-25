using System;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// One car's lap state: lap count, running/last/best times, and ordered
    /// checkpoint progress. Serializable so session snapshots can save/restore
    /// it directly. BestLap uses a -1 sentinel instead of Infinity (JsonUtility
    /// can't round-trip Infinity).
    /// </summary>
    [Serializable]
    public sealed class LapTracker
    {
        public int LapCount;
        public float CurrentLap;
        public float LastLap;
        public float BestLap = -1f;    // -1 = no lap completed yet
        public int NextCheckpoint;
        public bool Armed;

        [NonSerialized] public float lastCrossTime = -999f;

        public bool HasBest => BestLap >= 0f;

        public LapTracker Clone() => JsonUtility.FromJson<LapTracker>(JsonUtility.ToJson(this));
    }
}
