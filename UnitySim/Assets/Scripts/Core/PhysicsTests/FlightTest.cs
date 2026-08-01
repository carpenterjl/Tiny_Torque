using AIHWSim.Core.Flight;
using AIHWSim.Garage;
using AIHWSim.Telemetry;
using AIHWSim.Vehicles;
using AIHWSim.Vehicles.Aero;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// Everything in a physics test that is specifically about an AIRCRAFT: the
    /// airfield, the aeroplane, the scripted pilot, and the few helpers that only
    /// mean anything when the subject has wings.
    ///
    /// The sibling of <see cref="CarPhysicsTest"/>, and it inherits none of its
    /// vocabulary — there is no <c>WheelsSynced</c> here to satisfy and no
    /// <c>SetFreewheel</c> to misuse. Both sit on <see cref="PhysicsTest"/>, which
    /// owns the phase machine, the verdict, the result JSON and the HUD, and which
    /// never learns which subject it has.
    ///
    /// <b>Every measurement starts in the air.</b> A take-off roll depends on the
    /// gear model, which is a declared simplification, so no result is allowed to
    /// rest on it. <see cref="LaunchAtTrim"/> puts the aeroplane at a chosen
    /// airspeed, wings level, and the sync phase waits for the launch transient to
    /// settle before the run begins — the direct analogue of a car waiting for its
    /// wheels to spin up.
    /// </summary>
    public abstract class FlightTest : PhysicsTest
    {
        // ---- what a flight test declares ----

        protected virtual FlightTestEnvironment.EnvSpec Environment =>
            FlightTestEnvironment.EnvSpec.Bare();

        /// <summary>Height to fly at. High, because several of these tests descend
        /// for half a minute and none of them should be interrupted by the ground.</summary>
        protected virtual float TestAltitude => 600f;

        /// <summary>Airspeed the aircraft is launched at. Roughly its own trim
        /// speed, so the launch does not excite a large phugoid — see
        /// <see cref="FlightTrimProbe"/> for what that costs a measurement.</summary>
        protected virtual float LaunchSpeed => 15.0f;

        /// <summary>What the pilot holds before the run begins.</summary>
        protected virtual void Idle(ScriptedPilot p) => p.Neutral();

        /// <summary>Write this tick's stick and throttle.</summary>
        protected abstract void Fly(ScriptedPilot p, float t);

        // ---- state ----

        protected PlaneVehicle Plane { get; private set; }
        protected ScriptedPilot Pilot { get; private set; }
        protected AircraftSpec Spec { get; private set; }
        protected AirData Air => Plane.Air;

        private PlaneInput _input;
        private float _syncTolVs = 0.5f;
        private float _syncSettleFrom;

        protected override string ResultFamily => "aero";
        protected override string LogTag => "[AERO]";
        protected override string HudFooter =>
            "still air · no wind · panel aero + prop only";

        // ---- hooks ----

        protected override void BuildSubject()
        {
            Spec = DebugPlanes.SportRc();

            var (cam, graph) = FlightTestEnvironment.Build(Environment);
            Physics.SyncTransforms();

            var pos = new Vector3(0f, TestAltitude, 0f);
            var rig = DebugPlaneRig.BuildPlane(Spec, pos, Quaternion.identity);
            Plane = rig.plane;
            Body = Plane.GetComponent<Rigidbody>();
            _input = rig.input;

            var follow = cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = Plane.transform;
            follow.offset = new Vector3(0f, 2.5f, -12f);

            DebugPlaneRig.AttachRunner(ref rig, graph, physicsRateHz, controlRateHz, logCsv);
            Runner = rig.runner;
        }

        protected override void InstallScriptedInput()
        {
            Pilot = new ScriptedPilot();
            _input.source = Pilot;
        }

        protected override void HandControlsToHuman() =>
            _input.source = new PilotInputSource();

        protected override void IdleInputs() => Idle(Pilot);

        protected override void DriveInputs(float t) => Fly(Pilot, t);

        /// <summary>
        /// The aircraft's launch condition: settled at a steady vertical speed with
        /// the wings level. The analogue of the car's wheel sync — it is what makes
        /// a glide measurement start from trim rather than from the transient the
        /// launch itself created.
        /// </summary>
        protected override bool SyncReady(out string why)
        {
            float vs = Plane.VerticalSpeed;
            float bank = Mathf.Abs(Wrap180(Plane.transform.eulerAngles.z));
            why = $"never settled after launch — vspeed {vs:0.00} m/s, bank {bank:0.0}°, "
                  + $"tas {Air.Tas:0.00} m/s";
            return Mathf.Abs(vs) < _syncTolVs && bank < 5f;
        }

        protected override void ConfigureGraph(GraphOverlay g)
        {
            g.AddPane("airspeed (m/s)", "air/tas");
            g.AddPane("alpha (deg)", "air/alpha_deg");
            g.AddPane("altitude (m)", "air/altitude_m");
            g.AddPane("load (g)", "air/load_g");
        }

        protected override void DrawExtra()
        {
            GUILayout.Label($"tas {Air.Tas:0.00} m/s   alt {Plane.AltitudeAgl:0.0} m   "
                            + $"vs {Plane.VerticalSpeed:+0.00;-0.00}");
            GUILayout.Label($"a {Air.AlphaDeg:+0.0;-0.0}°   b {Air.BetaDeg:+0.0;-0.0}°   "
                            + $"g {Plane.LoadFactor:0.00}   "
                            + $"bank {Wrap180(Plane.transform.eulerAngles.z):+0;-0;0}°");
            var r = Plane.LastAero;
            GUILayout.Label($"stalled {r.stalledPanels}/{r.totalPanels}   "
                            + $"margin {r.stallMargin * 100f:0}%   "
                            + $"thrust {Plane.Thrust:0.00} N");
        }

        // ---- helpers ----

        /// <summary>Launch level at a chosen airspeed and wait for it to settle.</summary>
        protected void LaunchAtTrim(float airspeed, float vsTolerance = 0.5f)
        {
            _syncTolVs = vsTolerance;
            _syncSettleFrom = Time.time;
            WantsSync = true;
            Plane.LaunchAt(new Vector3(0f, TestAltitude, 0f), Quaternion.identity, airspeed);
        }

        /// <summary>Put the aircraft somewhere with no wait at all.</summary>
        protected void PlaceAt(Vector3 pos, Quaternion rot, float airspeed) =>
            Plane.LaunchAt(pos, rot, airspeed);

        /// <summary>
        /// Hold the wings level with aileron. Both gains POSITIVE: in this body
        /// frame +Z is forward, so a positive rotation about it lifts the RIGHT
        /// wing — a positive z-Euler means banked left, and the correction is right
        /// aileron, which is a positive roll command. The intuitive `-bank` makes
        /// the loop positive-feedback and the aircraft departs within seconds.
        ///
        /// This exists because the propeller's torque reaction rolls the airframe
        /// continuously and nothing in an aeroplane opposes that by itself — a real
        /// model needs a click of aileron trim, and a test that does not hold the
        /// wings level ends up measuring a descending spiral.
        /// </summary>
        protected void HoldWingsLevel(ScriptedPilot p, float targetBankDeg = 0f)
        {
            float bank = Wrap180(Plane.transform.eulerAngles.z) - targetBankDeg;
            float rollRate = Body.transform.InverseTransformDirection(Body.angularVelocity).z
                             * Mathf.Rad2Deg;
            p.roll = Mathf.Clamp(bank * 0.030f + rollRate * 0.004f, -1f, 1f);
        }

        /// <summary>Hold a vertical speed with elevator. Used only where the
        /// QUANTITY being measured does not depend on how the attitude was reached
        /// — a level turn's load factor is trigonometry, so an autopilot flying the
        /// turn cannot bias it.</summary>
        protected void HoldVerticalSpeed(ScriptedPilot p, float targetVs)
        {
            float err = targetVs - Plane.VerticalSpeed;
            float pitchRate = Body.transform.InverseTransformDirection(Body.angularVelocity).x
                              * Mathf.Rad2Deg;
            p.pitch = Mathf.Clamp(err * 0.12f + pitchRate * 0.010f, -1f, 1f);
        }

        /// <summary>Hold an airspeed with throttle.</summary>
        protected void HoldAirspeed(ScriptedPilot p, float targetMps)
        {
            float err = targetMps - Air.Tas;
            p.throttle = Mathf.Clamp01(p.throttle + err * 0.02f * Time.fixedDeltaTime * 10f);
        }

        protected static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
