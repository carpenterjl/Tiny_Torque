using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Audio
{
    /// <summary>
    /// Every sound in the game, synthesized at runtime and cached by name.
    ///
    /// The project ships no audio assets and never has — meshes, textures and
    /// materials are all generated in code, and sound follows the same rule. That
    /// is not a limitation dressed up as a principle: it means a clip is a few
    /// numbers in a build script rather than a binary someone has to find,
    /// license and keep in sync, and it keeps the repo diffable.
    ///
    /// Two rules matter for anything that loops:
    ///   * a tonal loop must contain a WHOLE number of cycles, or the wrap is a
    ///     phase discontinuity and you hear a click every period;
    ///   * a noise loop has no cycles to align, so its head is cross-faded with
    ///     an overlapping tail instead.
    /// Both are handled below; get either wrong and it is instantly audible.
    ///
    /// Noise uses a fixed-seed generator, so a given clip is bit-identical every
    /// run — the same determinism the sensor noise models are held to.
    /// </summary>
    public static class ProceduralAudio
    {
        private static readonly Dictionary<string, AudioClip> _cache =
            new Dictionary<string, AudioClip>();

        private static int _sampleRate;

        private static int Rate
        {
            get
            {
                if (_sampleRate > 0) return _sampleRate;
                _sampleRate = AudioSettings.outputSampleRate;
                if (_sampleRate <= 0) _sampleRate = 44100;   // batchmode / no device
                return _sampleRate;
            }
        }

        // ---- clip keys ----
        public const string Pickup = "pickup";
        public const string Reveal = "reveal";
        public const string Boost = "boost";
        public const string BoostLoop = "boost_loop";
        public const string MissileFire = "missile_fire";
        public const string Explosion = "explosion";
        public const string BananaDrop = "banana_drop";
        public const string Smoke = "smoke";
        public const string Spin = "spin";
        public const string ShieldUp = "shield_up";
        public const string ShieldBlock = "shield_block";
        public const string WarnBeep = "warn_beep";
        public const string Engine = "engine";
        public const string Skid = "skid";
        public const string Impact = "impact";

        /// <summary>Fetch (building on first use). Null only if the key is unknown.</summary>
        public static AudioClip Get(string key)
        {
            if (_cache.TryGetValue(key, out var clip)) return clip;
            clip = Build(key);
            _cache[key] = clip;      // cache misses too, so a bad key probes once
            return clip;
        }

        private static AudioClip Build(string key) => key switch
        {
            Pickup => BuildPickup(),
            Reveal => BuildReveal(),
            Boost => BuildBoost(),
            BoostLoop => BuildBoostLoop(),
            MissileFire => BuildMissileFire(),
            Explosion => BuildExplosion(),
            BananaDrop => BuildBananaDrop(),
            Smoke => BuildSmoke(),
            Spin => BuildSpin(),
            ShieldUp => BuildShieldUp(),
            ShieldBlock => BuildShieldBlock(),
            WarnBeep => BuildWarnBeep(),
            Engine => BuildEngine(),
            Skid => BuildSkid(),
            Impact => BuildImpact(),
            _ => null,
        };

        // ================= synthesis primitives =================

        /// <summary>Deterministic white noise in [-1, 1]. A plain LCG rather than
        /// UnityEngine.Random so building a clip never disturbs, or depends on,
        /// the global random sequence the sim's noise models use.</summary>
        private sealed class Rng
        {
            private uint _s;
            public Rng(uint seed) { _s = seed == 0 ? 1u : seed; }
            public float Next()
            {
                _s = _s * 1664525u + 1013904223u;
                return (_s >> 8) / 8388608f - 1f;    // 24-bit mantissa → [-1, 1)
            }
        }

        private static AudioClip Make(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static int Samples(float seconds) => Mathf.Max(1, Mathf.RoundToInt(seconds * Rate));

        /// <summary>Attack/decay envelope, both as a fraction of the clip.</summary>
        private static float Env(float t01, float attack, float decayPower)
        {
            float a = attack <= 0f ? 1f : Mathf.Clamp01(t01 / attack);
            float d = Mathf.Pow(1f - t01, decayPower);
            return a * d;
        }

        /// <summary>One-pole low-pass, in place. cutoff01 ~ how much passes per
        /// sample (1 = no filtering).</summary>
        private static void LowPass(float[] d, float cutoff01)
        {
            float y = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                y += (d[i] - y) * cutoff01;
                d[i] = y;
            }
        }

        /// <summary>
        /// Two-pole resonator, in place: rings at <paramref name="freq"/> with
        /// sharpness <paramref name="q"/>.
        ///
        /// This is what turns noise into a tyre rather than a hiss. Rubber
        /// breaking away is a stick-slip oscillation — the contact patch grips,
        /// releases and re-grips at an audible rate — so the sound has strong
        /// formants, not a flat spectrum. A low-pass alone can only make white
        /// noise duller; a resonator gives it a pitch.
        /// </summary>
        private static void Resonate(float[] d, float freq, float q)
        {
            float w = 2f * Mathf.PI * freq / Rate;
            float r = Mathf.Exp(-w / (2f * Mathf.Max(0.5f, q)));
            float a1 = 2f * r * Mathf.Cos(w);
            float a2 = -r * r;
            float gain = 1f - r;
            float y1 = 0f, y2 = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float y = d[i] * gain + a1 * y1 + a2 * y2;
                y2 = y1; y1 = y;
                d[i] = y;
            }
        }

        private static void Normalize(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++) max = Mathf.Max(max, Mathf.Abs(d[i]));
            if (max < 1e-6f) return;
            float g = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= g;
        }

        /// <summary>
        /// Cross-fade the head of a noise buffer with an overlapping tail so the
        /// loop wraps without a seam. <paramref name="raw"/> must be
        /// <paramref name="n"/> + <paramref name="fade"/> samples long; the first
        /// n are returned.
        /// </summary>
        private static float[] LoopFade(float[] raw, int n, int fade)
        {
            var outp = new float[n];
            for (int i = 0; i < n; i++) outp[i] = raw[i];
            for (int i = 0; i < fade && i < n; i++)
            {
                float t = i / (float)fade;
                outp[i] = raw[i] * t + raw[n + i] * (1f - t);
            }
            return outp;
        }

        /// <summary>Exact cycle count for a seamless tonal loop: snap the request
        /// to the nearest whole number of periods in the buffer.</summary>
        private static float LoopCycles(float freq, float seconds)
        {
            return Mathf.Max(1f, Mathf.Round(freq * seconds));
        }

        // ================= the clips =================

        private static AudioClip BuildPickup()
        {
            int n = Samples(0.16f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(620f, 1180f, t);        // rising blip
                float ph = 2f * Mathf.PI * f * i / Rate;
                d[i] = (Mathf.Sin(ph) * 0.7f + Mathf.Sin(ph * 2f) * 0.3f) * Env(t, 0.06f, 1.8f);
            }
            Normalize(d, 0.85f);
            return Make(Pickup, d);
        }

        private static AudioClip BuildReveal()
        {
            // Three ascending notes — "the roulette landed on something".
            float[] notes = { 660f, 880f, 1320f };
            int per = Samples(0.09f);
            var d = new float[per * notes.Length];
            for (int k = 0; k < notes.Length; k++)
                for (int i = 0; i < per; i++)
                {
                    float t = i / (float)per;
                    float ph = 2f * Mathf.PI * notes[k] * i / Rate;
                    d[k * per + i] = Mathf.Sin(ph) * Env(t, 0.08f, 1.4f);
                }
            Normalize(d, 0.8f);
            return Make(Reveal, d);
        }

        private static AudioClip BuildBoost()
        {
            int n = Samples(0.55f);
            var d = new float[n];
            var rng = new Rng(0xB005);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(180f, 900f, t * t);      // whoosh up
                float ph = 2f * Mathf.PI * f * i / Rate;
                float tone = Mathf.Sin(ph) * 0.55f;
                float air = rng.Next() * 0.45f;
                d[i] = (tone + air) * Env(t, 0.12f, 1.2f);
            }
            LowPass(d, 0.35f);
            Normalize(d, 0.9f);
            return Make(Boost, d);
        }

        private static AudioClip BuildBoostLoop()
        {
            float secs = 0.5f;
            int n = Samples(secs);
            int fade = Samples(0.05f);
            var raw = new float[n + fade];
            var rng = new Rng(0xB1007);
            // Low turbine-ish rumble: a tonal core (whole cycles, so it wraps)
            // plus filtered noise (cross-faded, because noise cannot wrap).
            float cyc = LoopCycles(140f, secs);
            for (int i = 0; i < raw.Length; i++)
            {
                float ph = 2f * Mathf.PI * cyc * (i / (float)n);
                raw[i] = Mathf.Sin(ph) * 0.5f + Mathf.Sin(ph * 2f) * 0.2f + rng.Next() * 0.4f;
            }
            LowPass(raw, 0.25f);
            var d = LoopFade(raw, n, fade);
            Normalize(d, 0.75f);
            return Make(BoostLoop, d);
        }

        private static AudioClip BuildMissileFire()
        {
            int n = Samples(0.45f);
            var d = new float[n];
            var rng = new Rng(0x5A1D);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(900f, 220f, t);          // launch, falling away
                float ph = 2f * Mathf.PI * f * i / Rate;
                d[i] = (Mathf.Sin(ph) * 0.5f + rng.Next() * 0.5f) * Env(t, 0.04f, 1.1f);
            }
            LowPass(d, 0.5f);
            Normalize(d, 0.9f);
            return Make(MissileFire, d);
        }

        private static AudioClip BuildExplosion()
        {
            int n = Samples(0.75f);
            var d = new float[n];
            var rng = new Rng(0xE7B10);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                d[i] = rng.Next() * Env(t, 0.01f, 2.2f);
            }
            LowPass(d, 0.12f);                                // body, not hiss
            // A low thump under the noise gives it weight at RC scale.
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(150f, 45f, t);
                d[i] += Mathf.Sin(2f * Mathf.PI * f * i / Rate) * Env(t, 0.01f, 3f) * 0.8f;
            }
            Normalize(d, 1f);
            return Make(Explosion, d);
        }

        private static AudioClip BuildBananaDrop()
        {
            int n = Samples(0.18f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(320f, 120f, t);          // soft plop
                d[i] = Mathf.Sin(2f * Mathf.PI * f * i / Rate) * Env(t, 0.05f, 2.5f);
            }
            Normalize(d, 0.6f);
            return Make(BananaDrop, d);
        }

        /// <summary>
        /// The smoke cloud going off: a pressure release, not steam and not an
        /// explosion. Three deliberate departures from the neighbouring clips:
        ///
        ///   * a very low cutoff (0.06, against BuildBoost's 0.35) is what makes
        ///     it read as breath rather than hiss — above roughly 400 Hz there is
        ///     no version of this that isn't a leaking valve;
        ///   * ONE LOW-Q resonator, the exact opposite of BuildSkid's sharp pair.
        ///     A high Q would give it a pitch, and a pitched fart is a kazoo;
        ///     low Q gives body without a note;
        ///   * a slow attack (0.18, against BuildImpact's 0.005) — the attack
        ///     alone is the difference between a release of pressure and a
        ///     gunshot, whatever the spectrum underneath is doing.
        ///
        /// A one-shot, not a loop, so none of the wrap machinery applies.
        /// </summary>
        private static AudioClip BuildSmoke()
        {
            int n = Samples(0.85f);
            var d = new float[n];
            var rng = new Rng(0x5D0CE);
            for (int i = 0; i < n; i++) d[i] = rng.Next();

            LowPass(d, 0.06f);
            Resonate(d, 95f, 2.5f);

            // A slow downward flutter over the top: the pitch of a release drops
            // as the pressure behind it does, and without it this is just a puff.
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float flutter = 1f + 0.35f * Mathf.Sin(2f * Mathf.PI * Mathf.Lerp(26f, 11f, t) * t);
                d[i] *= flutter * Env(t, 0.18f, 1.6f);
            }
            Normalize(d, 0.8f);
            return Make(Smoke, d);
        }

        private static AudioClip BuildSpin()
        {
            // Tyre screech: narrow-ish noise with a slow amplitude wobble.
            int n = Samples(0.9f);
            var d = new float[n];
            var rng = new Rng(0x5C4EE);
            for (int i = 0; i < n; i++) d[i] = rng.Next();
            LowPass(d, 0.30f);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float wobble = 0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * 7f * t);
                d[i] *= wobble * Env(t, 0.05f, 1.3f);
            }
            Normalize(d, 0.8f);
            return Make(Spin, d);
        }

        private static AudioClip BuildShieldUp()
        {
            int n = Samples(0.5f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(300f, 900f, t);
                // Two slightly detuned partials shimmer rather than whistle.
                d[i] = (Mathf.Sin(2f * Mathf.PI * f * i / Rate) +
                        Mathf.Sin(2f * Mathf.PI * f * 1.01f * i / Rate) * 0.7f) *
                       Env(t, 0.25f, 1.0f);
            }
            Normalize(d, 0.7f);
            return Make(ShieldUp, d);
        }

        private static AudioClip BuildShieldBlock()
        {
            int n = Samples(0.35f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float ph = 2f * Mathf.PI * 1500f * i / Rate;
                d[i] = (Mathf.Sin(ph) + Mathf.Sin(ph * 1.5f) * 0.5f) * Env(t, 0.005f, 2.5f);
            }
            Normalize(d, 0.9f);
            return Make(ShieldBlock, d);
        }

        private static AudioClip BuildWarnBeep()
        {
            int n = Samples(0.10f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float s = Mathf.Sin(2f * Mathf.PI * 1000f * i / Rate);
                d[i] = Mathf.Sign(s) * 0.5f * Env(t, 0.05f, 0.8f);   // square: urgent
            }
            Normalize(d, 0.75f);
            return Make(WarnBeep, d);
        }

        private static AudioClip BuildEngine()
        {
            // Brushed-motor whine: a saw-ish stack of harmonics. Played back with
            // AudioSource.pitch scaled from motor speed, so the base frequency
            // here is only a reference.
            float secs = 0.25f;
            int n = Samples(secs);
            var d = new float[n];
            // Whole cycles → seamless, and an EVEN count so the sub-octave below
            // completes a whole number of its own cycles too. An odd count would
            // leave the sub half a period out at the wrap and click every loop.
            float cyc = LoopCycles(EngineBaseHz, secs);
            cyc = Mathf.Max(2f, Mathf.Round(cyc * 0.5f) * 2f);
            for (int i = 0; i < n; i++)
            {
                float ph = 2f * Mathf.PI * cyc * (i / (float)n);
                d[i] = Mathf.Sin(ph * 0.5f) * 0.40f            // sub-octave: the weight
                     + Mathf.Sin(ph) * 0.50f
                     + Mathf.Sin(ph * 2f) * 0.20f
                     + Mathf.Sin(ph * 3f) * 0.10f
                     + Mathf.Sin(ph * 5f) * 0.05f;             // the electric edge
            }
            Normalize(d, 0.7f);
            return Make(Engine, d);
        }

        /// <summary>Reference frequency of the <see cref="Engine"/> loop. Callers
        /// pitch against this, so it must not change without retuning them.</summary>
        public const float EngineBaseHz = 85f;

        /// <summary>
        /// Tyres letting go: a burnout squeal, not a hiss.
        ///
        /// A sliding tyre is a stick-slip oscillator — the contact patch grabs,
        /// tears loose and grabs again — so the sound is a strong, slightly
        /// unstable pitch with a rubber growl under it, and filtered white noise
        /// cannot get there however it is shaped. Three parts:
        ///   * a vibrato'd squeal tone, which carries the character;
        ///   * two sharp resonators on noise, giving the grain a pitch centre;
        ///   * one low resonator for the rubber growl and the weight.
        ///
        /// The tonal parts are built on whole cycles of the loop so they wrap
        /// exactly; the noise parts cannot, and are cross-faded like every other
        /// noise loop here.
        /// </summary>
        private static AudioClip BuildSkid()
        {
            float secs = 0.7f;
            int n = Samples(secs);
            int fade = Samples(0.06f);

            var noise = new float[n + fade];
            var rng = new Rng(0x5C1D);
            for (int i = 0; i < noise.Length; i++) noise[i] = rng.Next();

            var hi = (float[])noise.Clone(); Resonate(hi, 1290f, 14f);
            var mid = (float[])noise.Clone(); Resonate(mid, 820f, 22f);
            var low = (float[])noise.Clone(); Resonate(low, 150f, 5f);
            Normalize(hi, 0.55f);
            Normalize(mid, 1f);
            Normalize(low, 0.6f);

            // The squeal itself. The vibrato is what stops it reading as a test
            // tone: a real slide wanders in pitch as load moves around the patch.
            float cyc = Mathf.Max(1f, Mathf.Round(760f * secs));
            float vibCyc = Mathf.Max(1f, Mathf.Round(11f * secs));

            var raw = new float[n + fade];
            for (int i = 0; i < raw.Length; i++)
            {
                float x = i / (float)n;
                float ph = 2f * Mathf.PI * cyc * x +
                           0.55f * Mathf.Sin(2f * Mathf.PI * vibCyc * x);
                float squeal = Mathf.Sin(ph) * 0.5f + Mathf.Sin(ph * 2f) * 0.16f;
                raw[i] = squeal * 0.85f + mid[i] + hi[i] + low[i];
            }

            var d = LoopFade(raw, n, fade);
            Normalize(d, 0.8f);
            return Make(Skid, d);
        }

        private static AudioClip BuildImpact()
        {
            int n = Samples(0.28f);
            var d = new float[n];
            var rng = new Rng(0x11AC7);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)n;
                float f = Mathf.Lerp(220f, 70f, t);           // the thud
                d[i] = Mathf.Sin(2f * Mathf.PI * f * i / Rate) * Env(t, 0.005f, 2.2f)
                     + rng.Next() * Env(t, 0.002f, 6f) * 0.6f; // the clack
            }
            Normalize(d, 0.95f);
            return Make(Impact, d);
        }
    }
}
