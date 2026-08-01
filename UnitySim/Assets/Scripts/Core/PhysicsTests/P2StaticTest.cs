using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>P2 — static weight distribution and ride height</b> (with P0b folded in).
    ///
    /// The car is parked, settled, and touched by nothing. It checks the things
    /// every other test silently assumes: that the mass lands where the design
    /// says, that the suspension rests where the design says, and that the car
    /// sits level.
    ///
    /// <b>This is the harness's own calibration.</b> The headless static probe
    /// already measured all of it, so P2's job is to come back with the SAME
    /// numbers. If it does, the scene, the settle, the telemetry and the verdict
    /// path add nothing of their own and every other test's result can be trusted
    /// to be about the physics. If it does not, nothing else here means anything.
    ///
    /// <b>The expected compression is 0.5 — but not for the reason an early
    /// draft said.</b> That draft predicted 0.5 from an inferred rest-position
    /// model with load and spring terms in it. The model this engine actually
    /// implements is <c>rest_compression = 1 − targetPosition</c>, with no load
    /// and no spring rate in it at all (<c>DebugVehicles.cs</c>), so the number
    /// here is not a prediction about sag — it is a restatement of
    /// <c>TargetF</c>, and it moves whenever that constant does. It read 0.9578
    /// while <c>TargetF</c> was 0.0422; the skidpad showed that value left the
    /// car 12 mm from its droop stop, <c>TargetF</c> went to the mid-travel 0.5
    /// it was always documented as, and this followed it.
    ///
    /// Ride height did NOT move with it, and that is the point of checking both:
    /// <c>HubY</c> is derived from <c>TargetF</c>, so the hub stays at the
    /// measured loaded radius while the travel around it is repositioned. A
    /// change that moved this number and left 0.349 alone is the one that was
    /// intended.
    ///
    /// P0b (ride height stable while parked) is the same measurement over the
    /// same window, so it is reported as this test's band rather than as a scene
    /// of its own.
    /// </summary>
    public sealed class P2StaticTest : CarPhysicsTest
    {
        protected override string TestId => "P2";
        protected override string Title => "Static weight & ride height";
        protected override string Expected =>
            "front 60.38 % · ride 0.349 m · susp 0.5 · level";

        [Header("P2 references")]
        [Tooltip("MEASURED loaded wheel-centre height (m).")]
        public float rideTarget = 0.349f;
        public float rideTol = 0.002f;
        [Tooltip("Back-solved from the chassis CoM: 60 % front, h_cg 0.62 m.")]
        public float frontPctTarget = 60.38f;
        public float frontPctTol = 0.5f;
        [Tooltip("1 - suspTargetPos, i.e. mid-travel. See the class comment: "
                 + "no load term, so this tracks DebugVehicles.TargetF exactly.")]
        public float suspTarget = 0.5f;
        public float suspTol = 0.02f;
        [Tooltip("Seconds of parked observation after the settle.")]
        public float windowSec = 2f;

        private float _rideMin = float.MaxValue, _rideMax = float.MinValue;

        protected override void ConfigureGraph(Telemetry.GraphOverlay g)
        {
            g.AddPane("ride height (m)", "veh/ride_height");
            g.AddPane("attitude (deg)", "veh/pitch_deg", "veh/roll_deg");
            g.AddPane("corner load (N)", "veh/fz_0", "veh/fz_1", "veh/fz_2", "veh/fz_3");
        }

        protected override void Drive(ScriptedDriver d, float t) => d.Neutral();

        protected override void Sample(float dt)
        {
            float ride = Ch("veh/ride_height");
            if (ride < _rideMin) _rideMin = ride;
            if (ride > _rideMax) _rideMax = ride;
        }

        protected override Verdict? Evaluate()
        {
            if (RunTime < windowSec) return null;

            float ride = Ch("veh/ride_height");
            float bandMm = (_rideMax - _rideMin) * 1000f;
            float frontPct = Ch("veh/front_load_pct");
            float pitch = Ch("veh/pitch_deg");
            float roll = Ch("veh/roll_deg");

            float sumFz = 0f, suspMin = float.MaxValue, suspMax = float.MinValue;
            for (int i = 0; i < Car.WheelCount; i++)
            {
                sumFz += Ch($"veh/fz_{i}");
                float s = Ch($"veh/susp_{i}");
                if (s < suspMin) suspMin = s;
                if (s > suspMax) suspMax = s;
            }
            float weight = Body.mass * Mathf.Abs(Physics.gravity.y);
            float support = weight > 1f ? sumFz / weight : 0f;

            bool ok = Mathf.Abs(ride - rideTarget) <= rideTol
                      && Mathf.Abs(frontPct - frontPctTarget) <= frontPctTol
                      && Mathf.Abs(suspMin - suspTarget) <= suspTol
                      && Mathf.Abs(suspMax - suspTarget) <= suspTol
                      && Mathf.Abs(pitch) <= 0.1f
                      && Mathf.Abs(roll) <= 0.1f
                      && bandMm <= 1f;

            string detail =
                $"front {frontPct:0.00} % · susp {suspMin:0.0000}–{suspMax:0.0000} · "
                + $"band {bandMm:0.0} mm · pitch {pitch:0.00}° roll {roll:0.00}° · "
                + $"ΣFz/W {support:0.0000} · mass {Body.mass:0} kg";

            return ok ? Verdict.Pass(ride, "m", detail)
                      : Verdict.Fail(ride, "m", detail);
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"ride    {Ch("veh/ride_height"):0.0000} m");
            GUILayout.Label($"front   {Ch("veh/front_load_pct"):0.00} %");
            GUILayout.Label($"susp    {Ch("veh/susp_0"):0.0000}  {Ch("veh/susp_1"):0.0000}  "
                            + $"{Ch("veh/susp_2"):0.0000}  {Ch("veh/susp_3"):0.0000}");
            GUILayout.Label($"attitude pitch {Ch("veh/pitch_deg"):0.00}°  "
                            + $"roll {Ch("veh/roll_deg"):0.00}°");
        }
    }
}
