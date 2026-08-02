using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// A read-only mirror of the Blender exporter's <c>export.json</c>.
    ///
    /// The exporter writes, per asset, a folder of <c>&lt;NAME&gt;.fbx</c>,
    /// <c>export.json</c>, <c>Materials/*.mat</c> and <c>Textures/*.png</c>. This
    /// type is shaped for <c>JsonUtility</c> — no dictionaries, no ragged arrays —
    /// which is the same house rule <c>CircuitManifest</c> states, and the reason
    /// this side needs no JSON parser of its own.
    ///
    /// <b>Two things about the format are load-bearing and easy to get wrong.</b>
    ///
    /// First, <c>materials[].objects[]</c> lists Blender OBJECT names, and Unity
    /// names an imported GameObject after the Model, not after the mesh datablock.
    /// In <c>POLICE.fbx</c> the mesh names are <c>Police_Body_baked_baked_baked</c>
    /// and the object names are the clean <c>Police_Body</c> — so anything matching
    /// against <c>MeshFilter.sharedMesh.name</c> misses half the hierarchy. Match
    /// GameObject names.
    ///
    /// Second, one object may appear under SEVERAL materials — <c>Police_Body</c>
    /// carries both <c>M_Police_Dark</c> and <c>M_Police_Paint</c> — because it is a
    /// multi-submesh renderer. The export does not say which submesh slot is which,
    /// so slot order can only ever be read off the imported prefab, never inferred
    /// from the order materials appear here.
    /// </summary>
    [Serializable]
    public sealed class TtExport
    {
        public const string FileName = "export.json";

        public string assetName;
        public string objectName;
        public string sourceBlend;
        public string sourceBlendMtimeUtc;
        public long sourceBlendSize;
        public string exportedAtUtc;
        public string blenderVersion;
        public int exportSettingsVersion;
        public string fbxFile;
        public string[] textures;
        public TtMaterial[] materials;

        /// <summary>Maps the exporter could not resolve. Warns; never blocks.</summary>
        public string[] textureWarnings;

        /// <summary>Repairs the exporter ALREADY APPLIED — inward faces flipped and
        /// the like. Not a to-do list and not a failure; a record of geometry that
        /// left Blender differently from how it was modelled. Worth surfacing
        /// loudly, because "my roof has holes" and "the exporter silently fixed my
        /// roof" are the same symptom until somebody reads these.</summary>
        public string[] geometryFixes;

        public TtVerification verification;

        /// <summary>Absolute path of the folder this was read from. Filled by
        /// <see cref="Load"/>, never serialized — it is where the file IS, which is
        /// not something the file can tell us.</summary>
        [NonSerialized] public string dir;

        // ---- loading ---------------------------------------------------------

        /// <summary>
        /// Read and parse the export in <paramref name="assetDir"/>. Returns null
        /// with a sentence in <paramref name="problem"/> — every failure here is a
        /// row the window greys out with a reason, never an exception.
        /// </summary>
        public static TtExport Load(string assetDir, out string problem)
        {
            problem = "";
            string path = Path.Combine(assetDir ?? "", FileName);
            if (!File.Exists(path)) { problem = "no " + FileName; return null; }

            string json;
            try { json = File.ReadAllText(path); }
            catch (Exception e) { problem = "unreadable: " + e.Message; return null; }

            TtExport x;
            try { x = JsonUtility.FromJson<TtExport>(SanitiseNulls(json)); }
            catch (Exception e) { problem = "malformed JSON: " + e.Message; return null; }

            if (x == null) { problem = "empty " + FileName; return null; }
            x.dir = assetDir;
            x.materials ??= Array.Empty<TtMaterial>();
            x.textures ??= Array.Empty<string>();
            x.textureWarnings ??= Array.Empty<string>();
            x.geometryFixes ??= Array.Empty<string>();
            if (string.IsNullOrEmpty(x.assetName))
                x.assetName = Path.GetFileName(assetDir);
            return x;
        }

        /// <summary>
        /// The exporter writes JSON <c>null</c> for a BAKED material's flat values,
        /// because a baked material has none — its colour lives in a texture.
        /// <c>JsonUtility</c> cannot express "absent" for a float and will not
        /// reliably survive being handed a null for one, so those five fields are
        /// substituted before parsing.
        ///
        /// Deliberately five named keys rather than a blanket "any null becomes
        /// zero": the substituted values are never read — <see cref="TtMaterial.baked"/>
        /// is what decides whether the flat values mean anything — and any OTHER
        /// null is a schema change that should fail loudly here rather than arrive
        /// downstream as a silent zero.
        /// </summary>
        private static string SanitiseNulls(string json)
        {
            json = Regex.Replace(json, "\"baseColor\"\\s*:\\s*null", "\"baseColor\": []");
            foreach (string k in new[] { "metallic", "roughness", "alpha", "emissionStrength" })
                json = Regex.Replace(json, "\"" + k + "\"\\s*:\\s*null", "\"" + k + "\": 0");
            return json;
        }

        // ---- convenience -----------------------------------------------------

        public string FbxPath => string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fbxFile)
            ? "" : Path.Combine(dir, fbxFile);

        /// <summary>Every object named by any material, de-duplicated, in first-seen
        /// order. This is the child-mesh list — 41 of them for the police car.</summary>
        public List<string> AllObjects()
        {
            var seen = new HashSet<string>();
            var order = new List<string>();
            foreach (TtMaterial m in materials)
            {
                if (m?.objects == null) continue;
                foreach (string o in m.objects)
                    if (!string.IsNullOrEmpty(o) && seen.Add(o)) order.Add(o);
            }
            return order;
        }

        /// <summary>The materials that name <paramref name="objectName"/>. More than
        /// one means a multi-submesh renderer.</summary>
        public List<TtMaterial> MaterialsOn(string objectName)
        {
            var hits = new List<TtMaterial>();
            foreach (TtMaterial m in materials)
                if (m?.objects != null && Array.IndexOf(m.objects, objectName) >= 0)
                    hits.Add(m);
            return hits;
        }

        // ---- geometry, in the game's axes ------------------------------------

        /// <summary>
        /// The asset's size in UNITY axes, straight from the exporter's own
        /// measurement. Zero when the export carries no verification block.
        /// </summary>
        public Vector3 UnityDims => verification?.unityDimensions != null
                                    && verification.unityDimensions.Length >= 3
            ? new Vector3(verification.unityDimensions[0],
                          verification.unityDimensions[1],
                          verification.unityDimensions[2])
            : Vector3.zero;

        /// <summary>
        /// True when the longest horizontal axis is X. The game builds every car
        /// facing +Z — its sensors, lights and gear are all placed that way — so an
        /// asset modelled long-axis along X needs a quarter turn.
        /// </summary>
        public bool LengthRunsAlongX => UnityDims.x > UnityDims.z;

        /// <summary>Longest horizontal extent, whichever axis carries it.</summary>
        public float Length => Mathf.Max(UnityDims.x, UnityDims.z);

        /// <summary>
        /// The single factor that puts <see cref="Length"/> on
        /// <paramref name="targetLength"/>.
        ///
        /// <b>Uniform, and that is the whole point.</b> The old exporter built every
        /// arcade shell by scaling to length 0.420 with one factor, which is why
        /// <c>PartModelValidator</c> pins those bodies' length and leaves their
        /// width free — "real widths 0.17-0.20 after uniform scale". Proportions were
        /// never touched, and a per-axis fit to
        /// <c>CarVehicle.BodyMeshAuthorSize</c> would touch them, because that
        /// constant is a nominal DIVISOR and not a measurement of any shell.
        /// </summary>
        public float UniformScaleTo(float targetLength) =>
            Length > 1e-6f ? targetLength / Length : 1f;

        /// <summary>
        /// What the asset measures after <paramref name="uniformScale"/> and
        /// <paramref name="yawDeg"/>, expressed in the game's axes — i.e. the
        /// <c>authorSize</c> the runtime should divide by. A quarter turn swaps X
        /// and Z; anything else is refused rather than approximated, because a
        /// bounding box does not survive an arbitrary rotation.
        /// </summary>
        public Vector3 AuthorSizeAfter(float uniformScale, float yawDeg)
        {
            Vector3 d = UnityDims * uniformScale;
            int quarter = Mathf.RoundToInt(Mathf.Repeat(yawDeg, 360f) / 90f) & 3;
            return (quarter == 1 || quarter == 3) ? new Vector3(d.z, d.y, d.x) : d;
        }
    }

    [Serializable]
    public sealed class TtMaterial
    {
        public string name;
        public string safeName;

        /// <summary>The look is a texture, not a set of constants — and the
        /// discriminator for the null flat values, since <c>JsonUtility</c> cannot
        /// tell an absent number from a zero one. Never read
        /// <see cref="TtValues"/> without checking this first.</summary>
        public bool baked;

        public string[] objects;
        public TtMaps maps;
        public TtValues values;

        public int ObjectCount => objects?.Length ?? 0;

        /// <summary>
        /// A swatch colour for the browser. Linear values converted to gamma
        /// because this is drawn for a human to recognise a material by, not fed to
        /// the pipeline — the runtime builds its <c>Color</c> from the same linear
        /// numbers untouched, exactly as <c>TiguanMaterials</c> does. A baked
        /// material has no flat colour at all and gets mid-grey.
        /// </summary>
        public Color Swatch
        {
            get
            {
                if (baked || values?.baseColor == null || values.baseColor.Length < 3)
                    return new Color(0.5f, 0.5f, 0.5f);
                return new Color(values.baseColor[0], values.baseColor[1],
                                 values.baseColor[2]).gamma;
            }
        }
    }

    [Serializable]
    public sealed class TtMaps
    {
        public string baseColor;
        public string metallicSmoothness;
        public string emission;
        public string normal;

        public bool Any => !string.IsNullOrEmpty(baseColor)
                        || !string.IsNullOrEmpty(metallicSmoothness)
                        || !string.IsNullOrEmpty(emission)
                        || !string.IsNullOrEmpty(normal);
    }

    [Serializable]
    public sealed class TtValues
    {
        public float[] baseColor;
        public float metallic;

        /// <summary>Blender's roughness. The manifest stores SMOOTHNESS
        /// (1 − roughness) so the runtime has no per-source conversion to get
        /// wrong; the conversion happens once, in the tool.</summary>
        public float roughness;

        public float emissionStrength;
        public float alpha;

        public float Smoothness => 1f - Mathf.Clamp01(roughness);
    }

    [Serializable]
    public sealed class TtVerification
    {
        public bool passed;
        public float[] rotationDegrees;
        public float[] scale;
        public float[] sourceDimensions;
        public float[] importedDimensions;
        public float[] unityDimensions;
        public string[] issues;

        public int IssueCount => issues?.Length ?? 0;
    }
}
