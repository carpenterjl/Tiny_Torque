namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P4 — skidpad: peak steady-state lateral grip.</b>
    ///
    /// <b>INFO, never gating — and the ⚠ on its reference is the point.</b> The
    /// "0.82 g for a compact SUV" figure is <b>UNSOURCED</b>. Every other reference
    /// in this suite was checked against a primary road test; a skidpad number for
    /// this car could not be found, so it stays a declared estimate and is marked
    /// as one rather than quoted as if it were measured.
    ///
    /// <b>The old note here claimed one µ could not serve both this and P6a. That
    /// was wrong and is worth recording as wrong.</b> The model was delivering
    /// 0.833 g lateral against 0.788 g longitudinal — nearly equal, exactly as an
    /// isotropic µ predicts — so there was no anisotropy to explain. P6a's
    /// shortfall was brake balance, and fixing it took the tyres to their real
    /// 1.01 g peak WITHOUT touching µ, leaving this number where it was. The
    /// pair never disagreed; the diagnosis did.
    ///
    /// This value has since been stable at 0.831–0.833 g across the roll centre,
    /// roll steer and load-dependent-C_α changes, which is the expected result:
    /// none of them alters peak force (<c>µ·F_z·Shape(1)</c>), only the slip angle
    /// at which it arrives and how the body sits while it does.
    ///
    /// The flat frictionless plane IS the skidpad — no markings needed, since the
    /// car is not following a painted circle but holding a steady state.
    /// </summary>
    public sealed class P4SkidpadTest : SteadyCorneringTest
    {
        protected override string TestId => "P4";
        protected override string Title => "Skidpad — peak lateral grip";
        protected override string Expected => "≈ 0.83 g (⚠ reference 0.82 g is UNSOURCED)";

        protected override Verdict? Evaluate()
        {
            if (!Departed) return null;

            float radius = cruiseSpeed * cruiseSpeed / (PeakLatG * 9.81f);
            string detail = $"at {cruiseSpeed:0.0} m/s · implied radius {radius:0.0} m · "
                            + $"steer {Car.CurrentSteerAngle:0.0}° at the limit · "
                            + "⚠ reference unsourced — see the class note";

            return Verdict.Info(PeakLatG, "g", detail);
        }
    }
}
