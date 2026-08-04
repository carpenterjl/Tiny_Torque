using UnityEngine;

namespace AIHWSim.Core.Config
{
    /// <summary>
    /// What a level's rules are, as an asset you can edit in the Inspector.
    ///
    /// Today those rules live in <see cref="SessionConfig"/>, a static carrier
    /// the menu writes on its way into a scene. That works for the menu and
    /// nothing else: pressing Play directly in TTA_Monza gets whatever
    /// <c>SessionConfig</c> happened to be left holding, which is why
    /// <c>TrackBootstrap.Awake</c> already carries a hand-written branch that
    /// repairs it. This is that branch's data, promoted out of code.
    ///
    /// <b>Every default here is the value <see cref="SessionConfig.SetSinglePlayer"/>
    /// writes</b>, so a freshly created asset describes the session a legacy
    /// entry path already produced. That is not a coincidence to preserve
    /// casually — it is what makes "assign an asset" a visible change and
    /// "assign nothing" a no-op.
    ///
    /// <b>It is defaults, not overrides.</b> The scene descriptor applies these
    /// only when nobody has chosen otherwise — a menu-driven session has
    /// already populated <c>SessionConfig</c> and keeps every value it chose.
    /// A level asset never overrules a player.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Level Settings", fileName = "LevelSettings")]
    public sealed class LevelSettings : ScriptableObject
    {
        [Header("Rules")]
        [Tooltip("What the session IS: a race, one of the arena mini-games, or free roam. " +
                 "An arena mode needs a spawn ring on the map and a race needs a finish " +
                 "line; a mode the map cannot support refuses to compose rather than " +
                 "half-running.")]
        public MatchMode match = MatchMode.Race;

        [Tooltip("Laps to win. 0 = free drive with no race at all, which is what every " +
                 "legacy entry path produced.")]
        [Min(0)] public int targetLaps = 0;

        [Tooltip("Captures or goals that end a CTF or soccer match. Ignored by a race.")]
        [Min(1)] public int targetScore = 3;

        [Tooltip("Match clock in seconds. 0 = no time limit.")]
        [Min(0)] public int timeLimitSec = 0;

        [Header("Race start and finish")]
        [Tooltip("Countdown before the lights go out. 0 = go immediately.")]
        [Min(0)] public int countdownSeconds = 0;

        [Tooltip("How long the race keeps running after the FIRST car finishes. " +
                 "0 waits for the whole field, which only ends if the whole field can " +
                 "finish — a bot wedged against a wall never does, and that is why the " +
                 "shipped default is 30 rather than 0.")]
        [Min(0)] public int resultsWaitSeconds = 30;

        [Tooltip("Catch-up assist for bot opponents.")]
        public bool rubberBand = false;

        [Header("Opponents, when this scene is entered directly")]
        /// <summary>
        /// AI cars to put on the grid when nobody outside the scene chose a
        /// roster — pressing Play in the Editor, and nothing else.
        ///
        /// 0 is the shipped behaviour exactly: one car, yours. It is here because
        /// the mini-game modes are not demonstrable without opponents — a soccer
        /// pitch with a single car and no teams composes fine and is not a match —
        /// and because a scene that IS a soccer level has an opinion about that
        /// which no menu is around to ask for.
        ///
        /// Never applied to a session the menu, a championship, a LAN join or a
        /// snapshot resume chose: those all arrive through <c>GameFlow.LoadTrack</c>
        /// with a roster already built, and a level asset never overrules a player.
        /// </summary>
        [Tooltip("AI opponents added on a direct Play only. 0 = just your car, " +
                 "which is what every entry path produces today. A team mode " +
                 "alternates sides down the grid, so an even number gives even teams.")]
        [Range(0, 7)] public int botOpponents = 0;

        [Tooltip("0 Easy / 1 Medium / 2 Hard, for the opponents above.")]
        [Range(0, 2)] public int botDifficulty = 1;

        [Header("Arcade layer")]
        [Tooltip("Item boxes, power-ups, weapons and the arcade scoreboard. " +
                 "Needs a finish line and a lap count: power-ups in a free drive would " +
                 "be meaningless, so the layer simply does not compose without them.")]
        public bool arcade = false;

        [Tooltip("Off-track detection and penalty. A sub-toggle of arcade.")]
        public bool trackLimits = false;

        [Tooltip("Raise every car in the session — humans and bots alike — to an assist " +
                 "floor and a grip baseline, so a themed circuit can be driven on a " +
                 "keyboard without catching a slide. False races the same map on the " +
                 "honest brush-tyre model. On by default because that is what the menu " +
                 "offers, not because it is more correct.")]
        public bool arcadeHandling = true;

        [Header("The car, when nothing else chose one")]
        [Garage.VehiclePreset(true, "(none — the bootstrap's own default)")]
        [Tooltip("Preset name to drive when this scene is entered directly rather than " +
                 "through the garage or the menu. Empty falls back to whatever the " +
                 "bootstrap's own default is. A design already chosen — by the menu, by " +
                 "a snapshot, or by DebugVehicleSpawner — always wins over this.\n\n" +
                 "Read once, when the scene loads. Editing it during Play does not " +
                 "swap the car you are already driving; to tune THAT car, select it in " +
                 "the hierarchy and use its Live Car Tuner.")]
        public string defaultDesignName = "TT Patrol";

        /// <summary>
        /// Write these rules into <see cref="SessionConfig"/>.
        ///
        /// Unconditional by design: the decision about WHETHER a level's
        /// defaults should be applied belongs to the caller (which knows whether
        /// a menu already spoke), not to the asset. Splitting it that way is
        /// what keeps this type a plain description of a level rather than a
        /// second copy of the entry-path rules.
        /// </summary>
        public void ApplyTo()
        {
            SessionConfig.Match = match;
            SessionConfig.TargetLaps = Mathf.Max(0, targetLaps);
            SessionConfig.TargetScore = Mathf.Max(1, targetScore);
            SessionConfig.TimeLimitSec = Mathf.Max(0, timeLimitSec);
            SessionConfig.CountdownSeconds = Mathf.Max(0, countdownSeconds);
            SessionConfig.ResultsWaitSeconds = Mathf.Max(0, resultsWaitSeconds);
            SessionConfig.RubberBand = rubberBand;
            SessionConfig.Arcade = arcade;
            SessionConfig.TrackLimits = trackLimits;
            SessionConfig.ArcadeHandling = arcadeHandling;
        }
    }
}
