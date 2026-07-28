using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Audio
{
    /// <summary>
    /// The built-in soundtrack: deterministic chiptune loops rendered to
    /// AudioClips at runtime, one per theme key, in the ProceduralAudio
    /// tradition (no assets, fixed seeds, bit-identical every run). These are
    /// the FALLBACK half of the hybrid music system — any file dropped into a
    /// Music folder replaces them with no code change (see MusicDirector).
    ///
    /// Rendering is event-additive: every note and drum hit is synthesized
    /// into one shared mono buffer at its scheduled sample, and anything that
    /// rings past the end WRAPS to the start (modulo add). That wrap is what
    /// makes the loop seamless without cycle-counting every voice — the tail
    /// you hear at bar 1 is the release of the last bar, exactly as it would
    /// be on the repeat.
    ///
    /// Memory: a loop is 15–25 s of mono floats (~3–5 MB). Themes build
    /// lazily and at most two stay cached (the menu theme plus the current
    /// one) — switching maps evicts, coming back re-renders in tens of ms.
    /// </summary>
    public static class ProceduralMusic
    {
        private static readonly Dictionary<string, AudioClip> _cache =
            new Dictionary<string, AudioClip>();

        private static int _rate;
        private static int Rate
        {
            get
            {
                if (_rate > 0) return _rate;
                _rate = AudioSettings.outputSampleRate;
                if (_rate <= 0) _rate = 44100;
                return _rate;
            }
        }

        public static AudioClip Get(string key)
        {
            if (_cache.TryGetValue(key, out var clip) && clip != null) return clip;

            var theme = ThemeFor(key);
            if (theme == null) return null;
            clip = Render(theme);

            // Keep the menu theme (returned to constantly) + the newcomer.
            if (_cache.Count >= 2)
            {
                string evict = null;
                foreach (var k in _cache.Keys)
                    if (k != MusicDirector.ThemeMenu) { evict = k; break; }
                if (evict != null)
                {
                    if (_cache[evict] != null) Object.Destroy(_cache[evict]);
                    _cache.Remove(evict);
                }
            }
            _cache[key] = clip;
            return clip;
        }

        // ================= themes =================

        private sealed class Theme
        {
            public string name;
            public float bpm = 120f;
            public int bars = 8;
            public int beatsPerBar = 4;          // 3 = waltz
            public int[] roots;                   // midi root per bar (loops)
            public int[] chord = { 0, 4, 7 };     // intervals over the root
            public float leadDuty = 0.5f;         // pulse duty for the lead
            public int leadOctaves = 2;           // lead sits this far above the root
            public float leadGain = 0.30f;
            public float bassGain = 0.30f;
            public float padGain = 0.12f;
            public float kickGain = 0.5f;         // 0 = no kick
            public float hatGain = 0.12f;         // 0 = no hats
            public bool offbeatHats;
            public bool arpTriplets;              // 3 arp notes per beat instead of 2
            public bool staccato;                 // short bouncy notes
            public bool vibratoLead;              // slow pitch wobble (theremin-ish)
            public uint seed = 0x7EA1;
        }

        // Midi roots. D3=50, A2=45, C3=48, F3=53, G2=43, Bb2=46, E3=52.
        private static Theme ThemeFor(string key) => key switch
        {
            // "Showroom Shine" — confident, glossy. Dmaj7 → Gmaj7 arps.
            "menu" => new Theme
            {
                name = key, bpm = 92f, bars = 8,
                roots = new[] { 50, 50, 55, 55, 50, 50, 55, 43 },
                chord = new[] { 0, 4, 7, 11 },
                leadDuty = 0.25f, leadGain = 0.22f, bassGain = 0.24f, padGain = 0.14f,
                kickGain = 0f, hatGain = 0.06f, offbeatHats = true, seed = 0xD00D,
            },
            // Neon synthwave: Am – F – C – G, driving eighth-note bass.
            "downtown" => new Theme
            {
                name = key, bpm = 122f, bars = 8,
                roots = new[] { 45, 45, 41, 41, 48, 48, 43, 43 },
                chord = new[] { 0, 3, 7 },
                leadDuty = 0.5f, leadGain = 0.26f, bassGain = 0.34f, padGain = 0.10f,
                kickGain = 0.55f, hatGain = 0.10f, offbeatHats = true, seed = 0xD701,
            },
            // Music-box romp: C – G – Am – F two octaves up, staccato.
            "toyroom" => new Theme
            {
                name = key, bpm = 132f, bars = 8,
                roots = new[] { 48, 48, 43, 43, 45, 45, 41, 41 },
                chord = new[] { 0, 4, 7 },
                leadDuty = 0.125f, leadOctaves = 3, leadGain = 0.24f,
                bassGain = 0.26f, padGain = 0.06f,
                kickGain = 0.35f, hatGain = 0.14f, staccato = true, arpTriplets = true,
                seed = 0x70FF,
            },
            // Moonlit waltz in 3/4: Fmaj7 – G, bell pad, soft kick on 1.
            "enchanted" => new Theme
            {
                name = key, bpm = 100f, bars = 8, beatsPerBar = 3,
                roots = new[] { 53, 53, 55, 55, 53, 53, 55, 48 },
                chord = new[] { 0, 4, 7, 11 },
                leadDuty = 0.25f, leadGain = 0.18f, bassGain = 0.22f, padGain = 0.16f,
                kickGain = 0.25f, hatGain = 0f, seed = 0xE7C4,
            },
            // Spooky groove: D harmonic-minor ostinato, tritone stab, vibrato lead.
            "haunted" => new Theme
            {
                name = key, bpm = 96f, bars = 8,
                roots = new[] { 50, 50, 51, 50, 50, 50, 51, 45 },
                chord = new[] { 0, 3, 7 },
                leadDuty = 0.5f, leadGain = 0.16f, bassGain = 0.36f, padGain = 0.10f,
                kickGain = 0.45f, hatGain = 0f, vibratoLead = true, seed = 0xBAD5,
            },
            // Garage-rock vamp: G – F – C, four-on-floor.
            "generic" => new Theme
            {
                name = key, bpm = 128f, bars = 8,
                roots = new[] { 43, 43, 41, 41, 48, 48, 43, 43 },
                chord = new[] { 0, 4, 7 },
                leadDuty = 0.5f, leadGain = 0.24f, bassGain = 0.32f, padGain = 0.08f,
                kickGain = 0.6f, hatGain = 0.12f, offbeatHats = true, seed = 0x6E4E,
            },
            // Victory lap: bright C-major fanfare arps over I – IV – V – I.
            "results" => new Theme
            {
                name = key, bpm = 120f, bars = 8,
                roots = new[] { 48, 48, 53, 53, 55, 55, 48, 48 },
                chord = new[] { 0, 4, 7 },
                leadDuty = 0.33f, leadOctaves = 2, leadGain = 0.30f,
                bassGain = 0.28f, padGain = 0.14f,
                kickGain = 0.4f, hatGain = 0.10f, offbeatHats = true, seed = 0xF1A6,
            },
            _ => null,
        };

        // ================= renderer =================

        private static AudioClip Render(Theme t)
        {
            int spb = Mathf.RoundToInt(Rate * 60f / t.bpm);          // samples per beat
            int barLen = spb * t.beatsPerBar;
            int n = barLen * t.bars;
            var d = new float[n];
            var rng = new Rng(t.seed);

            for (int bar = 0; bar < t.bars; bar++)
            {
                int root = t.roots[bar % t.roots.Length];
                int barStart = bar * barLen;

                // Bass: root pulse on every beat (haunted/downtown get the
                // driving eighth-note octave pattern instead).
                bool eighths = t.name == "downtown" || t.name == "haunted";
                int bassSteps = eighths ? t.beatsPerBar * 2 : t.beatsPerBar;
                for (int i = 0; i < bassSteps; i++)
                {
                    int start = barStart + i * (barLen / bassSteps);
                    int oct = eighths && (i & 1) == 1 ? 12 : 0;
                    AddNote(d, start, (int)(spb * (eighths ? 0.45f : 0.85f)),
                        Freq(root - 12 + oct), Wave.Triangle, 0.5f, t.bassGain, 2.2f);
                }

                // Pad: the chord sustained under the bar, two detuned squares.
                foreach (int iv in t.chord)
                {
                    AddNote(d, barStart, barLen, Freq(root + iv), Wave.Pulse, 0.5f,
                        t.padGain / t.chord.Length, 0.8f);
                    AddNote(d, barStart, barLen, Freq(root + iv) * 1.006f, Wave.Pulse, 0.5f,
                        t.padGain * 0.6f / t.chord.Length, 0.8f);
                }

                // Lead: arpeggio over the chord tones. Deterministic wander via
                // the seeded RNG (an occasional octave jump keeps it alive).
                int perBeat = t.arpTriplets ? 3 : 2;
                int steps = t.beatsPerBar * perBeat;
                for (int s = 0; s < steps; s++)
                {
                    int tone = t.chord[s % t.chord.Length];
                    int oct = 12 * t.leadOctaves + (rng.Next() > 0.82f ? 12 : 0);
                    int start = barStart + s * (barLen / steps);
                    int dur = (int)((barLen / steps) * (t.staccato ? 0.45f : 0.9f));
                    AddNote(d, start, dur, Freq(root + tone + oct), Wave.Pulse,
                        t.leadDuty, t.leadGain, t.staccato ? 3.0f : 1.6f,
                        t.vibratoLead ? 5.5f : 0f);
                }

                // Percussion.
                for (int b = 0; b < t.beatsPerBar; b++)
                {
                    int beatStart = barStart + b * spb;
                    bool kickHere = t.beatsPerBar == 3 ? b == 0 : true;   // waltz: 1 only
                    if (t.kickGain > 0f && kickHere)
                        AddKick(d, beatStart, t.kickGain);
                    if (t.hatGain > 0f)
                    {
                        int hat = t.offbeatHats ? beatStart + spb / 2 : beatStart;
                        AddHat(d, hat, t.hatGain, rng);
                    }
                }
            }

            Normalize(d, 0.72f);
            var clip = AudioClip.Create("music_" + t.name, n, 1, Rate, false);
            clip.SetData(d, 0);
            return clip;
        }

        private enum Wave { Pulse, Triangle }

        private static float Freq(int midi) => 440f * Mathf.Pow(2f, (midi - 69) / 12f);

        /// <summary>Synthesize one note into the buffer. The release rings past
        /// <paramref name="dur"/> and wraps modulo the buffer, which is what
        /// keeps the loop point silent-click free.</summary>
        private static void AddNote(float[] d, int start, int dur, float freq,
            Wave wave, float duty, float gain, float decayPow, float vibratoHz = 0f)
        {
            int tail = dur / 2;
            int total = dur + tail;
            float phase = 0f;
            for (int i = 0; i < total; i++)
            {
                float t01 = i / (float)total;
                float f = freq;
                if (vibratoHz > 0f)
                    f *= 1f + 0.012f * Mathf.Sin(2f * Mathf.PI * vibratoHz * i / Rate);
                phase += f / Rate;
                float ph = phase - Mathf.Floor(phase);
                float v = wave == Wave.Pulse
                    ? (ph < duty ? 1f : -1f)
                    : 4f * Mathf.Abs(ph - 0.5f) - 1f;
                float env = Mathf.Clamp01(i / (0.005f * Rate)) * Mathf.Pow(1f - t01, decayPow);
                d[(start + i) % d.Length] += v * env * gain;
            }
        }

        private static void AddKick(float[] d, int start, float gain)
        {
            int n = (int)(0.11f * Rate);
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t01 = i / (float)n;
                float f = Mathf.Lerp(110f, 40f, t01);          // pitched-down thump
                phase += f / Rate;
                d[(start + i) % d.Length] +=
                    Mathf.Sin(2f * Mathf.PI * phase) * Mathf.Pow(1f - t01, 1.8f) * gain;
            }
        }

        private static void AddHat(float[] d, int start, float gain, Rng rng)
        {
            int n = (int)(0.03f * Rate);
            float y = 0f;
            for (int i = 0; i < n; i++)
            {
                float t01 = i / (float)n;
                float w = rng.Next() * 2f - 1f;
                y += (w - y) * 0.35f;                           // low half…
                d[(start + i) % d.Length] += (w - y) * Mathf.Pow(1f - t01, 2.5f) * gain; // …removed
            }
        }

        private static void Normalize(float[] d, float peak)
        {
            float max = 1e-6f;
            for (int i = 0; i < d.Length; i++) max = Mathf.Max(max, Mathf.Abs(d[i]));
            float k = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        /// <summary>Same fixed-seed LCG as ProceduralAudio, in [0,1).</summary>
        private sealed class Rng
        {
            private uint _s;
            public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }
            public float Next()
            {
                _s = _s * 1664525u + 1013904223u;
                return (_s >> 8) / 16777216f;
            }
        }
    }
}
