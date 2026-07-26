using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.Audio
{
    /// <summary>
    /// One-shot sound playback: a small pool of positional sources plus one
    /// non-positional source for "this happened to you".
    ///
    /// Pooled rather than <c>AudioSource.PlayClipAtPoint</c>, which allocates and
    /// destroys a GameObject per call — fine once, wasteful for a race where
    /// pickups, hits and explosions fire constantly.
    ///
    /// Scene-local and created on demand: it holds no state worth carrying across
    /// a scene load, and a stale DontDestroyOnLoad audio object outliving its
    /// listener is a classic source of sound that plays from nowhere.
    ///
    /// Volume is <c>sfxVolume</c> here; <c>masterVolume</c> is already applied
    /// globally by SettingsStore through AudioListener.volume, so it must NOT be
    /// multiplied in again.
    /// </summary>
    public sealed class SfxPlayer : MonoBehaviour
    {
        /// <summary>Global off switch, matching PartMeshLibrary.Enabled /
        /// TyreModel.Enabled.</summary>
        public static bool Enabled = true;

        private const int PoolSize = 10;
        /// <summary>Below this the 3D falloff starts. RC scale: a car is 0.42 m
        /// and the chase camera sits ~2 m back, so the default 1 m would make
        /// your own car's sounds lurch in volume as the camera breathes.</summary>
        private const float MinDistance = 3f;
        private const float MaxDistance = 45f;

        private static SfxPlayer _instance;

        private AudioSource[] _pool;
        private int _next;
        private AudioSource _ui;

        /// <summary>The scene's player, created if this is the first call.
        /// Null only when audio is disabled.</summary>
        public static SfxPlayer Ensure()
        {
            if (!Enabled) return null;
            if (_instance != null) return _instance;
            var go = new GameObject("SfxPlayer");
            _instance = go.AddComponent<SfxPlayer>();
            return _instance;
        }

        public static SfxPlayer Instance => _instance;

        private void Awake()
        {
            _instance = this;

            _pool = new AudioSource[PoolSize];
            for (int i = 0; i < PoolSize; i++)
            {
                var child = new GameObject($"sfx{i}");
                child.transform.SetParent(transform, false);
                _pool[i] = Configure(child.AddComponent<AudioSource>(), spatial: true);
            }

            var uiGo = new GameObject("sfx2d");
            uiGo.transform.SetParent(transform, false);
            _ui = Configure(uiGo.AddComponent<AudioSource>(), spatial: false);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        /// <summary>Shared source setup. Also used by <see cref="VehicleAudio"/>
        /// for its loops, so every source in the game rolls off identically.</summary>
        public static AudioSource Configure(AudioSource src, bool spatial)
        {
            src.playOnAwake = false;
            src.loop = false;
            // Doppler off deliberately: a chase camera that breathes relative to
            // its own car turns doppler into a constant warble, which reads as a
            // bug rather than as speed.
            src.dopplerLevel = 0f;
            src.spatialBlend = spatial ? 1f : 0f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = MinDistance;
            src.maxDistance = MaxDistance;
            return src;
        }

        public static float SfxGain => Mathf.Clamp01(SettingsStore.Current.sfxVolume);

        /// <summary>Play positionally in the world.</summary>
        public void PlayAt(string key, Vector3 pos, float volume = 1f, float pitch = 1f)
        {
            if (!Enabled || Time.timeScale <= 0f) return;      // paused: stay quiet
            var clip = ProceduralAudio.Get(key);
            if (clip == null) return;

            var src = _pool[_next];
            _next = (_next + 1) % PoolSize;
            src.transform.position = pos;
            src.clip = clip;
            src.volume = Mathf.Clamp01(volume) * SfxGain;
            src.pitch = pitch;
            src.Play();
        }

        /// <summary>Play flat, for events that happened to the local player and
        /// must be heard regardless of where the camera is.</summary>
        public void Play2D(string key, float volume = 1f, float pitch = 1f)
        {
            if (!Enabled || Time.timeScale <= 0f) return;
            var clip = ProceduralAudio.Get(key);
            if (clip == null) return;

            _ui.pitch = pitch;
            _ui.PlayOneShot(clip, Mathf.Clamp01(volume) * SfxGain);
        }
    }
}
