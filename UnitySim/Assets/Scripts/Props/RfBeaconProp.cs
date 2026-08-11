using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Sensors.Signals;
using AIHWSim.Telemetry;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// A placeable RF beacon: a ping source in the simulated
    /// <see cref="RfField"/> that vehicle antennas track and triangulate.
    /// Enable/disable in game with the Interact key from a car alongside it;
    /// its state also shows on the world telemetry hub. The beacon id is
    /// authored (unlike sound ids), so firmware can search for a known beacon.
    /// </summary>
    [AddComponentMenu("Tiny Torque/Props/RF Beacon")]
    [DisallowMultipleComponent]
    public sealed class RfBeaconProp : MonoBehaviour, IRfEmitter, IWorldSensor
    {
        [Tooltip("Beacon identity reported to receivers (≥ 0).")]
        public int beaconId = 0;
        [Tooltip("Transmit power at 1 m (dBm).")]
        public float txPowerDbm = 0f;
        [Tooltip("Start transmitting when the scene loads.")]
        public bool startOn = true;

        /// <summary>PropNetLink's toggle route; see SpeakerProp.ToggleHook.</summary>
        public static System.Func<int, bool, bool> ToggleHook;

        public bool Enabled { get; private set; }
        public int PropId => PropRig.PropId(transform.position, PropRig.KindBeacon);

        private Renderer _lamp;
        private MaterialPropertyBlock _mpb;

        public static RfBeaconProp Create(Transform parent, Vector3 pos, float yawDeg,
                                          float power, int id, bool startOn)
        {
            // Build at the origin, then move — TrackBuilder places world-space.
            var go = new GameObject("RfBeacon");
            go.transform.SetParent(parent, false);
            PropRig.BuildBeaconSkin(go.transform);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yawDeg, 0f));
            return Attach(go, power, id, startOn);
        }

        public static RfBeaconProp Attach(GameObject root, float power, int id, bool startOn)
        {
            var prop = root.GetComponent<RfBeaconProp>();
            if (prop == null) prop = root.AddComponent<RfBeaconProp>();
            prop.txPowerDbm = power;
            prop.beaconId = id;
            prop.startOn = startOn;
            return prop;
        }

        private void Awake()
        {
            if (PropRig.ExistingSkin(transform) == null)
                PropRig.BuildBeaconSkin(transform);
            Enabled = startOn;

            var lampTf = transform.Find("skin/lamp");
            _lamp = lampTf != null ? lampTf.GetComponent<Renderer>() : null;
        }

        private void OnEnable() => RfField.Register(this);
        private void OnDisable()
        {
            RfField.Unregister(this);
            WorldTelemetry.Unregister(this);
        }

        private void Start()
        {
            WorldTelemetry.Register(this);
            PresentLamp();
        }

        private void Update()
        {
            if (InputReader.InteractPressed()
                && PropInteraction.ClaimInteract(this, transform.position))
            {
                bool want = !Enabled;
                if (ToggleHook == null || !ToggleHook(PropId, want)) SetEnabled(want);
            }
        }

        /// <summary>Authoritative state apply (local toggle, or a net event).</summary>
        public void SetEnabled(bool on)
        {
            Enabled = on;
            PresentLamp();
        }

        private void PresentLamp()
        {
            if (_lamp == null) return;
            _mpb ??= new MaterialPropertyBlock();
            Color c = Enabled ? new Color(0.3f, 1f, 0.45f) : new Color(0.12f, 0.16f, 0.13f);
            _mpb.SetColor("_Color", c);
            _mpb.SetColor("_EmissionColor", Enabled ? new Color(0.15f, 0.8f, 0.3f) : Color.black);
            _lamp.SetPropertyBlock(_mpb);
        }

        // ---- IRfEmitter ----------------------------------------------------

        public bool RfActive => Enabled && isActiveAndEnabled;
        public Vector3 RfPosition => transform.position;
        public float TxPowerDbm => txPowerDbm;
        public int BeaconId => beaconId;

        // ---- IWorldSensor --------------------------------------------------

        private static readonly string[] WorldFields = { "enabled", "id", "tx_dbm" };

        public string WorldSensorName => name;
        public string WorldSensorKind => "beacon";
        public IReadOnlyList<string> WorldFieldNames => WorldFields;
        public Vector3 WorldPosition => transform.position;

        public void SampleWorld(float dt, float[] dest, int offset)
        {
            dest[offset] = Enabled ? 1f : 0f;
            dest[offset + 1] = beaconId;
            dest[offset + 2] = txPowerDbm;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.8f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.345f, 0.05f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 0.4f);
        }
    }
}
