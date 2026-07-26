using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Every arcade tuning number in one place. These are gameplay constants, not
    /// physics — they are meant to be edited by feel. Magnitudes assume the RC
    /// scale the rest of the sim runs at: a ~1.8 kg car, 0.42 m long, topping out
    /// near 10 m/s, on 1 m tiles.
    /// </summary>
    public static class ArcadeConfig
    {
        // ---- item boxes ----
        public const float BoxRespawnSeconds = 4f;
        public const float RouletteSeconds = 0.9f;   // spin time before the item lands
        public const float RouletteFaceHz = 12f;     // how fast the displayed face cycles
        /// <summary>Auto-placement spacing along the racing line when a map has no
        /// authored boxes (metres of arc length between rows of three).</summary>
        public const float AutoBoxSpacingMetres = 22f;
        public const float AutoBoxLateral = 0.5f;    // ± offset of the outer two
        public const float AutoBoxHeight = 0.10f;    // hover above the surface

        // ---- boost ----
        public const float BoostAccel = 14f;         // m/s² (a boost pad is 9)
        public const float BoostSeconds = 1.6f;
        public const int TripleBoostCharges = 3;

        // ---- shield ----
        public const float ShieldSeconds = 8f;

        // ---- being hit ----
        public const float SpinGripMult = 0.35f;
        public const float SpinSeconds = 1.2f;
        /// <summary>Yaw torque while spun out (N·m). The car's yaw inertia is only
        /// ~0.03 kg·m², so this is small on purpose — the grip drop does most of
        /// the work and the tyres still fight back. Tuned by feel.</summary>
        public const float SpinTorque = 0.10f;
        public const float HitImpulseFwd = 0.6f;     // kg·m/s along the missile's heading
        public const float HitImpulseUp = 0.9f;

        // ---- missile ----
        public const float MissileSpeed = 11f;       // ≈1.4× a Hard bot: catches, but dodgeable
        public const float MissileTurnRate = 3.2f;   // rad/s
        public const float MissileLifetime = 6f;
        public const float MissileArmSeconds = 0.15f;
        public const float MissileMuzzleOffset = 0.45f;  // clears the 0.42 m chassis
        public const float MissileRadius = 0.06f;
        public const float MissileHoverHeight = 0.06f;
        public const int MissileGroundProbeEvery = 5;    // FixedUpdates between ground raycasts
        public const float MaxLockDistance = 25f;

        // ---- banana ----
        public const float BananaRadius = 0.07f;
        public const float BananaLifetime = 25f;
        public const float BananaOwnerGrace = 0.4f;  // then it can hit its owner too
        public const int MaxBananasPerPlayer = 2;
        public const float BananaDropOffset = 0.30f; // behind the car

        // ---- track limits ----
        /// <summary>A surface at or below this friction multiplier is off-track.
        /// Against TrackCatalog.Floors that means grass/sand/ice/mud (and the
        /// themed carpet/wet-sand/lava) are off, asphalt/dirt/rumble/boost are on
        /// — classification for free, with no per-tile authoring.</summary>
        public const float OffTrackFrictionThreshold = 0.90f;
        public const float TrackLimitSampleHz = 10f;
        public const float OffTrackWarnSeconds = 1.0f;
        public const float OffTrackPenaltySeconds = 2.5f;
        public const float OffTrackDecayRate = 2f;    // × faster recovery than accumulation
        public const float JumpGraceSeconds = 1.0f;   // airborne this long before it counts
        public const float PenaltyDuration = 2.0f;
        public const float PenaltyCapSpeed = 3.5f;    // m/s
        /// <summary>Rearward drag per m/s of overspeed. A soft bleed (roughly
        /// 8 → 3.5 m/s in half a second on a 1.8 kg car), deliberately not a hard
        /// velocity clamp — clamping the body would fight the brush tyre model's
        /// own impulse limits.</summary>
        public const float PenaltyDragGain = 3f;
        public const float PenaltyCooldownSeconds = 3f;

        // ---- positions / scoring ----
        public const float PositionUpdateHz = 5f;
        public static readonly int[] PlacePoints = { 15, 12, 10, 8, 6, 4, 2, 1 };

        // ---- bots ----
        /// <summary>Master switch for bot item use — flip to false if it plays badly.</summary>
        public static bool BotsUseItems = true;
        public const float BotDecisionHz = 2f;
        public const float BotReactionMin = 0.3f;
        public const float BotReactionMax = 1.2f;
        public const float BotNoUseAfterGo = 1.5f;    // no items for this long after GO
        public const float BotStraightSteerDeg = 6f;  // "straight enough" to boost
        public const float BotBoostMinSpeed = 3f;
        public const float BotMissileRange = 15f;
        public const float BotBananaRange = 8f;       // someone this close behind
        public const float BotHoldTimeout = 8f;       // dump an unused item eventually

        // ---- roulette weights, indexed by (int)ItemKind ----
        // Front-runners get defensive/utility items, back-markers get the weapons.
        // This is the arcade catch-up mechanism and is independent of
        // SessionConfig.RubberBand, which only scales bot speed.
        private static readonly float[] Lead = { 0f, 3.0f, 0.5f, 4.0f, 2.0f, 0.5f };
        private static readonly float[] Mid = { 0f, 3.0f, 2.5f, 2.5f, 1.5f, 1.0f };
        private static readonly float[] Back = { 0f, 2.0f, 4.0f, 1.0f, 1.0f, 3.0f };

        /// <summary>Roll an item for a racer in <paramref name="position"/> (1 = leader)
        /// out of <paramref name="fieldSize"/>.</summary>
        public static ItemKind Roll(int position, int fieldSize, System.Random rng)
        {
            float frac = fieldSize > 1
                ? Mathf.Clamp01((position - 1) / (float)(fieldSize - 1))
                : 0f;
            var w = frac < 0.34f ? Lead : (frac < 0.67f ? Mid : Back);

            float total = 0f;
            for (int i = 1; i < w.Length; i++) total += w[i];
            if (total <= 0f) return ItemKind.Boost;

            double pick = rng.NextDouble() * total;
            for (int i = 1; i < w.Length; i++)
            {
                pick -= w[i];
                if (pick <= 0d) return (ItemKind)i;
            }
            return ItemKind.Boost;
        }

        /// <summary>Charges granted on pickup (TripleBoost is the only multi-use item).</summary>
        public static int ChargesFor(ItemKind kind) =>
            kind == ItemKind.TripleBoost ? TripleBoostCharges : 1;

        public static string DisplayName(ItemKind kind) => kind switch
        {
            ItemKind.Boost => "BOOST",
            ItemKind.Missile => "MISSILE",
            ItemKind.Banana => "BANANA",
            ItemKind.Shield => "SHIELD",
            ItemKind.TripleBoost => "BOOST ×3",
            _ => "",
        };
    }
}
