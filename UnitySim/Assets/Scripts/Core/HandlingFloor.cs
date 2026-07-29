using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Holds a simulated car at the session's handling mode, every frame.
    ///
    /// <see cref="SessionConfig.ArcadeHandling"/> used to be consumed exactly
    /// once, at rig build, and only by the arcade lap-race path — which made
    /// the Arcade/Sim choice structurally unreachable in free roam and the
    /// arena modes, and forced the in-session settings panel to show the
    /// toggle as read-only ("set when the session started"). This component is
    /// the mode-independent application point: it re-asserts the handling
    /// floor each frame, so the toggle is live mid-session and no later writer
    /// (a respawn's RestoreCar, a future mode script) can strand a car on
    /// stale values.
    ///
    /// Ownership is deliberately narrow. In a director session the
    /// ArcadeDirector rewrites every arcade channel every frame from the
    /// racer's gripBase/driveBase/stabilityBase, so here we steer those BASES
    /// and let ApplyEffects keep its per-frame path — drifts, spins and wrecks
    /// stand the boost down exactly as before. With no director, nothing else
    /// writes the three handling channels (RestoreCar needs a racer,
    /// ResetVehicleTo touches none), so they are written directly. The other
    /// arcade channels — boost, yaw, assistMult, handbrakeMult, aerial —
    /// belong to the director and AerialControl and are never touched here.
    ///
    /// Firmware rigs never get this component (TrackBootstrap skips them at
    /// attach), so C controllers keep facing the raw physics and the Opus
    /// mission stays bit-identical by construction.
    /// </summary>
    public sealed class HandlingFloor : MonoBehaviour
    {
        public Vehicles.CarVehicle car;
        public PlayerRig rig;

        // The sim-handling assists this rig was built with (slot values plus
        // the saved preset floor), restored when the toggle goes off
        // mid-session so a BOT does not keep the arcade floor forever. Humans
        // are deliberately not restored from here: the settings panel calls
        // AssistApplier.ApplyLive on the same transition with the live slider
        // values, and a stale snapshot written a frame later would stomp it.
        private Vehicles.AssistSettings _simAssists;
        private bool _wasOn;

        private void Start()
        {
            // Start, not Awake: AddComponent runs Awake before the caller has
            // assigned the fields above, and the snapshot must be taken after
            // the rig's slot assists + preset floor have been applied.
            if (car != null) _simAssists = car.assists;
            _wasOn = SessionConfig.ArcadeHandling;
            Apply(_wasOn);
        }

        private void Update()
        {
            bool on = SessionConfig.ArcadeHandling;
            bool botRig = rig != null && rig.slot != null &&
                          (rig.slot.isBot || rig.slot.control == DriveControl.BotAI);
            if (_wasOn && !on && botRig && car != null) car.assists = _simAssists;
            _wasOn = on;
            Apply(on);
        }

        private void Apply(bool on)
        {
            if (car == null) return;
            var racer = rig != null ? rig.arcade : null;
            if (racer != null)
            {
                racer.gripBase = on ? Arcade.ArcadeConfig.HandlingGripBonus : 1f;
                racer.driveBase = on ? Arcade.ArcadeConfig.HandlingDriveScale : 1f;
                racer.stabilityBase = on ? Arcade.ArcadeConfig.HandlingStabilityBoost : 1f;
            }
            else
            {
                car.arcadeGripMult = on ? Arcade.ArcadeConfig.HandlingGripBonus : 1f;
                car.arcadeDriveMult = on ? Arcade.ArcadeConfig.HandlingDriveScale : 1f;
                car.arcadeStabilityMult = on ? Arcade.ArcadeConfig.HandlingStabilityBoost : 1f;
            }
            // Downforce is HandlingFloor's channel in BOTH session kinds — the
            // director's ApplyEffects deliberately does not own it.
            car.arcadeDownforce = on ? Arcade.ArcadeConfig.HandlingDownforce : 0f;
            // Bots included, mirroring the old ApplyArcadeHandling's deliberate
            // bot inclusion (the arcade floor is a physics change, not a
            // player preference). Max per channel, so higher Options sliders
            // survive — and because it re-runs every frame it also repairs the
            // latent ApplyLive bug where a mid-session slider write dropped
            // the floor entirely.
            if (on) AssistApplier.ApplyFloor(car, Arcade.ArcadeConfig.HandlingAssists);
        }
    }
}
