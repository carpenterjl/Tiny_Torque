using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// One car's mini-game state. A pure state bag — every transition is driven
    /// by the mode's director — modelled on <c>ArcadeRacer</c>, and living on
    /// the car's own GameObject for the same reason: a trigger volume finds it
    /// with <c>GetComponentInParent</c> from whatever collider it touched.
    ///
    /// Shared by all three modes rather than split three ways. Health means
    /// nothing in soccer and score means nothing in a derby, but a per-car bag
    /// with two unused fields is a much smaller thing than three near-identical
    /// components plus the code to pick between them.
    /// </summary>
    public sealed class MatchRacer : MonoBehaviour
    {
        public PlayerRig rig;
        public CarVehicle car;
        public int netSlot = -1;
        public bool isBot;

        /// <summary>True when this machine simulates the car. A LAN client's
        /// view of a remote car is a follower, and followers decide nothing.</summary>
        public bool slotIsLocal;

        /// <summary>Team index, or -1 in a free-for-all.</summary>
        public int team = -1;

        public float health;
        public float maxHealth = ModeConfig.DerbyMaxHealth;

        /// <summary>Still in the match. A dead car spectates; it is not
        /// destroyed, because its camera is still someone's viewport.</summary>
        public bool alive = true;

        /// <summary>Captures, goals — whatever this mode counts.</summary>
        public int score;

        /// <summary>Finishing place once eliminated, 1-based; 0 while alive.</summary>
        public int place;

        /// <summary>Director clock at the moment this car died, so the wreck has
        /// a beat to play out before it becomes a frozen spectator.</summary>
        public float deathAt;

        /// <summary>Clock (director time) before which this car cannot be hurt —
        /// set on spawn and respawn so nobody dies to the countdown.</summary>
        public float invulnUntil;

        /// <summary>Carried flag's team id, or -1. CTF only.</summary>
        public int carrying = -1;

        /// <summary>Mines in hand, dropped with the use-item button. Derby only;
        /// one at a time, so the crate is worth going back for.</summary>
        public int heldMines;

        public float Health01 => maxHealth <= 0f ? 0f : Mathf.Clamp01(health / maxHealth);

        public void ResetForMatch(float clock)
        {
            health = maxHealth;
            alive = true;
            score = 0;
            place = 0;
            carrying = -1;
            heldMines = 0;
            invulnUntil = clock + ModeConfig.SpawnGrace;
        }

        /// <summary>Apply damage, clamped at zero. Returns true when this was the
        /// blow that killed the car, so the caller runs the death once.</summary>
        public bool ApplyDamage(float amount, float clock)
        {
            if (!alive || amount <= 0f || clock < invulnUntil) return false;
            health = Mathf.Max(0f, health - amount);
            if (health > 0f) return false;
            alive = false;
            return true;
        }

        public void Heal(float amount) =>
            health = Mathf.Clamp(health + amount, 0f, maxHealth);
    }
}
