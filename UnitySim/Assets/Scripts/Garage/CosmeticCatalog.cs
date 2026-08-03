using System.Collections.Generic;
using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>Which mount a cosmetic occupies. One item per slot per car.</summary>
    public enum CosmeticSlot { Topper, Rim, Ornament, Bobble, Wing }

    /// <summary>Drop tiers, ordered — the crate weights index this.</summary>
    public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

    /// <summary>The four worlds the pack is themed to (a themed crate draws
    /// only from its own).</summary>
    public enum CosmeticTheme { Arcade, Toybox, Enchanted, Haunted }

    public sealed class CosmeticItem
    {
        public string id;              // save key + FBX stem — never rename
        public string label;
        public CosmeticSlot slot;
        public Rarity rarity;
        public CosmeticTheme theme;
        public string description;     // the pack's own one-liner
        public int dupeValue;          // scrap paid when the crate repeats it
        public int directCost;         // scrap to buy it outright
    }

    public sealed class CrateDef
    {
        public string id;              // save key + FBX stem
        public string label;
        public string description;
        public string earn;            // how the game hands it over
        public int pulls;
        public int pity;               // pulls since epic+ that force one
        public Rarity? floor;          // at least one pull must reach this
        public CosmeticTheme? theme;   // draws only from this theme when set
        public float[] weights;        // per-rarity relative weight, Common..Legendary
    }

    /// <summary>
    /// The TinyTorque cosmetics pack as game data: 47 unlockable decorations,
    /// 4 crates, and the 39 materials they are painted with.
    ///
    /// EVERY number in this file came out of <c>Blender/build_cosmetics.py</c>
    /// or <c>TinyTorque_cosmetics.json</c> — the authored base colour, metallic,
    /// roughness and emission of each material, and each item's slot, rarity,
    /// theme and prices. Nothing here is eyeballed, because the point of the
    /// import is that a crown looks like the crown that was modelled.
    ///
    /// Materials are built in code rather than imported, matching the rest of
    /// the project: the FBX carry shape only (PartModelPostprocessor sets
    /// materialImportMode = None) and every mesh object is named for the
    /// material it wants, so <see cref="PartMeshLibrary.AssignByName"/> binds
    /// them. That matcher is first-match-substring, so <see cref="Tokens"/> is
    /// sorted longest-first — otherwise "gold" would swallow "glow_gold".
    /// </summary>
    public static class CosmeticCatalog
    {
        public const string MeshRoot = "Cosmetics/";

        // ---- materials ------------------------------------------------------

        private struct MatDef
        {
            public string token;
            public Color baseColor;
            public float metallic, roughness, alpha;
            public Color glow;
            public float glowStrength;
        }

        private static MatDef D(string token, float r, float g, float b,
            float metallic, float roughness, Color glow = default,
            float glowStrength = 0f, float alpha = 1f)
            => new MatDef
            {
                token = token, baseColor = new Color(r, g, b, alpha),
                metallic = metallic, roughness = roughness, alpha = alpha,
                glow = glow, glowStrength = glowStrength,
            };

        /// <summary>
        /// The pack's 39 materials, verbatim from the Principled BSDF behind
        /// each <c>M_Cos_*</c>. Two of them (ghost, fae) drive alpha and glow
        /// off a Layer Weight node — a Fresnel falloff the Standard shader has
        /// no equivalent for — so they land on the midpoint of the authored
        /// face..edge range, which is the honest flat stand-in. Their ranges are
        /// kept in the comments so the choice can be revisited.
        /// </summary>
        private static readonly MatDef[] Defs =
        {
            D("blue", 0.045f, 0.13f, 0.48f, 0f, 0.24f),
            D("bone", 0.66f, 0.63f, 0.545f, 0f, 0.56f),
            D("brass", 0.74f, 0.52f, 0.23f, 1f, 0.29f),
            D("carbon", 0.036f, 0.038f, 0.044f, 0.55f, 0.3f),
            D("chrome", 0.912f, 0.918f, 0.93f, 1f, 0.055f),
            D("copper", 0.62f, 0.17f, 0.045f, 1f, 0.3f),
            D("cream", 0.78f, 0.76f, 0.7f, 0f, 0.24f),
            D("dark", 0.03f, 0.031f, 0.035f, 0f, 0.48f),
            D("ebony", 0.055f, 0.042f, 0.038f, 0f, 0.42f),
            D("fae", 0.1736f, 0.3096f, 0.3332f, 0f, 0.34f, glow: new Color(0.62f, 0.86f, 0.98f), glowStrength: 1.0875f, alpha: 0.34f),   // Fresnel: alpha 0.10..0.58, glow 0.68..1.50
            D("felt", 0.4f, 0.028f, 0.032f, 0f, 0.88f),
            D("fur", 0.23f, 0.128f, 0.058f, 0f, 0.9f),
            D("ghost", 0.168f, 0.36f, 0.2856f, 0f, 0.34f, glow: new Color(0.6f, 1f, 0.84f), glowStrength: 1.3775f, alpha: 0.53f),        // Fresnel: alpha 0.20..0.86, glow 0.85..1.90
            D("glow_ember", 0.3f, 0.132f, 0.03f, 0f, 0.4f, glow: new Color(1f, 0.44f, 0.1f), glowStrength: 2.8f),
            D("glow_flame", 0.3f, 0.126f, 0.03f, 0f, 0.4f, glow: new Color(1f, 0.42f, 0.1f), glowStrength: 5.2f),
            D("glow_gold", 0.3f, 0.24f, 0.102f, 0f, 0.4f, glow: new Color(1f, 0.8f, 0.34f), glowStrength: 3.8f),
            D("glow_green", 0.06f, 0.3f, 0.066f, 0f, 0.4f, glow: new Color(0.2f, 1f, 0.22f), glowStrength: 2.4f),
            D("glow_ice", 0.06f, 0.126f, 0.3f, 0f, 0.4f, glow: new Color(0.2f, 0.42f, 1f), glowStrength: 2.4f),
            D("glow_soft", 0.162f, 0.078f, 0.3f, 0f, 0.4f, glow: new Color(0.54f, 0.26f, 1f), glowStrength: 1.4f),
            D("glow_violet", 0.186f, 0.105f, 0.3f, 0f, 0.4f, glow: new Color(0.62f, 0.35f, 1f), glowStrength: 3.4f),
            D("glow_warm", 0.3f, 0.222f, 0.126f, 0f, 0.4f, glow: new Color(1f, 0.74f, 0.42f), glowStrength: 3f),
            D("gold", 0.944f, 0.706f, 0.286f, 1f, 0.17f),
            D("green", 0.07f, 0.33f, 0.12f, 0f, 0.24f),
            D("gun", 0.18f, 0.19f, 0.205f, 0.9f, 0.38f),
            D("iron", 0.075f, 0.072f, 0.07f, 0.85f, 0.62f),
            D("lens_blue", 0.048f, 0.102f, 0.3f, 0f, 0.4f, glow: new Color(0.16f, 0.34f, 1f), glowStrength: 4.2f),
            D("lens_red", 0.3f, 0.03f, 0.027f, 0f, 0.4f, glow: new Color(1f, 0.1f, 0.09f), glowStrength: 4f),
            D("orange", 0.78f, 0.22f, 0.02f, 0f, 0.24f),
            D("paper", 0.72f, 0.7f, 0.64f, 0f, 0.72f),
            D("pumpkin", 0.62f, 0.185f, 0.022f, 0f, 0.4f),
            D("purple", 0.18f, 0.055f, 0.3f, 0f, 0.24f),
            D("red", 0.52f, 0.03f, 0.038f, 0f, 0.24f),
            D("rust", 0.24f, 0.105f, 0.045f, 0.55f, 0.76f),
            D("stalk", 0.18f, 0.15f, 0.055f, 0f, 0.78f),
            D("steel", 0.86f, 0.872f, 0.89f, 1f, 0.26f),
            D("straw", 0.42f, 0.3f, 0.11f, 0f, 0.82f),
            D("white", 0.78f, 0.782f, 0.79f, 0f, 0.29f),
            D("wood", 0.3f, 0.18f, 0.082f, 0f, 0.52f),
            D("yellow", 0.86f, 0.56f, 0.04f, 0f, 0.24f),
        };

        private static readonly Dictionary<string, Material> _mats =
            new Dictionary<string, Material>();
        private static (string token, Material mat)[] _tokens;

        /// <summary>
        /// The token → material map for <see cref="PartMeshLibrary.AssignByName"/>,
        /// built once and sorted LONGEST TOKEN FIRST. Ordering is load-bearing:
        /// the matcher takes the first token contained in the object name, so
        /// "glow_gold" has to be tested before "gold" and "lens_red" before
        /// "red" or half the pack turns the wrong colour.
        /// </summary>
        public static (string token, Material mat)[] Tokens
        {
            get
            {
                if (_tokens == null || _tokens.Length != Defs.Length)
                {
                    var list = new List<(string, Material)>(Defs.Length);
                    foreach (var d in Defs) list.Add((d.token, Mat(d.token)));
                    list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
                    _tokens = list.ToArray();
                }
                return _tokens;
            }
        }

        /// <summary>One shared material per token, so a car wearing four rims
        /// still batches.</summary>
        public static Material Mat(string token)
        {
            if (_mats.TryGetValue(token, out var m) && m != null) return m;
            foreach (var d in Defs)
            {
                if (d.token != token) continue;
                m = TrackBuilder.StandardMat(d.baseColor);
                m.name = "Cos_" + token;
                m.SetFloat("_Metallic", d.metallic);
                // Unity's Standard shader takes smoothness; Blender authors
                // roughness. They are complements, not two names for one thing.
                m.SetFloat("_Glossiness", Mathf.Clamp01(1f - d.roughness));
                if (d.glowStrength > 0f)
                {
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                    m.SetColor("_EmissionColor", d.glow * d.glowStrength);
                }
                if (d.alpha < 1f) MakeFade(m);
                _mats[token] = m;
                return m;
            }
            // An unmapped token means the FBX and this table disagree; a loud
            // magenta beats a silent default-grey object nobody notices.
            m = TrackBuilder.StandardMat(Color.magenta);
            m.name = "Cos_MISSING_" + token;
            _mats[token] = m;
            Debug.LogWarning($"[CosmeticCatalog] no material for token '{token}'");
            return m;
        }

        /// <summary>Standard shader → Fade mode, the transparent setup the two
        /// spectre materials need. These are the render-state flags Unity's own
        /// StandardShaderGUI writes when you pick Fade in the inspector.</summary>
        private static void MakeFade(Material m)
        {
            m.SetFloat("_Mode", 2f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // ---- instantiation --------------------------------------------------

        /// <summary>
        /// Instantiate a cosmetic (or crate) mesh under <paramref name="parent"/>
        /// at the identity local pose — the authored frame IS the mount frame —
        /// and bind its materials. Returns null when the FBX is absent, and the
        /// caller then draws nothing: same never-black-screen rule the track
        /// props follow, so a missing asset costs a bare car, not a crash.
        /// </summary>
        public static GameObject Build(Transform parent, string meshKey,
            int layer = PartVisualFactory.VizLayer)
        {
            if (parent == null || string.IsNullOrEmpty(meshKey)) return null;
            var go = PartMeshLibrary.TryInstantiate(meshKey, parent, layer, MeshRoot);
            if (go == null) return null;
            PartMeshLibrary.AssignByName(go, Mat("dark"), Tokens);
            return go;
        }

        // ---- items ----------------------------------------------------------

        public static readonly CosmeticItem[] Seed =
        {
            // ---- topper ----
            new CosmeticItem { id = "top_lightbar", label = "Light Bar", slot = CosmeticSlot.Topper,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "marshal light bar, six alternating lenses" },
            new CosmeticItem { id = "top_spare", label = "Spare & Can", slot = CosmeticSlot.Topper,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "spare wheel and jerry can strapped to a tray" },
            new CosmeticItem { id = "top_blocks", label = "Alphabet Blocks", slot = CosmeticSlot.Topper,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "three stacked alphabet blocks" },
            new CosmeticItem { id = "top_gift", label = "Gift Box", slot = CosmeticSlot.Topper,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "wrapped present with a four-loop bow" },
            new CosmeticItem { id = "top_surfboard", label = "Roof Surfboard", slot = CosmeticSlot.Topper,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "board on two chrome roof racks" },
            new CosmeticItem { id = "top_cauldron", label = "Cauldron", slot = CosmeticSlot.Topper,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "three-legged cauldron with a boiling brew" },
            new CosmeticItem { id = "top_pizza", label = "Pizza Sign", slot = CosmeticSlot.Topper,
                rarity = Rarity.Rare, theme = CosmeticTheme.Arcade, dupeValue = 30, directCost = 300,
                description = "lit delivery sign, reads from both flanks" },
            new CosmeticItem { id = "top_pumpkin", label = "Jack-o'-Lantern", slot = CosmeticSlot.Topper,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "carved lantern with a lit face" },
            new CosmeticItem { id = "top_booster", label = "Rocket Booster", slot = CosmeticSlot.Topper,
                rarity = Rarity.Epic, theme = CosmeticTheme.Arcade, dupeValue = 80, directCost = 900,
                description = "strapped rocket booster with a lit bell" },
            new CosmeticItem { id = "top_wizardhat", label = "Wizard Hat", slot = CosmeticSlot.Topper,
                rarity = Rarity.Epic, theme = CosmeticTheme.Enchanted, dupeValue = 80, directCost = 900,
                description = "drooping star-strewn hat with a gold buckle" },
            new CosmeticItem { id = "top_candelabra", label = "Candelabra", slot = CosmeticSlot.Topper,
                rarity = Rarity.Legendary, theme = CosmeticTheme.Haunted, dupeValue = 200, directCost = 2400,
                description = "three skulls, three lit candles, one iron frame" },
            new CosmeticItem { id = "top_crown", label = "Gold Crown", slot = CosmeticSlot.Topper,
                rarity = Rarity.Legendary, theme = CosmeticTheme.Enchanted, dupeValue = 200, directCost = 2400,
                description = "eight-point gold crown set with jewels" },
            // ---- rim ----
            new CosmeticItem { id = "rim_deepdish", label = "Deep Dish", slot = CosmeticSlot.Rim,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "six-spoke deep dish with a ten-bolt lip" },
            new CosmeticItem { id = "rim_steelie", label = "Steelie", slot = CosmeticSlot.Rim,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "pressed steel wheel under a chrome cap" },
            new CosmeticItem { id = "rim_pinwheel", label = "Pinwheel", slot = CosmeticSlot.Rim,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "eight candy vanes in red, white and blue" },
            new CosmeticItem { id = "rim_turbine", label = "Turbine", slot = CosmeticSlot.Rim,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Arcade, dupeValue = 12, directCost = 110,
                description = "twelve dished turbine vanes and a spinner cone" },
            new CosmeticItem { id = "rim_cog", label = "Clockwork Cog", slot = CosmeticSlot.Rim,
                rarity = Rarity.Rare, theme = CosmeticTheme.Enchanted, dupeValue = 30, directCost = 300,
                description = "brass clockwork gear driving a small steel pinion" },
            new CosmeticItem { id = "rim_lace", label = "Gold Lace", slot = CosmeticSlot.Rim,
                rarity = Rarity.Rare, theme = CosmeticTheme.Enchanted, dupeValue = 30, directCost = 300,
                description = "gold filigree curls with green leaves" },
            new CosmeticItem { id = "rim_mesh", label = "Mesh", slot = CosmeticSlot.Rim,
                rarity = Rarity.Rare, theme = CosmeticTheme.Arcade, dupeValue = 30, directCost = 300,
                description = "twenty crossed struts in two handed sets" },
            new CosmeticItem { id = "rim_web", label = "Spider Web", slot = CosmeticSlot.Rim,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "sagging spider web with the spider for a centre cap" },
            new CosmeticItem { id = "rim_blade", label = "Tri-Blade", slot = CosmeticSlot.Rim,
                rarity = Rarity.Epic, theme = CosmeticTheme.Arcade, dupeValue = 80, directCost = 900,
                description = "three swept blades on a gold centre lock" },
            new CosmeticItem { id = "rim_bone", label = "Bone Spoke", slot = CosmeticSlot.Rim,
                rarity = Rarity.Epic, theme = CosmeticTheme.Haunted, dupeValue = 80, directCost = 900,
                description = "five femur spokes around a skull cap" },
            // ---- ornament ----
            new CosmeticItem { id = "orn_jet", label = "Chrome Jet", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "chrome swept dart" },
            new CosmeticItem { id = "orn_duck", label = "Rubber Duck", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "rubber duck sitting on the bonnet" },
            new CosmeticItem { id = "orn_flame", label = "Flames", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Arcade, dupeValue = 12, directCost = 110,
                description = "three chrome licks curling back" },
            new CosmeticItem { id = "orn_lightning", label = "Lightning Bolt", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Arcade, dupeValue = 12, directCost = 110,
                description = "chrome lightning bolt" },
            new CosmeticItem { id = "orn_skull", label = "Chrome Skull", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "chrome skull on a plinth" },
            new CosmeticItem { id = "orn_bat", label = "Bat", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Epic, theme = CosmeticTheme.Haunted, dupeValue = 80, directCost = 900,
                description = "bat with its wings up on a plinth" },
            new CosmeticItem { id = "orn_crystal", label = "Arcane Crystal", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Epic, theme = CosmeticTheme.Enchanted, dupeValue = 80, directCost = 900,
                description = "lit violet shard floating in a gold ring" },
            new CosmeticItem { id = "orn_phoenix", label = "Phoenix", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Legendary, theme = CosmeticTheme.Enchanted, dupeValue = 200, directCost = 2400,
                description = "gold phoenix with lit primaries" },
            new CosmeticItem { id = "orn_wrench", label = "Mascot Spanner", slot = CosmeticSlot.Ornament,
                rarity = Rarity.Legendary, theme = CosmeticTheme.Arcade, dupeValue = 200, directCost = 2400,
                description = "the mascot spanner rearing off the bonnet" },
            // ---- bobble ----
            new CosmeticItem { id = "bob_8ball", label = "Eight Ball", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "eight ball, disc facing forward" },
            new CosmeticItem { id = "bob_cone", label = "Traffic Cone", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "mini traffic cone" },
            new CosmeticItem { id = "bob_bear", label = "Teddy Head", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "teddy head in fabric" },
            new CosmeticItem { id = "bob_helmet", label = "Crash Helmet", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Arcade, dupeValue = 12, directCost = 110,
                description = "crash helmet with a smoked visor" },
            new CosmeticItem { id = "bob_mushroom", label = "Toadstool", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Enchanted, dupeValue = 12, directCost = 110,
                description = "spotted toadstool with lit gills" },
            new CosmeticItem { id = "bob_dice", label = "Fuzzy Dice", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Rare, theme = CosmeticTheme.Arcade, dupeValue = 30, directCost = 300,
                description = "a pair on a chrome fork, every face pipped" },
            new CosmeticItem { id = "bob_eyeball", label = "Eyeball", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "bloodshot eye with a green iris" },
            new CosmeticItem { id = "bob_ghost", label = "Ghost", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "translucent spectre with a wavy hem" },
            new CosmeticItem { id = "bob_star", label = "Wish Star", slot = CosmeticSlot.Bobble,
                rarity = Rarity.Epic, theme = CosmeticTheme.Enchanted, dupeValue = 80, directCost = 900,
                description = "lit five-point star in a gold halo" },
            // ---- wing ----
            new CosmeticItem { id = "wing_ducktail", label = "Ducktail", slot = CosmeticSlot.Wing,
                rarity = Rarity.Common, theme = CosmeticTheme.Arcade, dupeValue = 5, directCost = 40,
                description = "carbon lip that tapers with the boot lid" },
            new CosmeticItem { id = "wing_plank", label = "Scaffold Plank", slot = CosmeticSlot.Wing,
                rarity = Rarity.Common, theme = CosmeticTheme.Toybox, dupeValue = 5, directCost = 40,
                description = "scaffold board lashed to two brackets" },
            new CosmeticItem { id = "wing_kite", label = "Paper Kite", slot = CosmeticSlot.Wing,
                rarity = Rarity.Uncommon, theme = CosmeticTheme.Toybox, dupeValue = 12, directCost = 110,
                description = "paper kite on dowels with a ribbon tail" },
            new CosmeticItem { id = "wing_broom", label = "Witch's Broom", slot = CosmeticSlot.Wing,
                rarity = Rarity.Rare, theme = CosmeticTheme.Haunted, dupeValue = 30, directCost = 300,
                description = "besom lashed across the deck, head to one side" },
            new CosmeticItem { id = "wing_gt", label = "GT Wing", slot = CosmeticSlot.Wing,
                rarity = Rarity.Rare, theme = CosmeticTheme.Arcade, dupeValue = 30, directCost = 300,
                description = "dual-plane GT wing on swan necks" },
            new CosmeticItem { id = "wing_batwing", label = "Batwing", slot = CosmeticSlot.Wing,
                rarity = Rarity.Epic, theme = CosmeticTheme.Haunted, dupeValue = 80, directCost = 900,
                description = "scalloped membrane on claw spars" },
            new CosmeticItem { id = "wing_fae", label = "Fae Wings", slot = CosmeticSlot.Wing,
                rarity = Rarity.Legendary, theme = CosmeticTheme.Enchanted, dupeValue = 200, directCost = 2400,
                description = "four gossamer blades on a gold spine" },
        };

        private static CosmeticItem[] _all;

        /// <summary>
        /// The pack's 47 plus every cosmetic Asset Studio has committed.
        ///
        /// <b>The decision C1 owed, and this is the whole of it.</b> A cosmetic
        /// that imports, validates and previews but cannot be worn stops one step
        /// short of the thing the pipeline was built for, so the registry does
        /// grow a data-driven source. It stays small: the manifest authors slot,
        /// rarity, theme, label and blurb, and the two PRICES are derived from the
        /// rarity through <see cref="DupeValueFor"/> / <see cref="DirectCostFor"/>
        /// — a new hat must not be able to reprice the economy — with no cheat
        /// code, matching all 47 already here.
        ///
        /// Seed wins on a collision, so a committed asset cannot redefine an item
        /// a save already names. <c>UnlockCatalog</c> composes its pool from this,
        /// so a committed cosmetic reaches the crates and the shop with no further
        /// wiring; its id is its save key, exactly as the pack's are.
        /// </summary>
        public static CosmeticItem[] All => _all ??= ComposeItems();

        private static CosmeticItem[] ComposeItems()
        {
            var list = new List<CosmeticItem>(Seed);
            var taken = new HashSet<string>();
            foreach (CosmeticItem i in Seed) taken.Add(i.id);

            foreach (var m in Vehicles.AssetManifests.Discover(MeshRoot))
            {
                if (m == null || m.kind != Vehicles.AssetKinds.Cosmetic) continue;
                var c = m.cosmetic;
                if (c == null || string.IsNullOrEmpty(c.slot)) continue;
                if (!taken.Add(m.key))
                {
                    // A pack cosmetic whose mesh Asset Studio has replaced. Only
                    // this ROW is ignored — the manifest still binds the mesh's
                    // materials at instantiate time. Unlike a body or a wheel
                    // there is no measured field to hand back, because a
                    // CosmeticItem carries none: slot, rarity and theme are
                    // registry facts a replacement mesh has no say in.
                    Debug.Log($"[CosmeticCatalog] '{m.key}' is a pack cosmetic whose mesh has " +
                              "been replaced by Asset Studio. It keeps its slot, rarity and " +
                              "theme; the manifest still binds its materials.");
                    continue;
                }
                if (!System.Enum.TryParse(c.slot, out CosmeticSlot slot)
                    || !System.Enum.TryParse(c.rarity, out Rarity rarity)
                    || !System.Enum.TryParse(c.theme, out CosmeticTheme theme))
                {
                    Debug.LogWarning($"[CosmeticCatalog] '{m.key}' names slot \"{c.slot}\", rarity " +
                                     $"\"{c.rarity}\", theme \"{c.theme}\" — one of those is not a " +
                                     "value this build has. It will not be offered.");
                    continue;
                }
                list.Add(new CosmeticItem
                {
                    id = m.key, label = m.Label, slot = slot, rarity = rarity, theme = theme,
                    description = c.description ?? "",
                    dupeValue = DupeValueFor(rarity), directCost = DirectCostFor(rarity),
                });
            }
            return list.ToArray();
        }

        /// <summary>Forget the composed item list and the lookup built from it.
        /// The commit pipeline calls this; <c>UnlockCatalog.ResetCache</c> has to
        /// go with it, since its pool is built from this one.</summary>
        public static void ResetItems()
        {
            _all = null;
            _byId = null;
        }

        // ---- crates ---------------------------------------------------------

        /// <summary>
        /// The four boxes, with the pack's own weights, floors and pity counts.
        /// The manifest also ships a per-item <c>odds</c> table; it is not
        /// duplicated here because it is DERIVED — every entry is exactly
        /// <c>weight[rarity] / |pool[rarity]|</c>. CrateSystem rolls a rarity by
        /// weight and then picks uniformly inside it, which reproduces that
        /// table and keeps holding as the pool grows.
        ///
        /// <c>earn</c> is the game's trigger, not the manifest's wording: the
        /// Gold Vault's "championship event" and the Cursed Casket's "seasonal"
        /// had no home until this pass built one, and now they do.
        /// </summary>
        public static readonly CrateDef[] Crates =
        {
            new CrateDef { id = "crate", label = "Scrap Crate", pulls = 1, pity = 40,
                floor = null, theme = null,
                weights = new[] { 58f, 27f, 11f, 3.5f, 0.5f },
                earn = "finish any race",
                description = "banded pallet crate with a stencilled lid" },
            new CrateDef { id = "chrome", label = "Chrome Case", pulls = 2, pity = 25,
                floor = Rarity.Uncommon, theme = null,
                weights = new[] { 30f, 34f, 24f, 10f, 2f },
                earn = "finish on the podium",
                description = "polished flight case with a lit seam" },
            new CrateDef { id = "vault", label = "Gold Vault", pulls = 3, pity = 12,
                floor = Rarity.Rare, theme = null,
                weights = new[] { 0f, 30f, 42f, 22f, 6f },
                earn = "win a championship",
                description = "gold strongbox with a wheel-lock door" },
            new CrateDef { id = "haunt", label = "Cursed Casket", pulls = 2, pity = 8,
                floor = Rarity.Rare, theme = CosmeticTheme.Haunted,
                weights = new[] { 0f, 0f, 52f, 38f, 10f },
                earn = "win the Midnight Series",
                description = "hexagonal coffin, lid tipped open on a violet glow" },
        };

        // ---- lookups --------------------------------------------------------

        private static Dictionary<string, CosmeticItem> _byId;
        private static Dictionary<string, CrateDef> _crateById;

        public static CosmeticItem ById(string id)
        {
            if (_byId == null)
            {
                _byId = new Dictionary<string, CosmeticItem>(All.Length);
                foreach (var i in All) _byId[i.id] = i;
            }
            return id != null && _byId.TryGetValue(id, out var item) ? item : null;
        }

        public static CrateDef CrateById(string id)
        {
            if (_crateById == null)
            {
                _crateById = new Dictionary<string, CrateDef>(Crates.Length);
                foreach (var def in Crates) _crateById[def.id] = def;
            }
            return id != null && _crateById.TryGetValue(id, out var crate) ? crate : null;
        }

        /// <summary>Every item in a slot, catalog order (rarity ascending).</summary>
        public static List<CosmeticItem> InSlot(CosmeticSlot slot)
        {
            var list = new List<CosmeticItem>();
            foreach (var i in All) if (i.slot == slot) list.Add(i);
            return list;
        }

        public static string SlotLabel(CosmeticSlot s) => s switch
        {
            CosmeticSlot.Topper => "Topper",
            CosmeticSlot.Rim => "Rims",
            CosmeticSlot.Ornament => "Ornament",
            CosmeticSlot.Bobble => "Bobble",
            _ => "Wing",
        };

        public static string RarityLabel(Rarity r) => r switch
        {
            Rarity.Common => "Common",
            Rarity.Uncommon => "Uncommon",
            Rarity.Rare => "Rare",
            Rarity.Epic => "Epic",
            _ => "Legendary",
        };

        /// <summary>Scrap a duplicate of this tier pays. The manifest's
        /// per-item dupe_value never deviates from its tier, so this is the
        /// same table — and it prices the legacy unlocks, which have no entry
        /// of their own.</summary>
        public static int DupeValueFor(Rarity r) => r switch
        {
            Rarity.Common => 5,
            Rarity.Uncommon => 12,
            Rarity.Rare => 30,
            Rarity.Epic => 80,
            _ => 200,
        };

        /// <summary>Scrap to buy this tier outright — the manifest's escape
        /// hatch from box luck.</summary>
        public static int DirectCostFor(Rarity r) => r switch
        {
            Rarity.Common => 40,
            Rarity.Uncommon => 110,
            Rarity.Rare => 300,
            Rarity.Epic => 900,
            _ => 2400,
        };

        /// <summary>Tier colours for the palette and the crate reveal.</summary>
        public static Color RarityColor(Rarity r) => r switch
        {
            Rarity.Common => new Color(0.72f, 0.74f, 0.78f),
            Rarity.Uncommon => new Color(0.40f, 0.82f, 0.45f),
            Rarity.Rare => new Color(0.32f, 0.60f, 0.98f),
            Rarity.Epic => new Color(0.70f, 0.40f, 0.95f),
            _ => new Color(0.98f, 0.76f, 0.25f),
        };
    }
}
