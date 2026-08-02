using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Combat
{
    /// <summary>
    /// Something a weapon can hurt. The flight scene's OWN damage receiver,
    /// deliberately not <c>MatchRacer</c>: that class is a limb of the arena
    /// stack — registered by a <c>ModeDirector</c>, keyed on <c>CarVehicle</c>,
    /// scored against a match clock — and dragging it into a debug airfield
    /// would mean standing up a match to shoot a barrel. This is the whole
    /// contract in one component: health, a category (because missiles fly
    /// twice as fast at AIR targets — the Hydra's hidden speed modifier, here
    /// in the open), and an event on death.
    ///
    /// Discovery follows the project convention twice over: weapons find their
    /// victim with <c>GetComponentInParent&lt;WeaponTarget&gt;()</c> in the
    /// collision callback (never a layer), and the lock-on cone iterates the
    /// static <see cref="All"/> registry (never a scene scan per frame).
    /// </summary>
    public sealed class WeaponTarget : MonoBehaviour
    {
        public enum Category { Air = 0, Ground = 1, Static = 2 }

        /// <summary>Every live target in the scene. OnEnable/OnDisable keep it
        /// honest across destroys and respawns, so a reader never needs a null
        /// sweep — a disabled or dying target is simply not in the list.</summary>
        public static readonly List<WeaponTarget> All = new List<WeaponTarget>();

        public Category category = Category.Static;
        public float maxHealth = 20f;

        /// <summary>Roughly how big it is on screen (m) — the lock circle's
        /// radius and the gun's forgiveness both read this.</summary>
        public float radius = 2f;

        public float Health { get; private set; }
        public bool Alive => Health > 0f;

        /// <summary>Raised once, at the moment health reaches zero, with the
        /// position of the killing blow. The owner (spawner, drone, runner)
        /// decides what dying LOOKS like; this component only decides when.</summary>
        public event System.Action<WeaponTarget, Vector3> Destroyed;

        private void Awake() => Health = maxHealth;

        private void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        private void OnDisable() => All.Remove(this);

        public void ApplyDamage(float amount, Vector3 worldPos)
        {
            if (!Alive || amount <= 0f) return;
            Health -= amount;
            if (Health > 0f) return;
            Health = 0f;
            Destroyed?.Invoke(this, worldPos);
        }

        /// <summary>Back to full — how a respawning drone reuses its own body
        /// rather than being reinstantiated.</summary>
        public void ResetHealth() => Health = maxHealth;

        /// <summary>The point weapons aim at and measure cones against — the
        /// rigidbody's centre when there is one, else the transform.</summary>
        public Vector3 AimPoint
        {
            get
            {
                var rb = GetComponent<Rigidbody>();
                return rb != null ? rb.worldCenterOfMass : transform.position;
            }
        }

        /// <summary>Written by a kinematic mover each FixedUpdate. A kinematic
        /// rigidbody driven by MovePosition reads zero velocity off the physics,
        /// so the mover — which knows exactly how fast it is going — reports it
        /// here instead of the reader guessing by differencing.</summary>
        [System.NonSerialized] public Vector3 reportedVelocity;

        /// <summary>World velocity, for lead computation and homing. A dynamic
        /// body answers from the physics; a kinematic mover answers through
        /// <see cref="reportedVelocity"/>; a static answers zero.</summary>
        public Vector3 Velocity
        {
            get
            {
                var rb = GetComponent<Rigidbody>();
                return rb != null && !rb.isKinematic ? rb.linearVelocity : reportedVelocity;
            }
        }
    }
}
