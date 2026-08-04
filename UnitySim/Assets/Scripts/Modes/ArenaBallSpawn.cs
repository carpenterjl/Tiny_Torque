using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// The centre spot: where the ball starts a soccer match and where it goes
    /// back to after a goal or an escape.
    ///
    /// Optional. With none in the scene the ball is kicked off from
    /// <c>ArenaNav.Centre</c> — the middle of the playfield — exactly as it
    /// always has been. This exists for the pitch whose middle is not the middle
    /// of its floor.
    ///
    /// The authored height is taken as given rather than dropped onto the
    /// surface, because "how high does the ball start" is a thing an author can
    /// legitimately want to set (a ball spawned at head height plays quite
    /// differently), and a marker that silently snapped to the floor would take
    /// that away with no way to ask for it back.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="ArenaGoalMarker"/>.</remarks>
    [AddComponentMenu("Tiny Torque/Arena/Ball Spawn")]
    [DisallowMultipleComponent]
    public sealed class ArenaBallSpawn : MonoBehaviour
    {
        /// <summary>The first one in the scene, or null. First rather than a
        /// complaint about a second: a match with two centre spots should still
        /// kick off, and the template validator is where duplicates are named.</summary>
        public static ArenaBallSpawn Find() => FindFirstObjectByType<ArenaBallSpawn>();

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.94f, 0.95f, 0.97f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, ModeConfig.BallRadius);
            // A stalk down to the floor plane below, so the spawn height is
            // readable in the Scene view instead of being a sphere in space.
            Gizmos.DrawLine(transform.position,
                            transform.position - Vector3.up * ModeConfig.BallRadius * 3f);
        }
    }
}
