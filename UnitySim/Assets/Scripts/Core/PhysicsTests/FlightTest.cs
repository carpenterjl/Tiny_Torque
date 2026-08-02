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
        /// <see cref="FlightTrimProbe"/> for what that costs a measurement.
        ///
        /// 14.66 m/s is what [TRIM] measures WITH the propeller wake modelled; it
        /// was 15.03 before, and the difference is the slipstream raising the
        /// tailplane's dynamic pressure and with it the download the tail carries.
        /// Left stale, this would launch every test half a metre per second off
        /// trim and start each one with a phugoid nobody asked for.</summary>
        protected virtual float LaunchSpeed => 14.66f;

        /// <summary>What the pilot holds before the run begins.</summary>
        protected virtual void Idle(ScriptedPilot p) => p.Neutral();

        /// <summary>Write this tick's stick and throttle.</summary>
        protected abstract void Fly(ScriptedPilot p, float t);

        // ---- state ----

        protected PlaneVehicle Plane { get; private set; }
        protected ScriptedPilot Pilot { get; private set; }
        protected AircraftSpec Spec { get; private set; }
        protected AirData Air => Plane.Air;

        /// <summary>Seconds the launch condition must hold CONTINUOUSLY before the run
        /// begins. Short, because a launch at trim settles fast — it only has to be
        /// longer than the transient the reposition itself creates.</summary>
        [Tooltip("Seconds the launch condition must hold continuously before the run "
                 + "begins. Zero would let the reset itself satisfy the gate.")]
        public float syncHoldSeconds = 1.5f;

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
        ///
        /// <b>The condition has to HOLD, not merely be true once.</b> The first
        /// version returned as soon as |vspeed| and bank were small, which is a
        /// condition <see cref="PlaneVehicle.LaunchAt"/> establishes by construction:
        /// it puts the aircraft level with a horizontal velocity, so vspeed and bank
        /// are exactly zero on the very step the reset happens. The gate therefore
        /// certified the reset rather than a settled aeroplane, and the run began one
        /// step later with the launch transient still in front of it. Tests that
        /// settle for twenty seconds inside their own run phase never noticed; the
        /// stall test, which measures from t = 0, did.
        ///
        /// Body rates are checked too. Attitude and vertical speed can both read zero
        /// at the instant of a reset while the aircraft is rotating hard, and it is
        /// precisely that state which produces a saturated control input on the first
        /// step of the run.
        /// </summary>
        protected override bool SyncReady(out string why)
        {
            float vs = Plane.VerticalSpeed;
            float bank = Mathf.Abs(Wrap180(Plane.transform.eulerAngles.z));
            float rate = Body.angularVelocity.magnitude * Mathf.Rad2Deg;

            bool quiet = Mathf.Abs(vs) < _syncTolVs && bank < 5f && rate < 15f;
            if (!quiet) _syncSettleFrom = Time.time;

            float held = Time.time - _syncSettleFrom;
            why = $"never settled after launch — vspeed {vs:0.00} m/s, bank {bank:0.0}°, "
                  + $"rates {rate:0.0}°/s, tas {Air.Tas:0.00} m/s "
                  + $"(held {held:0.00} s of the {syncHoldSeconds:0.00} s required)";
            return quiet && held >= syncHoldSeconds;
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
            GUILayout.Label($"wash {r.slipstreamIncrement:0.0} m/s   "
                            + $"worst {(r.worstSurface >= 0 ? Spec.surfaces[r.worstSurface].name : "—")}"
                            + $" @ {r.worstStation * 100f:0}% span");
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
        /// <param name="authority">Largest aileron deflection the leveller may use.
        /// Worth turning down near the stall: the ailerons live outboard, where the
        /// washout has deliberately left the least margin, so a leveller at full
        /// deflection is capable of stalling the very tip it is trying to hold up.
        /// That is the same reason a pilot is taught not to pick up a dropping wing
        /// with aileron at the stall, and A3 relies on it.</param>
        protected void HoldWingsLevel(ScriptedPilot p, float targetBankDeg = 0f,
                                      float authority = 1f)
        {
            float bank = Wrap180(Plane.transform.eulerAngles.z) - targetBankDeg;
            float rollRate = Body.transform.InverseTransformDirection(Body.angularVelocity).z
                             * Mathf.Rad2Deg;
            float a = Mathf.Clamp01(authority);
            p.roll = Mathf.Clamp(bank * 0.030f + rollRate * 0.004f, -a, a);
        }

        /// <summary>
        /// Hold a vertical speed with elevator. Used only where the QUANTITY being
        /// measured does not depend on how the attitude was reached — a level turn's
        /// load factor is trigonometry, so an autopilot flying the turn cannot bias
        /// it, and a stall speed is a property of the wing rather than of whatever
        /// held the height.
        ///
        /// <b>A cascade, and the flat version it replaces did not work.</b> The
        /// original drove the elevator straight from the vertical-speed error, which
        /// is the textbook pilot-induced-oscillation arrangement: vertical speed
        /// responds to elevator through pitch attitude, so a proportional loop closed
        /// around it is closing around two integrations and rings. Nothing exercised
        /// it until A3 — A2 flies its turn kinematically — and A3's first run saw it
        /// saturate the elevator within one step and put the aircraft at −3.9 g.
        ///
        /// So the outer loop asks for a pitch ATTITUDE, clamped to angles an
        /// aeroplane actually flies, and an inner loop holds that attitude with rate
        /// damping. Attitude is one integration from elevator rather than two, and
        /// the short period damps it, so the inner loop is well behaved by
        /// construction rather than by tuning.
        /// </summary>
        protected void HoldVerticalSpeed(ScriptedPilot p, float targetVs)
        {
            float vsErr = targetVs - Plane.VerticalSpeed;

            // Outer: vertical speed → a pitch attitude to fly, clamped to angles an
            // aeroplane actually holds so a transient cannot ask for the impossible.
            //
            // SIGN: about the body +X axis the nose swings toward −Y, so a POSITIVE
            // eulerAngles.x is nose DOWN. Sinking (vsErr > 0) therefore wants a
            // NEGATIVE attitude, hence the minus. The trim probe's own log is the
            // evidence: it printed pitch +6.3° while descending and −1.4° while
            // climbing.
            float wanted = Mathf.Clamp(-vsErr * 1.2f, -14f, 10f);

            // Inner: hold that attitude. Being below the wanted (more nose-down)
            // number calls for up elevator, which is a positive command — hence
            // (pitch − wanted) rather than the other way round. The rate term is
            // damping: +ve body-x rate is nose dropping, and a positive command
            // opposes it.
            float pitchDeg = Wrap180(Plane.transform.eulerAngles.x);
            float rate = Body.transform.InverseTransformDirection(Body.angularVelocity).x
                         * Mathf.Rad2Deg;
            p.pitch = Mathf.Clamp((pitchDeg - wanted) * 0.06f + rate * 0.010f, -1f, 1f);
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
