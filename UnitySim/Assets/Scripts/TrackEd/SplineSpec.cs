using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// One curved track ribbon: Catmull-Rom control points (y = height above the
    /// floor) with parallel per-point lists for bank angle, ribbon width, and
    /// surface type (the segment from point i to i+1 uses point i's surface).
    /// JsonUtility-flat; parallel primitive lists keep the JSON small and match
    /// the project's flat-fields style. Old saves without splines load as empty.
    /// </summary>
    [Serializable]
    public class SplineSpec
    {
        public List<Vector3> points = new List<Vector3>();
        public List<float> rollDeg = new List<float>();   // +ve = right edge down
        public List<float> widths = new List<float>();    // ribbon width (m)
        public List<int> surface = new List<int>();       // TrackCatalog.Floors id
        public bool closed;
        public bool edgeWalls;
        public bool edgeStripes;

        /// <summary>Painted lane markings. Defaults to none, so old saves — which
        /// carry no "lines" key at all — deserialize to the initializer and build
        /// the road they always built.</summary>
        public RoadLineStyle lines = new RoadLineStyle();

        public const float DefaultWidth = 1.5f;
        public const int DefaultSurface = 1; // asphalt

        public int Count => points?.Count ?? 0;

        /// <summary>Repair the parallel lists to match points.Count (hand-edited/old JSON).</summary>
        public void EnsureArrays()
        {
            points ??= new List<Vector3>();
            lines ??= new RoadLineStyle();
            rollDeg ??= new List<float>();
            widths ??= new List<float>();
            surface ??= new List<int>();
            Pad(rollDeg, 0f);
            Pad(widths, DefaultWidth);
            Pad(surface, DefaultSurface);
        }

        private void Pad<T>(List<T> list, T fill)
        {
            while (list.Count < points.Count) list.Add(list.Count > 0 ? list[list.Count - 1] : fill);
            if (list.Count > points.Count) list.RemoveRange(points.Count, list.Count - points.Count);
        }

        /// <summary>Append a point, inheriting the last point's roll/width/surface.</summary>
        public void AddPoint(Vector3 p)
        {
            EnsureArrays();
            points.Add(p);
            rollDeg.Add(rollDeg.Count > 0 ? rollDeg[rollDeg.Count - 1] : 0f);
            widths.Add(widths.Count > 0 ? widths[widths.Count - 1] : DefaultWidth);
            surface.Add(surface.Count > 0 ? surface[surface.Count - 1] : DefaultSurface);
        }

        /// <summary>Insert at index i, interpolating roll/width from neighbors.</summary>
        public void InsertPoint(int i, Vector3 p)
        {
            EnsureArrays();
            i = Mathf.Clamp(i, 0, points.Count);
            int a = Mathf.Clamp(i - 1, 0, Mathf.Max(0, points.Count - 1));
            int b = Mathf.Clamp(i, 0, Mathf.Max(0, points.Count - 1));
            float roll = points.Count > 0 ? (rollDeg[a] + rollDeg[b]) * 0.5f : 0f;
            float width = points.Count > 0 ? (widths[a] + widths[b]) * 0.5f : DefaultWidth;
            int surf = points.Count > 0 ? surface[a] : DefaultSurface;
            points.Insert(i, p);
            rollDeg.Insert(i, roll);
            widths.Insert(i, width);
            surface.Insert(i, surf);
        }

        public void RemovePoint(int i)
        {
            EnsureArrays();
            if (i < 0 || i >= points.Count) return;
            points.RemoveAt(i);
            rollDeg.RemoveAt(i);
            widths.RemoveAt(i);
            surface.RemoveAt(i);
        }

        public SplineSpec Clone() => JsonUtility.FromJson<SplineSpec>(JsonUtility.ToJson(this));
    }
}
