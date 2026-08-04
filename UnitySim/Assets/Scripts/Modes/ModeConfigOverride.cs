using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// The mini-game modes' numbers, as an asset you can edit while the match
    /// runs.
    ///
    /// Same contract as <c>AssistTuningOverride</c>, and for the same reasons:
    /// <see cref="ModeConfig"/> keeps every value below as its shipped default
    /// and reads this object only when one is installed, so <b>no asset assigned
    /// means the literals, verbatim</b> — the field initialisers here ARE those
    /// literals, and <c>[DSC]</c> compares the two so neither copy can drift.
    ///
    /// <b>Nearly everything here is live because of where it is read, not
    /// because of any machinery.</b> Damage is read when a car is hit, a jump
    /// impulse when the button is pressed, the flag's return timer every frame
    /// of the countdown — so the accessor sees the new number the next time the
    /// event happens. The exceptions are the values that get copied into engine
    /// state once: the ball's mass, radius and damping, and the derby's starting
    /// health. Those consumers subscribe to <c>TuningBus</c> and re-apply, which
    /// is why dragging them mid-match works too.
    ///
    /// Units are the game's: metres, seconds, m/s, and the 1:10 RC scale (a car
    /// is 0.42 m long and about 1.6 kg, and 8 m/s is quick).
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Mode Tuning", fileName = "ModeTuning")]
    public sealed class ModeConfigOverride : ScriptableObject
    {
        // ---- demolition derby -------------------------------------------------

        [Header("Derby — health")]
        [Tooltip("Starting health, and the number the HUD bar is a fraction of. " +
                 "Live: every car's bar rescales on the spot, keeping the fraction it " +
                 "was on, so halving this halves how much punishment is left rather " +
                 "than killing the field.")]
        [Min(1f)] public float derbyMaxHealth = 100f;

        [Header("Derby — hit strength")]
        [Tooltip("Below this closing speed (m/s) a touch does nothing at all, so " +
                 "jostling in a corner is not a slow death.")]
        [Min(0f)] public float impactMinSpeed = 1.6f;

        [Tooltip("Closing speed (m/s) that deals full head-on damage; faster than this " +
                 "is clamped, so a boost pad cannot one-shot anybody. Lowering it is " +
                 "the fastest way to make the whole mode hit harder.")]
        [Min(0.01f)] public float impactRefSpeed = 7.0f;

        [Tooltip("Damage a square, full-speed ram deals to the car being hit.")]
        [Min(0f)] public float ramDamage = 34f;

        [Tooltip("Damage a side-on or glancing car-to-car hit deals to BOTH cars. " +
                 "Trading paint costs you something too — that is what stops the derby " +
                 "from being a game of chicken.")]
        [Min(0f)] public float sideDamage = 7f;

        [Tooltip("Damage for slamming a wall, scaled the same way.")]
        [Min(0f)] public float wallDamage = 9f;

        [Tooltip("How square a hit has to be to count as a ram: the dot of the " +
                 "attacker's forward against the contact normal. 0.72 is about 44°; " +
                 "raise it and only clean nose-on hits pay full damage.")]
        [Range(0f, 1f)] public float ramAlignment = 0.72f;

        [Tooltip("One damage event per car pair per this many seconds, so a single " +
                 "shunt that generates six contacts is still one hit.")]
        [Min(0f)] public float hitCooldown = 0.35f;

        [Tooltip("Seconds a freshly (re)spawned car cannot be damaged.")]
        [Min(0f)] public float spawnGrace = 2.0f;

        // ---- pickups ----------------------------------------------------------

        [Header("Pickups and mines")]
        [Tooltip("Health restored by a health pack.")]
        [Min(0f)] public float healthPackHeal = 35f;

        [Tooltip("Seconds before a taken pickup comes back.")]
        [Min(0f)] public float pickupRespawnSec = 12f;

        [Tooltip("Radius (m) of a mine's blast. Damage falls off to zero at the edge.")]
        [Min(0.01f)] public float mineRadius = 1.1f;

        [Tooltip("Damage at the centre of the blast.")]
        [Min(0f)] public float mineDamage = 45f;

        [Tooltip("The owner cannot trip their own mine for this long.")]
        [Min(0f)] public float mineOwnerGrace = 1.5f;

        // ---- capture the flag ---------------------------------------------------

        [Header("Capture the flag")]
        [Tooltip("How close (m) a car must get to pick a flag up or score.")]
        [Min(0.01f)] public float flagTouchRadius = 0.55f;

        [Tooltip("An impact this hard (N·s) knocks the flag out of a carrier's hands.")]
        [Min(0f)] public float flagDropImpulse = 2.4f;

        [Tooltip("A dropped flag returns itself home after this long, so a flag punted " +
                 "into a corner does not end the match.")]
        [Min(0f)] public float flagAutoReturnSec = 20f;

        [Tooltip("Carrying is meant to be a risk: the carrier's drive is scaled by this.")]
        [Range(0.1f, 1f)] public float carrierDriveMult = 0.92f;

        // ---- soccer -------------------------------------------------------------

        [Header("Soccer — the ball")]
        [Tooltip("Ball radius (m). Live: the collider and the visible sphere are both " +
                 "resized on the spot.")]
        [Min(0.01f)] public float ballRadius = 0.13f;

        [Tooltip("Ball mass (kg) — the 'ball weight' knob. Against a ~1.6 kg car, the " +
                 "shipped 0.35 is light enough to be launched and heavy enough to hold " +
                 "a line. Live.")]
        [Min(0.001f)] public float ballMass = 0.35f;

        [Tooltip("Linear damping: how fast a rolling ball gives up. Live.")]
        [Min(0f)] public float ballDrag = 0.25f;

        [Tooltip("Angular damping: how fast spin bleeds off. Live.")]
        [Min(0f)] public float ballAngularDrag = 0.35f;

        [Tooltip("Extra kick a car imparts beyond the raw collision, so a touch reads " +
                 "as a strike rather than a nudge.")]
        [Min(0f)] public float ballHitBoost = 1.35f;

        [Tooltip("Gravity felt by the BALL, as a multiple of the world's. 1 is normal; " +
                 "below 1 gives the floaty, hang-time ball a big arena wants, above 1 " +
                 "keeps it on the deck. The ball only — the cars are unaffected, which " +
                 "is what makes this safe to drag mid-match. For everyone at once use " +
                 "Arena Gravity Scale below.")]
        [Range(0f, 3f)] public float ballGravityScale = 1f;

        [Tooltip("Seconds of celebration between a goal and the kick-off.")]
        [Min(0f)] public float goalCelebrationSec = 3.0f;

        // ---- aerial -------------------------------------------------------------

        [Header("Aerial (soccer) — jump, flip, boost")]
        [Tooltip("Upward impulse of the first jump, in N·s — the 'jump height' knob. " +
                 "On a ~1.6 kg car, 8.6 N·s is about 5.4 m/s off the floor.")]
        [Min(0f)] public float jumpImpulse = 8.6f;

        [Tooltip("The second jump is weaker — it is a correction, not a lift.")]
        [Min(0f)] public float doubleJumpImpulse = 6.0f;

        [Tooltip("Window after the first jump in which a second press flips instead of " +
                 "jumping, if a direction is held.")]
        [Min(0f)] public float flipWindowSec = 1.25f;

        [Tooltip("Flip impulse along the held direction (N·s).")]
        [Min(0f)] public float flipImpulse = 3.2f;

        [Tooltip("Flip torque, which is what makes it read as a barrel roll rather " +
                 "than a shove.")]
        [Min(0f)] public float flipTorque = 1.055f;

        [Tooltip("Air-roll authority, in N·m per unit of stick.")]
        [Min(0f)] public float airTorque = 0.05f;

        [Tooltip("Boost meter: a full tank, in seconds of use.")]
        [Min(0.01f)] public float boostTankSec = 2.4f;

        [Tooltip("How fast a pad refills the tank (tank-seconds per second).")]
        [Min(0f)] public float boostRefillPerSec = 0.55f;

        [Tooltip("Boost acceleration, m/s².")]
        [Min(0f)] public float boostAccel = 11f;

        // ---- shared --------------------------------------------------------------

        [Header("Arena — shared")]
        [Tooltip("Seconds a dead car spectates before the match notices, so a kill has " +
                 "a beat to land.")]
        [Min(0f)] public float deathBeatSec = 1.6f;

        [Tooltip("Gravity for the whole arena, as a multiple of the project's own — " +
                 "cars, ball and everything else. 1 leaves the world exactly as it is " +
                 "and nothing is touched.\n\n" +
                 "Applied only while an arena match is live, and put back when it ends, " +
                 "so a moon-gravity derby cannot follow you into a circuit race. Note " +
                 "that the cars' suspension is authored at 1 g: well under 1 makes them " +
                 "ride high and skate, which is a look, not a bug.")]
        [Range(0.05f, 2f)] public float arenaGravityScale = 1f;

#if UNITY_EDITOR
        /// <summary>Tell the handful of consumers that copy a value into engine
        /// state (the ball's Rigidbody, each racer's health bar) to copy it
        /// again. Everything else re-reads through the accessors on its own.</summary>
        private void OnValidate() => Core.Config.TuningBus.Raise(this);
#endif
    }
}
