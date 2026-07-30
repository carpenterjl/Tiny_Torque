using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Base for the three things a hand-authored track scene has to declare that
    /// its geometry cannot: where a lap starts, in what order it must be driven,
    /// and where cars appear. On a tile map these are <c>PlacedItem</c>s carrying
    /// an <c>ItemBehavior</c>; in a scene they are GameObjects, because a scene
    /// author needs a transform gizmo, per-object Undo and the ability to park a
    /// marker as a child of the geometry it belongs to.
    ///
    /// EVERY marker's local +Z is the direction cars travel. A gate spans its own
    /// local X, so a marker rotated to "look nice" silently makes a gate cars can
    /// pass beside — this is the single most common authoring error, which is why
    /// the gizmos draw the arrow and the validator checks the heading against the
    /// local track tangent.
    ///
    /// <b>One MonoBehaviour per file, filename == class name.</b> These three used
    /// to share a single <c>SceneTrackMarkers.cs</c>, which cost a day: Unity
    /// creates exactly one <c>MonoScript</c> asset per .cs file, named after the
    /// file, so the classes that did not match the filename had no script asset to
    /// reference. Adding one in the editor still "worked", but the scene serialized
    /// <c>m_Script</c> as a fileID pointing at a MonoScript stub embedded in the
    /// scene itself instead of <c>{fileID: 11500000, guid: …}</c> — a component
    /// that reloads as Missing Script and is invisible to
    /// <c>GetComponentsInChildren</c>. Runtime-only components elsewhere in this
    /// project break the same rule harmlessly, because a scene built by
    /// <c>AddComponent</c> at play time is never serialized. Anything authored into
    /// a saved scene must not.
    /// </summary>
    public abstract class TrackMarker : MonoBehaviour
    {
        /// <summary>Gizmo colour for this marker kind.</summary>
        protected abstract Color GizmoColor { get; }

        /// <summary>Half-extent across local X, for the gizmo's gate outline.</summary>
        protected virtual float GizmoHalfWidth => 0.5f;

        protected virtual void OnDrawGizmos() => Draw(GizmoColor * new Color(1f, 1f, 1f, 0.6f));

        protected virtual void OnDrawGizmosSelected() => Draw(GizmoColor);

        private void Draw(Color c)
        {
            Gizmos.color = c;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);

            // The gate volume, matching what SceneTrackBuilder will actually
            // create: centred half a metre up, 1 m tall, 0.25 m thick.
            Gizmos.DrawWireCube(new Vector3(0f, 0.5f, 0f),
                new Vector3(GizmoHalfWidth * 2f, 1f, 0.25f));

            // Travel direction. Drawn from the gate centre so it reads as "cars
            // go this way through here", not "this object faces there".
            var tip = new Vector3(0f, 0.5f, 0.9f);
            Gizmos.DrawLine(new Vector3(0f, 0.5f, 0f), tip);
            Gizmos.DrawLine(tip, tip + new Vector3(0.12f, 0f, -0.18f));
            Gizmos.DrawLine(tip, tip + new Vector3(-0.12f, 0f, -0.18f));

            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
