using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The numbers behind the four driving assists, in one place.
    ///
    /// Every value here was lifted verbatim out of <see cref="CarVehicle"/>'s
    /// FixedUpdate — this type changed no behaviour when it was introduced, and
    /// that was the point. The assists had grown a dozen bare literals buried in
    /// the physics step, where retuning them meant editing force code and hoping
    /// nothing else in the expression moved. With them named and gathered, a
    /// tuning pass is a data change.
    ///
    /// Each assist reads 0 (pure physics, and the default) to 1 (maximum), and
    /// <c>assistsActive</c> forces the whole struct to zero in Autonomous mode —
    /// so nothing here is ever on the firmware path.
    /// </summary>
    public static class AssistTuning
    {
        // ---- steering assist ----
        /// <summary>Reference speed for the lock limiter (m/s): available lock is
        /// scaled by ref/speed above it, so a lower number means a tighter limit
        /// at any given speed.</summary>
        public const float SteerLimitRefSpeed = 4f;
        /// <summary>Floor on the speed used by the limiter, so it cannot divide
        /// by ~0 and hand back infinite lock at a standstill.</summary>
        public const float SteerLimitMinSpeed = 0.5f;
        /// <summary>Longitudinal speed below which countersteer stays out of it —
        /// slip angle is meaningless when the car is barely moving.</summary>
        public const float CounterSteerMinLongSpeed = 1f;
        /// <summary>Slip angle (rad) → steering correction.</summary>
        public const float CounterSteerGain = 0.5f;
        /// <summary>Cap on that correction, so the assist nudges rather than
        /// takes the wheel off you.</summary>
        public const float CounterSteerClamp = 0.35f;

        // ---- stability (ESC) ----
        /// <summary>Yaw-rate error → corrective torque.</summary>
        public const float StabilityGain = 0.08f;
        /// <summary>Hard cap on that torque (N·m). This is the real ceiling on
        /// the whole assist: against roughly 2 N·m of tyre resisting moment,
        /// 0.30 is barely a nudge.</summary>
        public const float StabilityTorqueClamp = 0.3f;

        // ---- traction control ----
        /// <summary>Slip ratio at which drive starts being cut.</summary>
        public const float TractionOnset = 0.25f;
        /// <summary>Slip range over which the cut reaches full authority.</summary>
        public const float TractionBand = 0.35f;

        // ---- ABS ----
        /// <summary>Negative slip ratio at which the brake starts being released.</summary>
        public const float AbsOnset = 0.3f;
        /// <summary>Slip range over which the release reaches full authority.</summary>
        public const float AbsBand = 0.4f;

        // ================= the top end =================
        //
        // Governing rule for everything below:
        //
        //     Every one of these is the IDENTITY FUNCTION at and below the
        //     original preset anchor points (steer .80 / stability .70 /
        //     traction .90 / abs .90), and only gains authority above them.
        //
        // Mathf.InverseLerp clamps, so the anchors are enforced by the shape of
        // the expression rather than by a check someone can forget: below one,
        // t is 0 and the Lerp returns exactly the value that shipped.
        //
        // (These anchors were originally the Arcade handling floor. Arcade now
        // pins every channel to 1.0 — see ArcadeConfig.HandlingAssists — so in
        // arcade the ramps run at their top end, deliberately, plus the
        // arcadeStabilityMult boost on top. The anchors still matter for SIM
        // sessions, where they keep the Standard preset at the shipped feel.)
        //
        // The intent at Full is a well-set-up touring car, NOT a car on rails.
        // The tyre model still decides whether you make the corner, and top
        // speed is untouched.

        /// <summary>
        /// Stability's torque cap, ramping 0.30 → 0.75 N·m over [0.70, 1.0].
        ///
        /// This clamp, not the gain, is the real ceiling on the ESC: at stability
        /// 1.0 the old 0.30 N·m had to work against roughly 2 N·m of tyre
        /// resisting moment (for scale, an arcade spin-out applies 1.2), so the
        /// assist could barely be felt however high the slider went.
        /// </summary>
        public static float StabilityClamp(float stability) =>
            Mathf.Lerp(StabilityTorqueClamp, 0.75f, Mathf.InverseLerp(0.70f, 1f, stability));

        /// <summary>Lock-limiter reference speed, 4 → 2.5 m/s over [0.80, 1.0]:
        /// a lower reference means less lock available at speed, which is what
        /// stops a keyboard's instant full-lock step from spinning the car.</summary>
        public static float SteerLimitRef(float steer) =>
            Mathf.Lerp(SteerLimitRefSpeed, 2.5f, Mathf.InverseLerp(0.80f, 1f, steer));

        /// <summary>Traction-control onset, 0.25 → 0.12 slip over [0.90, 1.0] —
        /// it intervenes earlier rather than harder, so the cut still feels like
        /// grip rather than like the throttle being taken away.</summary>
        public static float TractionOnsetFor(float traction) =>
            Mathf.Lerp(TractionOnset, 0.12f, Mathf.InverseLerp(0.90f, 1f, traction));

        /// <summary>ABS onset, 0.30 → 0.15 slip over [0.90, 1.0].</summary>
        public static float AbsOnsetFor(float abs) =>
            Mathf.Lerp(AbsOnset, 0.15f, Mathf.InverseLerp(0.90f, 1f, abs));
    }
}
