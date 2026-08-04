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
    /// therefore called by <c>TrackBootstrap</c> from ONE place, gated on
    /// <c>GameFlow.LaunchedFromMenu</c> being false.</item>
    /// </list>
    ///
    /// <b>What "nobody else chose this" means, exactly.</b> Not a heuristic over
    /// session state — that was tried and it was wrong. A single-player race
    /// leaves <c>SessionConfig.Players</c> empty exactly as a direct Play does,
    /// and a menu-chosen oval leaves both track carriers null exactly as a
    /// direct Play into TrackScene does. The honest signal is the entry point:
    /// <c>GameFlow.LoadTrack</c> is the only <c>LoadScene</c> call in the
    /// project that reaches a track, so every externally chosen session passes
    /// through it and a Play button passes through nothing.
    ///
    /// The first version of this borrowed a narrower condition — "the scene
    /// names itself a track" — and it under-covered by exactly the scenes that
    /// most want authoring: a driving scene whose geometry comes from a tile map
    /// or a terrain has no <c>SceneTrackDescriptor</c>, so its rules and its
    /// <c>defaultDesignName</c> were read and silently dropped while the solver
    /// and assist assets applied. Half-working is the worst outcome available,
    /// because the half that works is the half nobody looks at.
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

        [Tooltip("How the arena mini-games feel: derby health and hit strength, mine and " +
                 "pickup strengths, the ball's weight and gravity, jump and flip " +
                 "impulses, and the arena-wide gravity scale. Leave empty for the " +
                 "shipped values. Editable while playing — the numbers are read where " +
                 "they are used, and the two that are not (the ball's Rigidbody, each " +
                 "car's health bar) re-apply themselves on the edit.")]
        public Modes.ModeConfigOverride modes;

        [Tooltip("How the arcade layer feels: the arcade handling floor (grip, drive " +
                 "scale, downforce, stability boost), the whole drift model, boost, what " +
                 "being hit does, the missile's chase and the slipstream. Leave empty " +
                 "for the shipped values. Editable while playing: HandlingFloor " +
                 "re-asserts its channels every frame and the drift controller reads its " +
                 "torques every physics step.")]
        public Arcade.ArcadeConfigOverride arcade;

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
            Modes.ModeConfig.Override = modes;
            Arcade.ArcadeConfig.Override = arcade;
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
