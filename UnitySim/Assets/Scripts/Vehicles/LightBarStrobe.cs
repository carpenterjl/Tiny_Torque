using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Alternating red/blue flash for the police light bar. Purely cosmetic:
    /// attached by <see cref="PartVisualFactory.BuildLightViz"/> to bar-style
    /// light parts, it finds the renderers bound to the shared RedStrobe /
    /// BlueStrobe materials and modulates their emission through per-renderer
    /// MaterialPropertyBlocks — NEVER through the shared materials themselves,
    /// which are also used by the palette icon and every other bar in the
    /// scene. Runs identically on the garage preview, the local car and LAN
    /// ghosts (the part is rebuilt from vehicleJson on every machine, so no
    /// network state is needed; the phases simply free-run).
    /// </summary>
    public sealed class LightBarStrobe : MonoBehaviour
    {
        private const float FlashHz = 2.5f;      // full red+blue cycle rate
        private const float DimFactor = 0.04f;   // "off" lens keeps a faint glow

        private Renderer[] _red, _blue;
        private Color _redEmit, _blueEmit;
        private MaterialPropertyBlock _mpb;
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private void Start()
        {
            var reds = new System.Collections.Generic.List<Renderer>();
            var blues = new System.Collections.Generic.List<Renderer>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial == PartVisualFactory.RedStrobe) reds.Add(r);
                else if (r.sharedMaterial == PartVisualFactory.BlueStrobe) blues.Add(r);
            }
            _red = reds.ToArray();
            _blue = blues.ToArray();
            _redEmit = PartVisualFactory.RedStrobe.GetColor(EmissionId);
            _blueEmit = PartVisualFactory.BlueStrobe.GetColor(EmissionId);
            _mpb = new MaterialPropertyBlock();
            enabled = _red.Length > 0 || _blue.Length > 0;
        }

        private void Update()
        {
            // Square-wave alternation; a small offset keeps bars in one scene
            // from syncing into a single metronome.
            bool redOn = Mathf.Repeat(Time.time * FlashHz + GetInstanceID() * 0.013f, 1f) < 0.5f;
            Apply(_red, _redEmit, redOn);
            Apply(_blue, _blueEmit, !redOn);
        }

        private void Apply(Renderer[] set, Color emit, bool on)
        {
            Color c = on ? emit : emit * DimFactor;
            foreach (var r in set)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(_mpb);
            }
        }
    }
}
