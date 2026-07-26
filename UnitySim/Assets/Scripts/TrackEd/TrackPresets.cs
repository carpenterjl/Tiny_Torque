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
        // Themed surfaces (iteration 24). Carpet, wet sand and lava rock sit below
        // the 0.90 arcade track-limit threshold, so painting them as run-off makes
        // them count as off-track with no extra authoring.
        private const int Wood = 9, Carpet = 10, Neon = 11, Plank = 12,
                          WetSand = 13, LavaRock = 14, Obsidian = 15, Grate = 16;

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
            // Themed arcade circuits (iteration 24): built from the Blender prop
            // families, each with authored item boxes so ArcadeDirector's
            // automatic placement stays out of the way.
            ("Workshop Grand Prix", WorkshopGrandPrix),
            ("Neon Vortex II",      NeonVortexII),
            ("Boardwalk Cove",      BoardwalkCove),
            ("Foundry Descent",     FoundryDescent),
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

        /// <summary>
        /// Build a closed/open spline from world points (y = height). `width`,
        /// `surface` and a flat 0° bank are the defaults; the three optional
        /// parallel arrays override them per control point, which is what turns a
        /// flat constant-width oval into a real circuit — elevation lives in the
        /// points' y, banking in `roll`, pinch points and sweepers in `widths`,
        /// and boost pads / kerbs / low-grip patches in `surfaces` (segment i→i+1
        /// takes point i's surface).
        /// </summary>
        private static SplineSpec Spline(Vector3[] pts, float width, int surface, bool closed,
            bool walls, bool stripes, float[] roll = null, float[] widths = null, int[] surfaces = null)
        {
            var s = new SplineSpec { closed = closed, edgeWalls = walls, edgeStripes = stripes };
            for (int i = 0; i < pts.Length; i++)
            {
                s.points.Add(pts[i]);
                s.widths.Add(widths != null && i < widths.Length ? widths[i] : width);
                s.surface.Add(surfaces != null && i < surfaces.Length ? surfaces[i] : surface);
                s.rollDeg.Add(roll != null && i < roll.Length ? roll[i] : 0f);
            }
            return s;
        }

        /// <summary>
        /// A row of three item boxes across the racing line. `alongX` says which
        /// way the cars are travelling there, so the row always spans the track
        /// rather than lying down it; `y` is the deck height, which matters once
        /// the ribbon climbs (TrackFactory drops each item from y+3, so a box on
        /// an elevated section still lands on the deck rather than the floor).
        /// Narrow the `spread` on a pinched section or the outer boxes hang off.
        /// </summary>
        private static void BoxRow(TrackDesign d, float x, float z, bool alongX, float y = 0f, float spread = 0.9f)
        {
            for (int k = -1; k <= 1; k++)
                d.items.Add(alongX ? It("item_box", x, z + k * spread, 0f, y)
                                   : It("item_box", x + k * spread, z, 0f, y));
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

        // ---- themed arcade circuits (iteration 24) ---------------------------
        //
        // Each of these four is a DIFFERENT shape, not one oval in four colours.
        // The spline carries elevation in its points' y, banking in rollDeg, and
        // width per control point, so the circuit itself is the content and the
        // props only dress it:
        //
        //   Workshop Grand Prix — climbs 1.3 m off the bench onto a narrow plank
        //                         run along the top, then drops back down.
        //   Neon Vortex II      — a true figure-8: the lap crosses over itself at
        //                         the map centre, low branch under, high branch
        //                         1.2 m over, and the banking inverts through it.
        //   Boardwalk Cove      — a whoops rhythm section into a 22°-banked bowl,
        //                         out onto a 2 m pier, back down to the beach.
        //   Foundry Descent     — a steady climb to a 1.9 m gantry, a narrow grate
        //                         bridge, then a plunge with a crest at the top.
        //
        // Gradients stay under ~11%: at RC scale that is dramatic to look at (the
        // car is 0.10 m tall) while costing almost nothing in speed — the rear
        // pair make ~50 N of thrust against ~3 N of grade resistance.
        //
        // `yawDeg` on every gate is the HEADING OF TRAVEL through it (+X = 90,
        // +Z = 0, −X = 270, −Z = 180), so the bar lies across the road; on a
        // curved section it is the Catmull-Rom tangent, P(i+1) − P(i−1).
        //
        // Two placement rules that are invisible until they bite:
        //   * TrackFactory drops each item from y+3 and takes the HIGHEST hit, so
        //     an item under an overpass snaps onto the overpass. Nothing is placed
        //     beneath the Vortex crossover.
        //   * The narrow-bore props — tape arch, light hoop, rock arch — stay off
        //     the racing line. Their openings are 0.34–0.46 m against a 0.20 m
        //     car, which is a coin flip at speed. Hazards that ARE on the line
        //     (pencils, barrels, beach balls, blocks) are things you can hit and
        //     survive.

        /// <summary>
        /// ★ Workshop Grand Prix — the RC car's own scale, played straight. The
        /// lap starts on the varnished bench, climbs 1.3 m up the right-hand side
        /// onto a stack of books, runs the top as a 2.2 m plank with pencils
        /// rolling loose across it, then drops back to the bench down the left.
        /// Carpet everywhere off the ribbon: 0.80 friction, so it is both slow and
        /// off-track for the arcade limits rule.
        /// </summary>
        private static TrackDesign WorkshopGrandPrix()
        {
            var d = New("Workshop Grand Prix", 40, 36, Wood);
            PaintRect(d, 0, 0, 39, 2, Carpet);
            PaintRect(d, 0, 33, 39, 35, Carpet);
            PaintRect(d, 0, 0, 2, 35, Carpet);
            PaintRect(d, 37, 0, 39, 35, Carpet);
            PaintRect(d, 12, 12, 27, 23, Carpet);   // infield

            // Bench level round the bottom, up the right, along the top at 1.30 m,
            // down the left. The plank (P5→P6) is the narrow bit that hurts.
            var pts = new[]
            {
                new Vector3(-6f, 0.00f, -13f),  // 0  start straight, +X
                new Vector3(5f, 0.00f, -13f),   // 1  ruler ramp sits here
                new Vector3(13f, 0.20f, -9f),   // 2  turn-in, climb begins
                new Vector3(16f, 0.75f, -1f),   // 3  climbing, banked
                new Vector3(14f, 1.15f, 7f),    // 4
                new Vector3(6f, 1.30f, 12f),    // 5  top of the book stack
                new Vector3(-5f, 1.30f, 13f),   // 6  the plank, travelling −X
                new Vector3(-13f, 1.05f, 9f),   // 7  the drop starts
                new Vector3(-16f, 0.30f, 1f),   // 8  descent
                new Vector3(-13f, 0.00f, -8f),  // 9  back on the bench
            };
            var widths  = new[] { 3.6f, 3.6f, 3.2f, 3.0f, 2.6f, 2.4f, 2.2f, 2.8f, 3.2f, 3.4f };
            var roll    = new[] { 0f, 0f, -6f, -10f, -8f, 0f, 0f, -5f, -8f, -4f };
            var surfs   = new[] { Wood, Wood, Rumble, Wood, Wood, Wood, Wood, Wood, Wood, Boost };
            // Walls matter here: the plank is 2.2 m at 1.3 m up.
            d.splines.Add(Spline(pts, 3.0f, Wood, closed: true, walls: true, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            // On the line. The ruler is the launch into the climb; the pencils are
            // loose on the plank, where there is nowhere to go.
            d.items.Add(It("tw_ruler_ramp", 2f, -13f, 90f));
            d.items.Add(It("tw_pencil", 2f, 12.4f, 90f, 1.30f));
            d.items.Add(It("tw_pencil", -2f, 12.8f, 90f, 1.30f));
            // Bricks stagger the start straight into a weave.
            d.items.Add(It("tw_brick_wall", -3f, -11.9f));
            d.items.Add(It("tw_brick_wall", 1f, -14.1f));

            // Books stacked under and around the high section — they are what the
            // track is standing on, so they go where the ribbon is in the air.
            d.items.Add(It("tw_book_stack", 18.6f, 4f));
            d.items.Add(It("tw_book_stack", 18.6f, 8f));
            d.items.Add(It("tw_book_stack", 10f, 15.6f));
            d.items.Add(It("tw_book_stack", -14f, 14f));
            d.items.Add(It("tw_brick_wall", -18.6f, -2f));
            d.items.Add(It("tw_brick_wall", -18.6f, 3f));

            // Landmarks at bench level, where they read against the raised loop.
            d.items.Add(It("tw_tape_arch", 9f, -16.2f, 90f));
            d.items.Add(It("tw_tape_arch", -9f, -16.4f, 90f));
            d.items.Add(It("tw_mug", 18.5f, -6f));
            d.items.Add(It("tw_mug", -19f, -5f));
            d.items.Add(It("tw_mug", 4f, 16.4f));

            BoxRow(d, 0f, -13f, alongX: true);
            BoxRow(d, 16f, -1f, alongX: false, y: 0.75f);
            BoxRow(d, 0f, 12.7f, alongX: true, y: 1.30f, spread: 0.6f);   // on the plank
            BoxRow(d, -16f, 1f, alongX: false, y: 0.30f);

            d.items.Add(It("finish", -1f, -13f, 90f));
            d.items.Add(It("checkpoint", 16f, -1f, 0f, 0.75f, 0));
            d.items.Add(It("checkpoint", -5f, 13f, 261f, 1.30f, 1));
            d.items.Add(It("checkpoint", -16f, 1f, 180f, 0.30f, 2));
            d.items.Add(It("spawn", -5f, -13f, 90f));
            return d;
        }

        /// <summary>
        /// ★ Neon Vortex II — a true figure-8. One lap crosses over itself at the
        /// map centre: the north lobe is entered low heading north-east, and the
        /// track returns to the same spot 1.2 m higher heading south-east, so the
        /// bridge passes over its own road. Because the two lobes are traversed in
        /// opposite senses the banking inverts through the crossover — 18° one way
        /// round, 18° the other — which is where the name comes from.
        ///
        /// The two centre control points are deliberately offset 1.7 m diagonally
        /// rather than sharing an exact XZ position: coincident points would give
        /// SplineMath a degenerate tangent, and the offset still leaves the 2.4 m
        /// decks overlapping. Clearance under the bridge is ~1.15 m against a
        /// 0.10 m car.
        /// </summary>
        private static TrackDesign NeonVortexII()
        {
            var d = New("Neon Vortex II", 44, 44, Neon);
            PaintRect(d, 0, 0, 43, 2, Asphalt);
            PaintRect(d, 0, 41, 43, 43, Asphalt);
            PaintRect(d, 15, 27, 28, 38, Asphalt);   // north lobe infield
            PaintRect(d, 15, 5, 28, 16, Asphalt);    // south lobe infield

            var pts = new[]
            {
                new Vector3(0.6f, 0.00f, -0.6f),  // 0  centre, LOW, heading NE
                new Vector3(9f, 0.10f, 6f),       // 1
                new Vector3(14f, 0.35f, 13f),     // 2  north lobe, east side
                new Vector3(0f, 0.55f, 18f),      // 3  north apex
                new Vector3(-14f, 0.40f, 13f),    // 4  north lobe, west side
                new Vector3(-9f, 0.85f, 6f),      // 5  climbing to the bridge
                new Vector3(-0.6f, 1.20f, 0.6f),  // 6  centre, HIGH, heading SE
                new Vector3(9f, 0.85f, -6f),      // 7
                new Vector3(14f, 0.45f, -13f),    // 8  south lobe, east side
                new Vector3(5f, 0.25f, -18f),     // 9  start/finish straight, −X
                new Vector3(-5f, 0.25f, -18f),    // 10
                new Vector3(-14f, 0.40f, -13f),   // 11 south lobe, west side
                new Vector3(-9f, 0.15f, -6f),     // 12 diving back under the bridge
            };
            // North lobe is taken anticlockwise (left-hand), south lobe clockwise
            // (right-hand); rollDeg is +ve for right-edge-down, so the sign flips
            // with the direction of turn.
            // The start/finish straight (9→10) is deliberately flat: a banked grid
            // would have the field sliding before the lights go out.
            var roll = new[] { 0f, -8f, -16f, -18f, -16f, -8f,
                               0f, 8f, 16f, 0f, 0f, 16f, 8f };
            var widths = new[] { 2.6f, 3.0f, 3.4f, 3.6f, 3.4f, 3.0f,
                                 2.4f, 3.0f, 3.4f, 3.6f, 3.6f, 3.4f, 3.0f };
            var surfs = new[] { Neon, Neon, Neon, Rumble, Neon, Boost,
                                Neon, Neon, Neon, Neon, Rumble, Neon, Boost };
            d.splines.Add(Spline(pts, 3.0f, Neon, closed: true, walls: true, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            // Barriers stagger the bridge approach, where the road is narrowest.
            d.items.Add(It("ng_barrier_glow", -7.5f, 5.0f, 40f, 0.90f));
            d.items.Add(It("ng_barrier_glow", -4.0f, 2.6f, 40f, 1.05f));

            // Start gate on the bottom straight, where the field is lined up.
            d.items.Add(It("ng_arch_gate", 2f, -18f, 270f, 0.25f));

            // Pylons trace the outside of both lobes rather than sitting in rows.
            foreach (float z in new[] { 9f, 15f, 19f })
            {
                d.items.Add(It("ng_pylon", 17f, z - 4f));
                d.items.Add(It("ng_pylon", -17f, z - 4f));
            }
            foreach (float z in new[] { -9f, -15f, -19f })
            {
                d.items.Add(It("ng_pylon", 17f, z + 4f));
                d.items.Add(It("ng_pylon", -17f, z + 4f));
            }

            // Hoops frame the bridge from the side; stacks and spires build the
            // skyline. Nothing goes under the crossover — it would snap onto it.
            d.items.Add(It("ng_ring_float", 19f, 0f, 90f));
            d.items.Add(It("ng_ring_float", -19f, 0f, 270f));
            d.items.Add(It("ng_data_cube", 17.5f, -4f));
            d.items.Add(It("ng_data_cube", -17.5f, 4f));
            d.items.Add(It("ng_spire", 20.5f, -17f));
            d.items.Add(It("ng_spire", -20.5f, 17f));
            d.items.Add(It("ng_spire", 20f, 18f));
            d.items.Add(It("ng_spire", -20f, -18f));

            BoxRow(d, 0f, -18f, alongX: true, y: 0.25f);
            BoxRow(d, 14f, 12.5f, alongX: false, y: 0.35f);
            BoxRow(d, -0.6f, 0.6f, alongX: true, y: 1.20f, spread: 0.55f);  // on the bridge
            BoxRow(d, -14f, -12.5f, alongX: false, y: 0.40f);

            d.items.Add(It("finish", 0f, -18f, 270f, 0.25f));
            d.items.Add(It("checkpoint", 14f, 13f, 323f, 0.35f, 0));
            d.items.Add(It("checkpoint", -0.6f, 0.6f, 124f, 1.20f, 1));   // on the bridge
            d.items.Add(It("checkpoint", -14f, -13f, 342f, 0.40f, 2));
            d.items.Add(It("spawn", 4f, -18f, 270f, 0.25f));
            return d;
        }

        /// <summary>
        /// ★ Boardwalk Cove — the rhythm map. The start straight is four whoops on
        /// a 6 m wavelength, which at ~10 m/s is a ~1.7 Hz pumping section that
        /// gets the car light over every crest; it fires you into a 4.4 m-wide bowl
        /// banked at 22°, out onto a 2.0 m pier raised over the tide, then down a
        /// ramp to a flat beach left-hander. Wet sand (0.45) is the infield cut:
        /// shorter, and off-track the whole way across.
        /// </summary>
        private static TrackDesign BoardwalkCove()
        {
            var d = New("Boardwalk Cove", 46, 40, Sand);
            PaintRect(d, 0, 0, 45, 3, WetSand);      // tide line
            PaintRect(d, 0, 36, 45, 39, WetSand);
            PaintRect(d, 14, 13, 31, 26, WetSand);   // the infield cut

            var pts = new[]
            {
                new Vector3(-9f, 0.00f, -14f),  // 0  finish, +X
                new Vector3(-3f, 0.40f, -14f),  // 1  whoop
                new Vector3(3f, 0.00f, -14f),   // 2  trough
                new Vector3(9f, 0.40f, -14f),   // 3  whoop — launch into the bowl
                new Vector3(15f, 0.10f, -10f),  // 4  turn-in
                new Vector3(19f, 0.60f, -3f),   // 5  THE BOWL, 22°
                new Vector3(19f, 0.60f, 4f),    // 6
                new Vector3(13f, 0.10f, 11f),   // 7  drop out of the bowl
                new Vector3(3f, 0.65f, 15f),    // 8  climb onto the pier
                new Vector3(-7f, 0.65f, 15f),   // 9  the pier, 2.0 m wide
                new Vector3(-15f, 0.25f, 9f),   // 10 ramp down
                new Vector3(-19f, 0.00f, 0f),   // 11 beach left-hander
                new Vector3(-17f, 0.00f, -9f),  // 12
                new Vector3(-13f, 0.25f, -14f), // 13 back onto the whoops
            };
            var widths = new[] { 3.2f, 3.2f, 3.2f, 3.2f, 3.6f, 4.4f, 4.4f,
                                 3.4f, 2.4f, 2.0f, 2.6f, 3.4f, 3.4f, 3.2f };
            var roll = new[] { 0f, 0f, 0f, 0f, -8f, -22f, -22f,
                               -10f, 0f, 0f, -6f, -14f, -10f, -4f };
            var surfs = new[] { Plank, Plank, Plank, Boost, Plank, Rumble, Plank,
                                Plank, Plank, Plank, Boost, Plank, Plank, Plank };
            d.splines.Add(Spline(pts, 3.2f, Plank, closed: true, walls: true, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            // On the line: a kicker on the pier, balls loose in the bowl where the
            // banking keeps feeding them back across the racing line, and a
            // sandcastle on the apex of the beach corner — clip it and it stops you.
            d.items.Add(It("bb_surfboard_ramp", -2f, 15f, 270f, 0.65f));
            d.items.Add(It("bb_beach_ball", 18.5f, -1f, 0f, 0.60f));
            d.items.Add(It("bb_beach_ball", 19.6f, 1f, 0f, 0.60f));
            d.items.Add(It("bb_beach_ball", 17.9f, 2.2f, 0f, 0.60f));
            d.items.Add(It("bb_sandcastle", -16.4f, 0f));

            // Railings on the outside of the whoops; palms and torches read the
            // height of the pier from the beach.
            foreach (float x in new[] { -6f, 0f, 6f })
                d.items.Add(It("bb_plank_wall", x, -16.2f));
            d.items.Add(It("bb_palm", 22f, -8f));
            d.items.Add(It("bb_palm", 21.5f, 7f));
            d.items.Add(It("bb_palm", -22f, -6f));
            d.items.Add(It("bb_palm", -21f, 6.5f));
            d.items.Add(It("bb_palm", 14f, 18f));
            d.items.Add(It("bb_palm", -14f, 18.2f));
            d.items.Add(It("bb_tiki_torch", 12f, -17.5f));
            d.items.Add(It("bb_tiki_torch", -12f, -17.5f));
            d.items.Add(It("bb_tiki_torch", 20f, 11f));
            d.items.Add(It("bb_tiki_torch", -20f, -12f));
            d.items.Add(It("bb_sandcastle", 21f, -14f));

            BoxRow(d, 0f, -14f, alongX: true, y: 0.20f);
            BoxRow(d, 19f, 0.5f, alongX: false, y: 0.60f, spread: 1.1f);   // the bowl
            BoxRow(d, -2f, 15f, alongX: true, y: 0.65f, spread: 0.5f);     // the pier
            BoxRow(d, -19f, -4f, alongX: false);

            d.items.Add(It("finish", -9f, -14f, 90f));
            d.items.Add(It("checkpoint", 19f, -3f, 16f, 0.60f, 0));
            d.items.Add(It("checkpoint", -7f, 15f, 252f, 0.65f, 1));
            d.items.Add(It("checkpoint", -19f, 0f, 186f, 0f, 2));
            d.items.Add(It("spawn", -12f, -14.2f, 90f));
            return d;
        }

        /// <summary>
        /// ★ Foundry Descent — an obsidian ribbon threaded over lava scree, with
        /// grate jumps, steam vents and a basalt arch on the hero corner. Obsidian
        /// is the grippiest surface in the catalog (1.20) and the scree either side
        /// is the loosest that still counts as ground, so the track punishes a
        /// wide line harder than any other map here.
        /// </summary>
        private static TrackDesign FoundryDescent()
        {
            var d = New("Foundry Descent", 42, 42, LavaRock);
            PaintRect(d, 14, 15, 27, 26, Obsidian);   // infield plate
            PaintRect(d, 0, 18, 3, 23, Grate);
            PaintRect(d, 38, 18, 41, 23, Grate);

            // The name is the layout: a steady boosted climb up the right to a
            // 1.90 m gantry, a narrow grate bridge across the top, then a 10.7%
            // plunge down the left. The crest at point 5→6 is convex, so the car
            // goes light exactly where the descent begins.
            var pts = new[]
            {
                new Vector3(-6f, 0.00f, -15f),  // 0  start straight, +X
                new Vector3(6f, 0.00f, -15f),   // 1
                new Vector3(14f, 0.35f, -10f),  // 2  grate ramp, climb begins
                new Vector3(17f, 1.10f, -2f),   // 3  climbing, banked 14°
                new Vector3(15f, 1.85f, 6f),    // 4  the gantry
                new Vector3(7f, 1.90f, 12f),    // 5  grate bridge, 2.2 m
                new Vector3(-4f, 1.75f, 14f),   // 6  crest — then it drops
                new Vector3(-13f, 0.70f, 10f),  // 7  the plunge
                new Vector3(-17f, 0.15f, 1f),   // 8  runout, banked 16°
                new Vector3(-15f, 0.00f, -8f),  // 9
                new Vector3(-11f, 0.00f, -14f), // 10
            };
            var widths = new[] { 3.4f, 3.4f, 3.0f, 2.8f, 2.6f, 2.2f,
                                 2.4f, 3.6f, 3.6f, 3.4f, 3.4f };
            var roll = new[] { 0f, 0f, -6f, -14f, -12f, 0f, 0f, -6f, -16f, -10f, -4f };
            var surfs = new[] { Obsidian, Obsidian, Boost, Obsidian, Grate, Grate,
                                Obsidian, Obsidian, Obsidian, Obsidian, Obsidian };
            d.splines.Add(Spline(pts, 3.0f, Obsidian, closed: true, walls: true, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            // On the line. The barrels sit on the descent, so a hit sends them
            // downhill into whoever is behind — the best hazard on any of these maps.
            d.items.Add(It("vf_grate_ramp", 11f, -12.5f, 58f, 0.20f));
            d.items.Add(It("vf_barrel", -9f, 11.5f, 0f, 0.95f));
            d.items.Add(It("vf_barrel", -10.4f, 10.2f, 0f, 0.85f));
            d.items.Add(It("vf_barrel", -15f, 4f, 0f, 0.35f));
            d.items.Add(It("vf_steam_vent", 4f, 13.2f, 0f, 1.85f));
            d.items.Add(It("vf_steam_vent", 0f, 14.2f, 0f, 1.78f));
            d.items.Add(It("vf_obsidian_block", 2f, -13.4f));
            d.items.Add(It("vf_obsidian_block", -2f, -16.6f));

            // Landmarks: the arches frame the climb and the runout, the crags
            // build the skyline behind the gantry.
            d.items.Add(It("vf_rock_arch", 19.6f, -8f, 0f));
            d.items.Add(It("vf_rock_arch", -19.6f, 7f, 0f));
            d.items.Add(It("vf_crag_spire", 20f, 4f));
            d.items.Add(It("vf_crag_spire", -20f, -5f));
            d.items.Add(It("vf_crag_spire", 10f, 18.5f));
            d.items.Add(It("vf_crag_spire", -10f, -18.5f));
            d.items.Add(It("vf_obsidian_block", 18.5f, 12f));
            d.items.Add(It("vf_obsidian_block", -18.5f, -12f));

            BoxRow(d, 0f, -15f, alongX: true);
            BoxRow(d, 17f, -2f, alongX: false, y: 1.10f);
            BoxRow(d, 2f, 13.4f, alongX: true, y: 1.85f, spread: 0.55f);   // the bridge
            BoxRow(d, -16f, -3f, alongX: false, y: 0.05f);

            d.items.Add(It("finish", 0f, -15f, 90f));
            d.items.Add(It("checkpoint", 17f, -2f, 4f, 1.10f, 0));
            d.items.Add(It("checkpoint", 7f, 12f, 293f, 1.90f, 1));
            d.items.Add(It("checkpoint", -17f, 1f, 186f, 0.15f, 2));
            d.items.Add(It("spawn", -4f, -15f, 90f));
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
