using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>What kind of asset an entry is — decides which prefab variants it
    /// gets and which material table shades it.</summary>
    public enum PackKind
    {
        /// <summary>Vehicle body/wheel/light/antenna/battery. Mounts to a car at
        /// runtime, so the pack prefab carries no collider.</summary>
        VehiclePart,
        /// <summary>Track scenery. Gets both a mesh-collider and a box-collider
        /// prefab.</summary>
        Prop,
        /// <summary>Unlockable cosmetic or crate. No collider, same as parts.</summary>
        Cosmetic,
    }

    /// <summary>One asset in the pack: where it came from and where its generated
    /// mesh/material/prefab live.</summary>
    public struct PackEntry
    {
        public string key;          // "city_garage", "body_coupe", "rim_web", ...
        public PackKind kind;
        public string category;     // "City", "Coupe", "Rim", ...
        public string sourcePath;   // the Resources/ asset this was copied from ("" for pack-native)
        public string modelPath;    // Assets/TinyTorqueAssets/Models/<...>/<key>.fbx

        public string MaterialDir => PackPaths.MaterialDirFor(kind, category);
        public string PrefabDir => PackPaths.PrefabDirFor(kind, category);
    }

    /// <summary>
    /// The pack's single source of truth for layout and categorisation.
    ///
    /// Categories are DERIVED from the key prefixes the exporters already bake in
    /// (`build_map_props.py` writes `&lt;pack&gt;_&lt;prop&gt;`; `build_vehicles.py` writes
    /// `body_&lt;car&gt;`/`wheel_&lt;car&gt;`; `build_cosmetics.py` writes `&lt;slot&gt;_&lt;item&gt;`),
    /// not hand-listed — a hand-typed list of 204 names is a list that goes stale
    /// the first time a prop is added. <see cref="All"/> scans the three Resources
    /// folders plus the pack's own Arena folder, so the manifest is always what is
    /// actually on disk and <c>PackValidator</c> can compare it against the counts.
    /// </summary>
    public static class PackPaths
    {
        public const string Root = "Assets/TinyTorqueAssets";
        public const string ModelsRoot = Root + "/Models";
        public const string MaterialsRoot = Root + "/Materials";
        public const string PrefabsRoot = Root + "/Prefabs";
        public const string BrushesRoot = Root + "/Brushes";
        public const string MapsRoot = Root + "/Maps";
        public const string ScenesRoot = Root + "/Scenes";

        /// <summary>The three shipping Resources folders the pack copies from.</summary>
        public const string ResPartModels = "Assets/Resources/PartModels";
        public const string ResTrackProps = "Assets/Resources/TrackProps";
        public const string ResCosmetics = "Assets/Resources/Cosmetics";

        /// <summary>Arena tiles are pack-native — they are exported straight here by
        /// <c>build_map_props.py</c> and deliberately never enter Resources, because
        /// adding them to the game is out of scope for the pack.</summary>
        public const string ArenaCategory = "Arena";

        // ---- categorisation -------------------------------------------------

        /// <summary>Track-prop key prefix → category folder. Order does not matter:
        /// these are `&lt;prefix&gt;_` matches, not substring matches.</summary>
        private static readonly (string prefix, string category)[] PropCategories =
        {
            ("dt_",    "Downtown"),
            ("toy_",   "Toy"),
            ("ench_",  "Enchanted"),
            ("haunt_", "Haunted"),
            ("city_",  "City"),
            ("soc_",   ArenaCategory),
            ("arc_",   "Arcade"),     // item box, banana, missile, shield orb
            ("bb_",    "Beach"),      // the three original arcade circuits' scenery
            ("ng_",    "NeonGrid"),
            ("tw_",    "Tabletop"),
            ("vf_",    "Volcano"),
        };

        /// <summary>Vehicle-part key suffix → car folder. The seven TinyTorque cars
        /// each get a folder; the legacy shells, the three shared wheel styles and
        /// the small parts (battery, antennas, light bars) share "Common".</summary>
        private static readonly string[] Cars =
        { "coupe", "baja", "patrol", "rattle", "redline", "highwing", "autopia" };

        private static readonly (string prefix, string category)[] CosmeticCategories =
        {
            ("top_",  "Topper"),
            ("rim_",  "Rim"),
            ("orn_",  "Ornament"),
            ("bob_",  "Bobble"),
            ("wing_", "Wing"),
        };

        /// <summary>The four crates carry no slot prefix (they are not equippable).</summary>
        private static readonly string[] Crates = { "crate", "chrome", "haunt", "vault" };

        public static string PropCategory(string key)
        {
            foreach (var (prefix, category) in PropCategories)
                if (key.StartsWith(prefix, System.StringComparison.Ordinal)) return category;
            return "Misc";
        }

        public static string VehicleCategory(string key)
        {
            foreach (string car in Cars)
                if (key.EndsWith("_" + car, System.StringComparison.Ordinal))
                    return char.ToUpperInvariant(car[0]) + car.Substring(1);
            return "Common";
        }

        public static string CosmeticCategory(string key)
        {
            foreach (string c in Crates)
                if (key == c) return "Crates";
            foreach (var (prefix, category) in CosmeticCategories)
                if (key.StartsWith(prefix, System.StringComparison.Ordinal)) return category;
            return "Misc";
        }

        // ---- folder layout --------------------------------------------------

        public static string ModelDirFor(PackKind kind, string category)
        {
            switch (kind)
            {
                case PackKind.VehiclePart: return ModelsRoot + "/Vehicles/" + category;
                case PackKind.Cosmetic: return ModelsRoot + "/Cosmetics/" + category;
                default: return ModelsRoot + "/Props/" + category;
            }
        }

        public static string MaterialDirFor(PackKind kind, string category)
        {
            switch (kind)
            {
                case PackKind.VehiclePart: return MaterialsRoot + "/Vehicles";
                case PackKind.Cosmetic: return MaterialsRoot + "/Cosmetics";
                default: return MaterialsRoot + "/Props/" + category;
            }
        }

        public static string PrefabDirFor(PackKind kind, string category)
        {
            switch (kind)
            {
                case PackKind.VehiclePart: return PrefabsRoot + "/Vehicles/" + category;
                case PackKind.Cosmetic: return PrefabsRoot + "/Cosmetics/" + category;
                default: return PrefabsRoot + "/Props/" + category;
            }
        }

        // ---- the manifest ---------------------------------------------------

        /// <summary>
        /// Every asset the pack owns, derived from what is on disk. Sources are the
        /// three Resources folders (the 204 shipping meshes) plus the pack's own
        /// Arena folder (the soccer tiles, which have no Resources original).
        /// </summary>
        public static List<PackEntry> All()
        {
            var list = new List<PackEntry>();

            foreach (string src in Fbx(ResPartModels))
            {
                string key = Path.GetFileNameWithoutExtension(src);
                if (IsNotPackContent(key)) continue;
                string cat = VehicleCategory(key);
                list.Add(new PackEntry
                {
                    key = key,
                    kind = PackKind.VehiclePart,
                    category = cat,
                    sourcePath = src,
                    modelPath = ModelDirFor(PackKind.VehiclePart, cat) + "/" + key + ".fbx",
                });
            }

            foreach (string src in Fbx(ResTrackProps))
            {
                string key = Path.GetFileNameWithoutExtension(src);
                string cat = PropCategory(key);
                list.Add(new PackEntry
                {
                    key = key,
                    kind = PackKind.Prop,
                    category = cat,
                    sourcePath = src,
                    modelPath = ModelDirFor(PackKind.Prop, cat) + "/" + key + ".fbx",
                });
            }

            foreach (string src in Fbx(ResCosmetics))
            {
                string key = Path.GetFileNameWithoutExtension(src);
                string cat = CosmeticCategory(key);
                list.Add(new PackEntry
                {
                    key = key,
                    kind = PackKind.Cosmetic,
                    category = cat,
                    sourcePath = src,
                    modelPath = ModelDirFor(PackKind.Cosmetic, cat) + "/" + key + ".fbx",
                });
            }

            // Pack-native: the arena tile kit, exported straight into the pack.
            string arenaDir = ModelDirFor(PackKind.Prop, ArenaCategory);
            foreach (string m in Fbx(arenaDir))
            {
                list.Add(new PackEntry
                {
                    key = Path.GetFileNameWithoutExtension(m),
                    kind = PackKind.Prop,
                    category = ArenaCategory,
                    sourcePath = "",          // no Resources original by design
                    modelPath = m,
                });
            }

            list.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            return list;
        }

        /// <summary>
        /// Keys that live in Resources/PartModels but that the pack deliberately
        /// does not own.
        ///
        /// The Tiguan is a physics-verification asset: a real car at 1:1 in a kit
        /// of 1/10 arcade toys, and its 29 materials come from its own probed
        /// manifest rather than from <c>PartVisualFactory.AccentTokens</c>. Left
        /// in, <c>PackPrefabGenerator</c> would hand it the accent table, none of
        /// its "tig*" names would match, and every renderer would take the
        /// fallback — the pack would ship a full-size Tiguan uniformly painted in
        /// generic arcade body paint. <c>PackValidator</c> would NOT fail that,
        /// because a fallback is a real material and not a MISSING one, which
        /// makes it worse than a failure: silently wrong content that passes the
        /// gate.
        /// </summary>
        public static readonly string[] NotPackContent =
        {
            "body_tiguan", "wheel_tiguan", "wheel_tiguan_r",
        };

        public static bool IsNotPackContent(string key)
        {
            foreach (string k in NotPackContent)
                if (key == k) return true;
            return false;
        }

        /// <summary>The four baked liveries live beside their bodies and must ride
        /// along, or the pack's paint materials load a null texture.</summary>
        public static IEnumerable<string> LiveryTextures()
        {
            string abs = ToAbsolute(ResPartModels);
            if (!Directory.Exists(abs)) yield break;
            foreach (string f in Directory.GetFiles(abs, "*_paint.png"))
                yield return ToProjectRelative(f);
        }

        private static IEnumerable<string> Fbx(string projectRelativeDir)
        {
            string abs = ToAbsolute(projectRelativeDir);
            if (!Directory.Exists(abs)) yield break;
            var files = Directory.GetFiles(abs, "*.fbx");
            System.Array.Sort(files, string.CompareOrdinal);
            foreach (string f in files) yield return ToProjectRelative(f);
        }

        public static string ToAbsolute(string projectRelative) =>
            Path.Combine(Directory.GetCurrentDirectory(), projectRelative);

        public static string ToProjectRelative(string absolute)
        {
            string p = absolute.Replace('\\', '/');
            int i = p.IndexOf("/Assets/", System.StringComparison.Ordinal);
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>
        /// Create a project folder and every missing parent.
        ///
        /// Deliberately <see cref="Directory.CreateDirectory"/> rather than
        /// <c>AssetDatabase.CreateFolder</c>. CreateFolder UNIQUIFIES: ask for a
        /// folder it thinks already exists and it silently makes "Arena 1"
        /// instead. Inside a <c>StartAssetEditing</c> batch the database has not
        /// caught up with folders created moments earlier in the same batch, so
        /// <c>IsValidFolder</c> answers false and every repeat call spawns another
        /// numbered twin — which is exactly how this pack grew a "Misc 13".
        /// Directory.CreateDirectory is idempotent and never renames; the Refresh
        /// at the end of each step imports the result.
        /// </summary>
        public static void EnsureFolder(string projectRelativeDir)
        {
            if (string.IsNullOrEmpty(projectRelativeDir)) return;
            Directory.CreateDirectory(ToAbsolute(projectRelativeDir));
        }

        /// <summary>Every folder the pack expects to exist, created up front so the
        /// generators never race the AssetDatabase.</summary>
        public static void EnsureLayout()
        {
            EnsureFolder(ModelsRoot);
            EnsureFolder(MaterialsRoot);
            EnsureFolder(PrefabsRoot);
            EnsureFolder(BrushesRoot);
            EnsureFolder(MapsRoot);
            EnsureFolder(ScenesRoot);

            foreach (var (_, category) in PropCategories)
            {
                EnsureFolder(ModelDirFor(PackKind.Prop, category));
                EnsureFolder(MaterialDirFor(PackKind.Prop, category));
                EnsureFolder(PrefabDirFor(PackKind.Prop, category));
            }
            EnsureFolder(MaterialDirFor(PackKind.VehiclePart, "Common"));
            EnsureFolder(MaterialDirFor(PackKind.Cosmetic, "Common"));
            foreach (string car in Cars)
            {
                string cat = char.ToUpperInvariant(car[0]) + car.Substring(1);
                EnsureFolder(ModelDirFor(PackKind.VehiclePart, cat));
                EnsureFolder(PrefabDirFor(PackKind.VehiclePart, cat));
            }
            EnsureFolder(ModelDirFor(PackKind.VehiclePart, "Common"));
            EnsureFolder(PrefabDirFor(PackKind.VehiclePart, "Common"));
            foreach (var (_, category) in CosmeticCategories)
            {
                EnsureFolder(ModelDirFor(PackKind.Cosmetic, category));
                EnsureFolder(PrefabDirFor(PackKind.Cosmetic, category));
            }
            EnsureFolder(ModelDirFor(PackKind.Cosmetic, "Crates"));
            EnsureFolder(PrefabDirFor(PackKind.Cosmetic, "Crates"));

            AssetDatabase.Refresh();
        }

        /// <summary>Shared log prefix so pack output is greppable next to
        /// [PMV]/[TPV]/[COS] in a headless log.</summary>
        public const string Tag = "[PACK]";

        public static void Log(string msg) => Debug.Log(Tag + " " + msg);
        public static void Warn(string msg) => Debug.LogWarning(Tag + " " + msg);
    }
}
