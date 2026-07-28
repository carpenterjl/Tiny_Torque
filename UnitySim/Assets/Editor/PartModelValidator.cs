using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Headless check on the Blender-authored part meshes: loads each FBX via
    /// Resources, measures its world-space renderer bounds, and compares them
    /// against the size every asset is contractually authored to. This guards the
    /// two failure modes this pipeline actually hits — the FBX exporter's
    /// metre→centimetre bake importing everything 100x oversized, and an asset
    /// drifting off its authored dimensions during a re-model.
    ///
    /// Run with (editor must be closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.PartModelValidator.Report -logFile &lt;log&gt;
    /// then grep the log for "[PMV] RESULT".
    /// </summary>
    public static class PartModelValidator
    {
        private const float TolMetres = 0.002f;   // +-2 mm

        private const float MinExtentMetres = 0.02f;   // below this, something is scaled wrong

        /// <summary>Expected renderer-bounds size in metres plus a triangle budget.
        /// A null axis is deliberately unconstrained: body height is free, wheel
        /// width varies with tread, and the buggy's flares exceed the core width.
        /// Track props are checked differently — they carry no runtime scale
        /// contract, so they are bounded by max extent and budget instead.</summary>
        private readonly struct Spec
        {
            public readonly string Key, Root;
            public readonly float? X, Y, Z, MaxExtent;
            public readonly int MaxTris;

            /// <summary>Vehicle part: exact authored axes (null = free).</summary>
            public Spec(string key, float? x, float? y, float? z, int maxTris)
            {
                Key = key; Root = "PartModels/";
                X = x; Y = y; Z = z; MaxExtent = null; MaxTris = maxTris;
            }

            /// <summary>Track prop: bound the extent and the triangle budget.</summary>
            public Spec(string key, float maxExtent, int maxTris)
            {
                Key = key; Root = "TrackProps/";
                X = null; Y = null; Z = null; MaxExtent = maxExtent; MaxTris = maxTris;
            }
        }

        private static readonly Spec[] Specs =
        {
            // Wheels: axle along X, outer tyre radius 33 mm -> 66 mm across.
            new Spec("wheel_slick",   null,   0.066f, 0.066f, 4000),
            new Spec("wheel_knobby",  null,   0.066f, 0.066f, 4000),
            new Spec("wheel_rally",   null,   0.066f, 0.066f, 4000),
            // Bodies: width and length are pinned because the runtime divides by
            // CarVehicle.BodyMeshAuthorSize; height is free.
            new Spec("body_shell",    0.200f, null,   0.420f, 6500),
            new Spec("body_lowracer", 0.200f, null,   0.420f, 6500),
            new Spec("body_buggy",    null,   null,   0.420f, 6500),
            // Battery renders at authored size - no runtime scale at all.
            new Spec("battery_stick", 0.047f, 0.025f, 0.138f, 800),
            new Spec("antenna_stub",  null,   null,   null,   600),
            // TinyTorque show cars (build_vehicles.py). Length is pinned by the
            // exporter's scale-to-0.420 construction; width is free (real widths
            // 0.17-0.20 after uniform scale). Budgets from the first [PMV] run.
            new Spec("body_coupe",    null,   null,   0.420f, 12000),
            new Spec("body_baja",     null,   null,   0.420f, 25000),
            new Spec("body_patrol",   null,   null,   0.420f, 19000),
            new Spec("wheel_coupe",   null,   0.066f, 0.066f, 6000),
            new Spec("wheel_baja",    null,   0.066f, 0.066f, 6000),
            new Spec("wheel_patrol",  null,   0.066f, 0.066f, 6000),
            // Cosmetic parts render at authored size, like antenna_stub.
            new Spec("light_bar",     null,   null,   null,  2500),
            new Spec("light_pods",    null,   null,   null,  2500),
            new Spec("antenna_whip",  null,   null,   null,  1500),
            new Spec("antenna_flag",  null,   null,   null,  2000),
            new Spec("antenna_twin",  null,   null,   null,  1500),
            // Track props (Resources/TrackProps) are appended here as each family
            // is authored — a listed asset that is missing is a hard FAIL, so a
            // key only goes in once its FBX ships. Budgets: small prop 1500,
            // medium 3000, hero landmark 6000.
            new Spec("arc_item_box",   0.30f, 2000),
            new Spec("arc_missile",    0.22f, 1200),
            new Spec("arc_banana",     0.18f, 1200),
            new Spec("arc_shield_orb", 0.10f,  600),
            // Toy Workshop — real desk objects at real size, which is exactly
            // why they read as enormous beside a 0.42 m car.
            new Spec("tw_book_stack",  0.35f, 1500),
            new Spec("tw_ruler_ramp",  0.40f, 1500),
            new Spec("tw_brick_wall",  0.42f, 3000),
            new Spec("tw_pencil",      0.28f, 1000),
            new Spec("tw_mug",         0.18f, 1500),
            new Spec("tw_tape_arch",   0.75f, 1500),
            // Neon Grid
            new Spec("ng_pylon",       0.40f, 1000),
            new Spec("ng_arch_gate",   1.05f, 1500),
            new Spec("ng_ring_float",  0.60f, 2000),
            new Spec("ng_barrier_glow",0.62f, 1000),
            new Spec("ng_data_cube",   0.28f, 1500),
            new Spec("ng_spire",       0.90f, 1000),
            // Beach Boardwalk
            new Spec("bb_palm",            0.80f, 1500),
            new Spec("bb_surfboard_ramp",  0.62f, 1500),
            new Spec("bb_plank_wall",      0.72f, 1500),
            new Spec("bb_tiki_torch",      0.62f, 1500),
            new Spec("bb_beach_ball",      0.22f, 2000),
            new Spec("bb_sandcastle",      0.45f, 2500),
            // Volcano Foundry
            new Spec("vf_rock_arch",     0.98f, 1500),
            new Spec("vf_obsidian_block",0.48f,  600),
            new Spec("vf_steam_vent",    0.40f, 1000),
            new Spec("vf_barrel",        0.28f, 2000),
            new Spec("vf_grate_ramp",    0.55f, 1000),
            new Spec("vf_crag_spire",    0.85f,  600),
            // TinyTorque map packs (build_map_props.py) — extents are the
            // scaled authored size +10 %, budgets rounded up from measured
            // counts. The landmark rows (volcano, peak, barrow, castle,
            // tower, buildings) are backdrop pieces at full 1/10-world scale,
            // hence extents far past the 1 m-ish prop norm — deliberate.
            // Downtown
            new Spec("dt_arch_gate",       3.50f,  5000),
            new Spec("dt_arch_rock",       5.14f,   600),
            new Spec("dt_barrier",         0.34f,   300),
            new Spec("dt_bld_block",       3.40f,   600),
            new Spec("dt_bld_hangar",      3.22f,  1000),
            new Spec("dt_bld_tower",       9.70f,  1000),
            new Spec("dt_cone",            0.09f,  1000),
            new Spec("dt_ramp_jump",       2.45f,  2500),
            new Spec("dt_ramp_kicker",     1.57f,  1500),
            new Spec("dt_rock_large",      0.96f,   300),
            new Spec("dt_rock_small",      0.22f,   300),
            new Spec("dt_street_lamp",     0.90f,  1000),
            new Spec("dt_traffic_light",   0.63f,  1500),
            new Spec("dt_volcano",        13.91f,  3000),
            // Toy Room
            new Spec("toy_ball",           0.96f,  1000),
            new Spec("toy_bed",            5.53f,  4500),
            new Spec("toy_block_tower",    1.48f,  3500),
            new Spec("toy_bookcase",       5.02f,  9000),
            new Spec("toy_box",            1.76f,   300),
            new Spec("toy_brick",          0.53f,  1500),
            new Spec("toy_chair",          2.38f,  1500),
            new Spec("toy_crayon",         0.27f,   600),
            new Spec("toy_domino",         0.27f,  1000),
            new Spec("toy_dresser",        3.38f,  2500),
            new Spec("toy_floor_lamp",     4.26f,  1000),
            new Spec("toy_gate",           3.17f,  6000),
            new Spec("toy_hoop",           2.45f,  2000),
            new Spec("toy_lamp",           1.29f,  1500),
            new Spec("toy_ramp_bridge",    4.73f,   600),
            new Spec("toy_ramp_plank",     2.14f,   600),
            new Spec("toy_table",          3.17f,  2000),
            // Enchanted Kingdom
            new Spec("ench_arch_vine",     1.77f,  2500),
            new Spec("ench_boulder",       0.57f,   300),
            new Spec("ench_castle",        9.61f, 20000),
            new Spec("ench_cottage",       1.79f,  1500),
            new Spec("ench_crystal",       1.02f,   300),
            new Spec("ench_fountain",      1.32f,  2500),
            new Spec("ench_gate",          3.55f,  7000),
            new Spec("ench_gatehouse",     3.33f,  4500),
            new Spec("ench_hedge",         0.56f,   300),
            new Spec("ench_lamp",          0.89f,  1500),
            new Spec("ench_peak",         16.72f,  3000),
            new Spec("ench_ramp_bridge",   2.03f,  1000),
            new Spec("ench_ramp_terrace",  2.75f,  2500),
            new Spec("ench_topiary",       0.49f,  1000),
            new Spec("ench_tower",         6.13f,  3500),
            new Spec("ench_tree",          2.88f,  2000),
            // Haunted Hollow
            new Spec("haunt_arch_ruin",    2.53f,  3000),
            new Spec("haunt_barrow",      12.42f,  3500),
            new Spec("haunt_chapel",       2.73f,  2500),
            new Spec("haunt_crypt",        1.01f,  1500),
            new Spec("haunt_fence",        0.63f,  1500),
            new Spec("haunt_gaslamp",      0.89f,  1000),
            new Spec("haunt_gate",         3.53f,  5000),
            new Spec("haunt_ghost",        1.21f,  3000),
            new Spec("haunt_gravestone",   0.35f,   600),
            new Spec("haunt_hearse",       1.04f,  3000),
            new Spec("haunt_mansion",      4.02f,  6000),
            new Spec("haunt_pumpkin",      0.32f,   600),
            new Spec("haunt_ramp_slab",    1.87f,  2000),
            new Spec("haunt_ramp_tomb",    2.87f,  2000),
            new Spec("haunt_tree",         2.17f,  2000),
            new Spec("haunt_wisp",         0.49f,   600),
        };

        public static void Report()
        {
            int fail = 0;
            foreach (var s in Specs)
            {
                var src = Resources.Load<GameObject>(s.Root + s.Key);
                if (src == null)
                {
                    Debug.LogError($"[PMV] FAIL {s.Key}: missing asset");
                    fail++; continue;
                }

                var inst = Object.Instantiate(src);
                var rs = inst.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0)
                {
                    Debug.LogError($"[PMV] FAIL {s.Key}: no renderers");
                    Object.DestroyImmediate(inst); fail++; continue;
                }

                var b = rs[0].bounds;
                for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);

                int tris = 0;
                foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
                    if (mf.sharedMesh != null) tris += mf.sharedMesh.triangles.Length / 3;

                string why = "";
                if (Off(b.size.x, s.X)) why += $" X={b.size.x:0.000}!={s.X:0.000}";
                if (Off(b.size.y, s.Y)) why += $" Y={b.size.y:0.000}!={s.Y:0.000}";
                if (Off(b.size.z, s.Z)) why += $" Z={b.size.z:0.000}!={s.Z:0.000}";
                if (s.MaxExtent.HasValue)
                {
                    // Props have no axis contract; bound them at both ends so the
                    // x100 metre->centimetre bake and its inverse both get caught.
                    float ext = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    if (ext > s.MaxExtent.Value) why += $" extent={ext:0.000}>{s.MaxExtent.Value:0.000}";
                    if (ext < MinExtentMetres) why += $" extent={ext:0.000}<{MinExtentMetres:0.000}";
                }
                if (tris > s.MaxTris) why += $" tris={tris}>{s.MaxTris}";

                string line = $"{s.Key}: parts={rs.Length} tris={tris} " +
                              $"size=({b.size.x:0.000},{b.size.y:0.000},{b.size.z:0.000}) " +
                              $"center=({b.center.x:0.000},{b.center.y:0.000},{b.center.z:0.000})";
                if (why.Length == 0) Debug.Log($"[PMV] PASS {line}");
                else { Debug.LogError($"[PMV] FAIL {line} -{why}"); fail++; }

                Object.DestroyImmediate(inst);
            }
            Debug.Log($"[PMV] RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({Specs.Length} assets)");
        }

        private static bool Off(float actual, float? expected) =>
            expected.HasValue && Mathf.Abs(actual - expected.Value) > TolMetres;
    }
}
