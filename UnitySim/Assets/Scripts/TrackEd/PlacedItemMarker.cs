using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Tag on a built track item's root linking it back to its
    /// <see cref="TrackDesign.items"/> index (builder selection/raycasts).
    /// </summary>
    public sealed class PlacedItemMarker : MonoBehaviour
    {
        public int index;
    }
}
