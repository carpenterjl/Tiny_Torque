using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Marks an <c>int</c> field as an index into <c>TrackCatalog.Floors</c>, so the
    /// inspector draws it as a named dropdown instead of a number.
    ///
    /// The index is the persisted id — it goes into track JSON, terrain tables and
    /// <see cref="SurfaceTag"/>, and the catalogue is append-only for exactly that
    /// reason — but "1" tells an author nothing about whether they are painting
    /// asphalt or ice. Every surface field in the project should carry this; the
    /// drawer also shows each floor's grip, because <c>frictionMult</c> doubles as
    /// the arcade track-limit classifier and the 0.90 threshold is invisible
    /// otherwise.
    /// </summary>
    public sealed class FloorTypeAttribute : PropertyAttribute
    {
    }
}
