namespace AIHWSim.Modes
{
    /// <summary>
    /// Every tunable the mini-game modes have, in one file — the same shape as
    /// <c>ArcadeConfig</c>, and for the same reason: numbers that decide how a
    /// mode FEELS belong somewhere a person can read them all at once, not
    /// scattered through the director that happens to use each one.
    ///
    /// Each value below is "the installed asset's field, or the literal that
    /// shipped". The literal lives here, in a private const beside its accessor,
    /// rather than only in the asset — so a project with no asset anywhere is not
    /// merely equivalent to the old code, it runs the same numbers from the same
    /// file. See <see cref="ModeConfigOverride"/> for the asset, and <c>[DSC]</c>
    /// for the check that keeps the two copies honest.
    ///
    /// Units are the game's: metres, seconds, m/s, and the 1:10 RC scale (a car
    /// is 0.42 m long and about 1.6 kg, and 8 m/s is quick).
    /// </summary>
    public static class ModeConfig
    {
        /// <summary>
        /// The asset a scene has chosen to tune the modes with, or null — and
        /// null is what every value below falls back to its shipped literal for.
        /// Installed by <c>DrivingSceneDescriptor</c>; cleared by setting it back
        /// to null, which restores the defaults in one assignment because nothing
        /// here caches or copies them.
        ///
        /// Static, and so it outlives a scene load within a session — the same
        /// contract <c>AssistTuning.Override</c> already has, for the same
        /// reason: there is one physics world and one set of rules in it.
        /// </summary>
        public static ModeConfigOverride Override;

        // ---- demolition derby -----------------------------------------------

        private const float DerbyMaxHealthDefault = 100f;
        private const float ImpactMinSpeedDefault = 1.6f;
        private const float ImpactRefSpeedDefault = 7.0f;
        private const float RamDamageDefault = 34f;
        private const float SideDamageDefault = 7f;
        private const float WallDamageDefault = 9f;
        private const float RamAlignmentDefault = 0.72f;
        private const float HitCooldownDefault = 0.35f;
        private const float SpawnGraceDefault = 2.0f;

        /// <summary>Starting health, and the number the HUD bar is a fraction of.</summary>
        public static float DerbyMaxHealth =>
            Override != null ? Override.derbyMaxHealth : DerbyMaxHealthDefault;

        /// <summary>Below this closing speed a touch does nothing at all, so
        /// jostling in a corner is not a slow death.</summary>
        public static float ImpactMinSpeed =>
            Override != null ? Override.impactMinSpeed : ImpactMinSpeedDefault;

        /// <summary>Closing speed that deals full head-on damage; faster than
        /// this is clamped, so a boost pad cannot one-shot anybody.</summary>
        public static float ImpactRefSpeed =>
            Override != null ? Override.impactRefSpeed : ImpactRefSpeedDefault;

        /// <summary>Damage a square, full-speed ram deals to the car being hit.</summary>
        public static float RamDamage =>
            Override != null ? Override.ramDamage : RamDamageDefault;

        /// <summary>Damage a side-on or glancing car-to-car hit deals to BOTH
        /// cars. Trading paint costs you something too — that is what stops the
        /// derby from being a game of chicken.</summary>
        public static float SideDamage =>
            Override != null ? Override.sideDamage : SideDamageDefault;

        /// <summary>Damage for slamming a wall, scaled the same way.</summary>
        public static float WallDamage =>
            Override != null ? Override.wallDamage : WallDamageDefault;

        /// <summary>How square a hit has to be to count as a ram: the dot of the
        /// attacker's forward against the contact normal. 0.72 is about 44°.</summary>
        public static float RamAlignment =>
            Override != null ? Override.ramAlignment : RamAlignmentDefault;

        /// <summary>One damage event per car pair per this many seconds, so a
        /// single shunt that generates six contacts is still one hit.</summary>
        public static float HitCooldown =>
            Override != null ? Override.hitCooldown : HitCooldownDefault;

        /// <summary>Seconds a freshly (re)spawned car cannot be damaged.</summary>
        public static float SpawnGrace =>
            Override != null ? Override.spawnGrace : SpawnGraceDefault;

        // ---- pickups ---------------------------------------------------------

        private const float HealthPackHealDefault = 35f;
        private const float PickupRespawnSecDefault = 12f;
        private const float MineRadiusDefault = 1.1f;
        private const float MineDamageDefault = 45f;
        private const float MineOwnerGraceDefault = 1.5f;

        public static float HealthPackHeal =>
            Override != null ? Override.healthPackHeal : HealthPackHealDefault;
        public static float PickupRespawnSec =>
            Override != null ? Override.pickupRespawnSec : PickupRespawnSecDefault;

        /// <summary>Radius of the mine's blast, and what it costs at the centre.</summary>
        public static float MineRadius =>
            Override != null ? Override.mineRadius : MineRadiusDefault;
        public static float MineDamage =>
            Override != null ? Override.mineDamage : MineDamageDefault;

        /// <summary>The owner cannot trip their own mine for this long.</summary>
        public static float MineOwnerGrace =>
            Override != null ? Override.mineOwnerGrace : MineOwnerGraceDefault;

        // ---- capture the flag -------------------------------------------------

        private const float FlagTouchRadiusDefault = 0.55f;
        private const float FlagDropImpulseDefault = 2.4f;
        private const float FlagAutoReturnSecDefault = 20f;
        private const float CarrierDriveMultDefault = 0.92f;

