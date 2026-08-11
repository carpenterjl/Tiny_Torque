using System.Collections.Generic;
using AIHWSim.Audio;
using AIHWSim.Core;
using AIHWSim.Sensors.Signals;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// A placeable speaker: what it plays is heard twice — audibly, through an
    /// ordinary looping AudioSource (SfxPlayer's roll-off standard, scaled by
    /// the user's SFX volume), and physically, as an emitter in the simulated
    /// <see cref="SoundField"/> world microphones read (loudness in field
    /// units, NEVER scaled by any volume setting — the mixer is not the
    /// simulation). Playback modes: always-on loop, global-clock timer
    /// (SignalCycle idiom, so peers and other speakers agree without sync),
    /// polled proximity trigger, or Interact-key toggle.
    ///
    /// Serves all three placement surfaces: author the component in a scene
    /// (the skin builds itself at Awake), let a TrackCatalog row build the
    /// skin and <see cref="Attach"/> the behaviour, or <see cref="Create"/> it
    /// live in free play.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Props/Speaker")]
    [DisallowMultipleComponent]
    public sealed class SpeakerProp : MonoBehaviour, ISoundEmitter, IWorldSensor
    {
        public SpeakerConfig config = new SpeakerConfig();

        /// <summary>
        /// Installed by PropNetLink on LAN: (propId, wantOn) → true when the
        /// toggle was routed through the session (host applies + broadcasts).
        /// Null (solo) = apply locally. The prop stays ignorant of the net
        /// layer, the way the arcade directors are.
        /// </summary>
        public static System.Func<int, bool, bool> ToggleHook;

        public bool Playing { get; private set; }
        public int PropId => PropRig.PropId(transform.position, PropRig.KindSpeaker);

        private AudioSource _source;
        private bool _interactOn;
        private float _toneHz;

        public static SpeakerProp Create(Transform parent, Vector3 pos, float yawDeg,
                                         SpeakerConfig cfg)
        {
            // Skin geometry is placed in world space (TrackBuilder), so build
            // at the origin first and move the root after — the catalog
            // contract every ItemDef build already follows.
            var go = new GameObject("Speaker");
            go.transform.SetParent(parent, false);
            PropRig.BuildSpeakerSkin(go.transform);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
            return Attach(go, cfg);
        }

        /// <summary>Add the behaviour to a root whose skin may already exist
        /// (catalog-built); builds one otherwise.</summary>
        public static SpeakerProp Attach(GameObject root, SpeakerConfig cfg)
        {
            var prop = root.GetComponent<SpeakerProp>();
            if (prop == null) prop = root.AddComponent<SpeakerProp>();
            if (cfg != null) prop.config = cfg;
            return prop;
        }

        private void Awake()
        {
            if (PropRig.ExistingSkin(transform) == null)
                PropRig.BuildSpeakerSkin(transform);

            var entry = SpeakerCatalog.Find(config.clipKey);
            _toneHz = entry.toneHz;
            _interactOn = config.startOn;

            // Prop-owned looping source: the SfxPlayer pool is one-shots only.
            // Configure() is the mandatory roll-off standard — every source in
            // the game rolls off identically.
            _source = gameObject.AddComponent<AudioSource>();
            SfxPlayer.Configure(_source, spatial: true);
            _source.loop = true;
            _source.clip = ProceduralAudio.Get(entry.clipKey);
            _source.playOnAwake = false;
        }

        private void OnEnable() => SoundField.Register(this);

        private void OnDisable()
        {
            SoundField.Unregister(this);
            WorldTelemetry.Unregister(this);
        }

        private void Start()
        {
            // Register with the world hub in Start, not OnEnable: the name is
            // stable by then and the host (created by TrackBootstrap.Awake)
            // exists whichever Awake ran first.
            WorldTelemetry.Register(this);
        }

        private void Update()
        {
            bool on = config.mode switch
            {
                SpeakerMode.Timer => Time.time % Mathf.Max(0.1f, config.timerPeriodSec)
                                     < config.timerOnSec,
                SpeakerMode.Trigger => PropInteraction.NearestCar(
                                           transform.position, config.triggerRadius) != null,
                SpeakerMode.Interact => _interactOn,
                _ => true,
            };

            if (config.mode == SpeakerMode.Interact && InputReader.InteractPressed()
                && PropInteraction.ClaimInteract(this, transform.position))
            {
                bool want = !_interactOn;
                if (ToggleHook == null || !ToggleHook(PropId, want)) SetPlaying(want);
                on = _interactOn;
            }

            Playing = on;
            if (on && !_source.isPlaying) _source.Play();
            else if (!on && _source.isPlaying) _source.Stop();

            // Track the user's SFX volume live; loudness only scales the
            // AUDIBLE side (clamped as a mixer gain, unbounded in the field).
            _source.volume = SfxPlayer.SfxGain * Mathf.Clamp01(config.loudness);
        }

        /// <summary>Authoritative state apply (local toggle, or a net event).</summary>
        public void SetPlaying(bool on) => _interactOn = on;

        /// <summary>Interact-mode state for net healing; meaningless otherwise.</summary>
        public bool InteractState => _interactOn;

        // ---- ISoundEmitter -------------------------------------------------

        public bool SoundActive => Playing;
        public Vector3 SoundPosition => transform.position;
        public float Loudness => config.loudness;
        public float ToneHz => _toneHz;
        public int SoundEmitterId { get; set; }

        // ---- IWorldSensor --------------------------------------------------

        private static readonly string[] WorldFields = { "enabled", "loudness", "tone_hz" };

        public string WorldSensorName => name;
        public string WorldSensorKind => "speaker";
        public IReadOnlyList<string> WorldFieldNames => WorldFields;
        public Vector3 WorldPosition => transform.position;

        public void SampleWorld(float dt, float[] dest, int offset)
        {
            dest[offset] = Playing ? 1f : 0f;
            dest[offset + 1] = config.loudness;
            dest[offset + 2] = _toneHz;
        }

        private void OnDrawGizmos()
        {
            if (config == null) return;
            if (config.mode == SpeakerMode.Trigger)
            {
                Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.8f);
                Gizmos.DrawWireSphere(transform.position, config.triggerRadius);
            }
            else if (config.mode == SpeakerMode.Interact)
            {
                Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.8f);
                Gizmos.DrawWireSphere(transform.position, PropInteraction.InteractRadius);
            }
        }
    }
}
