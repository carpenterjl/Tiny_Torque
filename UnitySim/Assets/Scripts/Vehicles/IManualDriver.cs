namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Supplies actuator commands directly from human input (Manual Mode),
    /// bypassing the C controller. The buffer uses the same actuator[] layout
    /// the vehicle expects (e.g. car: [0]=throttle, [1]=steer, [2]=brake).
    /// </summary>
    public interface IManualDriver
    {
        void ReadManualCommands(float[] actuatorOut);
    }

    /// <summary>
    /// A manual driver whose commands are raw actuator values from a program,
    /// not a human (IPC raw takeover). The runner treats it like firmware:
    /// arcade assists are forced off while it is installed, so the commands
    /// meet the raw physics exactly as a C controller's would.
    /// </summary>
    public interface IRawActuatorDriver : IManualDriver { }
}
