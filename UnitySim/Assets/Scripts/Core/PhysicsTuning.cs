using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Global physics-engine tuning for the 1/10-scale RC world, applied once at
    /// boot (before any scene builds — everything in this project is created at
    /// runtime, so the defaults set here reach every collider/body).
    ///
    /// Why: PhysX defaults are tuned for ~meter-sized objects. A 33 mm wheel with
    /// the stock 0.01 m contact offset (30% of its radius) picks up phantom
    /// contacts and hovers; small stiff suspensions want more solver iterations.
    /// The diff-drive scene (force-based, no WheelColliders) is unaffected by
    /// these beyond slightly tighter contacts — harmless.
    /// </summary>
    public static class PhysicsTuning
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyAtBoot() => Apply(null);

        /// <summary>
        /// Apply the global PhysX tuning: the shipped numbers, or a scene's own
        /// <see cref="Config.PhysicsSettings"/> asset when it has one.
        ///
        /// <b>null is the whole contract.</b> The boot path passes null and gets
        /// the five literals below, which is why a project with no settings
        /// asset anywhere behaves bit-identically to one that never had this
        /// parameter. A scene descriptor calls it again, later, with its own
        /// asset; running twice is harmless because both calls are plain
        /// assignments to the same five globals.
        ///
        /// The asset's own field initialisers are these same five numbers, so a
        /// freshly created asset is also a no-op — assigning one has to be a
        /// deliberate edit before it means anything.
        /// </summary>
        public static void Apply(Config.PhysicsSettings settings)
        {
            if (settings != null) { settings.ApplySolver(); return; }

            Physics.defaultContactOffset = 0.002f;
            Physics.defaultSolverIterations = 10;
            Physics.defaultSolverVelocityIterations = 2;
            Physics.defaultMaxDepenetrationVelocity = 2f;
            // Cap catch-up simulation after a hitch (frame drops at 400 Hz fixed
            // step would otherwise spiral).
            Time.maximumDeltaTime = 0.05f;
        }
    }
}
