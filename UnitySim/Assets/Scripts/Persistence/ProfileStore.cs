using System;
using System.Collections.Generic;

namespace AIHWSim.Persistence
{
    [Serializable]
    public sealed class TrackRecord
    {
        public string trackName = "";
        public float bestLap = -1f;   // -1 sentinel = none (JsonUtility can't round-trip Infinity)
    }

    /// <summary>One player's lifetime driving stats and per-track bests.</summary>
    [Serializable]
    public sealed class PlayerProfile
    {
        public string name = "";
        public int totalLaps;
        public float totalDriveTime;  // sum of completed lap times (s)
        public List<TrackRecord> records = new List<TrackRecord>();

        public TrackRecord RecordFor(string trackName)
        {
            foreach (var r in records)
                if (r.trackName == trackName) return r;
            var nr = new TrackRecord { trackName = trackName };
            records.Add(nr);
            return nr;
        }
    }

    [Serializable]
    public sealed class ProfileList   // JsonUtility needs a wrapper for top-level lists
    {
        public List<PlayerProfile> profiles = new List<PlayerProfile>();
    }

    /// <summary>Persistent per-player records in Saves/profiles.json.</summary>
    public static class ProfileStore
    {
        private const string FileName = "profiles.json";
        private static ProfileList _list;

        private static ProfileList List => _list ??= SaveSystem.LoadJson<ProfileList>(FileName) ?? new ProfileList();

        /// <summary>Get (or create) the profile with this name.</summary>
        public static PlayerProfile Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Player 1";
            foreach (var p in List.profiles)
                if (p.name == name) return p;
            var np = new PlayerProfile { name = name };
            List.profiles.Add(np);
            return np;
        }

        /// <summary>
        /// Log a completed lap into the profile; saves immediately.
        /// Returns true when the lap is a new personal best for the track.
        /// </summary>
        public static bool RecordLap(string profileName, string trackName, float lapTime)
        {
            var profile = Get(profileName);
            profile.totalLaps++;
            profile.totalDriveTime += lapTime;
            var rec = profile.RecordFor(trackName);
            bool newBest = rec.bestLap < 0f || lapTime < rec.bestLap;
            if (newBest) rec.bestLap = lapTime;
            SaveSystem.SaveJson(FileName, List);
            return newBest;
        }
    }
}
