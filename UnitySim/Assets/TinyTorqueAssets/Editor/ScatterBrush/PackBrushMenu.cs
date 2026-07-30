using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 4 of the pack pipeline: the six shipped scatter presets.
    ///
    /// These are ordinary <see cref="ScatterPreset"/> assets — edit them, or make
    /// your own. They exist so the brush is useful the moment it opens instead of
    /// starting as an empty palette you have to fill by hand from 150 prefabs.
    ///
    /// Every palette pulls the <c>_Mesh</c> variant: scattered scenery is scenery
    /// you expect to collide with. A key that has no prefab is skipped with a
    /// warning rather than baking a null into the asset, so a preset naming a prop
    /// that later gets renamed degrades to a smaller palette instead of a
    /// NullReferenceException mid-drag.
    /// </summary>
    public static class PackBrushMenu
    {
        private struct Def
        {
            public string name;
            public string[] keys;
            public float radius, avoid, scaleMin, scaleMax;
            public int density;
            public bool align;
        }

        private static readonly Def[] Presets =
        {
            // Rocks tilt onto the surface — a boulder standing perfectly upright
            // on a slope is the giveaway that something was placed, not dropped.
            new Def { name = "Rocks", density = 3, radius = 1.2f, avoid = 0.10f,
                      scaleMin = 0.7f, scaleMax = 1.6f, align = true,
                      keys = new[] { "dt_rock_large", "dt_rock_small", "ench_boulder",
                                     "vf_obsidian_block", "vf_crag_spire" } },

            new Def { name = "Trees", density = 2, radius = 2.0f, avoid = 0.35f,
                      scaleMin = 0.85f, scaleMax = 1.35f, align = false,
                      keys = new[] { "city_tree_maple", "city_tree_oak", "city_tree_pine",
                                     "city_tree_young", "ench_tree", "haunt_tree",
                                     "bb_palm" } },

            new Def { name = "Foliage", density = 6, radius = 1.0f, avoid = 0.06f,
                      scaleMin = 0.8f, scaleMax = 1.3f, align = false,
                      keys = new[] { "city_bush", "city_hedge", "ench_hedge",
                                     "ench_topiary", "city_planter" } },

            new Def { name = "UrbanClutter", density = 2, radius = 1.5f, avoid = 0.25f,
                      scaleMin = 0.95f, scaleMax = 1.05f, align = false,
                      keys = new[] { "city_bench", "city_hydrant", "city_mailbox",
                                     "city_busstop", "city_sign", "city_lamp" } },

            new Def { name = "ToyClutter", density = 5, radius = 1.0f, avoid = 0.05f,
                      scaleMin = 0.8f, scaleMax = 1.25f, align = true,
                      keys = new[] { "toy_brick", "toy_block_tower", "toy_crayon",
                                     "toy_domino", "toy_ball", "tw_pencil",
                                     "tw_book_stack" } },

            new Def { name = "HauntClutter", density = 4, radius = 1.4f, avoid = 0.12f,
                      scaleMin = 0.85f, scaleMax = 1.2f, align = true,
                      keys = new[] { "haunt_gravestone", "haunt_pumpkin", "haunt_fence",
                                     "haunt_barrow", "haunt_arch_ruin" } },
        };

        [MenuItem("Tools/TinyTorque Assets/4. Generate brush presets", priority = 103)]
        public static void GeneratePresets()
        {
            PackPaths.EnsureFolder(PackPaths.BrushesRoot);
            int made = 0, missing = 0;

            foreach (var d in Presets)
            {
                string path = PackPaths.BrushesRoot + "/" + d.name + ".asset";
                var preset = AssetDatabase.LoadAssetAtPath<ScatterPreset>(path);
                bool isNew = preset == null;
                if (isNew) preset = ScriptableObject.CreateInstance<ScatterPreset>();

                preset.entries = new List<ScatterPreset.Entry>();
                foreach (string key in d.keys)
                {
                    var prefab = FindMeshPrefab(key);
                    if (prefab == null) { missing++; PackPaths.Warn($"brush '{d.name}': no prefab for '{key}'"); continue; }
                    preset.entries.Add(new ScatterPreset.Entry { prefab = prefab, weight = 1f });
                }

                preset.radius = d.radius;
                preset.density = d.density;
                preset.avoidRadius = d.avoid;
                preset.scaleMin = d.scaleMin;
                preset.scaleMax = d.scaleMax;
                preset.alignToSurface = d.align;
                preset.randomYaw = true;
                preset.surfaceMask = ~0;
                preset.parentName = "Scatter";

                if (isNew) AssetDatabase.CreateAsset(preset, path);
                else EditorUtility.SetDirty(preset);
                made++;
            }

            AssetDatabase.SaveAssets();
            PackPaths.Log($"BRUSHES {made} presets, {missing} palette entries unresolved");
        }

        private static GameObject FindMeshPrefab(string key)
        {
            string dir = PackPaths.PrefabDirFor(PackKind.Prop, PackPaths.PropCategory(key));
            return AssetDatabase.LoadAssetAtPath<GameObject>(dir + "/" + key + "_Mesh.prefab");
        }
    }
}
