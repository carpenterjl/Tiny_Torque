using System.Collections.Generic;
using System.IO;
using AIHWSim.Garage;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Step 2 of the pack pipeline: turn the game's runtime materials into real
    /// <c>.mat</c> assets so pack prefabs look right in the Scene view.
    ///
    /// The rule this file exists to enforce: <b>no PBR number is ever retyped</b>.
    /// Every material here is a CLONE of the material the game builds at runtime,
    /// taken from the same tables — <see cref="PartVisualFactory.AccentTokens"/>,
    /// <see cref="PartVisualFactory.WheelTokens"/>, <see cref="CosmeticCatalog.Tokens"/>
    /// and, for props, whatever actually lands on the renderers. So the pack cannot
    /// drift from the game by a decimal point, and re-running this menu item
    /// re-syncs it if the game's tables change. The only table with no runtime
    /// source is the arena kit, which is pack-only; see <see cref="PackSocData"/>.
    ///
    /// Props are harvested rather than read off a table on purpose. Themed props
    /// expose their tokens through <c>TrackCatalog.TokensFor(theme)</c>, but the 28
    /// props of the four procedural themes (<c>tw_</c>, <c>ng_</c>, <c>bb_</c>,
    /// <c>vf_</c>) carry their token arrays INLINE in each <c>ItemDef.build</c>
    /// lambda, and <c>TokensFor</c> returns null for those themes. Building each
    /// item through <see cref="TrackFactory.BuildItemVisual"/> and reading the
    /// renderers covers both shapes with one code path — and it is the same code
    /// path the game runs, which is the strongest guarantee available that the
    /// pack shows what the game shows.
    ///
    /// Idempotent: an existing <c>.mat</c> is updated in place rather than
    /// recreated, so prefabs that already reference it keep their references.
    /// </summary>
    public static class PackMaterialGenerator
    {
        [MenuItem("Tools/TinyTorque Assets/2. Generate materials", priority = 101)]
        public static void Generate()
        {
            PackPaths.EnsureLayout();
            int n = 0;

            try
            {
                AssetDatabase.StartAssetEditing();

                // ---- vehicle parts ------------------------------------------
                string vehDir = PackPaths.MaterialDirFor(PackKind.VehiclePart, "");
                PackPaths.EnsureFolder(vehDir);
                foreach (var (token, mat) in PartVisualFactory.AccentTokens)
                    if (Asset(mat, vehDir, token) != null) n++;
                foreach (var (token, mat) in PartVisualFactory.WheelTokens)
                    if (Asset(mat, vehDir, token) != null) n++;

                // ---- cosmetics ----------------------------------------------
                string cosDir = PackPaths.MaterialDirFor(PackKind.Cosmetic, "");
                PackPaths.EnsureFolder(cosDir);
                foreach (var (token, mat) in CosmeticCatalog.Tokens)
                    if (Asset(mat, cosDir, token) != null) n++;

                // ---- arena kit (pack-only palette, three themes) -------------
                foreach (string theme in PackSocData.Themes)
                {
                    string dir = PackPaths.MaterialsRoot + "/Arena/" + theme;
                    PackPaths.EnsureFolder(dir);
                    foreach (var (token, mat) in PackSocData.Tokens(theme))
                        if (Asset(mat, dir, token) != null) n++;
                }

                // ---- track props (harvested from the real build path) --------
                // Only ids the pack actually owns a mesh for. The catalog also
                // holds ~30 wholly procedural items (cone, wall_tall, ramp,
                // checkpoint …) that have no FBX and so no pack entry; emitting
                // their materials would file them under a "Misc" category that
                // exists for nothing.
                var packKeys = new HashSet<string>();
                foreach (var e in PackPaths.All())
                    if (e.kind == PackKind.Prop) packKeys.Add(e.key);

                foreach (var kv in PropMaterialMap())
                {
                    if (!packKeys.Contains(kv.Key)) continue;
                    string dir = PackPaths.MaterialDirFor(
                        PackKind.Prop, PackPaths.PropCategory(kv.Key));
                    PackPaths.EnsureFolder(dir);
                    foreach (var rm in kv.Value)
                        if (Asset(rm.Value, dir, kv.Key + "_" + rm.Key) != null) n++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            PackPaths.Log($"MATERIALS {n} bindings resolved, {_assets.Count} .mat assets");
        }

        // -------------------------------------------------------------------
        // material assets
        // -------------------------------------------------------------------

        /// <summary>Runtime material instance → the .mat asset that mirrors it.
        /// Keyed by instance so two tokens sharing one material (which happens a
        /// lot — one grey serves a dozen props) share one asset.</summary>
        private static readonly Dictionary<Material, Material> _assets =
            new Dictionary<Material, Material>();

        /// <summary>Get or create the <c>.mat</c> asset mirroring <paramref name="src"/>.
        /// Returns null for a null source rather than throwing, because a token
        /// table with a null entry should be reported by the validator, not crash
        /// generation halfway through.</summary>
        public static Material Asset(Material src, string dir, string fallbackName)
        {
            if (src == null) return null;
            if (_assets.TryGetValue(src, out var cached) && cached != null) return cached;

            // The runtime names are already unique and meaningful where the game
            // bothered to set one ("Prop_city_brick", "Cos_chrome"). Where it did
            // not, the name is just the shader's, so fall back to the token.
            string name = src.name;
            if (string.IsNullOrEmpty(name) || name == "Standard" ||
                name.StartsWith("Standard ", System.StringComparison.Ordinal))
                name = fallbackName;
            name = Sanitize(name);

            string path = dir + "/" + name + ".mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                // Update in place: prefabs already reference this GUID.
                existing.shader = src.shader;
                existing.CopyPropertiesFromMaterial(src);
                existing.renderQueue = src.renderQueue;
                existing.globalIlluminationFlags = src.globalIlluminationFlags;
                Retarget(existing);
                EditorUtility.SetDirty(existing);
                _assets[src] = existing;
                return existing;
            }

            var copy = new Material(src) { name = name };
            copy.renderQueue = src.renderQueue;
            Retarget(copy);
            AssetDatabase.CreateAsset(copy, path);
            _assets[src] = copy;
            return copy;
        }

        /// <summary>Point a cloned material's textures at the pack's own copies.
        ///
        /// The baked liveries (<c>body_coupe_paint</c> and friends) load through
        /// <c>Resources.Load</c> at runtime, so a straight clone would leave the
        /// pack's materials depending on assets in <c>Resources/</c> — which is
        /// exactly the coupling the copy-into-the-pack decision was meant to
        /// avoid. Procedurally generated textures have no asset at all and are
        /// left alone: they are baked into the material instance and serialize
        /// with it.</summary>
        private static void Retarget(Material m)
        {
            var tex = m.mainTexture as Texture2D;
            if (tex == null) return;
            string srcPath = AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(srcPath)) return;          // generated at runtime
            if (!srcPath.Replace('\\', '/').Contains("/Resources/")) return;

            string file = Path.GetFileNameWithoutExtension(srcPath);
            foreach (var e in PackPaths.All())
            {
                if (e.kind != PackKind.VehiclePart) continue;
                string candidate = Path.GetDirectoryName(e.modelPath).Replace('\\', '/')
                                   + "/" + file + ".png";
                var packTex = AssetDatabase.LoadAssetAtPath<Texture2D>(candidate);
                if (packTex != null) { m.mainTexture = packTex; return; }
            }
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Replace(' ', '_').Trim();
        }

        // -------------------------------------------------------------------
        // prop harvest
        // -------------------------------------------------------------------

        private static Dictionary<string, Dictionary<string, Material>> _propMap;

        /// <summary>
        /// item id → (renderer object name → the material the game binds to it).
        ///
        /// Built by running every catalog item through the real build path once.
        /// The renderer names come from the FBX, so they match the pack's copy of
        /// the same mesh piece-for-piece — which is what lets the prefab generator
        /// shade a pack mesh with the game's own answer.
        /// </summary>
        public static Dictionary<string, Dictionary<string, Material>> PropMaterialMap()
        {
            if (_propMap != null) return _propMap;

            _propMap = new Dictionary<string, Dictionary<string, Material>>();
            var root = new GameObject("~PackHarvest");
            try
            {
                foreach (var def in TrackCatalog.Items)
                {
                    if (def == null || def.build == null) continue;
                    GameObject go = null;
                    try
                    {
                        go = TrackFactory.BuildItemVisual(def, root.transform);
                        var byName = new Dictionary<string, Material>();
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (r.sharedMaterial == null) continue;
                            // Strip the "(Clone)" Object.Instantiate appends. It
                            // only ever lands on the FBX ROOT, and the root is
                            // precisely where a SINGLE-material prop keeps its
                            // mesh — so leaving it on silently unbinds every
                            // one-material prop in the kit (city_bush, toy_brick,
                            // haunt_tree, the ornaments, the legacy body shells).
                            string name = Strip(r.gameObject.name);
                            // First writer wins: a prop can repeat a piece name
                            // across submeshes, and they carry the same material.
                            if (!byName.ContainsKey(name)) byName[name] = r.sharedMaterial;
                        }
                        if (byName.Count > 0) _propMap[def.id] = byName;
                    }
                    catch (System.Exception e)
                    {
                        PackPaths.Warn($"harvest failed for '{def.id}': {e.Message}");
                    }
                    finally
                    {
                        if (go != null) Object.DestroyImmediate(go);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            return _propMap;
        }

        // -------------------------------------------------------------------
        // pack-owned defaults
        // -------------------------------------------------------------------

        /// <summary>
        /// The two materials the pack has to invent, because the game does not
        /// hold them in any table:
        ///
        ///   • <b>Neutral</b> — for pieces no token claims. Some are legitimately
        ///     unclaimable: the arcade item box, banana, missile and shield orb
        ///     have no <c>ItemDef</c> at all (the arcade director spawns them), so
        ///     there is nothing to harvest. Better an explicit, obviously-neutral
        ///     grey than Unity's default-material pink-adjacent white.
        ///   • <b>BodyPaint</b> — the <c>paint_*</c> pieces on the tintable body
        ///     shells. In game those take the player's chosen <c>bodyColor</c>, so
        ///     there IS no static material to clone; the pack shows the design
        ///     default (VehicleDesign.bodyColor) so a body reads as a car rather
        ///     than as untextured geometry.
        /// </summary>
        public static Material Neutral => Default(ref _neutral, "TTA_Neutral",
            new Color(0.55f, 0.56f, 0.58f), 0.35f);

        public static Material BodyPaint => Default(ref _bodyPaint, "TTA_BodyPaint",
            new Color(0.20f, 0.55f, 0.95f), 0.75f);

        private static Material _neutral, _bodyPaint;

        private static Material Default(ref Material slot, string name, Color c, float smooth)
        {
            if (slot != null) return slot;
            string dir = PackPaths.MaterialsRoot;
            PackPaths.EnsureFolder(dir);
            string path = dir + "/" + name + ".mat";

            slot = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (slot == null)
            {
                slot = AIHWSim.Track.TrackBuilder.StandardMat(c);
                slot.name = name;
                slot.SetFloat("_Glossiness", smooth);
                slot.SetFloat("_Metallic", 0f);
                AssetDatabase.CreateAsset(slot, path);
            }
            return slot;
        }

        private static string Strip(string name)
        {
            const string clone = "(Clone)";
            return name.EndsWith(clone, System.StringComparison.Ordinal)
                ? name.Substring(0, name.Length - clone.Length).TrimEnd()
                : name;
        }

        private static Dictionary<string, Dictionary<string, Material>> _partMap;

        /// <summary>
        /// The same harvest, for the small vehicle parts.
        ///
        /// Bodies and wheels are covered by <c>AccentTokens</c>/<c>WheelTokens</c>,
        /// but the battery and the four antenna styles carry their token arrays
        /// INLINE at their call sites in <c>PartVisualFactory</c> (whip→Rubber,
        /// base/sma→Can, wrap/cell→Lipo, term/nub→Stud, lead→Rim) against private
        /// materials no table exposes. Running the public builders and reading the
        /// result gets those without reaching into the class or retyping a colour.
        /// </summary>
        public static Dictionary<string, Dictionary<string, Material>> PartMaterialMap()
        {
            if (_partMap != null) return _partMap;

            _partMap = new Dictionary<string, Dictionary<string, Material>>();
            var root = new GameObject("~PackPartHarvest");
            try
            {
                Harvest(root, "battery_stick", p => PartVisualFactory.BuildBatteryViz(p));
                Harvest(root, "antenna_stub", p => PartVisualFactory.BuildAntennaViz(p, 0f, 1f, 0));
                Harvest(root, "antenna_whip", p => PartVisualFactory.BuildAntennaViz(p, 0f, 1f, 1));
                Harvest(root, "antenna_flag", p => PartVisualFactory.BuildAntennaViz(p, 0f, 1f, 2));
                Harvest(root, "antenna_twin", p => PartVisualFactory.BuildAntennaViz(p, 0f, 1f, 3));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            return _partMap;
        }

        private static void Harvest(GameObject root, string key, System.Action<Transform> build)
        {
            var holder = new GameObject(key);
            holder.transform.SetParent(root.transform, false);
            try
            {
                build(holder.transform);
                var byName = new Dictionary<string, Material>();
                foreach (var r in holder.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.sharedMaterial == null) continue;
                    string n = Strip(r.gameObject.name);
                    if (!byName.ContainsKey(n)) byName[n] = r.sharedMaterial;
                }
                if (byName.Count > 0) _partMap[key] = byName;
            }
            catch (System.Exception e)
            {
                PackPaths.Warn($"part harvest failed for '{key}': {e.Message}");
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>Drop the caches so a regeneration in the same editor session
        /// re-reads the game's tables instead of the previous run's answer.</summary>
        public static void ResetCaches()
        {
            _assets.Clear();
            _propMap = null;
            _partMap = null;
            _neutral = null;
            _bodyPaint = null;
        }
    }
}
