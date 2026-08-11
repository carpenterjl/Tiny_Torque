using System.Collections.Generic;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// The steps for the overlay tutorials, as code.
    ///
    /// The driving lessons author their steps as objects in their scene, because
    /// those steps are about PLACES and want to sit next to the place in the
    /// scene view. These two are about screens. There is no scene to put them in,
    /// so they live here in the code-as-data style the rest of the project's
    /// catalogues use — greppable, diffable, and impossible to empty by losing an
    /// asset reference.
    ///
    /// The tokens they wait on (<c>garage:*</c>) are raised by the garage's own
    /// code through <see cref="TutorialSignals"/>. That indirection is what keeps
    /// the garage from importing the tutorial system: it says what it did, and
    /// whether anything is listening is not its problem.
    /// </summary>
    public static class TutorialScripts
    {
        public static List<TutorialStepData> For(string id) => id switch
        {
            "customize" => Customize(),
            "online" => Online(),
            _ => new List<TutorialStepData>(),
        };

        /// <summary>Is there a script for this id? The [TUT] gate asks, so a
        /// catalogue row with no scene AND no script cannot ship.</summary>
        public static bool Has(string id) => For(id).Count > 0;

        private static List<TutorialStepData> Customize() => new List<TutorialStepData>
        {
            new TutorialStepData
            {
                title = "This is the garage",
                body = "Every car in the game is a design: a chassis, a motor, a " +
                       "battery, wheels and sensors, all written down in a file you " +
                       "can read. The garage is where you change those numbers and " +
                       "see what they do.",
                condition = TutorialCondition.Continue,
            },
            new TutorialStepData
            {
                title = "Start from something that works",
                body = "Load one of the built-in presets. Starting from a car that " +
                       "already drives is faster than assembling one from nothing, " +
                       "and you can change every part of it afterwards.",
                condition = TutorialCondition.Signal,
                token = "garage:preset_loaded",
                banner = "Loaded",
            },
            new TutorialStepData
            {
                title = "Make it yours",
                body = "Pick a colour. Paint is the one change that costs nothing " +
                       "in performance — everything else on this screen is a " +
                       "trade.",
                condition = TutorialCondition.Signal,
                token = "garage:painted",
                banner = "Nice.",
            },
            new TutorialStepData
            {
                title = "Save it",
                body = "Saving writes a real design file into your Vehicles folder. " +
                       "It shows up in the car picker on every screen from now on, " +
                       "and you can hand the file to somebody else.",
                condition = TutorialCondition.Signal,
                token = "garage:saved",
                banner = "Saved",
            },
            new TutorialStepData
            {
                title = "That is the whole loop",
                body = "Change something, drive it, change it again. The Vehicle " +
                       "Studio next door does the same for the car's SHAPE — body, " +
                       "parts and paint — if you want to go further than numbers.",
                condition = TutorialCondition.Timer,
                seconds = 6f,
                banner = "Garage — done",
            },
        };

        private static List<TutorialStepData> Online() => new List<TutorialStepData>
        {
            new TutorialStepData
            {
                title = "Racing on your network",
                body = "Two copies of the game on the same network can race each " +
                       "other. No account, no server, nothing to sign up for — one " +
                       "machine hosts and the others find it.",
                condition = TutorialCondition.Continue,
            },
            new TutorialStepData
            {
                title = "Open Multiplayer",
                body = "Everything to do with playing together lives on one screen.",
                condition = TutorialCondition.ScreenReached,
                token = "menu:Multiplayer",
            },
            new TutorialStepData
            {
                title = "Host a game",
                body = "The host picks the track and the rules, and its machine is " +
                       "the one that says when the race starts. Every car is still " +
                       "simulated on the machine driving it — so your car always " +
                       "feels like yours, whatever the network is doing.",
                condition = TutorialCondition.ScreenReached,
                token = "menu:LanHost",
            },
            new TutorialStepData
            {
                title = "Start the lobby",
                body = "Open it now, with nobody else on it. Friends who launch the " +
                       "game on this network will see it appear in their Join list " +
                       "on their own — there is no address to type in.",
                condition = TutorialCondition.LobbyHosted,
                banner = "You're hosting",
            },
            new TutorialStepData
            {
                title = "That is all there is to it",
                body = "Leave the lobby whenever you like. If a friend's game cannot " +
                       "see yours, it is nearly always a firewall prompt that got " +
                       "dismissed rather than anything in here.",
                condition = TutorialCondition.Timer,
                seconds = 7f,
                banner = "Online — done",
            },
        };
    }
}
