using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Marks a collider as a drivable surface of a specific floor type (spline
    /// ribbon runs). <see cref="SurfaceMap"/> resolves wheel contacts on tagged
    /// colliders to the type's physics without any positional lookup.
    /// </summary>
    public sealed class SurfaceTag : MonoBehaviour
    {
        public int floorType;
    }
}
