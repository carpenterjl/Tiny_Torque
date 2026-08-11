using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Exact-answer hook for the colour sensor: a component on (or above) a hit
    /// collider that knows its own surface colour better than the sensor's
    /// material fallbacks do. Painted line strips and coloured props implement
    /// this so a line-follow course doesn't depend on readable textures or
    /// MeshCollider UVs.
    /// </summary>
    public interface ISurfaceColorProvider
    {
        /// <summary>Return the surface colour at the hit, or false to fall
        /// through to the sensor's material-based chain.</summary>
        bool TryGetSurfaceColor(in RaycastHit hit, out Color color);
    }
}
