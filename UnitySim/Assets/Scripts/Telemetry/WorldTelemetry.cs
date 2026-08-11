using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// A world-level sensor: a prop (microphone, speaker, RF beacon) whose
    /// readings belong to the SCENE, not to any vehicle's hub. Implementors
    /// register with <see cref="WorldTelemetry"/> OnEnable / unregister
    /// OnDisable; the world host samples them at its own fixed rate.
    /// </summary>
    public interface IWorldSensor
    {
        /// <summary>Unique per scene; the host de-dupes clashes with a suffix.</summary>
        string WorldSensorName { get; }
        /// <summary>Stable lowercase kind: "mic" | "speaker" | "beacon".</summary>
        string WorldSensorKind { get; }
        IReadOnlyList<string> WorldFieldNames { get; }
        /// <summary>Write exactly WorldFieldNames.Count floats at [offset..].</summary>
        void SampleWorld(float dt, float[] dest, int offset);
        Vector3 WorldPosition { get; }
    }

    /// <summary>
    /// The scene-level telemetry hub: world props are not tied to any vehicle's
    /// control rate, must survive vehicle despawn, and must exist with zero
    /// vehicles — so they get their own hub, ticked by
    /// <see cref="WorldSensorHost"/> and streamed over IPC under the
    /// world-stream sentinel id. Channels are world/&lt;kind&gt;/&lt;name&gt;/&lt;field&gt;.
    ///
    /// Lifecycle: the sensor REGISTRY persists independently of the hub —
    /// Awake order between scene-authored props and the host is undefined, so
    /// a prop may register before the hub exists. <see cref="Reset"/> (host
    /// Awake) makes a fresh hub and rebuilds channels for whatever is already
    /// registered; props unregister themselves OnDisable at scene teardown.
    /// </summary>
    public static class WorldTelemetry
    {
        public const float WorldRateHz = 50f;

        private static readonly List<IWorldSensor> _sensors = new List<IWorldSensor>();
        private static readonly List<string[]> _channelNames = new List<string[]>();
        private static readonly HashSet<string> _usedNames = new HashSet<string>();

        /// <summary>Null until a host exists (menu scenes have no world hub).</summary>
        public static TelemetryHub Hub { get; private set; }

        public static IReadOnlyList<IWorldSensor> Sensors => _sensors;

        /// <summary>Channel names of sensor i (parallel to <see cref="Sensors"/>).</summary>
        public static IReadOnlyList<string> ChannelsOf(int i) =>
            i >= 0 && i < _channelNames.Count ? _channelNames[i] : System.Array.Empty<string>();

        /// <summary>
        /// Fresh hub for a fresh scene (host Awake). Sensors already registered
        /// — scene-authored props whose OnEnable beat the host's Awake — keep
        /// their registration; their channels are rebuilt on the new hub.
        /// </summary>
        public static void Reset(int channelCapacity = 8192)
        {
            Hub = new TelemetryHub(channelCapacity);
            _channelNames.Clear();
            _usedNames.Clear();
            for (int i = 0; i < _sensors.Count; i++)
                _channelNames.Add(BuildChannels(_sensors[i]));
        }

        /// <summary>Tear the hub down (host destroyed — scene unloading). The
        /// registry stays; props clean themselves up OnDisable.</summary>
        public static void Shutdown()
        {
            Hub = null;
        }

        /// <summary>
        /// Register a world sensor. Channels are created now when the hub
        /// exists, or when the host's Reset later rebuilds them. Props all
        /// exist at scene build, before the host's first Commit, satisfying the
        /// stable-columns rule.
        /// </summary>
        public static void Register(IWorldSensor s)
        {
            if (s == null || _sensors.Contains(s)) return;
            _sensors.Add(s);
            _channelNames.Add(BuildChannels(s));
        }

        public static void Unregister(IWorldSensor s)
        {
            int i = _sensors.IndexOf(s);
            if (i < 0) return;
            _sensors.RemoveAt(i);
            _channelNames.RemoveAt(i);
            // Channels stay registered on the hub (columns are stable); they
            // simply stop updating.
        }

        private static string[] BuildChannels(IWorldSensor s)
        {
            string baseName = string.IsNullOrWhiteSpace(s.WorldSensorName) ? s.WorldSensorKind
                                                                           : s.WorldSensorName;
            string unique = baseName;
            for (int n = 2; !_usedNames.Add($"{s.WorldSensorKind}/{unique}"); n++)
                unique = $"{baseName}_{n}";

            var fields = s.WorldFieldNames;
            var names = new string[fields.Count];
            for (int f = 0; f < fields.Count; f++)
            {
                names[f] = $"world/{s.WorldSensorKind}/{unique}/{fields[f]}";
                Hub?.RegisterChannel(names[f]);
            }
            return names;
        }

        /// <summary>One world tick: sample every sensor and commit. Called by
        /// the host only.</summary>
        public static void Tick(float dt, float worldTime, float[] scratch)
        {
            if (Hub == null) return;
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                var names = _channelNames[i];
                int n = names.Length;
                if (n > scratch.Length) continue; // contract breach; skip, never throw
                s.SampleWorld(dt, scratch, 0);
                for (int f = 0; f < n; f++)
                    Hub.SetValue(names[f], scratch[f]);
            }
            Hub.Commit(worldTime);
        }
    }
}
