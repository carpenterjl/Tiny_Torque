using System;
using System.Collections.Generic;
using System.Globalization;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// NAV-05 harness: obstacles put on the track for a mission run, and
    /// what the car did about them. <c>-opusObstacles</c> names them:
    /// <c>"barrier@s=12,clear=3;cone@s=6,lat=0.45;cone@s=6,lat=-0.45"</c> —
    /// a catalogue item (<see cref="TrackCatalog"/>) at a route arc length
    /// <c>s</c> (metres along <c>-opusRoute</c>), offset <c>lat</c> to the
    /// left, facing along the route; or at site coordinates <c>x</c>,
    /// <c>y</c> with heading <c>yaw</c> (degrees). <c>clear</c> removes the
    /// item that many seconds after the car first stops short of it, so a
    /// run can show the car going on once the way has opened.
    ///
    /// Scored: the gap from the car's nose to the item's near face when it
    /// first stopped (within a metre of it), and whether the item was
    /// removed. The items go into the track design before the scene loads,
    /// so a campaign's reloads rebuild them.
    /// </summary>
    public sealed class ObstacleCourse
    {
        public sealed class Entry
        {
            public string itemId = "";
            public float x, z, yawDeg;          // Unity world
            public float clearAfterS = -1f;     // < 0: stays
            public float halfDepth;             // along the item's forward, to its near face
            public int index = -1;              // in TrackDesign.items
            // Per run:
            public GameObject go;
            public bool removed;
            public float stoppedAt = -1f;       // run time the car first stood short of it
            public float gapM = float.NaN;      // nose to the near face then
            public float stillT;
        }

        public readonly List<Entry> entries = new List<Entry>();
        private bool _found;

        /// <summary>Parse a spec; null when it is empty. A route is needed
        /// for entries placed by arc length.</summary>
        public static ObstacleCourse Parse(string spec, RouteTable route)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var c = new ObstacleCourse();
            foreach (var part in spec.Split(';'))
            {
                string p = part.Trim();
                if (p.Length == 0) continue;
                int at = p.IndexOf('@');
                var e = new Entry { itemId = (at < 0 ? p : p.Substring(0, at)).Trim() };
                float s = float.NaN, lat = 0f, sx = float.NaN, sy = float.NaN, yaw = float.NaN;
                if (at >= 0)
                    foreach (var kv in p.Substring(at + 1).Split(','))
                    {
                        var t = kv.Split('=');
                        if (t.Length != 2) throw new ArgumentException($"obstacle '{p}': expected key=value, got '{kv}'");
                        float v = float.Parse(t[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                        switch (t[0].Trim())
                        {
                            case "s": s = v; break;
                            case "lat": lat = v; break;
                            case "x": sx = v; break;
                            case "y": sy = v; break;
                            case "yaw": yaw = v; break;
                            case "clear": e.clearAfterS = v; break;
                            default: throw new ArgumentException($"obstacle '{p}': unknown key '{t[0]}'");
                        }
                    }
                float psi;
                if (!float.IsNaN(s))
                {
                    if (route == null) throw new ArgumentException($"obstacle '{p}': s= needs a route (-opusRoute)");
                    route.At(s, out sx, out sy, out psi);
                    // Left of the route's heading, in the site frame.
                    sx -= lat * Mathf.Sin(psi);
                    sy += lat * Mathf.Cos(psi);
                }
                else
                {
                    if (float.IsNaN(sx) || float.IsNaN(sy)) throw new ArgumentException($"obstacle '{p}': give s= or x= and y=");
                    psi = float.IsNaN(yaw) ? 0f : yaw * Mathf.Deg2Rad;
                }
                // Site -> Unity: site x = world z, site y = -world x; a site
                // heading psi is a Unity yaw of -psi (RouteTable.SiteHeading).
                e.x = -sy;
                e.z = sx;
                e.yawDeg = -psi * Mathf.Rad2Deg;
                e.halfDepth = e.itemId == "barrier" ? 0.0625f : e.itemId == "cone" ? 0.07f : 0f;
                c.entries.Add(e);
            }
            return c.entries.Count > 0 ? c : null;
        }

        /// <summary>Add the items to the design the scene is built from.</summary>
        public void Place(TrackDesign track)
        {
            foreach (var e in entries)
            {
                if (TrackCatalog.Item(e.itemId) == null) throw new ArgumentException($"obstacle: no track item '{e.itemId}'");
                e.index = track.items.Count;
                track.items.Add(new PlacedItem { itemId = e.itemId, x = e.x, z = e.z, yawDeg = e.yawDeg });
            }
        }

        /// <summary>Forget the last run (a campaign reloads the scene).</summary>
        public void Reset()
        {
            _found = false;
            foreach (var e in entries) { e.go = null; e.removed = false; e.stoppedAt = -1f; e.gapM = float.NaN; e.stillT = 0f; }
        }

        /// <summary>One physics step: watch for the car standing short of an
        /// item, and remove the item when its time comes. <paramref name="noseM"/>
        /// is the car's nose ahead of its origin.</summary>
        public void Step(CarVehicle car, float noseM, float elapsed)
        {
            if (car == null) return;
            if (!_found)
            {
                _found = true;
                foreach (var m in UnityEngine.Object.FindObjectsByType<PlacedItemMarker>(FindObjectsSortMode.None))
                    foreach (var e in entries)
                        if (m.index == e.index) e.go = m.gameObject;
            }
            Vector3 nose = car.transform.position + car.transform.forward * noseM;
            float spd = Mathf.Abs(car.ForwardSpeed);
            float dt = Time.fixedDeltaTime;
            foreach (var e in entries)
            {
                if (e.removed) continue;
                Vector3 dir = Quaternion.Euler(0f, e.yawDeg, 0f) * Vector3.forward;
                Vector3 face = new Vector3(e.x, nose.y, e.z) - dir * e.halfDepth;
                float gap = Vector3.Dot(face - nose, dir);
                if (e.stoppedAt < 0f)
                {
                    if (spd < 0.05f && gap > -0.3f && gap < 1.0f) e.stillT += dt; else e.stillT = 0f;
                    if (e.stillT >= 0.3f) { e.stoppedAt = elapsed; e.gapM = gap; }
                }
                else if (e.clearAfterS >= 0f && elapsed >= e.stoppedAt + e.clearAfterS)
                {
                    if (e.go != null) e.go.SetActive(false);
                    e.removed = true;
                }
            }
        }

        /// <summary>The gap at the first stop short of any item, mm; -1 if
        /// the car never stood short of one.</summary>
        public float GapMm
        {
            get
            {
                float best = float.NaN;
                foreach (var e in entries)
                    if (e.stoppedAt >= 0f && (float.IsNaN(best) || e.gapM < best)) best = e.gapM;
                return float.IsNaN(best) ? -1f : best * 1000f;
            }
        }

        /// <summary>1 when an item was removed during the run.</summary>
        public int Removed
        {
            get { foreach (var e in entries) if (e.removed) return 1; return 0; }
        }
    }
}
