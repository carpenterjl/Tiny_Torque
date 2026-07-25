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
}
