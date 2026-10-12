using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// A route as the firmware follows it (NAV-01): the points
    /// Tools/gen_route.js writes into Resources/Routes/&lt;name&gt;.json, the same
    /// float32 values it builds into Controllers/core/routes_gen.c. The scorer
    /// projects the car's true pose onto it; nothing here steers anything.
    ///
    /// Site frame, as the UWB anchors report (UwbAnchor.WorldFlu): x = Unity
    /// +z, y = Unity −x, heading + = left (CCW from +x).
    /// </summary>
    public sealed class RouteTable
    {
        public string name = "", track = "";
        public int id;
        public uint crc;
        public float length;
        public float[] start = new float[3];
        public bool stopAtEnd;
        public float vMax, aLatMax, aDrive, aBrake;
        public float[] x, y, s, psi, kappa, vCap;
        public int Count => x != null ? x.Length : 0;

        [Serializable]
        private class Head
        {
            public string name = "", track = "";
            public int id;
            public double crc;
            public float length_m;
            public float[] start = new float[3];
            public int stop_at_end;
            public float v_max, a_lat_max, a_drive, a_brake;
        }

        /// <summary>The route called <paramref name="spec"/> ("oval", or
        /// "oval@2" — the speed part is the firmware's), or null.</summary>
        public static RouteTable Load(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            string name = spec.Split('@')[0].Trim();
            var ta = Resources.Load<TextAsset>("Routes/" + name);
            if (ta == null) return null;
            return Parse(ta.text);
        }

        public static RouteTable Parse(string text)
        {
            var h = JsonUtility.FromJson<Head>(text);
            var r = new RouteTable
            {
                name = h.name, track = h.track, id = h.id, crc = (uint)h.crc, length = h.length_m,
                start = h.start, stopAtEnd = h.stop_at_end != 0, vMax = h.v_max, aLatMax = h.a_lat_max,
                aDrive = h.a_drive, aBrake = h.a_brake,
            };
            // "pts": one [x, y, s, psi, kappa, v_cap] per line (JsonUtility
            // cannot read nested arrays).
            int at = text.IndexOf("\"pts\"", StringComparison.Ordinal);
            var rows = new List<float[]>();
            if (at >= 0)
            {
                int i = text.IndexOf('[', at) + 1;
                while (i > 0 && i < text.Length)
                {
                    int a = text.IndexOf('[', i), b = a >= 0 ? text.IndexOf(']', a) : -1;
                    if (a < 0 || b < 0) break;
                    var parts = text.Substring(a + 1, b - a - 1).Split(',');
                    if (parts.Length == 6)
                    {
                        var v = new float[6];
                        for (int k = 0; k < 6; k++) v[k] = float.Parse(parts[k], NumberStyles.Float, CultureInfo.InvariantCulture);
                        rows.Add(v);
                    }
                    i = b + 1;
                }
            }
            int n = rows.Count;
            r.x = new float[n]; r.y = new float[n]; r.s = new float[n];
            r.psi = new float[n]; r.kappa = new float[n]; r.vCap = new float[n];
            for (int k = 0; k < n; k++)
            {
                r.x[k] = rows[k][0]; r.y[k] = rows[k][1]; r.s[k] = rows[k][2];
                r.psi[k] = rows[k][3]; r.kappa[k] = rows[k][4]; r.vCap[k] = rows[k][5];
            }
            return r;
        }

        /// <summary>A Unity world position in the site frame.</summary>
        public static Vector2 Site(Vector3 world) => new Vector2(world.z, -world.x);

        /// <summary>The heading of a Unity forward vector in the site frame.</summary>
        public static float SiteHeading(Vector3 forward) => Mathf.Atan2(-forward.x, forward.z);

        public static float Wrap(float a)
        {
            while (a > Mathf.PI) a -= 2f * Mathf.PI;
            while (a <= -Mathf.PI) a += 2f * Mathf.PI;
            return a;
        }

        public struct Proj { public int i; public float s, ey, psi, kappa; }

        /// <summary>The closest point to (px, py), searched from segment
        /// <paramref name="from"/> back and ahead: tt_route_project.</summary>
        public Proj Project(float px, float py, int from, int back = 10, int ahead = 40)
        {
            int n = Count;
            from = Mathf.Clamp(from, 0, n - 2);
            int i0 = Mathf.Max(0, from - back), i1 = Mathf.Min(n - 2, from + ahead);
            int bi = i0;
            float bd = float.MaxValue, bt = 0f;
            for (int i = i0; i <= i1; i++)
            {
                float ex = x[i + 1] - x[i], ey = y[i + 1] - y[i], l2 = ex * ex + ey * ey;
                float t = l2 > 0f ? ((px - x[i]) * ex + (py - y[i]) * ey) / l2 : 0f;
                if (t < 0f && i > 0) t = 0f;
                if (t > 1f && i + 2 < n) t = 1f;
                float dx = x[i] + t * ex - px, dy = y[i] + t * ey - py, d2 = dx * dx + dy * dy;
                if (d2 < bd) { bd = d2; bi = i; bt = t; }
            }
            float tc = Mathf.Clamp01(bt);
            float sx = x[bi + 1] - x[bi], sy = y[bi + 1] - y[bi], l = Mathf.Sqrt(sx * sx + sy * sy);
            float qx = x[bi] + bt * sx, qy = y[bi] + bt * sy;
            return new Proj
            {
                i = bi,
                s = s[bi] + bt * (s[bi + 1] - s[bi]),
                psi = psi[bi] + tc * (psi[bi + 1] - psi[bi]),
                kappa = kappa[bi] + tc * (kappa[bi + 1] - kappa[bi]),
                ey = l > 0f ? ((px - qx) * (-sy) + (py - qy) * sx) / l : 0f,
            };
        }
    }
}
