using System;

namespace AIHWSim.Props
{
    /// <summary>How a speaker decides when it is playing.</summary>
    public enum SpeakerMode
    {
        /// <summary>Always on.</summary>
        Loop = 0,
        /// <summary>On for timerOnSec out of every timerPeriodSec, phase-locked
        /// to the global clock like the traffic signals — every timer speaker
        /// on a map fires together, and LAN peers agree with zero sync.</summary>
        Timer = 1,
        /// <summary>Playing while a car is within triggerRadius (polled
        /// distance — cars are many colliders and rigidbodies sleep).</summary>
        Trigger = 2,
        /// <summary>Toggled by the Interact key from a car alongside it.</summary>
        Interact = 3,
    }

    /// <summary>
    /// One speaker's authored setup. Plain serializable data: the same object
    /// serves the scene-authored component's inspector, the Track Studio
    /// catalog defaults, and a row in the per-map prop layout JSON.
    /// </summary>
    [Serializable]
    public sealed class SpeakerConfig
    {
        public SpeakerMode mode = SpeakerMode.Loop;
        /// <summary>ProceduralAudio clip key, from <see cref="SpeakerCatalog"/>.</summary>
        public string clipKey = SpeakerCatalog.DefaultKey;
        /// <summary>SoundField linear amplitude at 1 m. Physical units — never
        /// scaled by any user volume setting.</summary>
        public float loudness = 1f;
        public float timerPeriodSec = 8f;
        public float timerOnSec = 2f;
        public float triggerRadius = 1.5f;
        /// <summary>Interact mode: the state the speaker starts in.</summary>
        public bool startOn = true;

        public SpeakerConfig Clone() => (SpeakerConfig)MemberwiseClone();
    }
}
