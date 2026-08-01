namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// A pilot that does exactly what it is told and nothing else. Twin of
    /// <c>ScriptedDriver</c>, and it exists for the same reason: a measurement
    /// whose initial condition depends on a human holding a stick near the right
    /// place is not repeatable, and a number that is not repeatable is not a
    /// measurement.
    ///
    /// It is swapped into <see cref="PlaneInput.source"/> in place of
    /// <see cref="PilotInputSource"/>, so a scripted flight and a hand-flown one go
    /// through identical code — which is what lets a test claim to have measured
    /// what a pilot would actually experience.
    ///
    /// Stateless on purpose: no ratchet, no ramps. A test that wants a control to
    /// move over time moves it itself, where the trace can see it.
    /// </summary>
    public sealed class ScriptedPilot : IPilotInputSource
    {
        public float throttle;   // [0,1]
        public float roll;       // [-1,1]
        public float pitch;      // [-1,1]
        public float yaw;        // [-1,1]

        public void Neutral()
        {
            throttle = 0f;
            roll = pitch = yaw = 0f;
        }

        public float Throttle() => throttle;
        public float Roll() => roll;
        public float Pitch() => pitch;
        public float Yaw() => yaw;

        // A script never asks for a respawn or a camera change; the harness owns
        // both, so that a test cannot accidentally reset itself mid-measurement.
        public bool ResetPressed() => false;
        public bool ViewTogglePressed() => false;
    }
}
