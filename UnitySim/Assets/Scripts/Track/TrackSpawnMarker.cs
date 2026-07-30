using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Where one car starts. <b>One marker is one grid slot</b>:
    /// <see cref="gridOrder"/> 0 is player 1, 1 is player 2, and so on, which is
    /// why these are named "Player N Spawn" in the hierarchy rather than all being
    /// called "Spawn".
    ///
    /// <b>How many you need.</b> One is enough — with a single marker the game lays
    /// the rest of the field out from it in a staggered two-wide row, exactly as a
    /// tile map does, so a FreeRoam scene never has to author a grid it does not
    /// care about. Author more and they win: slot <c>i</c> starts on the marker
    /// with <c>gridOrder == i</c>, which is how you get a real start grid whose
    /// spacing follows the road instead of a straight line projected backwards from
    /// the pole position. Author fewer than the field size and the remainder falls
    /// back to the procedural row behind the last authored slot.
    ///
    /// <see cref="team"/> is only read by an arena mode pooling spawns by end, and
    /// arena scene tracks are refused today — it is here so the data survives when
    /// they are not.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="TrackMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Track/Spawn Marker")]
    public sealed class TrackSpawnMarker : TrackMarker
    {
        [Tooltip("Team index, mirroring PlacedItem.order on tile maps. 0 everywhere " +
                 "outside a team arena.")]
        public int team;

        /// <summary>
        /// Grid slot, zero-based: 0 is player 1. Dense <c>0..n-1</c> across the
        /// scene, which "Renumber spawns" enforces and the validator checks.
        ///
        /// Explicit rather than implied by sibling order, because a marker parented
        /// to the piece of geometry it sits on has a sibling index that says nothing
        /// about the grid.
        /// </summary>
        [Tooltip("Grid slot, zero-based. 0 = player 1. Must be a dense 0..n-1 run.")]
        public int gridOrder;

        /// <summary>Hierarchy name for grid slot <paramref name="gridOrder"/>.
        /// One place, so the setup tool, the renumberer and the validator cannot
        /// disagree about what a spawn is called.</summary>
        public static string NameFor(int gridOrder) => $"Player {gridOrder + 1} Spawn";

        protected override Color GizmoColor => new Color(0.4f, 1f, 0.5f);
        protected override float GizmoHalfWidth => 0.22f;   // roughly a car half-width
    }
}
