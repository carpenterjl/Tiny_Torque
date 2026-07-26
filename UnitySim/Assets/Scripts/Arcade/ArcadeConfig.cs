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
        /// <summary>Speed (m/s) the item boost stops pushing towards.
        ///
        /// The boost force is a plain <c>AddForce</c> on the body with no speed
        /// ceiling, no grounded check and no traction limit, so 1.6 s of it kept
        /// accelerating the car well past the ~10 m/s the drivetrain can reach —
        /// which is what made boosting read as skittish rather than fast. The
        /// punch is unchanged; only the runaway is removed.
        ///
        /// Applied to <c>arcadeBoostAccel</c> in ArcadeDirector, deliberately not
        /// in CarVehicle: surface boost PADS are maxed in separately from
        /// <c>surf.boostAccel</c>, so they keep their authored 9 m/s² untouched.
        /// </summary>
        public const float BoostTopSpeed = 11f;
        /// <summary>Speed band over which the boost fades out below the cap, so
        /// it tapers instead of switching off.</summary>
        public const float BoostFadeBand = 1.5f;

        // ---- shield ----
        public const float ShieldSeconds = 8f;

        // ---- being hit: a banana spins you, a missile wrecks you ----
        public const float SpinGripMult = 0.35f;
        public const float SpinSeconds = 1.4f;
        /// <summary>Yaw torque while spun out (N·m).
        ///
        /// The first pass used 0.10 here on the reasoning that the car's yaw
        /// inertia is only ~0.03 kg·m². That was the wrong comparison: inertia
        /// sets how fast the torque WOULD spin a free body, but the tyres are
        /// what it actually fights, and each one generates roughly 0.5 N·m of
        /// resisting moment about the CoM even at SpinGripMult. 0.10 N·m lost to
        /// them outright and the hit was invisible. This has to beat the tyres,
        /// not the inertia.</summary>
        public const float SpinTorque = 1.2f;
        /// <summary>Drive is cut while spinning, so a hit costs momentum and you
        /// cannot simply power out of it.</summary>
        public const float SpinDriveMult = 0f;
        public const float HitImpulseFwd = 0.6f;     // kg·m/s along the hit's heading
        public const float HitImpulseUp = 0.9f;

        // ---- missile wreck + recovery ----
        /// <summary>Limp time after a missile hit, before the car is lifted back
        /// onto the racing line.</summary>
        public const float WreckSeconds = 1.5f;
        public const float WreckImpulseUp = 3.2f;    // enough to visibly leave the ground
        public const float WreckImpulseFwd = 1.4f;
        public const float WreckTorque = 3.0f;       // tumble, applied as one impulse
        /// <summary>Metres further along the spine to place the recovered car, so
        /// it never lands exactly on top of whatever it was hit next to.</summary>
        public const float WreckRecoverAhead = 1.0f;
        /// <summary>Immunity after recovering. Without it a second missile already
        /// in flight re-kills you the instant you reappear.</summary>
        public const float InvulnSeconds = 1.0f;
        public const float ExplosionSeconds = 0.8f;

        // ---- on-screen hit feedback ----
        /// <summary>Banner dwell after a spin-out or a blocked hit. Slightly
        /// longer than SpinSeconds so the text is still up as control returns and
        /// the player can connect the two.</summary>
        public const float HitBannerSeconds = 1.6f;
        /// <summary>Banner dwell after a wreck — long enough to survive the limp
        /// AND the recovery teleport, which is the confusing part without it.</summary>
        public const float WreckBannerSeconds = 2.6f;
        /// <summary>Colour wash on the moment of impact. Short: it is a punch, not
        /// a tint, and it must never obscure the corner you are about to take.</summary>
        public const float HitFlashSeconds = 0.35f;
        public static readonly Color SpinFeedbackColor = new Color(1f, 0.72f, 0.28f);
        public static readonly Color WreckFeedbackColor = new Color(1f, 0.34f, 0.22f);
        public static readonly Color ShieldFeedbackColor = new Color(0.40f, 0.85f, 1f);

        /// <summary>Log every banana/missile contact. A diagnostic, not a feature:
        /// the first build's banana was reported as doing nothing, and this
        /// separates "the trigger never fired" from "the effect was too weak to
        /// feel" without guessing.</summary>
        public static bool LogHits = false;

        // ---- missile ----
        public const float MissileSpeed = 11f;       // ≈1.4× a Hard bot: catches, but dodgeable
        /// <summary>Homing rate at range (rad/s).
        ///
        /// Was 3.2, which at 11 m/s is a 3.4 m turning radius — about as tight as
        /// the car itself, so the missile simply followed you in and "dodgeable"
        /// was a claim the geometry did not support. 2.2 rad/s is a 5.0 m radius:
        /// still corners hard enough to chase down a straight, no longer glued to
        /// the target's own line.</summary>
        public const float MissileTurnRate = 2.2f;
        /// <summary>Inside this range the missile has committed and steers only
        /// weakly, so a late swerve genuinely makes it miss. Without a commit
        /// window any turn rate high enough to be threatening is also high enough
        /// to track a last-moment dodge.</summary>
        public const float MissileCommitRange = 1.5f;
        public const float MissileCommitTurnRate = 0.6f;
        public const float MissileLifetime = 6f;
        public const float MissileArmSeconds = 0.15f;
        public const float MissileMuzzleOffset = 0.45f;  // clears the 0.42 m chassis
        public const float MissileRadius = 0.06f;
        public const float MissileHoverHeight = 0.06f;
        public const int MissileGroundProbeEvery = 5;    // FixedUpdates between ground raycasts
        public const float MaxLockDistance = 25f;

        // ---- banana ----
        /// <summary>Trigger radius. Sized against the car, not the peel: the root
        /// BoxCollider spans roughly ground+0.03 to ground+0.13, so the old 0.07
        /// left only ~6 cm of vertical overlap to catch a car crossing it at
        /// 10 m/s. The visual mesh is unchanged — gameplay volumes are authored
        /// in code precisely so they don't depend on the art.</summary>
        public const float BananaRadius = 0.13f;
        public const float BananaHeight = 0.05f;     // centre above the surface
        public const float BananaLifetime = 25f;
        public const float BananaOwnerGrace = 0.4f;  // then it can hit its owner too
        public const int MaxBananasPerPlayer = 2;
        /// <summary>Drop distance behind the car centre. The chassis half-length
        /// is 0.21 m, so 0.30 cleared the rear bumper by 2 cm and a peel dropped
        /// mid-corner could spawn already touching its own dropper.</summary>
        public const float BananaDropOffset = 0.55f;

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

        // ---- arcade handling (SessionConfig.ArcadeHandling) ----
        /// <summary>
        /// The assist floor every arcade car is raised to, bots included.
        ///
        /// Applied as a per-channel MAX, so a player who set higher values in
        /// Options keeps them. Bots previously got a zeroed AssistSettings with
        /// the comment "bots race on raw physics" — correct for a sim race, but
        /// it is why the AI was visibly spinning off on the banked circuits.
        /// </summary>
        public static readonly Vehicles.AssistSettings HandlingAssists =
            new Vehicles.AssistSettings
            {
                steer = 0.80f, stability = 0.70f, traction = 0.90f, abs = 0.90f,
            };

        /// <summary>Tyre grip baseline in arcade. Rides the existing
        /// <c>CarVehicle.arcadeGripMult</c> channel, which is already folded into
        /// µ on both the brush and legacy friction paths — so this costs no new
        /// physics code and no new friction-write site.</summary>
        public const float HandlingGripBonus = 1.25f;

        /// <summary>
        /// Drive-command scale in arcade — the "slow the cars down" knob.
        ///
        /// Rides <c>CarVehicle.arcadeDriveMult</c>, the single choke point every
        /// motor command already passes through (manual, bot, autonomous and LAN
        /// host alike). Top speed is set purely by motor back-EMF — steady state
        /// is <c>V = Kt·ω_motor</c> — so scaling the command scales top speed
        /// essentially linearly: 0.85 turns ~10 m/s into ~8.5 m/s. Launch torque
        /// scales with it, which is the accepted trade for costing no new
        /// physics code.
        ///
        /// Reaches the car through <see cref="ArcadeRacer.driveBase"/> rather
        /// than a direct write, because ApplyEffects re-asserts arcadeDriveMult
        /// every frame and would otherwise stomp it.
        /// </summary>
        public const float HandlingDriveScale = 0.85f;

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
