using UnityEngine;

namespace AIHWSim.Core.Config
{
    /// <summary>
    /// The solver and the clock, as an asset.
    ///
    /// Two things that are currently unreachable without a recompile end up
    /// here. <see cref="PhysicsTuning"/> is a boot-time static that overrides
    /// the project's own DynamicsManager settings with numbers chosen for a
    /// 1/10-scale world; and the physics/control rates are public fields
    /// duplicated across six bootstraps, each with its own default (400 in the
    /// track scene, 500 in the diff-drive one, 400 in the tests).
    ///
    /// <b>Every default below is the literal it replaced</b>, so an asset
    /// created and left alone configures the world exactly as the boot-time
    /// static does. Assign nothing and <see cref="PhysicsTuning.Apply()"/> runs
    /// with no argument and no behaviour change at all — that is what makes the
    /// regression gates meaningful rather than decorative.
    ///
    /// <b>Why the contact offset is 2 mm.</b> PhysX's 0.01 m default is 30 % of
    /// a 33 mm RC wheel's radius; at that ratio a wheel picks up phantom
    /// contacts and hovers. The extra solver iterations are for the same reason
    /// — small, stiff suspensions. A full-scale car in this project (the
    /// reference Tiguan) pairs this with its own per-collider offset rather than
    /// wanting a different global.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Physics Settings", fileName = "PhysicsSettings")]
    public sealed class PhysicsSettings : ScriptableObject
    {
        [Header("Rates")]
        [Tooltip("Fixed physics steps per second. Small stiff RC suspension wants steps " +
                 "of 2.5 ms or shorter, which is where 400 comes from. This is written " +
                 "to Time.fixedDeltaTime, which is GLOBAL — see PhysicsRateAuthority.")]
        [Min(1)] public int physicsRateHz = 400;

        [Tooltip("Controller ticks per second. Snapped down to an integer division of " +
                 "the physics rate, so 100 against 400 is one control tick every four " +
                 "physics steps.")]
        [Min(1)] public int controlRateHz = 100;

        [Header("Solver (PhysX globals)")]
        [Tooltip("Distance at which contacts start being generated. 2 mm rather than " +
                 "PhysX's 10 mm because 10 mm is a third of an RC wheel's radius.")]
        [Min(0.0001f)] public float defaultContactOffset = 0.002f;

        [Tooltip("Position solver iterations per step.")]
        [Min(1)] public int defaultSolverIterations = 10;

        [Tooltip("Velocity solver iterations per step.")]
        [Min(1)] public int defaultSolverVelocityIterations = 2;

        [Tooltip("Cap on the speed a body may be pushed out of an overlap. The stock 10 " +
                 "m/s launches a 2.5 kg car across the map on a bad frame.")]
        [Min(0f)] public float defaultMaxDepenetrationVelocity = 2f;

        [Header("Frame clock")]
        [Tooltip("Cap on catch-up simulation after a hitch. Without it, a dropped frame " +
                 "at a 400 Hz fixed step asks for hundreds of steps at once and spirals.")]
        [Min(0.001f)] public float maximumDeltaTime = 0.05f;

        /// <summary>
        /// Push the solver half of this asset at Unity. The rates are not
        /// applied here: they belong to a <c>SimulationRunner</c>, which owns
        /// <c>Time.fixedDeltaTime</c> and derives the control decimation from
        /// them, and writing the step from two places is exactly the confusion
        /// this asset exists to end.
        /// </summary>
        public void ApplySolver()
        {
            Physics.defaultContactOffset = defaultContactOffset;
            Physics.defaultSolverIterations = defaultSolverIterations;
            Physics.defaultSolverVelocityIterations = defaultSolverVelocityIterations;
            Physics.defaultMaxDepenetrationVelocity = defaultMaxDepenetrationVelocity;
            Time.maximumDeltaTime = maximumDeltaTime;
        }
    }
}
