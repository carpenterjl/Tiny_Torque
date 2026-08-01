using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P6b — park hold on a 10 % slope.</b> The car is placed on the grade,
    /// the handbrake is set, and it must not creep.
    ///
    /// This is the only check on whether the sticky-tyre constraint — the
    /// mechanism that stops a stationary car from sliding — actually holds
    /// 1500 kg. That mechanism is scale-sensitive in a way nothing else here is:
    /// it applies a fixed phantom torque against the wheel's own inertia, and at
    /// the RC car's 2.7e-5 kg·m² the stock 0.05 N·m is decisive while against the
    /// Tiguan's 1.8 kg·m² it is 0.028 rad/s² — nothing at all. The design
    /// authors <c>stickyPhantomNm = 2800</c> precisely to restore the ratio, and
    /// this test is what says whether that worked.
    ///
    /// A 10 % grade asks for 1471 N of hold, about a tenth of the car's weight.
    ///
    /// <b>This test found a real engine defect, and it was not the sticky
    /// constraint.</b> The first run creeped downhill at a dead-steady
    /// 0.141 m/s, µ_eff 0.271 against an authored 1.0. The tyre curve was
    /// delivering plenty (~2330 N per locked rear); the longitudinal impulse
    /// clamp in <c>TyreModel.Forces</c> was throwing it away. The clamp's
    /// wheel-side compliance term (r²/J) assumes the tyre force is free to
    /// spin the wheel up and kill the slip through the wheel's own degree of
    /// freedom — false for a brake-locked wheel, whose spin DOF is rigid. With
    /// r²/J at 27× the body term, the cap collapsed to ~759 N per wheel, and
    /// the car settled exactly where the clamp balances gravity: solving
    /// 2·v/(dt·(invMassEff+rSqOverJ)) = 1464 N predicts v = 0.136 m/s, and the
    /// test measured 0.1407. That creep sat above the rest gate forever, so the
    /// parking constraint never re-engaged. The fix is the stiction branch in
    /// <c>TyreModel.Forces</c>: solve with body-only compliance assuming the
    /// wheel stays locked, then check the brake's torque budget — all derived
    /// from the integrator, no tuned numbers.
    ///
    /// <b>The handbrake is held from the first frame, not from the arm.</b> The
    /// default settle leaves the driver neutral for five seconds, which on a
    /// slope means five seconds of rolling downhill before the test starts — so
    /// this one overrides <c>Idle</c>. The ground is frictionless, as everywhere
    /// else in this suite, so the holding force has to come from the tyre model
    /// or the test proves nothing.
    /// </summary>
    public sealed class P6bParkHoldTest : PhysicsTest
    {
        protected override string TestId => "P6b";
        protected override string Title => "Park hold on 10 % slope";
        protected override string Expected => "drift < 0.05 m in 10 s";

        [Header("P6b")]
        public float holdSec = 10f;
        public float driftLimit = 0.05f;
        [Tooltip("Where on the slope to park (world z). The slope runs from "
                 + "z = -150 to z = -350.")]
        public float parkZ = -250f;

        private Vector3 _start;
        private float _maxDrift;

        protected override PhysicsTestEnvironment.EnvSpec Environment =>
            PhysicsTestEnvironment.EnvSpec.WithSlope();

        /// <summary>Parked facing up the grade, found by raycast — see
        /// <see cref="PhysicsTest.OnSurfaceAt"/> for why not by arithmetic.</summary>
        protected override (Vector3 pos, Quaternion rot) SpawnPose() =>
            OnSurfaceAt(0f, parkZ, Vector3.back);

        protected override void Idle(ScriptedDriver d)
        {
            d.Neutral();
            d.handbrake = true;   // from frame zero, or it rolls during the settle
        }

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("position", "veh/pos_x", "veh/pos_z");
            g.AddPane("corner load (N)", "veh/fz_0", "veh/fz_1", "veh/fz_2", "veh/fz_3");
        }

        protected override void Arm() => _start = Car.transform.position;

        protected override void Drive(ScriptedDriver d, float t)
        {
            d.throttle = 0f;
            d.steer = 0f;
            d.brake = 0f;
            d.handbrake = true;
        }

        protected override void Sample(float dt)
        {
            float drift = Vector3.Distance(Car.transform.position, _start);
            if (drift > _maxDrift) _maxDrift = drift;
        }

        protected override Verdict? Evaluate()
        {
            if (RunTime < holdSec) return null;

            // The car creeping at a steady speed is a force balance, and that
            // makes the effective friction directly readable: the tyres must be
            // producing exactly the downhill component of weight, so dividing it
            // by the load on the wheels actually doing the holding gives the µ
            // the model delivered. Reported whether it passes or fails, because
            // "it slipped" and "it slipped at µ 0.24" are different findings.
            float grade = 0.10f;
            float needed = Body.mass * Mathf.Abs(Physics.gravity.y)
                           * grade / Mathf.Sqrt(1f + grade * grade);
            float lockedFz = 0f;
            for (int i = 0; i < Car.WheelCount; i++)
                if (Mathf.Abs(Car.WheelOmega(i)) < 0.05f) lockedFz += Ch($"veh/fz_{i}");
            float muEff = lockedFz > 1f ? needed / lockedFz : 0f;

            string detail = $"held {holdSec:0} s on 10 % · "
                            + $"final speed {Speed:0.0000} m/s · "
                            + $"pitch {Ch("veh/pitch_deg"):0.00}° · "
                            + $"handbrake {(Car.Handbraking ? "ON" : "OFF")} · "
                            + $"need {needed:0} N, locked-wheel load {lockedFz:0} N "
                            + $"→ µ_eff {muEff:0.000} (authored µ 1.0) · "
                            + WheelStateText();

            return _maxDrift <= driftLimit
                ? Verdict.Pass(_maxDrift, "m", detail)
                : Verdict.Fail(_maxDrift, "m", detail + " — tyre model failed to hold");
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"drift   {_maxDrift * 1000f:0.0} mm "
                            + $"(limit {driftLimit * 1000f:0} mm)");
            GUILayout.Label($"speed   {Speed:0.0000} m/s");
        }
    }
}
