using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The Volkswagen Tiguan's palette, as Built-in-RP Standard materials.
    ///
    /// <b>Every number here comes from the manifest.</b> Blender/build_tiguan_part.py
    /// walks each authored material's node tree to its Principled BSDF and reads
    /// base colour, metallic, roughness, coat, emission and transmission off it,
    /// so <b>this file has no colour table in it</b> — the same standing rule
    /// CircuitMaterials carries, and the reason a retune in Blender reaches the
    /// game without anyone retyping a float.
    ///
    /// It is a THIRD token table, deliberately not thirty more rows in
    /// <see cref="PartVisualFactory.AccentTokens"/>. Leaving that table's
    /// contents and order untouched is what guarantees none of the seven arcade
    /// cars can regress behind this work, and every token here is prefixed
    /// "tig" so the two namespaces cannot collide either way.
    ///
    /// Three rules turn probed physical parameters into a shader that has no
    /// equivalent for them. Each is a RULE, applied to every material, rather
    /// than a number chosen per material — that distinction is the whole point:
    ///
    ///   • <b>Clearcoat.</b> Standard has none. A coated material probed for its
    ///     base roughness alone comes out matte — M_TigPaint is 0.20–0.48 rough
    ///     under a 0.04-rough coat, and what you see on a real car is the coat.
    ///     So: smoothness = Lerp(base, coat, coatWeight).
    ///   • <b>Emission.</b> M_TigEmitW/R/A are pure emitters over a black base;
    ///     drop the emission and every lamp on the car becomes a hole.
    ///   • <b>Transmission.</b> The glazing and the three lamp lenses are
    ///     transmissive and the cabin is modelled, so opaque glass would hide an
    ///     interior that is genuinely there.
    ///
    /// <b>M_TigPaint is a colour, not a paint.</b> It is a procedural Voronoi
    /// flake at scale 500 driving base colour, metallic, roughness and a normal
    /// together, under a coat with orange peel. The probe resolves it to the
    /// flake/binder mean and the Map Range bands — a dark navy metallic. The
    /// flakes, the sparkle, the tilt normal and the peel are gone. That is the
    /// honest answer for an untextured Built-in-RP kit, and recovering it would
    /// mean baking to a texture the shell has no UVs for.
    /// </summary>
    public static class TiguanMaterials
    {
        /// <summary>Manifest path under Resources/, without the extension.</summary>
        public const string ManifestPath = "PartModels/tiguan_materials";

        /// <summary>
        /// Blender's Transmission Weight is a physical parameter; Standard's
        /// alpha is a blend weight. There is no conversion between them, only a
        /// choice, so this is the ONE authored number in the file and it is
        /// named rather than inlined. Fully transmissive glass lands here.
        /// </summary>
        private const float GlassAlpha = 0.30f;

        [Serializable]
        private sealed class Entry
        {
            public string token;
            public string name;
            public float[] rgb;
            public float metallic;
            public float transmission;
            public float smoothness;
            public float coat;
            public float coatSmoothness;
            public float[] emission;
            public float emissionStrength;
            public bool probed;
        }

        [Serializable]
        private sealed class Doc
        {
            public int schema;
            public Entry[] materials;
        }

        private static (string, Material)[] _tokens;

        /// <summary>
        /// token → material, ordered so a first-match substring test is safe.
        ///
        /// Sorted by DESCENDING token length, which makes "every compound token
        /// precedes the token it contains" a property of this loader instead of
        /// a rule someone has to remember: "tiglensred" and "tiglensamber" both
        /// contain "tiglens", and the comment at PartVisualFactory.AccentTokens
        /// records two bugs shipped from maintaining that order by hand.
        ///
        /// Rebuilt transparently if Unity destroyed the materials on a play-mode
        /// exit, exactly like PartVisualFactory's lazy slots.
        /// </summary>
        public static (string, Material)[] Tokens
        {
            get
            {
                if (_tokens != null && _tokens.Length > 0 && _tokens[0].Item2 != null)
                    return _tokens;
                return _tokens = Build();
            }
        }

        private static (string, Material)[] Build()
        {
            var ta = Resources.Load<TextAsset>(ManifestPath);
            if (ta == null)
            {
                Debug.LogWarning($"[TiguanMaterials] {ManifestPath} not found — the "
                                 + "Tiguan will render in its fallback body material. "
                                 + "Re-run Blender/build_tiguan_part.py.");
                return Array.Empty<(string, Material)>();
            }

            Doc doc;
            try { doc = JsonUtility.FromJson<Doc>(ta.text); }
            catch (Exception e)
            {
                Debug.LogWarning($"[TiguanMaterials] {ManifestPath} did not parse: {e.Message}");
                return Array.Empty<(string, Material)>();
            }
            if (doc?.materials == null || doc.materials.Length == 0)
            {
                Debug.LogWarning($"[TiguanMaterials] {ManifestPath} names no materials.");
                return Array.Empty<(string, Material)>();
            }

            var list = new List<(string, Material)>(doc.materials.Length);
            foreach (var e in doc.materials)
            {
                if (e == null || string.IsNullOrEmpty(e.token)) continue;
                list.Add((e.token, Make(e)));
            }

            // Longest token first. See the doc comment — this is the ordering
            // guarantee, not a tidy-up.
            list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            return list.ToArray();
        }

        private static Material Make(Entry e)
        {
            Color c = ToColor(e.rgb);

            // Standard has no clearcoat; the coat is what you actually see.
            float smooth = Mathf.Clamp01(Mathf.Lerp(e.smoothness, e.coatSmoothness,
                                                    Mathf.Clamp01(e.coat)));

            Material m;
            if (e.transmission > 0.5f)
            {
                c.a = Mathf.Lerp(1f, GlassAlpha, Mathf.Clamp01(e.transmission));
                m = PartVisualFactory.MakeGhostMat(c);
            }
            else
            {
                m = new Material(Shader.Find("Standard")) { color = c };
            }
            m.name = string.IsNullOrEmpty(e.name) ? e.token : e.name;
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", Mathf.Clamp01(e.metallic));

            if (e.emissionStrength > 0f && e.emission != null && e.emission.Length >= 3)
            {
                // Blender's strength is a multiplier on the emission colour, and
                // Unity's _EmissionColor is HDR, so the product carries straight
                // across — a 14x white lamp stays a 14x white lamp and blooms.
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                m.SetColor("_EmissionColor", new Color(
                    e.emission[0] * e.emissionStrength,
                    e.emission[1] * e.emissionStrength,
                    e.emission[2] * e.emissionStrength));
            }
            return m;
        }

        private static Color ToColor(float[] rgb) =>
            rgb == null || rgb.Length < 3
                ? Color.grey
                : new Color(rgb[0], rgb[1], rgb[2], 1f);

        /// <summary>Drop the cache — for an editor tool that has just re-imported
        /// the manifest and wants the next build to read it.</summary>
        public static void ResetCache() => _tokens = null;
    }
}
