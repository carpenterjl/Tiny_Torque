using System.Collections.Generic;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// The general-purpose hook a tutorial step waits on: game code raises a
    /// named token, a step asks whether it has been raised.
    ///
    /// One <c>Raise</c> call at a call site teaches a step about anything the
    /// game can do — saving a vehicle, painting one, opening the pause menu —
    /// without that call site knowing what a tutorial is. That indirection is the
    /// point: the garage does not import the tutorial system, it just says what
    /// it did.
    ///
    /// Tokens LATCH rather than fire, and are cleared per step. A player can do
    /// the thing before the step asks for it (paint the car, then reach the step
    /// about painting), and a latch means the step completes instead of asking
    /// them to do it twice. The engine clears the set when a step starts, so the
    /// latch only ever reaches back to the beginning of the current step.
    ///
    /// Screens are separate from tokens because a screen is a STATE, not an
    /// event: the host reports where it is every frame, and a step asking for
    /// "menu:LanHost" is asking where the player is now, not where they have
    /// been.
    /// </summary>
    public static class TutorialSignals
    {
        private static readonly HashSet<string> Raised = new HashSet<string>();

        /// <summary>Where the host UI currently is, e.g. "menu:LanHost". Set
        /// every frame by whatever is drawing; "" when nothing reports.</summary>
        public static string Screen { get; private set; } = "";

        /// <summary>Say that something happened. Cheap and safe to call from
        /// anywhere, including when no tutorial is running.</summary>
        public static void Raise(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            Raised.Add(token);
        }

        public static bool WasRaised(string token) =>
            !string.IsNullOrEmpty(token) && Raised.Contains(token);

        /// <summary>Report the current screen. Called from the host's Layout
        /// pass, so it is one value per frame rather than two.</summary>
        public static void NotifyScreen(string screen) => Screen = screen ?? "";

        public static bool OnScreen(string screen) =>
            !string.IsNullOrEmpty(screen) && Screen == screen;

        /// <summary>Forget every latched token. The engine calls this as each
        /// step begins.</summary>
        public static void Clear() => Raised.Clear();
    }
}
