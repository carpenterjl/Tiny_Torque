using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// Builds preview Materials from a draft's numbers and the export's textures.
    ///
    /// <b>The property mapping is not here.</b> It is
    /// <c>AssetManifests.Configure</c> — the runtime's — and this class supplies
    /// only the two things the runtime cannot: a draft projected to
    /// <c>AssetMaterialDef</c>, and a texture loader that reads PNGs off disk.
    /// The draft's textures are not in the project at all; they are files in a
    /// Blender export folder outside it, which is why <c>Resources.Load</c> is
    /// not an option and why <c>Configure</c> takes its loader as an argument.
    ///
    /// That injection is the whole design: a preview where you edit a smoothness
    /// and see nothing change is a form rather than an editor, and a preview that
    /// computes the answer its OWN way is worse than useless — it agrees with
    /// itself and disagrees with the game. Same argument as
    /// <c>PartVisualFactory.BindByToken</c> one level up.
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

            // The game's own mapping, against the draft's own textures. The normal
            // map is the one place the preview cannot match the shipped asset: it
            // arrives here as a plain RGB image because there is no importer
            // involved, where a committed one gets
            // TextureImporterType.NormalMap from PartTexturePostprocessor. It
            // reads well enough to judge shape by, and not well enough to judge
            // lighting by.
            AIHWSim.Vehicles.AssetManifests.Configure(
                m, d.ToDef(), tint, file => Load(exportDir, file));

            _mats[key] = m;
            return m;
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
