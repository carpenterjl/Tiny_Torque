using System.Collections.Generic;
using System.IO;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 6 of the pack pipeline: the two starter maps.
    ///
    /// They are <see cref="TrackDesign"/> JSON, not <c>TrackPresets</c> entries.
    /// That is the whole point: a preset row would put them in every in-game race
    /// and map picker, which "don't add it to the game yet" rules out. As JSON
    /// they open in the Track Builder's Load list, edit like any user map and run
    /// from its Drive button — and the game's own map list is untouched.
    ///
    /// Both are assembled ONLY from ids already in <see cref="TrackCatalog"/> and
    /// floor indices already in <c>TrackCatalog.Floors</c>. Nothing here can place
    /// an arena tile, because arena tiles are pack-only and the builder can only
    /// place what the catalog knows.
    ///
    /// Floor indices are positional and append-only in the catalog:
    /// 0 dirt · 1 asphalt · 2 grass · 3 sand · 7 boost · 8 checker · 17 paving.
    /// </summary>
    public static class PackMapGenerator
    {
        private const int Dirt = 0, Asphalt = 1, Grass = 2, Boost = 7, Checker = 8, Paving = 17;

        [MenuItem("Tools/TinyTorque Assets/6. Generate maps", priority = 105)]
        public static void Generate()
        {
            PackPaths.EnsureFolder(PackPaths.MapsRoot);
            Write(BuildFreeRoam());
            Write(BuildBaseRace());
            AssetDatabase.Refresh();
        }

        [MenuItem("Tools/TinyTorque Assets/7. Install maps to Track Builder", priority = 106)]
        public static void InstallMaps()
        {
            // TrackLibrary.Dir is the user's save area, not a repo path — hence a
            // separate, explicit step rather than a side effect of generation.
            Directory.CreateDirectory(TrackLibrary.Dir);
            int n = 0;
            foreach (string src in Directory.GetFiles(
                         PackPaths.ToAbsolute(PackPaths.MapsRoot), "*.json"))
            {
                string dst = Path.Combine(TrackLibrary.Dir, Path.GetFileName(src));
                File.Copy(src, dst, overwrite: true);
                n++;
            }
            PackPaths.Log($"MAPS installed {n} to {TrackLibrary.Dir}");
        }

        private static void Write(TrackDesign d)
        {
            d.NormalizeCheckpointOrders();
            string path = PackPaths.MapsRoot + "/" + d.name + ".json";
            File.WriteAllText(PackPaths.ToAbsolute(path), JsonUtility.ToJson(d, true));
            PackPaths.Log($"MAP {d.name}: {d.width}x{d.length} tiles, {d.items.Count} items, " +
                          $"{d.splines.Count} splines");
        }

        // ===================================================================
        // free roam
        // ===================================================================

        /// <summary>
        /// An open sandbox: a road grid with a block of town on it, the five
        /// drive-in props, a couple of ramps and a park corner left deliberately
        /// bare so there is somewhere to try the scatter brush.
        ///
        /// No checkpoints and no finish line — this is somewhere to drive around,
        /// and the Track Builder is happy to build a map that has neither.
        /// </summary>
        private static TrackDesign BuildFreeRoam()
        {
            var d = TrackDesign.Default(60, 60);
            d.name = "TinyTorque_FreeRoam";
            d.tileSize = 1f;
            d.ambience = MapAmbience.CityNoon;

            for (int i = 0; i < d.floor.Length; i++) d.floor[i] = Grass;

            // Roads: three each way, four tiles wide, with paved shoulders. The
            // shoulder is a tile of paving either side so the road edge reads as
            // a kerb line from the car rather than grass meeting asphalt.
            int[] lanesX = { 10, 30, 48 };
            int[] lanesZ = { 10, 30, 48 };
            foreach (int cx in lanesX) Stripe(d, cx, true, 4, Asphalt, Paving);
            foreach (int cz in lanesZ) Stripe(d, cz, false, 4, Asphalt, Paving);

            var items = d.items;

            // A town block on each of the four quadrants round the middle
            // junction, fronts turned toward the road they sit on.
            Row(items, "city_house_a", 16, 18, 4, 4.0f, 0f);
            Row(items, "city_house_b", 16, 22, 4, 4.0f, 0f);
            Row(items, "city_townhouse", 36, 18, 3, 4.5f, 0f);
            Row(items, "city_cottage", 36, 23, 3, 4.5f, 0f);
            Row(items, "city_store", 16, 38, 2, 6f, 180f);
            Row(items, "city_diner", 30, 38, 1, 0f, 180f);
            Row(items, "city_apartment", 38, 38, 2, 7f, 180f);
            Row(items, "city_warehouse", 50, 20, 1, 0f, 90f);

            // The five props you can actually drive into. Spread along the middle
            // road so a smoke test is one lap of it.
            Put(items, "city_garage", 20, 34, 0f);
            Put(items, "city_autoshop", 26, 34, 0f);
            Put(items, "city_gas", 34, 34, 0f);
            Put(items, "city_firehouse", 42, 34, 0f);
            Put(items, "city_arena", 50, 46, 0f, 1.0f);

            // Something to jump off.
            Put(items, "dt_ramp_jump", 30, 12, 0f);
            Put(items, "dt_ramp_kicker", 34, 12, 0f);
            Put(items, "toy_ramp_bridge", 12, 30, 90f);

            // Street furniture down the middle road, both sides.
            for (int t = 6; t < 56; t += 6)
            {
                Put(items, "city_lamp", t, 27, 0f);
                Put(items, "city_lamp", t, 33, 180f);
            }
            for (int t = 8; t < 54; t += 12)
            {
                Put(items, "city_hydrant", t, 27, 0f);
                Put(items, "city_bench", t + 3, 33, 180f);
            }
            Put(items, "city_busstop", 24, 27, 0f);
            Put(items, "city_signal", 30, 32, 0f);
            Put(items, "city_signal", 30, 28, 180f);
            Put(items, "city_billboard", 44, 12, 200f);
            Put(items, "city_watertower", 54, 54, 0f);
            Put(items, "city_clocktower", 30, 52, 0f);

            // The park corner. Sparse on purpose — the point is that there is
            // room left for the brush, not that it is already full.
            Put(items, "city_tree_oak", 6, 46, 0f);
            Put(items, "city_tree_maple", 9, 50, 0f);
            Put(items, "city_tree_pine", 5, 53, 0f);
            Put(items, "city_bush", 8, 44, 0f);
            Put(items, "city_hedge", 4, 42, 90f);
            Put(items, "dt_rock_large", 12, 52, 0f);
            Put(items, "dt_rock_small", 14, 49, 0f);

            Put(items, "spawn", 30, 30, 0f);
            return d;
        }

        // ===================================================================
        // base race
        // ===================================================================

        /// <summary>
        /// The clone-me template: one closed spline oval with walls and kerb
        /// stripes, a finish line, eight checkpoints and a four-car grid.
        ///
        /// Deliberately plain. It exists so that "make a new circuit" starts from
        /// something that already laps cleanly, rather than from an empty grid —
        /// dress it, bend the spline, and it is a new track.
        /// </summary>
        private static TrackDesign BuildBaseRace()
        {
            var d = TrackDesign.Default(48, 48);
            d.name = "TinyTorque_BaseRace";
            d.tileSize = 1f;
            d.ambience = "";                       // neutral daylight

            for (int i = 0; i < d.floor.Length; i++) d.floor[i] = Grass;

            // A rounded rectangle, sampled at 16 control points. Catmull-Rom
            // through these is smooth enough that no corner needs a hand-placed
            // apex point.
            const float rx = 16f, rz = 11f;
            var spline = new SplineSpec { closed = true, edgeWalls = true, edgeStripes = true };
            var pts = new List<Vector3>();
            const int N = 16;
            for (int i = 0; i < N; i++)
            {
                float a = i * Mathf.PI * 2f / N;
                // Squared-off cosine: a plain ellipse is one long corner, and a
                // circuit wants straights to brake into.
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                float x = rx * Mathf.Sign(cx) * Mathf.Pow(Mathf.Abs(cx), 0.72f);
                float z = rz * Mathf.Sign(cz) * Mathf.Pow(Mathf.Abs(cz), 0.72f);
                pts.Add(new Vector3(x, 0f, z));
            }
            foreach (var p in pts) spline.AddPoint(p);
            for (int i = 0; i < spline.widths.Count; i++)
            {
                spline.widths[i] = 2.2f;
                spline.surface[i] = Asphalt;
            }
            d.splines.Add(spline);

            var items = d.items;

            // Finish on the middle of the +Z straight, then eight checkpoints
            // spaced evenly round the lap. Gate heading is the tangent, so a
            // gate always faces the way the car goes through it.
            AddGate(items, pts, 0f, "finish", -1);
            for (int i = 1; i <= 8; i++)
                AddGate(items, pts, i / 9f, "checkpoint", i - 1);

            // Four-car grid, staggered, just behind the finish line.
            var start = Sample(pts, 0f);
            var back = -Tangent(pts, 0f);
            var side = new Vector3(-back.z, 0f, back.x);
            for (int i = 0; i < 4; i++)
            {
                var p = start + back * (1.2f + 1.1f * (i / 2)) + side * (i % 2 == 0 ? -0.6f : 0.6f);
                Put(items, "spawn", p, Heading(Tangent(pts, 0f)));
            }

            // Enough scenery to read the corners against, and no more.
            for (int i = 0; i < N; i++)
            {
                var p = Sample(pts, i / (float)N);
                var outward = p.normalized;
                Put(items, i % 2 == 0 ? "city_tree_pine" : "dt_barrier",
                    p + outward * 3.2f, Heading(-outward));
            }

            return d;
        }

        // ===================================================================
        // helpers
        // ===================================================================

        /// <summary>Paint a road: `wide` tiles of `surface`, flanked by one tile
        /// of `shoulder` each side.</summary>
        private static void Stripe(TrackDesign d, int centre, bool alongZ, int wide,
                                   int surface, int shoulder)
        {
            int half = wide / 2;
            int span = alongZ ? d.length : d.width;
            for (int t = 0; t < span; t++)
            {
                for (int o = -half - 1; o <= half; o++)
                {
                    int type = (o < -half || o >= half) ? shoulder : surface;
                    if (alongZ) d.SetFloor(centre + o, t, type);
                    else d.SetFloor(t, centre + o, type);
                }
            }
        }

        private static void Put(List<PlacedItem> items, string id, int tx, int tz,
                                float yaw, float scale = 1f)
        {
            // Tile indices are more readable than world coordinates when hand-
            // authoring a grid; the map is centred on the origin, so convert.
            Put(items, id, new Vector3(tx - 30f + 0.5f, 0f, tz - 30f + 0.5f), yaw, scale);
        }

        private static void Put(List<PlacedItem> items, string id, Vector3 world,
                                float yaw, float scale = 1f)
        {
            items.Add(new PlacedItem
            {
                itemId = id,
                x = world.x,
                z = world.z,
                y = 0f,
                yawDeg = yaw,
                scale = scale,
                pinned = true,      // decorative fill: no Rigidbody, stays batchable
            });
        }

        private static void Row(List<PlacedItem> items, string id, int tx, int tz,
                                int count, float pitch, float yaw)
        {
            for (int i = 0; i < count; i++)
                Put(items, id, Mathf.RoundToInt(tx + i * pitch), tz, yaw);
        }

        private static void AddGate(List<PlacedItem> items, List<Vector3> pts,
                                    float t, string id, int order)
        {
            var p = Sample(pts, t);
            items.Add(new PlacedItem
            {
                itemId = id,
                x = p.x,
                z = p.z,
                y = 0f,
                yawDeg = Heading(Tangent(pts, t)),
                order = order,
                scale = 1f,
            });
        }

        /// <summary>Point on the closed control polygon at normalised t. Linear
        /// between control points is close enough for gate placement — the
        /// ribbon's own Catmull-Rom deviates by centimetres at this point
        /// density, and a gate is metres wide.</summary>
        private static Vector3 Sample(List<Vector3> pts, float t)
        {
            float f = Mathf.Repeat(t, 1f) * pts.Count;
            int i = Mathf.FloorToInt(f);
            return Vector3.Lerp(pts[i % pts.Count], pts[(i + 1) % pts.Count], f - i);
        }

        private static Vector3 Tangent(List<Vector3> pts, float t)
        {
            var a = Sample(pts, t - 0.01f);
            var b = Sample(pts, t + 0.01f);
            var v = b - a;
            v.y = 0f;
            return v.sqrMagnitude > 1e-8f ? v.normalized : Vector3.forward;
        }

        private static float Heading(Vector3 dir) =>
            Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
    }
}
