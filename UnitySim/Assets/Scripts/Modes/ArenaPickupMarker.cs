using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Where one derby pickup sits — a repair wrench or a bomb crate.
    ///
    /// Optional, and all-or-nothing: with no markers in the scene
    /// <c>DerbyDirector</c> scatters four repairs and four crates on its two
    /// rings exactly as it always has, and with any markers in the scene it
    /// places one pickup per marker and scatters nothing. A half-authored arena
    /// getting both the four you placed and the eight it invented is the outcome
    /// nobody wants and the hardest one to notice.
    ///
    /// The position is dropped onto the arena floor, so a marker parked roughly
    /// above the surface lands on it — the same trust <c>ArenaNav.Drop</c> places
    /// in every other thing an arena puts down.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="ArenaGoalMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Arena/Pickup Marker")]
    [DisallowMultipleComponent]
    public sealed class ArenaPickupMarker : MonoBehaviour
    {
        [Tooltip("Repair heals the car that drives through it. Mine arms a crate " +
                 "the driver can drop behind them.")]
        public ArenaPickup.Kind kind = ArenaPickup.Kind.Repair;

        private void OnDrawGizmos()
        {
            Gizmos.color = kind == ArenaPickup.Kind.Repair
                ? new Color(0.3f, 0.95f, 0.45f, 0.6f)
                : new Color(1f, 0.55f, 0.15f, 0.6f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * 0.06f,
                                new Vector3(0.16f, 0.12f, 0.16f));
        }
    }
}
