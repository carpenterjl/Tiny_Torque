using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Where a soccer goal is, authored in the scene instead of derived from the
    /// spawn ring.
    ///
    /// <b>The marker's own transform IS the goal box</b>: its position is the
    /// centre of the mouth, its +Z is the direction a shot comes from, and
    /// <see cref="halfExtents"/> is the volume a ball has to be inside to score.
    /// The posts and the crossbar are drawn from the same three numbers, so what
    /// you see in the Scene view is what the rules test.
    ///
    /// <b>Optional.</b> With no goal markers in the scene, <c>SoccerDirector</c>
    /// runs the arithmetic it always has: each goal at the centroid of that
    /// team's spawns, unrotated, sized by the shipped defaults below. That path
    /// is not a fallback bolted on for this component — it is the original code,
    /// and this marker simply supplies the same three values from a place you can
    /// drag.
    ///
    /// One class per file, like the track markers: a MonoBehaviour authored into
    /// a saved scene whose filename does not match its class serializes against a
    /// MonoScript stub and reloads as a Missing Script — invisible to
    /// <c>FindObjectsByType</c>, which is exactly how a goal would silently stop
    /// being a goal.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Arena/Goal Marker")]
    [DisallowMultipleComponent]
    public sealed class ArenaGoalMarker : MonoBehaviour
    {
        [Tooltip("Which team DEFENDS this goal. Scoring here awards the point to " +
                 "the other side — 0 is blue, 1 is orange.")]
        [Range(0, 1)] public int team;

        /// <summary>Half-width, half-height and half-depth of the scoring volume.
        /// The defaults are <c>SoccerDirector</c>'s shipped <c>GoalHalf</c>, so a
        /// freshly added marker describes the goal the game already had.</summary>
        [Tooltip("Half-extents of the scoring volume, in metres, in this marker's " +
                 "own axes. X is half the mouth width, Y half its height, Z how " +
                 "deep past the line counts as in.")]
        public Vector3 halfExtents = new Vector3(0.9f, 0.5f, 0.35f);

        /// <summary>Is a world point inside this goal? Rotation-aware and
        /// deliberately scale-blind — the box is defined by
        /// <see cref="halfExtents"/> in metres, so scaling the marker's transform
        /// must not quietly resize the rules.</summary>
        public bool Contains(Vector3 world)
        {
            Vector3 d = Quaternion.Inverse(transform.rotation) * (world - transform.position);
            return Mathf.Abs(d.x) <= halfExtents.x
                && Mathf.Abs(d.y) <= halfExtents.y
                && Mathf.Abs(d.z) <= halfExtents.z;
        }

        private void OnDrawGizmos() => Draw(0.5f);

        private void OnDrawGizmosSelected() => Draw(1f);

        private void Draw(float alpha)
        {
            var c = ModeDirector.TeamColors[Mathf.Clamp(team, 0, 1)];
            Gizmos.color = new Color(c.r, c.g, c.b, alpha);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, halfExtents * 2f);

            // The mouth, as a line across the front face, so a goal rotated to
            // face the wrong way reads as wrong at a glance rather than at 3-1.
            var front = new Vector3(0f, 0f, -halfExtents.z);
            Gizmos.DrawLine(front + new Vector3(-halfExtents.x, -halfExtents.y, 0f),
                            front + new Vector3(halfExtents.x, -halfExtents.y, 0f));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
