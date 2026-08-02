using UnityEditor;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Forces the import settings that a PBR map has to have to be a PBR map, on
    /// the textures that ride beside the Blender-authored assets under
    /// <c>Resources/PartModels/</c>, <c>Resources/TrackProps/</c> and
    /// <c>Resources/Cosmetics/</c>.
    ///
    /// <b>Both rules are about colour space, and both fail silently.</b>
    ///
    ///   • <c>*_MetallicSmoothness</c> is DATA, not a picture: R is metalness and A
    ///     is smoothness, and neither was ever a colour. Imported as sRGB — Unity's
    ///     default — every value is pushed through a gamma curve on the way in, so
    ///     a 0.5 metal reads as roughly 0.21 and the whole car turns matte
    ///     plastic. Nothing errors; the car simply looks wrong in a way that
    ///     invites people to go and edit the numbers in Blender until it matches.
    ///
    ///   • <c>*_Normal</c> must be <c>TextureImporterType.NormalMap</c>, which does
    ///     three things at once (linear, the right DXT5nm-style packing, and the
    ///     shader binding Unity expects). A tangent-space normal map imported as a
    ///     plain sRGB image is the classic "lighting is subtly inside out" bug, and
    ///     Unity's own "this looks like a normal map" fixit dialog never appears in
    ///     a batch run.
    ///
    /// Named by SUFFIX because that is what the Blender exporter writes —
    /// <c>M_Police_Chrome_MetallicSmoothness.png</c> — and what Asset Studio copies
    /// in unchanged, so the convention that decides the import is the same string
    /// the manifest's <c>mapMetallicSmoothness</c> field points at.
    ///
    /// Everything else in scope (albedo, emission) is genuinely a colour and keeps
    /// Unity's defaults, which is why this touches nothing today: the four shipped
    /// liveries are <c>body_*_paint.png</c> and match neither suffix.
    ///
    /// Separate from <see cref="PartModelPostprocessor"/> rather than another method
    /// on it: that class's <c>GetVersion()</c> is the reimport trigger for 200+ FBX,
    /// and a texture rule has no business being able to fire it.
    /// </summary>
    public sealed class PartTexturePostprocessor : AssetPostprocessor
    {
        private const string MetallicSmoothnessSuffix = "_MetallicSmoothness";
        private const string NormalSuffix = "_Normal";

        private static bool InScope(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("Resources/PartModels/")
                || p.Contains("Resources/TrackProps/")
                || p.Contains("Resources/Cosmetics/");
            // Not Asset Studio's preview staging: staging copies the FBX and
            // nothing else — a draft's PNGs are read straight off disk through
            // ImageConversion, never imported — so there is no texture there for
            // this to have an opinion about.
        }

        private void OnPreprocessTexture()
        {
            if (!InScope(assetPath)) return;
            var ti = (TextureImporter)assetImporter;
            string stem = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            if (stem.EndsWith(NormalSuffix, System.StringComparison.OrdinalIgnoreCase))
            {
                ti.textureType = TextureImporterType.NormalMap;
                return;   // NormalMap owns the colour space; setting sRGB after it is meaningless
            }

            if (stem.EndsWith(MetallicSmoothnessSuffix, System.StringComparison.OrdinalIgnoreCase))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                // Smoothness lives in the ALPHA channel, so the alpha has to
                // survive the import: FromInput is the default, but it is also the
                // one setting whose loss turns every smoothness into 1 and every
                // surface into a mirror, so it is stated rather than assumed.
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
            }
        }

        /// <summary>
        /// 1: the first version.
        ///
        /// <b>Adding this class costs one full texture reimport, and not of the
        /// five textures in scope — of every texture Unity knows about, packages
        /// included.</b> An <c>AssetPostprocessor</c>'s version participates in the
        /// import hash of every asset of the type it handles, before anything gets
        /// to ask whether the path is in scope; the scope test happens inside
        /// <c>OnPreprocessTexture</c>, which is far too late to save the hash. It
        /// took about two minutes here and it happens once. Worth knowing before
        /// it looks like a hang, and worth knowing before bumping this number:
        /// the same bill arrives again every time it changes.
        /// </summary>
        public override uint GetVersion() => 1;
    }
}
