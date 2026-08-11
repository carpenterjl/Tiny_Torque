namespace AIHWSim.Tutorial
{
    /// <summary>
    /// What has to happen before a step is done.
    ///
    /// APPEND-ONLY: the value is serialized into every tutorial scene, and
    /// inserting one renumbers the rest — which would silently turn "drive
    /// through the gate" into "hold the brake" in a scene nobody reopened.
    /// </summary>
    public enum TutorialCondition
    {
        /// <summary>The player's car entered a <see cref="TutorialTrigger"/>.</summary>
        TriggerVolume = 0,

        /// <summary>An input held past a threshold for long enough. The
        /// device-agnostic one: it reads the driver input source, so it is
        /// satisfied by a keyboard, a pad or a remapped binding alike.</summary>
        InputHeld = 1,

        /// <summary>Car speed reached (m/s, forward).</summary>
        SpeedReached = 2,

        /// <summary>Nothing to do; the step ends after <c>seconds</c>. For the
        /// ones that are pure explanation and want reading time.</summary>
        Timer = 3,

        /// <summary>A "Continue" button on the objective panel. The other pure
        /// explanation option, for text long enough that a timer would either
        /// rush a slow reader or bore a fast one.</summary>
        Continue = 4,

        /// <summary>A named token was raised by game code —
        /// <c>TutorialSignals.Raise("garage:painted")</c>. The general hook: one
        /// call at a call site teaches a step about anything the game can do.</summary>
        Signal = 5,

        /// <summary>The host UI reported reaching a named screen, e.g.
        /// <c>"menu:LanHost"</c>.</summary>
        ScreenReached = 6,

        /// <summary>A LAN session is up and this machine is hosting it.</summary>
        LobbyHosted = 7,

        /// <summary>The IPC bridge has a client connected.</summary>
        IpcConnected = 8,

        /// <summary>A telemetry channel exists and is producing values.</summary>
        TelemetryObserved = 9,
    }

    /// <summary>
    /// One step, flattened away from wherever it was authored.
    ///
    /// The driving tutorials author steps as scene objects (see
    /// <see cref="TutorialStep"/>) and the overlay tutorials as C# lists (see
    /// <c>TutorialScripts</c>); both convert to this, so
    /// <see cref="TutorialStepEngine"/> has exactly one shape to run and the two
    /// kinds of tutorial cannot drift apart in what a step can be.
    ///
    /// A plain class rather than a struct because the engine holds one per step
    /// and copies of a 9-field struct through a List indexer is a papercut with
    /// no upside at these sizes.
    /// </summary>
    public sealed class TutorialStepData
    {
        /// <summary>Heading on the objective panel, e.g. "Get moving".</summary>
        public string title = "";

        /// <summary>The explanation under it. Placeholders like <c>{throttle}</c>
        /// are expanded by <see cref="TutorialText"/> at draw time, never here —
        /// the right label depends on what the player last touched.</summary>
        public string body = "";

        /// <summary>Big centre-screen flash when the step completes. Empty for
        /// no flash, which is right for steps that just roll into the next one.</summary>
        public string banner = "";

        public TutorialCondition condition;

        /// <summary>The volume for <see cref="TutorialCondition.TriggerVolume"/>.</summary>
        public TutorialTrigger trigger;

        /// <summary>The axis for <see cref="TutorialCondition.InputHeld"/>.</summary>
        public TutorialInput input;

        /// <summary>Threshold: the axis level for InputHeld (0..1), the speed for
        /// SpeedReached (m/s). Ignored by the rest.</summary>
        public float amount;

        /// <summary>Seconds: the hold for InputHeld, the wait for Timer, and for
        /// every other condition a minimum dwell before it may pass — so a step
        /// whose condition is already true when it starts still gets read.</summary>
        public float seconds;

        /// <summary>The token for Signal, the screen id for ScreenReached, the
        /// channel name for TelemetryObserved.</summary>
        public string token = "";
    }

    /// <summary>
    /// A driver input a step can ask for, named by intent rather than by key so
    /// the condition holds however the player has their controls bound.
    ///
    /// APPEND-ONLY — serialized in scenes.
    /// </summary>
    public enum TutorialInput
    {
        Throttle = 0,
        Brake = 1,
        SteerLeft = 2,
        SteerRight = 3,
        SteerEither = 4,
        Handbrake = 5,
        Respawn = 6,
        Horn = 7,
        Jump = 8,
        Boost = 9,
        UseItem = 10,
        LookBack = 11,
    }
}
