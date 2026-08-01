namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P5 — understeer gradient.</b> How much extra steering the car needs per
    /// g of cornering, above the geometric Ackermann angle.
    ///
    /// <b>This test found a genuine degeneracy in the tyre model, and it is now
    /// fixed.</b> The classic gradient is <c>K = W_f/C_f − W_r/C_r</c>. With a
    /// constant <c>AlphaPeak</c> the brush model gives cornering stiffness
    /// <c>C_α = 2µF_z/AlphaPeak</c> — <i>exactly</i> proportional to vertical load
    /// — and substituting that in, the loads cancel identically:
    /// <c>W_f/(kW_f) − W_r/(kW_r) = 0</c>. For ANY weight distribution whatsoever
    /// the geometric understeer gradient was <b>identically zero</b>: a nose-heavy
    /// car and a tail-heavy car steered the same. That was not a tuning error, it
    /// was the model being unable to represent the effect at all.
    ///
    /// Two real mechanisms replaced it, both derived rather than fitted:
    ///
    /// <b>1. Cornering stiffness now varies with load.</b> Real C_α peaks near the
    /// tyre's RATED load, which follows from the tyre size (235/50R19 → 7603 N);
    /// see <c>TyreModel.AlphaPeakAt</c>. Worth knowing what it says about this car:
    /// a 3679 N corner is only 0.48 of rated, so the tyres sit low on their
    /// stiffness curve where it is still nearly linear, and this term stays small
    /// — about +0.34 deg/g predicted.
    ///
    /// <b>2. Roll steer, from the toe-link hardpoints.</b> As the body rolls, one
    /// wheel bumps and the other droops, and the toe link's arc toes each of them;
    /// the two add into an axle steer. This is the term that actually moved the
    /// number, and it is why a real car's understeer is mostly SUSPENSION rather
    /// than tyre load sensitivity. See <c>SuspensionGeometry.ToeSteerPerMetre</c>.
    ///
    /// Result: 0.451 → 0.853 deg/g, correctly signed. Note the coupling this
    /// creates — roll steer is driven BY roll, so fitting an anti-roll bar to pull
    /// P7 down would pull this number down with it. The two tests are no longer
    /// independent, which is physically correct and worth remembering before
    /// tuning either.
    ///
    /// This matters well beyond the Tiguan: it says the RC cars' handling balance
    /// comes from geometry and load sensitivity rather than from where the mass
    /// sits, so moving the battery in the garage still will not change understeer
    /// the way a player expects — they have no toe links authored.
    /// </summary>
    public sealed class P5UndersteerTest : SteadyCorneringTest
    {
        protected override string TestId => "P5";
        protected override string Title => "Understeer gradient";
        // VERIFIED, and it moved: modern passenger cars sit at ~1–3 deg/g, not the
        // 3–5 this used to claim. The value is procedure-dependent, so the
        // procedure is named — this is a CONSTANT-SPEED RAMP-STEER sweep, one of
        // the three standard methods (the others being constant radius and
        // constant steer), and figures from different methods are not comparable.
        protected override string Expected =>
            "1–3 deg/g (modern passenger cars, constant-speed ramp steer)";

        protected override Verdict? Evaluate()
        {
            if (!Departed) return null;

            if (!TryUndersteerGradient(out float k))
                return new Verdict
                {
                    kind = Kind.Invalid,
                    detail = "not enough samples in the linear region (< 0.4 g)",
                };

            string detail = $"linear-region fit below 0.4 g · peak {PeakLatG:0.000} g · "
                            + $"front load {Ch("veh/front_load_pct"):0.0} % · "
                            + "load-dependent C_α + toe-link roll steer; was 0.451 "
                            + "when the geometric term was identically zero";

            return Verdict.Info(k, "deg/g", detail);
        }
    }
}
