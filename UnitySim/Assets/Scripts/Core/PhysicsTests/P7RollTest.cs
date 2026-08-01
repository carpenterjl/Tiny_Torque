using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P7 — roll gradient: body roll per g of cornering.</b>
    ///
    /// <b>The roll centre exists now, and one of the two cancelling errors is
    /// gone.</b> This test used to report a number that looked right for the wrong
    /// reason: tyre forces were applied at <c>hit.point</c> — ground level — so the
    /// model had <b>no roll centre at all</b> and the roll moment arm was the full
    /// CoM height rather than the height above the roll axis. It rolled roughly the
    /// right amount only because it also carried no anti-roll bar, and the two
    /// omissions happened to cancel.
    ///
    /// The lateral force now reaches the sprung mass at the roll centre, which is
    /// DERIVED from the authored suspension hardpoints rather than authored
    /// directly: 87 mm front, 109 mm rear, giving a roll axis 95 mm up at the CoM
    /// against a 620 mm CoM height — a 15 % shorter moment arm. Predicted
    /// 7.53 deg/g from that alone; measured 7.49, which is the kind of agreement
    /// that says the construction is right rather than merely plausible.
    /// (8.90 → 7.49 deg/g, i.e. 5.99° at 0.8 g against a real 5–6°.)
    ///
    /// <b>The anti-roll bar is still missing, deliberately.</b> Its old
    /// justification — cancelling the absent roll centre — is void, and a real
    /// Tiguan certainly has one, but its rate is not published and authoring a
    /// value that lands this test on 6.3° would be fitting the answer. So
    /// <c>antiRoll</c> stays 0 and this row is a <b>lower bound on roll
    /// stiffness</b>, i.e. an upper bound on roll angle: the residual above the
    /// real band is the bar. Note also that an ARB would reduce roll steer and
    /// therefore pull P5 down with it — the two are coupled now, correctly.
    /// </summary>
    public sealed class P7RollTest : SteadyCorneringTest
    {
        protected override string TestId => "P7";
        protected override string Title => "Roll gradient";
        // The 5–6° band is a general compact-SUV expectation, NOT a road test of
        // this car — no published Tiguan roll gradient was found, same as P4's
        // skidpad figure. Marked rather than quoted as measured.
        protected override string Expected => "≈ 6.3° at 0.8 g (⚠ 5–6° band is generic, unsourced)";

        protected override Verdict? Evaluate()
        {
            if (!Departed) return null;

            if (!TryRollGradient(out float degPerG))
                return new Verdict
                {
                    kind = Kind.Invalid,
                    detail = "not enough samples in the linear region (< 0.4 g)",
                };

            float at08 = degPerG * 0.8f;
            string detail = $"{degPerG:0.00} deg/g · peak {PeakLatG:0.000} g · "
                            + "roll centre derived from hardpoints (87 mm front / "
                            + "109 mm rear); was 8.90 deg/g at ground level · "
                            + "no anti-roll bar, so this is an UPPER bound on roll";

            return Verdict.Info(at08, "deg @ 0.8 g", detail);
        }
    }
}
