using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Tag on a ribbon surface-run GameObject: which spline it belongs to plus
    /// baked sample data so the editor can map a raycast hit to the nearest
    /// sample / owning control point (paint, insert) without recomputing.
    /// </summary>
    /// <remarks>Its own file on purpose — see <see cref="RibbonMeshMarker"/> for
    /// what happens to a MonoBehaviour whose filename does not match its class
    /// once somebody saves a scene containing one.</remarks>
    public sealed class SplineRunMarker : MonoBehaviour
    {
        public int splineIndex;
        public Vector3[] samplePos;
        public int[] samplePointIndex;
    }
}
