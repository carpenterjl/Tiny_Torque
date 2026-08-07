using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Turns a <see cref="FeatureTint"/> into a material.
    ///
    /// <b>Built FROM the authored material, not instead of it.</b> Every finish in
    /// the game carries more than a colour — the police paint has a normal map, the
    /// chrome has a metallic map, the glass is in Fade mode with its own render
    /// queue — and a tint that said "red" by constructing a fresh Standard material
    /// would throw all of that away. Cloning keeps the shader, the maps and the
    /// render state, and writes only what the tint actually asks for. That is what
    /// makes "paint the chrome red" produce red chrome rather than red plastic.
    ///
    /// The −1 sentinels on the tint are what carry "not asked for" through to here;
    /// see <see cref="FeatureTint"/>.
    /// </summary>
    public static class StudioPaint
    {
        /// <summary>
        /// A material for <paramref name="tint"/>, derived from
        /// <paramref name="authored"/>. The caller owns the result and must
        /// destroy it — <see cref="FeatureChannels.Binding"/> does.
        /// </summary>
        public static Material Build(Material authored, FeatureTint tint)
        {
            if (tint == null) return null;

            Material m = authored != null
                ? new Material(authored)
                : new Material(Shader.Find("Standard"));
            m.name = (authored != null ? authored.name : "Studio") + "_tint";

            if (m.HasProperty("_Color")) m.color = tint.color;

            if (!string.IsNullOrEmpty(tint.texture))
            {
                Texture2D tex = StudioTextures.Get(tint.texture);
                if (tex != null)
                {
                    float tiling = tint.tiling > 0f ? tint.tiling
                                                    : StudioTextures.DefaultTiling(tint.texture);
                    if (m.HasProperty("_MainTex"))
                    {
                        m.mainTexture = tex;
                        m.mainTextureScale = new Vector2(tiling, tiling);
                    }
                    // The triplanar body shader has no UVs to scale — it projects
                    // from world space — so its tiling is a scalar property
                    // instead. Both are set when both exist; neither is set when
                    // neither does.
                    if (m.HasProperty("_TileScale")) m.SetFloat("_TileScale", tiling);
                }
            }

            if (tint.metallic >= 0f && m.HasProperty("_Metallic"))
                m.SetFloat("_Metallic", Mathf.Clamp01(tint.metallic));
            if (tint.smoothness >= 0f && m.HasProperty("_Glossiness"))
                m.SetFloat("_Glossiness", Mathf.Clamp01(tint.smoothness));

            if (tint.emission >= 0f && m.HasProperty("_EmissionColor"))
            {
                if (tint.emission <= 0f)
                {
                    m.DisableKeyword("_EMISSION");
                    m.SetColor("_EmissionColor", Color.black);
                }
                else
                {
                    m.EnableKeyword("_EMISSION");
                    // EmissiveIsBlack, matching CosmeticCatalog: these materials are
                    // built at runtime and can never contribute to a baked GI
                    // solution, so claiming they might would only cost a warning.
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                    m.SetColor("_EmissionColor", tint.color * tint.emission);
                }
            }

            return m;
        }

        /// <summary>
        /// A tint pre-filled with what a channel is wearing now, so opening the
        /// paint panel on a part does not reset it to white.
        /// </summary>
        public static FeatureTint Sample(FeatureChannels.Binding binding, int channel)
        {
            var t = new FeatureTint();
            if (binding == null || channel < 0 || channel >= binding.Names.Count) return t;
            t.channel = binding.Names[channel];
            t.color = binding.AuthoredColor(channel);
            return t;
        }
    }
}
