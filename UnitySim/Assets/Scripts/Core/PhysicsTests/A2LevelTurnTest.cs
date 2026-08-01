using AIHWSim.Core.Flight;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A2 — load factor in a level turn. The one test with no aerodynamics in
    /// its answer at all.</b>
    ///
    /// Hold a steady bank with the height constant, and the load factor is fixed by
    /// geometry alone: lift must supply weight vertically and centripetal force
    /// horizontally, so
    /// <code>
    ///   n = 1 / cos φ      → at 60° of bank, exactly 2.000 g
    /// </code>
    /// No coefficient, no area, no mass, no aspect ratio. If the aircraft is banked
    /// 60° and neither climbing nor descending, that number is 2.000 or the model
    /// is wrong about something fundamental.
    ///
    /// <b>This is the specific guard against the transport-term bug.</b> Body-frame
    /// acceleration must be derived by differencing WORLD velocity and rotating the
    /// answer, never by differencing body-frame velocity — the latter silently drops
    /// ω × v. In a steady turn the body-frame velocity is very nearly constant (same
    /// speed, still along its own nose), so the entire 2 g lives in that term and the
    /// broken form reports approximately 1. The car suite learned this the hard way:
    /// its skidpad read 0.36 g while the tyres delivered close to 1, and every
    /// straight-line test had agreed with the broken form because ω is zero there.
    /// An aircraft has no straight-line case that would hide it either, which is why
    /// this row exists.
    ///
    /// <b>Autopilot is legitimate here, uniquely.</b> The other tests fly open-loop
    /// so the aircraft's own trim is what gets measured. Here a bank-hold and a
    /// height-hold fly the turn — and cannot bias the result, because the quantity
    /// measured is a consequence of the ATTITUDE reached, not of how it was reached.
    /// </summary>
    public sealed class A2LevelTurnTest : FlightTest
    {
        [Tooltip("Bank angle to hold (deg). 60° gives exactly 2 g.")]
        public float bankDeg = 60f;
        [Tooltip("Seconds to let the turn settle after it is set up.")]
        public float establishSeconds = 3f;
        [Tooltip("Seconds of steady turn to average over. Short on purpose — the "
                 + "turn is set up kinematically and then only has to survive, so "
                 + "a long window just gives it time to drift out of validity.")]
        public float windowSeconds = 3f;
        [Tooltip("Allowed error in load factor (g).")]
        public float toleranceG = 0.06f;
        /// <summary>
        /// Mean climb rate the turn may still have. Not zero, because a residual
        /// descent is HANDLED rather than rejected — the prediction below uses
        /// cos γ / cos φ, which is the same force triangle with the flight path
        /// included. Beyond this the small-angle assumptions stop holding.
        /// </summary>
        public float levelToleranceVs = 1.5f;

        protected override string TestId => "A2";
        protected override string Title => "Level turn load factor (kinematic)";
        protected override string Expected => "n = 1/cos φ — exactly 2.000 g at 60° bank";

        /// <summary>Separate note, because the row's number does not carry it: this
        /// airframe CANNOT sustain a 60° level turn under its own power. It needs
        /// about 4.2 N at 21 m/s and the propeller supplies under 3. The turn here
        /// is enforced so the accelerometer can be checked in isolation; the
        /// performance limit is real and is recorded in the detail line.</summary>
        private const string SustainNote =
            "the airframe cannot hold this turn on its own power (needs ~4.2 N, has <3 N) "
            + "— the state is enforced so the instrument can be checked in isolation";

        protected override float TestAltitude => 700f;

        /// <summary>
        /// 16 m/s, and the first attempt at 18 showed why it matters. A 60° bank
        /// doubles the required lift, and the induced drag that comes with it rises
        /// as C_L². At 21 m/s the aeroplane needed about 4.2 N to hold the turn and
        /// the propeller could supply under 3 — so it descended at 3.8 m/s, which is
        /// not a level turn and not what 1/cos φ describes. At 16 m/s the sums work
        /// out the other way and the turn is sustainable. <b>That is a real
        /// performance limit of the airframe, not a defect</b>: a 2 kg trainer with
        /// a thrust-to-weight of 0.57 genuinely cannot hold 60° of bank at 21 m/s.
        /// </summary>
        protected override float LaunchSpeed => 16f;

        private float _t;
        private float _sumG, _sumBank, _sumVs;
        private int _n;

        protected override void Idle(ScriptedPilot p)
        {
            p.Neutral();
            p.throttle = 0.75f;
            HoldWingsLevel(p);
        }

        /// <summary>
        /// Establish the turn as an INITIAL CONDITION rather than by flying into it.
        ///
        /// A coordinated level turn is fully determined by bank angle and speed:
        /// the aircraft banks at φ, flies horizontally at V, and yaws about the
        /// world vertical at Ω = g·tan φ / V. All three are set directly here.
        ///
        /// <b>Why not fly into it.</b> The first attempt used a bank-hold plus a
        /// height-hold and never got past 7.5° — the elevator loop pulled, the
        /// aircraft slowed, and it stalled and fell at 15 m/s. Tuning that
        /// controller would make the test a report on the controller. Setting the
        /// state directly makes it a report on ONE thing: given an aeroplane
        /// demonstrably in a 60° level turn, does the modelled accelerometer read
        /// 1/cos φ? That is the transport-term question, and nothing else.
        /// </summary>
        protected override void Arm()
        {
            float v = LaunchSpeed;
            float g = Mathf.Abs(Physics.gravity.y);
            float phi = bankDeg * Mathf.Deg2Rad;

            var pos = new Vector3(0f, TestAltitude, 0f);
            var rot = Quaternion.Euler(0f, 0f, -bankDeg);   // −z Euler = right wing down
            Plane.LaunchAt(pos, rot, v);
            // Velocity horizontal along the nose's heading, not along the banked
            // body axis — a level turn is level.
            Body.linearVelocity = new Vector3(0f, 0f, v);
            // Ω = g·tan φ / V about world up. This IS the ω that the transport term
            // lives in.
            Body.angularVelocity = Vector3.up * (g * Mathf.Tan(phi) / v);

            // No sync: the state is already what it should be.
        }

        /// <summary>
        /// Fly the circle KINEMATICALLY: the state is re-imposed every step rather
        /// than commanded through the controls.
        ///
        /// <b>Two attempts at flying it failed, and both failed honestly.</b> With a
        /// bank-hold and a height-hold the aircraft stalled at 7.5°; set up
        /// correctly at 21 m/s it descended at 3.8 m/s because a 60° bank needs
        /// more thrust than a 0.57 thrust-to-weight trainer has; at 16 m/s it
        /// departed the other way. <b>That is a genuine performance limit of this
        /// airframe</b> — it is not a bug and it is recorded rather than tuned away.
        ///
        /// But it is not what this row measures. The claim here is narrow and
        /// worth isolating: <i>given an aircraft demonstrably in a banked, level,
        /// constant-speed turn, does the modelled accelerometer read cos γ / cos φ?</i>
        /// That is a question about the instrument, not the aerodynamics, and the
        /// only way to ask it cleanly is to guarantee the state instead of hoping
        /// the aeroplane can hold it. Enforcing the circle also makes the answer
        /// unambiguous: any deviation is the transport term ω×v, because there is
        /// nothing else left for it to be.
        /// </summary>
        protected override void Fly(ScriptedPilot p, float t)
        {
            _t = t;
            p.Neutral();

            float v = LaunchSpeed;
            float g = Mathf.Abs(Physics.gravity.y);
            float phi = bankDeg * Mathf.Deg2Rad;
            float omega = g * Mathf.Tan(phi) / v;      // rad/s about world up
            float heading = omega * t;                 // started at 0 in Arm()

            float radius = v / omega;
            // Circle centred to the right of the initial heading, since a right
            // bank turns right.
            var centre = new Vector3(radius, TestAltitude, 0f);
            var pos = centre + new Vector3(-Mathf.Cos(heading), 0f, Mathf.Sin(heading)) * radius;
            var fwd = new Vector3(Mathf.Sin(heading), 0f, Mathf.Cos(heading));

            Plane.transform.SetPositionAndRotation(
                pos, Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(0f, 0f, -bankDeg));
            Body.linearVelocity = fwd * v;
            Body.angularVelocity = Vector3.up * omega;
        }

        protected override void Sample(float dt)
        {
            if (_t < establishSeconds) return;
            _sumG += Plane.LoadFactor;
            _sumBank += Mathf.Abs(Wrap180(Plane.transform.eulerAngles.z));
            _sumVs += Plane.VerticalSpeed;
            _n++;
        }

        protected override Verdict? Evaluate()
        {
            if (_t < establishSeconds + windowSeconds || _n == 0) return null;

            float meanG = _sumG / _n;
            float meanBank = _sumBank / _n;
            float meanVs = _sumVs / _n;

            // Predict against the bank actually FLOWN, not the one commanded: a
            // turn that settles at 58° must be judged against 1/cos 58°, or the
            // test grades the controller instead of the physics.
            //
            // And include the flight path: vertical equilibrium in a steady turn is
            // L·cos φ = W·cos γ, so n = cos γ / cos φ. At γ = 0 this is exactly
            // 1/cos φ; a residual descent is then accounted for rather than
            // invalidating the run. Still trigonometry, still parameter-free.
            float gamma = Mathf.Asin(Mathf.Clamp(meanVs / Mathf.Max(1f, Air.Tas), -1f, 1f));
            float predicted = Mathf.Cos(gamma) / Mathf.Cos(meanBank * Mathf.Deg2Rad);
            float err = meanG - predicted;

            string detail = $"held {meanBank:0.00}° bank at {Air.Tas:0.0} m/s · "
                            + $"γ {gamma * Mathf.Rad2Deg:+0.0;-0.0}° · "
                            + $"cos γ / cos φ = {predicted:0.000} g · "
                            + $"err {err:+0.000;-0.000} · mean climb {meanVs:+0.00;-0.00} m/s · "
                            + $"trigonometry only — no aero coefficient enters this, so a "
                            + $"disagreement is the transport term ω×v, not the aerodynamics · "
                            + SustainNote;

            if (Mathf.Abs(meanVs) > levelToleranceVs)
                return new Verdict
                {
                    kind = Kind.Invalid,
                    value = meanG,
                    units = "g",
                    detail = detail + " · NOT LEVEL, so 1/cos φ does not apply",
                };

            return Mathf.Abs(err) <= toleranceG
                ? Verdict.Pass(meanG, "g", detail)
                : Verdict.Fail(meanG, "g", detail + $" (tol ±{toleranceG})");
        }
    }
}
