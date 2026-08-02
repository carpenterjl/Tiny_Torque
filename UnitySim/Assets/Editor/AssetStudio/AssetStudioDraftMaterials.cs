using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Builds preview Materials from a draft's numbers and the export's textures.
    ///
    /// <b>A stopgap, and named as one.</b> R2 ships <c>AssetManifests</c>, the
    /// RUNTIME loader that builds these same materials from a committed manifest
    /// with <c>Resources.Load&lt;Texture2D&gt;</c>. This class exists because the
    /// draft's textures are not in the project yet — they are PNGs in a Blender
    /// export folder two directories outside it — and because an authoring tool
    /// where you edit a smoothness and see nothing change is a form, not an
    /// editor. When R2 lands, this becomes a call into it plus the texture
    /// loading, and the property mapping below should move there rather than be
    /// duplicated.
    ///
    /// Everything it creates is <c>HideAndDontSave</c> and destroyed by
    /// <see cref="Dispose"/>. Textures come in through <c>ImageConversion</c>
    /// rather than the AssetDatabase, so nothing here can dirty the project.
    /// </summary>
    public sealed class AssetStudioDraftMaterials : System.IDisposable
    {
        private readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        private readonly Dictionary<string, Texture2D> _texs = new Dictionary<string, Texture2D>();

        /// <summary>Textures the export names but the folder does not have.
        /// Reported once each rather than per frame.</summary>
        public readonly List<string> MissingMaps = new List<string>();

        /// <summary>
        /// The material for <paramref name="d"/>, built once per
        /// (name, tint) pair. <paramref name="tint"/> is the design's bodyColor
        /// and is applied only to the paint channel, which is what makes
        /// "tinting over a baked livery" a multiply rather than a replace.
        /// </summary>
        public Material Get(DraftMaterial d, string exportDir, Color tint)
        {
            if (d == null) return null;
            Color paint = d.paintChannel ? tint : Color.white;
            string key = d.name + "|" + ColorUtility.ToHtmlStringRGBA(paint);
            if (_mats.TryGetValue(key, out Material cached) && cached != null) return cached;

            var m = new Material(Shader.Find("Standard"))
            {
                name = d.name,
                hideFlags = HideFlags.HideAndDontSave,
            };

            // A baked material has no flat colour of its own — the texture carries
            // it — so the tint IS the colour and white means untinted. An unbaked
            // one multiplies its authored rgb by the same tint.
            Color albedo = d.baked ? paint : d.rgb * paint;
            albedo.a = Mathf.Clamp01(d.alpha);
            m.color = albedo;
            m.SetFloat("_Metallic", Mathf.Clamp01(d.metallic));
            m.SetFloat("_Glossiness", Mathf.Clamp01(d.smoothness));

            Texture2D albedoTex = Load(exportDir, d.mapAlbedo);
            if (albedoTex != null) m.mainTexture = albedoTex;

            Texture2D ms = Load(exportDir, d.mapMetallicSmoothness);
            if (ms != null)
            {
                m.SetTexture("_MetallicGlossMap", ms);
                m.EnableKeyword("_METALLICGLOSSMAP");
            }

            Texture2D normal = Load(exportDir, d.mapNormal);
            if (normal != null)
            {
                // Loaded as a plain RGB image, not as a Unity NormalMap import —
                // there is no importer involved. It reads correctly enough to
                // judge shape by; the committed asset gets the real
                // TextureImporterType.NormalMap treatment at R2.
                m.SetTexture("_BumpMap", normal);
                m.EnableKeyword("_NORMALMAP");
            }

            if (d.emissionStrength > 0f || d.mapEmission.Length > 0)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", d.emission * Mathf.Max(0f, d.emissionStrength));
                Texture2D em = Load(exportDir, d.mapEmission);
                if (em != null) m.SetTexture("_EmissionMap", em);
            }

            if (d.alpha < 1f) MakeTransparent(m);

            _mats[key] = m;
            return m;
        }

        /// <summary>
        /// Standard's transparent mode, set completely.
        ///
        /// All five of these matter and the project has been bitten by setting
        /// only some: <c>_Mode</c> alone leaves the shader opaque, the keyword
        /// alone leaves the blend state opaque, and the render queue alone leaves
        /// it sorting with the opaques. <c>MakeGhostMat</c> elsewhere in the
        /// project still sets <c>_Mode 3</c> with <c>_ALPHABLEND_ON</c>, which are
        /// two different modes — noted as a follow-up, not fixed from here.
        /// </summary>
        private static void MakeTransparent(Material m)
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

        private Texture2D Load(string exportDir, string file)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(exportDir)) return null;
            if (_texs.TryGetValue(file, out Texture2D cached)) return cached;

            // The exporter writes bare file names into maps[] and puts the files
            // in Textures/. Accept the folder root too, so a hand-edited export
            // that flattened them still previews.
            string path = Path.Combine(exportDir, "Textures", file);
            if (!File.Exists(path)) path = Path.Combine(exportDir, file);
            if (!File.Exists(path))
            {
                _texs[file] = null;
                MissingMaps.Add(file);
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = file,
            };
            if (!tex.LoadImage(File.ReadAllBytes(path)))
            {
                Object.DestroyImmediate(tex);
                _texs[file] = null;
                MissingMaps.Add(file + " (unreadable)");
                return null;
            }
            _texs[file] = tex;
            return tex;
        }

        /// <summary>
        /// Drop the built materials, keep the textures.
        ///
        /// A draft edit changes numbers, never which PNGs the export holds, so
        /// reloading fourteen images every time a smoothness slider moves would
        /// make the one control that most wants to feel live the one that stutters.
        /// </summary>
        public void ClearMaterials()
        {
            foreach (Material m in _mats.Values)
                if (m != null) Object.DestroyImmediate(m);
            _mats.Clear();
            // MissingMaps deliberately survives: a miss is cached in _texs, so
            // clearing the list here would empty it on the first rebuild and the
            // window would stop reporting textures that are still absent.
        }

        public void Dispose()
        {
            foreach (Material m in _mats.Values)
                if (m != null) Object.DestroyImmediate(m);
            foreach (Texture2D t in _texs.Values)
                if (t != null) Object.DestroyImmediate(t);
            _mats.Clear();
            _texs.Clear();
            MissingMaps.Clear();
        }
    }
}
