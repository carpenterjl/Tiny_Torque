using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Traffic-signal lamp cycle for dt_traffic_light: green 4 s → amber 1 s →
    /// red 3 s. The exporter names one lens piece per lamp (siggreen / sigamber
    /// / sigred), each carrying a shared emission-enabled material; this drives
    /// their <c>_EmissionColor</c> through per-renderer MaterialPropertyBlocks,
    /// never the shared materials (shared by every signal and the palette
    /// icon). No phase offset on purpose — every signal on a map changes
    /// together, like a timed downtown grid. Cosmetic only.
    /// </summary>
    public sealed class SignalCycle : MonoBehaviour
    {
        private const float GreenS = 4f, AmberS = 1f, RedS = 3f;
        private const float Dim = 0.05f;

        private static readonly Color GreenOn = new Color(0.4f, 3.2f, 1.1f);
        private static readonly Color AmberOn = new Color(3.2f, 1.7f, 0.25f);
        private static readonly Color RedOn = new Color(3.2f, 0.35f, 0.15f);

        private readonly List<Renderer> _green = new List<Renderer>();
        private readonly List<Renderer> _amber = new List<Renderer>();
        private readonly List<Renderer> _red = new List<Renderer>();
        private MaterialPropertyBlock _mpb;
        private int _lastLamp = -1;

        private void Start()
        {
            _mpb = new MaterialPropertyBlock();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                string n = r.gameObject.name;
                if (n.StartsWith("siggreen")) _green.Add(r);
                else if (n.StartsWith("sigamber")) _amber.Add(r);
                else if (n.StartsWith("sigred")) _red.Add(r);
            }
        }

        private void Update()
        {
            float t = Time.time % (GreenS + AmberS + RedS);
            int lamp = t < GreenS ? 0 : t < GreenS + AmberS ? 1 : 2;
            if (lamp == _lastLamp) return;
            _lastLamp = lamp;
            Apply(_green, GreenOn, lamp == 0);
            Apply(_amber, AmberOn, lamp == 1);
            Apply(_red, RedOn, lamp == 2);
        }

        private void Apply(List<Renderer> list, Color on, bool lit)
        {
            var c = lit ? on : on * Dim;
            for (int i = 0; i < list.Count; i++)
            {
                list[i].GetPropertyBlock(_mpb);
                _mpb.SetColor("_EmissionColor", c);
                list[i].SetPropertyBlock(_mpb);
            }
        }
    }
}
