using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using AIHWSim.Core;
using AIHWSim.Persistence;

namespace AIHWSim.Audio
{
    /// <summary>
    /// The music layer — the game's first. Lives on the persistent
    /// TinyTorqueRuntime GO (the coroutine host the project never had), owns
    /// two 2D AudioSources it crossfades between, and picks a theme per scene:
    /// menu/garage/builder play the menu theme, the drive scene plays a theme
    /// keyed off <c>TrackDesign.ambience</c> — the same key that drives the
    /// map's sky and fog, so a map sounds like it looks.
    ///
    /// HYBRID sourcing, resolved per theme key in this order:
    ///   1. an .ogg/.wav/.mp3 named after the key in &lt;save dir&gt;/Music/
    ///   2. the same in StreamingAssets/Music/ (ships with the build)
    ///   3. the built-in ProceduralMusic chiptune loop.
    /// Dropping a file in wins with zero configuration; deleting it brings the
    /// chiptune back.
    ///
    /// Volume = musicVolume × duck × fade weight. masterVolume already rides
    /// AudioListener.volume and must never be multiplied in again (the same
    /// rule SfxPlayer documents). AudioSources ignore timeScale, so music
    /// plays through pause; the pause menu ducks it instead.
    /// </summary>
    public sealed class MusicDirector : MonoBehaviour
    {
        // Theme keys — also the accepted Music-folder file names.
        public const string ThemeMenu = "menu";
        public const string ThemeGeneric = "generic";
        public const string ThemeResults = "results";

        private const float FadeSec = 1.5f;
        private const float CountdownDuck = 0.4f;
        private const float PauseDuck = 0.5f;

        /// <summary>True while the splash video owns audio.</summary>
        public static bool Suppress;

        private static MusicDirector _instance;

        private AudioSource _srcA, _srcB;
        private bool _aActive = true;
        private float _wA, _wB;                  // fade weights per source
        private string _currentKey = "";         // theme actually playing
        private string _sceneKey = "";           // theme the scene wants
        private readonly Dictionary<string, AudioClip> _fileCache =
            new Dictionary<string, AudioClip>();
        private Coroutine _switching;

        // Race mood, pushed per-frame by RaceDirector (local) and polled from
        // NetSession (LAN). Frame-stamped so a torn-down director reads false.
        private static bool _moodCounting, _moodResults;
        private static int _moodFrame = -1;
        private static bool _pauseDucked;
        private float _duck = 1f;

        public static void Attach(GameObject host)
        {
            if (_instance == null) _instance = host.AddComponent<MusicDirector>();
        }

        /// <summary>Request a theme; crossfades if it differs from what plays.</summary>
        public static void Play(string key)
        {
            if (_instance == null) return;
            _instance._sceneKey = key;
        }

        /// <summary>Local race state, pushed every frame from RaceDirector.Update.</summary>
        public static void RaceMood(bool counting, bool results)
        {
            _moodCounting = counting;
            _moodResults = results;
            _moodFrame = Time.frameCount;
        }

        /// <summary>The pause menu ducks music rather than stopping it.</summary>
        public static void SetPaused(bool paused) => _pauseDucked = paused;

        private void Awake()
        {
            _srcA = MakeSource("musicA");
            _srcB = MakeSource("musicB");
            SceneManager.sceneLoaded += OnSceneLoaded;
            // The runtime GO is created AFTER the boot scene finished loading,
            // so that scene's sceneLoaded already fired — seed from it directly
            // or the menu would sit silent until the first scene change.
            OnSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
            EnsureMusicFolder();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_instance == this) _instance = null;
        }

