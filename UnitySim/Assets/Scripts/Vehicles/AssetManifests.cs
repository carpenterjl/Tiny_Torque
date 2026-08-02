using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One material as the asset's author means it, in the game's own terms.
    ///
    /// <b>Smoothness, not roughness.</b> Blender exports roughness; the conversion
    /// happens once, in Asset Studio, so nothing at runtime has a per-source
    /// convention left to get wrong.
    ///
    /// <b><see cref="baked"/> is the null-discriminator</b> and it survives all the
    /// way down here for the same reason it exists in the export: a baked material
    /// has no flat colour — the texture carries it — and <c>JsonUtility</c> cannot
    /// tell an absent float from a zero one. Never read <see cref="rgb"/>,
    /// <see cref="metallic"/> or <see cref="smoothness"/> as meaningful without
    /// checking this first.
    /// </summary>
    [Serializable]
    public sealed class AssetMaterialDef
    {
        /// <summary>The Blender material name. This is the join key: an object's
        /// <see cref="AssetObjectDef.slots"/> hold these names.</summary>
        public string name = "";

        public bool baked;

        /// <summary>
        /// This material is the car's paint channel — the design's body colour
        /// MULTIPLIES it rather than replacing it.
        ///
        /// It is what makes a baked livery tintable: white leaves the artwork
        /// alone and any other colour themes it. It is also why a paint-channel
        /// material must not come from <see cref="AssetManifest.Shared"/> — the
        /// tint is per car, so the binder builds these per instance and shares
        /// only the accents. Applies to manifest assets only; the four shipped
        /// baked cars are bound by the legacy token tables and
        /// <c>CarVehicle.HasPaintableBody</c> already answers no for them by name.
        /// </summary>
        public bool paintChannel;

        /// <summary>Linear RGB, length 3. Empty on a baked material.</summary>
        public float[] rgb;

        public float metallic;

        /// <summary>1 − Blender's roughness, folded in by the tool.</summary>
        public float smoothness = 0.5f;

        public float alpha = 1f;

        /// <summary>Linear RGB, length 3, multiplied by
        /// <see cref="emissionStrength"/> into Unity's HDR _EmissionColor — so a
        /// 14× lamp stays a 14× lamp and blooms, exactly as the Tiguan's does.</summary>
        public float[] emission;
        public float emissionStrength;

        // Resources paths WITHOUT the extension — "PartModels/body_police/
        // M_Police_Paint_BaseColor" — because that is what Resources.Load takes.
        // Asset Studio rewrites the export's bare file names into these when it
        // copies the PNGs in; a manifest never carries a path off this machine.
        public string mapAlbedo = "";
        public string mapMetallicSmoothness = "";
        public string mapEmission = "";
        public string mapNormal = "";

        public bool HasMaps => !string.IsNullOrEmpty(mapAlbedo)
                            || !string.IsNullOrEmpty(mapMetallicSmoothness)
                            || !string.IsNullOrEmpty(mapEmission)
                            || !string.IsNullOrEmpty(mapNormal);

        public Color Rgb => AssetManifests.ToColor(rgb, Color.grey);
        public Color Emission => AssetManifests.ToColor(emission, Color.black);
    }

    /// <summary>
    /// One child mesh: what it is bound to, and what it is FOR.
    ///
    /// <b>Object names, never mesh names.</b> Unity names an imported GameObject
    /// after the Blender OBJECT; the mesh datablock inside it is called something
    /// else entirely (<c>Police_Body</c> vs <c>Police_Body_baked_baked_baked</c>),
    /// so anything matching on <c>sharedMesh.name</c> misses half the hierarchy.
    ///
    /// The damage fields are authored and consumed by nothing. Deliberate: they are
    /// cheap to collect while an author is already looking at the hierarchy saying
    /// "that is the left door and it comes off", and expensive to re-derive later
    /// from forty-one anonymous meshes.
    /// </summary>
    [Serializable]
    public sealed class AssetObjectDef
    {
        public string name = "";

        /// <summary>Material name per submesh slot; the INDEX is the slot. An empty
        /// entry means "leave that slot as imported" rather than "bind null".</summary>
        public string[] slots;

        public string role = "Structural";

        /// <summary>Hit points before this piece counts as destroyed. 0 = not
        /// separately destructible.</summary>
        public float healthHp;

        /// <summary>Pieces that fail together. Empty means "on its own".</summary>
        public string group = "";

        public int SlotCount => slots?.Length ?? 0;

        public string SlotAt(int i) =>
            slots != null && i >= 0 && i < slots.Length ? slots[i] : null;
    }

    /// <summary>Where the asset came from. Read by the tool and the validator to
    /// answer "is this stale", never by the game.</summary>
    [Serializable]
    public sealed class AssetSourceDef
    {
        public string assetName = "";
        public string sourceBlend = "";
        public string exportedAtUtc = "";
        public string fbxMd5 = "";
    }

    /// <summary>
    /// The geometry contract, in the shape <c>PartModelValidator</c> already states
    /// its 207 literal rows in: an axis is either pinned or free.
    ///
    /// <b>−1 means unpinned</b>, and that is not a placeholder for null. The bodies
    /// were exported by scaling to length 0.420 with ONE uniform factor, so their
    /// real widths land between 0.17 and 0.20 — pinning a width would fail every
    /// car for being correctly proportioned. A hand-written <c>null</c> is accepted
    /// and read as −1 (see <c>AssetManifests.SanitiseNulls</c>); nothing else is.
    /// </summary>
    [Serializable]
    public sealed class AssetSpecDef
    {
        public float x = -1f;
        public float y = -1f;
        public float z = -1f;
        public int maxTris;

        /// <summary>"exporter", "measured", "authored" — who decided these numbers.
        /// A sentence in the validator's failure line, not a switch.</summary>
        public string specSource = "";

        public bool Pinned(int axis) => Axis(axis) >= 0f;
        public float Axis(int axis) => axis == 0 ? x : axis == 1 ? y : z;
    }

    /// <summary>What the exporter said, carried forward so it stays visible after
    /// the export folder is gone.</summary>
    [Serializable]
    public sealed class AssetNotesDef
    {
        /// <summary>Repairs the exporter ALREADY APPLIED — inward faces flipped and
        /// the like. Not a to-do list; a record that geometry left Blender
        /// differently from how it was modelled.</summary>
        public int geometryFixes;
        public int textureWarnings;

        /// <summary>Committed past a failed exporter verification. The reason is
        /// mandatory and printed by every <c>[AST]</c> run, so an override stays
        /// loud for as long as it lasts.</summary>
        public bool verificationOverridden;
        public string overrideReason = "";
    }

    /// <summary>
    /// The manifest that rides beside an asset's FBX — everything the game needs to
    /// know about art it did not have a switch statement for.
    ///
    /// <b>Every string that could have been an enum is a string</b>
    /// (<see cref="kind"/>, <see cref="materialMode"/>,
    /// <see cref="AssetObjectDef.role"/>). <c>JsonUtility</c> writes an enum as its
    /// ordinal, so inserting a value in the middle silently reinterprets every asset
    /// already authored, and the diff shows a number nobody can read.
    /// </summary>
    [Serializable]
    public sealed class AssetManifest
    {
        public int schema = 1;
        public string key = "";
        public string kind = "";

        /// <summary>"Manifest" (materials built from the numbers below) or
        /// "Verbatim" (the FBX keeps its own <c>.mat</c> assets and this loader
        /// builds nothing). R4 ships the second; until then a Verbatim manifest
        /// parses, reports itself, and binds nothing.</summary>
        public string materialMode = AssetMaterialModes.Manifest;

        public AssetSourceDef source;

        /// <summary>The asset's real measured extents in the game's axes, AFTER
        /// <see cref="authorYawDeg"/> — i.e. the divisor a design's bodySize is a
        /// ratio against, the per-asset answer to
        /// <c>CarVehicle.BodyMeshAuthorSize</c>'s single nominal one.</summary>
        public float[] authorSize;

        /// <summary>The quarter turn that puts the long axis on +Z. A multiple of
        /// 90: a bounding box does not survive anything else, and the game builds
        /// every car facing +Z — sensors, lights and gear are all placed that way.</summary>
        public float authorYawDeg;

        public AssetSpecDef spec;
        public AssetMaterialDef[] materials;
        public AssetObjectDef[] objects;
        public AssetNotesDef notes;

        /// <summary>Where this was loaded from — a Resources path
        /// ("PartModels/body_police_asset") at runtime, an Assets/ path when the
        /// model postprocessor read it off disk during import. Where the file IS,
        /// which is not something the file can say.</summary>
        [NonSerialized] public string resourcePath;

        // Built lazily, shared by every instance of this asset, and dropped
        // wholesale if Unity destroyed them on a play-mode exit — the same lazy
        // slot pattern PartVisualFactory and TiguanMaterials both use.
        [NonSerialized] private Dictionary<string, Material> _shared;

        public bool IsVerbatim => materialMode == AssetMaterialModes.Verbatim;

        public Vector3 AuthorSize => AssetManifests.ToVector(authorSize);

        public int MaterialCount => materials?.Length ?? 0;
        public int ObjectCount => objects?.Length ?? 0;

        public AssetMaterialDef MaterialDef(string materialName)
        {
            if (materials == null || string.IsNullOrEmpty(materialName)) return null;
            foreach (AssetMaterialDef m in materials)
                if (m != null && m.name == materialName) return m;
            return null;
        }

        public AssetObjectDef ObjectDef(string objectName)
        {
            if (objects == null || string.IsNullOrEmpty(objectName)) return null;
            foreach (AssetObjectDef o in objects)
                if (o != null && o.name == objectName) return o;
            return null;
        }

        /// <summary>
        /// The UNTINTED material for <paramref name="materialName"/>, shared by
        /// every instance of this asset.
        ///
        /// <b>Not for the paint channel.</b> A paint-channel material's colour is
        /// the design's, so it differs per car and sharing one would make two cars
        /// the same colour the moment the second was built. The binder asks
        /// <see cref="AssetMaterialDef.paintChannel"/> and builds those itself —
        /// exactly the split <c>PartVisualFactory</c> already makes between shared
        /// accents and the car's own <c>_bodyMat</c>.
        /// </summary>
        public Material Shared(string materialName)
        {
            AssetMaterialDef d = MaterialDef(materialName);
            if (d == null) return null;

            // A destroyed material tests true against null through Unity's
            // overload, so a cache that survived a play-mode exit rebuilds rather
            // than handing back a fake-null reference.
            if (_shared != null && _shared.TryGetValue(materialName, out Material m) && m != null)
                return m;

            _shared ??= new Dictionary<string, Material>();
            Material built = AssetManifests.BuildMaterial(d, Color.white);
            _shared[materialName] = built;
            return built;
        }

        /// <summary>Drop the shared materials — for an editor tool that has just
        /// re-imported textures and wants the next build to read them.</summary>
        public void ResetMaterials() => _shared = null;
    }

    /// <summary>How an asset's materials reach the game. Strings, for the reason
    /// <see cref="AssetManifest"/> gives.</summary>
    public static class AssetMaterialModes
    {
        public const string Manifest = "Manifest";
        public const string Verbatim = "Verbatim";
    }

    /// <summary>
    /// Loads the per-asset manifests that ride beside the FBX under
    /// <c>Resources/PartModels/</c>, <c>Resources/TrackProps/</c> and
    /// <c>Resources/Cosmetics/</c>, and turns their numbers into Built-in-RP
    /// Standard materials.
    ///
    /// This is the runtime half of Asset Studio: the path by which art authored in
    /// Blender reaches the game with its own materials and texture maps, instead of
    /// arriving correctly shaped in one flat colour because
    /// <c>PartVisualFactory</c>'s hand-ordered token table had never heard of it.
    ///
    /// <b>Nothing in the game calls this yet.</b> R3 moves the binding call sites
    /// onto it; until then the only consumers are the tool and its probe, and every
    /// existing asset renders through exactly the code it did before — which is the
    /// claim the binding dump's empty diff is there to make.
    ///
    /// <b>A missing manifest is the normal case and says nothing.</b>
    /// <c>PartMeshLibrary</c> warns on a missing MESH because a key with no mesh is
    /// always a mistake; 207 of the 207 shipped assets have no manifest and are
    /// perfectly correct, so a warning here would be 207 lines of noise per session.
    /// A manifest that exists and does not PARSE is the opposite and shouts.
    /// </summary>
    public static class AssetManifests
    {
        /// <summary>Appended to the asset key: <c>body_police</c> →
        /// <c>body_police_asset.json</c>, sitting beside <c>body_police.fbx</c>.
        /// Sibling rather than a Manifests/ subfolder because the model
        /// postprocessor has to find it by path arithmetic DURING import, where it
        /// cannot call <c>Resources.Load</c>.</summary>
        public const string Suffix = "_asset";

        // Parsed manifests, keyed root+key. Absences are cached as null: a miss is
        // the common answer and re-probing Resources for it on every car build
        // would be the expensive way to keep saying "no".
        private static readonly Dictionary<string, AssetManifest> _cache =
            new Dictionary<string, AssetManifest>();

        /// <summary>True when <paramref name="key"/> ships a manifest.</summary>
        public static bool Has(string key, string root = PartMeshLibrary.PartRoot) =>
            Load(key, root) != null;

        /// <summary>
        /// The manifest for <paramref name="key"/>, or null when the asset has
        /// none (the normal case) or its manifest did not parse (warned once).
        /// </summary>
        public static AssetManifest Load(string key, string root = PartMeshLibrary.PartRoot)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string path = root + key + Suffix;
            if (_cache.TryGetValue(path, out AssetManifest cached)) return cached;

            AssetManifest m = Parse(path);
            _cache[path] = m;
            return m;
        }

        private static AssetManifest Parse(string resourcePath)
        {
            var ta = Resources.Load<TextAsset>(resourcePath);
            if (ta == null) return null;   // no manifest: silent, and normal
            return FromJson(ta.text, resourcePath);
        }

        /// <summary>
        /// Parse manifest text that has already been fetched, or null (warned) if
        /// it does not parse. <paramref name="label"/> only names the source in
        /// that warning.
        ///
        /// Separate from <see cref="Load"/> because the model postprocessor has to
        /// read the sibling manifest DURING import, where <c>Resources.Load</c>
        /// does not exist yet — it reads the file off disk and hands the text in
        /// here, so verbatim mode is decided by the same parser the game uses
        /// rather than by an editor-side re-reading of the same JSON.
        /// </summary>
        public static AssetManifest FromJson(string json, string label)
        {
            AssetManifest m;
            try { m = JsonUtility.FromJson<AssetManifest>(SanitiseNulls(json)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[AssetManifests] {label} did not " +
                                 $"parse: {e.Message} — the asset will bind through the " +
                                 "legacy token tables instead.");
                return null;
            }
            if (m == null)
            {
                Debug.LogWarning($"[AssetManifests] {label} is empty.");
                return null;
            }
            m.resourcePath = label;
            m.materials ??= Array.Empty<AssetMaterialDef>();
            m.objects ??= Array.Empty<AssetObjectDef>();
            // An omitted block deserializes to null, and a hand-written manifest
            // legitimately omits all three — they are for the validator, not the
            // game. Filled with defaults so every reader can dot through them; the
            // defaults say "unpinned, unknown, not overridden", which is the truth
            // about a manifest that did not mention them.
            m.spec ??= new AssetSpecDef();
            m.source ??= new AssetSourceDef();
            m.notes ??= new AssetNotesDef();
            return m;
        }

        /// <summary>
        /// A hand-written manifest may spell an unpinned spec axis <c>null</c>,
        /// which is how the plan wrote it and how a person naturally writes it.
        /// <c>JsonUtility</c> has no nullable float and will not reliably survive
        /// being handed one, so the three axis keys are substituted for the −1 that
        /// means the same thing.
        ///
        /// Three named keys rather than a blanket null-to-zero, for the reason
        /// <c>TtExport.SanitiseNulls</c> states one level up: any OTHER null is a
        /// schema change that should fail loudly here rather than arrive downstream
        /// as a silent zero. Zero is a legal size and −1 is not, which is the whole
        /// reason the sentinel is negative.
        /// </summary>
        private static string SanitiseNulls(string json) =>
            Regex.Replace(json, "\"([xyz])\"\\s*:\\s*null", "\"$1\": -1");

        /// <summary>Drop every parsed manifest and every material built from one.
        /// For the commit pipeline, which has just written a manifest the session
        /// has already cached the absence of.</summary>
        public static void ResetCache()
        {
            foreach (AssetManifest m in _cache.Values) m?.ResetMaterials();
            _cache.Clear();
            // The binder reports a manifest's complaints once per key per session.
            // A rewritten manifest deserves to be complained about again — or, much
            // more usefully, to fall silent where it did not before.
            AssetManifestBinder.ResetDiagnostics();
        }

        // ---- materials --------------------------------------------------------

        /// <summary>A fresh Standard material for <paramref name="d"/>. The caller
        /// owns it; use <see cref="AssetManifest.Shared"/> for anything that is not
        /// the per-car paint channel.</summary>
        public static Material BuildMaterial(AssetMaterialDef d, Color tint)
        {
            if (d == null) return null;
            var m = new Material(Shader.Find("Standard")) { name = d.name };
            Configure(m, d, tint, null);
            return m;
        }

        /// <summary>
        /// Write <paramref name="d"/> onto <paramref name="m"/> — <b>the one place
        /// the manifest's numbers become shader properties</b>.
        ///
        /// Takes a material rather than returning one because the paint channel is
        /// the car's OWN body material: the manifest supplies its livery maps and
        /// its metal/gloss while the car keeps owning its colour, so
        /// <c>bodyColor</c>, <c>SetBodyMaterial</c> and the garage painter go on
        /// working against the same object they always did.
        ///
        /// <paramref name="load"/> resolves a map path to a texture; null means
        /// <c>Resources.Load</c>. Asset Studio's preview passes its own loader,
        /// because a draft's PNGs are still in a Blender export folder outside the
        /// project — and that injection is the whole reason this mapping lives in
        /// one function instead of two that drift.
        ///
        /// <paramref name="tint"/> is the design's body colour and reaches only a
        /// <see cref="AssetMaterialDef.paintChannel"/> material. A BAKED one has no
        /// flat colour of its own, so the tint IS the colour and white means
        /// untinted; an unbaked one multiplies its authored rgb by the same tint.
        /// </summary>
        public static void Configure(Material m, AssetMaterialDef d, Color tint,
                                     Func<string, Texture2D> load)
        {
            if (m == null || d == null) return;
            load ??= LoadTexture;

            Color paint = d.paintChannel ? tint : Color.white;
            Color albedo = d.baked ? paint : d.Rgb * paint;
            albedo.a = Mathf.Clamp01(d.alpha);
            m.color = albedo;
            m.SetFloat("_Metallic", Mathf.Clamp01(d.metallic));
            m.SetFloat("_Glossiness", Mathf.Clamp01(d.smoothness));

            Texture2D albedoTex = load(d.mapAlbedo);
            if (albedoTex != null) m.mainTexture = albedoTex;

            Texture2D ms = load(d.mapMetallicSmoothness);
            if (ms != null)
            {
                m.SetTexture("_MetallicGlossMap", ms);
                m.EnableKeyword("_METALLICGLOSSMAP");
                // With the map bound, Standard reads metallic from R and smoothness
                // from A and IGNORES _Metallic entirely; _Glossiness is replaced by
                // _GlossMapScale. Pinning the scale to 1 is what keeps the map
                // meaning what it says — folding the authored smoothness in here
                // would apply it twice, once baked into the alpha channel and once
                // as a multiplier over it.
                m.SetFloat("_GlossMapScale", 1f);
            }

            Texture2D normal = load(d.mapNormal);
            if (normal != null)
            {
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }

            if (d.emissionStrength > 0f || !string.IsNullOrEmpty(d.mapEmission))
            {
                // Blender's strength multiplies its emission colour and Unity's
                // _EmissionColor is HDR, so the product carries straight across:
                // a 26x blue strobe stays a 26x blue strobe and blooms.
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                Color emis = d.Emission * Mathf.Max(0f, d.emissionStrength);
                // Multiplying a Color by a float scales its ALPHA too, which would
                // leave the strength sitting in a channel Standard's emission does
                // not read. Harmless to render and actively misleading to anyone
                // reading a material dump, where it shows up as an alpha of 26.
                emis.a = 1f;
                m.SetColor("_EmissionColor", emis);
                Texture2D em = load(d.mapEmission);
                if (em != null) m.SetTexture("_EmissionMap", em);
            }

            if (albedo.a < 1f) MakeTransparent(m);
        }

        /// <summary>
        /// Standard's transparent mode, set completely.
        ///
        /// All five of these matter and the project has been bitten by setting only
        /// some: <c>_Mode</c> alone leaves the shader opaque, the keyword alone
        /// leaves the blend state opaque, and the render queue alone leaves it
        /// sorting with the opaques. (<c>PartVisualFactory.MakeGhostMat</c> still
        /// pairs <c>_Mode 3</c> with <c>_ALPHABLEND_ON</c>, which are two different
        /// modes — a noted follow-up, not fixed from here.)
        /// </summary>
        public static void MakeTransparent(Material m)
        {
            m.SetFloat("_Mode", 3f);                       // Transparent
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>A map path to a texture, or null for "" and for a path that
        /// resolves to nothing. Unity caches the load itself, so this stays cheap
        /// when fourteen materials share four maps.</summary>
        public static Texture2D LoadTexture(string resourcePath) =>
            string.IsNullOrEmpty(resourcePath)
                ? null
                : Resources.Load<Texture2D>(resourcePath);

        // ---- small conversions -------------------------------------------------

        public static Color ToColor(float[] v, Color fallback) =>
            v == null || v.Length < 3 ? fallback : new Color(v[0], v[1], v[2], 1f);

        public static Vector3 ToVector(float[] v) =>
            v == null || v.Length < 3 ? Vector3.zero : new Vector3(v[0], v[1], v[2]);
    }
}
