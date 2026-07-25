using System;
using System.Collections.Generic;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One live-adjustable float parameter, exposed to the pause-menu tuner.
    /// Get/Set close over the owning component's field so edits apply immediately.
    /// </summary>
    public sealed class TunableParam
    {
        public readonly string Name;
        public readonly float Min;
        public readonly float Max;
        public readonly Func<float> Get;
        public readonly Action<float> Set;

        public TunableParam(string name, float min, float max, Func<float> get, Action<float> set)
        {
            Name = name; Min = min; Max = max; Get = get; Set = set;
        }
    }

    /// <summary>Something (e.g. a vehicle) that exposes parameters for live tuning.</summary>
    public interface ITunable
    {
        IReadOnlyList<TunableParam> GetTunables();
    }
}
