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
        private readonly List<LedPart> _leds = new List<LedPart>();

        private SensorInfo[] _manifest = System.Array.Empty<SensorInfo>();
        private float[] _flat = System.Array.Empty<float>();
        private float[] _flatFlu = System.Array.Empty<float>();
        private SensorStamp[] _stamps = System.Array.Empty<SensorStamp>();
        private string[][] _channelNames = System.Array.Empty<string[]>();
        private Transform _root;

        public SensorInfo[] Manifest => _manifest;
        public float[] FlatData => _flat;
        public int SensorCount => _manifest.Length;

        /// <summary>v7 stamps, one per extended-manifest entry (the sensors, then
        /// the steering entry, which has no data and keeps a zero stamp).</summary>
        public SensorStamp[] Stamps => _stamps;
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
            _root = vehicleRoot;
            _sensors.Clear();
            _cameras.Clear();
            GetComponentsInChildren(true, _sensors);

            _manifest = new SensorInfo[_sensors.Count];
            _channelNames = new string[_sensors.Count][];
            _motors.Clear();
            _leds.Clear();

            int offset = 0;
            int motorOrdinal = 0;
            var ledManifestIdx = new List<int>();
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                s.Bind(vehicle, vehicleRoot);
                s.ResetSampling();
                if (s is CameraSensor cam) _cameras.Add(cam);
                if (s is LedPart led) { _leds.Add(led); ledManifestIdx.Add(i); }

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
            _flatFlu = new float[offset];
            _stamps = new SensorStamp[_sensors.Count + 1];   // + the steering entry

            // LEDs claim actuator slots AFTER the motors (only then is the
            // motor count final): two consecutive slots each — RGB24 pack +
            // blink Hz. Slots 6/7 are reserved (CTRL_STEER/BRAKE_ACTUATOR), so
            // the free pool is [motorCount..5]; no free pair → display-only.
            int nextSlot = motorOrdinal;
            for (int l = 0; l < _leds.Count; l++)
            {
                if (nextSlot + 1 < 6)
                {
                    _leds[l].ActuatorIndex = nextSlot;
                    _manifest[ledManifestIdx[l]].actuator_index = nextSlot;
                    nextSlot += 2;
                }
                else
                {
                    _leds[l].ActuatorIndex = -1;
                    Debug.LogWarning($"[SENS] no free actuator slot pair for LED " +
                        $"'{_leds[l].sensorName}' ({motorOrdinal} motors) — display-only.");
                }
            }

            // Hand the driven motors to the vehicle before the first physics step,
            // and to the world-facing sound emitter (motor whine for world mics).
            if (vehicle != null) vehicle.BindMotors(_motors);
            GetComponent<VehicleSoundEmitter>()?.BindMotors(_motors);
        }

        /// <summary>Register this rig's telemetry channels with the hub.</summary>
        public void RegisterChannels(TelemetryHub hub)
        {
            for (int i = 0; i < _channelNames.Length; i++)
                foreach (var n in _channelNames[i])
                    hub.RegisterChannel(n);
        }

        /// <summary>Sample every sensor into the flat buffer (control-rate tick).</summary>
        public void Sample(float dt, double simTime)
        {
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                // SampleGated = per-sensor update rate + latency ring; with both
                // at 0 (default) it's a straight fresh sample (legacy behaviour).
                if (s.DataCount > 0)
                {
                    s.SampleGated(dt, simTime, _flat, _manifest[i].data_offset);
                    _stamps[i].seq = s.StampSeq;
                    // Low 32 bits of the µs clock: firmware compares by difference.
                    _stamps[i].t_sample_us = unchecked((uint)(long)System.Math.Round(s.StampTime * 1e6));
                }
            }
            for (int c = 0; c < _cameras.Count; c++)
                _cameras[c].CaptureIfDue((float)simTime);
        }

        /// <summary>
        /// The flat data in the frame a v7 controller expects (ABI-04): FLU, so
        /// an RF bearing is positive to the LEFT. Recomputed into its own buffer
        /// each call; telemetry keeps publishing the native values.
        /// </summary>
        public float[] FlatDataFlu()
        {
            System.Array.Copy(_flat, _flatFlu, _flat.Length);
            for (int i = 0; i < _sensors.Count; i++)
            {
                if (_sensors[i].Type != SensorType.Rf) continue;
                int off = _manifest[i].data_offset;
                for (int k = 0; k < 3; k++)
                {
                    int b = off + 3 + 3 * k;   // [count, (id, rssi, bearing) x3]
                    if (b < _flatFlu.Length) _flatFlu[b] = -_flatFlu[b];
                }
            }
            return _flatFlu;
        }

        /// <summary>
        /// The v7 extended manifest (ABI-02): every v6 entry with its geometry,
        /// rates and drive constants filled in, then one SENSOR_STEER_FB entry
        /// that describes the steering actuator. FLU throughout.
        /// </summary>
        public unsafe SensorInfo2[] BuildManifest2()
        {
            var m2 = new SensorInfo2[_sensors.Count + 1];
            for (int i = 0; i < _sensors.Count; i++)
            {
                var s = _sensors[i];
                var e = new SensorInfo2 { @base = _manifest[i], wheel_index = -1 };
                MountPoseFlu(s.transform, out Vector3 pos, out Vector3 rpy);
                e.pos_m[0] = pos.x; e.pos_m[1] = pos.y; e.pos_m[2] = pos.z;
                e.rpy_rad[0] = rpy.x; e.rpy_rad[1] = rpy.y; e.rpy_rad[2] = rpy.z;
                e.rate_hz = Mathf.Max(0f, s.updateRateHz);
                e.latency_s = Mathf.Max(0f, s.latencyMs) * 0.001f;

                int wheel = -1;
                switch (s)
                {
                    case WheelEncoderSensor enc:
                        wheel = enc.wheelIndex;
                        e.cpr = enc.countsPerRev;
                        e.wrap = enc.wrap;
                        e.gear_ratio = enc.gearRatio;
                        break;
                    case MotorPart mp:
                        wheel = mp.wheelIndex;
                        e.units = (int)ActuatorUnits.Volts;
                        e.gear_ratio = mp.motor.gearRatio;
                        e.kt = mp.motor.kt;
                        e.resistance_ohm = mp.motor.resistance;
                        e.efficiency = mp.motor.efficiency;
                        break;
                    case SuspensionSensor sus:
                        wheel = sus.wheelIndex;
                        break;
                }
                if (wheel >= 0)
                {
                    e.wheel_index = wheel;
                    var wc = _vehicle != null ? _vehicle.GetWheel(wheel) : null;
                    e.wheel_radius_m = wc != null ? wc.radius : 0f;
                }
                m2[i] = e;
            }

            // The steering actuator, so firmware commands radians and reads the
            // full-lock angle instead of hard-coding it.
            float lockRad = _vehicle != null ? _vehicle.MaxSteerDeg * Mathf.Deg2Rad : 0f;
            var steer = new SensorInfo2
            {
                @base = new SensorInfo
                {
                    type = (int)SensorType.SteerFb,
                    data_offset = _flat.Length,
                    data_count = 0,
                    range_min = -lockRad,
                    range_max = lockRad,
                    actuator_index = 6,
                },
                wheel_index = -1,
                units = (int)ActuatorUnits.Radians,
            };
            steer.@base.SetName("steer");
            m2[_sensors.Count] = steer;
            return m2;
        }

        // Unity (x right, y up, z forward; left-handed) -> FLU (x forward,
        // y left, z up; right-handed): v_F = (v.z, -v.x, v.y). A rotation is
        // conjugated by the same axis map, which keeps it proper.
        private void MountPoseFlu(Transform t, out Vector3 pos, out Vector3 rpy)
        {
            Transform root = _root != null ? _root : transform;
            Vector3 lp = root.InverseTransformPoint(t.position);
            pos = FluFrame.Vector(lp);

            Quaternion q = Quaternion.Inverse(root.rotation) * t.rotation;
            // Sensor axes in FLU: forward = local z, left = -local x, up = local y.
            Vector3 fx = FluFrame.Vector(q * Vector3.forward);
            Vector3 fy = FluFrame.Vector(q * Vector3.left);
            Vector3 fz = FluFrame.Vector(q * Vector3.up);
            // R columns are fx, fy, fz. Z-Y-X Euler: yaw, pitch, roll.
            float yaw = Mathf.Atan2(fx.y, fx.x);
            float pitch = Mathf.Asin(Mathf.Clamp(-fx.z, -1f, 1f));
            float roll = Mathf.Atan2(fy.z, fz.z);
            rpy = new Vector3(roll, pitch, yaw);
        }


        /// <summary>
        /// Forward the (possibly transport-delayed) actuator command array to
        /// every LED part — the same array the motors are driven from, so LEDs
        /// honour actuation delay identically. Called once per control tick.
        /// </summary>
        public void ApplyActuators(float[] actuators, float simTime)
        {
            for (int i = 0; i < _leds.Count; i++)
                _leds[i].ApplyActuators(actuators, simTime);
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
