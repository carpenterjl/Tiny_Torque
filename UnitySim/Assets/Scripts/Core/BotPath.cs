using System.Collections.Generic;
using AIHWSim.TrackEd;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Builds the ordered centerline a bot follows for the current environment:
    /// the longest spline's samples (a real racing line), the classic oval loop,
    /// or ordered checkpoint gates on a tile map. The result is re-oriented so
    /// index increases in the spawn's forward direction, so bots drive the track
    /// the intended way. Returns null when no drivable path exists (e.g. a
    /// finish-less tile map — no race there anyway).
    /// </summary>
    public static class BotPath
    {
        public static List<Vector3> Build(TrackDesign design, AIHWSim.Track.LapTimer lapTimer,
            Vector3[] ovalPath, Vector3 spawnPos, Vector3 spawnForward, out bool closed)
        {
            closed = true;
            List<Vector3> pts = null;

            // 1. Spline map — follow the longest spline (the racing line).
            if (design != null && design.splines != null && design.splines.Count > 0)
            {
                SplineSpec best = null;
                foreach (var sp in design.splines)
                    if (sp != null && sp.Count >= 2 && (best == null || sp.Count > best.Count))
                        best = sp;
                if (best != null)
                {
                    var samples = SplineMath.SampleAll(best);
                    if (samples.Count >= 2)
                    {
                        pts = new List<Vector3>(samples.Count);
                        foreach (var s in samples) pts.Add(s.pos);
                        closed = best.closed;
                    }
                }
            }

            // 2. Classic oval (no TrackDesign) — the procedural loop.
            if (pts == null && design == null && ovalPath != null && ovalPath.Length >= 2)
            {
                pts = new List<Vector3>(ovalPath);
                closed = true;
            }

            // 3. Tile map without a spline — ordered checkpoint gates as coarse
            //    waypoints, with the finish line as one more point on the loop.
            if (pts == null)
            {
                var cps = Object.FindObjectsByType<AIHWSim.Track.Checkpoint>(FindObjectsSortMode.None);
                if (cps != null && cps.Length > 0)
                {
                    System.Array.Sort(cps, (a, b) => a.index.CompareTo(b.index));
                    var list = new List<Vector3>();
                    if (lapTimer != null) list.Add(lapTimer.transform.position);
                    foreach (var c in cps) list.Add(c.transform.position);
                    if (list.Count >= 2) { pts = list; closed = true; }
                }
            }

            if (pts == null || pts.Count < 2) return null;

            // Re-orient so index increases along the spawn's forward heading.
            Vector3 fwd = new Vector3(spawnForward.x, 0f, spawnForward.z);
            if (fwd.sqrMagnitude > 1e-4f)
            {
                int ni = NearestIndex(pts, spawnPos);
                Vector3 dir = pts[(ni + 1) % pts.Count] - pts[ni];
                dir.y = 0f;
                if (Vector3.Dot(dir, fwd) < 0f) pts.Reverse();
            }
            return pts;
        }

        private static int NearestIndex(List<Vector3> pts, Vector3 p)
        {
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float sq = (pts[i] - p).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            return best;
        }
    }
}
