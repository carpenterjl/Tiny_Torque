using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// One material as the author means it, not as Blender exported it.
    ///
    /// <see cref="smoothness"/> rather than roughness on purpose: the conversion
    /// happens once, here, so the runtime never has a per-source convention to get
    /// wrong. <see cref="baked"/> is carried through because it is the export's
    /// null-discriminator and stays the reason the flat values below may mean
    /// nothing.
    /// </summary>
    [System.Serializable]
    public sealed class DraftMaterial
    {
        public string name = "";

        /// <summary>The look lives in the textures; the flat values are absent in
        /// the export and are only defaults here.</summary>
        public bool baked;

        /// <summary>
        /// This material is the car's paint channel: <c>bodyColor</c> multiplies
        /// it instead of replacing it.
        ///
        /// Defaults ON for a baked material, which is the decision taken with the
        /// user — a livery you cannot tint is a livery you cannot theme, and
        /// multiplying keeps the artwork. It applies to NEW manifest assets only;
        /// the four shipped baked cars keep paint mode standing down, because
        /// their renderers are bound by the legacy tables and
        /// <c>HasPaintableBody</c> already answers no for them by name.
        /// </summary>
        public bool paintChannel;

        public Color rgb = Color.white;
        [Range(0f, 1f)] public float metallic;
        [Range(0f, 1f)] public float smoothness = 0.5f;
        [Range(0f, 1f)] public float alpha = 1f;
        public Color emission = Color.black;
        public float emissionStrength;

        // File names as export.json gives them, resolved against the export
        // folder. They become Resources paths when the asset is committed.
        public string mapAlbedo = "";
        public string mapMetallicSmoothness = "";
        public string mapEmission = "";
        public string mapNormal = "";

        public bool HasMaps => !string.IsNullOrEmpty(mapAlbedo)
                            || !string.IsNullOrEmpty(mapMetallicSmoothness)
                            || !string.IsNullOrEmpty(mapEmission)
                            || !string.IsNullOrEmpty(mapNormal);

        /// <summary>
        /// This material as the RUNTIME's manifest type — a straight field-for-field
        /// projection, which is the point: the draft is the authoring shape (a
        /// <c>Color</c> an IMGUI field can edit, an Undo can record) and
        /// <see cref="AIHWSim.Vehicles.AssetMaterialDef"/> is the shipped shape
        /// (<c>float[]</c>, because <c>JsonUtility</c> writes a Color as rgba and a
        /// hand-edited manifest should read as three numbers).
        ///
        /// The map fields cross over unchanged and are therefore still the export's
        /// bare FILE names here, not Resources paths — the commit pipeline rewrites
        /// them when it copies the PNGs in. Preview loading resolves them against
        /// the export folder instead, which is exactly what
        /// <c>AssetManifests.Configure</c>'s injected loader is for.
        /// </summary>
        public AIHWSim.Vehicles.AssetMaterialDef ToDef() =>
            new AIHWSim.Vehicles.AssetMaterialDef
            {
                name = name,
                baked = baked,
                paintChannel = paintChannel,
                rgb = new[] { rgb.r, rgb.g, rgb.b },
                metallic = metallic,
                smoothness = smoothness,
                alpha = alpha,
                emission = new[] { emission.r, emission.g, emission.b },
                emissionStrength = emissionStrength,
                mapAlbedo = mapAlbedo,
                mapMetallicSmoothness = mapMetallicSmoothness,
                mapEmission = mapEmission,
                mapNormal = mapNormal,
            };
    }

    /// <summary>
    /// One child mesh: what it is bound to, and what it is FOR.
    ///
    /// The damage fields are authored now and consumed by nothing. That is
    /// deliberate — the information is only cheap to collect while the author is
    /// already looking at the hierarchy and can say "that is the left door and it
    /// comes off", and re-deriving it later from 41 anonymous meshes is the
    /// expensive version of the same question.
    /// </summary>
    [System.Serializable]
    public sealed class DraftObject
    {
        /// <summary>The Blender OBJECT name, which is what Unity names the
        /// GameObject — never the mesh datablock name.</summary>
        public string name = "";

        /// <summary>Material name per submesh slot; index IS the slot.</summary>
        public List<string> slots = new List<string>();

        /// <summary>
        /// False until a human has looked at this object in the preview and said
        /// the slot order is right.
        ///
        /// <b>Why it cannot start true.</b> The imported FBX gives the slot COUNT
        /// and nothing else — <c>PartModelPostprocessor</c> forces
        /// <c>materialImportMode = None</c>, so every slot arrives null. And
        /// <c>export.json</c> lists an object's materials in the order the
        /// materials appear in the file, which is Blender's material list order,
        /// not the object's slot order. So the initial mapping is a proposal, and
        /// a proposal that called itself verified would be the exact kind of
        /// plausible-wrong this whole tool exists to stop.
        /// </summary>
        public bool slotsVerified;

        public string role = DraftRoles.Structural;

        /// <summary>Hit points before this piece is considered destroyed. 0 means
        /// "not separately destructible".</summary>
        public float healthHp;

        /// <summary>Pieces that fail together — a door and its window, a bumper
        /// and its lights. Free text; empty means "on its own".</summary>
        public string group = "";
    }

    /// <summary>
    /// The damage roles, as STRINGS.
    ///
    /// Not an enum, and for the same reason the manifest's materialMode is not
    /// one: <c>JsonUtility</c> writes an enum as its ordinal, so inserting a role
    /// in the middle silently reinterprets every asset already authored, and a
    /// diff of the manifest shows a number nobody can read.
    /// </summary>
    public static class DraftRoles
    {
        public const string Structural = "Structural";

        public static readonly string[] All =
        {
            Structural,   // load path; the car is not a car without it
            "Panel",      // body panel, deformable, can detach
            "Glass",      // shatters rather than dents
            "Light",      // emissive; breaking it should stop the emission
            "Trim",       // cosmetic only, free to fall off
            "Wheel",      // rotates; owned by the wheel rig, not the shell
            "Detachable", // designed to come away whole (bumper, spoiler, door)
            "Ignore",     // never damaged; interior, hidden geometry
        };

        public static int IndexOf(string role)
        {
            int i = System.Array.IndexOf(All, role);
            return i < 0 ? 0 : i;
        }
    }

    /// <summary>How an asset's materials reach the game.</summary>
    public static class DraftMaterialModes
    {
        public const string Manifest = "Manifest";
        public const string Verbatim = "Verbatim";
        public static readonly string[] All = { Manifest, Verbatim };
    }

    /// <summary>
    /// An asset being authored: everything the manifest will say, in a form Unity
    /// can serialize and <c>Undo</c> can reach.
    ///
    /// <b>A ScriptableObject and not a JSON string, and that is the whole point.</b>
    /// <c>Undo.RecordObject</c> records the serialized state of a
    /// <c>UnityEngine.Object</c>; it has nothing to say about a string you parsed
    /// yourself. Authoring against an object and serialising only at commit is what
    /// makes every edit in this tool a Ctrl+Z — including the ones that span forty
    /// rows, like "propose the scale correction".
    ///
    /// Lives under <c>Assets/TinyTorqueAssets/AssetStudio/Drafts/</c>: outside
    /// <c>Resources/</c> and in no build scene, so a draft costs a player build
    /// nothing, and inside the editor-only pack folder that already ships nothing.
    /// </summary>
    public sealed class AssetStudioDraft : ScriptableObject
    {
        /// <summary>Manifest schema this draft targets. Bumped when the written
        /// manifest's shape changes, never for a UI change.</summary>
        public int schema = 1;

        /// <summary>The game key — the FBX stem the asset will be committed
        /// under, and what <c>Resources.Load</c> will resolve.</summary>
        public string key = "";

        /// <summary>One of <see cref="AssetKind"/>, as a string for the same
        /// reason the roles are.</summary>
        public string kind = AssetKind.CarBody.ToString();

        /// <summary>Picker text. Free to differ from <see cref="key"/>: renaming
        /// what a player reads is not a save-format change, which is the point of
        /// a string key. Empty falls back to the key.</summary>
        public string label = "";

        public string materialMode = DraftMaterialModes.Manifest;

        /// <summary>
        /// The shipped key this draft was explicitly authorised to REPLACE, or "".
        ///
        /// Overwriting an asset the project ships is refused by default and has to
        /// be, because <c>BodyCatalog</c>'s seed row wins over any manifest of the
        /// same name: a body_patrol that arrived by accident would swap the car's
        /// mesh while the row describing it stayed the old one. The Replace
        /// workflow is that refusal's one exception, and this field is the
        /// author's signature on it.
        ///
        /// <b>It records the KEY, not a bool, and that is the whole design.</b>
        /// The authorisation is checked with a string comparison against the key
        /// actually being committed, so editing the key afterwards revokes it
        /// rather than carrying a licence to overwrite <c>body_patrol</c> across
        /// to <c>body_coupe</c>.
        /// </summary>
        public string replacesKey = "";

        /// <summary>Whether this draft may overwrite <paramref name="k"/> — an
        /// asset the project ships. See <see cref="replacesKey"/>.</summary>
        public bool MayReplace(string k) =>
            !string.IsNullOrEmpty(k)
            && string.Equals(replacesKey, k, System.StringComparison.Ordinal);

        // ---- what the catalogue row will say --------------------------------

        /// <summary>
        /// Drag coefficient, for a body. <b>−1 means nobody has decided</b>, and
        /// the commit refuses rather than defaulting: a car whose top speed was
        /// chosen by a fallback constant is a car nobody chose.
        ///
        /// The band is the one <c>[AKEY]</c> already holds every seed row to —
        /// 0.15 is a teardrop, 1.2 a flat plate broadside.
        /// </summary>
        public float cd = -1f;

        /// <summary>Built-in downforce area (m²) — what the SHELL does without
        /// parts. Zero is the honest answer for anything that is not carrying an
        /// authored wing, which is most things.</summary>
        public float clA;

        /// <summary>Wheels: whether the garage's style cycle offers it. False is
        /// how a wheel ships as an unlock rather than as a choice.</summary>
        public bool garageOffered = true;

        // ---- cosmetics ------------------------------------------------------

        /// <summary>Which mount it occupies — a <c>CosmeticSlot</c> name.
        /// Cosmetics only.</summary>
        public string cosmeticSlot = "Topper";

        /// <summary>Drop tier — a <c>Rarity</c> name. It also decides the scrap
        /// value and the shop price, which are NOT authored here: a new hat must
        /// not be able to reprice the economy.</summary>
        public string cosmeticRarity = "Common";

        /// <summary>Which of the four worlds a themed crate draws it from — a
        /// <c>CosmeticTheme</c> name.</summary>
        public string cosmeticTheme = "Arcade";

        /// <summary>The one-liner the crate reveal and the shop print.</summary>
        public string description = "";

        // ---- where it came from -------------------------------------------
        public string sourceAssetName = "";
        public string sourceBlend = "";
        public string sourceExportedAtUtc = "";

        /// <summary>Absolute path of the export folder. Not written into the
        /// manifest — it is a fact about this machine — but it is how the draft
        /// finds its textures and how re-sync knows what to re-read.</summary>
        public string sourceDir = "";

        // ---- geometry ------------------------------------------------------

        /// <summary>
        /// The asset's real measured extents in the game's axes, after
        /// <see cref="authorYawDeg"/>. Zero until proposed or measured.
        /// </summary>
        public Vector3 authorSize;

        /// <summary>The quarter turn that puts the long axis on +Z. A multiple of
        /// 90; a bounding box does not survive anything else.</summary>
        public float authorYawDeg;

        /// <summary>
        /// A pivot correction in MESH UNITS, applied after the yaw and inside the
        /// scale — see <see cref="AIHWSim.Vehicles.AssetManifest.authorOffset"/>
        /// for why those are the right two answers.
        ///
        /// Never proposed. The export can measure how big a mesh is and which way
        /// its long axis runs, but "the wheels should touch the ground" is a
        /// judgement about a car that only a person looking at one can make.
        /// </summary>
        public Vector3 authorOffset;

        /// <summary>
        /// The single factor that puts this mesh on the game's scale — the
        /// correction the old exporter baked in and this one does not.
        ///
        /// Renamed from <c>proposedUniformScale</c> once it stopped being a
        /// proposal and started being what the manifest records and the game
        /// divides by. <see cref="UnityEngine.Serialization.FormerlySerializedAsAttribute"/>
        /// rather than a plain rename because a draft already on disk carries
        /// the old name, and a silently defaulted 1.0 is a wrong number that
        /// looks exactly like a right one.
        /// </summary>
        [UnityEngine.Serialization.FormerlySerializedAs("proposedUniformScale")]
        public float authorScale = 1f;

        // ---- the payload ---------------------------------------------------
        public List<DraftMaterial> materials = new List<DraftMaterial>();
        public List<DraftObject> objects = new List<DraftObject>();

        // ---- what the exporter said ----------------------------------------
        public int notesGeometryFixes;
        public int notesTextureWarnings;

        /// <summary>Set when the author commits past a failed exporter
        /// verification. The reason is mandatory and is printed by every
        /// <c>[AST]</c> run, so an override is loud for as long as it lasts.</summary>
        public bool verificationOverridden;
        public string overrideReason = "";

        // ---- lookups -------------------------------------------------------

        /// <summary>The kind as the enum, or <see cref="AssetKind.Unassigned"/>
        /// when the string names nothing — which the commit gate refuses on
        /// rather than guessing.</summary>
        public AssetKind Kind =>
            System.Enum.TryParse(kind, out AssetKind k) ? k : AssetKind.Unassigned;

        public DraftMaterial Material(string materialName)
        {
            foreach (DraftMaterial m in materials)
                if (m != null && m.name == materialName) return m;
            return null;
        }

        public DraftObject Object(string objectName)
        {
            foreach (DraftObject o in objects)
                if (o != null && o.name == objectName) return o;
            return null;
        }

        public string[] MaterialNames()
        {
            var names = new string[materials.Count + 1];
            names[0] = "(none)";
            for (int i = 0; i < materials.Count; i++)
                names[i + 1] = materials[i]?.name ?? "";
            return names;
        }

        /// <summary>Objects whose slot mapping is still a proposal. The number the
        /// commit gate will refuse on.</summary>
        public int UnverifiedSlots()
        {
            int n = 0;
            foreach (DraftObject o in objects)
                if (o != null && !o.slotsVerified && o.slots.Count > 1) n++;
            return n;
        }

        /// <summary>Slots pointing at a material this draft does not have.</summary>
        public int DanglingSlots()
        {
            int n = 0;
            foreach (DraftObject o in objects)
            {
                if (o?.slots == null) continue;
                foreach (string s in o.slots)
                    if (!string.IsNullOrEmpty(s) && Material(s) == null) n++;
            }
            return n;
        }
    }
}
