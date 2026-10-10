using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// RC receiver (FW-08), SBUS / CRSF class, ABI <c>SENSOR_RC</c>:
    /// <c>[ch1..ch8 in -1..1, frame_lost 0/1, failsafe 0/1]</c>.
    ///
    /// The transmitter is virtual: <see cref="armSwitch"/> and
    /// <see cref="killSwitch"/> are the two switches the safety layer reads
    /// (ch5 and ch6 by convention: +1 = arm / kill, -1 = off), and the sticks
    /// sit at neutral unless something sets them. A frame is one sample, so
    /// <see cref="SensorComponent.updateRateHz"/> is the frame rate.
    ///
    /// With the link down (<see cref="linkLost"/>, set by the FaultInjector)
    /// the receiver behaves as an SBUS one does: it keeps sending frames, each
    /// flagged lost and repeating the last good channels, and declares
    /// failsafe once <see cref="failsafeHoldMs"/> has passed without a good
    /// frame. A receiver that stops sending at all is the injector's "stale"
    /// fault on this part, not this flag.
    /// </summary>
    public sealed class RcReceiverSensor : SensorComponent
    {
        [Header("Virtual transmitter")]
        public float steer;                 // ch1
        public float throttle;              // ch2
        public bool armSwitch = true;       // ch5
        public bool killSwitch;             // ch6

        [Header("Receiver")]
        [Tooltip("Lost frames this long before the receiver declares failsafe (ms).")]
        public float failsafeHoldMs = 1000f;
        [Tooltip("VAL-11: the radio link is down.")]
        public bool linkLost;

        private static readonly string[] Fields =
            { "ch1", "ch2", "ch3", "ch4", "ch5", "ch6", "ch7", "ch8", "frame_lost", "failsafe" };
        private readonly float[] _last = new float[8];
        private bool _hasLast;
        private float _lostS;

        public override SensorType Type => SensorType.Rc;
        public override int DataCount => 10;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            rangeMin = -1f;
            rangeMax = 1f;
            _hasLast = false;
            _lostS = 0f;
        }

        public override void Sample(float dt, float[] dest, int offset)
        {
            if (linkLost && _hasLast)
            {
                _lostS += dt;
                for (int i = 0; i < 8; i++) dest[offset + i] = _last[i];
                dest[offset + 8] = 1f;
                dest[offset + 9] = _lostS * 1000f >= failsafeHoldMs ? 1f : 0f;
                return;
            }
            _lostS = 0f;
            _last[0] = Mathf.Clamp(steer, -1f, 1f);
            _last[1] = Mathf.Clamp(throttle, -1f, 1f);
            _last[2] = 0f;
            _last[3] = 0f;
            _last[4] = armSwitch ? 1f : -1f;
            _last[5] = killSwitch ? 1f : -1f;
            _last[6] = 0f;
            _last[7] = 0f;
            _hasLast = true;
            for (int i = 0; i < 8; i++) dest[offset + i] = _last[i];
            dest[offset + 8] = 0f;
            dest[offset + 9] = 0f;
        }
    }
}
