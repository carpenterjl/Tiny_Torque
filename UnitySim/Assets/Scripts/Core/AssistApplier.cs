using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Gets assist settings onto cars — the saved floor at rig build, and the
    /// live update when a slider moves mid-session.
    ///
    /// Both exist because <c>TrackBootstrap</c>'s single
    /// <c>built.car.assists = slot.assists</c> is otherwise the ONLY write in the
    /// game: without <see cref="ApplyLive"/> a slider moved from the pause menu
    /// does nothing until the next scene load, which reads as a broken control
    /// rather than as a deferred one.
    /// </summary>
    public static class AssistApplier
    {
        /// <summary>
        /// Raise a local human's assists to at least the saved preset.
        ///
        /// A max rather than an assignment: Arcade sessions set a deliberately
        /// high handling floor of their own, and this must never pull that down.
        ///
        /// Two exclusions, both deliberate:
        ///   * BOTS keep exactly what they were given — they drive their own
        ///     racing line and assists would flatter their lap times;
        ///   * FIRMWARE rigs are skipped explicitly, mirroring
        ///     <c>ArcadeDirector.Register</c>'s refusal of the same case. Leaning
        ///     on <c>assistsActive</c> alone would be fragile if a firmware rig
        ///     were ever mode-toggled at runtime — and "C code always faces the
        ///     raw physics" is the one promise the whole sim rests on.
        /// </summary>
        public static void ApplyFloor(CarVehicle car, PlayerSlot slot, AssistSettings floor)
        {
            if (slot == null) return;
            if (slot.isBot || slot.control == DriveControl.BotAI) return;
            if (slot.control == DriveControl.Firmware) return;
            ApplyFloor(car, floor);
        }

        /// <summary>Floor for a car the caller has ALREADY established is a local
        /// human's — the LAN path, where the only rig this machine builds for
        /// itself is its own, and there is no slot to inspect.</summary>
        public static void ApplyFloor(CarVehicle car, AssistSettings floor)
        {
            if (car == null) return;
            var a = car.assists;
            a.steer = Mathf.Max(a.steer, floor.steer);
            a.stability = Mathf.Max(a.stability, floor.stability);
            a.traction = Mathf.Max(a.traction, floor.traction);
            a.abs = Mathf.Max(a.abs, floor.abs);
            car.assists = a;
        }

        /// <summary>
        /// Push the current saved assists onto the live rigs, so a slider moved
        /// mid-session is felt on the next physics step.
        ///
        /// P1's and P2's sliders map onto LOCAL HUMAN rigs in order. Counting the
        /// whole list instead would hand player 2's settings to the first bot in
        /// a bot race — the rigs are not in "humans first" order and never were.
        ///
        /// In LAN this needs no wire message at all: since protocol 4 each client
        /// simulates its own car, so a local assist change applies to the car
        /// that machine is actually stepping. That falls straight out of
        /// owner-authority.
        /// </summary>
        public static void ApplyLive(IReadOnlyList<PlayerRig> rigs)
        {
            if (rigs == null) return;
            var s = Persistence.SettingsStore.Current;

            int human = 0;
            for (int i = 0; i < rigs.Count; i++)
            {
                var rig = rigs[i];
                if (rig == null || rig.car == null || rig.slot == null) continue;
                if (rig.slot.isBot || rig.slot.control == DriveControl.BotAI) continue;
                if (rig.slot.control == DriveControl.Firmware) continue;

                rig.car.assists = human == 0 ? SessionConfig.P1Assists(s)
                                             : SessionConfig.P2Assists(s);
                human++;
            }
        }
    }
}
