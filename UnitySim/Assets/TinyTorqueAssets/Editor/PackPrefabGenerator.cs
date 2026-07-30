using System.Collections.Generic;
using System.IO;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 3 of the pack pipeline: a draggable prefab for every mesh in the kit.
    ///
    /// Variants, and why there are two of them for props:
    ///   • <c>&lt;key&gt;_Mesh</c> — one non-convex MeshCollider per MeshFilter, which
    ///     is exactly what <c>TrackCatalog.AddMeshColliders</c> does at map build.
    ///     Drop this in a scene and it collides the way it collides in the game.
    ///   • <c>&lt;key&gt;_Box</c> — a single BoxCollider fitted to combined renderer
    ///     bounds. The cheap stand-in, and a useful A/B against the mesh version:
    ///     the whole reason the game abandoned primitive hulls was that a box
    ///     round a hollow building seals it and a capsule round a wide low mesh
    ///     degenerates into a sphere.
    /// Vehicle parts and cosmetics get one <c>&lt;key&gt;_Viz</c> with no collider at
    /// all — they mount to a moving car at runtime, so any collider on them would
    /// be wrong by construction.
    ///
    /// Materials come from <see cref="PackMaterialGenerator"/>, which clones the
    /// game's own. Binding follows <c>PartMeshLibrary.AssignByName</c>'s rule
    /// exactly — first-match, case-insensitive substring, table order load-bearing
    /// — because the tables were hand-ordered longest-first to satisfy it and a
    /// different rule here would silently paint different colours.
    /// </summary>
    public static class PackPrefabGenerator
    {
        [MenuItem("Tools/TinyTorque Assets/3. Generate prefabs", priority = 102)]
        public static void Generate()
        {
            PackPaths.EnsureLayout();
            int made = 0, unbound = 0, missing = 0;
            var entries = PackPaths.All();

            try
            {
                AssetDatabase.StartAssetEditing();

                foreach (var e in entries)
                {
                    var src = AssetDatabase.LoadAssetAtPath<GameObject>(e.modelPath);
                    if (src == null) { missing++; PackPaths.Warn("no mesh at " + e.modelPath); continue; }

                    PackPaths.EnsureFolder(e.PrefabDir);

                    _currentKey = e.key;
                    if (e.category == PackPaths.ArenaCategory)
                    {
                        // One geometry, three palettes — the theme axis cashed in.
                        foreach (string theme in PackSocData.Themes)
                        {
                            string dir = PackPaths.MaterialsRoot + "/Arena/" + theme;
                            var tokens = ToAssets(PackSocData.Tokens(theme), dir);
                            made += Emit(e, src, tokens, theme, ArenaFallback(e.key, tokens),
                                         ref unbound);
                        }
                    }
                    else
                    {
                        made += Emit(e, src, TokensFor(e), null, Fallback(e), ref unbound);
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            PackPaths.Log($"PREFABS {made} written from {entries.Count} meshes " +
                          $"({unbound} renderers took their kind's fallback material, " +
                          $"{missing} meshes missing)");

            // Name them rather than just counting them. Most are expected — a
            // one-material FBX imports with the ROOT carrying the mesh, named
            // after the file rather than after its token, so an ornament or a
            // legacy shell can never match and the fallback IS the right answer
            // (the game reaches the same one). What this line is really for is
            // the day a MULTI-material prop shows up here, which means a token
            // stopped matching and a piece is now wearing grey.
            if (_unboundByKey.Count > 0)
            {
                var keys = new List<string>(_unboundByKey.Keys);
                keys.Sort();
                foreach (string k in keys)
                {
                    var (names, mat) = _unboundByKey[k];
                    PackPaths.Log($"FALLBACK {k} -> {mat}: {string.Join(", ", names)}");
                }
            }
            _unboundByKey.Clear();
        }

        // -------------------------------------------------------------------

        /// <summary>
        /// What a renderer wears when no token claims it. Kind-specific on
        /// purpose, because the GAME's fallback is kind-specific:
        ///   • cosmetics — <c>CosmeticCatalog.Build</c> passes <c>Mat("dark")</c>,
        ///     so the pack passes the same and single-material ornaments look
        ///     right rather than merely non-magenta;
        ///   • body shells — the tintable ones take the player's colour at
        ///     runtime, so the pack shows the design default;
        ///   • everything else — an explicit neutral.
        /// </summary>
        private static Material Fallback(PackEntry e)
        {
            if (e.kind == PackKind.Cosmetic) return CosmeticAsset("dark");
            if (e.kind == PackKind.VehiclePart &&
                e.key.StartsWith("body_", System.StringComparison.Ordinal))
                return PackMaterialGenerator.BodyPaint;
            return PackMaterialGenerator.Neutral;
        }

        private static Material CosmeticAsset(string token)
        {
            foreach (var (t, m) in CosmeticCatalog.Tokens)
                if (t == token)
                    return PackMaterialGenerator.Asset(
                        m, PackPaths.MaterialDirFor(PackKind.Cosmetic, ""), token);
            return PackMaterialGenerator.Neutral;
        }

        /// <summary>
        /// Two arena tiles are a single material, and a one-object FBX imports
        /// with the ROOT carrying the mesh — named after the file, not after the
        /// exporter's token. So <c>soc_circle</c> and <c>soc_crease</c> arrive as
        /// names no slot matches, even though both are unambiguously the pitch
        /// <c>line</c> material. (<c>soc_line</c> and <c>soc_ball</c> escape this
        /// only because their filenames happen to contain their own token.)
        /// </summary>
        private static readonly Dictionary<string, string> ArenaSingleSlot =
            new Dictionary<string, string> { { "soc_circle", "line" }, { "soc_crease", "line" } };

        private static Material ArenaFallback(string key, (string token, Material mat)[] tokens)
        {
            if (ArenaSingleSlot.TryGetValue(key, out string slot) && tokens != null)
                foreach (var (t, m) in tokens)
                    if (t == slot) return m;
            return PackMaterialGenerator.Neutral;
        }

        private static int Emit(PackEntry e, GameObject src,
                                (string token, Material mat)[] tokens,
                                string themeSuffix, Material fallback, ref int unbound)
        {
            string stem = e.key + (themeSuffix != null ? "_" + themeSuffix : "");
            int n = 0;

            if (e.kind == PackKind.Prop)
            {
                n += Write(src, tokens, fallback, e.PrefabDir + "/" + stem + "_Mesh.prefab",
                           ColliderMode.Mesh, ref unbound) ? 1 : 0;
                n += Write(src, tokens, fallback, e.PrefabDir + "/" + stem + "_Box.prefab",
                           ColliderMode.Box, ref unbound) ? 1 : 0;
            }
            else
            {
                n += Write(src, tokens, fallback, e.PrefabDir + "/" + stem + "_Viz.prefab",
                           ColliderMode.None, ref unbound) ? 1 : 0;
            }
            return n;
        }

        private enum ColliderMode { None, Mesh, Box }

        /// <summary>Which asset is being written, and every renderer name in it
        /// that no token claimed — reported by name at the end of the run.</summary>
        private static string _currentKey = "";
        private static readonly
            SortedDictionary<string, (SortedSet<string> names, string mat)> _unboundByKey =
            new SortedDictionary<string, (SortedSet<string>, string)>();

        private static bool Write(GameObject src, (string token, Material mat)[] tokens,
                                  Material fallback, string path, ColliderMode mode,
                                  ref int unbound)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(src);
            if (go == null) return false;
            try
            {
                // Break the model link so the prefab owns its own materials and
                // colliders; a variant of an FBX cannot carry added components on
                // imported children reliably across a reimport.
                PrefabUtility.UnpackPrefabInstance(
                    go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                unbound += Bind(go, tokens, fallback);
                switch (mode)
                {
                    case ColliderMode.Mesh: AddMeshColliders(go); break;
                    case ColliderMode.Box: AddFittedBox(go); break;
                }

                PrefabUtility.SaveAsPrefabAsset(go, path);
                return true;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Assign materials by name token. Returns how many renderers
        /// matched nothing — the number the log reports, because a silent fallback
        /// is how a wrong colour ships.</summary>
        private static int Bind(GameObject root, (string token, Material mat)[] tokens,
                                Material fallback)
        {
            int missed = 0;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name.ToLowerInvariant();
                Material hit = null;
                if (tokens != null)
                {
                    foreach (var (token, mat) in tokens)
                    {
                        if (string.IsNullOrEmpty(token)) continue;
                        if (n.Contains(token.ToLowerInvariant())) { hit = mat; break; }
                    }
                }
                if (hit == null)
                {
                    missed++;
                    hit = fallback;
                    if (!_unboundByKey.TryGetValue(_currentKey, out var rec))
                        _unboundByKey[_currentKey] = rec =
                            (new SortedSet<string>(), hit != null ? hit.name : "none");
                    rec.names.Add(r.gameObject.name);
                }
                if (hit == null) continue;

                // One material per renderer: these meshes are separated by
                // material in Blender, so a renderer is one material by
                // construction — but honour a multi-submesh piece if one appears.
                var mats = new Material[Mathf.Max(1, r.sharedMaterials.Length)];
                for (int i = 0; i < mats.Length; i++) mats[i] = hit;
                r.sharedMaterials = mats;
            }
            return missed;
        }

        /// <summary>Per-piece non-convex MeshCollider — the same rule
        /// <c>TrackCatalog.AddMeshColliders</c> applies at map build time.</summary>
        private static void AddMeshColliders(GameObject root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;      // non-convex; static prop
            }
        }

        /// <summary>One BoxCollider on the root, fitted to combined renderer
        /// bounds expressed in the root's local space.</summary>
        private static void AddFittedBox(GameObject root)
        {
            var rs = root.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return;

            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            var inv = root.transform.worldToLocalMatrix;
            foreach (var r in rs)
            {
                var b = r.bounds;
                // All eight corners through the inverse — a bounds' centre and
                // extents alone are wrong the moment a child is rotated.
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    var l = inv.MultiplyPoint3x4(c);
                    lo = Vector3.Min(lo, l);
                    hi = Vector3.Max(hi, l);
                }
            }

            var box = root.AddComponent<BoxCollider>();
            box.center = (lo + hi) * 0.5f;
            box.size = hi - lo;
        }

        // -------------------------------------------------------------------
        // token tables
        // -------------------------------------------------------------------

        private static (string token, Material mat)[] TokensFor(PackEntry e)
        {
            switch (e.kind)
            {
                case PackKind.VehiclePart:
                {
                    string dir = PackPaths.MaterialDirFor(PackKind.VehiclePart, e.category);

                    // Battery and antennas keep their token arrays inline at their
                    // call sites against private materials, so they are harvested
                    // from the public builders rather than read off a table.
                    var parts = PackMaterialGenerator.PartMaterialMap();
                    if (parts.TryGetValue(e.key, out var partNames))
                        return FromHarvest(partNames, dir, e.key);

                    if (e.key.StartsWith("wheel_", System.StringComparison.Ordinal))
                        return ToAssets(PartVisualFactory.WheelTokens, dir);

                    // "paint" last, so the four baked-livery tokens that CONTAIN
                    // it (rustpaint, coupepaint, bajapaint, patrolpaint) still win
                    // — they sit at the front of AccentTokens for exactly that
                    // reason. What reaches this entry is a tintable shell, which
                    // has no static material to clone.
                    var accents = ToAssets(PartVisualFactory.AccentTokens, dir);
                    var withPaint = new (string, Material)[accents.Length + 1];
                    accents.CopyTo(withPaint, 0);
                    withPaint[accents.Length] = ("paint", PackMaterialGenerator.BodyPaint);
                    return withPaint;
                }

                case PackKind.Cosmetic:
                    return ToAssets(CosmeticCatalog.Tokens,
                                    PackPaths.MaterialDirFor(PackKind.Cosmetic, e.category));

                default:
                {
                    // Props: the harvested answer, keyed by the renderer's own
                    // name. Turned into a token table so one Bind() serves every
                    // kind — an exact name is just a substring that matches itself.
                    var map = PackMaterialGenerator.PropMaterialMap();
                    if (!map.TryGetValue(e.key, out var byName)) return null;
                    return FromHarvest(byName, PackPaths.MaterialDirFor(PackKind.Prop, e.category),
                                       e.key);
                }
            }
        }

        /// <summary>Turn a harvested (renderer name → material) map into a token
        /// table. An exact name is just a substring that matches itself, so one
        /// Bind() serves harvested and tabled assets alike.</summary>
        private static (string token, Material mat)[] FromHarvest(
            Dictionary<string, Material> byName, string dir, string key)
        {
            PackPaths.EnsureFolder(dir);
            var list = new List<(string, Material)>();
            foreach (var kv in byName)
            {
                var asset = PackMaterialGenerator.Asset(kv.Value, dir, key + "_" + kv.Key);
                if (asset != null) list.Add((kv.Key, asset));
            }
            // Longest first so "glow_a_1" cannot be claimed by "glow".
            list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            return list.ToArray();
        }

        /// <summary>Swap a runtime token table for the equivalent table of .mat
        /// assets, preserving order — the order is the matching rule.</summary>
        private static (string token, Material mat)[] ToAssets(
            (string, Material)[] table, string dir)
        {
            if (table == null) return null;
            PackPaths.EnsureFolder(dir);
            var outp = new (string, Material)[table.Length];
            for (int i = 0; i < table.Length; i++)
            {
                var (token, mat) = table[i];
                outp[i] = (token, PackMaterialGenerator.Asset(mat, dir, token));
            }
            return outp;
        }
    }
}
