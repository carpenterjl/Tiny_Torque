using AIHWSim.Core.Flight;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>A0 — free fall with the aerodynamics switched off.</b>
    ///
    /// The aircraft equivalent of the car's free-roll test: before any number is
    /// believed, prove the harness itself contributes nothing. Drop the aeroplane
    /// with the panels, the parasitic drag and the propeller all disabled, and it
    /// must fall exactly as a stone does. Anything else — a stray damping term, a
    /// force applied in the wrong frame, an integrator that leaks energy — shows up
    /// here as a clean discrepancy with nothing to blame it on.
    ///
    /// <b>The expected distance is not ½gt², and the difference is not an error.</b>
    /// Unity integrates semi-implicitly: it adds gravity to the velocity, then moves
    /// by the new velocity. After n steps that gives
    /// <code>
    ///   x = g·dt²·(1 + 2 + … + n) = g·dt²·n(n+1)/2 = ½·g·t·(t + dt)
    /// </code>
    /// which at 400 Hz over 3 s is 37 mm further than the continuous answer — a
    /// 0.08 % offset that is entirely predictable and entirely correct for the
    /// scheme. Predicting it instead of widening the tolerance around it is what
    /// lets this test pass to the MILLIMETRE, and a millimetre band is what makes
    /// it able to catch anything at all.
    /// </summary>
    public sealed class A0BallisticTest : FlightTest
    {
        [Tooltip("Seconds of free fall to measure over.")]
        public float fallSeconds = 3f;
        [Tooltip("Allowed error against the closed form (m).")]
        public float tolerance = 0.002f;

        protected override string TestId => "A0";
        protected override string Title => "Ballistic drop (aero off)";
        protected override string Expected => "exactly ½·g·t·(t+dt) — the harness adds nothing";

        // Nothing is flying, so the world can be bare and the settle can be short.
        protected override FlightTestEnvironment.EnvSpec Environment =>
            FlightTestEnvironment.EnvSpec.Bare();
        protected override float TestAltitude => 1500f;

        private float _startY;
        private int _steps;
        private bool _armed;

        protected override void Idle(ScriptedPilot p) => p.Neutral();

        protected override void Arm()
        {
            Plane.aeroEnabled = false;
            Plane.propulsionEnabled = false;
            PlaceAt(new Vector3(0f, TestAltitude, 0f), Quaternion.identity, 0f);
            _startY = TestAltitude;
            _steps = 0;
            _armed = true;
            // No sync: there is nothing to settle into. WantsSync stays false, so
            // the run begins on the next step.
        }

        protected override void Fly(ScriptedPilot p, float t) => p.Neutral();

        protected override void Sample(float dt)
        {
            if (_armed) _steps++;
        }

        protected override Verdict? Evaluate()
        {
            float dt = Time.fixedDeltaTime;
            if (_steps * dt < fallSeconds) return null;

            float fell = _startY - Plane.transform.position.y;
            float g = Mathf.Abs(Physics.gravity.y);
            float t = _steps * dt;

            // Semi-implicit Euler, exactly: x = g·dt²·n(n+1)/2.
            float expect = g * dt * dt * _steps * (_steps + 1) * 0.5f;
            float continuous = 0.5f * g * t * t;

            float err = fell - expect;
            string detail = $"fell {fell:0.0000} m in {t:0.000} s over {_steps} steps · "
                            + $"semi-implicit closed form {expect:0.0000} m "
                            + $"(continuous ½gt² would be {continuous:0.0000}, "
                            + $"a {(expect - continuous) * 1000f:0.0} mm scheme offset) · "
                            + $"err {err * 1000f:+0.000;-0.000} mm";

            return Mathf.Abs(err) <= tolerance
                ? Verdict.Pass(fell, "m", detail)
                : Verdict.Fail(fell, "m", detail);
        }
    }
}
