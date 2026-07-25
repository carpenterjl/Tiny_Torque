using AIHWSim.Telemetry;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Contract the SimulationRunner drives. Sensor sampling and command
    /// latching happen at the control rate; StepPhysics runs every physics
    /// FixedUpdate (commands are held zero-order between control updates).
    /// </summary>
    public interface IControlledVehicle
    {
        /// <summary>Number of setpoint channels this vehicle expects from the operator.</summary>
        int SetpointCount { get; }

        void ResetVehicle();

        /// <summary>
        /// Sample sensors into the provided buffers (called at control rate).
        /// Buffers are sized to the ABI (wheelVel[4], gyro[3], accel[3]);
        /// unused entries should be left at zero.
        /// </summary>
        void SampleSensors(float dt, float[] wheelVel, float[] gyro, float[] accel);

        /// <summary>Latch the newest normalized actuator commands (control rate).</summary>
        void SetCommands(float[] actuatorCommands);

        /// <summary>Integrate motor + traction physics for one physics step.</summary>
        void StepPhysics(float dt);

        /// <summary>Publish vehicle-specific state to telemetry.</summary>
        void PublishTelemetry(TelemetryHub hub);
    }
}
