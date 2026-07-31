using AIHWSim.Track;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Writes a run of surface into a road's <c>surfaceChannel</c>.
    ///
    /// <b>Why a road is painted into the spline rather than onto the ribbon.</b> The
    /// ribbon's colliders are destroyed and recreated by every
    /// <see cref="TrackSplineAuthoring.Bake"/>, which now runs live on every knot
    /// drag — so a <see cref="SurfaceTag"/> stamped onto one survives until the next
    /// edit and then silently vanishes. Keys in the channel are the road's own data:
    /// they survive a rebuild, they move with the curve, and they are the same keys
    /// the inspector and the Scene-view handles edit.
    ///
    /// Separate from the brush window because none of this is a GUI concern, and
    /// because the arc-length projection and the key bookkeeping are the parts worth
    /// exercising without a Scene view in front of them.
    /// </summary>
    internal static class RoadSurfacePainter
    {
        /// <summary>
        /// Projection accuracy for "where on the road did I click". The package
        /// defaults to <c>PickResolutionDefault = 4</c> with two iterations, which on
        /// a 148 m spline lands about a quarter of a metre from the cursor — enough
        /// that a short run visibly does not start where you clicked. A brush fires
        /// this a few dozen times a second at most, so the extra subdivision is free.
        /// </summary>
        private const int PickResolution = 32;
        private const int PickIterations = 4;

        /// <summary>
        /// Force the stretch of road within <paramref name="radius"/> metres of
        /// <paramref name="worldPoint"/> to <paramref name="floorType"/>.
        ///
        /// The radius becomes an arc-length half-span either side of the nearest
        /// point on the curve, so a 1.5 m brush paints a 3 m run — which is what a
        /// road brush should mean, since the ribbon is already exactly as wide as
        /// the width channel says and there is no "part of the road" to paint.
        /// </summary>
        /// <returns>False if the road has nothing paintable — no container, fewer
        /// than two knots, or zero length.</returns>
        internal static bool Paint(TrackSplineAuthoring a, Vector3 worldPoint,
                                   float radius, int floorType)
        {
            var container = a != null ? a.Container : null;
            var spline = SplineOf(a);
            if (container == null || spline == null || spline.Count < 2) return false;
            if (a.surfaceChannel == null) return false;

            // The channel is indexed in normalized units everywhere else — the
            // sampler evaluates against Normalized and the Scene-view handles assume
            // it — but SplineData defaults to Knot, so a channel that has never been
            // through the inspector could still be in knot indices.
            TrackSplineAuthoringEditor.EnsureNormalized(a);

            float len = spline.CalculateLength(container.transform.localToWorldMatrix);
            if (len < 1e-3f) return false;

            float3 local = container.transform.InverseTransformPoint(worldPoint);
            SplineUtility.GetNearestPoint(spline, local, out _, out float t,
                                          PickResolution, PickIterations);

            // A step in a linearly interpolated channel needs two keys a hair apart.
            // A quarter of the resample spacing puts the transition inside one ribbon
            // segment, which is as sharp as the geometry can express.
            float eps = Mathf.Max(1e-4f, 0.25f * Mathf.Max(0.05f, a.spacing) / len);

            float half = radius / len;
            float t0 = Offset(spline, t, -radius, half, len);
            float t1 = Offset(spline, t, radius, half, len);
            if (spline.Closed && t0 < 0f)
            {
                SetRun(a, spline, 0f, t1, eps, floorType);
                SetRun(a, spline, 1f + t0, 1f, eps, floorType);
            }
            else if (spline.Closed && t1 > 1f)
            {
                SetRun(a, spline, t0, 1f, eps, floorType);
                SetRun(a, spline, 0f, t1 - 1f, eps, floorType);
            }
            else
            {
                SetRun(a, spline, Mathf.Max(0f, t0), Mathf.Min(1f, t1), eps, floorType);
            }

            Compact(a.surfaceChannel);
            return true;
        }

        /// <summary>
        /// The curve parameter <paramref name="metres"/> of ARC LENGTH away from
        /// <paramref name="t"/>.
        ///
        /// Not <c>t + metres/length</c>: Unity's t is uniform in the parameter, not in
        /// distance, so that shorthand paints a short run through a tight corner and a
        /// long one down a straight — and a corner is exactly where you reach for a
        /// different surface. <c>GetPointAtLinearDistance</c> walks the curve for real.
        /// It clamps at the ends rather than wrapping, so a walk that ran off is
        /// discarded in favour of the linear estimate, which the caller's own wrap
        /// handling then picks up.
        /// </summary>
        private static float Offset(Spline spline, float t, float metres,
                                    float linearHalf, float worldLen)
        {
            float fallback = t + Mathf.Sign(metres) * linearHalf;
            if (fallback <= 0f || fallback >= 1f) return fallback;

            // GetPointAtLinearDistance measures in the spline's own units, which are
            // world metres only while the container is unscaled.
            float local = spline.GetLength();
            float scale = worldLen > 1e-6f ? local / worldLen : 1f;

            spline.GetPointAtLinearDistance(t, metres * scale, out float walked);
            if (walked <= 0f || walked >= 1f) return fallback;   // clamped, not solved
            return walked;
        }

        /// <summary>
        /// Force <c>[t0, t1]</c> to the painted floor with hard edges, preserving
        /// whatever the channel said either side of the run.
        /// </summary>
        private static void SetRun(TrackSplineAuthoring a, Spline spline,
                                   float t0, float t1, float eps, int floorType)
        {
            var ch = a.surfaceChannel;
            if (t1 <= t0) return;

            // Read the neighbours BEFORE touching anything — after the removals below
            // the channel no longer knows what used to be there.
            float pre = Sample(ch, spline, t0 - eps, a.defaultSurface);
            float post = Sample(ch, spline, t1 + eps, a.defaultSurface);

            for (int i = ch.Count - 1; i >= 0; i--)
                if (ch[i].Index >= t0 - eps && ch[i].Index <= t1 + eps)
                    ch.RemoveAt(i);

            if (t0 - eps > 0f) ch.Add(t0 - eps, pre);
            ch.Add(t0, floorType);
            ch.Add(t1, floorType);
            if (t1 + eps < 1f) ch.Add(t1 + eps, post);
        }

        /// <summary>
        /// Drop keys the curve no longer needs. A drag lays down four keys per stamp
        /// and most of them are immediately buried by the next one — without this a
        /// two-second stroke leaves a channel of several hundred keys that is
        /// unreadable in the inspector and impossible to hand-edit afterwards.
        ///
        /// A key survives if it is a step: equal to BOTH neighbours is the only safe
        /// removal test, since the pair either side of a transition differ by
        /// construction.
        /// </summary>
        internal static void Compact(SplineData<float> ch)
        {
            if (ch == null) return;

            for (int i = ch.Count - 2; i >= 1; i--)
                if (Same(ch[i].Value, ch[i - 1].Value) && Same(ch[i].Value, ch[i + 1].Value))
                    ch.RemoveAt(i);

            // The value before the first key and after the last is that key's own
            // value, so a leading or trailing duplicate carries no information.
            while (ch.Count >= 2 && Same(ch[0].Value, ch[1].Value)) ch.RemoveAt(0);
            while (ch.Count >= 2 && Same(ch[ch.Count - 1].Value, ch[ch.Count - 2].Value))
                ch.RemoveAt(ch.Count - 1);
        }

        private static bool Same(float a, float b) => Mathf.Abs(a - b) < 1e-3f;

        /// <summary>The channel's value at normalized t, or the road's default when
        /// the channel is empty.</summary>
        internal static float Sample(SplineData<float> ch, Spline spline,
                                     float t, float fallback)
        {
            if (ch == null || ch.Count == 0) return fallback;
            return ch.Evaluate(spline, Mathf.Clamp01(t), PathIndexUnit.Normalized,
                               new UnityEngine.Splines.Interpolators.LerpFloat());
        }

        internal static Spline SplineOf(TrackSplineAuthoring a)
        {
            var c = a != null ? a.Container : null;
            if (c == null || a.splineIndex < 0 || a.splineIndex >= c.Splines.Count) return null;
            return c.Splines[a.splineIndex];
        }
    }
}
