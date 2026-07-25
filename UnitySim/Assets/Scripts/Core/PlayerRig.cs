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
    }
}
