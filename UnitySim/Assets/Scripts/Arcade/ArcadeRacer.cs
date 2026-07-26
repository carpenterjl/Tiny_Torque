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
            offTrackTime = ungroundedTime = 0f;
            warned = penalized = false;
            penaltyUntil = penaltyCooldownUntil = 0f;
            botHeldSince = 0f;
            hitLabel = null;
            hitUntil = flashUntil = 0f;
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

        /// <summary>Put the car's arcade channels back to their no-effect values.</summary>
        public void RestoreCar()
        {
            if (car == null) return;
            car.arcadeBoostAccel = 0f;
            car.arcadeGripMult = gripBase;
            car.arcadeYawTorque = 0f;
            car.arcadeDriveMult = driveBase;
        }
    }
}
