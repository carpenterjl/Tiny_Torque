using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// The arcade layer's feel, as an asset you can edit while the race runs.
    ///
    /// Same contract as <c>AssistTuningOverride</c> and
    /// <see cref="Modes.ModeConfigOverride"/>: <see cref="ArcadeConfig"/> keeps
    /// every value below as its shipped default and reads this object only when
    /// one is installed, so <b>no asset assigned means the literals,
    /// verbatim</b>. <c>[DSC]</c> compares the two copies so neither can drift.
    ///
    /// <b>Everything here is live where it is read, which for the arcade layer
    /// is essentially everywhere.</b> The drift controller reads its torques
    /// every physics step, <c>HandlingFloor</c> re-asserts the four handling
    /// channels every frame, and a hit reads its numbers when it lands — so a
    /// slider drag is felt on the next corner without anything having to be
    /// told. The only lag is on objects that bake a number at spawn (a banana's
    /// trigger radius, a hazard's size): those apply to the NEXT one dropped,
    /// which is a second away and is noted on the field.
    ///
    /// <b>Not here, deliberately.</b> Item roulette weights, the tier colours,
    /// bot decision rates and the track-limits penalty model. Those are rules
    /// and presentation rather than feel, and a knob you would never drag while
    /// driving is better read in code beside the reasoning for it.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Arcade Tuning", fileName = "ArcadeTuning")]
    public sealed class ArcadeConfigOverride : ScriptableObject
    {
        // ---- arcade handling ---------------------------------------------------

        [Header("Arcade handling (SessionConfig.ArcadeHandling)")]
        [Tooltip("Tyre grip baseline in arcade — a multiplier on µ for every car, bots " +
                 "included. The single biggest 'does it slide' knob: free roam's grass " +
                 "sits at 0.85 µ, so 1.60 keeps even the lawn planted.")]
        [Range(0.5f, 3f)] public float handlingGripBonus = 1.60f;

        [Tooltip("Multiplier on the stability assist's gain and torque clamp in arcade. " +
                 "The sim-sized ESC cannot hold an arcade car on its own; at 3 the clamp " +
                 "reaches 2.25 N·m, comparable to what the tyres themselves put about " +
                 "the yaw axis. Stood down to 1 during a drift, a spin-out and a wreck, " +
                 "so none of those is retuned by this.")]
        [Range(1f, 6f)] public float handlingStabilityBoost = 3f;

        [Tooltip("Drive-command scale in arcade — the 'slow the cars down' knob. Top " +
                 "speed is set by motor back-EMF, so this scales it almost linearly: " +
                 "0.85 turns ~10 m/s into ~8.5.")]
        [Range(0.3f, 1.5f)] public float handlingDriveScale = 0.85f;

        [Tooltip("Arcade downforce, N per (m/s)² — the 'car should feel heavier' knob. " +
                 "At 0.10 an 8 m/s car carries an extra 6.4 N, about a third of its own " +
                 "weight, so grip grows with speed while parking-speed handling is " +
                 "untouched.")]
        [Range(0f, 0.5f)] public float handlingDownforce = 0.10f;

        [Tooltip("The assist floor every arcade car is raised to, bots included — " +
                 "applied as a per-channel MAX, so a player who set higher values in " +
                 "Options keeps them. Pinned to full on every channel because arcade " +
                 "lap time is meant to come from the line and the items, never from " +
                 "catching slides.")]
        public Vehicles.AssistSettings handlingAssists = new Vehicles.AssistSettings
        {
            steer = 1f, stability = 1f, traction = 1f, abs = 1f, launch = 1f,
        };

        // ---- drift -------------------------------------------------------------

        [Header("Drift — entry")]
        [Tooltip("Below this speed (m/s) the handbrake is a handbrake, not a drift.")]
        [Min(0f)] public float driftMinSpeed = 3.5f;

        [Tooltip("Steering deflection needed to commit. Above a keyboard's own smoothed " +
                 "ramp (~0.35 in the first 100 ms) so a twitch while braking in a " +
                 "straight line cannot latch a drift.")]
        [Range(0f, 1f)] public float driftEntrySteer = 0.30f;

        [Tooltip("Speed at which a latched drift gives up — hysteresis against Drift " +
                 "Min Speed, so a slide that scrubs momentarily does not drop you out " +
                 "mid-corner.")]
        [Min(0f)] public float driftHoldSpeed = 2.2f;

        [Tooltip("The 'hop': a small vertical impulse (N·s) on commitment. Mostly " +
                 "theatre — but it briefly unloads the tyres, which is what makes the " +
                 "slide start crisply instead of washing in.")]
        [Min(0f)] public float driftHopImpulse = 1.1f;

        [Header("Drift — the slide")]
        [Tooltip("How long the yaw controller may use the kick torque instead of the " +
                 "hold torque. This is what snaps the car into the slide.")]
        [Min(0f)] public float driftKickSeconds = 0.28f;

        [Tooltip("Slip angle held at full counter-steer, i.e. the shallowest arc.")]
        [Min(0f)] public float driftAngleMinDeg = 11f;

        [Tooltip("Slip angle held at full lock into the slide. The player picks a point " +
                 "on this band with the stick, which is what turns the drift into an arc " +
                 "you steer rather than a state you are in.")]
        [Min(0f)] public float driftAngleMaxDeg = 34f;

        [Tooltip("Yaw torque per degree of angle error (N·m/deg).")]
        [Min(0f)] public float driftYawGain = 0.055f;

        [Tooltip("Torque clamp during the kick. Kept under the spin-out's 1.2 N·m on " +
                 "purpose: getting hit must always out-rotate anything you can do to " +
                 "yourself.")]
        [Min(0f)] public float driftYawKick = 0.95f;

        [Tooltip("Torque clamp once the slide is established.")]
        [Min(0f)] public float driftYawHold = 0.45f;

        [Tooltip("Torque clamp while straightening out on release.")]
        [Min(0f)] public float driftYawStraighten = 0.70f;

        [Tooltip("Longest the exit straighten may run. It also ends early, the moment " +
                 "the slip angle is small — this is the ceiling, not the duration.")]
        [Min(0f)] public float driftStraightenSeconds = 0.5f;

        [Tooltip("Residual slip angle that counts as 'pointing where you are going', " +
                 "ending the straighten.")]
        [Min(0f)] public float driftStraightenDoneDeg = 4f;

        [Tooltip("Grip while sliding. Between neutral and the spin-out's 0.35: enough " +
                 "to keep the slide alive without the tyres giving up entirely, which " +
                 "would make the angle uncontrollable rather than steerable.")]
        [Range(0.05f, 1.5f)] public float driftGripMult = 0.70f;

        [Tooltip("Assist scale while sliding. Not zero: a fifth of the countersteer " +
                 "assist keeps a full-lock entry from becoming a spin.")]
        [Range(0f, 1f)] public float driftAssistMult = 0.20f;

        [Tooltip("Handbrake torque scale while sliding. The most important number here: " +
                 "a drift button that keeps the rear axle locked scrubs the speed out of " +
                 "the arc, and no amount of carry acceleration buys it back.")]
        [Range(0f, 1f)] public float driftHandbrakeMult = 0.25f;

        [Header("Drift — carry")]
        [Tooltip("Forward acceleration (m/s²) fed into the boost channel while sliding, " +
                 "so the arc CARRIES momentum instead of scrubbing to a halt. Applied " +
                 "along the nose, which also rotates the velocity toward where the car " +
                 "is pointing.")]
        [Min(0f)] public float driftCarryAccel = 4.5f;

        [Tooltip("Ceiling for the carry. A drift must never be a way to exceed the " +
                 "straight-line pace, so keep this at or below Boost Top Speed.")]
        [Min(0f)] public float driftCarryTopSpeed = 10f;

        [Tooltip("Speed band over which the carry fades out below the ceiling.")]
        [Min(0.01f)] public float driftCarryFadeBand = 2.5f;

        [Header("Drift — charge and payout")]
        [Tooltip("Charge rate multiplier at full lock INTO the slide: steering in " +
                 "tightens the arc and pays better.")]
        [Min(0f)] public float driftChargeInto = 1.5f;

        [Tooltip("Charge rate multiplier at full counter-steer: steering out widens the " +
                 "arc and nearly stops the clock. One stick axis, one decision.")]
        [Min(0f)] public float driftChargeOut = 0.35f;

        [Tooltip("Tier 1 gate, in units of CHARGE rather than seconds — at full " +
                 "commitment it arrives in about 0.6 s.")]
        [Min(0f)] public float driftTier1Seconds = 0.9f;

        [Tooltip("Tier 2 gate (about 1.3 s at full commitment).")]
        [Min(0f)] public float driftTier2Seconds = 1.9f;

        [Tooltip("Tier 3 gate (about 2.0 s at full commitment). A car nursed sideways " +
                 "on counter-steer may never reach it at all.")]
        [Min(0f)] public float driftTier3Seconds = 3.0f;

        [Tooltip("Charge at which the meter reads full — tier 3 plus a little, so the " +
                 "bar has somewhere to go once the last tier lands.")]
        [Min(0.01f)] public float driftChargeFull = 3.5f;

        [Tooltip("Boost duration granted per tier on release.")]
        [Min(0f)] public float driftBoostSeconds = 0.8f;

        [Tooltip("Forward impulse (N·s) per tier on release. The timed acceleration " +
                 "alone ramps in over a few frames, which reads as recovering rather " +
                 "than being fired out of the corner; this is the kick that makes the " +
                 "exit an event.")]
        [Min(0f)] public float driftExitImpulse = 0.55f;

        // ---- boost ---------------------------------------------------------------

        [Header("Boost and shield")]
        [Tooltip("Item-boost acceleration, m/s² (a surface boost pad is 9 and is not " +
                 "touched by this).")]
        [Min(0f)] public float boostAccel = 14f;

        [Tooltip("How long one boost charge lasts.")]
        [Min(0f)] public float boostSeconds = 1.6f;

        [Tooltip("Speed the item boost stops pushing towards. Without a ceiling the " +
                 "force kept accelerating the car well past what the drivetrain can " +
                 "reach, which is what made boosting read as skittish rather than fast.")]
        [Min(0f)] public float boostTopSpeed = 11f;

        [Tooltip("Speed band over which the boost fades out below the cap, so it tapers " +
                 "instead of switching off.")]
        [Min(0.01f)] public float boostFadeBand = 1.5f;

        [Tooltip("How long a shield lasts.")]
        [Min(0f)] public float shieldSeconds = 8f;

        // ---- being hit -----------------------------------------------------------

        [Header("Being hit — spin out (banana)")]
        [Tooltip("Grip while spun out.")]
        [Range(0.05f, 1f)] public float spinGripMult = 0.35f;

        [Tooltip("How long the spin lasts.")]
        [Min(0f)] public float spinSeconds = 1.4f;

        [Tooltip("Yaw torque while spun out (N·m). This has to beat the TYRES, not the " +
                 "inertia: each one puts roughly 0.5 N·m about the CoM even at the " +
                 "reduced grip, which is why 0.10 was invisible and 1.2 reads as a hit.")]
        [Min(0f)] public float spinTorque = 1.2f;

        [Tooltip("Drive scale while spinning. Zero means a hit costs momentum and you " +
                 "cannot power out of it.")]
        [Range(0f, 1f)] public float spinDriveMult = 0f;

        [Tooltip("Impulse along the hit's heading (kg·m/s).")]
        [Min(0f)] public float hitImpulseFwd = 0.6f;

        [Tooltip("Upward part of the same impulse.")]
        [Min(0f)] public float hitImpulseUp = 0.9f;

        [Header("Being hit — wreck (missile)")]
        [Tooltip("Limp time after a missile hit, before the car is lifted back onto the " +
                 "racing line.")]
        [Min(0f)] public float wreckSeconds = 1.5f;

        [Tooltip("Upward impulse — enough to visibly leave the ground.")]
        [Min(0f)] public float wreckImpulseUp = 3.2f;

        [Tooltip("Forward impulse along the missile's heading.")]
        [Min(0f)] public float wreckImpulseFwd = 1.4f;

        [Tooltip("Tumble, applied as one torque impulse.")]
        [Min(0f)] public float wreckTorque = 3.0f;

        [Tooltip("Immunity after recovering. Without it a second missile already in " +
                 "flight re-kills you the instant you reappear.")]
        [Min(0f)] public float invulnSeconds = 1.0f;

        // ---- weapons and hazards --------------------------------------------------

        [Header("Missile")]
        [Tooltip("Cruise speed (m/s). About 1.4× a Hard bot: catches, but dodgeable.")]
        [Min(0.1f)] public float missileSpeed = 11f;

        [Tooltip("Homing rate at range (rad/s). At 11 m/s, 2.2 rad/s is a 5 m turning " +
                 "radius — it still chases down a straight without being glued to the " +
                 "target's own line.")]
        [Min(0f)] public float missileTurnRate = 2.2f;

        [Tooltip("Inside this range the missile has committed and steers only weakly, " +
                 "so a late swerve genuinely makes it miss.")]
        [Min(0f)] public float missileCommitRange = 1.5f;

        [Tooltip("Turn rate once committed.")]
        [Min(0f)] public float missileCommitTurnRate = 0.6f;

        [Header("Banana and hazards (applies to the NEXT one dropped)")]
        [Tooltip("Trigger radius of a dropped banana. Sized against the CAR, not the " +
                 "peel — the car's colliders span only about 10 cm of height, so a " +
                 "small radius is easy to drive through at 10 m/s.")]
        [Min(0.01f)] public float bananaRadius = 0.13f;

        [Tooltip("Full gameplay radius of a smoke cloud once grown.")]
        [Min(0.01f)] public float smokeRadius = 0.75f;

        [Tooltip("Radius of an oil slick — oil spreads flatter and wider than smoke " +
                 "billows.")]
        [Min(0.01f)] public float slickRadius = 0.85f;

        [Tooltip("Grip inside an oil slick. Live: this one IS read per step, so it " +
                 "applies to slicks already on the track.")]
        [Range(0.05f, 1f)] public float slickGripMult = 0.45f;

        [Tooltip("How long a smoke cloud blinds you. Much longer-lived than the grip " +
                 "loss from oil: oil comes off the tyres in a moment, whereas not being " +
                 "able to see stays with you.")]
        [Min(0f)] public float blindSeconds = 2.6f;

        // ---- slipstream ------------------------------------------------------------

        [Header("Slipstream")]
        [Tooltip("How far behind the car ahead the tow reaches (m).")]
        [Min(0f)] public float draftRange = 3.0f;

        [Tooltip("Heading alignment required to be in the tow (degrees).")]
        [Min(0f)] public float draftConeDeg = 25f;

        [Tooltip("Tow acceleration, m/s² — well under a boost's 14.")]
        [Min(0f)] public float draftAccel = 4f;

        [Tooltip("Speed the tow stops pushing towards.")]
        [Min(0f)] public float draftTopSpeed = 11f;

#if UNITY_EDITOR
        /// <summary>Nothing in the arcade layer copies these into engine state,
        /// so no subscriber strictly needs this — it is raised for symmetry with
        /// the other tuning assets, and so a future consumer that DOES bake a
        /// value has the signal already flowing.</summary>
        private void OnValidate() => Core.Config.TuningBus.Raise(this);
#endif
    }
}
