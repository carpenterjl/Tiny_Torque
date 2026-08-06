using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Telemetry
{
    /// <summary>
    /// The channels a physics measurement needs and the game does not.
    ///
    /// <see cref="SimulationRunner"/> publishes <c>veh/pos_x</c> and
    /// <c>veh/pos_z</c> — a plan view, which is everything a lap time or a
    /// racing line wants and nothing a suspension test does. There is no
    /// <c>pos_y</c>, no roll, no pitch, no per-wheel load and no slip, so
    /// vertical dynamics are currently unobservable: a heave-frequency test, a
    /// weight-distribution check and a load-transfer measurement all need
    /// numbers the hub does not carry.
    ///
    /// <b>Registration timing is load-bearing.</b> <c>CsvLogger.Begin</c>
    /// snapshots the hub's channel list ONCE, while <c>Commit</c> walks the live
    /// set — so a channel registered after logging starts writes a row wider
    /// than its header and silently shifts every column after it. Everything
    /// here is therefore registered from <see cref="Bind"/>, called during the
    /// bootstrap's Awake, before <c>SimulationRunner.Start</c> enables logging.
    ///
    /// Publishing happens in FixedUpdate, after the physics step, so the values
    /// belong to the same tick the runner is about to commit.
    /// </summary>
    public sealed class PhysicsDebugTelemetry : MonoBehaviour
    {
        private SimulationRunner _runner;
        private CarVehicle _car;
        private Rigidbody _body;
        private Vector3 _lastVelWorld;
        private bool _havePrev;

        /// <summary>The chassis origin's height above the wheel-centre plane, so
        /// ride height reads as the tyre's loaded radius rather than as a
        /// position. Matches DebugVehicles' HubY.</summary>
        private const float HubDrop = 0.5615f;

        public void Bind(SimulationRunner runner, CarVehicle car)
        {
            _runner = runner;
            _car = car;
            _body = car != null ? car.GetComponent<Rigidbody>() : null;

            var hub = runner != null ? runner.Hub : null;
            if (hub == null) return;

            // Attitude and the vertical axis the hub is missing entirely.
            hub.RegisterChannel("veh/pos_y");
            hub.RegisterChannel("veh/ride_height");
            hub.RegisterChannel("veh/roll_deg");
            hub.RegisterChannel("veh/pitch_deg");

            // Body-frame accelerations, from delta-v rather than from the IMU:
            // the IMU carries vibration and quantisation models that exist to
            // make a sensor feel real, which is the opposite of what a
            // measurement wants.
            hub.RegisterChannel("veh/a_long");
            hub.RegisterChannel("veh/a_lat");
            hub.RegisterChannel("veh/a_vert");

            hub.RegisterChannel("veh/front_load_pct");
            int n = car != null ? car.WheelCount : 0;
            for (int i = 0; i < n; i++)
            {
                hub.RegisterChannel($"veh/fz_{i}");
                hub.RegisterChannel($"veh/susp_{i}");
                hub.RegisterChannel($"veh/slip_{i}");
                hub.RegisterChannel($"veh/omega_{i}");
                hub.RegisterChannel($"veh/tyre_temp_{i}");
                hub.RegisterChannel($"veh/tyre_press_{i}");
            }
        }

        private void FixedUpdate()
        {
            var hub = _runner != null ? _runner.Hub : null;
            if (hub == null || _car == null || _body == null) return;

            Vector3 p = _car.transform.position;
            hub.SetValue("veh/pos_y", p.y);
            hub.SetValue("veh/ride_height", p.y - HubDrop);

            Vector3 e = _car.transform.rotation.eulerAngles;
            hub.SetValue("veh/roll_deg", Wrap180(e.z));
            hub.SetValue("veh/pitch_deg", Wrap180(e.x));

            // Body-frame acceleration: difference the velocity in the WORLD
            // frame, then rotate the answer into the body frame. It excludes
            // gravity (which a real accelerometer would include) and it is what
            // "0.9 g of lateral acceleration" conventionally means.
            //
            // The order is the whole measurement. Differencing the body-frame
            // velocity instead — (v_local − v_local_prev)/dt — silently subtracts
            // the transport term ω × v, because the frame the components are
            // expressed in is itself rotating. In a steady-state corner the
            // body-frame velocity is very nearly CONSTANT: same speed, still
            // pointing along its own nose. The entire lateral acceleration lives
            // in ω × v, so that form reports approximately nothing for a car
            // pulling its maximum g.
            //
            // It survived as long as it did because every straight-line test
            // agrees with it: at ω ≈ 0 the two forms are identical, so the
            // coastdown, the braking run and the timestep sweep were right either
            // way. Only the skidpad could see it, and it read 0.36 g where the
            // tyre model was delivering close to 1.
            Vector3 velWorld = _body.linearVelocity;
            if (_havePrev && Time.fixedDeltaTime > 0f)
            {
                Vector3 aWorld = (velWorld - _lastVelWorld) / Time.fixedDeltaTime;
                Vector3 a = _car.transform.InverseTransformDirection(aWorld);
                hub.SetValue("veh/a_long", a.z);
                hub.SetValue("veh/a_lat", a.x);
                hub.SetValue("veh/a_vert", a.y);
            }
            _lastVelWorld = velWorld;
            _havePrev = true;

            int n = _car.WheelCount;
            float total = 0f, front = 0f;
            for (int i = 0; i < n; i++)
            {
                float fz = _car.GetSuspensionForce(i);
                total += fz;
                // Front by geometry, not by index: nothing guarantees a wheel
                // order, and a load split read off the wrong two corners is a
                // plausible wrong answer rather than an obvious one.
                if (_car.GetWheelLocalPos(i).z > 0f) front += fz;

                hub.SetValue($"veh/fz_{i}", fz);
                hub.SetValue($"veh/susp_{i}", _car.GetSuspensionCompression(i));
                hub.SetValue($"veh/slip_{i}", _car.WheelSlipRatio(i));
                hub.SetValue($"veh/omega_{i}", _car.WheelOmega(i));
                // Flat at ambient and 0 on a car with no pressure authored, which
                // is how a trace shows at a glance whether the thermal model is
                // even running on this vehicle.
                hub.SetValue($"veh/tyre_temp_{i}", _car.WheelTyreTempC(i));
                hub.SetValue($"veh/tyre_press_{i}", _car.WheelTyrePressKpa(i));
            }
            hub.SetValue("veh/front_load_pct", total > 1f ? 100f * front / total : 0f);
        }

        private static float Wrap180(float deg) => deg > 180f ? deg - 360f : deg;
    }
}
