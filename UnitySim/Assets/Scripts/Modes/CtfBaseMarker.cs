using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// A team's base: where its flag lives, where it returns to, and where the
    /// other side's flag has to be carried for a capture.
    ///
    /// Optional. With no base markers, <c>CtfDirector</c> puts each base at the
    /// centroid of that team's spawns and falls back to a point offset from the
    /// arena centre when a team authored none — the arithmetic it has always
    /// used. This marker is for the arena whose bases are not where its grid is.
    ///
    /// The position is dropped onto the arena floor, so the plinth sits on the
    /// surface however roughly the marker was placed.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="ArenaGoalMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Arena/CTF Base Marker")]
    [DisallowMultipleComponent]
    public sealed class CtfBaseMarker : MonoBehaviour
    {
        [Tooltip("Which team this base belongs to. 0 is blue, 1 is orange.")]
        [Range(0, 1)] public int team;

        private void OnDrawGizmos()
        {
            var c = ModeDirector.TeamColors[Mathf.Clamp(team, 0, 1)];
            Gizmos.color = new Color(c.r, c.g, c.b, 0.6f);
            // The plinth CtfDirector draws is a 0.9 m disc; a wire sphere of that
            // radius reads as the same footprint without needing a mesh.
            Gizmos.DrawWireSphere(transform.position, 0.45f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 0.22f);
        }
    }
}
