using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Tags one grabbable handle so a pick raycast can name what it hit.
    ///
    /// Its own file because it is a <c>MonoBehaviour</c>: Unity resolves a
    /// behaviour's script asset by FILE NAME, so a second one sharing
    /// <c>TransformGizmo.cs</c> could be compiled but never added with
    /// <c>AddComponent</c>.
    /// </summary>
    public sealed class GizmoHandle : MonoBehaviour
    {
        /// <summary>0 = X, 1 = Y, 2 = Z. For a plane handle, the axis it is
        /// NORMAL to — so the YZ quad is handle 0, which is also the arrow it sits
        /// between and the ring that turns about it.</summary>
        public int axis;

        public GizmoMode mode;

        /// <summary>True for the square that slides in a plane, false for the
        /// arrow that slides along an axis.</summary>
        public bool plane;

        /// <summary>True for the one handle that scales everything at once.</summary>
        public bool uniform;
    }
}
