using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Tag on a spline control-point handle sphere linking it back to its
    /// spline/point indices (builder picking + dragging).
    /// </summary>
    public sealed class SplinePointMarker : MonoBehaviour
    {
        public int spline;
        public int point;
    }
}
