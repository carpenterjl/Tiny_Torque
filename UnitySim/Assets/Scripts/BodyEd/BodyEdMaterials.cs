using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The editor's materials, built in code and cached lazily — the same shape
    /// <c>PartVisualFactory</c> uses, and for the same reason: Unity destroys
    /// runtime materials when play mode exits, so a static assigned once at domain
    /// load would come back null on the second Play.
    /// </summary>
    public static class BodyEdMaterials
    {
        /// <summary>The triplanar body shader's name, as declared in
        /// <c>Assets/Resources/Shaders/AIHWSimTriplanar.shader</c>.</summary>
        public const string TriplanarShader = "AIHWSim/TriplanarBody";

        private static Material _body, _stand, _wheel, _shellPaint;
        private static Texture2D _checker, _ring;
        private static bool _warnedMissingShader;

        /// <summary>
        /// An anti-aliased ring, drawn at the pointer to show how wide the sculpt
        /// brush currently reaches.
        ///
        /// Its own texture rather than <c>GarageSkin.RoundedOutline</c>: that one
        /// builds a rounded RECTANGLE with an eight-pixel straight run on each
        /// side, which reads as a squircle at brush sizes and misstates where the
        /// falloff actually ends.
        /// </summary>
        public static Texture2D BrushRing()
        {
            if (_ring != null) return _ring;

            const int N = 64;
            _ring = new Texture2D(N, N, TextureFormat.RGBA32, false)
            {
                name = "BodyEd_BrushRing",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var px = new Color[N * N];
            const float outer = N * 0.5f - 1.5f;
            const float thick = 1.6f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - N * 0.5f + 0.5f) * (x - N * 0.5f + 0.5f) +
                                         (y - N * 0.5f + 0.5f) * (y - N * 0.5f + 0.5f));
                    // 1 on the ring's centre line, fading to 0 half a thickness out.
                    float a = Mathf.Clamp01(1f - Mathf.Abs(d - outer) / thick);
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            _ring.SetPixels(px);
            _ring.Apply(false);
            return _ring;
        }

        /// <summary>
        /// The deformable body's surface: world-space triplanar, so pulling a
        /// vertex moves the surface without dragging the texture with it.
        ///
        /// Falls back to Standard if the shader did not compile or did not ship.
        /// A magenta car would be a clearer signal, but it would also be the only
        /// signal — and this editor is about shape, which a fallback material
        /// still shows correctly.
        /// </summary>
        public static Material Body()
        {
            if (_body != null) return _body;

            Shader sh = Shader.Find(TriplanarShader);
            if (sh == null)
            {
                if (!_warnedMissingShader)
                {
                    _warnedMissingShader = true;
                    Debug.LogWarning($"[BodyEdMaterials] Shader '{TriplanarShader}' not found — " +
                                     "falling back to Standard. Textures will stretch where the " +
                                     "body is deformed, which is the thing triplanar exists to fix.");
                }
                sh = Shader.Find("Standard");
            }

            _body = new Material(sh) { name = "BodyEd_Triplanar" };
            _body.color = new Color(0.62f, 0.66f, 0.72f);
            _body.mainTexture = Checker();
            if (_body.HasProperty("_TileScale")) _body.SetFloat("_TileScale", 12f);
            if (_body.HasProperty("_BlendSharpness")) _body.SetFloat("_BlendSharpness", 4f);
            if (_body.HasProperty("_Glossiness")) _body.SetFloat("_Glossiness", 0.45f);
            if (_body.HasProperty("_Metallic")) _body.SetFloat("_Metallic", 0.15f);
            return _body;
        }

        /// <summary>
        /// The tintable channel a harvested shell piece is bound with.
        ///
        /// A shell binds its renderers by token against one "paint" material — the
        /// car's <c>bodyColor</c> in the game. A piece lifted out of a shell has no
        /// design to take a colour from, so it wears a neutral one and the studio's
        /// paint panel takes it from there. Light rather than mid-grey so an
        /// unpainted piece reads as unfinished rather than as deliberately dark.
        /// </summary>
        public static Material ShellPaint()
        {
            if (_shellPaint != null) return _shellPaint;
            _shellPaint = new Material(Shader.Find("Standard")) { name = "BodyEd_ShellPaint" };
            _shellPaint.color = new Color(0.74f, 0.76f, 0.80f);
            _shellPaint.SetFloat("_Glossiness", 0.5f);
            _shellPaint.SetFloat("_Metallic", 0.05f);
            return _shellPaint;
        }

        /// <summary>The turntable the body sits on.</summary>
        public static Material Stand()
        {
            if (_stand != null) return _stand;
            _stand = new Material(Shader.Find("Standard")) { name = "BodyEd_Stand" };
            _stand.color = new Color(0.13f, 0.14f, 0.17f);
            _stand.SetFloat("_Glossiness", 0.2f);
            _stand.SetFloat("_Metallic", 0f);
            return _stand;
        }

        /// <summary>The wheel markers — context for how big the body is, and the
        /// discs the drag readout charges exposed-wheel drag for.</summary>
        public static Material Wheel()
        {
            if (_wheel != null) return _wheel;
            _wheel = new Material(Shader.Find("Standard")) { name = "BodyEd_Wheel" };
            _wheel.color = new Color(0.08f, 0.08f, 0.09f);
            _wheel.SetFloat("_Glossiness", 0.25f);
            _wheel.SetFloat("_Metallic", 0f);
            return _wheel;
        }

        /// <summary>
        /// A plain checker, generated rather than shipped.
        ///
        /// It exists so the triplanar projection is VISIBLE: a flat colour would
        /// look identical whether the mapping were world-space, object-space or
        /// broken. A grid that stays square across a heavy pull is the whole
        /// demonstration.
        /// </summary>
        public static Texture2D Checker()
        {
            if (_checker != null) return _checker;

            const int N = 64, Cell = 8;
            _checker = new Texture2D(N, N, TextureFormat.RGBA32, true)
            {
                name = "BodyEd_Checker",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var light = new Color(0.86f, 0.87f, 0.90f);
            var dark = new Color(0.68f, 0.70f, 0.75f);
            var px = new Color[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    px[y * N + x] = ((x / Cell + y / Cell) & 1) == 0 ? light : dark;
            _checker.SetPixels(px);
            _checker.Apply();
            return _checker;
        }
    }
}
