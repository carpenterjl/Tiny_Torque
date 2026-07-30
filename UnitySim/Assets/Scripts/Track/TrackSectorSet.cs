using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Slices a lap into sectors at arc-length boundaries along the racing line,
    /// with a target time per sector taken from the baked velocity profile.
    ///
    /// Sectors are arc lengths rather than trigger volumes on purpose. A trigger
    /// needs a collider, a position and a rotation that all have to stay consistent
    /// with a line that gets re-baked; an arc length is one float that the line
    /// already parameterises, and moving a boundary is dragging a number.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Track Sectors", fileName = "TrackSectors")]
    public sealed class TrackSectorSet : ScriptableObject
    {
        [System.Serializable]
        public struct Sector
        {
            [Tooltip("Where this sector begins, in metres along the racing line.")]
            public float sStart;

            [Tooltip("Target time in seconds, derived from the baked velocity profile.")]
            public float targetSec;

            public string label;
        }

        [Tooltip("Racing line these boundaries are measured along. A sector set " +
                 "without its line is a list of meaningless distances.")]
        public RacingLineAsset line;

        /// <summary>Ascending by sStart, first entry at 0. Enforced by the
        /// validator, because an out-of-order boundary would make the split for one
        /// sector negative and every subsequent one wrong.</summary>
        public Sector[] sectors = System.Array.Empty<Sector>();

        public float TotalTarget
        {
            get
            {
                float t = 0f;
                foreach (var s in sectors) t += s.targetSec;
                return t;
            }
        }

        /// <summary>Which sector a distance falls in, or -1 when there are none.</summary>
        public int SectorAt(float s)
        {
            if (sectors.Length == 0) return -1;
            int found = 0;
            for (int i = 0; i < sectors.Length; i++)
                if (s >= sectors[i].sStart) found = i;
            return found;
        }
    }
}
