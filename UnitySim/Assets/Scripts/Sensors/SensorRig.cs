using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Owns the sensor loadout for one vehicle. Discovers all child
    /// <see cref="SensorComponent"/>s, lays their outputs out contiguously in a
    /// flat float buffer, builds the <see cref="SensorInfo"/> manifest handed to
    /// the controller via ctrl_configure(), samples everything at the control
    /// rate, and publishes per-field telemetry channels. The first camera's frame
    /// is exposed for the ABI's cam_pixels.
    /// </summary>
    public sealed class SensorRig : MonoBehaviour
    {
        private CarVehicle _vehicle;
        private readonly List<SensorComponent> _sensors = new List<SensorComponent>();
        private readonly List<CameraSensor> _cameras = new List<CameraSensor>();
        private readonly List<MotorPart> _motors = new List<MotorPart>();

        private SensorInfo[] _manifest = System.Array.Empty<SensorInfo>();
        private float[] _flat = System.Array.Empty<float>();
        private string[][] _channelNames = System.Array.Empty<string[]>();

        public SensorInfo[] Manifest => _manifest;
        public float[] FlatData => _flat;
        public int SensorCount => _manifest.Length;
        public IReadOnlyList<SensorComponent> Sensors => _sensors;
        public IReadOnlyList<MotorPart> Motors => _motors;

        /// <summary>First output value of sensor i (e.g. ToF distance), or 0.</summary>
        public float FirstValue(int i)
        {
            if (i < 0 || i >= _manifest.Length) return 0f;
            var info = _manifest[i];
            return info.data_count > 0 && info.data_offset < _flat.Length ? _flat[info.data_offset] : 0f;
        }

        // First-camera view for the ABI (null / 0 if no camera present).
        public byte[] CamPixels => _cameras.Count > 0 ? _cameras[0].Pixels : null;
        public int CamWidth => _cameras.Count > 0 ? _cameras[0].Width : 0;
        public int CamHeight => _cameras.Count > 0 ? _cameras[0].Height : 0;
        public CameraSensor PrimaryCamera => _cameras.Count > 0 ? _cameras[0] : null;

        /// <summary>
        /// Build the manifest + buffers from the child sensors. Call once after the
        /// vehicle (with its sensors already parented) is fully constructed.
        /// </summary>
        public void Initialize(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            _sensors.Clear();
            _cameras.Clear();
            GetComponentsInChildren(true, _sensors);

            _manifest = new SensorInfo[_sensors.Count];
            _channelNames = new string[_sensors.Count][];
            _motors.Clear();

            int offset = 0;
            int motorOrdinal = 0;
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                s.Bind(vehicle, vehicleRoot);
                s.ResetSampling();
                if (s is CameraSensor cam) _cameras.Add(cam);

                var info = new SensorInfo
                {
                    type = (int)s.Type,
                    data_offset = offset,
                    data_count = s.DataCount,
                    range_min = s.rangeMin,
                    range_max = s.rangeMax,
                    actuator_index = -1,
                };

                // Motors are also actuators: give each a distinct actuator slot and
                // advertise its voltage limits via range_min/range_max.
                if (s is MotorPart motor)
                {
                    motor.ActuatorIndex = motorOrdinal++;
                    info.actuator_index = motor.ActuatorIndex;
                    info.range_min = -motor.MaxVoltage;
                    info.range_max = motor.MaxVoltage;
                    _motors.Add(motor);
                }

                info.SetName(s.sensorName);
                _manifest[i] = info;

                // Precompute telemetry channel names: sens/<name>/<field>.
                var fields = s.FieldNames;
                var names = new string[fields.Count];
                for (int f = 0; f < fields.Count; f++)
                    names[f] = $"sens/{s.sensorName}/{fields[f]}";
                _channelNames[i] = names;

                offset += s.DataCount;
            }

            _flat = new float[offset];

            // Hand the driven motors to the vehicle before the first physics step.
            if (vehicle != null) vehicle.BindMotors(_motors);
        }

        /// <summary>Register this rig's telemetry channels with the hub.</summary>
        public void RegisterChannels(TelemetryHub hub)
        {
            for (int i = 0; i < _channelNames.Length; i++)
                foreach (var n in _channelNames[i])
                    hub.RegisterChannel(n);
        }

        /// <summary>Sample every sensor into the flat buffer (control-rate tick).</summary>
        public void Sample(float dt, float simTime)
        {
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                // SampleGated = per-sensor update rate + latency ring; with both
                // at 0 (default) it's a straight fresh sample (legacy behaviour).
                if (s.DataCount > 0) s.SampleGated(dt, simTime, _flat, _manifest[i].data_offset);
            }
            for (int c = 0; c < _cameras.Count; c++)
                _cameras[c].CaptureIfDue(simTime);
        }

        /// <summary>Publish the most recent sampled values to telemetry.</summary>
        public void PublishTelemetry(TelemetryHub hub)
        {
            for (int i = 0; i < _channelNames.Length; i++)
            {
                var names = _channelNames[i];
                int off = _manifest[i].data_offset;
                for (int f = 0; f < names.Length; f++)
                    hub.SetValue(names[f], _flat[off + f]);
            }
        }
    }
}
