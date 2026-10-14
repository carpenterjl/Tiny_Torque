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
    /// An item can move (NAV-05 close-out): <c>vlon</c> and <c>vlat</c>
    /// give its speed along and to the left of its facing (m/s), from run
    /// time <c>at</c> (s) or from when the car's place on the route reaches
    /// <c>when</c> (m), for <c>dist</c> metres (0 = until the run ends).
    /// <c>"barrier@s=16,vlon=-0.5,dist=3"</c> comes down the route at the
    /// car; <c>"cone@s=10,lat=1,vlat=-1,dist=1,when=7"</c> slides into the
    /// corridor as the car arrives.
    ///
    /// Scored: the gap from the car's nose to the item's near face when it
    /// first stopped (within a metre of it), how much it moved while it
    /// stood there (creep), whether the item was removed, and whether a
    /// moving item reached the car. The items go into the track design
    /// before the scene loads, so a campaign's reloads rebuild them.
    /// </summary>
    public sealed class ObstacleCourse
    {
        public sealed class Entry
        {
            public string itemId = "";
            public float x, z, yawDeg;          // Unity world
            public float clearAfterS = -1f;     // < 0: stays
            public float halfDepth;             // along the item's forward, to its near face
            public float halfWidth;             // across it
            public int index = -1;              // in TrackDesign.items
            public float s = float.NaN;         // route arc length, when placed by s=
            public Vector3 vel;                 // Unity world, m/s (zero = static)
            public float moveAt = -1f;          // run time it starts moving; < 0 = see moveWhen
            public float moveWhen = float.NaN;  // ...or the car's route s it starts at
            public float moveDist;              // how far it goes; 0 = no end
            // Per run:
            public GameObject go;
            public bool removed;
            public float stoppedAt = -1f;       // run time the car first stood short of it
            public float gapM = float.NaN;      // nose to the near face then
            public float gapMin, gapMax;        // ...and the gap's range while it stood there
            public float stillT;
            public float detourMax;             // NAV-08: the most the car left the route near it, m
            public float rejoinM = -1f;         // ...and how far past its face it was back within 3 cm
            public Vector3 placedAt;            // where it stood: a dynamic item that moved was hit
            public bool hit;
            public bool moving;                 // its scripted motion has begun
            public float moved;                 // m so far
            public float x0, z0;                // where it was placed (a campaign reloads it there)
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
                float vlon = 0f, vlat = 0f;
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
                            case "vlon": vlon = v; break;
                            case "vlat": vlat = v; break;
                            case "at": e.moveAt = v; break;
                            case "when": e.moveWhen = v; break;
                            case "dist": e.moveDist = v; break;
                            default: throw new ArgumentException($"obstacle '{p}': unknown key '{t[0]}'");
                        }
                    }
                float psi;
                if (!float.IsNaN(s))
                {
                    if (route == null) throw new ArgumentException($"obstacle '{p}': s= needs a route (-opusRoute)");
                    route.At(s, out sx, out sy, out psi);
                    e.s = s;
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
                e.x0 = e.x;
                e.z0 = e.z;
                e.yawDeg = -psi * Mathf.Rad2Deg;
                e.halfDepth = e.itemId == "barrier" ? 0.0625f : e.itemId == "cone" ? 0.07f : 0f;
                e.halfWidth = e.itemId == "barrier" ? 0.3f : e.itemId == "cone" ? 0.07f : 0f;
                if (vlon != 0f || vlat != 0f)
                {
                    // Along its facing and to its left, in the site frame, then
                    // into Unity's as the position above.
                    float vxs = vlon * Mathf.Cos(psi) - vlat * Mathf.Sin(psi);
                    float vys = vlon * Mathf.Sin(psi) + vlat * Mathf.Cos(psi);
                    e.vel = new Vector3(-vys, 0f, vxs);
                    if (e.moveAt < 0f && float.IsNaN(e.moveWhen)) e.moveAt = 0f;
                    if (!float.IsNaN(e.moveWhen) && route == null)
                        throw new ArgumentException($"obstacle '{p}': when= needs a route (-opusRoute)");
                }
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
                // A moving item must not be static-batched (the factory
                // would leave its mesh where it was built).
                track.items.Add(new PlacedItem { itemId = e.itemId, x = e.x, z = e.z, yawDeg = e.yawDeg,
                                                 mobile = e.vel.sqrMagnitude > 0f });
            }
        }

        /// <summary>Forget the last run (a campaign reloads the scene).</summary>
        public void Reset()
        {
            _found = false;
            foreach (var e in entries)
            {
                e.go = null; e.removed = false; e.stoppedAt = -1f; e.gapM = float.NaN; e.stillT = 0f;
                e.gapMin = e.gapMax = float.NaN;
                e.detourMax = 0f; e.rejoinM = -1f; e.hit = false;
                e.moving = false; e.moved = 0f; e.x = e.x0; e.z = e.z0;
            }
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
                        if (m.index == e.index) { e.go = m.gameObject; e.placedAt = m.transform.position; }
            }
            Vector3 nose = car.transform.position + car.transform.forward * noseM;
            Vector3 body = car.transform.position;
            float spd = Mathf.Abs(car.ForwardSpeed);
            float dt = Time.fixedDeltaTime;
            foreach (var e in entries)
            {
                if (e.removed) continue;
                Vector3 dir = Quaternion.Euler(0f, e.yawDeg, 0f) * Vector3.forward;
                Vector3 right = Quaternion.Euler(0f, e.yawDeg, 0f) * Vector3.right;
                bool scripted = e.vel.sqrMagnitude > 0f;
                if (scripted)
                {
                    // Its scripted motion: from its time, for its distance. A
                    // dynamic item is driven kinematically so the physics does
                    // not fight the script; what it runs into is pushed.
                    if (!e.moving && e.moveAt >= 0f && elapsed >= e.moveAt) e.moving = true;
                    if (e.moving && e.go != null && (e.moveDist <= 0f || e.moved < e.moveDist))
                    {
                        if (e.moved == 0f)
                            Debug.Log($"[ObstacleCourse] {e.itemId} starts moving at {elapsed:0.00} s from ({e.x:0.00}, {e.z:0.00}) at {e.vel.magnitude:0.00} m/s");
                        var rb = e.go.GetComponent<Rigidbody>();
                        if (rb != null && !rb.isKinematic) rb.isKinematic = true;
                        Vector3 step = e.vel * dt;
                        if (e.moveDist > 0f && e.moved + step.magnitude > e.moveDist)
                            step = step.normalized * (e.moveDist - e.moved);
                        e.moved += step.magnitude;
                        e.go.transform.position += step;
                        e.placedAt = e.go.transform.position;
                        e.x = e.go.transform.position.x;
                        e.z = e.go.transform.position.z;
                        if (e.moveDist > 0f && e.moved >= e.moveDist)
                            Debug.Log($"[ObstacleCourse] {e.itemId} stops at {elapsed:0.00} s at ({e.x:0.00}, {e.z:0.00})");
                    }
                    // Reached the car: its nose or its origin inside the item.
                    if (!e.hit)
                    {
                        Vector3 centre = new Vector3(e.x, 0f, e.z);
                        foreach (var q in new[] { nose, body })
                        {
                            Vector3 r = new Vector3(q.x, 0f, q.z) - centre;
                            if (Mathf.Abs(Vector3.Dot(r, dir)) < e.halfDepth + 0.03f &&
                                Mathf.Abs(Vector3.Dot(r, right)) < e.halfWidth + 0.03f) e.hit = true;
                        }
                    }
                }
                else if (e.go != null && !e.hit && (e.go.transform.position - e.placedAt).sqrMagnitude > 0.02f * 0.02f)
                    e.hit = true;   // knocked (a cone flies; a barrier is static and stops the car instead)
                Vector3 face = new Vector3(e.x, nose.y, e.z) - dir * e.halfDepth;
                float gap = Vector3.Dot(face - nose, dir);
                if (e.stoppedAt < 0f)
                {
                    if (spd < 0.05f && gap > -0.3f && gap < 1.0f) e.stillT += dt; else e.stillT = 0f;
                    if (e.stillT >= 0.3f) { e.stoppedAt = elapsed; e.gapM = e.gapMin = e.gapMax = gap; }
                }
                else
                {
                    // Standing there: how much the car moves (the creep). A
                    // moving item's own motion is not the car's.
                    if (!scripted)
                    {
                        if (gap < e.gapMin) e.gapMin = gap;
                        if (gap > e.gapMax) e.gapMax = gap;
                    }
                    if (e.clearAfterS >= 0f && elapsed >= e.stoppedAt + e.clearAfterS)
                    {
                        if (e.go != null) e.go.SetActive(false);
                        e.removed = true;
                    }
                }
            }
        }

        /// <summary>The route part of a step (NAV-08): the car's place on the
        /// route and its cross-track error. For an item placed by arc length:
        /// the most the car left the route within 6 m of it, and how far past
        /// its face the car was back within 3 cm.</summary>
        public void Track(float s, float ey)
        {
            foreach (var e in entries)
            {
                if (!e.moving && !float.IsNaN(e.moveWhen) && s >= e.moveWhen) e.moving = true;
                if (float.IsNaN(e.s)) continue;
                if (Mathf.Abs(s - e.s) < 6f) e.detourMax = Mathf.Max(e.detourMax, Mathf.Abs(ey));
                if (e.rejoinM < 0f && s > e.s + e.halfDepth + 0.1f && Mathf.Abs(ey) < 0.03f) e.rejoinM = s - e.s;
            }
        }

        /// <summary>How much the car moved while it stood short of a static
        /// item (the gap's range from its first stop until the item was
        /// removed), mm; -1 if it never stood short of one.</summary>
        public float CreepMm
        {
            get
            {
                float m = -1f;
                foreach (var e in entries)
                    if (e.stoppedAt >= 0f && e.vel.sqrMagnitude == 0f && !float.IsNaN(e.gapMax))
                        m = Mathf.Max(m, (e.gapMax - e.gapMin) * 1000f);
                return m;
            }
        }

        /// <summary>The most the car left the route near an item placed by
        /// arc length, mm; -1 without one.</summary>
        public float DetourMaxMm
        {
            get
            {
                float m = -1f;
                foreach (var e in entries) if (!float.IsNaN(e.s)) m = Mathf.Max(m, e.detourMax * 1000f);
                return m;
            }
        }

        /// <summary>How far past such an item's face the car was back on the
        /// route, m (the longest of them); -1 if never.</summary>
        public float RejoinM
        {
            get
            {
                float m = -1f;
                foreach (var e in entries) if (e.rejoinM >= 0f) m = Mathf.Max(m, e.rejoinM);
                return m;
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

        /// <summary>Items the car knocked out of place (dynamic ones only).</summary>
        public int Hit
        {
            get { int n = 0; foreach (var e in entries) if (e.hit) n++; return n; }
        }

        /// <summary>1 when an item was removed during the run.</summary>
        public int Removed
        {
            get { foreach (var e in entries) if (e.removed) return 1; return 0; }
        }
    }
}
