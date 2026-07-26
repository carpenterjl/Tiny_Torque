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

        /// <summary>Clear inventory and every active effect, restoring the car to
        /// neutral. Used on race start/restart and on respawn.</summary>
        public void ClearAll()
        {
            held = ItemKind.None;
            charges = 0;
            rolling = false;
            rollPick = rollFace = ItemKind.None;
            boostUntil = spinUntil = shieldUntil = 0f;
            offTrackTime = ungroundedTime = 0f;
            warned = penalized = false;
            penaltyUntil = penaltyCooldownUntil = 0f;
            botHeldSince = 0f;
            RestoreCar();
        }

        /// <summary>Put the car's arcade channels back to their no-effect values.</summary>
        public void RestoreCar()
        {
            if (car == null) return;
            car.arcadeBoostAccel = 0f;
            car.arcadeGripMult = 1f;
            car.arcadeYawTorque = 0f;
        }
    }
}
