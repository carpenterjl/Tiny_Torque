using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// Shared manoeuvre for the three steady-state cornering measurements: hold a
    /// constant speed and wind the steering on slowly, sampling the car's
    /// response all the way to the grip limit.
    ///
    /// <b>One manoeuvre, three tests.</b> Skidpad grip (P4), understeer gradient
    /// (P5) and roll gradient (P7) are three different readings of the same
    /// steady-state sweep, so they share it rather than each driving their own.
    /// They stay separate scenes because each answers a different question and
    /// carries a different reference — but if they disagreed about the manoeuvre
    /// they would not be comparable, and sharing it is what stops that.
    ///
    /// <b>This manoeuvre found two real defects, and it is worth knowing what
    /// they were.</b> It first reported a peak of 0.36 g against a tyre model
    /// whose nominal capability is 1.0 g — and both causes were in the engine,
    /// not here.
    ///
    /// <b>1. The lateral acceleration channel was measuring the wrong thing.</b>
    /// <c>PhysicsDebugTelemetry</c> differenced the velocity expressed in the
    /// BODY frame, which silently subtracts the transport term ω × v. In a
    /// steady-state corner the body-frame velocity is nearly constant and the
    /// whole of the lateral acceleration lives in that term, so the channel read
    /// almost nothing for a car at its limit. Every straight-line test agreed
    /// with the broken form — at ω ≈ 0 the two are identical — so only this
    /// manoeuvre could see it. Fixing it took the peak from 0.36 g to 0.62 g.
    ///
    /// <b>2. The car ran out of suspension before it ran out of grip.</b> With
    /// <c>DebugVehicles.TargetF</c> at 0.0422 the Tiguan rested 12 mm from its
    /// droop stop. About 2° of roll — roughly 0.35 g — put the inside wheels off
    /// the end of their travel, the WheelCollider raycast stopped returning a
    /// hit, and those two corners produced no tyre force at all for a step at a
    /// time. The trace showed it plainly: <c>susp_1</c> and <c>susp_3</c> pinned
    /// at 1.0000 with <c>fz_1</c>/<c>fz_3</c> flicking to exactly 0 about thirty
    /// times a second, and a lateral acceleration chattering ±30 % around its
    /// mean. Resting the car mid-travel took the peak from 0.62 g to 0.833 g,
    /// and the same run now logs zero contact dropouts.
    ///
    /// What makes the result believable is not the number but where it departs:
    /// 7.3° of steer at a 96 m implied radius is about 5.7° of tyre slip angle,
    /// which is genuinely at this model's <c>AlphaPeak</c> of 6.8°. The car is
    /// leaving the road because the tyres are saturated, which is the only
    /// reason a skidpad figure means anything.
    ///
    /// <b>Steer ramp, not speed ramp.</b> The classic skidpad holds a fixed
    /// radius and raises speed. That needs the powertrain to be trustworthy at
    /// every speed, and this vehicle's is declared fiction. Winding steering on
    /// at a constant speed reaches the same limit through the same tyre states,
    /// and depends on the engine only to hold one cruise. The ramp is slow on
    /// purpose: each sample has to be a steady state, not a transient.
    ///
    /// <b>It coasts through the limit rather than powering through it, and on
    /// this car that is not a detail.</b> The Tiguan is front-wheel drive, so the
    /// same two tyres do the steering and the driving. Holding a constant speed
    /// at the limit means feeding them enough torque to overcome cornering drag,
    /// and every newton of that comes out of the friction circle they were using
    /// to turn. Measured with a speed-holding throttle servo, the car departed at
    /// 0.435 g with 70 % of its weight on the nose — the servo was fighting for
    /// speed and losing, and the number was a report on the controller.
    ///
    /// Disconnecting the driveline and letting the car coast removes the
    /// contamination entirely: no drive torque, so the front tyres spend their
    /// whole friction budget on turning, and the peak is the tyre model's actual
    /// lateral capability. The car slows as it corners, which is fine — every
    /// sample uses its own instantaneous speed.
    /// </summary>
    public abstract class SteadyCorneringTest : PhysicsTest
    {
        [Header("Steady cornering")]
        [Tooltip("Entry speed (m/s). The car coasts from here, so start high "
                 + "enough that it is still moving well when the steer ramp "
                 + "reaches the grip limit. 28 m/s at 0.9 g is an ~87 m radius, "
                 + "well inside the 600 m pad.")]
        public float cruiseSpeed = 28f;
        [Tooltip("Steer command per second. Sized from the geometry, not by feel: "
                 + "at 20 m/s the linear region (0.05–0.4 g) spans steer commands "
                 + "0.005–0.043 and the grip limit arrives near 0.10, so 0.008/s "
                 + "spends about 5 s crossing the linear region and reaches the "
                 + "limit around 12 s. Faster and the fit has almost no samples "
                 + "to work with.")]
        public float steerRatePerSec = 0.008f;
        [Tooltip("Give up once lateral acceleration has fallen this far below its "
                 + "peak — the car has departed and later samples are a spin.")]
        public float departFrac = 0.85f;
        [Tooltip("Ignore everything before this. The launch settles the body and "
                 + "spins the wheels up, and the lateral wobble that produces "
                 + "reads as a peak-then-fall — i.e. as a departure — while the "
                 + "steering is still essentially straight. Measured: a false "
                 + "departure at 2.1 s and 0.22 g, which is a quarter of the "
                 + "real limit.")]
        public float settleIgnoreSec = 4f;

        /// <summary>Published wheelbase (m); the sum of the authored axle offsets
        /// lands on it exactly.</summary>
        protected const float Wheelbase = 2.681f;

        protected float PeakLatG { get; private set; }
        protected float SteerCmd { get; private set; }

        // Linear-region accumulators: steer vs lateral g, and roll vs lateral g.
        private int _n;
        private double _sx, _sy, _sxx, _sxy;     // x = lat g, y = understeer deg
        private int _rn;
        private double _rx, _ry, _rxx, _rxy;     // x = lat g, y = roll deg

        protected override PhysicsTestEnvironment.EnvSpec Environment =>
            PhysicsTestEnvironment.EnvSpec.Pad();

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("lateral (m/s²)", "veh/a_lat");
            g.AddPane("roll (deg)", "veh/roll_deg");
            g.AddPane("steer (deg)", "veh/steer_deg");
            g.AddPane("speed (m/s)", "veh/speed");
        }

        /// <summary>No wheel-sync wait — see <see cref="PhysicsTest.SetSpeedNow"/>.
        /// The steer ramp starts from zero and takes seconds to reach anything
        /// worth measuring, so the launch transient is long gone by the first
        /// sample that counts.</summary>
        protected override void Arm()
        {
            SetFreewheel(true);
            SetSpeedNow(cruiseSpeed);
        }

        protected override void Drive(ScriptedDriver d, float t)
        {
            // No throttle at all — see the class comment on why driving a FWD
            // car through its own cornering limit measures the throttle servo.
            d.throttle = 0f;
            d.brake = 0f;
            SteerCmd = Mathf.Clamp01(steerRatePerSec * t);
            d.steer = SteerCmd;
        }

        protected override void Sample(float dt)
        {
            if (RunTime < settleIgnoreSec) return;

            float latG = Mathf.Abs(Ch("veh/a_lat")) / 9.81f;
            if (latG > PeakLatG) PeakLatG = latG;

            float v = Speed;
            if (v < 1f) return;

            // Linear region only. Past ~0.4 g the tyres are no longer near their
            // linear range and a gradient fitted through the saturating part is a
            // number about the peak, not about the gradient.
            if (latG < 0.05f || latG > 0.4f) return;

            // Understeer angle = actual steer − the Ackermann steer that this
            // curvature would need with no slip angles at all.
            float ay = latG * 9.81f;
            float ackermannDeg = Wheelbase * ay / (v * v) * Mathf.Rad2Deg;
            float understeerDeg = Mathf.Abs(Car.CurrentSteerAngle) - ackermannDeg;

            _sx += latG; _sy += understeerDeg;
            _sxx += (double)latG * latG; _sxy += (double)latG * understeerDeg;
            _n++;

            float roll = Mathf.Abs(Ch("veh/roll_deg"));
            _rx += latG; _ry += roll;
            _rxx += (double)latG * latG; _rxy += (double)latG * roll;
            _rn++;
        }

        /// <summary>Least-squares slope of understeer angle against lateral g —
        /// the understeer gradient, in deg/g.</summary>
        protected bool TryUndersteerGradient(out float degPerG) =>
            Slope(_n, _sx, _sy, _sxx, _sxy, out degPerG);

        /// <summary>Least-squares slope of body roll against lateral g.</summary>
        protected bool TryRollGradient(out float degPerG) =>
            Slope(_rn, _rx, _ry, _rxx, _rxy, out degPerG);

        private static bool Slope(int n, double sx, double sy, double sxx, double sxy,
                                  out float m)
        {
            m = 0f;
            if (n < 50) return false;
            double den = n * sxx - sx * sx;
            if (System.Math.Abs(den) < 1e-12) return false;
            m = (float)((n * sxy - sx * sy) / den);
            return true;
        }

        /// <summary>True once the car has passed the limit and started to depart,
        /// which is where every one of these measurements stops being valid.</summary>
        protected bool Departed =>
            RunTime > settleIgnoreSec + 2f
            && PeakLatG > 0.35f
            && Mathf.Abs(Ch("veh/a_lat")) / 9.81f < PeakLatG * departFrac;

        protected override void DrawExtra()
        {
            GUILayout.Label($"lat     {Mathf.Abs(Ch("veh/a_lat")) / 9.81f:0.000} g "
                            + $"(peak {PeakLatG:0.000})");
            GUILayout.Label($"steer   {Car.CurrentSteerAngle:0.0}°   cmd {SteerCmd:0.00}");
            GUILayout.Label($"roll    {Ch("veh/roll_deg"):0.00}°");
        }
    }
}
