using System;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Painted lane markings on a ribbon: a centre line or a double line, an
    /// optional pair of edge lines, solid or dashed.
    ///
    /// <b>Not the kerb stripes.</b> <see cref="SplineSpec.edgeStripes"/> is the
    /// red/white rumble strip that hangs off the ribbon's outer edge and is part
    /// of the road mesh. These are flat paint ON the road, built as their own
    /// collider-less mesh — see <c>RibbonMeshBuilder.BuildLines</c> for why that
    /// separation is the whole safety story.
    ///
    /// Every field defaults to "no markings", so a spline authored before this
    /// existed, and every track JSON already on disk, builds exactly the geometry
    /// it built before.
    /// </summary>
    [Serializable]
    public class RoadLineStyle
    {
        [Tooltip("0 = none, 1 = a single centre line, 2 = a double line.")]
        [Range(0, 2)]
        public int centreLines;

        [Tooltip("Also paint a line just inside each edge of the road.")]
        public bool edgeLines;

        [Tooltip("Width of one painted line, in metres.")]
        public float width = 0.05f;

        [Tooltip("Spacing in metres: the gap between the two lines of a double, " +
                 "and how far an edge line sits in from the edge of the road.")]
        public float spacing = 0.07f;

        [Tooltip("Length of one dash, in metres. 0 paints a solid line.")]
        public float dashLength;

        [Tooltip("Gap between dashes, in metres. Ignored when dash length is 0.")]
        public float dashGap = 0.25f;

        public Color color = new Color(0.95f, 0.95f, 0.90f);

        /// <summary>How many painted strips this style asks for. Constant along
        /// the road — only their lateral OFFSETS vary, with the road's width — so
        /// the mesh can use a fixed vertex stride per ring.</summary>
        public int LineCount => Mathf.Clamp(centreLines, 0, 2) + (edgeLines ? 2 : 0);

        public bool Any => LineCount > 0;

        /// <summary>Dash + gap, in metres; 0 when the line is solid.</summary>
        public float DashPeriod =>
            dashLength > 0f ? dashLength + Mathf.Max(0f, dashGap) : 0f;

        /// <summary>Fraction of a period that is painted. 1 = solid.</summary>
        public float DashRatio
        {
            get
            {
                float p = DashPeriod;
                return p > 0f ? Mathf.Clamp01(dashLength / p) : 1f;
            }
        }

        public RoadLineStyle Clone() =>
            JsonUtility.FromJson<RoadLineStyle>(JsonUtility.ToJson(this));
    }
}
