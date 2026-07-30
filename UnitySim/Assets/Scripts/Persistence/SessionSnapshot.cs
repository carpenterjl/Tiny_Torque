using System;
using System.Collections.Generic;
using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.Persistence
{
    /// <summary>One player's saved state inside a session snapshot.</summary>
    [Serializable]
    public sealed class PlayerSnapshot
    {
        public string name = "";
        public string profileId = "";
        public string vehicleJson = "";   // embedded VehicleDesign dump (survives library edits)
        public int deviceKind;            // InputDeviceKind
        public int gamepadIndex;

        public Vector3 position;
        public Quaternion rotation;
        public Vector3 linearVelocity;
        public Vector3 angularVelocity;

        public LapTracker lap = new LapTracker();
    }

    /// <summary>
    /// A saved in-progress drive session (single-player or split-screen):
    /// which track, the race target, per-player car state and lap progress, and
    /// the sim clock. Written to Saves/Snapshots/ and resumed from the main menu.
    /// </summary>
    [Serializable]
    public sealed class SessionSnapshot
    {
        public int version = 1;
        public string savedUtc = "";      // ISO-8601
        public int mode;                  // SessionMode
        public string trackName = "";     // display + records key; "" = classic oval
        public string trackJson = "";     // embedded TrackDesign dump; "" = classic oval
        /// <summary>Scene name for a hand-authored track; "" for every other source.
        /// A scene track is not data, so there is nothing to embed — it is named,
        /// and resuming means loading that scene again. Additive by design: an older
        /// snapshot deserialises this as "" and takes the trackJson path, exactly as
        /// it always did.</summary>
        public string trackScene = "";
        public int targetLaps;
        public float simTime;
        public List<PlayerSnapshot> players = new List<PlayerSnapshot>();
    }
}
