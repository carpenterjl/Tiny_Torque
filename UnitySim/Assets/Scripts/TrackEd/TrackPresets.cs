using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Built-in track maps, defined in code (like <see cref="Garage.VehiclePresets"/>)
    /// so they always exist and stay in sync with the catalog. Each is themed to a
    /// vehicle archetype — a whoops course for the buggy, a smooth GP circuit for the
    /// F1 car, a boulder field for the crawler, a low-grip yard for the drift car —
    /// and is assembled only from catalog floors/items/splines, so it loads and edits
    /// in the Track Builder like any user map. Pickers show them with a ★ prefix.
    /// </summary>
    public static class TrackPresets
    {
        public const string Prefix = "★ ";

        // Floor-type ids = index into TrackCatalog.Floors (append-only order).
        private const int Dirt = 0, Asphalt = 1, Grass = 2, Sand = 3, Ice = 4, Mud = 5, Rumble = 6, Boost = 7, Checker = 8;

        public static readonly (string name, Func<TrackDesign> build)[] All =
        {
            ("Whoop Canyon", WhoopCanyon),
            ("Monza Mini",   MonzaMini),
            ("Boulder Basin", BoulderBasin),
            ("Slide Yard",   SlideYard),
            // Dedicated race circuits (closed splines, boost pads, jumps).
            ("Boost Speedway",   BoostSpeedway),
            ("Dust Devil Rally", DustDevilRally),
            ("Neon Vortex",      NeonVortex),
            // Not a circuit: a straight-line measurement range for the Opus Vector
            // mission firmware.
            ("Opus Proving Ground", OpusProvingGround),
        };

        public static List<string> DisplayNames()
        {
            var list = new List<string>(All.Length);
            foreach (var p in All) list.Add(Prefix + p.name);
            return list;
        }

        public static TrackDesign Resolve(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            string bare = display.StartsWith(Prefix) ? display.Substring(Prefix.Length) : display;
            foreach (var p in All)
                if (p.name == bare) { var d = p.build(); d.EnsureFloor(); d.EnsureSplines(); return d; }
            return null;
        }

        public static bool IsPreset(string display) => Resolve(display) != null;

        // ---- helpers ---------------------------------------------------------

        private static TrackDesign New(string name, int w, int l, int fill)
        {
            var d = new TrackDesign { name = name, width = w, length = l, floor = new int[w * l] };
            for (int i = 0; i < d.floor.Length; i++) d.floor[i] = fill;
            return d;
        }

        private static void PaintRect(TrackDesign d, int x0, int z0, int x1, int z1, int type)
        {
            for (int z = Mathf.Min(z0, z1); z <= Mathf.Max(z0, z1); z++)
                for (int x = Mathf.Min(x0, x1); x <= Mathf.Max(x0, x1); x++)
                    d.SetFloor(x, z, type);
        }

        private static PlacedItem It(string id, float x, float z, float yaw = 0f, float y = 0f, int order = -1) =>
            new PlacedItem { itemId = id, x = x, z = z, yawDeg = yaw, y = y, order = order };

        /// <summary>Build a closed/open spline from world points (y = height).</summary>
        private static SplineSpec Spline(Vector3[] pts, float width, int surface, bool closed,
            bool walls, bool stripes, float[] roll = null)
        {
            var s = new SplineSpec { closed = closed, edgeWalls = walls, edgeStripes = stripes };
            for (int i = 0; i < pts.Length; i++)
            {
                s.points.Add(pts[i]);
                s.widths.Add(width);
                s.surface.Add(surface);
                s.rollDeg.Add(roll != null && i < roll.Length ? roll[i] : 0f);
            }
            return s;
        }

        /// <summary>An oval ring of control points on the XZ plane (CCW from +X).</summary>
        private static Vector3[] Oval(float ax, float az, int n, float[] heights = null)
        {
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                float t = i * Mathf.PI * 2f / n;
                float y = heights != null && i < heights.Length ? heights[i] : 0f;
                pts[i] = new Vector3(Mathf.Cos(t) * ax, y, Mathf.Sin(t) * az);
            }
            return pts;
        }

        // ---- presets ---------------------------------------------------------

        /// <summary>Rough dirt whoops loop with jumps — home turf for the buggy.</summary>
        private static TrackDesign WhoopCanyon()
        {
            var d = New("Whoop Canyon", 40, 30, Dirt);
            // Sandy run-off around the outside corners.
            PaintRect(d, 0, 0, 39, 3, Sand);
            PaintRect(d, 0, 26, 39, 29, Sand);

            // Dirt ribbon loop with rolling whoop crests on the straights.
            float[] h = { 0f, 0.20f, 0f, 0.20f, 0f, 0.20f, 0f, 0.20f };
            var loop = Oval(15f, 10f, 8, h);
            d.splines.Add(Spline(loop, 2.6f, Dirt, closed: true, walls: true, stripes: false));

            // Jumps on the two long straights + tire-stack corner markers.
            d.items.Add(It("ramp", 15f, 0f, 90f));
            d.items.Add(It("platform", 11.5f, 0f, 90f));
            d.items.Add(It("ramp", -15f, 0f, 270f));
            d.items.Add(It("tire_stack", 11f, 7f));
            d.items.Add(It("tire_stack", -11f, 7f));
            d.items.Add(It("tire_stack", 11f, -7f));
            d.items.Add(It("tire_stack", -11f, -7f));

            // Start/finish at the bottom of the loop; ordered checkpoints round it.
            d.items.Add(It("finish", 0f, -10f, 0f));
            d.items.Add(It("checkpoint", 15f, 0f, 90f, 0f, 0));
            d.items.Add(It("checkpoint", 0f, 10f, 0f, 0f, 1));
            d.items.Add(It("checkpoint", -15f, 0f, 90f, 0f, 2));
            d.items.Add(It("spawn", 0f, -11.3f, 90f));
            return d;
        }

        /// <summary>Smooth wide asphalt GP circuit with mild banking — for the F1 car.</summary>
        private static TrackDesign MonzaMini()
        {
            var d = New("Monza Mini", 50, 36, Grass);

            // Wide smooth circuit; slight bank into the two ends.
            float[] roll = { 0f, 6f, 0f, 0f, 0f, 6f, 0f, 0f };
            var loop = Oval(19f, 12f, 8);
            var s = Spline(loop, 3.2f, Asphalt, closed: true, walls: false, stripes: true, roll);
            // Rumble apex kerbs on the banked corners (segment surface at those points).
            s.surface[1] = Rumble;
            s.surface[5] = Rumble;
            d.splines.Add(s);

            // Barrier walls flanking the main straight + a cone chicane.
            d.items.Add(It("barrier", 0f, 12.6f, 0f));
            d.items.Add(It("barrier", 0f, -12.6f, 0f));
            d.items.Add(It("cone", 5f, 0f));
            d.items.Add(It("cone", -5f, 0f));

            d.items.Add(It("finish", 0f, -12f, 0f));
            d.items.Add(It("checkpoint", 19f, 0f, 90f, 0f, 0));
            d.items.Add(It("checkpoint", 0f, 12f, 0f, 0f, 1));
            d.items.Add(It("checkpoint", -19f, 0f, 90f, 0f, 2));
            d.items.Add(It("spawn", 0f, -13.4f, 90f));
            return d;
        }

        /// <summary>Boulder field of stepped platforms and mud — crawler articulation test.</summary>
        private static TrackDesign BoulderBasin()
        {
            var d = New("Boulder Basin", 30, 30, Dirt);
            // Muddy hollows scattered through the basin.
            PaintRect(d, 4, 4, 10, 10, Mud);
            PaintRect(d, 18, 16, 25, 23, Mud);
            PaintRect(d, 8, 18, 14, 24, Sand);

            // No ribbon — a spread of stepped platforms and ridges to clamber over.
            d.items.Add(It("platform", -8f, -6f, 0f, 0.06f));
            d.items.Add(It("platform", -3f, -2f, 30f, 0.14f));
            d.items.Add(It("platform", 2f, 3f, 15f, 0.22f));
            d.items.Add(It("ramp", 6f, 6f, 45f));
            d.items.Add(It("platform", 9f, 9f, 0f, 0.25f)); // summit
            d.items.Add(It("speed_bump", -6f, 2f, 90f));
            d.items.Add(It("speed_bump", 4f, -4f, 0f));
            d.items.Add(It("wall_small", 0f, 0f, 0f));
            d.items.Add(It("wall_small", 1.2f, 0f, 0f));

            // Summit finish; a couple of gates to route the climb.
            d.items.Add(It("finish", 9f, 11f, 0f));
            d.items.Add(It("checkpoint", -3f, -2f, 30f, 0.14f, 0));
            d.items.Add(It("checkpoint", 2f, 3f, 15f, 0.22f, 1));
            d.items.Add(It("spawn", -11f, -11f, 45f));
            return d;
        }

        /// <summary>Low-grip ice/asphalt yard, open horseshoe — for provoking drifts.</summary>
        private static TrackDesign SlideYard()
        {
            var d = New("Slide Yard", 34, 34, Asphalt);
            // Patchwork of ice to break traction mid-corner.
            PaintRect(d, 4, 4, 12, 12, Ice);
            PaintRect(d, 20, 20, 29, 29, Ice);
            PaintRect(d, 6, 22, 12, 28, Ice);

            // Open horseshoe ribbon (asphalt) — plenty of room to slide.
            var pts = new[]
            {
                new Vector3(-12f, 0f, -12f),
                new Vector3(-12f, 0f, 8f),
                new Vector3(-4f, 0f, 13f),
                new Vector3(8f, 0f, 13f),
                new Vector3(13f, 0f, 4f),
                new Vector3(13f, 0f, -12f),
            };
            d.splines.Add(Spline(pts, 3.4f, Asphalt, closed: false, walls: false, stripes: false));

            // Tire-stack outer walls + clipping cones.
            d.items.Add(It("tire_stack", -13.5f, 0f));
            d.items.Add(It("tire_stack", 14.5f, 0f));
            d.items.Add(It("cone", 0f, 13f));
            d.items.Add(It("cone", -4f, 13f));
            d.items.Add(It("cone", 8f, 13f));

            d.items.Add(It("finish", -12f, -8f, 0f));
            d.items.Add(It("checkpoint", -8f, 13f, 0f, 0f, 0));
            d.items.Add(It("checkpoint", 13f, 0f, 90f, 0f, 1));
            d.items.Add(It("spawn", -12f, -10.5f, 0f));
            return d;
        }

        // ---- dedicated race circuits ----------------------------------------

        /// <summary>Fast asphalt oval with boost-pad straights and kerbed corners.</summary>
        private static TrackDesign BoostSpeedway()
        {
            var d = New("Boost Speedway", 54, 38, Grass);
            var loop = Oval(22f, 13f, 8);
            var s = Spline(loop, 3.4f, Asphalt, closed: true, walls: false, stripes: true);
            s.surface[2] = Boost;   // top straight — boost pad
            s.surface[6] = Boost;   // bottom straight — boost pad
            s.surface[0] = Rumble;  // right-hand kerb
            s.surface[4] = Rumble;  // left-hand kerb
            d.splines.Add(s);

            // Chicane cones + flanking barriers (obstacles).
            d.items.Add(It("cone", 5f, 13f));
            d.items.Add(It("cone", -5f, -13f));
            d.items.Add(It("barrier", 0f, 15f, 0f));
            d.items.Add(It("barrier", 0f, -15f, 0f));

            d.items.Add(It("finish", 0f, -13f, 0f));
            d.items.Add(It("checkpoint", 22f, 0f, 90f, 0f, 0));
            d.items.Add(It("checkpoint", 0f, 13f, 0f, 0f, 1));
            d.items.Add(It("checkpoint", -22f, 0f, 90f, 0f, 2));
            d.items.Add(It("spawn", -4f, -13f, 90f));
            return d;
        }

        /// <summary>Dirt rally loop with jumps, a boost blast, and sandy run-offs.</summary>
        private static TrackDesign DustDevilRally()
        {
            var d = New("Dust Devil Rally", 44, 34, Dirt);
            PaintRect(d, 0, 0, 43, 3, Sand);
            PaintRect(d, 0, 30, 43, 33, Sand);

            float[] h = { 0f, 0.18f, 0f, 0.18f, 0f, 0.18f, 0f, 0.18f };
            var loop = Oval(17f, 11f, 8, h);
            var s = Spline(loop, 2.8f, Dirt, closed: true, walls: true, stripes: false);
            s.surface[2] = Boost;   // boost out of the top sweeper
            d.splines.Add(s);

            // Jumps on the straights + tire-stack / cone hazards.
            d.items.Add(It("ramp", 6f, 11f, 90f));
            d.items.Add(It("ramp", -6f, -11f, 90f));
            d.items.Add(It("tire_stack", 12f, 8f));
            d.items.Add(It("tire_stack", -12f, -8f));
            d.items.Add(It("cone", 17f, 3f));
            d.items.Add(It("cone", -17f, -3f));

            d.items.Add(It("finish", 0f, -11f, 0f));
            d.items.Add(It("checkpoint", 17f, 0f, 90f, 0f, 0));
            d.items.Add(It("checkpoint", 0f, 11f, 0f, 0f, 1));
            d.items.Add(It("checkpoint", -17f, 0f, 90f, 0f, 2));
            d.items.Add(It("spawn", -4f, -11f, 90f));
            return d;
        }

        /// <summary>Technical asphalt circuit (rounded-rectangle spline) with a boost
        /// straight, a jump, and a grass infield that punishes running wide.</summary>
        private static TrackDesign NeonVortex()
        {
            var d = New("Neon Vortex", 40, 40, Asphalt);
            PaintRect(d, 15, 15, 24, 24, Grass); // infield

            // Counter-clockwise rounded rectangle (bottom straight travels +X).
            var pts = new[]
            {
                new Vector3(6f, 0f, -14f),
                new Vector3(15f, 0f, -6f),
                new Vector3(15f, 0f, 6f),
                new Vector3(8f, 0f, 14f),
                new Vector3(-6f, 0f, 14f),
                new Vector3(-14f, 0f, 9f),
                new Vector3(-14f, 0f, -9f),
                new Vector3(-6f, 0f, -14f),
            };
            var s = Spline(pts, 3.0f, Asphalt, closed: true, walls: false, stripes: true);
            s.surface[5] = Boost;   // boost down the left straight
            s.surface[1] = Rumble;  // kerb on the right straight
            d.splines.Add(s);

            // A jump on the top straight + clipping cones.
            d.items.Add(It("ramp", 0f, 14f, 90f));
            d.items.Add(It("cone", 4f, 14f));
            d.items.Add(It("cone", -4f, 14f));

            d.items.Add(It("finish", 0f, -14f, 0f));
            d.items.Add(It("checkpoint", 15f, 0f, 90f, 0f, 0));
            d.items.Add(It("checkpoint", 0f, 14f, 0f, 0f, 1));
            d.items.Add(It("checkpoint", -14f, 0f, 90f, 0f, 2));
            d.items.Add(It("spawn", -4f, -14f, 90f));
            return d;
        }

        /// <summary>
        /// Opus Proving Ground — a straight-line measurement range, not a circuit.
        /// It exists to run and score the Opus Vector's mission firmware
        /// (<c>Controllers/opus_mission</c>), whose manoeuvre is: accelerate to
        /// 4.5 m/s, hold it for exactly 14.5 m, turn 45° left without lifting,
        /// run exactly 7.5 m more, then stop in exactly 1.5 m.
        ///
        /// The layout is derived from that manoeuvre rather than drawn by eye. The
        /// car spawns heading +X at (−15, −5); every station below is a real
        /// distance along the driven path, and the corner's radius is the one the
        /// controller actually commands (5.06 m, from a 4.0 m/s² lateral-
        /// acceleration budget — see Opus_Car_Spec/derived_parameters.md §9), so
        /// the ribbon and the car agree on where the road goes.
        ///
        /// | station | path distance | position |
        /// |---|---|---|
        /// | spawn | −5.0 m | (−15.0, −5.0) |
        /// | measured leg starts | 0 m | (−10.0, −5.0) |
        /// | turn entry | 14.5 m | (4.5, −5.0) |
        /// | turn exit (45°) | 18.5 m | (8.08, −3.52) |
        /// | stop target | 27.5 m | (14.44, 2.85) |
        ///
        /// Surface is asphalt end to end: braking distance is the tightest of the
        /// four tolerances, and it is a direct function of grip, so the one thing
        /// this track must not do is change surface under the car mid-stop.
        /// </summary>
        private static TrackDesign OpusProvingGround()
        {
            var d = New("Opus Proving Ground", 40, 20, Grass);

            // Sand aprons past each end of the strip — visually reads as run-off,
            // and gives a stray car something slow to wander into.
            PaintRect(d, 0, 0, 3, 19, Sand);
            PaintRect(d, 36, 0, 39, 19, Sand);

            // The driven path. Straight legs are collinear so Catmull-Rom keeps
            // them dead straight (an open spline clamps its end tangents); the
            // corner is sampled every 15° of arc, and the control points either
            // side of it are spaced to match, because uniform Catmull-Rom through
            // unevenly-spaced points bulges at the spacing change.
            var pts = new[]
            {
                new Vector3(-17.000f, 0f, -5.000f),
                new Vector3(-12.000f, 0f, -5.000f),
                new Vector3( -7.000f, 0f, -5.000f),
                new Vector3( -2.000f, 0f, -5.000f),
                new Vector3(  1.500f, 0f, -5.000f),
                new Vector3(  3.000f, 0f, -5.000f),
                new Vector3(  4.500f, 0f, -5.000f),   // turn entry — 14.5 m
                new Vector3(  5.810f, 0f, -4.827f),   // 15° of arc
                new Vector3(  7.031f, 0f, -4.322f),   // 30°
                new Vector3(  8.080f, 0f, -3.517f),   // 45° — turn exit
                new Vector3(  9.140f, 0f, -2.457f),   // +1.5 m
                new Vector3( 10.201f, 0f, -1.396f),   // +3.0 m
                new Vector3( 12.323f, 0f,  0.726f),   // +6.0 m
                new Vector3( 14.444f, 0f,  2.847f),   // +9.0 m — stop target
                new Vector3( 16.560f, 0f,  4.970f),   // run-off
            };
            // 3 m wide: the car is 0.2 m, so ±1.4 m of lateral margin. Generous on
            // purpose — the mission is dead-reckoned, and an uncalibrated odometer
            // puts the corner in the wrong place by a good fraction of a metre.
            d.splines.Add(Spline(pts, 3.0f, Asphalt, closed: false, walls: false, stripes: true));

            // Distance-marker cone pairs, set 2.4 m off the centreline — outside
            // the 1.5 m ribbon half-width, so clipping one means the run was
            // already lost.
            void Markers(float x, float z, float nx, float nz)
            {
                d.items.Add(It("cone", x + nx * 2.4f, z + nz * 2.4f));
                d.items.Add(It("cone", x - nx * 2.4f, z - nz * 2.4f));
            }
            Markers(-10.0f, -5.0f, 0f, 1f);   //  0 m — measured leg starts
            Markers( -5.0f, -5.0f, 0f, 1f);   //  5 m
            Markers(  0.0f, -5.0f, 0f, 1f);   // 10 m
            Markers(  4.5f, -5.0f, 0f, 1f);   // 14.5 m — turn entry
            const float Diag = 0.70710678f;   // normal to the 45° exit leg
            Markers(10.201f, -1.396f, -Diag, Diag);   // +3 m
            Markers(12.323f,  0.726f, -Diag, Diag);   // +6 m

            // Gates. yawDeg is the heading of travel through each gate, so the bar
            // lies ACROSS the road: the item's posts span its local X while its
            // local +Z is the heading, which is the same convention the spawn
            // arrow uses.
            //
            // A gate's posts are SOLID and stand only 0.65 m either side of the
            // centreline, so a gate is a 1.3 m gap — narrower than the lateral
            // error a dead-reckoned car can legitimately carry. There is exactly
            // one place on this course where that error peaks (the turn exit,
            // where every degree of yaw-tracking error has just been integrated
            // into a sideways offset), and a gate there turns a 4 cm heading
            // miss into a crash. The first full run proved it: the ToF saw a post
            // at 0.87 m and the car hit it 0.17 s later. So gates go only where
            // the car is travelling straight and its lateral error is small.
            // The stop target is marked by cones set OUTSIDE the ribbon, not by a
            // gate. A gate on the stop line was the second version of the same
            // mistake: the car arrives there at 4.4 m/s carrying whatever lateral
            // error the dead-reckoned corner left it with, and it clipped a post
            // and spun. Lateral position is not something this mission controls —
            // it controls distance and heading — so nothing solid may stand where
            // lateral error accumulates.
            d.items.Add(It("cone", 14.444f - Diag * 1.7f, 2.847f + Diag * 1.7f));
            d.items.Add(It("cone", 14.444f + Diag * 1.7f, 2.847f - Diag * 1.7f));

            d.items.Add(It("finish", 16.560f, 4.970f, 45f));            // end of the run-off
            d.items.Add(It("checkpoint", 4.500f, -5.000f, 90f, 0f, 0)); // turn entry
            d.items.Add(It("spawn", -15.000f, -5.000f, 90f));           // heading +X

            // Lighting down the strip, and something soft at the far end.
            d.items.Add(It("light_post", -12f, -8.5f, 0f));
            d.items.Add(It("light_post",  -2f, -8.5f, 0f));
            d.items.Add(It("light_post",   8f, -8.5f, 0f));
            d.items.Add(It("light_post",  13f,  6.5f, 180f));
            d.items.Add(It("tire_stack", 18.2f, 6.4f));
            d.items.Add(It("tire_stack", 19.4f, 7.4f));
            d.items.Add(It("tire_stack", -18.6f, -5.0f));
            return d;
        }
    }
}
