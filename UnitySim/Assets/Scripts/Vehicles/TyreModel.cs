using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Brush-style combined-slip tyre model — the replacement for the PhysX
    /// WheelCollider friction curves (iteration 22).
    ///
    /// Why: the WheelCollider's stylized two-segment slip curve needs an order of
    /// magnitude more slip than rubber to produce a given force. The Opus Vector
    /// calibration measured the consequences directly: 53 % of commanded wheel
    /// force lost to longitudinal slip at a steady 4.5 m/s (real tyres: a few %),
    /// and free-rolling encoders under-reading the ground by 11.6 % (real: 1–4 %).
    /// Those constants were simulator artifacts, not physics.
    ///
    /// How it is used: the WheelCollider is kept for suspension only (raycast,
    /// spring/damper, ground hit / normal load). Its friction stiffness is set to
    /// 0 so PhysX contributes no tyre force, CarVehicle integrates each wheel's
    /// spin ω itself (J·ω̇ = τ_drive − τ_brake − Fx·r − τ_roll), and the forces
    /// from <see cref="Forces"/> are applied at the contact point.
    ///
    /// The model: slip ratio κ = (ω·r − vx)/max(|vx|, VLow) and lateral slip
    /// tan α ≈ −vy/max(|vx|, VLow), normalized by their peak values and combined
    /// through a friction ellipse; a magic-formula-shaped curve rises linearly,
    /// peaks at the normalized slip 1, and eases to <see cref="SlideRatio"/> of
    /// peak in a deep slide. Peak grip µ comes from the surface × per-wheel
    /// gripMult × load sensitivity — asphalt (frictionMult 1.15) lands at a
    /// physical µ ≈ 1.1.
    ///
    /// Numerical safety (the classic failure of custom slip models at low speed):
    /// the returned forces are impulse-clamped so no single 2.5 ms step can push
    /// the slip velocity through zero — below VLow the model degenerates into an
    /// exactly-critically-damped closure instead of a chattering spring. Static
    /// hold is split between two places: the ω side lives in the integrator
    /// (MoveTowards zero), and the FORCE side lives in the stiction branch of
    /// <see cref="Forces"/> — a brake-locked wheel is clamped through the body's
    /// compliance only, because its spin degree of freedom is rigid.
    /// </summary>
    public static class TyreModel
    {
        /// <summary>
        /// Dev A/B switch (the PartMeshLibrary.Enabled pattern): false restores
        /// the legacy WheelCollider friction path wholesale. Build-time — cars
        /// built while false keep PhysX friction until rebuilt.
        /// </summary>
        public static bool Enabled = true;

        /// <summary>Slip ratio at peak longitudinal force. Rubber on tarmac
        /// peaks around 8–15 % slip; RC foam/rubber sits mid-range.</summary>
        public const float KappaPeak = 0.10f;

        /// <summary>Lateral slip (tan α) at peak cornering force ≈ 7°, at the tyre's
        /// RATED load. See <see cref="AlphaPeakAt"/> for why the qualifier matters.</summary>
        public const float AlphaPeak = 0.12f;

        /// <summary>
        /// Peak slip angle at a given load — the fix for a genuine degeneracy.
        ///
        /// With a constant <see cref="AlphaPeak"/>, cornering stiffness
        /// C_α = 2µF_z/α_peak is EXACTLY proportional to vertical load. Substitute
        /// that into the classic understeer gradient K = W_f/C_f − W_r/C_r and the
        /// loads cancel identically: W_f/(kW_f) − W_r/(kW_r) = 0. For any weight
        /// distribution whatsoever the geometric understeer of this model was zero,
        /// so a nose-heavy car and a tail-heavy car steered the same. That is not a
        /// tuning error, it is the model being unable to represent the effect.
        ///
        /// Real cornering stiffness rises with load, peaks near the tyre's RATED
        /// load, and falls away past it — the standard shape being
        /// C_α ∝ sin(2·atan(F_z/F_rated)). Inverting C_α = 2µF_z/α_peak for that
        /// shape gives the expression below, which is exactly AlphaPeak at
        /// F_z = F_rated and rises either side. Because the rated load follows from
        /// the tyre SIZE, this is derived from the vehicle rather than fitted.
        ///
        /// Peak FORCE is untouched — µ·F_z·Shape(1) does not involve α_peak. Only
        /// the slip angle at which the peak arrives moves, which is why this changes
        /// the understeer gradient without changing the skidpad number.
        ///
        /// <paramref name="ratedLoadN"/> ≤ 0 returns <see cref="AlphaPeak"/>
        /// verbatim — the sentinel every pre-existing design takes.
        /// </summary>
        public static float AlphaPeakAt(float fz, float ratedLoadN)
        {
            if (ratedLoadN <= 0f || fz <= 0f) return AlphaPeak;
            float x = fz / ratedLoadN;
            float shape = Mathf.Sin(2f * Mathf.Atan(x));
            if (shape < 1e-4f) return AlphaPeak;
            return AlphaPeak * x / shape;
        }

        /// <summary>Force fraction remaining in a deep slide (full lock / donut).</summary>
        public const float SlideRatio = 0.85f;

        /// <summary>Slip-denominator floor (m/s). Below this speed slip ratios
        /// become slip velocities over VLow — a damper region, not a spring.</summary>
        public const float VLow = 0.5f;

        /// <summary>
        /// Combined-slip tyre forces for one wheel, impulse-clamped for explicit
        /// integration at the physics step.
        /// </summary>
        /// <param name="vx">Contact-patch velocity along the wheel's forward (m/s).</param>
        /// <param name="vy">Contact-patch velocity along the wheel's right (m/s).</param>
        /// <param name="omega">Wheel spin (rad/s, forward-positive).</param>
        /// <param name="r">Rolling radius (m).</param>
        /// <param name="fz">Normal load (N, ≥ 0).</param>
        /// <param name="mu">Peak friction coefficient (surface × wheel × load).</param>
        /// <param name="latMuScale">Lateral grip scale (the Tune "Grip (side)" knob; 1 = neutral).</param>
        /// <param name="dt">Physics step (s).</param>
        /// <param name="invMassEff">1 / (body mass share per wheel) (1/kg).</param>
        /// <param name="rSqOverJ">r² / wheel spin inertia (1/kg) — the wheel-side compliance.</param>
        /// <param name="ratedLoadN">Tyre rated load (N); 0 = load-independent AlphaPeak.</param>
        /// <param name="driveTorque">Motor torque on this wheel this step (N·m).</param>
        /// <param name="resistTorque">Brake + rolling-resistance torque available to
        /// hold the wheel this step (N·m) — the same number the spin integrator
        /// feeds MoveTowards, so the stiction test and the integrator agree.</param>
        /// <param name="fx">Longitudinal force on the body at the patch (N).</param>
        /// <param name="fy">Lateral force on the body at the patch (N).</param>
        public static void Forces(
            float vx, float vy, float omega, float r, float fz,
            float mu, float latMuScale, float dt,
            float invMassEff, float rSqOverJ,
            float ratedLoadN, float driveTorque, float resistTorque,
            out float fx, out float fy)
        {
            fx = 0f; fy = 0f;
            if (fz <= 0f || mu <= 0f) return;

            float denom = Mathf.Max(Mathf.Abs(vx), VLow);
            float vsx = omega * r - vx;   // slip velocity, + = wheel outrunning ground
            float vsy = -vy;              // patch resists its own sideways motion

            float sx = (vsx / denom) / KappaPeak;
            float sy = (vsy / denom) / AlphaPeakAt(fz, ratedLoadN);
            float s = Mathf.Sqrt(sx * sx + sy * sy);
            if (s < 1e-6f) return;

            float f = mu * fz * Shape(s);
            float fx0 = f * (sx / s);
            float fy0 = f * (sy / s) * Mathf.Max(0.1f, latMuScale);

            // Impulse clamps: a force larger than this would reverse the slip
            // velocity it acts on within one explicit step (through both the body
            // AND the wheel-spin degree of freedom for Fx). Signs already match
            // the slip velocities, so a symmetric clamp is enough.
            float fyMax = Mathf.Abs(vsy) / Mathf.Max(1e-9f, dt * invMassEff);
            fy = Mathf.Clamp(fy0, -fyMax, fyMax);

            // Stiction branch — the wheel-side compliance is CONDITIONAL. The
            // rSqOverJ term in the clamp models the tyre force spinning the
            // wheel up and killing the slip through the wheel's own degree of
            // freedom. A brake-held wheel has no such freedom: the brake absorbs
            // the tyre torque and ω stays at zero, so charging for that
            // compliance caps the force far below what the rubber delivers.
            // Measured on the P6b park-hold: rSqOverJ 27× invMassEff collapsed
            // a 2330 N holding force to 759 N and the car creeped downhill at a
            // clamp-set equilibrium forever.
            //
            // Classic stiction handling, derived from the integrator itself with
            // no tuned thresholds: solve assuming the wheel stays locked (body-
            // only clamp — for a locked wheel vsx = −vx, so this force can at
            // most bring the BODY to rest in one step, never overshoot), then
            // check the brake's torque budget. It holds iff the brake can stop
            // the current spin within the step (J·|ω|/dt) AND resist the net
            // torque the trial force implies. If the budget fails, the wheel
            // really will spin, and the original two-DOF clamp is correct.
            float fxLockedMax = Mathf.Abs(vsx) / Mathf.Max(1e-9f, dt * invMassEff);
            float fxLocked = Mathf.Clamp(fx0, -fxLockedMax, fxLockedMax);
            float spinInertiaJ = r * r / Mathf.Max(1e-9f, rSqOverJ);
            bool staysLocked = resistTorque >=
                Mathf.Abs(driveTorque - fxLocked * r) + spinInertiaJ * Mathf.Abs(omega) / dt;
            if (staysLocked)
            {
                fx = fxLocked;
            }
            else
            {
                float fxMax = Mathf.Abs(vsx) / Mathf.Max(1e-9f, dt * (invMassEff + rSqOverJ));
                fx = Mathf.Clamp(fx0, -fxMax, fxMax);
            }
        }

        /// <summary>Longitudinal slip ratio (for TC/ABS logic and telemetry).</summary>
        public static float SlipRatio(float vx, float omega, float r)
        {
            float denom = Mathf.Max(Mathf.Abs(vx), VLow);
            return (omega * r - vx) / denom;
        }

        /// <summary>Normalized force curve: parabolic rise to the peak at s = 1,
        /// then a linear ease down to SlideRatio by s = 3.</summary>
        private static float Shape(float s)
        {
            if (s <= 1f) return s * (2f - s);
            return 1f - (1f - SlideRatio) * Mathf.Min(1f, (s - 1f) * 0.5f);
        }
    }
}
