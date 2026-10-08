using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Sensors
{
    /// <summary>
    /// Battery pack telemetry (SENSOR_BATTERY). Reports the bus terminal voltage
    /// (sagging under load across the pack's internal resistance), the total motor
    /// current, and the live state-of-charge from the vehicle's coulomb counter
    /// (pinned at 1.0 for capacity-0 legacy packs). Firmware reads this exactly
    /// like probing the pack with an ADC divider — e.g. to voltage-compensate its
    /// motor commands.
    ///
    /// Realistic profile (SEN-06/09): V and I through an INA228-class monitor
    /// (gain, offset, noise, LSB), and SoC reads NaN — a real pack has no SoC
    /// pin; firmware estimates it. The truth is in veh/soc.
    /// </summary>
    public sealed class BatterySensor : SensorComponent
    {
        public NoiseModel noise = new NoiseModel();

        private static readonly string[] Fields = { "volt", "amps", "soc" };
        private CarVehicle _vehicle;

        public override SensorType Type => SensorType.Battery;
        public override int DataCount => 3;
        public override IReadOnlyList<string> FieldNames => Fields;

        public override void Bind(CarVehicle vehicle, Transform vehicleRoot)
        {
            _vehicle = vehicle;
            rangeMin = 0f;
            rangeMax = vehicle != null ? Mathf.Max(0.01f, vehicle.batteryNominalV) : 0.01f;
        }

        private SenseChannel _senseV, _senseI;

        public override void Sample(float dt, float[] dest, int offset)
        {
            float v = noise.Apply(_vehicle != null ? _vehicle.BatteryTerminalV : 0f, dt, 0);
            float i = noise.Apply(_vehicle != null ? _vehicle.BatteryCurrent : 0f, dt, 1);
            if (Realistic)
            {
                if (_senseV == null)
                {
                    var rng = SensorRealism.Rng(sensorName, 1);
                    _senseV = new SenseChannel(SensorRealism.BatteryVolt, rng);
                    _senseI = new SenseChannel(SensorRealism.BatteryAmp, rng);
                }
                v = _senseV.Apply(v);
                i = _senseI.Apply(i);
            }
            dest[offset]     = v;
            dest[offset + 1] = i;
            dest[offset + 2] = Realistic ? float.NaN : _vehicle != null ? _vehicle.BatterySoc : 1f;
        }
    }
}
