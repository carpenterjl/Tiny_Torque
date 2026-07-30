using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// One ordered gate on the lap. <see cref="order"/> MUST form a dense
    /// <c>0..n-1</c> run across the scene: <c>LapTimer.NotifyCheckpoint</c> only
    /// advances when the index equals the tracker's next expected one, and a lap
    /// is refused until every checkpoint has been hit — so a single gap makes the
    /// track permanently un-lappable, with no error anywhere. The Track Studio
    /// window renumbers densely and the validator fails on a gap.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="TrackMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Track/Checkpoint Marker")]
    public sealed class TrackCheckpointMarker : TrackMarker
    {
        [Tooltip("Sequence position. Must be a dense 0..n-1 run across the scene.")]
        public int order;

        /// <summary>Default matches the tile map's checkpoint gate (1.35 m).</summary>
        public float gateWidth = 1.35f;

        /// <summary>Hierarchy name for a checkpoint at <paramref name="order"/>, so
        /// the Scene view reads in lap order without opening each inspector.</summary>
        public static string NameFor(int order) => $"Checkpoint {order}";

        protected override Color GizmoColor => new Color(0.35f, 0.85f, 1f);
        protected override float GizmoHalfWidth => gateWidth * 0.5f;
    }
}
