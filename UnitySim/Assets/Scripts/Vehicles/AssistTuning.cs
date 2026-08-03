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
        /// <summary>
        /// The asset a scene has chosen to tune these numbers with, or null —
        /// and null is what every value below falls back to its shipped literal
        /// for. Installed by <c>DrivingSceneDescriptor</c>; cleared by setting
        /// it back to null, which restores the defaults in one assignment
        /// because nothing here caches or copies them.
        ///
        /// Static, and so it outlives a scene load within a session: whoever
        /// installs one owns clearing it. That is the same contract
        /// <see cref="Core.SessionConfig"/> already has, for the same reason —
        /// there is exactly one physics world and these are its assists.
        /// </summary>
        public static AssistTuningOverride Override;

        // Each accessor below is "the asset's field, or the literal that
        // shipped". The literal lives in a private const beside its accessor
        // rather than in the asset alone, so that a project with no asset
        // anywhere is not merely equivalent to the old code — it runs the same
        // numbers from the same file. The asset's own field initialisers are
        // these same values, so a freshly created asset is also a no-op.

        // ---- steering assist ----
        private const float SteerLimitRefSpeedDefault = 4f;
        private const float SteerLimitMinSpeedDefault = 0.5f;
        private const float CounterSteerMinLongSpeedDefault = 1f;
        private const float CounterSteerGainDefault = 0.5f;
        private const float CounterSteerClampDefault = 0.35f;

        /// <summary>Reference speed for the lock limiter (m/s): available lock is
        /// scaled by ref/speed above it, so a lower number means a tighter limit
        /// at any given speed.</summary>
        public static float SteerLimitRefSpeed =>
            Override != null ? Override.steerLimitRefSpeed : SteerLimitRefSpeedDefault;
        /// <summary>Floor on the speed used by the limiter, so it cannot divide
        /// by ~0 and hand back infinite lock at a standstill.</summary>
        public static float SteerLimitMinSpeed =>
            Override != null ? Override.steerLimitMinSpeed : SteerLimitMinSpeedDefault;
        /// <summary>Longitudinal speed below which countersteer stays out of it —
        /// slip angle is meaningless when the car is barely moving.</summary>
        public static float CounterSteerMinLongSpeed =>
            Override != null ? Override.counterSteerMinLongSpeed : CounterSteerMinLongSpeedDefault;
        /// <summary>Slip angle (rad) → steering correction.</summary>
        public static float CounterSteerGain =>
            Override != null ? Override.counterSteerGain : CounterSteerGainDefault;
        /// <summary>Cap on that correction, so the assist nudges rather than
        /// takes the wheel off you.</summary>
        public static float CounterSteerClamp =>
            Override != null ? Override.counterSteerClamp : CounterSteerClampDefault;

        // ---- stability (ESC) ----
        private const float StabilityGainDefault = 0.08f;
        private const float StabilityTorqueClampDefault = 0.3f;

        /// <summary>Yaw-rate error → corrective torque.</summary>
        public static float StabilityGain =>
            Override != null ? Override.stabilityGain : StabilityGainDefault;
        /// <summary>Hard cap on that torque (N·m). This is the real ceiling on
        /// the whole assist: against roughly 2 N·m of tyre resisting moment,
        /// 0.30 is barely a nudge.</summary>
        public static float StabilityTorqueClamp =>
            Override != null ? Override.stabilityTorqueClamp : StabilityTorqueClampDefault;

        // ---- traction control ----
        private const float TractionOnsetDefault = 0.25f;
        private const float TractionBandDefault = 0.35f;

        /// <summary>Slip ratio at which drive starts being cut.</summary>
        public static float TractionOnset =>
            Override != null ? Override.tractionOnset : TractionOnsetDefault;
        /// <summary>Slip range over which the cut reaches full authority.</summary>
        public static float TractionBand =>
            Override != null ? Override.tractionBand : TractionBandDefault;

        // ---- ABS ----
        private const float AbsOnsetDefault = 0.3f;
        private const float AbsBandDefault = 0.4f;

        /// <summary>Negative slip ratio at which the brake starts being released.</summary>
        public static float AbsOnset =>
            Override != null ? Override.absOnset : AbsOnsetDefault;
        /// <summary>Slip range over which the release reaches full authority.</summary>
        public static float AbsBand =>
            Override != null ? Override.absBand : AbsBandDefault;

        // ---- launch control ----
        // A voltage-side governor for standing starts (see
        // CarVehicle.UpdateLaunchControl). TC composes with it: TC is per-wheel
        // and stateless, this is global and integrating.

        private const float LaunchSlipTargetDefault = 0.12f;
        private const float LaunchGainDefault = 4f;
        private const float LaunchFloorDefault = 0.30f;
        private const float LaunchEngageSpeedDefault = 3.0f;
        private const float LaunchReleaseSpeedDefault = 4.0f;
        private const float LaunchReleaseRateDefault = 2f;

        /// <summary>Slip ratio the governor holds the worst powered wheel at:
        /// 1.2 × TyreModel.KappaPeak (0.10), i.e. just PAST the force peak — a
        /// governed launch leaves at maximum force, and converging from the
        /// spin side keeps the integrator from hunting across the peak.</summary>
        public static float LaunchSlipTarget =>
            Override != null ? Override.launchSlipTarget : LaunchSlipTargetDefault;
        /// <summary>Integrator gain: voltage-scale units per second per unit of
        /// excess slip. At a floored-launch excess of ~0.5 this cuts ~2/s, so
        /// the governor bites in a couple of tenths.</summary>
        public static float LaunchGain =>
            Override != null ? Override.launchGain : LaunchGainDefault;
        /// <summary>The governor never cuts below this — a launch should feel
        /// managed, not confiscated.</summary>
        public static float LaunchFloor =>
            Override != null ? Override.launchFloor : LaunchFloorDefault;
        /// <summary>Below this speed (m/s) the governor is armed.</summary>
        public static float LaunchEngageSpeed =>
            Override != null ? Override.launchEngageSpeed : LaunchEngageSpeedDefault;
        /// <summary>Above this speed the scale is handed back at 4× the release
        /// rate — the launch is over, the motor belongs to the driver.</summary>
        public static float LaunchReleaseSpeed =>
            Override != null ? Override.launchReleaseSpeed : LaunchReleaseSpeedDefault;
        /// <summary>Recovery rate (scale units per second) while not cutting.</summary>
        public static float LaunchReleaseRate =>
            Override != null ? Override.launchReleaseRate : LaunchReleaseRateDefault;

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
