using AIHWSim.Track;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Draws the baked racing line in the Scene view: the line itself coloured by
    /// speed, apex markers, and bars over the braking zones.
    ///
    /// This is most of the point of baking a line at all. A lap time and an array of
    /// floats tell you nothing about whether the solve did something sensible;
    /// seeing the line run out-in-out through a corner, brake before the apex and
    /// unwind onto the straight tells you immediately. It is drawn via
    /// <see cref="DrawGizmo"/> on the descriptor so it appears without needing the
    /// Track Studio window open, and disappears with gizmos like everything else.
    /// </summary>
    public static class RacingLineGizmos
    {
        /// <summary>Speed at which the ribbon is fully "fast" coloured. Not the
        /// car's real top speed — a fixed reference keeps the colouring comparable
        /// between two tracks, which is the whole reason to look at it.</summary>
        private const float FastReference = 11f;

        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        private static void Draw(SceneTrackDescriptor d, GizmoType type)
        {
            var rl = d.racingLine;
            if (rl == null || !rl.IsUsable) return;

            int n = rl.points.Length;
            int segs = rl.closed ? n : n - 1;

            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % n;
                Gizmos.color = SpeedColor(rl.speed[i]);
                Gizmos.DrawLine(rl.points[i] + Vector3.up * 0.02f,
                                rl.points[j] + Vector3.up * 0.02f);
            }

            // Braking is drawn as a raised second line rather than by recolouring the
            // first: a brake zone and a slow corner are different facts, and colour
            // alone would conflate them.
            Gizmos.color = new Color(1f, 0.35f, 0.2f, 0.9f);
            foreach (var z in rl.brakeZones)
            {
                DrawArcRange(rl, z.sStart, z.sEnd, 0.16f);
            }

            Gizmos.color = new Color(1f, 0.9f, 0.2f, 1f);
            foreach (int a in rl.apexIndices)
            {
                if (a < 0 || a >= n) continue;
                var p = rl.points[a];
                Gizmos.DrawLine(p, p + Vector3.up * 0.30f);
                Gizmos.DrawWireSphere(p + Vector3.up * 0.30f, 0.05f);
            }
        }

        /// <summary>Blue-green at speed, red when slow — the convention every
        /// telemetry tool uses, so it needs no legend.</summary>
        private static Color SpeedColor(float v)
        {
            float t = Mathf.Clamp01(v / FastReference);
            return Color.Lerp(new Color(0.95f, 0.25f, 0.15f),
                              new Color(0.25f, 0.95f, 0.75f), t);
        }

        private static void DrawArcRange(RacingLineAsset rl, float sStart, float sEnd, float lift)
        {
            var cum = PathCurvature.Cumulative(rl.points);
            int n = rl.points.Length;
            for (int i = 0; i < n - 1; i++)
            {
                if (cum[i] < sStart || cum[i] > sEnd) continue;
                Gizmos.DrawLine(rl.points[i] + Vector3.up * lift,
                                rl.points[i + 1] + Vector3.up * lift);
            }
        }
    }
}
