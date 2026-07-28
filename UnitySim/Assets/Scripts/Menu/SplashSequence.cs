using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using UnityEngine;
using UnityEngine.Video;

namespace AIHWSim.Menu
{
    /// <summary>
    /// Boot intro: the TinyTorque video (with its own soundtrack), then the
    /// title card with a pulsing PRESS ANY BUTTON, then the menu. Any input
    /// skips forward. Runs once per app launch (<see cref="ShownThisBoot"/>) —
    /// coming back from a race goes straight to the menu.
    ///
    /// The video plays from StreamingAssets through a VideoPlayer built
    /// entirely from code (no prefab, no scene object): decoded into a
    /// RenderTexture and drawn letterboxed by IMGUI, with audio routed through
    /// an AudioSource so the master volume slider applies (Direct mode would
    /// bypass AudioListener.volume). Every failure path — module missing a
    /// codec, file deleted, prepare timeout — advances to the title card
    /// instead of blocking: the splash is theatre, never a gate.
    /// </summary>
    public sealed class SplashSequence : MonoBehaviour
    {
        public static bool ShownThisBoot;

        private const string VideoFile = "TinyTorque_Intro.mp4";
        private const float PrepareTimeout = 6f;
        private const float TitleMinDwell = 0.4f;   // so mashing past the video can't skip the title unseen

        private enum Phase { Video, Title, Done }

        private Phase _phase = Phase.Video;
        private System.Action _onFinished;
        private VideoPlayer _vp;
        private RenderTexture _rt;
        private Texture2D _title;
        private float _phaseStart;

        /// <summary>Start the sequence on <paramref name="host"/>; the callback
        /// fires exactly once when the splash is done (or was skipped).</summary>
        public static void Run(GameObject host, System.Action onFinished)
        {
            var s = host.AddComponent<SplashSequence>();
            s._onFinished = onFinished;
        }

        private void Start()
        {
            ShownThisBoot = true;
            Audio.MusicDirector.Suppress = true;   // the video owns audio until Finish
            _title = Resources.Load<Texture2D>("UI/TinyTorque_Title");
            _phaseStart = Time.unscaledTime;

            string path = System.IO.Path.Combine(Application.streamingAssetsPath, VideoFile);
            if (!System.IO.File.Exists(path)) { EnterTitle(); return; }

            var src = gameObject.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 0.85f;

            _vp = gameObject.AddComponent<VideoPlayer>();
            _vp.playOnAwake = false;
            _vp.source = VideoSource.Url;
            _vp.url = path;
            _vp.renderMode = VideoRenderMode.RenderTexture;
            _vp.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _vp.controlledAudioTrackCount = 1;
            _vp.EnableAudioTrack(0, true);
            _vp.SetTargetAudioSource(0, src);
            _vp.isLooping = false;
            _vp.prepareCompleted += p =>
            {
                if (_phase != Phase.Video) return;
                _rt = new RenderTexture((int)p.width, (int)p.height, 0);
                p.targetTexture = _rt;
                p.Play();
            };
            _vp.loopPointReached += _ => EnterTitle();
            _vp.errorReceived += (_, msg) =>
            {
                Debug.LogWarning($"[Splash] video failed ({msg}) — skipping to title.");
                EnterTitle();
            };
            _vp.Prepare();
        }

        private void Update()
        {
            switch (_phase)
            {
                case Phase.Video:
                    // A player that never prepares (codec trouble) must not
                    // hold the game on a black screen.
                    if (!_vp.isPlaying && Time.unscaledTime - _phaseStart > PrepareTimeout)
                        EnterTitle();
                    else if (AnyInput())
                        EnterTitle();
                    break;
                case Phase.Title:
                    if (Time.unscaledTime - _phaseStart > TitleMinDwell && AnyInput())
                        Finish();
                    break;
            }
        }

        private static bool AnyInput() =>
            KeyTable.CaptureThisFrame() != KeyCode.None ||
            PadTable.CaptureThisFrame() != PadButton.None ||
            InputReader.LeftMousePressed();

        private void EnterTitle()
        {
            if (_phase != Phase.Video) return;
            _phase = Phase.Title;
            _phaseStart = Time.unscaledTime;
            TearDownVideo();
            if (_title == null) Finish();   // no card to show — straight to the menu
        }

        private void Finish()
        {
            if (_phase == Phase.Done) return;
            _phase = Phase.Done;
            Audio.MusicDirector.Suppress = false;  // menu theme fades in behind the dip
            var done = _onFinished;
            _onFinished = null;
            ScreenFade.Dip(() =>
            {
                done?.Invoke();
                Destroy(this);
            });
        }

        private void TearDownVideo()
        {
            if (_vp != null) { _vp.Stop(); Destroy(_vp); _vp = null; }
            var src = GetComponent<AudioSource>();
            if (src != null) Destroy(src);
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }

        private void OnDestroy() => TearDownVideo();

        private void OnGUI()
        {
            if (_phase == Phase.Done) return;
            GUI.depth = -900;   // above the menu/attract, below only the fade
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();

            // Solid black behind everything — the attract loop is running
            // beneath and must not peek through the letterbox.
            var black = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, UIScale.W, UIScale.H), Texture2D.whiteTexture);
            GUI.color = black;

            if (_phase == Phase.Video && _rt != null)
            {
                GUI.DrawTexture(Fit(_rt.width, _rt.height), _rt, ScaleMode.StretchToFill);
            }
            else if (_phase == Phase.Title && _title != null)
            {
                GUI.DrawTexture(Cover(_title.width, _title.height), _title, ScaleMode.StretchToFill);

                float pulse = 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 3.2f);
                var style = new GUIStyle(GarageSkin.Title) { fontSize = 22 };
                var c = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, pulse);
                GUI.Label(new Rect(0f, UIScale.H * 0.86f, UIScale.W, 30f), "PRESS ANY BUTTON", style);
                GUI.color = c;
            }

            UIScale.End();
        }

        /// <summary>Largest rect of the given aspect that fits on screen
        /// (letterbox), centred, in UI units.</summary>
        private static Rect Fit(float tw, float th)
        {
            float s = Mathf.Min(UIScale.W / tw, UIScale.H / th);
            float w = tw * s, h = th * s;
            return new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
        }

        /// <summary>Smallest rect of the given aspect that covers the screen
        /// (crop overflow), centred, in UI units.</summary>
        private static Rect Cover(float tw, float th)
        {
            float s = Mathf.Max(UIScale.W / tw, UIScale.H / th);
            float w = tw * s, h = th * s;
            return new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
        }
    }
}
