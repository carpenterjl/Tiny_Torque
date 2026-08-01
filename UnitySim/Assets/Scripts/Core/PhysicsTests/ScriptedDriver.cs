namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// A driver whose inputs are written by a test rather than read from a
    /// keyboard. Plugged into <see cref="CarInput.source"/>, which both
    /// <c>Awake</c> and <c>Update</c> fill with <c>??=</c> — so a non-null
    /// assignment survives every frame and nothing has to be re-installed.
    ///
    /// <b>Why route through CarInput at all</b>, rather than replacing the
    /// runner's <c>inputBehaviour</c> with a bespoke <c>IManualDriver</c>: this
    /// way the scripted run and a human taking over are the same code path with
    /// one field swapped. Pressing M during a test hands the car back to a
    /// <see cref="PlayerInputSource"/> and everything downstream — actuator
    /// mapping, steer shaping, telemetry — is bit-for-bit what the test was
    /// using. A parallel path would let the two diverge without anyone noticing.
    ///
    /// One piece of CarInput is worth knowing is inert here:
    /// <c>ShapeReverse</c> intercepts NEGATIVE throttle at rest and holds
    /// neutral for ≥0.2 s before letting reverse through. No test commands
    /// negative throttle — braking uses the brake channel, which is separate —
    /// so the blip never fires and no measurement contains it.
    /// </summary>
    public sealed class ScriptedDriver : IDriverInputSource
    {
        /// <summary>Normalised drive demand [0, 1]. Keep this non-negative: see
        /// the ShapeReverse note on the class.</summary>
        public float throttle;
        /// <summary>Steer demand [-1, 1]; the car scales it by its own
        /// steerAngle and rate limit.</summary>
        public float steer;
        /// <summary>Brake demand [0, 1], scaled by maxBrakeTorque per wheel.</summary>
        public float brake;
        /// <summary>Handbrake. Read at frame rate by CarInput, not at control
        /// rate — irrelevant for a test that holds it for seconds (P6b).</summary>
        public bool handbrake;

        /// <summary>Drop every input. Called between phases so a test cannot
        /// leak throttle from its arming phase into its measurement.</summary>
        public void Neutral()
        {
            throttle = 0f;
            steer = 0f;
            brake = 0f;
            handbrake = false;
        }

        public float Throttle() => throttle;
        public float Steer() => steer;
        public float Brake() => brake;
        public bool Handbrake() => handbrake;

        // Everything a race needs and a measurement does not. Respawn is the one
        // that matters: it teleports the car to its spawn, which mid-coastdown
        // would look like an impossible result rather than an obvious fault.
        public bool RespawnPressed() => false;
        public bool UseItemPressed() => false;
        public bool LookBackHeld() => false;
        public bool HornHeld() => false;
        public bool JumpPressed() => false;
        public bool BoostHeld() => false;
        public float MouseSteerDelta() => 0f;
    }
}
