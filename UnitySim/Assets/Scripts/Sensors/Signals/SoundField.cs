using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Sensors.Signals
{
    /// <summary>One receiver slot from <see cref="SoundField.StrongestAt"/>.
    /// Empty slot sentinel: id = -1, level = 0, toneHz = 0.</summary>
    public struct SoundReading
    {
        public int id;
        public float level;
        public float toneHz;
    }

    /// <summary>
    /// The simulated acoustic field: a static registry of emitters queried by
    /// receivers at sample time. Deliberately not a MonoBehaviour (the
    /// TutorialSignals idiom) — emitters register from OnEnable/OnDisable, and
    /// <see cref="Reset"/> is called once per session by the world sensor host.
    ///
    /// Determinism: iteration is insertion order (a List), which is stable for
    /// a given track + design — the same assumption NoiseModel ordinals already
    /// rest on. Queries mutate nothing, so being read from both vehicle control
    /// ticks and the world hub tick cannot skew results.
    ///
    /// Falloff: inverse-square in linear amplitude, reference at 1 m, clamped
    /// so a mic sitting on a speaker reads the emitter's loudness, never
    /// infinity. No occlusion (see ISoundEmitter).
    /// </summary>
    public static class SoundField
    {
        private static readonly List<ISoundEmitter> _emitters = new List<ISoundEmitter>();
        private static int _nextId = 1;

        public static IReadOnlyList<ISoundEmitter> Emitters => _emitters;

        /// <summary>Register an emitter and assign its monotonic id. Safe to
        /// call twice (re-enable); the id sticks for the emitter's lifetime.</summary>
        public static int Register(ISoundEmitter e)
        {
            if (e == null) return -1;
            if (e.SoundEmitterId <= 0) e.SoundEmitterId = _nextId++;
            if (!_emitters.Contains(e)) _emitters.Add(e);
            return e.SoundEmitterId;
        }

        public static void Unregister(ISoundEmitter e)
        {
            if (e != null) _emitters.Remove(e);
            // Registry drains at scene teardown; restart the id sequence so ids
            // stay small and reproducible run-to-run.
            if (_emitters.Count == 0) _nextId = 1;
        }

        /// <summary>Clear the registry and id counter (world host Awake — a
        /// scene reload starts a fresh field).</summary>
        public static void Reset()
        {
            _emitters.Clear();
            _nextId = 1;
        }

        /// <summary>Perceived level of one emitter at a point: inverse-square
        /// with a 1 m near clamp.</summary>
        public static float LevelFrom(ISoundEmitter e, Vector3 pos)
        {
            if (e == null || !e.SoundActive) return 0f;
            float d2 = (e.SoundPosition - pos).sqrMagnitude;
            return e.Loudness / Mathf.Max(1f, d2);
        }

        /// <summary>Total linear level at a point (sum over active emitters,
        /// insertion order — stable float summation).</summary>
        public static float LevelAt(Vector3 pos)
        {
            float sum = 0f;
            for (int i = 0; i < _emitters.Count; i++)
                sum += LevelFrom(_emitters[i], pos);
            return sum;
        }

        /// <summary>
        /// Strongest-k sources at a point, sorted by level descending with id
        /// ascending as the deterministic tie-break. Fills the first k entries
        /// of dest (empty slots get the sentinel) and returns how many were
        /// found. No allocation.
        /// </summary>
        public static int StrongestAt(Vector3 pos, int k, SoundReading[] dest)
        {
            k = Mathf.Min(k, dest.Length);
            for (int i = 0; i < k; i++)
                dest[i] = new SoundReading { id = -1, level = 0f, toneHz = 0f };

            int found = 0;
            for (int i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e == null || !e.SoundActive) continue;
                float level = LevelFrom(e, pos);
                if (level <= 0f) continue;

                // Insertion sort into the fixed slots: level desc, id asc.
                for (int s = 0; s < k; s++)
                {
                    bool wins = dest[s].id < 0
                        || level > dest[s].level
                        || (level == dest[s].level && e.SoundEmitterId < dest[s].id);
                    if (!wins) continue;
                    for (int t = k - 1; t > s; t--) dest[t] = dest[t - 1];
                    dest[s] = new SoundReading { id = e.SoundEmitterId, level = level, toneHz = e.ToneHz };
                    break;
                }
                found++;
            }
            return Mathf.Min(found, k);
        }
    }
}
