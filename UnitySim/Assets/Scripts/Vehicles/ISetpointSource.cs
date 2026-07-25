namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Supplies high-level operator setpoints for Autonomous Mode (the targets
    /// the loaded C controller closes the loop around). Meaning is
    /// vehicle-defined and mirrors CtrlInputs.setpoint[].
    /// </summary>
    public interface ISetpointSource
    {
        float[] Setpoints { get; }
    }
}
