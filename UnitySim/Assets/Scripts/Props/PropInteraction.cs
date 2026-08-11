using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// Polled proximity + Interact-press arbitration for world props. Distance
    /// polling instead of trigger colliders for Flag.cs's reasons: a car is
    /// one transform with five colliders (multi-fire), OnTriggerStay stops when
    /// a rigidbody sleeps, and LAN kinematic followers make trigger callbacks
    /// murky. The car list is cached and refreshed every couple of seconds —
    /// cars appear at scene build and (rarely) via IPC spawn.
    ///
    /// InputReader's edge checks are non-consuming (every caller in a frame
    /// sees the same press), so <see cref="ClaimInteract"/> arbitrates: in any
    /// one frame exactly one prop — the nearest claimant — wins the press.
    /// </summary>
    public static class PropInteraction
    {
        /// <summary>How close a car must be for the Interact key to reach a prop.</summary>
        public const float InteractRadius = 1.2f;

        private const float RefreshInterval = 2f;

        private static readonly List<CarVehicle> _cars = new List<CarVehicle>();
        private static float _nextRefresh;

        // Per-frame interact arbitration.
        private static int _claimFrame = -1;

        private static void RefreshCars()
        {
            if (Time.unscaledTime < _nextRefresh && _cars.Count > 0) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            _cars.Clear();
            _cars.AddRange(Object.FindObjectsByType<CarVehicle>(FindObjectsSortMode.None));
        }

        /// <summary>Nearest car transform within radius of a point, or null.</summary>
        public static Transform NearestCar(Vector3 pos, float radius)
        {
            RefreshCars();
            Transform best = null;
            float bestSq = radius * radius;
            for (int i = 0; i < _cars.Count; i++)
            {
                var car = _cars[i];
                if (car == null) continue;
                float sq = (car.transform.position - pos).sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; best = car.transform; }
            }
            return best;
        }

        /// <summary>
        /// Called by a prop that saw Interact pressed this frame with a car in
        /// range. The first claimant in a frame wins the press; everyone else
        /// is told no — so a single press toggles exactly one prop even when
        /// two overlap a car's interact radius (a degenerate authoring case;
        /// which one wins is Update order, and stably so).
        /// </summary>
        public static bool ClaimInteract(Object prop, Vector3 pos)
        {
            if (NearestCar(pos, InteractRadius) == null) return false;
            if (_claimFrame == Time.frameCount) return false;
            _claimFrame = Time.frameCount;
            return true;
        }
    }
}
