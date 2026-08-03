using AIHWSim.Core.Config;
using UnityEngine;

namespace AIHWSim.Core.Boot
{
    /// <summary>
    /// What a driving scene wants, authored in the scene rather than in code.
    ///
    /// Sits beside <c>TrackBootstrap</c> and <c>SceneTrackDescriptor</c> and
    /// completes the pair they already form: the track descriptor says what the
    /// map IS (its geometry, its surfaces, its racing line), and this says what
    /// a session ON it should be — which rules, which solver, which assist
    /// numbers, and which car when nobody else chose one.
    ///
    /// This is the same contract <c>FlightSceneDescriptor</c> has on the
    /// aircraft side, and it is deliberately the same shape: a plain component
    /// of references that a bootstrap consults, not a second bootstrap.
    ///
    /// <b>The whole design is in one distinction.</b>
    ///
    /// <list type="bullet">
    /// <item><b>World tuning is applied always.</b> The solver and the assist
    /// numbers are properties of the level, not of who chose to play it — a
    /// scene whose suspension needs a 2 ms step needs it whether you arrived
    /// from the menu or from the Play button. <see cref="InstallGlobals"/> runs
    /// unconditionally in Awake.</item>
    /// <item><b>Rules are defaults, never overrides.</b> Laps, countdown, mode
    /// and the arcade layer are the player's choice, and the menu has already
    /// made it by the time a scene loads. <see cref="ApplyLevelDefaults"/> is
    /// therefore called by <c>TrackBootstrap</c> from ONE place — the branch
    /// that already exists for "someone pressed Play directly in an authored
    /// scene, so there was no menu to have chosen anything".</item>
    /// </list>
    ///
    /// <b>Why not a condition of its own.</b> Deciding "was this session
    /// menu-driven?" from the outside is harder than it looks: a single-player
    /// race leaves <c>SessionConfig.Players</c> empty exactly as a direct Play
    /// does, and a menu-chosen procedural oval leaves both track carriers null
    /// exactly as a direct Play into TrackScene does. The bootstrap's existing
    /// branch is not a heuristic — it is reached only when nothing outside this
    /// scene named a track AND the scene names itself one, which the menu never
    /// produces. Borrowing it is strictly safer than inventing a second rule
    /// that has to agree with it forever.
    ///
    /// The consequence, stated rather than discovered: <b>in TrackScene itself
    /// — the procedural oval and the tile maps — the level rules are not
    /// applied</b>, because that branch does not fire there. The solver and
    /// assist assets still are.
    /// </summary>
    [DefaultExecutionOrder(-4000)]
    public sealed class DrivingSceneDescriptor : MonoBehaviour
    {
        [Header("Rules (defaults for a direct Play; the menu always wins)")]
        [Tooltip("Mode, laps, countdown, arcade layer. Leave empty to keep the " +
                 "bootstrap's own behaviour exactly as it is today.")]
        public LevelSettings level;

        [Header("World (applied whatever chose this session)")]
        [Tooltip("Physics step, control rate and the PhysX solver globals. Leave empty " +
                 "for the shipped 1/10-scale tuning.")]
        public PhysicsSettings physics;

        [Tooltip("The numbers behind traction control, ABS, the ESC and the launch " +
                 "governor — what an assist DOES, not how much of it a player asked " +
                 "for. Leave empty for the shipped values. Editable while playing: " +
                 "nothing caches these, so a slider drag lands on the next physics step.")]
        public Vehicles.AssistTuningOverride assists;

        private void Awake() => InstallGlobals();

        /// <summary>
        /// Push the level's world tuning at the engine. Safe in any session and
        /// safe to call twice — every line is an assignment, and every one of
        /// them is a no-op when its asset is null.
        ///
        /// Runs at −4000 so it lands before any <c>SimulationRunner</c> exists,
        /// and after <c>DebugVehicleSpawner</c> (−5000) has had its say about
        /// which car is being built.
        /// </summary>
        public void InstallGlobals()
        {
            Vehicles.AssistTuning.Override = assists;
            if (physics != null) PhysicsTuning.Apply(physics);
        }

        /// <summary>
        /// Write this level's rules into <see cref="SessionConfig"/>.
        ///
        /// Called only from <c>TrackBootstrap</c>'s adopt-this-scene branch —
        /// see the class note for why the condition lives there and not here.
        /// Returns the preset name the scene wants driven, or null when it has
        /// no opinion and the caller should keep its own default.
        /// </summary>
        public string ApplyLevelDefaults()
        {
            if (level == null) return null;
            level.ApplyTo();
            return string.IsNullOrWhiteSpace(level.defaultDesignName)
                ? null : level.defaultDesignName;
        }

        /// <summary>The scene's physics rate, or <paramref name="fallback"/> when
        /// it does not name one. Kept as a pair with <see cref="ControlRate"/> so a
        /// caller cannot take one from the asset and the other from itself.</summary>
        public int PhysicsRate(int fallback) => physics != null ? physics.physicsRateHz : fallback;

        /// <summary>The scene's control rate, or <paramref name="fallback"/>.</summary>
        public int ControlRate(int fallback) => physics != null ? physics.controlRateHz : fallback;

        /// <summary>The one in the loaded scenes, or null. A scene track loads
        /// itself single and pulls TrackScene in additively on top, so the
        /// descriptor and the bootstrap that consults it routinely live in
        /// different scenes — which is why this is a global find rather than a
        /// <c>GetComponent</c>.</summary>
        public static DrivingSceneDescriptor Find() =>
            FindFirstObjectByType<DrivingSceneDescriptor>();
    }
}
