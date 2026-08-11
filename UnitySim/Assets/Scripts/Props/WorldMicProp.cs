using System.Collections.Generic;
using AIHWSim.Sensors.Signals;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// A placeable world microphone: reads the simulated <see cref="SoundField"/>
    /// at its own position and publishes to the world telemetry hub —
    /// total level plus the strongest three sources as (id, level, tone)
    /// triples, so external code can tell WHO it hears and triangulate a
    /// source from several mics. Three slots because trilateration needs
    /// exactly three references; empty slots read id = −1.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Props/World Microphone")]
    [DisallowMultipleComponent]
    public sealed class WorldMicProp : MonoBehaviour, IWorldSensor
    {
        public const int Slots = 3;

        public int PropId => PropRig.PropId(transform.position, PropRig.KindMic);

        private readonly SoundReading[] _scratch = new SoundReading[Slots];

        public static WorldMicProp Create(Transform parent, Vector3 pos, float yawDeg)
        {
            // Build at the origin, then move — TrackBuilder places world-space.
            var go = new GameObject("WorldMic");
            go.transform.SetParent(parent, false);
            PropRig.BuildMicSkin(go.transform);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
            return Attach(go);
        }

        public static WorldMicProp Attach(GameObject root)
        {
            var prop = root.GetComponent<WorldMicProp>();
            return prop != null ? prop : root.AddComponent<WorldMicProp>();
        }

        private void Awake()
        {
            if (PropRig.ExistingSkin(transform) == null)
                PropRig.BuildMicSkin(transform);
        }

        private void Start() => WorldTelemetry.Register(this);
        private void OnDisable() => WorldTelemetry.Unregister(this);

        // ---- IWorldSensor --------------------------------------------------

        private static readonly string[] WorldFields =
        {
            "level",
            "s0/id", "s0/level", "s0/tone",
            "s1/id", "s1/level", "s1/tone",
            "s2/id", "s2/level", "s2/tone",
        };

        public string WorldSensorName => name;
        public string WorldSensorKind => "mic";
        public IReadOnlyList<string> WorldFieldNames => WorldFields;
        public Vector3 WorldPosition => transform.position;

        public void SampleWorld(float dt, float[] dest, int offset)
        {
            Vector3 pos = transform.position;
            dest[offset] = SoundField.LevelAt(pos);
            SoundField.StrongestAt(pos, Slots, _scratch);
            for (int s = 0; s < Slots; s++)
            {
                int o = offset + 1 + s * 3;
                dest[o] = _scratch[s].id;
                dest[o + 1] = _scratch[s].level;
                dest[o + 2] = _scratch[s].toneHz;
            }
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.5f, 0.7f, 1f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.21f, 0.05f);
        }
    }
}
