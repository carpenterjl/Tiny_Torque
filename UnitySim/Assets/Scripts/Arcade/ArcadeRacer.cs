using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// One car's arcade state. A pure state bag — every transition is driven by
    /// <see cref="ArcadeDirector"/>, and this component has no Update of its own.
    ///
    /// It lives on the car root rather than in a dictionary keyed by CarVehicle
    /// because trigger callbacks hand you a Collider: resolving a hit is then
    /// <c>GetComponentInParent&lt;CarVehicle&gt;()</c> plus one GetComponent, the
    /// same idiom Checkpoint uses. It also dies with its car, which matters in LAN
    /// where the roster destroys cars mid-session and a dictionary would leak.
    ///
    /// On a LAN client this exists on the ghost cars too, mirroring host state for
    /// the HUD; it is never authoritative there.
    /// </summary>
    public sealed class ArcadeRacer : MonoBehaviour
    {
        public PlayerRig rig;
        public CarVehicle car;
        public int netSlot = -1;          // LAN roster slot; -1 in local sessions
        public bool isBot;

        /// <summary>
        /// This machine drives this car. True for every registered car on a host
        /// or in a local session; on a LAN client, true only for the one car it
        /// simulates — the rest are ghosts posed from the stream, with no physics
        /// to apply an effect to.
        /// </summary>
        public bool slotIsLocal;

        /// <summary>Resting tyre-grip multiplier for this car: 1 on Sim handling,
        /// <see cref="ArcadeConfig.HandlingGripBonus"/> on Arcade. Every write to
        /// <c>arcadeGripMult</c> goes through this so the bonus survives a
        /// spin-out, a respawn and a race restart.</summary>
        public float gripBase = 1f;

        /// <summary>Resting drive-command scale for this car: 1 on Sim handling,
        /// <see cref="ArcadeConfig.HandlingDriveScale"/> on Arcade. The exact twin
        /// of <see cref="gripBase"/>, and for the same reason — ApplyEffects
        /// rewrites <c>arcadeDriveMult</c> every frame, so the baseline has to
        /// live here rather than being written onto the car once.</summary>
        public float driveBase = 1f;

        // ---- inventory ----
        public ItemKind held = ItemKind.None;
        public int charges;
        public bool rolling;
        public float rollEndsAt;
        public ItemKind rollPick;         // decided at pickup, revealed when the roll ends
        public ItemKind rollFace;         // what the HUD is currently showing

        // ---- active effects (absolute ArcadeDirector.Clock deadlines) ----
        public float boostUntil;
        public float spinUntil;
        public float spinTorqueSigned;    // which way this hit threw the car
        public float shieldUntil;
        /// <summary>Bubble + orbs while the shield is up. Parented to the car, so
        /// it dies with the car; the director still tears it down explicitly on
        /// expiry, on a block and on ClearAll so a car can never be seen wearing
        /// a shield it no longer has.</summary>
        [System.NonSerialized] public Transform shieldViz;
        /// <summary>The orbiting-orb ring inside <see cref="shieldViz"/>, cached at
        /// creation so the per-frame spin costs no hierarchy lookup.</summary>
        [System.NonSerialized] public Transform shieldOrbs;

        // ---- wrecked (missile hit) ----
        /// <summary>Limp until this deadline, then get lifted back onto the line.</summary>
        public float wreckedUntil;
        /// <summary>Set on the hit, cleared once the recovery teleport has run —
        /// so recovery fires exactly once no matter how many frames elapse.</summary>
        public bool awaitingRecover;
        /// <summary>Immune to further hits until this deadline. Covers the moment
        /// of reappearing, when a second missile would otherwise be sitting on
        /// top of the recovery point.</summary>
        public float invulnUntil;

        // ---- area hazards ----
        // Both deadlines are re-armed every frame the car is inside the hazard, so
        // it is LEAVING that starts the recovery, and a car parked in a cloud stays
        // affected without any of it being a special case.
        /// <summary>Screen is fogged until this deadline (smoke cloud).</summary>
        public float blindUntil;
        /// <summary>Clock at the rising edge of the current blinding. The tint's
        /// hold-then-fade envelope is anchored here rather than to the time left,
        /// because on a LAN client <see cref="blindUntil"/> is only a short liveness
        /// token refreshed by each sync packet — a remaining-time envelope would sit
        /// permanently mid-fade there.</summary>
        public float blindStartedAt;
        /// <summary>Grip is cut until this deadline (oil slick).</summary>
        public float slickUntil;

        // ---- drift boost (mini-turbo) ----
        /// <summary>Which way this car is committed to sliding: +1 right, -1 left,
        /// 0 not drifting. Latched from the steering command at the moment the
        /// handbrake commits and held until release, which is what makes the drift
        /// a thing you DO rather than a state the physics might grant you.</summary>
        public int driftDir;
        /// <summary>The direction of the drift currently being straightened out
        /// of. A second field because <see cref="driftDir"/> has to read zero the
        /// instant the slide ends — <see cref="Drifting"/> and every grip and
        /// assist decision keys off it — while the exit arc still needs to know
        /// which way it is unwinding.</summary>
        public int driftDirLast = 1;
        /// <summary>Seconds of slide banked so far. Reset the moment the slide
        /// ends, whether it paid out or not.</summary>
        public float driftCharge;
        /// <summary>0 = charging but no tier yet, 1..3 = blue / orange / purple.</summary>
        public int driftTier;
        /// <summary>Arcade clock at which the entry kick stops being allowed its
        /// larger torque clamp.</summary>
        public float driftKickUntil;
        /// <summary>Straightening out after a release: the same yaw controller,
        /// aimed at zero slip instead of the drift angle, so the car exits down
        /// the road rather than sideways with a boost lit.</summary>
        public float driftStraightenUntil;
        /// <summary>Sparks at the rear wheels while charging. Same lifecycle rules
        /// as <see cref="shieldViz"/>: parented to the car so it dies with it, and
        /// still torn down explicitly everywhere the charge can end.</summary>
        [System.NonSerialized] public Transform driftSparks;

        /// <summary>
        /// Mini-turbo payout deadline, kept SEPARATE from <see cref="boostUntil"/>
        /// on purpose.
        ///
        /// A drift is decided entirely by the machine that simulates the car —
        /// its own handbrake, its own slip angle — whereas <c>boostUntil</c> is
        /// host-adjudicated and overwritten wholesale by every sync packet. Paying
        /// a mini-turbo into <c>boostUntil</c> on a LAN client therefore hands it
        /// out and takes it away again within 66 ms. Two fields, two owners, and
        /// <see cref="Boosting"/> is the only thing that has to know.
        /// </summary>
        public float driftBoostUntil;

        /// <summary>Currently under any boost — item, pad-independent drift
        /// payout, or both.</summary>
        public bool Boosting =>
            ArcadeDirector.Clock < boostUntil || ArcadeDirector.Clock < driftBoostUntil;

        /// <summary>Committed to a slide right now (not merely straightening out
        /// of one).</summary>
        public bool Drifting => driftDir != 0;

        /// <summary>Slipstream acceleration, recomputed at the 5 Hz position tick
        /// and re-applied every frame. A field rather than a per-frame test
        /// because the cone-and-range search is O(n²) over the field and does not
        /// need to run at 60 Hz to feel right.</summary>
        public float draftAccel;

        // ---- on-screen hit feedback ----
        // State rather than an event subscription, so the solo HUD and the
        // per-viewport split-screen HUD read the same three fields and neither
        // has to duplicate plumbing. The arcade EVENT stream stays the audio
        // layer's concern.
        /// <summary>Banner text ("SPUN OUT!"), shown until <see cref="hitUntil"/>.</summary>
        public string hitLabel;
        public Color hitColor = Color.white;
        public float hitUntil;
        /// <summary>Short full-viewport colour wash on the moment of impact.</summary>
        public Color flashColor;
        public float flashUntil;

        /// <summary>Raise a banner and a flash on this car. Duration is in arcade
        /// clock seconds, so a pause holds it rather than eating it.</summary>
        public void ShowHit(string label, Color color, float seconds, float flashSeconds)
        {
            hitLabel = label;
            hitColor = color;
            hitUntil = ArcadeDirector.Clock + seconds;
            flashColor = color;
            flashUntil = ArcadeDirector.Clock + flashSeconds;
        }

        /// <summary>LAN client mirror of "a missile is locked onto me". The client
        /// owns no Missile components — projectiles arrive as poses — so the
        /// warning cannot be derived locally and is carried in the sync stream
        /// instead.</summary>
        public bool incomingRemote;

        // ---- race position ----
        public int spineHint = -1;        // seeds TrackSpine.Project
        public float trackProgress;       // laps * spine length + arc position
        public int livePosition = 1;      // 1 = leader
        public int points;

        // ---- track limits ----
        public float offTrackTime;
        public float ungroundedTime;
        public bool warned;
        public bool penalized;
        public float penaltyUntil;
        public float penaltyCooldownUntil;

        // ---- bots ----
        public float botNextDecision;
        public float botHeldSince;

        public bool HasItem => held != ItemKind.None;
        public bool Busy => rolling || held != ItemKind.None;
        /// <summary>Destroyed and not yet recovered — no pickups, no item use.</summary>
        public bool Wrecked => ArcadeDirector.Clock < wreckedUntil;
        /// <summary>Inside (or just left) a smoke cloud.</summary>
        public bool Blinded => ArcadeDirector.Clock < blindUntil;
        /// <summary>Inside (or just left) an oil slick.</summary>
        public bool OnSlick => ArcadeDirector.Clock < slickUntil;

        /// <summary>Clear inventory and every active effect, restoring the car to
        /// neutral. Used on race start/restart and on respawn.</summary>
        public void ClearAll()
        {
            held = ItemKind.None;
            charges = 0;
            rolling = false;
            rollPick = rollFace = ItemKind.None;
            boostUntil = spinUntil = shieldUntil = 0f;
            wreckedUntil = invulnUntil = 0f;
            awaitingRecover = false;
            blindUntil = blindStartedAt = slickUntil = 0f;
            offTrackTime = ungroundedTime = 0f;
            warned = penalized = false;
            penaltyUntil = penaltyCooldownUntil = 0f;
            botHeldSince = 0f;
            hitLabel = null;
            hitUntil = flashUntil = 0f;
            driftCharge = draftAccel = 0f;
            driftTier = driftDir = 0;
            driftKickUntil = driftStraightenUntil = driftBoostUntil = 0f;
            HideDriftSparks();
            HideShield();
            RestoreCar();
        }

        /// <summary>Tear down the shield visual if one is up. Idempotent.</summary>
        public void HideShield()
        {
            if (shieldViz != null) Destroy(shieldViz.gameObject);
            shieldViz = null;
            shieldOrbs = null;
        }

        /// <summary>Tear down the drift sparks if any are up. Idempotent.</summary>
        public void HideDriftSparks()
        {
            if (driftSparks != null) Destroy(driftSparks.gameObject);
            driftSparks = null;
        }

        /// <summary>Put the car's arcade channels back to their no-effect values.</summary>
        public void RestoreCar()
        {
            if (car == null) return;
            car.arcadeBoostAccel = 0f;
            car.arcadeGripMult = gripBase;
            car.arcadeYawTorque = 0f;
            car.arcadeDriveMult = driveBase;
            car.arcadeAssistMult = 1f;
            car.arcadeHandbrakeMult = 1f;
        }
    }
}
