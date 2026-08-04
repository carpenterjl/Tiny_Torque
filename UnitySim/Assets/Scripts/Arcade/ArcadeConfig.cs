using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Every arcade tuning number in one place. These are gameplay constants, not
    /// physics — they are meant to be edited by feel. Magnitudes assume the RC
    /// scale the rest of the sim runs at: a ~1.8 kg car, 0.42 m long, topping out
    /// near 10 m/s, on 1 m tiles.
    ///
    /// The ones a person would actually want to drag while driving — handling,
    /// the whole drift model, boost, what a hit does, the missile's chase, the
    /// tow — read through <see cref="Override"/> and can be retuned mid-session
    /// from an <see cref="ArcadeConfigOverride"/> asset. The rest stay plain
    /// consts: item weights, tier colours and bot decision rates are rules and
    /// presentation, and a knob nobody would drag is better read here beside the
    /// reasoning for it.
    /// </summary>
    public static class ArcadeConfig
    {
        /// <summary>
        /// The asset a scene has chosen to tune the arcade layer with, or null —
        /// and null is what every tunable below falls back to its shipped literal
        /// for. Installed by <c>DrivingSceneDescriptor</c>; static, so it outlives
        /// a scene load within a session exactly as <c>AssistTuning.Override</c>
        /// does.
        /// </summary>
        public static ArcadeConfigOverride Override;

        // ---- item boxes ----
        public const float BoxRespawnSeconds = 4f;
        public const float RouletteSeconds = 0.9f;   // spin time before the item lands
        public const float RouletteFaceHz = 12f;     // how fast the displayed face cycles
        /// <summary>Number of real items, i.e. <see cref="ItemKind"/> values above
        /// None. The roulette animation cycles faces 1..this while spinning; it was
        /// a hardcoded literal, which meant a new item silently never appeared on
        /// the spinner. Bump it whenever ItemKind grows.</summary>
        public const int RouletteFaceCount = 7;
        /// <summary>Auto-placement spacing along the racing line when a map has no
        /// authored boxes (metres of arc length between rows of three).</summary>
        public const float AutoBoxSpacingMetres = 22f;
        public const float AutoBoxLateral = 0.5f;    // ± offset of the outer two
        public const float AutoBoxHeight = 0.10f;    // hover above the surface

        // ---- boost ----
        private const float BoostAccelDefault = 14f;     // m/s² (a boost pad is 9)
        private const float BoostSecondsDefault = 1.6f;

        public static float BoostAccel =>
            Override != null ? Override.boostAccel : BoostAccelDefault;
        public static float BoostSeconds =>
            Override != null ? Override.boostSeconds : BoostSecondsDefault;

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
        private const float BoostTopSpeedDefault = 11f;
        public static float BoostTopSpeed =>
            Override != null ? Override.boostTopSpeed : BoostTopSpeedDefault;

        /// <summary>Speed band over which the boost fades out below the cap, so
        /// it tapers instead of switching off.</summary>
        private const float BoostFadeBandDefault = 1.5f;
        public static float BoostFadeBand =>
            Override != null ? Override.boostFadeBand : BoostFadeBandDefault;

        // ---- shield ----
        private const float ShieldSecondsDefault = 8f;
        public static float ShieldSeconds =>
            Override != null ? Override.shieldSeconds : ShieldSecondsDefault;

        // ---- being hit: a banana spins you, a missile wrecks you ----
        private const float SpinGripMultDefault = 0.35f;
        private const float SpinSecondsDefault = 1.4f;
        public static float SpinGripMult =>
            Override != null ? Override.spinGripMult : SpinGripMultDefault;
        public static float SpinSeconds =>
            Override != null ? Override.spinSeconds : SpinSecondsDefault;
        /// <summary>Yaw torque while spun out (N·m).
        ///
        /// The first pass used 0.10 here on the reasoning that the car's yaw
        /// inertia is only ~0.03 kg·m². That was the wrong comparison: inertia
        /// sets how fast the torque WOULD spin a free body, but the tyres are
        /// what it actually fights, and each one generates roughly 0.5 N·m of
        /// resisting moment about the CoM even at SpinGripMult. 0.10 N·m lost to
        /// them outright and the hit was invisible. This has to beat the tyres,
        /// not the inertia.</summary>
        private const float SpinTorqueDefault = 1.2f;
        public static float SpinTorque =>
            Override != null ? Override.spinTorque : SpinTorqueDefault;

        /// <summary>Drive is cut while spinning, so a hit costs momentum and you
        /// cannot simply power out of it.</summary>
        private const float SpinDriveMultDefault = 0f;
        public static float SpinDriveMult =>
            Override != null ? Override.spinDriveMult : SpinDriveMultDefault;

        private const float HitImpulseFwdDefault = 0.6f;  // kg·m/s along the hit's heading
        private const float HitImpulseUpDefault = 0.9f;
        public static float HitImpulseFwd =>
            Override != null ? Override.hitImpulseFwd : HitImpulseFwdDefault;
        public static float HitImpulseUp =>
            Override != null ? Override.hitImpulseUp : HitImpulseUpDefault;

        // ---- missile wreck + recovery ----
        /// <summary>Limp time after a missile hit, before the car is lifted back
        /// onto the racing line.</summary>
        private const float WreckSecondsDefault = 1.5f;
        private const float WreckImpulseUpDefault = 3.2f;  // visibly leaves the ground
        private const float WreckImpulseFwdDefault = 1.4f;
        private const float WreckTorqueDefault = 3.0f;     // tumble, as one impulse
        public static float WreckSeconds =>
            Override != null ? Override.wreckSeconds : WreckSecondsDefault;
        public static float WreckImpulseUp =>
            Override != null ? Override.wreckImpulseUp : WreckImpulseUpDefault;
        public static float WreckImpulseFwd =>
            Override != null ? Override.wreckImpulseFwd : WreckImpulseFwdDefault;
        public static float WreckTorque =>
            Override != null ? Override.wreckTorque : WreckTorqueDefault;
        /// <summary>Metres further along the spine to place the recovered car, so
        /// it never lands exactly on top of whatever it was hit next to.</summary>
        public const float WreckRecoverAhead = 1.0f;
        /// <summary>Immunity after recovering. Without it a second missile already
        /// in flight re-kills you the instant you reappear.</summary>
        private const float InvulnSecondsDefault = 1.0f;
        public static float InvulnSeconds =>
            Override != null ? Override.invulnSeconds : InvulnSecondsDefault;
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
        private const float MissileSpeedDefault = 11f;  // ≈1.4× a Hard bot: catches, but dodgeable
        public static float MissileSpeed =>
            Override != null ? Override.missileSpeed : MissileSpeedDefault;
        /// <summary>Homing rate at range (rad/s).
        ///
        /// Was 3.2, which at 11 m/s is a 3.4 m turning radius — about as tight as
        /// the car itself, so the missile simply followed you in and "dodgeable"
        /// was a claim the geometry did not support. 2.2 rad/s is a 5.0 m radius:
        /// still corners hard enough to chase down a straight, no longer glued to
        /// the target's own line.</summary>
        private const float MissileTurnRateDefault = 2.2f;
        public static float MissileTurnRate =>
            Override != null ? Override.missileTurnRate : MissileTurnRateDefault;
        /// <summary>Inside this range the missile has committed and steers only
        /// weakly, so a late swerve genuinely makes it miss. Without a commit
        /// window any turn rate high enough to be threatening is also high enough
        /// to track a last-moment dodge.</summary>
        private const float MissileCommitRangeDefault = 1.5f;
        private const float MissileCommitTurnRateDefault = 0.6f;
        public static float MissileCommitRange =>
            Override != null ? Override.missileCommitRange : MissileCommitRangeDefault;
        public static float MissileCommitTurnRate =>
            Override != null ? Override.missileCommitTurnRate : MissileCommitTurnRateDefault;
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
        private const float BananaRadiusDefault = 0.13f;
        public static float BananaRadius =>
            Override != null ? Override.bananaRadius : BananaRadiusDefault;
        public const float BananaHeight = 0.05f;     // centre above the surface
        public const float BananaLifetime = 25f;
        public const float BananaOwnerGrace = 0.4f;  // then it can hit its owner too
        public const int MaxBananasPerPlayer = 2;
        /// <summary>Drop distance behind the car centre. The chassis half-length
        /// is 0.21 m, so 0.30 cleared the rear bumper by 2 cm and a peel dropped
        /// mid-corner could spawn already touching its own dropper.</summary>
        public const float BananaDropOffset = 0.55f;

        // ---- area hazards (smoke cloud, oil slick) ----
        /// <summary>Full gameplay radius once grown. Deliberately a poll radius and
        /// not a collider: the car root BoxCollider and all four WheelColliders hang
        /// off one transform, so a trigger fires several times per car per pass (the
        /// problem ArcadeItemBox documents), and OnTriggerStay — which re-arming the
        /// effect would need — stops firing once a parked car's body sleeps. A
        /// distance test has neither failure, and a hazard with no collider can never
        /// accidentally become a wall.</summary>
        private const float SmokeRadiusDefault = 0.75f;
        public static float SmokeRadius =>
            Override != null ? Override.smokeRadius : SmokeRadiusDefault;
        public const float SmokeStartRadius = 0.18f;
        /// <summary>Grow-in time. The gameplay radius follows the visual so an
        /// unexpanded puff cannot blind someone two car-lengths away.</summary>
        public const float SmokeGrowSeconds = 0.55f;
        public const float SmokeLifetime = 9f;
        public const float SmokeFadeSeconds = 1.5f;
        public const float SmokeDriftSpeed = 0.12f;  // m/s, flat, rolled once at spawn
        /// <summary>Oil spreads flatter and wider than smoke billows.</summary>
        private const float SlickRadiusDefault = 0.85f;
        public static float SlickRadius =>
            Override != null ? Override.slickRadius : SlickRadiusDefault;
        public const float SlickLifetime = 12f;
        private const float SlickGripMultDefault = 0.45f;
        public static float SlickGripMult =>
            Override != null ? Override.slickGripMult : SlickGripMultDefault;
        /// <summary>Grace before a hazard can catch its own dropper. Longer than the
        /// banana's 0.4 s because an area you are still inside of would otherwise
        /// catch you the instant you laid it.</summary>
        public const float HazardOwnerGrace = 1.0f;
        /// <summary>Drop distance behind the car centre: 0.21 m chassis half-length
        /// plus most of a radius, so it lands clear rather than on the bumper.</summary>
        public const float HazardDropOffset = 0.90f;
        public const float HazardHeight = 0.18f;     // centre above the surface
        public const int MaxHazardsPerPlayer = 1;
        /// <summary>Half-height of the containment test. Without it the poll is
        /// purely horizontal, and a cloud dropped on Neon Vortex II's bridge would
        /// blind cars passing metres underneath it.</summary>
        public const float HazardVerticalBand = 0.6f;
        /// <summary>How long grip stays down after LEAVING a slick. Far shorter
        /// than <see cref="BlindSeconds"/> on purpose: oil comes off the tyres in
        /// a moment, whereas not being able to see stays with you.</summary>
        public const float SlickLingerSeconds = 0.4f;

        // ---- blinded (smoke cloud) ----
        private const float BlindSecondsDefault = 2.6f;
        public static float BlindSeconds =>
            Override != null ? Override.blindSeconds : BlindSecondsDefault;
        public const float BlindRampSeconds = 0.15f;
        public const float BlindFadeSeconds = 0.9f;
        /// <summary>Peak tint alpha. Much heavier than the hit flash's 0.30: that is
        /// a punch you already took and only has to register, this is a state you are
        /// in and has to actually cost you the corner.</summary>
        public const float BlindTintAlpha = 0.62f;
        public static readonly Color BlindFeedbackColor = new Color(0.42f, 0.85f, 0.32f);
        public static readonly Color SlickFeedbackColor = new Color(0.55f, 0.45f, 0.75f);
        /// <summary>How long a blinded bot needs to ease the wheel back to centre.
        /// Not an instant zero — that snaps straight and reads as a magic correction.</summary>
        public const float BlindBotSteerRelease = 0.5f;
        public const float BlindBotThrottle = 0.35f;

        // ---- drift boost (mini-turbo) ----
        // The drift is LATCHED, not detected. Pass 3 shipped a detector — it
        // watched for handbrake + speed + slip angle and paid out if it saw all
        // three — and the trouble with a detector is that it makes the mechanic
        // something the physics might grant you rather than something you do.
        // Here, pulling the handbrake while turned commits the car to a slide in
        // that direction and holds it there until you let go.

        /// <summary>Below this the handbrake is a handbrake, not a drift.</summary>
        private const float DriftMinSpeedDefault = 3.5f;
        public static float DriftMinSpeed =>
            Override != null ? Override.driftMinSpeed : DriftMinSpeedDefault;
        /// <summary>Steering deflection needed to commit. Above a keyboard's own
        /// smoothed ramp (SteerSmoother reaches ~0.35 in the first 100 ms) so a
        /// twitch of the wheel while braking in a straight line cannot latch a
        /// drift, and low enough that a deliberate turn always does.</summary>
        private const float DriftEntrySteerDefault = 0.30f;
        public static float DriftEntrySteer =>
            Override != null ? Override.driftEntrySteer : DriftEntrySteerDefault;
        /// <summary>Speed at which a latched drift gives up. Hysteresis against
        /// <see cref="DriftMinSpeed"/> — a drift that scrubs momentarily below
        /// the entry speed should not drop you out mid-corner.</summary>
        private const float DriftHoldSpeedDefault = 2.2f;
        public static float DriftHoldSpeed =>
            Override != null ? Override.driftHoldSpeed : DriftHoldSpeedDefault;

        /// <summary>The "jump": a small vertical impulse (N·s) on the moment of
        /// commitment. It is mostly theatre — you see the car set itself — but it
        /// also briefly unloads the tyres, which is exactly what makes the slide
        /// start crisply instead of washing in.</summary>
        private const float DriftHopImpulseDefault = 1.1f;
        public static float DriftHopImpulse =>
            Override != null ? Override.driftHopImpulse : DriftHopImpulseDefault;
        /// <summary>How long the yaw controller may use <see cref="DriftYawKick"/>
        /// instead of <see cref="DriftYawHold"/>. This is what snaps the car into
        /// the slide; after it, the same controller only maintains the angle.</summary>
        private const float DriftKickSecondsDefault = 0.28f;
        public static float DriftKickSeconds =>
            Override != null ? Override.driftKickSeconds : DriftKickSecondsDefault;

        /// <summary>Slip angle held at full counter-steer (steering out of the
        /// slide) and at full lock into it. The player picks a point on this band
        /// with the stick, which is what turns the drift into an arc you steer
        /// rather than a state you are in.</summary>
        private const float DriftAngleMinDegDefault = 11f;
        public static float DriftAngleMinDeg =>
            Override != null ? Override.driftAngleMinDeg : DriftAngleMinDegDefault;
        private const float DriftAngleMaxDegDefault = 34f;
        public static float DriftAngleMaxDeg =>
            Override != null ? Override.driftAngleMaxDeg : DriftAngleMaxDegDefault;

        /// <summary>Yaw torque per degree of angle error (N·m/deg).</summary>
        private const float DriftYawGainDefault = 0.055f;
        public static float DriftYawGain =>
            Override != null ? Override.driftYawGain : DriftYawGainDefault;
        /// <summary>Torque clamp during <see cref="DriftKickSeconds"/>. Under the
        /// spin-out's 1.2 N·m on purpose: getting hit must always out-rotate
        /// anything you can do to yourself.</summary>
        private const float DriftYawKickDefault = 0.95f;
        public static float DriftYawKick =>
            Override != null ? Override.driftYawKick : DriftYawKickDefault;
        /// <summary>Torque clamp once the slide is established.</summary>
        private const float DriftYawHoldDefault = 0.45f;
        public static float DriftYawHold =>
            Override != null ? Override.driftYawHold : DriftYawHoldDefault;
        /// <summary>Torque clamp while straightening out on release.</summary>
        private const float DriftYawStraightenDefault = 0.70f;
        public static float DriftYawStraighten =>
            Override != null ? Override.driftYawStraighten : DriftYawStraightenDefault;
        /// <summary>Longest the exit straighten may run. It also ends early, the
        /// moment the slip angle is small — this is the ceiling, not the duration.</summary>
        private const float DriftStraightenSecondsDefault = 0.5f;
        public static float DriftStraightenSeconds =>
            Override != null ? Override.driftStraightenSeconds : DriftStraightenSecondsDefault;
        /// <summary>Residual slip angle that counts as "pointing where you are
        /// going", ending the straighten.</summary>
        private const float DriftStraightenDoneDegDefault = 4f;
        public static float DriftStraightenDoneDeg =>
            Override != null ? Override.driftStraightenDoneDeg : DriftStraightenDoneDegDefault;

        /// <summary>Grip while sliding. Between neutral and the spin-out's 0.35:
        /// enough to keep the slide alive without the tyres giving up entirely,
        /// which would make the angle uncontrollable rather than steerable.</summary>
        private const float DriftGripMultDefault = 0.70f;
        public static float DriftGripMult =>
            Override != null ? Override.driftGripMult : DriftGripMultDefault;
        /// <summary>Assist scale while sliding — see CarVehicle.arcadeAssistMult.
        /// Not zero: a fifth of the countersteer assist keeps a full-lock entry
        /// from becoming a spin, which is a help rather than a correction.</summary>
        private const float DriftAssistMultDefault = 0.20f;
        public static float DriftAssistMult =>
            Override != null ? Override.driftAssistMult : DriftAssistMultDefault;
        /// <summary>Handbrake torque scale while sliding — see
        /// CarVehicle.arcadeHandbrakeMult. This is the single most important
        /// number here: a drift button that keeps the rear axle locked scrubs the
        /// speed out of the arc, and no amount of carry acceleration buys it back.
        /// A quarter leaves the back end willing without braking the corner
        /// away.</summary>
        private const float DriftHandbrakeMultDefault = 0.25f;
        public static float DriftHandbrakeMult =>
            Override != null ? Override.driftHandbrakeMult : DriftHandbrakeMultDefault;

        /// <summary>Forward acceleration (m/s²) fed into the boost channel while
        /// sliding, so the arc CARRIES momentum instead of scrubbing to a halt.
        /// Applied along the car's NOSE, which is why it also rotates the velocity
        /// vector toward where the car is pointing — the slide tightens onto its
        /// own heading rather than washing out sideways forever.</summary>
        private const float DriftCarryAccelDefault = 4.5f;
        public static float DriftCarryAccel =>
            Override != null ? Override.driftCarryAccel : DriftCarryAccelDefault;
        /// <summary>Ceiling for the carry, below <see cref="BoostTopSpeed"/>: a
        /// drift must never be a way to exceed the straight-line pace. It used to
        /// be 8.5, which several designs simply cruise past — a carry that is
        /// already faded to nothing at corner-entry speed is a carry that does not
        /// exist.</summary>
        private const float DriftCarryTopSpeedDefault = 10f;
        public static float DriftCarryTopSpeed =>
            Override != null ? Override.driftCarryTopSpeed : DriftCarryTopSpeedDefault;
        /// <summary>Speed band over which the carry fades out below the ceiling.</summary>
        private const float DriftCarryFadeBandDefault = 2.5f;
        public static float DriftCarryFadeBand =>
            Override != null ? Override.driftCarryFadeBand : DriftCarryFadeBandDefault;

        /// <summary>
        /// Charge multipliers at full lock INTO the slide and at full counter-steer.
        ///
        /// This is what gives the player something to do while the drift is held.
        /// Steering in both tightens the arc (it raises the target angle, above)
        /// and pays better; steering out widens it and nearly stops the clock. One
        /// stick axis, one decision — commit or bail — and the reward follows the
        /// commitment rather than the stopwatch.
        /// </summary>
        private const float DriftChargeIntoDefault = 1.5f;
        public static float DriftChargeInto =>
            Override != null ? Override.driftChargeInto : DriftChargeIntoDefault;
        private const float DriftChargeOutDefault = 0.35f;
        public static float DriftChargeOut =>
            Override != null ? Override.driftChargeOut : DriftChargeOutDefault;

        // Tier gates, in units of CHARGE, not seconds — at full commitment they
        // arrive in about 0.6 / 1.3 / 2.0 s, and a car being nursed sideways on
        // counter-steer may never reach tier 3 at all.
        private const float DriftTier1SecondsDefault = 0.9f;
        public static float DriftTier1Seconds =>
            Override != null ? Override.driftTier1Seconds : DriftTier1SecondsDefault;
        private const float DriftTier2SecondsDefault = 1.9f;
        public static float DriftTier2Seconds =>
            Override != null ? Override.driftTier2Seconds : DriftTier2SecondsDefault;
        private const float DriftTier3SecondsDefault = 3.0f;
        public static float DriftTier3Seconds =>
            Override != null ? Override.driftTier3Seconds : DriftTier3SecondsDefault;
        /// <summary>Charge at which the meter reads full (tier 3 plus a little, so
        /// the bar has somewhere to go once the last tier lands).</summary>
        private const float DriftChargeFullDefault = 3.5f;
        public static float DriftChargeFull =>
            Override != null ? Override.driftChargeFull : DriftChargeFullDefault;
        /// <summary>Boost duration granted per tier on release.</summary>
        private const float DriftBoostSecondsDefault = 0.8f;
        public static float DriftBoostSeconds =>
            Override != null ? Override.driftBoostSeconds : DriftBoostSecondsDefault;
        /// <summary>Forward impulse (N·s) per tier on release, through the centre
        /// of mass. The timed acceleration alone ramps in over a few frames, which
        /// reads as the car gradually recovering rather than as being fired out of
        /// the corner; this is the kick that makes the exit an event. At ~1.8 kg,
        /// tier 3 is a shade under 1 m/s of instant speed.</summary>
        private const float DriftExitImpulseDefault = 0.55f;
        public static float DriftExitImpulse =>
            Override != null ? Override.driftExitImpulse : DriftExitImpulseDefault;
        public static readonly Color[] DriftTierColors =
        {
            new Color(0.35f, 0.70f, 1.00f),   // tier 1 — blue
            new Color(1.00f, 0.60f, 0.15f),   // tier 2 — orange
            new Color(0.75f, 0.40f, 1.00f),   // tier 3 — purple
        };
        /// <summary>Meter colour before the first tier lands.</summary>
        public static readonly Color DriftChargeColor = new Color(0.72f, 0.76f, 0.82f);

        // ---- slipstream ----
        private const float DraftRangeDefault = 3.0f; // metres behind the car ahead
        public static float DraftRange =>
            Override != null ? Override.draftRange : DraftRangeDefault;
        private const float DraftConeDegDefault = 25f; // heading alignment required
        public static float DraftConeDeg =>
            Override != null ? Override.draftConeDeg : DraftConeDegDefault;
        private const float DraftAccelDefault = 4f; // m/s², well under a boost's 14
        public static float DraftAccel =>
            Override != null ? Override.draftAccel : DraftAccelDefault;
        private const float DraftTopSpeedDefault = 11f;
        public static float DraftTopSpeed =>
            Override != null ? Override.draftTopSpeed : DraftTopSpeedDefault;

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
        ///
        /// Now pinned to FULL on every channel. Play-testing showed arcade cars
        /// spinning out at any assist setting — the grip war is not one the sim
        /// assists can win at sim strength (see the stability boost below) — so
        /// the arcade answer is: everyone drives the best-assisted car the game
        /// has, and the Options preset is a SIM-mode preference. Steer at 1 is
        /// also most of the "less twitchy" ask: the lock limiter's reference
        /// speed drops from 4 to 2.5 m/s, roughly halving the available lock at
        /// racing speed. Lap time in arcade is meant to come from the line and
        /// the items, never from catching slides.
        /// </summary>
        private static readonly Vehicles.AssistSettings HandlingAssistsDefault =
            new Vehicles.AssistSettings
            {
                steer = 1f, stability = 1f, traction = 1f, abs = 1f, launch = 1f,
            };

        public static Vehicles.AssistSettings HandlingAssists =>
            Override != null ? Override.handlingAssists : HandlingAssistsDefault;

        /// <summary>Tyre grip baseline in arcade. Rides the existing
        /// <c>CarVehicle.arcadeGripMult</c> channel, which is already folded into
        /// µ on both the brush and legacy friction paths — so this costs no new
        /// physics code and no new friction-write site. Raised 1.25 → 1.45 in
        /// the anti-spin pass: the extra lateral headroom is what lets a car
        /// take a boost pad mid-corner, and the extra longitudinal grip is most
        /// of the full-throttle-launch fix. Raised again 1.45 → 1.60 on user
        /// feedback ("slips way too much" — free roam's grass verges sit at
        /// 0.85 µ, and 0.85 × 1.60 ≈ 1.36 keeps even the lawn planted).</summary>
        private const float HandlingGripBonusDefault = 1.60f;
        public static float HandlingGripBonus =>
            Override != null ? Override.handlingGripBonus : HandlingGripBonusDefault;

        /// <summary>
        /// Multiplier on the stability assist's gain and torque clamp in arcade
        /// — see <c>CarVehicle.arcadeStabilityMult</c> for why the sim-sized ESC
        /// cannot hold an arcade car on its own. At 3, the clamp reaches
        /// 2.25 N·m, finally comparable to the ~2 N·m the tyres themselves can
        /// put about the yaw axis.
        ///
        /// Stood down to 1 during a drift (the slide IS yaw), a spin-out and a
        /// wreck (both must out-rotate anything helping you), so none of those
        /// mechanics is retuned by this.
        /// </summary>
        private const float HandlingStabilityBoostDefault = 3f;
        public static float HandlingStabilityBoost =>
            Override != null ? Override.handlingStabilityBoost : HandlingStabilityBoostDefault;

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
        private const float HandlingDriveScaleDefault = 0.85f;
        public static float HandlingDriveScale =>
            Override != null ? Override.handlingDriveScale : HandlingDriveScaleDefault;

        /// <summary>
        /// Arcade downforce, N per (m/s)² of forward speed — the "car should
        /// feel heavier" knob. Rides <c>CarVehicle.arcadeDownforce</c>, owned
        /// by HandlingFloor (not ApplyEffects). At 0.10 an 8 m/s car carries
        /// an extra 6.4 N — ≈36 % of a 1.8 kg car's 17.7 N weight — so tyre
        /// load (and with it grip) grows with speed the way a planted car's
        /// does, while parking-speed handling is untouched. Honest aero at
        /// this scale is ~0.6 N (AeroDynamics' own doc), which is why this is
        /// an arcade channel and not a wing coefficient.
        /// </summary>
        private const float HandlingDownforceDefault = 0.10f;
        public static float HandlingDownforce =>
            Override != null ? Override.handlingDownforce : HandlingDownforceDefault;

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
        //             None  Boost  Missile Banana Shield Triple Smoke  Oil
        // Roll() loops w.Length, so extending these is all it takes to put a new
        // ItemKind into circulation — and forgetting to extend them is a SILENT
        // failure, not an error: the item simply never appears.
        private static readonly float[] Lead = { 0f, 3.0f, 0.5f, 4.0f, 2.0f, 0.5f, 3.0f, 2.5f };
        private static readonly float[] Mid = { 0f, 3.0f, 2.5f, 2.5f, 1.5f, 1.0f, 2.0f, 1.5f };
        private static readonly float[] Back = { 0f, 2.0f, 4.0f, 1.0f, 1.0f, 3.0f, 1.0f, 1.0f };

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
            ItemKind.SmokeCloud => "SMOKE",
            ItemKind.OilSlick => "OIL",
            _ => "",
        };
    }
}
