using UnityEngine;

namespace AIHWSim.Sensors.Signals
{
    /// <summary>
    /// A source in the simulated sound field: speakers, and every vehicle's
    /// motor. Emitters register with <see cref="SoundField"/> OnEnable and
    /// unregister OnDisable, so scene teardown cleans the list without any
    /// scene-load hook. The field is idealized on purpose — inverse-square
    /// falloff, no occlusion — because the headline use is triangulating a
    /// source from several microphones, and occlusion shadows would make that
    /// math exercise unsolvable on a cluttered track.
    /// </summary>
    public interface ISoundEmitter
    {
        /// <summary>False = silent (a stopped speaker stays registered).</summary>
        bool SoundActive { get; }
        Vector3 SoundPosition { get; }
        /// <summary>Linear amplitude at 1 m, ≥ 0. Physical units — never scaled
        /// by any user volume setting (the mixer is not the simulation).</summary>
        float Loudness { get; }
        /// <summary>Waveform signature: the dominant tone in Hz. This is how a
        /// receiver tells WHO it hears, not just how loud. No DSP, no phase —
        /// deterministic scalars only.</summary>
        float ToneHz { get; }
        /// <summary>Identity on the wire; assigned by <see cref="SoundField.Register"/>.</summary>
        int SoundEmitterId { get; set; }
    }

    /// <summary>
    /// A ping source in the simulated RF field: world beacons, and vehicle
    /// antennas with emit enabled. Same register-OnEnable/unregister-OnDisable
    /// lifecycle and same no-occlusion idealization as the sound field.
    /// </summary>
    public interface IRfEmitter
    {
        /// <summary>False = not transmitting (a disabled beacon stays registered).</summary>
        bool RfActive { get; }
        Vector3 RfPosition { get; }
        /// <summary>Transmit power in dBm at 1 m (free-space reference). Default 0.</summary>
        float TxPowerDbm { get; }
        /// <summary>User-chosen identity (≥ 0) reported in receiver slots — unlike
        /// sound ids this is authored, so firmware can look for a known beacon.</summary>
        int BeaconId { get; }
    }
}
