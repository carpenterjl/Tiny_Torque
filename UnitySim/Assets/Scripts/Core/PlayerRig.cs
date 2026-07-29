using AIHWSim.Sensors;
using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Everything one player owns in a drive session: their car, input, sim
    /// runner, camera (with its viewport rect), and the shared lap timer.
    /// Built per <see cref="PlayerSlot"/> by TrackBootstrap.
    /// </summary>
    public sealed class PlayerRig
    {
        public PlayerSlot slot;
        public CarVehicle car;
        public CarInput input;
        public SimulationRunner runner;
        public SensorRig sensorRig;
        public Camera camera;
        public LapTimer lapTimer;   // shared scene timer; null on finish-less maps
        public int netSlot = -1;    // LAN roster slot; -1 in local sessions
        /// <summary>Arcade state, or null outside arcade sessions. The component
        /// itself lives on the car so a trigger can find it from a collider.</summary>
        public Arcade.ArcadeRacer arcade;

        /// <summary>Mini-game state (health, team, score), or null in a race.
        /// On the car for the same reason <see cref="arcade"/> is.</summary>
        public Modes.MatchRacer match;
    }
}