        /// <summary>How close a car must get to pick a flag up or score.</summary>
        public static float FlagTouchRadius =>
            Override != null ? Override.flagTouchRadius : FlagTouchRadiusDefault;

        /// <summary>An impact this hard knocks the flag out of a carrier's hands.</summary>
        public static float FlagDropImpulse =>
            Override != null ? Override.flagDropImpulse : FlagDropImpulseDefault;

        /// <summary>A dropped flag returns itself home after this long, so a
        /// flag punted into a corner does not end the match.</summary>
        public static float FlagAutoReturnSec =>
            Override != null ? Override.flagAutoReturnSec : FlagAutoReturnSecDefault;

        /// <summary>Carrying is meant to be a risk: the carrier is slowed.</summary>
        public static float CarrierDriveMult =>
            Override != null ? Override.carrierDriveMult : CarrierDriveMultDefault;

        // ---- soccer -----------------------------------------------------------

        private const float BallRadiusDefault = 0.13f;
        private const float BallMassDefault = 0.35f;
        private const float BallDragDefault = 0.25f;
        private const float BallAngularDragDefault = 0.35f;
        private const float BallHitBoostDefault = 1.35f;
        private const float BallGravityScaleDefault = 1f;
        private const float GoalCelebrationSecDefault = 3.0f;

        public static float BallRadius =>
            Override != null ? Override.ballRadius : BallRadiusDefault;
        public static float BallMass =>
            Override != null ? Override.ballMass : BallMassDefault;
        public static float BallDrag =>
            Override != null ? Override.ballDrag : BallDragDefault;
        public static float BallAngularDrag =>
            Override != null ? Override.ballAngularDrag : BallAngularDragDefault;

        /// <summary>Extra kick a car imparts beyond the raw collision, so a
        /// touch reads as a strike rather than a nudge.</summary>
        public static float BallHitBoost =>
            Override != null ? Override.ballHitBoost : BallHitBoostDefault;

        /// <summary>Gravity felt by the ball as a multiple of the world's, so a
        /// hang-time ball is available without lifting the cars off the floor.
        /// 1 is the default and costs nothing: <c>SoccerBall</c> leaves the
        /// Rigidbody's own gravity alone and adds no force at all.</summary>
        public static float BallGravityScale =>
            Override != null ? Override.ballGravityScale : BallGravityScaleDefault;

        /// <summary>Seconds of celebration between a goal and the kick-off.</summary>
        public static float GoalCelebrationSec =>
            Override != null ? Override.goalCelebrationSec : GoalCelebrationSecDefault;

        // ---- aerial -----------------------------------------------------------

        private const float JumpImpulseDefault = 8.6f;
        private const float DoubleJumpImpulseDefault = 6.0f;
        private const float FlipWindowSecDefault = 1.25f;
        private const float FlipImpulseDefault = 3.2f;
        private const float FlipTorqueDefault = 1.055f;
        private const float AirTorqueDefault = 0.05f;
        private const float BoostTankSecDefault = 2.4f;
        private const float BoostRefillPerSecDefault = 0.55f;
        private const float BoostAccelDefault = 11f;

        /// <summary>Upward impulse of the first jump, in N·s.</summary>
        public static float JumpImpulse =>
            Override != null ? Override.jumpImpulse : JumpImpulseDefault;

        /// <summary>The second jump is weaker — it is a correction, not a lift.</summary>
        public static float DoubleJumpImpulse =>
            Override != null ? Override.doubleJumpImpulse : DoubleJumpImpulseDefault;

        /// <summary>Window after the first jump in which a second press flips
        /// instead of jumping, if a direction is held.</summary>
        public static float FlipWindowSec =>
            Override != null ? Override.flipWindowSec : FlipWindowSecDefault;

        /// <summary>Flip impulse along the held direction, and the torque that
        /// makes it read as a barrel roll rather than a shove.</summary>
        public static float FlipImpulse =>
            Override != null ? Override.flipImpulse : FlipImpulseDefault;
        public static float FlipTorque =>
            Override != null ? Override.flipTorque : FlipTorqueDefault;

        /// <summary>Air-roll authority, in N·m per unit of stick.</summary>
        public static float AirTorque =>
            Override != null ? Override.airTorque : AirTorqueDefault;

        /// <summary>Boost meter: full tank in seconds, and how fast a pad fills it.</summary>
        public static float BoostTankSec =>
            Override != null ? Override.boostTankSec : BoostTankSecDefault;
        public static float BoostRefillPerSec =>
            Override != null ? Override.boostRefillPerSec : BoostRefillPerSecDefault;
        public static float BoostAccel =>
            Override != null ? Override.boostAccel : BoostAccelDefault;

        // ---- shared ------------------------------------------------------------

        private const float DeathBeatSecDefault = 1.6f;
        private const float ArenaGravityScaleDefault = 1f;

        /// <summary>Seconds a dead car spectates before the match notices, so a
        /// kill has a beat to land.</summary>
        public static float DeathBeatSec =>
            Override != null ? Override.deathBeatSec : DeathBeatSecDefault;

        /// <summary>World gravity while an arena match runs, as a multiple of the
        /// project's own. 1 is the default and is a no-op by construction —
        /// <c>ArenaGravity</c> writes nothing at all until it differs. Owned and
        /// restored by that component, never by the modes themselves.</summary>
        public static float ArenaGravityScale =>
            Override != null ? Override.arenaGravityScale : ArenaGravityScaleDefault;
    }
}