        private AudioSource MakeSource(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            src.spatialBlend = 0f;
            src.volume = 0f;
            return src;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == GameFlow.TrackSceneName)
                _sceneKey = ThemeFor(GameFlow.ActiveTrack);
            else if (scene.name == GameFlow.MenuSceneName
                  || scene.name == GameFlow.GarageSceneName
                  || scene.name == GameFlow.TrackBuilderSceneName)
                _sceneKey = ThemeMenu;
            // Unknown scenes (SimMain, tests): keep whatever plays.
        }

        /// <summary>Ambience key → theme key. The four themed maps carry their
        /// own songs; everything else races on the generic theme.</summary>
        public static string ThemeFor(TrackEd.TrackDesign track)
        {
            switch (track != null ? track.ambience : "")
            {
                case "downtown": return "downtown";
                case "toyroom": return "toyroom";
                case "enchanted": return "enchanted";
                case "haunted": return "haunted";
                default: return ThemeGeneric;
            }
        }

        private void Update()
        {
            // LAN mood comes from the session singleton — no per-frame pushes
            // needed from the net layer.
            var lan = Net.NetSession.Instance;
            bool counting, results;
            if (lan != null)
            {
                counting = lan.State == Net.NetSession.LanState.Countdown;
                results = lan.State == Net.NetSession.LanState.Results;
            }
            else
            {
                bool fresh = Time.frameCount - _moodFrame <= 2;
                counting = fresh && _moodCounting;
                results = fresh && _moodResults;
            }

            string want = Suppress ? _currentKey
                : results ? ThemeResults
                : _sceneKey;
            if (!Suppress && !string.IsNullOrEmpty(want) && want != _currentKey)
            {
                _currentKey = want;
                if (_switching != null) StopCoroutine(_switching);
                _switching = StartCoroutine(SwitchTo(want));
            }

            float duckTarget = _pauseDucked ? PauseDuck : (counting ? CountdownDuck : 1f);
            _duck = Mathf.MoveTowards(_duck, duckTarget, Time.unscaledDeltaTime * 2f);

            float vol = Mathf.Clamp01(SettingsStore.Current.musicVolume) * _duck;
            if (_srcA != null) _srcA.volume = vol * _wA;
            if (_srcB != null) _srcB.volume = vol * _wB;
        }

        private IEnumerator SwitchTo(string key)
        {
            AudioClip clip = null;
            yield return Resolve(key, c => clip = c);
            if (clip == null) yield break;   // silence beats a broken loop

            // Flip which source is "active" and fade across.
            _aActive = !_aActive;
            var to = _aActive ? _srcA : _srcB;
            var from = _aActive ? _srcB : _srcA;
            to.clip = clip;
            to.Play();

            float fromStart = _aActive ? _wB : _wA;
            float toStart = _aActive ? _wA : _wB;
            for (float t = 0f; t < FadeSec; t += Time.unscaledDeltaTime)
            {
                float k = t / FadeSec;
                float wTo = Mathf.Lerp(toStart, 1f, k);
                float wFrom = Mathf.Lerp(fromStart, 0f, k);
                if (_aActive) { _wA = wTo; _wB = wFrom; }
                else { _wB = wTo; _wA = wFrom; }
                yield return null;
            }
            if (_aActive) { _wA = 1f; _wB = 0f; } else { _wB = 1f; _wA = 0f; }
            from.Stop();
            _switching = null;
        }

        /// <summary>File override → procedural fallback. Files are loaded once
        /// through UnityWebRequestMultimedia and cached for the session.</summary>
        private IEnumerator Resolve(string key, System.Action<AudioClip> done)
        {
            if (_fileCache.TryGetValue(key, out var cached) && cached != null)
            {
                done(cached);
                yield break;
            }

            string[] dirs =
            {
                Path.Combine(AppPaths.BaseDir, "Music"),
                Path.Combine(Application.streamingAssetsPath, "Music"),
            };
            string[] exts = { ".ogg", ".wav", ".mp3" };
            foreach (var dir in dirs)
                foreach (var ext in exts)
                {
                    string path = Path.Combine(dir, key + ext);
                    if (!File.Exists(path)) continue;

                    var type = ext == ".ogg" ? AudioType.OGGVORBIS
                        : ext == ".wav" ? AudioType.WAV : AudioType.MPEG;
                    using var req = UnityWebRequestMultimedia.GetAudioClip(
                        "file:///" + path.Replace('\\', '/'), type);
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        var clip = DownloadHandlerAudioClip.GetContent(req);
                        if (clip != null)
                        {
                            _fileCache[key] = clip;
                            done(clip);
                            yield break;
                        }
                    }
                    Debug.LogWarning($"[Music] failed to load {path} ({req.error}) — trying next.");
                }

            done(ProceduralMusic.Get(key));
        }

        /// <summary>First run: create the drop-in folder next to Saves with a
        /// how-to, so the feature is discoverable from the file system.</summary>
        private static void EnsureMusicFolder()
        {
            try
            {
                string dir = Path.Combine(AppPaths.BaseDir, "Music");
                if (Directory.Exists(dir)) return;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "README.txt"),
                    "Drop .ogg/.wav/.mp3 files here to replace the built-in music.\n" +
                    "Names: menu, generic, downtown, toyroom, enchanted, haunted, results.\n" +
                    "Example: downtown.mp3 plays on Downtown Dash. Files here win over\n" +
                    "the ones shipped in StreamingAssets/Music. Songs loop.\n");
            }
            catch { /* an unwritable disk must never block audio */ }
        }
    }
}
