using UnityEngine;

namespace AIHWSim.Rendering
{
    /// <summary>
    /// Dependency-free bloom for the Built-in RP: bright-pass → three-level
    /// half/quarter/eighth blur chain → additive composite, driven by
    /// Resources/Shaders/AIHWSimBloom.shader.
    ///
    /// Exists because the game runs Built-in RP, Gamma space, no post stack —
    /// where an emissive material above 1.0 just clips to a flat bright patch.
    /// The Blender source sells its neon at 5–19× albedo through AgX plus a
    /// compositor glare; this pass is that glare's stand-in, and the glow
    /// retune that followed it only reads correctly with this on.
    ///
    /// Attach via <see cref="Attach"/>, which also turns the camera's HDR on so
    /// authored >1 emission survives into the source texture. Display cameras
    /// only — NEVER the on-car CameraSensor (firmware eyes stay honest), the
    /// icon/preview RTs (crisp readback), or the builder camera (clarity).
    /// OnRenderImage runs inside the camera's own render, so IMGUI (drawn in
    /// the later GUI phase) is never smeared, and a split-screen viewport
    /// camera blooms only its own viewport-sized RT.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraBloom : MonoBehaviour
    {
        // HDR threshold: only pixels the scene pushed past display white bloom
        // — which in this project means emissives and the sun's specular. The
        // knee softens the cut so near-threshold pixels fade in.
        private const float Threshold = 1.05f;
        private const float Knee = 0.5f;
        private const float Intensity = 0.9f;
        private const int Levels = 3;

        private static Material _mat;
        private static bool _warned;

        /// <summary>Idempotent attach + HDR enable for a display camera.</summary>
        public static void Attach(Camera cam)
        {
            if (cam == null) return;
            cam.allowHDR = true;
            if (cam.GetComponent<CameraBloom>() == null)
                cam.gameObject.AddComponent<CameraBloom>();
        }

        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (!Persistence.SettingsStore.Current.bloom)
            {
                Graphics.Blit(src, dest);
                return;
            }
            if (_mat == null)
            {
                var sh = Shader.Find("Hidden/AIHWSim/Bloom");
                if (sh == null)
                {
                    // Stripped shader = no glow, never a black screen. Warn
                    // once (the RustPaint idiom), then pass frames through.
                    if (!_warned)
                    {
                        Debug.LogWarning("[CameraBloom] Hidden/AIHWSim/Bloom missing — " +
                                         "bloom disabled (is the shader under Resources?)");
                        _warned = true;
                    }
                    Graphics.Blit(src, dest);
                    return;
                }
                _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }

            _mat.SetFloat("_Threshold", Threshold);
            _mat.SetFloat("_Knee", Knee);

            var rts = new RenderTexture[Levels];
            int w = Mathf.Max(1, src.width >> 1), h = Mathf.Max(1, src.height >> 1);
            rts[0] = RenderTexture.GetTemporary(w, h, 0, src.format);
            rts[0].filterMode = FilterMode.Bilinear;
            Graphics.Blit(src, rts[0], _mat, 0);      // bright-pass into half res
            Blur(rts[0]);
            for (int i = 1; i < Levels; i++)
            {
                w = Mathf.Max(1, w >> 1); h = Mathf.Max(1, h >> 1);
                rts[i] = RenderTexture.GetTemporary(w, h, 0, src.format);
                rts[i].filterMode = FilterMode.Bilinear;
                Graphics.Blit(rts[i - 1], rts[i]);    // downsample
                Blur(rts[i]);
            }
            // Accumulate the wider levels back up into the half-res buffer, so
            // the composite reads one texture carrying tight + wide glow.
            for (int i = Levels - 1; i > 0; i--)
                Graphics.Blit(rts[i], rts[i - 1], _mat, 3);   // additive copy

            _mat.SetTexture("_BloomTex", rts[0]);
            _mat.SetFloat("_Intensity", Intensity);
            Graphics.Blit(src, dest, _mat, 2);

            for (int i = 0; i < Levels; i++) RenderTexture.ReleaseTemporary(rts[i]);
        }

        private void Blur(RenderTexture rt)
        {
            var tmp = RenderTexture.GetTemporary(rt.width, rt.height, 0, rt.format);
            tmp.filterMode = FilterMode.Bilinear;
            _mat.SetVector("_Dir", new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(rt, tmp, _mat, 1);
            _mat.SetVector("_Dir", new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(tmp, rt, _mat, 1);
            RenderTexture.ReleaseTemporary(tmp);
        }
    }
}
