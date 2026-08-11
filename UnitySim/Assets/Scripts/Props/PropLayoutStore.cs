using System;
using System.Collections.Generic;
using System.IO;
using AIHWSim.Core;
using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>One live-placed prop in a per-map layout. Flat union row
    /// (JsonUtility): unused fields keep their initializers, additive-only
    /// migration like every other save in the project.</summary>
    [Serializable]
    public sealed class PropPlacement
    {
        /// <summary>"speaker" | "mic" | "rf_beacon".</summary>
        public string kind = "speaker";
        public float x, y, z;
        public float yawDeg;

        // Speaker
        public int mode = (int)SpeakerMode.Loop;
        public string clipKey = SpeakerCatalog.DefaultKey;
        public float loudness = 1f;
        public float timerPeriodSec = 8f;
        public float timerOnSec = 2f;
        public float triggerRadius = 1.5f;
        public bool startOn = true;

        // RF beacon
        public int beaconId = 0;
        public float txPowerDbm = 0f;

        public SpeakerConfig ToSpeakerConfig() => new SpeakerConfig
        {
            mode = (SpeakerMode)mode,
            clipKey = clipKey,
            loudness = loudness,
            timerPeriodSec = timerPeriodSec,
            timerOnSec = timerOnSec,
            triggerRadius = triggerRadius,
            startOn = startOn,
        };
    }

    [Serializable]
    public sealed class PropLayout
    {
        public int version = 1;
        public List<PropPlacement> props = new List<PropPlacement>();
    }

    /// <summary>
    /// Per-map prop layouts for free-play live placement:
    /// <c>&lt;save dir&gt;/Props/&lt;trackKey&gt;.json</c>, keyed by whatever
    /// identifies the loaded map — scene name for scene tracks, design name
    /// for tile maps, "oval" for the procedural default. Deliberately a save
    /// file and not scene data: it works on hand-authored scenes without
    /// touching them, survives restarts, and stays per-machine (which is also
    /// why LAN sessions don't load it — whose file would win?).
    /// </summary>
    public static class PropLayoutStore
    {
        public static string Dir => Path.Combine(AppPaths.BaseDir, "Props");

        /// <summary>Key for the currently loaded map, or null when the session
        /// has no identifiable map yet.</summary>
        public static string TrackKey()
        {
            if (GameFlow.HasSceneTrack) return "scene_" + Sanitize(GameFlow.ActiveSceneTrack);
            var design = GameFlow.ActiveTrack;
            if (design != null && !string.IsNullOrWhiteSpace(design.name))
                return "track_" + Sanitize(design.name);
            return "oval";
        }

        public static string PathFor(string trackKey) =>
            Path.Combine(Dir, trackKey + ".json");

        /// <summary>Load a map's layout; a missing or unreadable file is an
        /// empty layout, never an exception.</summary>
        public static PropLayout Load(string trackKey)
        {
            try
            {
                string path = PathFor(trackKey);
                if (!File.Exists(path)) return new PropLayout();
                var layout = JsonUtility.FromJson<PropLayout>(File.ReadAllText(path));
                if (layout == null) return new PropLayout();
                layout.props ??= new List<PropPlacement>();
                return layout;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PRP] could not read prop layout '{trackKey}': {e.Message}");
                return new PropLayout();
            }
        }

        public static void Save(string trackKey, PropLayout layout)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathFor(trackKey), JsonUtility.ToJson(layout, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PRP] could not save prop layout '{trackKey}': {e.Message}");
            }
        }

        private static string Sanitize(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.Length > 0 ? sb.ToString() : "unnamed";
        }

        /// <summary>Instantiate a layout's props under a root. Returns the live
        /// components in row order (parallel to layout.props).</summary>
        public static List<MonoBehaviour> Spawn(PropLayout layout, Transform root)
        {
            var live = new List<MonoBehaviour>(layout.props.Count);
            foreach (var p in layout.props)
            {
                var pos = new Vector3(p.x, p.y, p.z);
                switch (p.kind)
                {
                    case "mic":
                        live.Add(WorldMicProp.Create(root, pos, p.yawDeg));
                        break;
                    case "rf_beacon":
                        live.Add(RfBeaconProp.Create(root, pos, p.yawDeg,
                            p.txPowerDbm, p.beaconId, p.startOn));
                        break;
                    default:
                        live.Add(SpeakerProp.Create(root, pos, p.yawDeg, p.ToSpeakerConfig()));
                        break;
                }
            }
            return live;
        }
    }
}
