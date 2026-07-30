namespace AIHWSim.Modes
{
    /// <summary>
    /// Every tunable the mini-game modes have, in one file — the same shape as
    /// <c>ArcadeConfig</c>, and for the same reason: numbers that decide how a
    /// mode FEELS belong somewhere a person can read them all at once, not
    /// scattered through the director that happens to use each one.
    ///
    /// Units are the game's: metres, seconds, m/s, and the 1:10 RC scale (a car
    /// is 0.42 m long and about 1.6 kg, and 8 m/s is quick).
    /// </summary>
    public static class ModeConfig
    {
        // ---- demolition derby -----------------------------------------------

        /// <summary>Starting health, and the number the HUD bar is a fraction of.</summary>
        public const float DerbyMaxHealth = 100f;

        /// <summary>Below this closing speed a touch does nothing at all, so
        /// jostling in a corner is not a slow death.</summary>
        public const float ImpactMinSpeed = 1.6f;

        /// <summary>Closing speed that deals full head-on damage; faster than
        /// this is clamped, so a boost pad cannot one-shot anybody.</summary>
        public const float ImpactRefSpeed = 7.0f;

        /// <summary>Damage a square, full-speed ram deals to the car being hit.</summary>
        public const float RamDamage = 34f;

        /// <summary>Damage a side-on or glancing car-to-car hit deals to BOTH
        /// cars. Trading paint costs you something too — that is what stops the
        /// derby from being a game of chicken.</summary>
        public const float SideDamage = 7f;

        /// <summary>Damage for slamming a wall, scaled the same way.</summary>
        public const float WallDamage = 9f;

        /// <summary>How square a hit has to be to count as a ram: the dot of the
        /// attacker's forward against the contact normal. 0.72 is about 44°.</summary>
        public const float RamAlignment = 0.72f;

        /// <summary>One damage event per car pair per this many seconds, so a
        /// single shunt that generates six contacts is still one hit.</summary>
        public const float HitCooldown = 0.35f;

        /// <summary>Seconds a freshly (re)spawned car cannot be damaged.</summary>
        public const float SpawnGrace = 2.0f;

        // ---- pickups ---------------------------------------------------------

        public const float HealthPackHeal = 35f;
        public const float PickupRespawnSec = 12f;

        /// <summary>Radius of the mine's blast, and what it costs at the centre.</summary>
        public const float MineRadius = 1.1f;
        public const float MineDamage = 45f;

        /// <summary>The owner cannot trip their own mine for this long.</summary>
        public const float MineOwnerGrace = 1.5f;

        // ---- capture the flag -------------------------------------------------

        /// <summary>How close a car must get to pick a flag up or score.</summary>
        public const float FlagTouchRadius = 0.55f;

        /// <summary>An impact this hard knocks the flag out of a carrier's hands.</summary>
        public const float FlagDropImpulse = 2.4f;

        /// <summary>A dropped flag returns itself home after this long, so a
        /// flag punted into a corner does not end the match.</summary>
        public const float FlagAutoReturnSec = 20f;

        /// <summary>Carrying is meant to be a risk: the carrier is slowed.</summary>
        public const float CarrierDriveMult = 0.92f;

        // ---- soccer -----------------------------------------------------------

        public const float BallRadius = 0.13f;
        public const float BallMass = 0.35f;
        public const float BallDrag = 0.25f;
        public const float BallAngularDrag = 0.35f;

        /// <summary>Extra kick a car imparts beyond the raw collision, so a
        /// touch reads as a strike rather than a nudge.</summary>
        public const float BallHitBoost = 1.35f;

        /// <summary>Seconds of celebration between a goal and the kick-off.</summary>
        public const float GoalCelebrationSec = 3.0f;

        // ---- aerial -----------------------------------------------------------

        /// <summary>Upward impulse of the first jump, in N·s.</summary>
        public const float JumpImpulse = 8.6f;

        /// <summary>The second jump is weaker — it is a correction, not a lift.</summary>
        public const float DoubleJumpImpulse = 6.0f;

        /// <summary>Window after the first jump in which a second press flips
        /// instead of jumping, if a direction is held.</summary>
        public const float FlipWindowSec = 1.25f;

        /// <summary>Flip impulse along the held direction, and the torque that
        /// makes it read as a barrel roll rather than a shove.</summary>
        public const float FlipImpulse = 3.2f;
        public const float FlipTorque = 1.055f;

        /// <summary>Air-roll authority, in N·m per unit of stick.</summary>
        public const float AirTorque = 0.05f;

        /// <summary>Boost meter: full tank in seconds, and how fast a pad fills it.</summary>
        public const float BoostTankSec = 2.4f;
        public const float BoostRefillPerSec = 0.55f;
        public const float BoostAccel = 11f;

        // ---- shared ------------------------------------------------------------

        /// <summary>Seconds a dead car spectates before the match notices, so a
        /// kill has a beat to land.</summary>
        public const float DeathBeatSec = 1.6f;
    }
}
