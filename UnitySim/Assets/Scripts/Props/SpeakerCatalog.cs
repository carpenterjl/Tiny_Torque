using AIHWSim.Audio;

namespace AIHWSim.Props
{
    /// <summary>
    /// The sounds a speaker prop can play — a curated table, not the full
    /// ProceduralAudio key set, because every entry must be loopable and must
    /// carry an honest dominant tone for the simulated sound field. The tone
    /// is what a microphone reports as the source's signature.
    /// </summary>
    public static class SpeakerCatalog
    {
        public struct Entry
        {
            public string clipKey;
            public string label;
            /// <summary>Dominant tone reported to the sound field (Hz).</summary>
            public float toneHz;
            /// <summary>Default SoundField loudness at 1 m.</summary>
            public float loudness;
        }

        public const string DefaultKey = ProceduralAudio.ToneA;

        public static readonly Entry[] Entries =
        {
            new Entry { clipKey = ProceduralAudio.ToneA, label = "Tone A (440 Hz)", toneHz = 440f, loudness = 1f },
            new Entry { clipKey = ProceduralAudio.ToneB, label = "Tone B (880 Hz)", toneHz = 880f, loudness = 1f },
            new Entry { clipKey = ProceduralAudio.ToneC, label = "Tone C (1.76 kHz)", toneHz = 1760f, loudness = 1f },
            new Entry { clipKey = ProceduralAudio.HornMusical, label = "Fanfare", toneHz = 523f, loudness = 1.2f },
            new Entry { clipKey = ProceduralAudio.HornSiren, label = "Siren", toneHz = 725f, loudness = 1.5f },
            new Entry { clipKey = ProceduralAudio.WarnBeep, label = "Warning beep", toneHz = 1000f, loudness = 0.8f },
        };

        /// <summary>Entry for a clip key; unknown keys fall back to Tone A so a
        /// hand-edited layout file never yields a silent speaker.</summary>
        public static Entry Find(string clipKey)
        {
            for (int i = 0; i < Entries.Length; i++)
                if (Entries[i].clipKey == clipKey) return Entries[i];
            return Entries[0];
        }
    }
}
