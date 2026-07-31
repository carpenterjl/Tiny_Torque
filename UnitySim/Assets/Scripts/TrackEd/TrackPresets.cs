using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Built-in track maps, defined in code (like <see cref="Garage.VehiclePresets"/>)
    /// so they always exist and stay in sync with the catalog: three race circuits,
    /// four TinyTorque themed circuits (downtown, toy room, enchanted kingdom,
    /// haunted hollow), and the Opus measurement range. Every map is assembled only
    /// from catalog floors/items/splines, so it loads and edits in the Track Builder
    /// like any user map. Pickers show them with a ★ prefix.
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
                          WetSand = 13, LavaRock = 14, Obsidian = 15, Grate = 16,
                          Paving = 17;

        /// <summary>
        /// What a map is FOR, which the pickers need and the geometry cannot
        /// answer. A circuit and an arena both turn up in the race and mini-game
        /// menus; a free-roam map must not, because there is nothing there to
        /// race — no finish line, no lap, no ring of two teams, just a town.
        /// It is reachable only through the mode built for it.
        /// </summary>
        public enum TrackKind { Circuit, Arena, FreeRoam }

        public static readonly (string name, TrackKind kind, Func<TrackDesign> build)[] All =
        {
            // Dedicated race circuits (closed splines, boost pads, jumps).
            ("Boost Speedway",   TrackKind.Circuit, BoostSpeedway),
            ("Dust Devil Rally", TrackKind.Circuit, DustDevilRally),
            ("Neon Vortex",      TrackKind.Circuit, NeonVortex),
            // TinyTorque themed circuits: built from the four Blender map packs
            // (build_map_props.py), each with authored item boxes so
            // ArcadeDirector's automatic placement stays out of the way. These
            // replaced both the four vehicle-archetype maps (Whoop Canyon,
            // Monza Mini, Boulder Basin, Slide Yard — their matching cars were
            // retired) and the four iteration-24 arcade circuits.
            ("Downtown Dash",    TrackKind.Circuit, DowntownDash),
            ("Playroom Raceway", TrackKind.Circuit, PlayroomRaceway),
            ("Enchanted Ascent", TrackKind.Circuit, EnchantedAscent),
            ("Graveyard Shift",  TrackKind.Circuit, GraveyardShift),
            // Arenas for the mini-game modes. No finish line and no racing
            // line: a ring of spawn points is what makes them playable, and
            // ArenaNav reads the floor slab for the rest.
            ("Scrapyard Bowl",   TrackKind.Arena, ScrapyardBowl),
            ("Cargo Yard",       TrackKind.Arena, CargoYard),
            ("Torque Dome",      TrackKind.Arena, TorqueDome),
            // The free-roam town. Kept out of every race picker by its kind,
            // not by remembering to filter it in four places.
            ("Torque Falls",     TrackKind.FreeRoam, TorqueFalls),
            // Not a circuit: a straight-line measurement range for the Opus Vector
            // mission firmware.
            ("Opus Proving Ground", TrackKind.Circuit, OpusProvingGround),
        };

        /// <summary>
        /// Every free-roam map, prefixed for a picker — exactly the set
        /// <see cref="DisplayNames"/> hides. Hidden there because there is
        /// nothing to race on them; offered here because that is the whole point
        /// of the mode that calls this.
        /// </summary>
        public static List<string> RoamNames()
        {
            var list = new List<string>();
            foreach (var p in All)
                if (p.kind == TrackKind.FreeRoam) list.Add(Prefix + p.name);
            return list;
        }

        /// <summary>
        /// Preset names for a picker. <paramref name="raceable"/> — every map a
        /// race or a mini-game can be started on — is the default because that
        /// is what all but one caller wants; the Track Builder passes false so
        /// the town can still be opened and edited like any other preset.
        /// </summary>
        public static List<string> DisplayNames(bool raceable = true)
        {
            var list = new List<string>(All.Length);
            foreach (var p in All)
                if (!raceable || p.kind != TrackKind.FreeRoam) list.Add(Prefix + p.name);
            return list;
        }

        public static TrackDesign Resolve(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            string bare = display.StartsWith(Prefix) ? display.Substring(Prefix.Length) : display;
            foreach (var p in All)
                if (p.name == bare) { var d = p.build(); d.EnsureFloor(); d.EnsureSplines(); d.EnsureItems(); return d; }
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


        // ---- arenas (mini-game modes) ----------------------------------------
        //
        // An arena is the opposite of a circuit: closed, symmetric, and defined
        // by where cars START rather than where they go. The spawn ring is the
        // load-bearing part - ArenaNav derives the centre, the radius and each
        // team's end from it, and TrackBootstrap refuses to compose a mode on a
        // map that has none.

        /// <summary>Ring of spawn items facing the middle. `order` carries the
        /// team, so a free-for-all leaves it 0 and a team mode splits by end.</summary>
        private static void SpawnRing(TrackDesign d, float radius, int count, bool teams)
        {
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                float x = Mathf.Cos(a) * radius, z = Mathf.Sin(a) * radius;
                // Face the centre: yaw 0 is +Z, so the heading is atan2(-x, -z).
                float yaw = Mathf.Atan2(-x, -z) * Mathf.Rad2Deg;
                int team = teams ? (z < 0f ? 0 : 1) : 0;
                d.items.Add(It("spawn", x, z, yaw, 0f, team));
            }
        }

        /// <summary>Perimeter wall, one block per step around a circle.</summary>
        private static void WallRing(TrackDesign d, float radius, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float a = i * Mathf.PI * 2f / count;
                float x = Mathf.Cos(a) * radius, z = Mathf.Sin(a) * radius;
                d.items.Add(It("wall_tall", x, z, Mathf.Atan2(x, z) * Mathf.Rad2Deg));
            }
        }

        /// <summary>Straight run of wall between two points.</summary>
        private static void WallLine(TrackDesign d, Vector2 a, Vector2 b, float step)
        {
            Vector2 ab = b - a;
            int n = Mathf.Max(1, Mathf.RoundToInt(ab.magnitude / step));
            float yaw = Mathf.Atan2(ab.x, ab.y) * Mathf.Rad2Deg + 90f;
            for (int i = 0; i <= n; i++)
            {
                Vector2 pt = a + ab * (i / (float)n);
                d.items.Add(It("wall_tall", pt.x, pt.y, yaw));
            }
        }

        /// <summary>
        /// DEMOLITION. A walled bowl of packed dirt with a concrete apron: open
        /// in the middle so nobody can hide, a few barriers to break a line of
        /// sight, and eight spawns on the rim.
        /// </summary>
        private static TrackDesign ScrapyardBowl()
        {
            var d = New("Scrapyard Bowl", 40, 40, Dirt);
            PaintRect(d, 8, 8, 31, 31, Asphalt);   // the fighting floor
            PaintRect(d, 16, 16, 23, 23, Checker); // centre, so the middle reads

            WallRing(d, 9.2f, 44);
            // Cover: four barriers on the diagonals, far enough in to be worth
            // driving round and far enough apart to never trap a car.
            d.items.Add(It("barrier", 3.4f, 3.4f, 45f));
            d.items.Add(It("barrier", -3.4f, 3.4f, 45f));
            d.items.Add(It("barrier", 3.4f, -3.4f, 45f));
            d.items.Add(It("barrier", -3.4f, -3.4f, 45f));
            d.items.Add(It("tire_stack", 0f, 2.6f));
            d.items.Add(It("tire_stack", 0f, -2.6f));

            SpawnRing(d, 7.6f, 8, teams: false);
            return d;
        }

        /// <summary>
        /// CAPTURE THE FLAG. A rectangular yard with a base at each end, a
        /// central crate line to break the straight run, and two side lanes so
        /// there is more than one way home.
        /// </summary>
        private static TrackDesign CargoYard()
        {
            var d = New("Cargo Yard", 44, 52, Asphalt);
            PaintRect(d, 6, 4, 37, 11, Checker);    // blue end
            PaintRect(d, 6, 40, 37, 47, Checker);   // orange end

            WallLine(d, new Vector2(-9f, -12f), new Vector2(9f, -12f), 0.9f);
            WallLine(d, new Vector2(-9f, 12f), new Vector2(9f, 12f), 0.9f);
            WallLine(d, new Vector2(-9f, -12f), new Vector2(-9f, 12f), 0.9f);
            WallLine(d, new Vector2(9f, -12f), new Vector2(9f, 12f), 0.9f);

            // Midfield cover: two runs with a gap either side of centre.
            WallLine(d, new Vector2(-6.5f, 0f), new Vector2(-2.5f, 0f), 0.9f);
            WallLine(d, new Vector2(2.5f, 0f), new Vector2(6.5f, 0f), 0.9f);
            d.items.Add(It("barrier", -4f, -5f, 90f));
            d.items.Add(It("barrier", 4f, -5f, 90f));
            d.items.Add(It("barrier", -4f, 5f, 90f));
            d.items.Add(It("barrier", 4f, 5f, 90f));

            // Four spawns per end. Team 0 defends -Z, team 1 defends +Z.
            for (int i = 0; i < 4; i++)
            {
                float x = -3f + i * 2f;
                d.items.Add(It("spawn", x, -9.5f, 0f, 0f, 0));
                d.items.Add(It("spawn", x, 9.5f, 180f, 0f, 1));
            }
            return d;
        }

        /// <summary>
        /// SOCCER. A walled pitch with a goal mouth at each end, boost pads on
        /// the wings and corner ramps to get airborne from. The floor is smooth
        /// so the ball rolls rather than skips.
        /// </summary>
        private static TrackDesign TorqueDome()
        {
            var d = New("Torque Dome", 40, 56, Obsidian);
            PaintRect(d, 14, 2, 25, 6, Neon);     // goal mouths, so they read
            PaintRect(d, 14, 49, 25, 53, Neon);
            PaintRect(d, 4, 24, 13, 31, Boost);   // wing boost pads
            PaintRect(d, 26, 24, 35, 31, Boost);

            // Perimeter, with the goal mouths left open.
            WallLine(d, new Vector2(-8f, -13.5f), new Vector2(-8f, 13.5f), 0.9f);
            WallLine(d, new Vector2(8f, -13.5f), new Vector2(8f, 13.5f), 0.9f);
            WallLine(d, new Vector2(-8f, -13.5f), new Vector2(-2.2f, -13.5f), 0.9f);
            WallLine(d, new Vector2(2.2f, -13.5f), new Vector2(8f, -13.5f), 0.9f);
            WallLine(d, new Vector2(-8f, 13.5f), new Vector2(-2.2f, 13.5f), 0.9f);
            WallLine(d, new Vector2(2.2f, 13.5f), new Vector2(8f, 13.5f), 0.9f);

            // Corner ramps: the only way to leave the floor without a jump.
            d.items.Add(It("ramp", -6.5f, -10f, 45f));
            d.items.Add(It("ramp", 6.5f, -10f, -45f));
            d.items.Add(It("ramp", -6.5f, 10f, 135f));
            d.items.Add(It("ramp", 6.5f, 10f, -135f));

            for (int i = 0; i < 3; i++)
            {
                float x = -2.4f + i * 2.4f;
                d.items.Add(It("spawn", x, -8f, 0f, 0f, 0));
                d.items.Add(It("spawn", x, 8f, 180f, 0f, 1));
            }
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

        // ---- TinyTorque themed circuits: ports of the Blender preview maps --
        //
        // Each is the matching TinyTorque_*_map.blend laid out at 1:10 — the
        // same districts in the same places, because the props were exported at
        // 0.1 and a layout at any other ratio would put the buildings at the
        // wrong spacing for their own size. MapLayout absorbs the scale, the
        // axis/handedness flip and the centring shift, so a line here reads
        // straight across from the Python it came from:
        //
        //     spawn(src["volcano"], c, (100, -305, 0), rot_z=24)   # tt_11_map
        //     L.Prop("dt_volcano", 100, -305, 24);                 // here
        //
        // Rules every port follows:
        //
        //   * ONE spline per map. Every other road in the source becomes
        //     painted floor tiles — which is what they are over there too
        //     (flat preview ribbons in a deletable ROADS collection), and a
        //     second spline would silently steal the bot racing line, because
        //     BotPath picks the spline with the MOST control points.
        //   * Gameplay items (finish/checkpoint/spawn/item_box) keep the game's
        //     own heading-of-travel convention: +X = 90, +Z = 0. Only Prop()
        //     speaks Blender rot_z.
        //   * Decorative fills of dynamic props ship pinned. The layouts place
        //     ~250 dominoes, bricks and pumpkins as scatter, and that many live
        //     Rigidbodies buys nothing; a handful near the racing line stay
        //     live so they still burst when hit.
        //   * tileSize is 2 m. The enchanted vale is 112 m across, which is 56
        //     tiles that way and 112 at 1 m — past the grid ceiling, and four
        //     times the floor geometry for ground nobody paints per-metre.
        //   * Where the source repeats a prop through several random seeds
        //     (three gravestone shapes, four crayon colours) the game has one
        //     mesh per id, so the variety comes from scale and yaw instead.
        //
        // Two things in the sources are deliberately NOT ported: sculpted
        // terrain (the castle's plateau, the mansion's rise — there is no
        // terrain system, so those landmarks stand on flat ground), and the
        // tightest linear runs. A 5-unit fence spacing is a picket every 0.5 m,
        // which is several hundred items per boundary; the long railings go out
        // to 10-12 and read the same from anywhere a car ever is.

        /// <summary>World point from authored map coordinates.</summary>
        private static Vector3 P(MapLayout L, float bx, float by, float y = 0f)
            => new Vector3(L.X(bx), y, L.Z(by));

        /// <summary>
        /// ★ Downtown Dash — the arcade preview map (<c>tt_11_map.py</c>): "a
        /// ~750 m world in four districts around one north-south avenue".
        /// Downtown on its 90 m block grid to the north, the industrial strip
        /// west, the stunt park east, and volcanic badlands south past the
        /// start gate.
        ///
        /// The lap is the avenue and the badlands loop — the one closed run the
        /// source's own road network already contains — plus a western return
        /// leg so it takes in the works rather than doubling back. ~190 m, the
        /// city half on asphalt and the badlands half on dirt.
        /// </summary>
        private static TrackDesign DowntownDash()
        {
            var d = New("Downtown Dash", 38, 47, Dirt);
            d.tileSize = 2f;
            d.ambience = MapAmbience.Downtown;
            var L = new MapLayout(d, 20260727, meshAxes: true);

            // Layout constants, verbatim from the source module.
            float[] avenueX = { -180f, -90f, 0f, 90f, 180f };
            float[] streetY = { 60f, 150f, 240f };
            const float cityN = 300f, cityS = 30f, cityW = -225f, cityE = 225f;
            const float roadW = 22f, gateY = -70f;
            var volcano = new Vector2(100f, -305f);

            var roads = new List<Vector2[]>();
            foreach (float x in avenueX)
                roads.Add(new[] { new Vector2(x, cityS), new Vector2(x, cityN) });
            foreach (float y in streetY)
                roads.Add(new[] { new Vector2(cityW, y), new Vector2(cityE, y) });
            roads.Add(new[] { new Vector2(0f, cityS), new Vector2(0f, -450f) });        // main avenue south
            roads.Add(new[] { new Vector2(0f, -40f), new Vector2(330f, -40f) });        // spur to the park
            roads.Add(new[] { new Vector2(cityW, 60f), new Vector2(-350f, 60f) });      // spur to the works
            roads.Add(new[] { new Vector2(-350f, 60f), new Vector2(-350f, -70f) });
            roads.Add(new[] { new Vector2(0f, -200f), new Vector2(60f, -214f), new Vector2(122f, -244f),
                              new Vector2(168f, -300f), new Vector2(152f, -372f), new Vector2(78f, -412f),
                              new Vector2(0f, -424f) });                                // badlands loop
            // The one road this port adds: the source's network has no way back
            // up the west side, and without it the lap is an out-and-back.
            roads.Add(new[] { new Vector2(0f, -424f), new Vector2(-110f, -400f), new Vector2(-190f, -320f),
                              new Vector2(-215f, -200f), new Vector2(-200f, -60f), new Vector2(-180f, 30f) });

            // Ground by district: cool asphalt north, warm dust south, sand out
            // in the badlands — the same split the source's one big ground
            // material paints procedurally.
            L.Rect(-390f, -480f, 390f, -90f, Sand);
            L.Rect(cityW - 60f, cityS - 40f, cityE + 60f, cityN + 40f, Asphalt);
            L.Rect(-390f, -90f, -200f, 190f, Dirt);
            L.Roads(roads, roadW, Asphalt);

            var lap = new[]
            {
                P(L, 0f, -70f),      // 0  the start gate, heading north
                P(L, 0f, 30f),       // 1  into town
                P(L, 0f, 120f),      // 2
                P(L, 0f, 240f),      // 3  the top street
                P(L, -90f, 250f),    // 4  west along it
                P(L, -180f, 240f),   // 5
                P(L, -180f, 150f),   // 6  down the western avenue
                P(L, -180f, 40f),    // 7
                P(L, -200f, -60f),   // 8  the return leg, past the works
                P(L, -215f, -200f),  // 9
                P(L, -190f, -320f),  // 10
                P(L, -110f, -400f),  // 11
                P(L, 0f, -424f),     // 12 the bottom of the avenue
                P(L, 78f, -412f),    // 13 round the badlands loop, eastbound
                P(L, 152f, -372f),   // 14
                P(L, 168f, -300f),   // 15 closest pass to the volcano
                P(L, 122f, -244f),   // 16
                P(L, 60f, -214f),    // 17
                P(L, 0f, -200f),     // 18 back onto the avenue
            };
            var widths = new[] { 3.2f, 3.2f, 3.2f, 3.0f, 2.8f, 2.8f, 3.0f, 3.0f,
                                 3.0f, 3.0f, 2.8f, 2.8f, 2.8f, 2.6f, 2.6f, 2.6f,
                                 2.6f, 2.8f, 3.2f };
            // Negated with the meshAxes mirror: the corners turned the other
            // way, and banking has to lean INTO them, not out.
            var roll = new[] { 0f, 0f, 0f, 8f, 10f, 8f, 0f, 0f, 0f, 0f,
                               -8f, -10f, -8f, 8f, 10f, 10f, 8f, 6f, 0f };
            var surfs = new[] { Boost, Asphalt, Asphalt, Rumble, Asphalt, Asphalt,
                                Asphalt, Asphalt, Asphalt, Asphalt, Dirt, Dirt,
                                Dirt, Dirt, Dirt, Dirt, Dirt, Dirt, Boost };
            d.splines.Add(Spline(lap, 3.0f, Asphalt, closed: true, walls: false, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            Downtown(L);
            Industrial(L);
            StuntPark(L);
            Badlands(L, roads, volcano);
            CityFurniture(L, avenueX, streetY, cityW, cityE, cityN, cityS);

            // The gate straddles the avenue. It is modelled spanning X with its
            // banner facing ±Y, which is already what a north-south road wants.
            L.Prop("dt_arch_gate", 0f, gateY, 0f);

            BoxRow(d, L.X(0f), L.Z(-130f), alongX: false);
            BoxRow(d, L.X(0f), L.Z(180f), alongX: false);
            BoxRow(d, L.X(-180f), L.Z(100f), alongX: false);
            BoxRow(d, L.X(130f), L.Z(-390f), alongX: true, spread: 0.7f);

            // Hand headings are GAME-frame, so the mirror maps each one
            // 0 -> 180 - 0 by hand (L.Prop rotations mirror themselves).
            d.items.Add(It("finish", L.X(0f), L.Z(gateY), 180f));
            d.items.Add(It("checkpoint", L.X(0f), L.Z(150f), 180f, 0f, 0));
            d.items.Add(It("checkpoint", L.X(-180f), L.Z(120f), 0f, 0f, 1));
            d.items.Add(It("checkpoint", L.X(-200f), L.Z(-320f), 28f, 0f, 2));
            d.items.Add(It("checkpoint", L.X(168f), L.Z(-300f), 193f, 0f, 3));
            d.items.Add(It("spawn", L.X(0f), L.Z(-110f), 180f));
            return d;
        }

        /// <summary>Buildings in the cells between the avenues, towers toward
        /// the core (tt_11_map.downtown).</summary>
        private static void Downtown(MapLayout L)
        {
            var rows = new[] { (cy: 105f, half: 30f), (cy: 195f, half: 30f), (cy: 272f, half: 19f) };
            float[] cols = { -135f, -45f, 45f, 135f };
            foreach (var (cy, half) in rows)
                foreach (float cx in cols)
                {
                    float core = Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cy - 165f) * 0.55f);
                    var slots = new List<Vector2>
                    {
                        new Vector2(-1, -1), new Vector2(1, -1),
                        new Vector2(-1, 1), new Vector2(1, 1),
                    };
                    L.Shuffle(slots);
                    int take = L.Choice(2, 3, 3, 4);
                    for (int i = 0; i < take; i++)
                    {
                        float x = cx + slots[i].x * L.Uniform(half * 0.42f, half * 0.62f);
                        float y = cy + slots[i].y * L.Uniform(half * 0.40f, half * 0.60f);
                        // Tall in the middle of town, low-rise on the edges.
                        bool tower = L.Random01() < Mathf.Max(0.10f, 0.85f - core / 190f);
                        L.Prop(tower ? "dt_bld_tower" : "dt_bld_block", x, y,
                               L.Choice(0f, 90f, 180f, 270f), L.Uniform(0.82f, 1.22f));
                    }
                }
        }

        /// <summary>Sheds in rows off the western spur, square to the access
        /// road (tt_11_map.industrial).</summary>
        private static void Industrial(MapLayout L)
        {
            float[] ys = { -50f, 5f, 60f, 115f };
            float[] xs = { -262f, -322f };
            foreach (float y in ys)
                for (int j = 0; j < xs.Length; j++)
                    L.Prop("dt_bld_hangar", xs[j], y, j == 1 ? 90f : 0f, L.Uniform(0.9f, 1.15f));
            for (int i = 0; i < 3; i++)
                L.Prop("dt_bld_block", -240f + i * 34f, 165f, 180f, L.Uniform(0.7f, 0.9f));
        }

        /// <summary>A loose course east of town: kickers, table-tops and marker
        /// cones (tt_11_map.stunt_park).</summary>
        private static void StuntPark(MapLayout L)
        {
            var course = new[]
            {
                ("dt_ramp_kicker", 172f, -96f, 90f, 1.00f),
                ("dt_ramp_jump", 172f, -32f, 90f, 1.10f),
                ("dt_ramp_kicker", 246f, -72f, 186f, 1.25f),
                ("dt_ramp_jump", 296f, 6f, 214f, 1.00f),
                ("dt_ramp_kicker", 232f, 44f, 302f, 0.90f),
                ("dt_ramp_jump", 300f, -110f, 60f, 1.15f),
                ("dt_ramp_kicker", 196f, 28f, 8f, 1.05f),
            };
            foreach (var (key, x, y, rot, sc) in course) L.Prop(key, x, y, rot, sc);

            var slalom = new[]
            {
                new Vector2(180f, 60f), new Vector2(214f, 20f), new Vector2(196f, -20f),
                new Vector2(238f, -48f), new Vector2(272f, -20f), new Vector2(258f, 24f),
                new Vector2(292f, 52f),
            };
            for (int i = 0; i < slalom.Length - 1; i++)
                foreach (var s in MapLayout.Along(new[] { slalom[i], slalom[i + 1] }, 5f))
                    L.Prop("dt_cone", s.x, s.y, L.Uniform(0f, 90f),
                           L.Uniform(0.9f, 1.15f), pinned: true);

            for (int i = 0; i < 14; i++)
                L.Prop(L.Random01() < 0.35 ? "dt_rock_large" : "dt_rock_small",
                       L.Uniform(160f, 340f), L.Uniform(-140f, 70f),
                       L.Uniform(0f, 360f), L.Uniform(0.7f, 1.5f));
        }

        /// <summary>Volcano, two stone arches and a boulder field that thickens
        /// toward the cone (tt_11_map.badlands).</summary>
        private static void Badlands(MapLayout L, List<Vector2[]> roads, Vector2 volcano)
        {
            L.Prop("dt_volcano", volcano.x, volcano.y, 24f);
            L.Prop("dt_arch_rock", -140f, -230f, 15f, 1.15f);
            L.Prop("dt_arch_rock", 0f, -395f, 0f, 1.30f);   // straddles the avenue

            int placed = 0, tries = 0;
            while (placed < 280 && tries < 9000)
            {
                tries++;
                float x = L.Uniform(-370f, 375f), y = L.Uniform(-465f, -112f);
                float vd = Vector2.Distance(new Vector2(x, y), volcano);
                if (vd < 66f) continue;                               // inside the skirt
                if (MapLayout.RoadDist(new Vector2(x, y), roads) < 17f) continue;
                // Denser near the cone, but with a floor under it — a pure
                // falloff left the whole western half as bare ground.
                if (L.Random01() > Mathf.Max(0.22f, 1.25f - vd / 300f)) continue;
                bool big = L.Random01() < 0.30;
                L.Prop(big ? "dt_rock_large" : "dt_rock_small", x, y,
                       L.Uniform(0f, 360f), L.Uniform(0.6f, big ? 1.9f : 1.4f));
                placed++;
            }
        }

        /// <summary>Signals at the junctions, lamps down the avenues, kerbing
        /// and a lane closure (tt_11_map.furniture).</summary>
        private static void CityFurniture(MapLayout L, float[] avenueX, float[] streetY,
            float cityW, float cityE, float cityN, float cityS)
        {
            const float roadW = 22f;
            foreach (float x in avenueX)
                foreach (float y in streetY)
                    foreach (var (sx, sy, rot) in new[] { (-1f, -1f, 0f), (1f, 1f, 180f) })
                        L.Prop("dt_traffic_light", x + sx * roadW * 0.62f,
                               y + sy * roadW * 0.62f, rot);

            var lampLines = new List<Vector2[]>
            {
                new[] { new Vector2(0f, -430f), new Vector2(0f, cityN) },
                new[] { new Vector2(-180f, cityS), new Vector2(-180f, cityN) },
                new[] { new Vector2(180f, cityS), new Vector2(180f, cityN) },
                new[] { new Vector2(cityW, 60f), new Vector2(cityE, 60f) },
                new[] { new Vector2(cityW, 240f), new Vector2(cityE, 240f) },
            };
            // Offset 18 rather than the source's 13.2: the racing ribbon is a
            // real 3 m surface where the preview road was a 2.2 m stripe, so a
            // lamp at the source's offset would stand in the carriageway.
            foreach (var line in lampLines)
                foreach (int side in new[] { -1, 1 })
                    foreach (var s in MapLayout.Along(line, 52f, side * 18f, 14f))
                        L.Prop("dt_street_lamp", s.x, s.y, s.head + 90f);

            // Kerbing where the avenue leaves town for the badlands.
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(
                    new[] { new Vector2(0f, -110f), new Vector2(0f, -430f) }, 12.8f, side * 17.5f))
                    L.Prop("dt_barrier", s.x, s.y, s.head);

            // A lane closure on the western spur, because a map needs one.
            foreach (var s in MapLayout.Along(
                new[] { new Vector2(-232f, 66f), new Vector2(-300f, 66f) }, 7f))
            {
                L.Prop("dt_cone", s.x, s.y, L.Uniform(0f, 90f), pinned: true);
                L.Prop("dt_barrier", s.x, s.y - 6f, s.head);
            }
        }

        /// <summary>
        /// ★ Playroom Raceway — the toybox preview map (<c>tt_16_toy_map.py</c>,
        /// "The Attic Circuit"): a stretch of attic floor with the furniture of
        /// a real room standing on it at 24× life size, so the car parks under
        /// the table with headroom and the bookcase is a tower.
        ///
        /// The circuit is the rug's own printed oval, which is what the source
        /// draws and what the render shows. The room's two wallpapered walls,
        /// skirting and dado rail come from the map's ambience — without them a
        /// floor running to the horizon reads as tarmac and the whole scale gag
        /// collapses.
        /// </summary>
        private static TrackDesign PlayroomRaceway()
        {
            var d = New("Playroom Raceway", 48, 44, Wood);
            d.tileSize = 2f;
            d.ambience = MapAmbience.ToyRoom;
            var L = new MapLayout(d, 20260728, meshAxes: true);

            const float wallN = 430f, wallW = -470f;
            var rugC = new Vector2(10f, -30f);
            const float rugA = 250f, rugB = 175f, trackW = 30f;
            var bed = new Vector2(-300f, 60f);
            var table = new Vector2(250f, 95f);
            var lamp = new Vector2(60f, -270f);
            var gate = new Vector2(10f, 145f);

            // The rug's printed circuit plus the three lanes off it.
            var oval = new Vector2[41];
            for (int i = 0; i <= 40; i++)
            {
                float a = Mathf.PI * 2f * i / 40f;
                oval[i] = new Vector2(rugC.x + Mathf.Cos(a) * rugA * 0.74f,
                                      rugC.y + Mathf.Sin(a) * rugB * 0.72f);
            }
            var lines = new List<Vector2[]>
            {
                oval,
                new[] { new Vector2(rugC.x - rugA * 0.74f, rugC.y),
                        new Vector2(wallW + 120f, rugC.y + 40f), new Vector2(wallW + 90f, 150f) },
                new[] { new Vector2(rugC.x + rugA * 0.74f, rugC.y + 20f),
                        new Vector2(table.x + 40f, 40f), new Vector2(table.x + 30f, 150f) },
                new[] { new Vector2(rugC.x - 60f, rugC.y - rugB * 0.72f), new Vector2(-40f, -330f),
                        new Vector2(140f, -350f), new Vector2(lamp.x + 40f, lamp.y + 30f) },
            };

            // Felt rug under the circuit, carpet borders, wood floor elsewhere.
            L.Ellipse(rugC.x, rugC.y, rugA, rugB, Carpet);
            L.Roads(lines, trackW, Asphalt);

            // 16 control points round the same ellipse the source prints.
            var lap = new Vector3[16];
            var widths = new float[16];
            var roll = new float[16];
            var surfs = new int[16];
            for (int i = 0; i < 16; i++)
            {
                float a = Mathf.PI * 2f * i / 16f;
                lap[i] = P(L, rugC.x + Mathf.Cos(a) * rugA * 0.74f,
                              rugC.y + Mathf.Sin(a) * rugB * 0.72f);
                // Pinch the two hoop gates, open the straights out.
                widths[i] = (i == 4 || i == 12) ? 2.4f : 3.0f;
                roll[i] = -10f * Mathf.Sin(a);
                surfs[i] = i == 0 || i == 8 ? Boost : Asphalt;
            }
            d.splines.Add(Spline(lap, 3.0f, Asphalt, closed: true, walls: false, stripes: true,
                                 roll: roll, widths: widths, surfaces: surfs));

            FurnitureWall(L, wallN, wallW);
            Bedroom(L, bed);
            Dining(L, table);
            ToyboxYard(L);
            RugCircuit(L, rugC, rugA, rugB, gate, lamp);
            FloorScatter(L, lines, rugC, rugA, rugB, wallN, wallW);

            BoxRow(d, L.X(rugC.x), L.Z(rugC.y + rugB * 0.72f), alongX: true, spread: 0.8f);
            BoxRow(d, L.X(rugC.x + rugA * 0.74f), L.Z(rugC.y), alongX: false, spread: 0.8f);
            BoxRow(d, L.X(rugC.x), L.Z(rugC.y - rugB * 0.72f), alongX: true, spread: 0.8f);
            BoxRow(d, L.X(rugC.x - rugA * 0.74f), L.Z(rugC.y), alongX: false, spread: 0.8f);

            // Start on the east end of the oval, running anticlockwise (+Z).
            d.items.Add(It("finish", L.X(rugC.x + rugA * 0.74f), L.Z(rugC.y), 180f));
            d.items.Add(It("checkpoint", L.X(rugC.x), L.Z(rugC.y + rugB * 0.72f), 270f, 0f, 0));
            d.items.Add(It("checkpoint", L.X(rugC.x - rugA * 0.74f), L.Z(rugC.y), 0f, 0f, 1));
            d.items.Add(It("checkpoint", L.X(rugC.x), L.Z(rugC.y - rugB * 0.72f), 90f, 0f, 2));
            d.items.Add(It("spawn", L.X(rugC.x + rugA * 0.74f), L.Z(rugC.y - 40f), 180f));
            return d;
        }

        /// <summary>Bookcases and dressers backed against the north wall — the
        /// skyline (tt_16_toy_map.furniture_wall). Fronts face −Y as modelled,
        /// which is already into the room, so rot_z stays 0: turning them round
        /// backs the shelves into the wallpaper and the row renders as slabs.
        /// </summary>
        private static void FurnitureWall(MapLayout L, float wallN, float wallW)
        {
            // (id, authored width, authored depth) — the source reads these off
            // the prop's own bounds; the meshes are fixed, so they are literals.
            var order = new[]
            {
                ("toy_bookcase", 20f, 8f), ("toy_dresser", 28f, 16f),
                ("toy_bookcase", 20f, 8f), ("toy_box", 11f, 16f),
                ("toy_dresser", 28f, 16f), ("toy_bookcase", 20f, 8f),
                ("toy_bookcase", 20f, 8f), ("toy_dresser", 28f, 16f),
            };
            float x = wallW + 150f;
            foreach (var (key, w, depth) in order)
            {
                float sc = L.Uniform(0.90f, 1.12f);
                L.Prop(key, x + w * sc * 0.5f, wallN - depth * sc * 0.62f, 0f, sc);
                x += w * sc + L.Uniform(14f, 34f);
            }
            // A second rank stood a little forward, to break the row.
            var second = new[] { "toy_box", "toy_block_tower", "toy_box" };
            for (int i = 0; i < second.Length; i++)
                L.Prop(second[i], wallW + 220f + i * 190f, wallN - 130f,
                       L.Uniform(0f, 360f), L.Uniform(0.9f, 1.25f));
        }

        /// <summary>The bed, and the clutter that gathers under and around a
        /// bed (tt_16_toy_map.bedroom).</summary>
        private static void Bedroom(MapLayout L, Vector2 bed)
        {
            L.Prop("toy_bed", bed.x, bed.y, 8f);
            L.Prop("toy_ball", bed.x + 34f, bed.y - 52f, 20f);
            L.Prop("toy_chair", bed.x + 46f, bed.y + 62f, -24f);
            L.Prop("toy_lamp", bed.x - 34f, bed.y + 78f, 140f);
            for (int i = 0; i < 9; i++)
                L.Prop("toy_brick", bed.x + L.Uniform(-16f, 16f), bed.y + L.Uniform(-20f, 20f),
                       L.Uniform(0f, 360f), pinned: true);
        }

        /// <summary>Table and chairs east of the rug — a forest of legs to
        /// thread (tt_16_toy_map.dining).</summary>
        private static void Dining(MapLayout L, Vector2 table)
        {
            L.Prop("toy_table", table.x, table.y, -6f);
            var seats = new[] { (-1f, 0f, 90f), (1f, 0f, -90f), (0f, -1f, 0f), (0f, 1f, 180f) };
            foreach (var (dx, dy, rot) in seats)
                L.Prop("toy_chair", table.x + dx * 20f + L.Uniform(-4f, 4f),
                       table.y + dy * 14f + L.Uniform(-4f, 4f), rot + L.Uniform(-14f, 14f));
            L.Prop("toy_table", table.x + 6f, table.y + 150f, 88f, 0.82f);
            L.Prop("toy_lamp", table.x - 40f, table.y - 60f, 210f);
        }

        /// <summary>South of the rug: cartons stacked into a yard, and
        /// everything that got tipped out of them (tt_16_toy_map.toybox_yard).
        /// </summary>
        private static void ToyboxYard(MapLayout L)
        {
            var stacks = new[]
            {
                (-190f, -300f, 0f), (-120f, -350f, 24f), (-250f, -220f, -14f),
                (60f, -390f, 8f), (150f, -300f, 40f), (230f, -220f, -20f),
                (-40f, -280f, 62f), (300f, -330f, 16f),
            };
            foreach (var (x, y, rot) in stacks)
            {
                float sc = L.Uniform(0.85f, 1.25f);
                L.Prop("toy_box", x, y, rot, sc);
                if (L.Random01() < 0.45)   // one balanced on another
                    L.Prop("toy_box", x + L.Uniform(-6f, 6f), y + L.Uniform(-6f, 6f),
                           rot + L.Uniform(20f, 70f), sc * 0.78f, bz: 8.2f * sc);
            }
            for (int i = 0; i < 5; i++)
                L.Prop("toy_block_tower", L.Uniform(-300f, 320f), L.Uniform(-400f, -190f),
                       L.Uniform(0f, 360f), L.Uniform(0.85f, 1.30f));
            for (int i = 0; i < 4; i++)
                L.Prop("toy_ball", L.Uniform(-280f, 300f), L.Uniform(-410f, -170f),
                       L.Uniform(0f, 360f), L.Uniform(0.85f, 1.35f));
        }

        /// <summary>The rug circuit: gate, hoops, ramps, crayon slalom and
        /// domino kerbing (tt_16_toy_map.circuit).</summary>
        private static void RugCircuit(MapLayout L, Vector2 rugC, float rugA, float rugB,
            Vector2 gate, Vector2 lamp)
        {
            L.Prop("toy_gate", gate.x, gate.y, 0f, 1.15f);
            L.Prop("toy_floor_lamp", lamp.x, lamp.y, 0f);

            var hoops = new[]
            {
                (rugC.x - rugA * 0.74f, rugC.y + 10f, 96f),
                (rugC.x + rugA * 0.72f, rugC.y - 30f, 84f),
                (rugC.x - 40f, rugC.y - rugB * 0.70f, 4f),
            };
            foreach (var (x, y, rot) in hoops) L.Prop("toy_hoop", x, y, rot, 1.10f);

            var ramps = new[]
            {
                ("toy_ramp_bridge", 150f, -110f, 12f, 1.00f),
                ("toy_ramp_plank", -140f, -120f, 186f, 1.00f),
                ("toy_ramp_plank", 210f, 30f, 250f, 0.90f),
                ("toy_ramp_bridge", -80f, 60f, 190f, 0.85f),
                ("toy_ramp_plank", 20f, -196f, 96f, 1.05f),
            };
            foreach (var (key, x, y, rot, sc) in ramps) L.Prop(key, x, y, rot, sc);

            // Crayon slalom threading the oval's infield.
            var slalom = new[]
            {
                new Vector2(-150f, 40f), new Vector2(-70f, 90f), new Vector2(30f, 60f),
                new Vector2(110f, 96f), new Vector2(180f, 40f), new Vector2(140f, -60f),
                new Vector2(40f, -100f), new Vector2(-60f, -70f), new Vector2(-150f, 40f),
            };
            for (int i = 0; i < slalom.Length - 1; i++)
                foreach (var s in MapLayout.Along(new[] { slalom[i], slalom[i + 1] }, 22f))
                    L.Prop("toy_crayon", s.x, s.y, L.Uniform(0f, 90f), pinned: true);

            // Dominoes kerbing the outside of the oval. Pinned: 80-odd live
            // bodies ringing the racing line is a physics bill for scenery.
            var ring = new Vector2[49];
            for (int i = 0; i <= 48; i++)
            {
                float a = Mathf.PI * 2f * i / 48f;
                ring[i] = new Vector2(rugC.x + Mathf.Cos(a) * rugA * 0.88f,
                                      rugC.y + Mathf.Sin(a) * rugB * 0.87f);
            }
            foreach (var s in MapLayout.Along(ring, 14f))
                L.Prop("toy_domino", s.x, s.y, s.head, pinned: true);

            // Desk lamps round the rug, standing in for floodlights.
            foreach (float a in new[] { 0.4f, 1.9f, 3.4f, 4.9f })
                L.Prop("toy_lamp", rugC.x + Mathf.Cos(a) * rugA * 1.10f,
                       rugC.y + Mathf.Sin(a) * rugB * 1.14f, a * Mathf.Rad2Deg + 180f);

            // A handful left LIVE on the racing line, so something still
            // scatters when a car clips it.
            for (int i = -2; i <= 2; i++)
                L.Prop("toy_domino", rugC.x + i * 16f, rugC.y + rugB * 0.72f + 6f, 0f);
            L.Prop("toy_ball", rugC.x - rugA * 0.74f - 14f, rugC.y + 30f, 0f);
            L.Prop("toy_crayon", rugC.x + 40f, rugC.y - rugB * 0.72f - 8f, 70f);
        }

        /// <summary>Bricks and dominoes over the open floor, thinning away from
        /// the toybox (tt_16_toy_map.scatter).</summary>
        private static void FloorScatter(MapLayout L, List<Vector2[]> lines, Vector2 rugC,
            float rugA, float rugB, float wallN, float wallW)
        {
            int placed = 0, tries = 0;
            while (placed < 150 && tries < 6000)
            {
                tries++;
                float x = L.Uniform(wallW + 60f, 430f), y = L.Uniform(-440f, wallN - 40f);
                if (MapLayout.RoadDist(new Vector2(x, y), lines) < 26f) continue;
                // Inside the rug the floor stays clear for racing.
                float u = (x - rugC.x) / rugA, v = (y - rugC.y) / rugB;
                if (u * u + v * v < 0.92f) continue;
                if (L.Random01() > Mathf.Max(0.20f,
                        1.15f - new Vector2(x + 60f, y + 300f).magnitude / 620f)) continue;
                L.Prop(L.Random01() < 0.62 ? "toy_brick" : "toy_domino", x, y,
                       L.Uniform(0f, 360f), L.Uniform(0.85f, 1.20f), pinned: true);
                placed++;
            }
        }

        /// <summary>
        /// ★ Enchanted Ascent — the enchanted preview map
        /// (<c>tt_17_ench_map.py</c>, "The Vale of Ardenholt"): one strong
        /// north-south axis with everything hung off it. You arrive at the race
        /// gate, come up the causeway through the village, and the castle closes
        /// the view from the end of the valley. Gardens east, enchanted wood
        /// west, tournament ground south-east, two peaks for the horizon.
        ///
        /// The props carry the light here: the key is a dim moon-blue and
        /// almost every warm note in frame is a lit window, a lantern, a crystal
        /// or a fountain. The lap runs the causeway straight at the castle, then
        /// the garden lane and a new arc back round the south of the vale.
        /// The castle's sculpted plateau is not ported — there is no terrain
        /// system, so it stands on flat ground.
        /// </summary>
        private static TrackDesign EnchantedAscent()
        {
            var d = New("Enchanted Ascent", 56, 56, Grass);
            d.tileSize = 2f;
            d.ambience = MapAmbience.Enchanted;
            // The vale runs −460..+560 in its own Y; a TrackDesign is centred.
            var L = new MapLayout(d, 20260729, shiftZ: -50f, meshAxes: true);

            var castle = new Vector2(0f, 430f);
            var village = new Vector2(0f, 120f);
            var garden = new Vector2(300f, 130f);
            var wood = new Vector2(-320f, -80f);
            const float plateauR = 150f, gatehouseY = 250f, gateY = -180f, roadW = 20f;

            var roads = new List<Vector2[]>
            {
                new[] { new Vector2(0f, -420f), new Vector2(0f, 0f), new Vector2(0f, 200f),
                        new Vector2(0f, castle.y - plateauR - 10f) },                // the causeway
                new[] { new Vector2(-260f, 60f), new Vector2(-90f, 80f), new Vector2(0f, 90f) },
                new[] { new Vector2(0f, 150f), new Vector2(140f, 140f), new Vector2(250f, 130f),
                        new Vector2(360f, 150f) },                                    // garden lane
                new[] { new Vector2(0f, -60f), new Vector2(150f, -110f), new Vector2(250f, -180f),
                        new Vector2(300f, -280f) },                                   // stunt lane
                new[] { new Vector2(0f, -300f), new Vector2(-180f, -300f), new Vector2(-300f, -220f),
                        new Vector2(-350f, -90f) },                                   // wood road
            };
            var ring = new Vector2[25];
            for (int i = 0; i < 25; i++)
            {
                float a = Mathf.PI * 2f * i / 24f;
                ring[i] = new Vector2(village.x + Mathf.Cos(a) * 78f,
                                      village.y + Mathf.Sin(a) * 58f);
            }
            roads.Add(ring);                                                          // village green

            L.Rect(-560f, -560f, 560f, 560f, Grass);
            L.Rect(-560f, -560f, 560f, -260f, Dirt);        // the dry southern end
            L.Ellipse(garden.x, garden.y, 150f, 120f, Grass);
            L.Roads(roads, roadW, Dirt);

            var lap = new[]
            {
                P(L, 0f, -390f),                // 0  the bottom of the causeway
                P(L, 0f, -180f),                // 1  the race gate
                P(L, 0f, -20f),                 // 2
                P(L, 0f, 150f, 0.15f),          // 3  climbing through the village
                P(L, 0f, 230f, 0.30f),          // 4  aimed at the castle
                P(L, 150f, 195f, 0.15f),        // 5  east onto the garden lane
                P(L, 280f, 140f),               // 6
                P(L, 352f, 10f),                // 7  round the gardens
                P(L, 350f, -140f),              // 8
                P(L, 270f, -270f),              // 9  the tournament ground
                P(L, 140f, -350f),              // 10
                P(L, 60f, -382f),               // 11
            };
            var widths = new[] { 3.0f, 3.0f, 3.0f, 2.8f, 2.6f, 2.6f,
                                 2.8f, 2.8f, 2.8f, 2.6f, 2.8f, 3.0f };
            var roll = new[] { 0f, 0f, 0f, 0f, 8f, 10f, 8f, 10f, 10f, 8f, 6f, 0f };
            var surfs = new[] { Boost, Dirt, Dirt, Dirt, Dirt, Dirt,
                                Grass, Dirt, Dirt, Rumble, Dirt, Dirt };
            d.splines.Add(Spline(lap, 2.8f, Dirt, closed: true, walls: false, stripes: false,
                                 roll: roll, widths: widths, surfaces: surfs));

            CastleHill(L, castle, plateauR);
            Village(L, village, gatehouseY);
            Gardens(L, garden);
            EnchantedWood(L, roads, wood);
            TourneyGround(L);
            ValeFurniture(L, roads, castle, plateauR, roadW);
            ValeOutfield(L, roads, castle, village, garden, plateauR);
            L.Prop("ench_peak", -330f, 520f, 24f);
            L.Prop("ench_peak", 400f, 480f, -58f, 0.78f);
            L.Prop("ench_gate", 0f, gateY, 0f);

            BoxRow(d, L.X(0f), L.Z(-80f), alongX: false);
            BoxRow(d, L.X(0f), L.Z(190f), alongX: false, y: 0.22f);
            BoxRow(d, L.X(352f), L.Z(-60f), alongX: false);
            BoxRow(d, L.X(140f), L.Z(-350f), alongX: true, spread: 0.7f);

            d.items.Add(It("finish", L.X(0f), L.Z(gateY), 180f));
            d.items.Add(It("checkpoint", L.X(0f), L.Z(230f), 180f, 0.30f, 0));
            d.items.Add(It("checkpoint", L.X(352f), L.Z(10f), 5f, 0f, 1));
            d.items.Add(It("checkpoint", L.X(140f), L.Z(-350f), 290f, 0f, 2));
            d.items.Add(It("spawn", L.X(0f), L.Z(-260f), 180f));
            return d;
        }

        /// <summary>The castle and its outworks (tt_17_ench_map.castle_hill),
        /// on flat ground: the source's plateau is sculpted terrain.</summary>
        private static void CastleHill(MapLayout L, Vector2 castle, float plateauR)
        {
            L.Prop("ench_castle", castle.x, castle.y, 0f);
            L.Prop("ench_tower", castle.x - 108f, castle.y - 40f, L.Uniform(0f, 360f), 0.85f);
            L.Prop("ench_tower", castle.x + 104f, castle.y - 40f, L.Uniform(0f, 360f), 0.78f);
            foreach (var s in MapLayout.Along(
                new[] { new Vector2(castle.x, castle.y - plateauR - 4f),
                        new Vector2(castle.x, castle.y - 40f) }, 34f))
                foreach (int side in new[] { -1, 1 })
                    L.Prop("ench_lamp", s.x + side * 17f, s.y, s.head);
        }

        /// <summary>Cottages round a green, with the gatehouse on the causeway
        /// (tt_17_ench_map.village). Doors face the green.</summary>
        private static void Village(MapLayout L, Vector2 village, float gatehouseY)
        {
            L.Prop("ench_gatehouse", 0f, gatehouseY, 0f, 1.15f);
            L.Prop("ench_fountain", village.x, village.y);
            const int n = 16;
            for (int i = 0; i < n; i++)
            {
                float a = Mathf.PI * 2f * i / n + 0.2f;
                float x = village.x + Mathf.Cos(a) * L.Uniform(96f, 132f);
                float y = village.y + Mathf.Sin(a) * L.Uniform(74f, 104f);
                if (Mathf.Abs(x) < 26f) continue;          // keep the causeway clear
                float head = Mathf.Atan2(village.y - y, village.x - x) * Mathf.Rad2Deg + 90f;
                L.Prop("ench_cottage", x, y, head, L.Uniform(0.88f, 1.18f));
            }
            for (int i = 0; i < 4; i++)
            {
                float a = Mathf.PI * 2f * i / 4f + 0.9f;
                L.Prop("ench_tower", village.x + Mathf.Cos(a) * 168f,
                       village.y + Mathf.Sin(a) * 128f, L.Uniform(0f, 360f),
                       L.Uniform(0.80f, 1.05f));
            }
            for (int i = 0; i < 3; i++)
                L.Prop("ench_fountain", village.x + L.Uniform(-190f, 190f),
                       village.y + L.Uniform(-140f, 150f), 0f, L.Uniform(0.7f, 0.95f));
        }

        /// <summary>Formal gardens east of the village: rows, not scatter — the
        /// whole point of a garden is that it was laid out
        /// (tt_17_ench_map.gardens). Hedge spacing is 10 rather than the
        /// source's 5: at 1:10 that is a picket every metre instead of every
        /// half, and 137 hedges instead of 274 for the same read.</summary>
        private static void Gardens(MapLayout L, Vector2 garden)
        {
            float gx = garden.x, gy = garden.y;
            for (int row = 0; row < 4; row++)
            {
                float y = gy - 70f + row * 46f;
                for (int i = 0; i < 9; i++)
                    L.Prop("ench_topiary", gx - 110f + i * 27f, y,
                           L.Uniform(0f, 360f), L.Uniform(0.9f, 1.2f));
            }
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(
                    new[] { new Vector2(gx - 124f, gy + side * 92f),
                            new Vector2(gx + 124f, gy + side * 92f) }, 10f))
                    L.Prop("ench_hedge", s.x, s.y, s.head);
            foreach (var s in MapLayout.Along(
                new[] { new Vector2(gx - 130f, gy - 92f), new Vector2(gx - 130f, gy + 92f) }, 10f))
                L.Prop("ench_hedge", s.x, s.y, s.head);

            for (int i = 0; i < 3; i++)
                L.Prop("ench_arch_vine", gx - 60f + i * 80f, gy + 118f, 90f, 1f + i * 0.05f);
            L.Prop("ench_fountain", gx, gy + 24f, 0f, 1.25f);
            for (int i = 0; i < 10; i++)
            {
                float a = Mathf.PI * 2f * i / 10f;
                L.Prop("ench_lamp", gx + Mathf.Cos(a) * 116f, gy + Mathf.Sin(a) * 84f,
                       a * Mathf.Rad2Deg);
            }
        }

        /// <summary>Trees thickening away from the road, crystal in the
        /// clearings. Density has a floor under it, or the west half goes bare
        /// (tt_17_ench_map.wood).</summary>
        private static void EnchantedWood(MapLayout L, List<Vector2[]> roads, Vector2 wood)
        {
            int placed = 0, tries = 0;
            while (placed < 150 && tries < 8000)
            {
                tries++;
                float x = L.Uniform(-470f, -60f), y = L.Uniform(-420f, 210f);
                if (MapLayout.RoadDist(new Vector2(x, y), roads) < 26f) continue;
                float dist = Vector2.Distance(new Vector2(x, y), wood);
                if (L.Random01() > Mathf.Max(0.24f, 1.20f - dist / 340f)) continue;
                double r = L.Random01();
                string key; float sc;
                if (r < 0.56) { key = "ench_tree"; sc = L.Uniform(0.72f, 1.25f); }
                else if (r < 0.78) { key = "ench_boulder"; sc = L.Uniform(0.7f, 1.8f); }
                else { key = "ench_crystal"; sc = L.Uniform(0.7f, 1.5f); }
                L.Prop(key, x, y, L.Uniform(0f, 360f), sc);
                placed++;
            }
        }

        /// <summary>Bridge ramps and terraces on the tournament field
        /// (tt_17_ench_map.stunt_ground).</summary>
        private static void TourneyGround(MapLayout L)
        {
            var course = new[]
            {
                ("ench_ramp_bridge", 190f, -190f, 90f, 1.00f),
                ("ench_ramp_terrace", 262f, -140f, 12f, 1.05f),
                ("ench_ramp_bridge", 330f, -220f, 198f, 1.15f),
                ("ench_ramp_terrace", 220f, -300f, 96f, 0.95f),
                ("ench_ramp_bridge", 132f, -300f, 20f, 0.90f),
            };
            foreach (var (key, x, y, rot, sc) in course) L.Prop(key, x, y, rot, sc);

            var slalom = new[]
            {
                new Vector2(150f, -140f), new Vector2(210f, -110f), new Vector2(270f, -80f),
                new Vector2(340f, -110f), new Vector2(380f, -190f), new Vector2(330f, -300f),
                new Vector2(240f, -350f), new Vector2(150f, -330f),
            };
            for (int i = 0; i < slalom.Length - 1; i++)
                foreach (var s in MapLayout.Along(new[] { slalom[i], slalom[i + 1] }, 9f))
                    L.Prop("ench_topiary", s.x, s.y, L.Uniform(0f, 360f), L.Uniform(0.85f, 1.15f));
            for (int i = 0; i < 9; i++)
                L.Prop("ench_crystal", L.Uniform(120f, 420f), L.Uniform(-380f, -80f),
                       L.Uniform(0f, 360f), L.Uniform(0.7f, 1.3f));
        }

        /// <summary>Lamps down the causeway, hedging where it leaves the
        /// village, and the two vine arches the road runs under
        /// (tt_17_ench_map.furniture).</summary>
        private static void ValeFurniture(MapLayout L, List<Vector2[]> roads, Vector2 castle,
            float plateauR, float roadW)
        {
            var lamped = new[] { (roads[0], 46f), (roads[2], 52f), (roads[3], 58f) };
            foreach (var (line, spacing) in lamped)
                foreach (int side in new[] { -1, 1 })
                    foreach (var s in MapLayout.Along(line, spacing, side * 17f, 16f))
                    {
                        if (Mathf.Abs(s.y - castle.y) < plateauR) continue;
                        L.Prop("ench_lamp", s.x, s.y, s.head);
                    }
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(
                    new[] { new Vector2(0f, -60f), new Vector2(0f, -340f) }, 8f,
                    side * (roadW * 0.5f + 8f)))
                    L.Prop("ench_hedge", s.x, s.y, s.head);
            foreach (float y in new[] { -40f, 190f })
                L.Prop("ench_arch_vine", 0f, y, 0f, 1.35f);
        }

        /// <summary>Trees and rock over the rest of the vale, so the map has no
        /// bare edge (tt_17_ench_map.outfield).</summary>
        private static void ValeOutfield(MapLayout L, List<Vector2[]> roads, Vector2 castle,
            Vector2 village, Vector2 garden, float plateauR)
        {
            int placed = 0, tries = 0;
            while (placed < 130 && tries < 8000)
            {
                tries++;
                float x = L.Uniform(-520f, 540f), y = L.Uniform(-460f, 560f);
                var p = new Vector2(x, y);
                if (MapLayout.RoadDist(p, roads) < 24f) continue;
                if (Vector2.Distance(p, castle) < plateauR + 30f) continue;
                if (Vector2.Distance(p, village) < 150f) continue;
                if (Mathf.Abs(x - garden.x) < 150f && Mathf.Abs(y - garden.y) < 130f) continue;
                if (x < -60f && y > -420f && y < 210f) continue;          // that is the wood
                if (L.Random01() < 0.62)
                    L.Prop("ench_tree", x, y, L.Uniform(0f, 360f), L.Uniform(0.65f, 1.15f));
                else
                    L.Prop("ench_boulder", x, y, L.Uniform(0f, 360f), L.Uniform(0.6f, 1.7f));
                placed++;
            }
        }

        /// <summary>
        /// ★ Graveyard Shift — the haunted preview map
        /// (<c>tt_18_haunt_map.py</c>, "Gravehollow"): the mansion on a rise at
        /// the head of a curving gravel drive, four fenced blocks of graves laid
        /// out on a grid to the east, the ruined chapel west, the barrow and its
        /// stone circle south-east, a pumpkin patch south-west, dead trees
        /// everywhere and ghosts drifting along the drive.
        ///
        /// Fog is the subject here rather than depth cueing, so the ambience
        /// runs about twice the density of the other three. The lap is the
        /// drive, a loop north of the cemetery, and the barrow road home — all
        /// on the source's own network bar two connecting legs. The mansion's
        /// rise is not ported (no terrain system), so it stands on flat ground
        /// at the head of the drive.
        /// </summary>
        private static TrackDesign GraveyardShift()
        {
            var d = New("Graveyard Shift", 54, 52, Dirt);
            d.tileSize = 2f;
            d.ambience = MapAmbience.Haunted;
            var L = new MapLayout(d, 20260730, shiftZ: -50f, meshAxes: true);

            var mansion = new Vector2(0f, 320f);
            var chapel = new Vector2(-280f, 130f);
            var barrow = new Vector2(300f, -270f);
            var patch = new Vector2(-270f, -260f);
            const float riseR = 130f, gateY = -170f, roadW = 18f;

            // (centre x, centre y, half width, half depth) of each grave block.
            var blocks = new[]
            {
                new Vector4(190f, 170f, 62f, 46f), new Vector4(320f, 150f, 58f, 44f),
                new Vector4(190f, 40f, 62f, 44f), new Vector4(320f, 20f, 58f, 46f),
            };

            var roads = new List<Vector2[]>
            {
                // The drive curves, because a straight avenue is a different genre.
                new[] { new Vector2(0f, -420f), new Vector2(0f, -240f), new Vector2(-14f, -140f),
                        new Vector2(-40f, -40f), new Vector2(-30f, 90f), new Vector2(10f, 170f),
                        new Vector2(0f, mansion.y - riseR - 8f) },
                new[] { new Vector2(-20f, 20f), new Vector2(110f, 60f), new Vector2(250f, 100f),
                        new Vector2(392f, 120f) },
                new[] { new Vector2(250f, -20f), new Vector2(250f, 210f) },      // cemetery spine
                new[] { new Vector2(-40f, 60f), new Vector2(-180f, 100f), new Vector2(-270f, 128f) },
                new[] { new Vector2(-10f, -200f), new Vector2(140f, -240f), new Vector2(260f, -230f),
                        new Vector2(330f, -350f) },                              // to the barrow
                new[] { new Vector2(-16f, -180f), new Vector2(-160f, -230f), new Vector2(-268f, -262f) },
            };

            L.Rect(-560f, -560f, 560f, 560f, Dirt);
            L.Rect(-560f, -320f, 560f, -120f, Mud);       // the low wet ground
            L.Roads(roads, roadW, Dirt);

            var lap = new[]
            {
                P(L, 0f, -170f),      // 0  the race gate on the drive
                P(L, -18f, -110f),    // 1
                P(L, -42f, -20f),     // 2  the drive's curve
                P(L, -30f, 80f),      // 3
                P(L, 10f, 160f),      // 4  under the mansion
                P(L, 110f, 225f),     // 5  north of the cemetery
                P(L, 245f, 258f),     // 6
                P(L, 365f, 225f),     // 7
                P(L, 425f, 110f),     // 8  round the east side
                P(L, 420f, -40f),     // 9
                P(L, 345f, -165f),    // 10
                P(L, 235f, -248f),    // 11 onto the barrow road
                P(L, 110f, -252f),    // 12
                P(L, 0f, -226f),      // 13 back onto the drive
            };
            var widths = new[] { 3.0f, 3.0f, 2.8f, 2.8f, 2.6f, 2.8f, 2.8f,
                                 2.8f, 2.6f, 2.6f, 2.8f, 2.8f, 3.0f, 3.0f };
            var roll = new[] { 0f, 6f, 8f, 0f, -8f, -10f, 0f,
                               8f, 10f, 8f, 8f, 6f, 0f, -6f };
            var surfs = new[] { Boost, Dirt, Dirt, Dirt, Dirt, Dirt, Dirt,
                                Dirt, Rumble, Dirt, Dirt, Mud, Dirt, Dirt };
            d.splines.Add(Spline(lap, 2.8f, Dirt, closed: true, walls: false, stripes: false,
                                 roll: roll, widths: widths, surfaces: surfs));

            MansionHill(L, mansion, riseR);
            Cemetery(L, blocks);
            ChapelRuin(L, chapel);
            BarrowField(L, barrow);
            PumpkinPatch(L, patch);
            DeadWood(L, roads, blocks, mansion, barrow, patch, riseR);
            Spirits(L, roads, gateY);
            HollowFurniture(L, roads, mansion, riseR, roadW);
            L.Prop("haunt_gate", 0f, gateY, 0f);

            BoxRow(d, L.X(-36f), L.Z(30f), alongX: false);
            BoxRow(d, L.X(245f), L.Z(258f), alongX: true, spread: 0.7f);
            BoxRow(d, L.X(422f), L.Z(35f), alongX: false);
            BoxRow(d, L.X(110f), L.Z(-252f), alongX: true, spread: 0.7f);

            d.items.Add(It("finish", L.X(0f), L.Z(gateY), 180f));
            d.items.Add(It("checkpoint", L.X(10f), L.Z(160f), 160f, 0f, 0));
            d.items.Add(It("checkpoint", L.X(425f), L.Z(110f), 20f, 0f, 1));
            d.items.Add(It("checkpoint", L.X(110f), L.Z(-252f), 275f, 0f, 2));
            d.items.Add(It("spawn", L.X(0f), L.Z(-215f), 180f));
            return d;
        }

        /// <summary>The house at the head of the drive, its hearse, its dead
        /// trees and the lamps up to it (tt_18_haunt_map.mansion_hill).</summary>
        private static void MansionHill(MapLayout L, Vector2 mansion, float riseR)
        {
            L.Prop("haunt_mansion", mansion.x, mansion.y, 0f);
            L.Prop("haunt_hearse", mansion.x - 34f, mansion.y - 46f, -24f);
            var flank = new[] { (-1f, 20f, 1.15f), (1f, 44f, 0.95f), (-1f, -60f, 0.80f), (1f, -34f, 1.05f) };
            foreach (var (ex, dy, sc) in flank)
                L.Prop("haunt_tree", mansion.x + ex * L.Uniform(52f, 70f), mansion.y + dy,
                       L.Uniform(0f, 360f), sc);
            foreach (var s in MapLayout.Along(
                new[] { new Vector2(mansion.x, mansion.y - riseR - 2f),
                        new Vector2(mansion.x, mansion.y - 40f) }, 30f))
                foreach (int side in new[] { -1, 1 })
                    L.Prop("haunt_gaslamp", s.x + side * 15f, s.y, L.Uniform(0f, 360f));
        }

        /// <summary>Four fenced blocks of graves with crypts between them
        /// (tt_18_haunt_map.cemetery). Graves go in rows on a grid with a small
        /// jitter: scattered at random they read as a rockfall, and a graveyard
        /// is legible precisely because somebody laid it out and then time
        /// knocked it about. Railings run at 12 rather than the source's 5 —
        /// 5 is a picket every half metre and 328 items for four blocks.
        /// </summary>
        private static void Cemetery(MapLayout L, Vector4[] blocks)
        {
            for (int bi = 0; bi < blocks.Length; bi++)
            {
                float cx = blocks[bi].x, cy = blocks[bi].y, hw = blocks[bi].z, hd = blocks[bi].w;
                const int nx = 7, ny = 5;
                for (int i = 0; i < nx; i++)
                    for (int j = 0; j < ny; j++)
                    {
                        if (L.Random01() < 0.10) continue;     // a gap, an empty plot
                        float x = cx + (i - (nx - 1) * 0.5f) * (2f * hw / nx);
                        float y = cy + (j - (ny - 1) * 0.5f) * (2f * hd / ny);
                        L.Prop("haunt_gravestone", x + L.Uniform(-2.4f, 2.4f),
                               y + L.Uniform(-2f, 2f), L.Uniform(-14f, 14f),
                               L.Uniform(0.85f, 1.25f));
                    }
                foreach (int side in new[] { -1, 1 })
                {
                    foreach (var s in MapLayout.Along(
                        new[] { new Vector2(cx - hw - 4f, cy + side * (hd + 6f)),
                                new Vector2(cx + hw + 4f, cy + side * (hd + 6f)) }, 12f))
                        L.Prop("haunt_fence", s.x, s.y, s.head);
                    foreach (var s in MapLayout.Along(
                        new[] { new Vector2(cx + side * (hw + 4f), cy - hd - 6f),
                                new Vector2(cx + side * (hw + 4f), cy + hd + 6f) }, 12f))
                    {
                        if (Mathf.Abs(s.y - cy) < 12f) continue;   // gateway
                        L.Prop("haunt_fence", s.x, s.y, s.head);
                    }
                }
                // Crypts go on the INWARD side of each block: the source picks a
                // side at random, and on the two eastern blocks that can put one
                // in the racing line.
                L.Prop("haunt_crypt", cx - (hw + 26f), cy + L.Uniform(-20f, 20f),
                       L.Uniform(-20f, 20f), L.Uniform(0.9f, 1.2f));
            }
            for (int i = 0; i < 8; i++)                            // lamps along the spine
                L.Prop("haunt_gaslamp", 250f + (i % 2 == 1 ? 12f : -12f), -10f + i * 30f,
                       L.Uniform(0f, 360f));
            for (int i = 0; i < 11; i++)                           // wisps between the stones
            {
                var b = blocks[i % blocks.Length];
                // The source also jitters each wisp's height; items drop onto
                // the surface below them here, so the hover lives in the prop
                // and the variety is all in the scale.
                L.Prop("haunt_wisp", b.x + L.Uniform(-b.z, b.z), b.y + L.Uniform(-b.w, b.w),
                       L.Uniform(0f, 360f), L.Uniform(0.8f, 1.3f));
            }
        }

        /// <summary>The chapel, its fallen arches, and the trees that have
        /// grown into it (tt_18_haunt_map.chapel_ruin).</summary>
        private static void ChapelRuin(MapLayout L, Vector2 chapel)
        {
            L.Prop("haunt_chapel", chapel.x, chapel.y, -14f);
            var arches = new[] { (78f, -34f, 8f, 1.00f), (96f, 44f, -22f, 0.85f), (-64f, 62f, 40f, 1.10f) };
            foreach (var (dx, dy, rot, sc) in arches)
                L.Prop("haunt_arch_ruin", chapel.x + dx, chapel.y + dy, rot, sc);
            L.Prop("haunt_crypt", chapel.x - 52f, chapel.y - 56f, 18f);
            for (int i = 0; i < 14; i++)
            {
                float a = L.Uniform(0f, Mathf.PI * 2f), dist = L.Uniform(60f, 190f);
                L.Prop("haunt_gravestone", chapel.x + Mathf.Cos(a) * dist,
                       chapel.y + Mathf.Sin(a) * dist * 0.8f,
                       L.Uniform(0f, 360f), L.Uniform(0.8f, 1.2f));
            }
            for (int i = 0; i < 5; i++)
                L.Prop("haunt_wisp", chapel.x + L.Uniform(-90f, 90f), chapel.y + L.Uniform(-70f, 70f),
                       L.Uniform(0f, 360f), L.Uniform(0.85f, 1.2f));
        }

        /// <summary>The barrow, and the stunt ground laid out over it
        /// (tt_18_haunt_map.barrow_field).</summary>
        private static void BarrowField(MapLayout L, Vector2 barrow)
        {
            L.Prop("haunt_barrow", barrow.x, barrow.y, 32f);
            var course = new[]
            {
                ("haunt_ramp_slab", 150f, -170f, 100f, 1.00f),
                ("haunt_ramp_tomb", 96f, -290f, 14f, 1.05f),
                ("haunt_ramp_slab", 200f, -390f, 210f, 1.10f),
                ("haunt_ramp_tomb", 400f, -180f, 96f, 0.95f),
                ("haunt_ramp_slab", 420f, -390f, 320f, 1.00f),
            };
            foreach (var (key, x, y, rot, sc) in course) L.Prop(key, x, y, rot, sc);
            for (int i = 0; i < 11; i++)
                L.Prop("haunt_pumpkin", L.Uniform(90f, 460f), L.Uniform(-420f, -120f),
                       L.Uniform(0f, 360f), L.Uniform(0.8f, 1.4f), pinned: true);
            L.Prop("haunt_arch_ruin", barrow.x - 120f, barrow.y + 90f, 64f);
        }

        /// <summary>Rows of pumpkins with a scarecrow's worth of fence around
        /// them (tt_18_haunt_map.pumpkin_patch).</summary>
        private static void PumpkinPatch(MapLayout L, Vector2 patch)
        {
            float px = patch.x, py = patch.y;
            for (int row = 0; row < 5; row++)
            {
                float y = py - 54f + row * 27f;
                for (int i = 0; i < 11; i++)
                {
                    if (L.Random01() < 0.16) continue;
                    L.Prop("haunt_pumpkin", px - 92f + i * 18.5f + L.Uniform(-3f, 3f),
                           y + L.Uniform(-3f, 3f), L.Uniform(0f, 360f),
                           L.Uniform(0.8f, 1.35f), pinned: true);
                }
            }
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(
                    new[] { new Vector2(px - 104f, py + side * 70f),
                            new Vector2(px + 104f, py + side * 70f) }, 10f))
                    L.Prop("haunt_fence", s.x, s.y, s.head);
            for (int i = 0; i < 4; i++)
                L.Prop("haunt_tree", px + L.Uniform(-120f, 120f), py + L.Uniform(80f, 130f),
                       L.Uniform(0f, 360f), L.Uniform(0.8f, 1.2f));
        }

        /// <summary>Dead trees over the whole hollow, thinning where districts
        /// already are (tt_18_haunt_map.dead_wood).</summary>
        private static void DeadWood(MapLayout L, List<Vector2[]> roads, Vector4[] blocks,
            Vector2 mansion, Vector2 barrow, Vector2 patch, float riseR)
        {
            int placed = 0, tries = 0;
            while (placed < 160 && tries < 12000)
            {
                tries++;
                float x = L.Uniform(-520f, 540f), y = L.Uniform(-460f, 560f);
                var p = new Vector2(x, y);
                if (MapLayout.RoadDist(p, roads) < 22f) continue;
                if (Vector2.Distance(p, mansion) < riseR + 20f) continue;
                if (Vector2.Distance(p, barrow) < 70f) continue;
                bool inBlock = false;
                foreach (var b in blocks)
                    if (Mathf.Abs(x - b.x) < b.z + 16f && Mathf.Abs(y - b.y) < b.w + 16f)
                        inBlock = true;
                if (inBlock) continue;
                if (Mathf.Abs(x - patch.x) < 120f && Mathf.Abs(y - patch.y) < 80f) continue;
                if (L.Random01() < 0.72)
                    L.Prop("haunt_tree", x, y, L.Uniform(0f, 360f), L.Uniform(0.55f, 1.20f));
                else
                    L.Prop("haunt_gravestone", x, y, L.Uniform(0f, 360f), L.Uniform(0.8f, 1.15f));
                placed++;
            }
        }

        /// <summary>The hitchhiking trio at the gate, and wisps drifting along
        /// the drive (tt_18_haunt_map.spirits). Both are drive-through: their
        /// hulls are triggers, so the car passes clean through a ghost.</summary>
        private static void Spirits(MapLayout L, List<Vector2[]> roads, float gateY)
        {
            L.Prop("haunt_ghost", 26f, gateY + 26f, -24f);
            L.Prop("haunt_ghost", -40f, 150f, 142f, 0.9f);
            foreach (var line in new[] { roads[0], roads[4] })
                foreach (var s in MapLayout.Along(line, 90f, L.Uniform(14f, 24f), 40f))
                    L.Prop("haunt_wisp", s.x, s.y, s.head + 90f, L.Uniform(0.8f, 1.25f));
        }

        /// <summary>Gas lamps down the drive and railings where it crosses the
        /// hollow (tt_18_haunt_map.furniture).</summary>
        private static void HollowFurniture(MapLayout L, List<Vector2[]> roads, Vector2 mansion,
            float riseR, float roadW)
        {
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(roads[0], 56f, side * 17f, 22f))
                {
                    if (Mathf.Abs(s.y - mansion.y) < riseR) continue;
                    L.Prop("haunt_gaslamp", s.x, s.y, L.Uniform(0f, 360f));
                }
            foreach (int side in new[] { -1, 1 })
                foreach (var s in MapLayout.Along(
                    new[] { new Vector2(-8f, -60f), new Vector2(-22f, -300f) }, 10f,
                    side * (roadW * 0.5f + 9f)))
                    L.Prop("haunt_fence", s.x, s.y, s.head);
        }

        // ---- Torque Falls: the free-roam town ------------------------------
        //
        // A port of tt_25_city_map.py, the only source map with no circuit in
        // it. Same districts, same street grid, same numbers — but on the MESH
        // axes (see MapLayout's meshAxes remarks), because a hundred and
        // sixteen houses all facing their own street is the entire read of the
        // place and the default layout convention turns every one of them
        // round.
        //
        // Two things scale differently from the four circuit ports:
        //
        //   * tileSize is 1 m, not 2. A road here is 20 authored units = 2 game
        //     metres, which at the circuit ports' 2 m tiles is a single tile
        //     wide and loses its kerbs entirely. At 1 m the carriageway is two
        //     tiles with a paved verge each side, which is what makes a grid
        //     read as streets rather than as a runway diagram.
        //   * There is NO spline. A town has no racing line, and BotPath picks
        //     the spline with the most points — inventing one would hand a bot
        //     a lap of a map that has no laps.
        //
        // Spawns: eight, scattered over the districts, order 0. That is not
        // decoration — it is the whole of what makes free roam playable, since
        // TrackRespawn falls back to the nearest free spawn when there is no
        // racing line to put the car back on.

        /// <summary>Blender heading (0 = +X) to the game's own item yaw
        /// (0 = +Z). On the mesh axes a Blender +Y goes to game −Z, so a
        /// heading rotates by a quarter turn rather than mirroring.</summary>
        private static float DriveYaw(float headingDeg) => 90f + headingDeg;

        /// <summary>
        /// What grows on a verge or in a park. The source draws from FIVE tree
        /// meshes — three oaks built under different seeds, a maple and a pine
        /// — and the port has one mesh per id, so a literal copy of its bag
        /// would be four-fifths oak. Spreading it over all four kinds keeps
        /// more of the variety the source was after, and costs a third of the
        /// triangles: an oak is 6 348 of them and there are some three hundred
        /// trees in this town, which was 80 % of the whole map's geometry.
        /// </summary>
        private static readonly string[] TreeBag =
        {
            "city_tree_oak", "city_tree_maple", "city_tree_pine",
            "city_tree_young", "city_tree_oak",
        };

        /// <summary>
        /// Copies of one prop from a to b at an EXACT pitch, never fitted to
        /// the span (tt_25_city_map.run). The fence, wall, hedge and pole
        /// sections in this kit only line up at their own pitch; stretching a
        /// run so it comes out even at both ends is exactly what leaves
        /// daylight between fence panels and hangs wires that do not meet.
        /// </summary>
        private static void CityRun(MapLayout L, string id, Vector2 a, Vector2 b,
            float pitch, float? rot = null)
        {
            Vector2 ab = b - a;
            float len = ab.magnitude;
            if (len < 1e-6f) return;
            Vector2 u = ab / len;
            float head = rot ?? Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg;
            int n = Mathf.FloorToInt(len / pitch + 1e-6f);
            for (int i = 0; i < n; i++)
            {
                Vector2 p = a + u * ((i + 0.5f) * pitch);
                L.Prop(id, p.x, p.y, head);
            }
        }

        /// <summary>
        /// A row of buildings down one side of a street, every one facing it.
        /// The kit's buildings face −Y, so which way a row turns is decided by
        /// the STREET and not by the building: north of an east-west street is
        /// rot 0 and south is 180, west of a north-south street is 90 and east
        /// is 270. Get one row's rotation wrong and a whole terrace backs its
        /// front doors into the gardens behind it.
        /// </summary>
        private static void CityFrontage(MapLayout L, string[] srcs, bool ew, float at,
            int side, float t0, float t1, float pitch, float setback,
            float scaleLo = 1f, float scaleHi = 1f, float jitter = 0f)
        {
            float rot = ew ? (side > 0 ? 0f : 180f) : (side > 0 ? 270f : 90f);
            int i = 0;
            for (float t = t0; t <= t1 + 1e-4f; t += pitch, i++)
            {
                string id = srcs.Length < 3 ? srcs[i % srcs.Length] : L.Choice(srcs);
                float off = at + side * (setback + (jitter > 0f ? L.Uniform(-jitter, jitter) : 0f));
                float bx = ew ? t : off;
                float by = ew ? off : t;
                L.Prop(id, bx, by, rot, L.Uniform(scaleLo, scaleHi));
            }
        }

        /// <summary>
        /// ★ Torque Falls — the city preview map (<c>tt_25_city_map.py</c>): a
        /// ~530-unit town on a five-by-four street grid. Clock-tower plaza and
        /// a thirteen-unit terrace in the middle, residential rows on most
        /// block faces, three parks, an industrial corner round the water
        /// tower, a motor strip carrying the garage / dealership / filling
        /// station, and the arena on its own approach road to the south.
        ///
        /// Free-roam only: no finish line, no checkpoints, no spline.
        /// </summary>
        private static TrackDesign TorqueFalls()
        {
            // 66 x 66 at 1 m: the streets run out to ±268 authored and carry
            // verge trees the whole way, so the outermost ITEM is at ±267 —
            // ±33 m of map leaves about six metres of open ground past the
            // last one on every side.
            var d = New("Torque Falls", 66, 66, Grass);
            d.tileSize = 1f;
            d.ambience = MapAmbience.CityNoon;
            // The source runs y from −346 (behind the arena) to +268 (the top
            // of the grid); the midpoint is −39, and a TrackDesign is always
            // centred on its origin. Measuring the shift off the buildings
            // rather than off the roads put fifteen verge trees over the edge.
            var L = new MapLayout(d, 20260731, shiftZ: 39f, meshAxes: true);

            // Layout constants, verbatim from the source module.
            const float roadW = 20f, half = 10f, walk = half + 2.6f, ext = 268f;
            float[] nsX = { -210f, -105f, 0f, 105f, 210f };
            float[] ewY = { -175f, -60f, 60f, 175f };
            const float arenaY = -302f, arenaMouth = arenaY + 43.6f;
            var clock = new Vector2(-56f, -18f);
            var water = new Vector2(-186f, -96f);
            var garage = new Vector2(48f, -92f);
            var shop = new Vector2(128f, -92f);
            var fuel = new Vector2(206f, -96f);

            // The grid, plus the arena approach. The first nsX+ewY of these are
            // the STREETS: utilities and verges key off those only, so the
            // approach road does not get a second set of poles down a lane that
            // already has them.
            var streets = new List<Vector2[]>();
            foreach (float x in nsX)
                streets.Add(new[] { new Vector2(x, -ext), new Vector2(x, ext) });
            foreach (float y in ewY)
                streets.Add(new[] { new Vector2(-ext, y), new Vector2(ext, y) });
            var roads = new List<Vector2[]>(streets)
            {
                new[] { new Vector2(0f, -ext), new Vector2(0f, arenaMouth - 2f) },
            };

            // Ground, then the footway band, then the carriageway on top of it.
            // Painting the wide one first is what leaves a kerb showing.
            L.Rect(-100f, arenaMouth, 100f, arenaMouth + 96f, Paving);   // arena car park
            L.Rect(-46f, arenaY - 46f, 46f, arenaY + 46f, Paving);       // the arena apron
            L.Roads(roads, (walk + 2.2f) * 2f, Paving);
            L.Roads(roads, roadW, Asphalt);

            CityDowntown(L, clock, walk);
            CityResidential(L);
            CityParks(L);
            CityIndustry(L, water);
            CityMotorStrip(L, garage, shop, fuel, walk);
            CityArenaGrounds(L, arenaMouth);
            CityUtilities(L, streets, ewY, walk, half);
            CityVerges(L, streets, roads, walk, half);

            // Eight places to start, and to be put back at. Yaw is the game's
            // own convention here, not Blender's — only Prop() speaks rot_z.
            var starts = new (float bx, float by, float head)[]
            {
                (6.5f, -30f, 90f),                 // the avenue, outside the plaza
                (-6.5f, 24f, 270f),                // the avenue, northbound
                (garage.x + 2.6f, -76.5f, 90f),    // the motor strip, on the bay line
                (shop.x + 5.5f, -82f, 90f),        // the dealership forecourt
                (-105f, 120f, 0f),                 // residential north
                (-186f, -60f, 180f),               // the industrial corner
                (150f, 118f, 180f),                // the park
                (6.5f, arenaMouth + 46f, 270f),    // the arena approach
                (-210f, 6.5f, 90f),                // the western avenue
                (210f, -6.5f, 270f),               // the eastern avenue
                (-60f, 168f, 0f),                  // the northern cross street
                (60f, -168f, 180f),                // the southern cross street
            };
            foreach (var (bx, by, head) in starts)
                d.items.Add(It("spawn", L.X(bx), L.Z(by), DriveYaw(head)));
            return d;
        }

        /// <summary>The centre: the terrace, the shops, the walk-ups and the
        /// clock-tower plaza (tt_25_city_map.downtown).</summary>
        private static void CityDowntown(MapLayout L, Vector2 clock, float walk)
        {
            // The townhouses abut at exactly their own width, which is the one
            // pitch in this kit that has to be exact rather than merely tidy.
            CityRun(L, "city_townhouse", new Vector2(-96f, 39f), new Vector2(-24f, 39f), 5.4f, 180f);
            CityFrontage(L, new[] { "city_apartment" }, true, 60f, -1, 26f, 88f, 34f, 28f);
            CityFrontage(L, new[] { "city_store", "city_diner" }, true, -60f, 1, 26f, 96f, 38f, 27f);
            CityFrontage(L, new[] { "city_store" }, true, -60f, 1, -84f, -84f, 40f, 27f);

            L.Prop("city_clocktower", clock.x, clock.y);
            foreach (float dx in new[] { -16f, 16f })
            {
                L.Prop("city_planter", clock.x + dx, clock.y - 12f);
                L.Prop("city_bench", clock.x + dx * 0.55f, clock.y - 15f, 180f);
            }
            CityRun(L, "city_tree_young", new Vector2(clock.x - 26f, clock.y - 20f),
                    new Vector2(clock.x + 26f, clock.y - 20f), 13f, 0f);

            L.Prop("city_busstop", walk + 0.4f, -34f, 90f);
            L.Prop("city_busstop", walk + 0.4f, 34f, 270f);
            L.Prop("city_billboard", -walk - 9f, -120f, 8f);
            L.Prop("city_billboard", walk + 11f, 118f, 186f);
        }

        /// <summary>
        /// Every street face that gets a row of houses, as (east-west, street,
        /// side, from, to). A table rather than a handful of calls because the
        /// source's first build only had rows on its two northern streets, and
        /// a grid with a tenth of its block faces built on does not read as a
        /// small town — it reads as one new housing development next to a lot
        /// of mown grass. The gaps are where a district that is not housing
        /// already stands.
        /// </summary>
        private static readonly (bool ew, float at, int side, float t0, float t1)[] HouseRows =
        {
            (true, 175f, 1, -196f, 196f),
            (true, 175f, -1, -196f, 196f),
            (true, 60f, 1, -196f, -128f),
            (true, 60f, 1, 128f, 196f),
            (true, -175f, 1, -196f, -54f),
            (true, -175f, 1, 54f, 196f),
            (true, -175f, -1, -196f, -54f),
            (true, -175f, -1, 54f, 196f),
            (false, -105f, 1, 86f, 150f),
            (false, -105f, -1, 86f, 150f),
            (false, 105f, 1, 86f, 150f),
            (false, -210f, 1, -46f, 46f),
            (false, -105f, -1, -46f, 46f),
            (false, 105f, 1, -46f, 46f),
            (false, 210f, -1, -46f, 46f),
            (false, -210f, 1, -155f, -86f),
            (false, 210f, -1, -155f, -86f),
            (false, -105f, -1, -155f, -122f),
            (false, 105f, 1, -155f, -122f),
        };

        /// <summary>Housing on most block faces, and the boundaries between the
        /// plots (tt_25_city_map.residential).</summary>
        private static void CityResidential(MapLayout L)
        {
            // The source shuffles a bag of twelve house sources — three shapes
            // rebuilt under four seeds each, so a street is not one repeated
            // house. The game has ONE mesh per id, so the variety has to come
            // from scale and placement instead; the bag keeps the shapes
            // interleaved the same way.
            var bag = new List<string>
            {
                "city_house_a", "city_house_b", "city_cottage",
                "city_house_a", "city_house_b", "city_cottage",
                "city_house_a", "city_house_b", "city_cottage",
                "city_house_a", "city_house_b", "city_cottage",
            };
            L.Shuffle(bag);
            var srcs = bag.ToArray();
            foreach (var (ew, at, side, t0, t1) in HouseRows)
                CityFrontage(L, srcs, ew, at, side, t0, t1, 24f, 32f, 0.94f, 1.06f, 1.6f);

            // Placed at exactly FENCE_SPAN (4.0): the sections are modelled
            // with their posts inset half a post width, so that pitch reads as
            // the double post real panel fencing has rather than z-fighting one
            // post through another.
            foreach (float y in new[] { 151f, 84f })
                foreach (var (x0, x1) in new[] { (-196f, -112f), (-84f, 84f), (112f, 196f) })
                    CityRun(L, "city_fence_picket", new Vector2(x0, y), new Vector2(x1, y), 4f, 0f);
            foreach (var (x0, x1) in new[] { (-196f, -112f), (112f, 196f) })
                CityRun(L, "city_hedge", new Vector2(x0, 201f), new Vector2(x1, 201f), 4f, 0f);
        }

        /// <summary>Three green blocks: trees, a hedge along the front and
        /// somewhere to sit (tt_25_city_map.park).</summary>
        private static void CityParks(MapLayout L)
        {
            var parks = new[]
            {
                (x0: 122f, x1: 196f, y0: 78f, y1: 158f),
                (x0: -96f, x1: -26f, y0: -164f, y1: -114f),
                (x0: 24f, x1: 96f, y0: -164f, y1: -134f),
            };
            foreach (var (x0, x1, y0, y1) in parks)
            {
                int n = Mathf.Max(10, (int)((x1 - x0) * (y1 - y0) / 230f));
                for (int i = 0; i < n; i++)
                    L.Prop(L.Choice(TreeBag),
                           L.Uniform(x0, x1), L.Uniform(y0, y1),
                           L.Uniform(0f, 360f), L.Uniform(0.85f, 1.20f));
                for (int i = 0; i < n / 2; i++)
                    L.Prop("city_bush", L.Uniform(x0, x1), L.Uniform(y0, y1),
                           L.Uniform(0f, 360f), L.Uniform(0.8f, 1.4f));
                int seats = Mathf.Max(2, (int)((x1 - x0) / 20f));
                for (int i = 0; i < seats; i++)
                {
                    L.Prop("city_bench", x0 + 8f + i * 18f, y0 - 2f, 0f);
                    L.Prop("city_planter", x0 + 14f + i * 18f, y0 - 2f);
                }
                CityRun(L, "city_hedge", new Vector2(x0 - 2f, y0 - 6f),
                        new Vector2(x1, y0 - 6f), 4f, 0f);
            }
        }

        /// <summary>South-west: the warehouse, the water tower, the fire
        /// station and chain link round it (tt_25_city_map.industry).</summary>
        private static void CityIndustry(MapLayout L, Vector2 water)
        {
            L.Prop("city_warehouse", -158f, -96f);
            L.Prop("city_watertower", water.x, water.y);
            L.Prop("city_firehouse", -62f, -92f);
            CityFrontage(L, new[] { "city_warehouse" }, true, -175f, 1, -196f, -196f, 40f, 30f);

            CityRun(L, "city_fence_chain", new Vector2(-208f, -132f), new Vector2(-120f, -132f), 4f, 0f);
            foreach (float x in new[] { -208f, -120f })
                CityRun(L, "city_fence_chain", new Vector2(x, -132f), new Vector2(x, -76f), 4f, 90f);
            for (int i = 0; i < 5; i++)
                L.Prop("city_planter", -140f + i * 6f, -74f);
        }

        /// <summary>South-east: the three props you drive into, square to one
        /// street (tt_25_city_map.motor_strip).</summary>
        private static void CityMotorStrip(MapLayout L, Vector2 garage, Vector2 shop,
            Vector2 fuel, float walk)
        {
            L.Prop("city_garage", garage.x, garage.y, 180f);
            L.Prop("city_autoshop", shop.x, shop.y, 180f);
            L.Prop("city_gas", fuel.x, fuel.y, 180f);
            L.Prop("city_warehouse", 86f, -158f, 0f);   // so the strip reads as a trade
            CityRun(L, "city_wall", new Vector2(24f, -125f), new Vector2(96f, -125f), 4f, 0f);
            for (int i = 0; i < 6; i++)
                L.Prop("city_tree_young", 30f + i * 20f, -walk - 62f);
        }

        /// <summary>The arena, its approach and its car park
        /// (tt_25_city_map.arena_grounds).</summary>
        private static void CityArenaGrounds(MapLayout L, float mouth)
        {
            // 87 x 57 with its tunnel on the local −X face, so it is turned a
            // quarter turn to point that tunnel back up the avenue at the town.
            L.Prop("city_arena", 0f, -302f, 270f);
            foreach (int sx in new[] { -1, 1 })
            {
                L.Prop("city_billboard", sx * 26f, mouth + 34f, 180f + sx * 26f);
                CityRun(L, "city_lamp", new Vector2(sx * 52f, mouth + 10f),
                        new Vector2(sx * 52f, mouth + 90f), 34f, 90f);
                CityRun(L, "city_fence_chain", new Vector2(sx * 74f, mouth),
                        new Vector2(sx * 74f, mouth + 92f), 4f, 90f);
                CityRun(L, "city_tree_pine", new Vector2(sx * 96f, mouth + 20f),
                        new Vector2(sx * 96f, mouth + 96f), 16f, 0f);
            }
        }

        /// <summary>
        /// Poles, lamps, signals and the small stuff, all keyed off the footway
        /// (tt_25_city_map.utilities). The poles are the reason POLE_PITCH
        /// exists: at exactly 26 units each one's half span meets its
        /// neighbour's with matching height AND matching slope, so a street of
        /// them carries one continuous run of wire with no wire prop between.
        /// </summary>
        private static void CityUtilities(MapLayout L, List<Vector2[]> streets,
            float[] ewY, float walk, float half)
        {
            // The garage's forecourt: the one place a street tree or a lamp
            // column landing in front of a drive-through would spoil the only
            // prop in the kit you are supposed to be able to drive into.
            bool KeepOut(float x, float y) =>
                new Vector2(x - 48f, y + 78f).magnitude < 27f;

            float poleAt = walk + 3.4f;
            for (int i = 0; i < streets.Count; i++)
            {
                int side = i % 2 == 0 ? 1 : -1;
                foreach (var s in MapLayout.Along(streets[i], 26f, side * poleAt, 13f))
                    L.Prop(L.Random01() < 0.22 ? "city_pole_t" : "city_pole", s.x, s.y, s.head);
                foreach (var s in MapLayout.Along(streets[i], 44f, -side * (walk - 1.2f), 22f))
                {
                    if (KeepOut(s.x, s.y)) continue;
                    L.Prop("city_lamp", s.x, s.y, s.head + (side > 0 ? 90f : -90f));
                }
            }

            // Signals on the four junctions the avenue makes. The source builds
            // a second red-lit variant for the cross street; here one prop
            // cycles through all three lamps, so both corners take it.
            foreach (float y in ewY)
                foreach (var (sx, sy) in new[] { (-1f, -1f), (1f, 1f) })
                    L.Prop("city_signal", sx * (half + 3f), y + sy * (half + 3f),
                           sy < 0f ? 0f : 180f);

            for (int i = 0; i < 26; i++)      // hydrants, signs, mailboxes
            {
                var line = streets[L.RandRange(0, streets.Count)];
                var pts = MapLayout.Along(line, 60f,
                    (L.Random01() < 0.5 ? -1f : 1f) * (walk + 1f), L.Uniform(0f, 60f));
                if (pts.Count == 0) continue;
                var p = pts[L.RandRange(0, pts.Count)];
                L.Prop(L.Choice("city_hydrant", "city_sign", "city_mailbox", "city_hydrant"),
                       p.x, p.y, p.head + (L.Random01() < 0.5 ? 90f : -90f));
            }
        }

        /// <summary>Street trees down the verge behind the footway, off the
        /// plots (tt_25_city_map.verges).</summary>
        private static void CityVerges(MapLayout L, List<Vector2[]> streets,
            List<Vector2[]> roads, float walk, float half)
        {
            foreach (var line in streets)
                foreach (int side in new[] { -1, 1 })
                    foreach (var s in MapLayout.Along(line, 26f, side * (walk + 4.2f), 13f))
                    {
                        // Keep the central junction and the garage forecourt
                        // open, and stay off anything that is road.
                        if (Mathf.Abs(s.x) < 46f && Mathf.Abs(s.y) < 46f) continue;
                        if (new Vector2(s.x - 48f, s.y + 78f).magnitude < 27f) continue;
                        if (MapLayout.RoadDist(new Vector2(s.x, s.y), roads) < half + 6f) continue;
                        L.Prop(L.Choice(TreeBag), s.x, s.y,
                               L.Uniform(0f, 360f), L.Uniform(0.82f, 1.10f));
                    }
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
