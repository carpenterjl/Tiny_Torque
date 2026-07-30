using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Start/finish line. Exactly one per Circuit scene; none on a FreeRoam scene.
    /// Its trigger becomes the scene's <see cref="LapTimer"/>, so without it there
    /// is no lap timing, no race, and <c>BotPath</c> has nothing to close a loop on.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="TrackMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Track/Finish Marker")]
    public sealed class TrackFinishMarker : TrackMarker
    {
        /// <summary>Gate width in metres. Default matches the tile map's finish
        /// gate (TrackFactory builds it at 1.6 m) so a scene circuit and a tile
        /// circuit feel the same to drive through.</summary>
        [Tooltip("Gate width in metres. Must span the drivable road or cars will " +
                 "miss the line entirely.")]
        public float gateWidth = 1.6f;

        /// <summary>Minimum seconds between two counted crossings, passed to
        /// LapTimer. Guards the car that noses back over the line.</summary>
        public float minLapTime = 3f;

        protected override Color GizmoColor => new Color(1f, 0.95f, 0.3f);
        protected override float GizmoHalfWidth => gateWidth * 0.5f;
    }
}
