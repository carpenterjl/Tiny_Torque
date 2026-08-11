using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Sensors.Signals;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// RF antenna module: receives pings from the simulated <see cref="RfField"/>
    /// and reports the strongest three — trilateration needs exactly three
    /// references, so the fixed slot count is 3 (channels and the ABI manifest
    /// cannot grow at runtime). Fields:
    ///   count, s0/id, s0/rssi, s0/bearing, s1/…, s2/…   (10 floats)
    /// Empty slots read id = −1, rssi = −100 dBm, bearing = 0.
    ///
    /// The antenna can also EMIT (a trackable ping source for other cars and
    /// props): enable <see cref="emitEnabled"/> and give it a beacon id. Its
    /// own transmission is excluded from its readings.
    /// </summary>
    public sealed class RfSensor : SensorComponent, IRfEmitter
    {
        public const int Slots = 3;

        [Header("RF receive")]
        [Tooltip("Pings weaker than this are not reported (dBm).")]
        public float minRssiDbm = -90f;

        [Header("RF emit")]
        [Tooltip("Transmit a ping other antennas can hear.")]
        public bool emitEnabled = false;
        [Tooltip("Beacon identity reported to receivers (≥ 0).")]
        public int emitId = 0;
        [Tooltip("Transmit power at 1 m (dBm).")]
        public float emitPowerDbm = 0f;

        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields =
        {
            "count",
            "s0/id", "s0/rssi", "s0/bearing",
            "s1/id", "s1/rssi", "s1/bearing",
            "s2/id", "s2/rssi", "s2/bearing",
        };
        private readonly RfReading[] _scratch = new RfReading[Slots];

        public override SensorType Type => SensorType.Rf;
        public override int DataCount => 10;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            rangeMin = RfField.RssiFloorDbm;
            rangeMax = 0f;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            RfField.StrongestAt(transform.position, transform.forward, Slots, _scratch,
                exclude: this);

            int count = 0;
            for (int s = 0; s < Slots; s++)
            {
                var r = _scratch[s];
                bool present = r.beaconId >= 0;
                if (present)
                {
                    // Noise on the RSSI only; id and bearing stay exact (the
                    // id is a label, and bearing noise would double-count the
                    // corruption a real DF rig gets from RSSI jitter anyway).
                    r.rssiDbm = Mathf.Clamp(noise.Apply(r.rssiDbm, dt), RfField.RssiFloorDbm, 0f);
                    present = r.rssiDbm >= minRssiDbm;
                }
                if (!present)
                    r = new RfReading { beaconId = -1, rssiDbm = RfField.RssiFloorDbm, bearingDeg = 0f };
                else
                    count++;

                int o = offset + 1 + s * 3;
                dest[o]     = r.beaconId;
                dest[o + 1] = r.rssiDbm;
                dest[o + 2] = r.bearingDeg;
            }
            dest[offset] = count;
        }

        // ---- IRfEmitter ----------------------------------------------------

        public bool RfActive => emitEnabled && isActiveAndEnabled;
        public Vector3 RfPosition => transform.position;
        public float TxPowerDbm => emitPowerDbm;
        public int BeaconId => emitId;

        private void OnEnable() => RfField.Register(this);
        private void OnDisable() => RfField.Unregister(this);

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 1f, 0.3f);
            Gizmos.DrawLine(transform.position, transform.position + transform.up * 0.15f);
        }
    }
}
