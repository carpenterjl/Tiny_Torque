using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P6a — threshold braking from 100 km/h.</b> Measures the longitudinal
    /// grip the tyre model actually delivers.
    ///
    /// <b>Read the two numbers separately: peak decel is the tyre, distance is
    /// the braking system.</b> They came apart once brake proportioning went in,
    /// and keeping them apart is the point of this test.
    ///
    /// <b>The tyre model is right, and this is where that got established.</b>
    /// Peak deceleration measures 1.01 g against an authored µ of 1.0 — the
    /// longitudinal capability is exactly what the friction coefficient says it
    /// should be, with nothing left over to explain.
    ///
    /// <b>What used to be blamed on µ was a brake-balance problem.</b> This row
    /// carried a note saying one friction coefficient could not produce both a
    /// real car's ~1.1 g braking and its ~0.82 g cornering. That was not what was
    /// happening: the model was delivering 0.788 g longitudinal against 0.833 g
    /// lateral — nearly EQUAL, exactly as an isotropic µ predicts — and the
    /// shortfall was the fixed 0.35 rear brake bias leaving the rears far below
    /// their slip peak while the fronts sat on theirs. A fixed ratio can only be
    /// right at one state of load transfer, and under 0.8 g the split is 78/22.
    /// Load-proportional distribution (<c>brakeProportioning</c>) is the fix, and
    /// it is what a real proportioning valve or EBD does.
    ///
    /// <b>The remaining distance gap is the ABS controller, and it is not being
    /// tuned away.</b> 46.97 m against a measured 39.31 m needs a mean of 0.98 g,
    /// and the tyres can supply it — the ABS model simply does not hold slip
    /// tightly enough at the peak (it intervenes at 0.15 against a KappaPeak of
    /// 0.10, then bleeds proportionally over a wide band). Those onsets are shared
    /// arcade-assist constants that every RC car drives on, so moving them to land
    /// a road-test number would be fitting the answer and changing ten other
    /// vehicles to do it. Recorded instead.
    ///
    /// <b>The test does the modulating, because the car cannot.</b> There is no
    /// ABS on this vehicle (the assists are forced off for every measurement),
    /// and the authored 2400 N·m brake is roughly a third more than the tyres
    /// can take — so full pedal locks all four wheels and the brush model's force
    /// falls away past its slip peak. A locked-wheel skid is a real measurement
    /// of something, but it is not what "threshold braking" means. So the brake
    /// is servoed to hold slip at the model's own <c>KappaPeak</c>, which is
    /// where longitudinal force is maximised. What is being measured is the
    /// tyre's peak capability, with the driver-aid gap filled explicitly rather
    /// than silently.
    ///
    /// The driveline is disconnected for the same reason as P1: a motor at zero
    /// volts is a short, and its braking would be counted as tyre grip.
    /// </summary>
    public sealed class P6aBrakingTest : CarPhysicsTest
    {
        protected override string TestId => "P6a";
        protected override string Title => "Threshold braking 100 → 0 km/h";
        // VERIFIED against a primary road test, unlike the "35–38 m" this used to
        // claim: BitAuto measured 39.31 m 100–0 km/h on a 2022 Tiguan L, and
        // MotorWeek's 2025 car stopped in ~116 ft from 60 mph (≈37.9 m equivalent).
        protected override string Expected => "39.3 m (BitAuto 2022 Tiguan L; 37–40 m band)";

        [Header("P6a")]
        [Tooltip("100 km/h in m/s.")]
        public float startSpeed = 27.78f;
        [Tooltip("Stop measuring here: slip ratio loses meaning as v → 0.")]
        public float stopSpeed = 0.5f;
        [Tooltip("Target slip. TyreModel.KappaPeak is 0.10 — the slip at which "
                 + "longitudinal force is greatest.")]
        public float targetSlip = 0.10f;
        [Tooltip("Servo gain (brake fraction per unit slip error per second) "
                 + "while building pressure. Release uses a tenth of it.")]
        public float gain = 40f;
        [Tooltip("Opening pedal position. 0 — the servo ramps up to the slip peak "
                 + "on its own. See the class note: this used to be 0.7, derived "
                 + "from a per-wheel brake authority that brake proportioning "
                 + "changed, and an opening guess this test does not need was not "
                 + "worth re-deriving.")]
        public float initialBrake = 0f;
        [Tooltip("Run with ABS, as the reference car has. See Arm(): past the slip "
                 + "peak tyre force falls, so lockup is a runaway that one pedal "
                 + "cannot control open-loop. Turn off to measure the tyre peak "
                 + "without it — the peak decel figure is the same either way.")]
        public bool useAbs = true;

        private float _brake;
        private float _distance;
        private float _v0;
        private float _peakDecel;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("speed (m/s)", "veh/speed");
            g.AddPane("a_long (m/s²)", "veh/a_long");
            g.AddPane("slip", "veh/slip_0", "veh/slip_1", "veh/slip_2", "veh/slip_3");
        }

        protected override void Arm()
        {
            SetFreewheel(true);
            _brake = initialBrake;

            // ABS ON — and it is the only assist any test in this suite enables.
            //
            // This is not a driver aid here, it is the car's braking system. The
            // reference figure is a road test of a car with ABS as mandated
            // equipment, so a no-ABS stop is not the same measurement, and the
            // difference is not small: past KappaPeak the tyre's force FALLS, so a
            // wheel that overshoots the peak sheds reaction torque and runs away to
            // lockup. Holding four wheels exactly on an unstable peak with one
            // pedal is the control problem ABS exists to solve, and open-loop the
            // servo cannot: measured, it oscillated between locked (slip -0.61) and
            // rolling (-0.04), touching 1.01 g at the peak but averaging 0.79.
            //
            // The tyre capability and the stopping distance are therefore reported
            // as two different numbers, because they answer two different
            // questions. Peak decel is the tyre model's own longitudinal limit and
            // owes nothing to ABS; distance is the whole braking system.
            if (useAbs)
            {
                var a = Car.assists;
                a.abs = 1f;
                Car.assists = a;
                Car.assistsActive = true;
            }

            LaunchAt(startSpeed, 2f);
        }

        protected override void Drive(ScriptedDriver d, float t)
        {
            d.throttle = 0f;
            d.steer = 0f;

            // With ABS, full pedal — because that IS the reference procedure. A
            // road-test emergency stop is the driver standing on the brake with
            // the anti-lock doing the modulating, and running the slip servo as
            // well would put two controllers on one actuator, each undoing the
            // other's correction. (Measured: servo + ABS gave 46.9 m, servo alone
            // 49.3 m.) The servo below is what the car has to fall back on when
            // there is no ABS to fill the gap.
            if (useAbs)
            {
                _brake = 1f;
                d.brake = 1f;
                return;
            }

            // Servo the pedal to the slip peak, ASYMMETRICALLY: build pressure
            // fast, bleed it off slowly.
            //
            // A symmetric integrator loses this test. Overshooting the slip peak
            // makes it wind the pedal all the way back out, and the stop then
            // runs at half the available grip — the first attempt averaged
            // 0.617 g while peaking at 1.02 g, which is a controller artefact
            // masquerading as a tyre result. Slow release also matches what
            // threshold braking IS: find the edge, then stay just under it.
            float slip = MeanAbsSlip();
            float err = targetSlip - slip;
            float k = err > 0f ? gain : gain * 0.1f;
            _brake = Mathf.Clamp01(_brake + k * err * Time.fixedDeltaTime);
            d.brake = _brake;
        }

        private float MeanAbsSlip()
        {
            float s = 0f;
            int n = 0;
            for (int i = 0; i < Car.WheelCount; i++)
            {
                if (!Car.WheelGrounded(i)) continue;
                s += Mathf.Abs(Car.WheelSlipRatio(i));
                n++;
            }
            return n > 0 ? s / n : 0f;
        }

        protected override void Sample(float dt)
        {
            if (_v0 <= 0f) _v0 = Speed;
            _distance += Speed * dt;
            float a = -Ch("veh/a_long");
            if (a > _peakDecel) _peakDecel = a;
        }

        protected override Verdict? Evaluate()
        {
            if (Speed > stopSpeed) return null;

            float meanG = _v0 * _v0 / (2f * Mathf.Max(_distance, 1e-3f)) / 9.81f;
            string detail = $"from {_v0:0.00} m/s · mean {meanG:0.000} g · "
                            + $"peak decel {_peakDecel:0.00} m/s² ({_peakDecel / 9.81f:0.00} g "
                            + "= the tyre's own limit, authored µ 1.0) · "
                            + $"final pedal {_brake:0.00} · "
                            + (useAbs ? "ABS on, full pedal (reference procedure)"
                                      : $"no ABS, slip servoed to {targetSlip:0.00}")
                            + " · gap to the reference is the ABS controller, not the tyre";

            // INFO by design — see the class comment. The number is reported and
            // trended; it does not gate.
            return Verdict.Info(_distance, "m", detail);
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"distance {_distance:0.00} m");
            GUILayout.Label($"pedal    {_brake:0.00}   slip {MeanAbsSlip():0.000} "
                            + $"(target {targetSlip:0.00})");
        }
    }
}
