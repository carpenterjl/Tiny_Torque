using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The handle an AUTHORED flight scene offers <see cref="RcPlaneBootstrap"/>.
    /// When this component sits beside the bootstrap with its references filled,
    /// the bootstrap adopts the scene instead of building one — the same contract
    /// <c>TrackBootstrap</c> has with <c>SceneTrackDescriptor</c>, and for the
    /// same reason: the scene you adjust in the inspector is the scene you test.
    ///
    /// Every field is a plain serialized reference, filled once by the editor
    /// scene builder (<c>RcPlaneSceneBuilder</c>). The bootstrap's adopt path
    /// fills NULLS only and never overwrites — whatever a hand set in the
    /// inspector is the truth. <see cref="sasTarget"/> is the serialized answer
    /// to what used to be <c>GameObject.Find("Pylons")</c> at Play time.
    /// </summary>
    public sealed class FlightSceneDescriptor : MonoBehaviour
    {
        public PlaneVehicle plane;
        public PlaneInput input;
        public SimulationRunner runner;
        public FlightTelemetry telemetry;
        public FlightCameraRig cameraRig;
        public SasController sas;

        [Tooltip("What SAS Target mode and the navball's target marker point at. "
                 + "Authored (the far pylon by default) instead of found by name.")]
        public Transform sasTarget;

        [Tooltip("Optional spawn override. Hand launches keep the bootstrap's "
                 + "launchAltitude; only the ground position and heading come "
                 + "from here.")]
        public Transform spawnPoint;

        /// <summary>Everything the bootstrap cannot fall back from. A missing
        /// optional (sasTarget, spawnPoint) degrades gracefully; a missing
        /// required reference means someone deleted part of the authored scene,
        /// and the bootstrap says so and rebuilds from code.</summary>
        public bool IsComplete =>
            plane != null && input != null && runner != null
            && telemetry != null && cameraRig != null && sas != null;
    }
}
