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
    /// exactly-critically-damped closure instead of a chattering spring. Brake
    /// static-hold lives in the ω integrator (MoveTowards zero), not here.
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

        /// <summary>Lateral slip (tan α) at peak cornering force ≈ 7°.</summary>
        public const float AlphaPeak = 0.12f;

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
        /// <param name="fx">Longitudinal force on the body at the patch (N).</param>
        /// <param name="fy">Lateral force on the body at the patch (N).</param>
        public static void Forces(
            float vx, float vy, float omega, float r, float fz,
            float mu, float latMuScale, float dt,
            float invMassEff, float rSqOverJ,
            out float fx, out float fy)
        {
            fx = 0f; fy = 0f;
            if (fz <= 0f || mu <= 0f) return;

            float denom = Mathf.Max(Mathf.Abs(vx), VLow);
            float vsx = omega * r - vx;   // slip velocity, + = wheel outrunning ground
            float vsy = -vy;              // patch resists its own sideways motion

            float sx = (vsx / denom) / KappaPeak;
            float sy = (vsy / denom) / AlphaPeak;
            float s = Mathf.Sqrt(sx * sx + sy * sy);
            if (s < 1e-6f) return;

            float f = mu * fz * Shape(s);
            float fx0 = f * (sx / s);
            float fy0 = f * (sy / s) * Mathf.Max(0.1f, latMuScale);

            // Impulse clamps: a force larger than this would reverse the slip
            // velocity it acts on within one explicit step (through both the body
            // AND the wheel-spin degree of freedom for Fx). Signs already match
            // the slip velocities, so a symmetric clamp is enough.
            float fxMax = Mathf.Abs(vsx) / Mathf.Max(1e-9f, dt * (invMassEff + rSqOverJ));
            float fyMax = Mathf.Abs(vsy) / Mathf.Max(1e-9f, dt * invMassEff);
            fx = Mathf.Clamp(fx0, -fxMax, fxMax);
            fy = Mathf.Clamp(fy0, -fyMax, fyMax);
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
