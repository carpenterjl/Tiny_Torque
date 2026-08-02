using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// The channels a flight measurement needs. Twin of
    /// <see cref="PhysicsDebugTelemetry"/>, and bound the same way for the same
    /// load-bearing reason: <c>CsvLogger.Begin</c> snapshots the hub's channel list
    /// ONCE while <c>Commit</c> walks the live set, so a channel registered after
    /// logging starts writes a row wider than its header and silently shifts every
    /// column after it. Everything here is registered from <see cref="Bind"/>,
    /// called during the bootstrap's Awake, before <c>SimulationRunner.Start</c>.
    ///
    /// <b>Airspeed and ground speed are separate channels from the first commit.</b>
    /// <c>veh/speed</c> is speed over the ground; <c>air/tas</c> is speed through
    /// the air. Today they are equal, because the wind is zero. Publishing one
    /// under the other's name would work perfectly until the day wind exists, at
    /// which point every measurement taken before it becomes unreadable — you could
    /// not tell which of the two any old number had meant.
    ///
    /// <b>No per-panel channels.</b> Where the lift is on the span is a gizmo
    /// question. Registering it would make the CSV's column count depend on the
    /// panel array existing before Bind returns, which reintroduces exactly the
    /// ordering hazard the Bind-in-Awake rule removes.
    ///
    /// <b>Columns that are always zero.</b> <c>SimulationRunner</c> registers
    /// <c>wheel/*</c> and <c>cmd/steer_deg</c> unconditionally, so an aircraft CSV
    /// carries them full of zeros. That is accepted rather than fixed: changing
    /// that method would put a diff on the file every car test depends on. Do not
    /// read those columns as measurements.
    /// </summary>
    public sealed class FlightTelemetry : MonoBehaviour
    {
        private SimulationRunner _runner;
        private PlaneVehicle _plane;
        private Rigidbody _body;

        public void Bind(SimulationRunner runner, PlaneVehicle plane)
        {
            _runner = runner;
            _plane = plane;
            _body = plane != null ? plane.GetComponent<Rigidbody>() : null;

            var hub = runner != null ? runner.Hub : null;
            if (hub == null) return;

            // Shared names, deliberately identical to the car's, so a tool that
            // reads attitude works on either vehicle.
            hub.RegisterChannel("veh/pos_y");
            hub.RegisterChannel("veh/roll_deg");
            hub.RegisterChannel("veh/pitch_deg");
            hub.RegisterChannel("veh/a_long");
            hub.RegisterChannel("veh/a_lat");
            hub.RegisterChannel("veh/a_vert");

            // Air data.
            hub.RegisterChannel("air/tas");
            hub.RegisterChannel("air/alpha_deg");
            hub.RegisterChannel("air/beta_deg");
            hub.RegisterChannel("air/q");
            hub.RegisterChannel("air/altitude_m");
            hub.RegisterChannel("air/vspeed");
            hub.RegisterChannel("air/load_g");

            // Whole-aircraft aerodynamic totals.
            hub.RegisterChannel("air/lift_n");
            hub.RegisterChannel("air/drag_n");
            hub.RegisterChannel("air/cl");
            hub.RegisterChannel("air/cd");
            hub.RegisterChannel("air/ld");

            // Stall state. panels_stalled is the honest metric: it reports the
            // WORST strip rather than an average, which is the only way a tip
            // stall is visible at all — an averaged alpha stays comfortable while
            // one wing lets go.
            hub.RegisterChannel("air/panels_stalled");
            hub.RegisterChannel("air/stall_margin");

            // Propulsion, and the wake it drags behind it. eta_tail is the tail's
            // local dynamic pressure over the free stream's — the η_h a stability
            // calculation normally has to author. Here it is a measurement, and
            // watching it move with the throttle column is the whole payoff of
            // modelling the slipstream at all.
            hub.RegisterChannel("air/thrust_n");
            hub.RegisterChannel("air/prop_rps");
            hub.RegisterChannel("air/slipstream");
            hub.RegisterChannel("air/eta_tail");

            // Commanded controls. "Was the stick actually centred" is the first
            // question of every unexplained roll, and without these the trace
            // cannot answer it.
            hub.RegisterChannel("cmd/throttle");
            hub.RegisterChannel("cmd/aileron");
            hub.RegisterChannel("cmd/elevator");
            hub.RegisterChannel("cmd/rudder");

            // Body rates, for the roll-rate and phugoid tests.
            hub.RegisterChannel("air/p_rate");
            hub.RegisterChannel("air/q_rate");
            hub.RegisterChannel("air/r_rate");
        }

        private void FixedUpdate()
        {
            var hub = _runner != null ? _runner.Hub : null;
            if (hub == null || _plane == null || _body == null) return;

            Transform t = _plane.transform;
            hub.SetValue("veh/pos_y", t.position.y);

            Vector3 e = t.rotation.eulerAngles;
            hub.SetValue("veh/roll_deg", Wrap180(e.z));
            hub.SetValue("veh/pitch_deg", Wrap180(e.x));

            // PlaneVehicle already differences world velocity and rotates into the
            // body frame — see the comment on its UpdateAccelerometer for why the
            // order is the measurement. It reports SPECIFIC force (gravity
            // included, as an accelerometer reads); the car's channels exclude
            // gravity, so subtract it back out to keep the two comparable.
            Vector3 a = _plane.BodyAcceleration + t.InverseTransformDirection(Physics.gravity);
            hub.SetValue("veh/a_long", a.z);
            hub.SetValue("veh/a_lat", a.x);
            hub.SetValue("veh/a_vert", a.y);

            var air = _plane.Air;
            hub.SetValue("air/tas", air.Tas);
            hub.SetValue("air/alpha_deg", air.AlphaDeg);
            hub.SetValue("air/beta_deg", air.BetaDeg);
            hub.SetValue("air/q", air.Q);
            hub.SetValue("air/altitude_m", _plane.AltitudeAgl);
            hub.SetValue("air/vspeed", _plane.VerticalSpeed);
            hub.SetValue("air/load_g", _plane.LoadFactor);

            var r = _plane.LastAero;
            float s = _plane.spec != null ? _plane.spec.WingArea : 0f;
            float denom = air.Q * s;
            hub.SetValue("air/lift_n", r.lift);
            hub.SetValue("air/drag_n", r.drag);
            hub.SetValue("air/cl", denom > 1e-4f ? r.lift / denom : 0f);
            hub.SetValue("air/cd", denom > 1e-4f ? r.drag / denom : 0f);
            hub.SetValue("air/ld", Mathf.Abs(r.drag) > 1e-4f ? r.lift / r.drag : 0f);
            hub.SetValue("air/panels_stalled", r.stalledPanels);
            hub.SetValue("air/stall_margin", r.stallMargin);

            hub.SetValue("air/thrust_n", _plane.Thrust);
            hub.SetValue("air/prop_rps", _plane.PropRevsPerSec);
            hub.SetValue("air/slipstream", r.slipstreamIncrement);
            // The tailplane is surface 1 by construction in DebugPlanes. Reported
            // as 0 rather than 1 when there is no free stream to divide by, because
            // at rest the ratio is genuinely undefined and printing 1 would claim
            // the tail was seeing still air when it is in a 19 m/s blast.
            hub.SetValue("air/eta_tail",
                air.Q > 1e-3f ? _plane.SurfaceDynamicPressure(1) / air.Q : 0f);

            hub.SetValue("cmd/throttle", _plane.ThrottleCommand);
            hub.SetValue("cmd/aileron", _plane.AileronCommand);
            hub.SetValue("cmd/elevator", _plane.ElevatorCommand);
            hub.SetValue("cmd/rudder", _plane.RudderCommand);

            Vector3 w = t.InverseTransformDirection(_body.angularVelocity) * Mathf.Rad2Deg;
            // Body axes: +Z forward, so roll rate is about z and pitch about x.
            hub.SetValue("air/p_rate", w.z);
            hub.SetValue("air/q_rate", w.x);
            hub.SetValue("air/r_rate", w.y);
        }

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
