using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Arc-length parameterisation of the racing line (the same ordered centerline
    /// <see cref="Core.BotPath"/> builds). Two jobs: turn a world position into a
    /// single "how far round the lap" scalar, and turn a distance back into a pose
    /// so item boxes can be auto-placed along the track.
    ///
    /// This is a finer position metric than <c>RaceDirector.Progress()</c>, which
    /// quantizes to whole checkpoints — fine for rubber-banding, useless as a live
    /// scoreboard on a map with three checkpoints. RaceDirector's version is left
    /// alone deliberately; it drives bot behaviour in existing non-arcade races.
    /// </summary>
    public sealed class TrackSpine
    {
        private readonly Vector3[] _pts;
        private readonly float[] _cum;   // arc length at node i (_cum[0] = 0)
        private readonly bool _closed;

        /// <summary>Total path length (m). A closed loop includes the wrap segment.</summary>
        public float TotalLength { get; }

        public int Count => _pts.Length;
        public bool Closed => _closed;

        public TrackSpine(IReadOnlyList<Vector3> pts, bool closed)
        {
            _pts = new Vector3[pts.Count];
            for (int i = 0; i < pts.Count; i++) _pts[i] = pts[i];
            _closed = closed && _pts.Length >= 3;

            _cum = new float[_pts.Length];
            float run = 0f;
            for (int i = 1; i < _pts.Length; i++)
            {
                run += Vector3.Distance(_pts[i - 1], _pts[i]);
                _cum[i] = run;
            }
            if (_closed) run += Vector3.Distance(_pts[_pts.Length - 1], _pts[0]);
            TotalLength = Mathf.Max(1e-3f, run);
        }

        /// <summary>Build from a bot path; null/degenerate input yields null.</summary>
        public static TrackSpine From(IReadOnlyList<Vector3> pts, bool closed) =>
            pts != null && pts.Count >= 2 ? new TrackSpine(pts, closed) : null;

        private int SegCount => _closed ? _pts.Length : _pts.Length - 1;

        /// <summary>
        /// Distance along the path of the closest point to <paramref name="world"/>.
        /// <paramref name="hint"/> seeds a local search (±<paramref name="window"/>
        /// segments) and is updated in place — a full scan runs only when the hint
        /// is negative, so this stays cheap at 5–10 Hz across a full grid.
        /// </summary>
        public float Project(Vector3 world, ref int hint, int window = 20)
        {
            int segs = SegCount;
            if (segs <= 0) return 0f;

            int from = 0, count = segs;
            if (hint >= 0 && segs > window * 2 + 1)
            {
                from = hint - window;
                count = window * 2 + 1;
            }

            float bestSq = float.MaxValue, bestS = 0f;
            int bestSeg = 0;
            for (int k = 0; k < count; k++)
            {
                int i = from + k;
                if (_closed) i = ((i % segs) + segs) % segs;
                else if (i < 0 || i >= segs) continue;

                Vector3 a = _pts[i];
                Vector3 b = _pts[(i + 1) % _pts.Length];
                Vector3 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-9f ? Mathf.Clamp01(Vector3.Dot(world - a, ab) / len2) : 0f;
                Vector3 p = a + ab * t;

                float sq = (world - p).sqrMagnitude;
                if (sq >= bestSq) continue;
                bestSq = sq;
                bestSeg = i;
                bestS = _cum[i] + Mathf.Sqrt(len2) * t;
            }

            hint = bestSeg;
            return bestS;
        }

        /// <summary>Pose at distance <paramref name="s"/> along the path.</summary>
        public void Sample(float s, out Vector3 pos, out Vector3 forward)
        {
            int segs = SegCount;
            if (segs <= 0) { pos = Vector3.zero; forward = Vector3.forward; return; }

            s = _closed ? Mathf.Repeat(s, TotalLength) : Mathf.Clamp(s, 0f, TotalLength);

            int seg = segs - 1;
            for (int i = 0; i < segs; i++)
            {
                float end = (i + 1 < _cum.Length) ? _cum[i + 1] : TotalLength;
                if (s <= end) { seg = i; break; }
            }

            Vector3 a = _pts[seg];
            Vector3 b = _pts[(seg + 1) % _pts.Length];
            float segLen = Mathf.Max(1e-4f, Vector3.Distance(a, b));
            float t = Mathf.Clamp01((s - _cum[seg]) / segLen);

            pos = Vector3.Lerp(a, b, t);
            Vector3 dir = b - a;
            dir.y = 0f;
            forward = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.forward;
        }

        /// <summary>
        /// Signed gap from <paramref name="fromS"/> to <paramref name="toS"/> in the
        /// direction of travel: positive means "ahead of me". On a closed loop the
        /// result is wrapped into ±half a lap, so the car just behind the start
        /// line reads as behind rather than a lap ahead.
        /// </summary>
        public float Gap(float fromS, float toS)
        {
            float d = toS - fromS;
            if (!_closed) return d;
            float half = TotalLength * 0.5f;
            if (d > half) d -= TotalLength;
            else if (d < -half) d += TotalLength;
            return d;
        }
    }
}
