using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>
    /// Section lift and drag for one strip of a lifting surface.
    ///
    /// <b>The stall angle is derived, not authored.</b> A section stalls when its
    /// lift curve reaches the most lift it can carry, so α_stall = C_Lmax / a
    /// falls out of the two things that are actually measurable. Authoring both
    /// C_Lmax and α_stall would let them disagree, and the model would then be
    /// carrying a contradiction nobody could see.
    ///
    /// Below stall this is thin-airfoil theory corrected for finite span:
    /// <code>
    ///   a  = a₀ / (1 + a₀/(π·AR·e)),   a₀ = 2π per radian
    /// </code>
    /// a₀ = 2π is exact for a thin section in incompressible flow — a universal
    /// result, not a fitted number. Above stall it becomes a flat plate carrying
    /// a pure normal force, C_N = 2·sin α (Viterna):
    /// <code>
    ///   C_L = 2·sin α·cos α = sin 2α        C_D = 2·sin²α
    /// </code>
    /// which gives C_D = 2.0 at 90°, against ≈1.98 measured for a 2D flat plate.
    /// The two branches are blended with a smoothstep so the force law is C¹:
    /// a kink in dC_L/dα is a step in the force derivative, and the phugoid is a
    /// low-damped oscillator that will happily ring on it.
    ///
    /// <b>What this does NOT include:</b> induced drag (a property of the whole
    /// surface's span loading, applied once by <see cref="PanelAero"/> rather than
    /// per strip — see the note there), and parasitic drag from the fuselage, gear
    /// and interference, which are not lifting-surface effects and are carried by
    /// the airframe. <see cref="profileCd"/> here is the SECTION profile drag only.
    ///
    /// Declared omissions: no Reynolds dependence (Re spans 1.6–3.5×10⁵ across
    /// this envelope, over which section c_d0 moves ~30 % — an RC-scale effect a
    /// full-scale model could ignore and this one cannot), and no stall hysteresis
    /// (the laminar separation bubble at this Re stalls near 13° and reattaches
    /// near 9°, which is why models sometimes do not recover the way you expect).
    /// </summary>
    [System.Serializable]
    public struct Airfoil
    {
        /// <summary>Maximum section lift coefficient. ⚠ empirical: 1.10 is the
        /// flat-bottom / semi-symmetric band at Re ≈ 2×10⁵. Sourcing this against
        /// UIUC low-Reynolds section data is the graduation path.</summary>
        public float clMax;

        /// <summary>Section profile drag coefficient at low lift. ⚠ empirical:
        /// 0.012 for a model-scale section at Re ≈ 2×10⁵.</summary>
        public float profileCd;

        /// <summary>Zero-lift angle (rad). 0 for a symmetric section, which is what
        /// the model wing uses; a cambered section shifts every angle here by α₀
        /// and changes nothing else about the model.</summary>
        public float alphaZeroLift;

        /// <summary>Oswald span efficiency. ⚠ empirical: 0.85 is the usual band for
        /// a straight rectangular wing. It disappears entirely if the model ever
        /// grows a lifting-line solve, which computes span loading directly.</summary>
        public float oswald;

        /// <summary>Angular width of the stall blend (rad). ⚠ empirical — a real
        /// stall break is not a step, and at model Reynolds numbers it is fairly
        /// sharp; 8° is the shape used here.</summary>
        public float stallBlend;

        public static Airfoil Default() => new Airfoil
        {
            clMax = 1.10f,
            profileCd = 0.012f,
            alphaZeroLift = 0f,
            oswald = 0.85f,
            stallBlend = 8f * Mathf.Deg2Rad,
        };

        /// <summary>Thin-airfoil lift slope (per rad), exact for a thin section.</summary>
        public const float SlopeInfinite = 2f * Mathf.PI;

        /// <summary>
        /// Finite-wing lift slope (per rad) for a surface of this aspect ratio.
        /// The 3D slope is always lower than 2π because trailing vorticity induces
        /// a downwash that reduces the angle the section actually sees.
        /// </summary>
        public static float LiftSlope(float aspectRatio, float oswald)
        {
            if (aspectRatio <= 0f) return SlopeInfinite;
            float e = oswald <= 0f ? 1f : oswald;
            return SlopeInfinite / (1f + SlopeInfinite / (Mathf.PI * aspectRatio * e));
        }

        /// <summary>Positive stall angle (rad) implied by <see cref="clMax"/>.</summary>
        public float StallAlpha(float liftSlope) =>
            alphaZeroLift + clMax / Mathf.Max(0.1f, liftSlope);

        /// <summary>
        /// How far past stall this angle of attack is, as a fraction of the stall
        /// angle. Negative below stall, zero at it, positive beyond.
        ///
        /// <b>Measured from the zero-lift angle, not from zero.</b> A cambered
        /// section is not symmetric about α = 0: this one stalls at +11.9° and not
        /// again until −18.9°, because the camber has already spent 3.5° of the
        /// upper-surface budget. Comparing |α| against the positive stall angle —
        /// the obvious thing to write — would report a stall seven degrees early in
        /// any pushover, and the stall warning would cry wolf every time the nose
        /// went down.
        /// </summary>
        public float StallExcess(float alpha, float liftSlope)
        {
            float band = clMax / Mathf.Max(0.1f, liftSlope);
            if (band < 1e-4f) return 0f;
            return (Mathf.Abs(alpha - alphaZeroLift) - band) / band;
        }

        /// <summary>
        /// Section coefficients at angle of attack <paramref name="alpha"/> (rad),
        /// for a surface whose finite-wing slope is <paramref name="liftSlope"/>.
        /// </summary>
        public void Coefficients(float alpha, float liftSlope, out float cl, out float cd)
        {
            float a = alpha - alphaZeroLift;
            float mag = Mathf.Abs(a);
            float stall = clMax / Mathf.Max(0.1f, liftSlope);

            // Attached branch: linear, but held at clMax rather than growing past
            // it — the plateau IS the physical statement that the section cannot
            // carry more, and it keeps the blend monotone.
            float clAttached = Mathf.Clamp(liftSlope * a, -clMax, clMax);

            // Fully separated branch: a flat plate with a pure normal force.
            float clPlate = Mathf.Sin(2f * a);
            float sinA = Mathf.Sin(a);
            float cdPlate = 2f * sinA * sinA;

            float t = Smooth01(Mathf.InverseLerp(stall, stall + Mathf.Max(1e-3f, stallBlend), mag));

            cl = Mathf.Lerp(clAttached, clPlate, t);
            cd = Mathf.Lerp(profileCd, cdPlate, t);
        }

        /// <summary>True once the section is past its attached-flow limit.</summary>
        public bool IsStalled(float alpha, float liftSlope) =>
            StallExcess(alpha, liftSlope) > 0f;

        /// <summary>Smoothstep — C¹ at both ends, which is the whole point of it.</summary>
        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }
    }
}
