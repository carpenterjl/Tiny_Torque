using System;
using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Built-in vehicle designs, defined in code so they always exist and can never
    /// be deleted. They surface in the menu and garage pickers with a ★ prefix; the
    /// garage clones one into an editable design (Save writes a normal user copy).
    /// Each showcases a different RC archetype — soft long-travel offroaders through
    /// stiff aero racers — built entirely from the same parts the garage exposes.
    /// </summary>
    public static class VehiclePresets
    {
        /// <summary>Marker prefix that flags a picker entry as a built-in preset.</summary>
        public const string Prefix = "★ ";

        public static readonly (string name, Func<VehicleDesign> build)[] All =
        {
            ("Real Twin 1/10", RealTwin),
            ("TT Coupe",    TTCoupe),
            ("TT Baja",     TTBaja),
            ("TT Patrol",   TTPatrol),
            ("Opus Vector", OpusVector),
        };

        /// <summary>Prefixed display names for the pickers.</summary>
        public static List<string> DisplayNames()
        {
            var list = new List<string>(All.Length);
            foreach (var p in All) list.Add(Prefix + p.name);
            return list;
        }

        /// <summary>Resolve a (possibly prefixed) display name to a fresh design.
        /// Returns null when the name is not a known preset.</summary>
        public static VehicleDesign Resolve(string display)
        {
            if (string.IsNullOrEmpty(display)) return null;
            string bare = display.StartsWith(Prefix) ? display.Substring(Prefix.Length) : display;
            foreach (var p in All)
                if (p.name == bare) return p.build();
            return null;
        }

        public static bool IsPreset(string display) => Resolve(display) != null;

        // ---- shared helpers --------------------------------------------------

        private static WheelSpec Wheel(string name, float x, float z, bool steer, bool powered)
        {
            return new WheelSpec
            {
                name = name,
                localPos = new Vector3(x, -0.045f, z),
                allowsSteering = steer,
                powered = powered,
            };
        }

        private static void AddEncoders(VehicleDesign d)
        {
            string[] tag = { "fl", "fr", "rl", "rr" };
            for (int i = 0; i < d.wheels.Count && i < 4; i++)
                d.sensors.Add(new SensorSpec { name = "enc_" + tag[i], kind = SensorType.Encoder, wheelIndex = i });
        }

        private static SensorSpec Susp(string name, int wheel) =>
            new SensorSpec { name = name, kind = SensorType.Suspension, wheelIndex = wheel };

        // ---- presets ---------------------------------------------------------

        /// <summary>
        /// The high-fidelity validation baseline: the stock 1/10 geometry with
        /// EVERY realism feature enabled at measured-hardware-shaped values —
        /// Coulomb friction, rotor inertia, ESC lag/PWM/deadband, battery sag,
        /// Ackermann, load-sensitive ballooning tires, composite mass/CoM, IMU
        /// vibration, and encoder-realistic wheel_vel. Clone it, then edit each
        /// number toward your physical car until sim traces match real logs.
        /// </summary>
        private static VehicleDesign RealTwin()
        {
            var d = VehicleDesign.Default();   // stock RC already opts into realism
            d.name = "Real Twin 1_10";
            d.bodyColor = new Color(0.15f, 0.75f, 0.55f);
            // Servo torque-speed line (replace with your servo's datasheet).
            d.servoStallNm = 0.883f;   // Savox SC-1251MG class @6 V

            foreach (var w in d.wheels)
            {
                w.loadSensitivity = 0.15f;
                w.balloonPct = 3f;
                if (w.powered)
                {
                    // MotorParams.Default() already carries Coulomb 1 / rotor J /
                    // ESC 1024-step 0.1 V 5 ms — restated here as the numbers to
                    // replace with your motor's measured figures.
                    w.motor.coulombScale = 1f;
                    w.motor.rotorInertia = 5e-6f;
                    w.motor.escPwmSteps = 1024;
                    w.motor.escDeadbandV = 0.10f;
                    w.motor.escTimeConstMs = 5f;
                }
            }

            // ABI wheel_vel behaves like the physical encoders (set your CPR).
            d.wheelVelNoiseStd = 0.05f;
            d.wheelVelQuantCpr = 360;

            // Modest per-sensor degradation on the rig sensors (VL53L1X-ish ToF).
            foreach (var s in d.sensors)
            {
                if (s.kind == SensorType.Tof)
                {
                    s.noiseStd = 0.004f;      // ~4 mm RMS
                    s.updateRateHz = 50f;
                    s.latencyMs = 20f;
                }
                else if (s.kind == SensorType.Encoder)
                {
                    s.updateRateHz = 100f;
                }
            }
            return d;
        }

        // ---- TinyTorque show cars --------------------------------------------
        // The wheel positions, radii and part mount points below are pasted from
        // the JSON block Blender/build_vehicles.py prints — they are derived from
        // the source models' own wheel empties, so the arches align by
        // construction. bodySize is EXACTLY (0.20, 0.10, 0.42): BuildBodyVisual
        // divides by BodyMeshAuthorSize, so this renders the mesh undistorted at
        // its authored proportions. bodyColor (0.8 grey) matches the authored
        // paint channel — the silver the cars were modeled in.

        /// <summary>TT Coupe — RWD street sports coupe, gold trim, glass canopy.</summary>
        private static VehicleDesign TTCoupe()
        {
            var d = new VehicleDesign
            {
                name = "TT Coupe",
                bodyShape = BodyShape.Coupe,
                bodySize = new Vector3(0.20f, 0.10f, 0.42f),
                bodyColor = new Color(0.80f, 0.80f, 0.80f),
                mass = 1.7f,
                steerRate = 560f,
            };
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                float x = (i % 2 == 0) ? -0.0964f : 0.0964f;
                float z = front ? 0.1329f : -0.1329f;
                var w = Wheel(front ? (i == 0 ? "wheel_fl" : "wheel_fr") : (i == 2 ? "wheel_rl" : "wheel_rr"),
                              x, z, front, !front);
                w.radius = 0.0438f;
                w.wheelStyle = 3;   // coupe gold-rim street tyre
                w.suspStiffness = 400f;
                w.suspDampingRatio = 0.7f;
                w.suspTravel = 0.025f;
                d.wheels.Add(w);
            }
            d.sensors.Add(new SensorSpec { name = "cam_front", kind = SensorType.Camera, localPos = new Vector3(0f, 0.10f, 0.05f), aimEuler = new Vector3(6f, 0f, 0f) });
            d.sensors.Add(new SensorSpec { name = "tof_front", kind = SensorType.Tof, localPos = new Vector3(0f, 0.03f, 0.22f), range = 4f });
            AddEncoders(d);
            // The rear-deck whip with the amber tip, exactly where it is modeled.
            d.antennas.Add(new AntennaSpec { name = "whip", localPos = new Vector3(0.0609f, 0.005f, -0.1569f), tiltDeg = 0f, antennaStyle = 1 });
            return d;
        }

        /// <summary>TT Baja — 4WD tube-frame trophy buggy, roof pods, flag whip.</summary>
        private static VehicleDesign TTBaja()
        {
            var d = new VehicleDesign
            {
                name = "TT Baja",
                bodyShape = BodyShape.Baja,
                bodySize = new Vector3(0.20f, 0.10f, 0.42f),
                bodyColor = new Color(0.80f, 0.80f, 0.80f),
                hornStyle = 2,     // air horn — it's the truck of the family
                mass = 1.95f,
                steerRate = 460f,
            };
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                float x = (i % 2 == 0) ? -0.1129f : 0.1129f;
                float z = front ? 0.1372f : -0.1372f;
                var w = Wheel(front ? (i == 0 ? "wheel_fl" : "wheel_fr") : (i == 2 ? "wheel_rl" : "wheel_rr"),
                              x, z, front, true);   // 4WD
                w.radius = 0.0522f;
                w.wheelStyle = 4;   // baja balloon tyre, orange rim
                w.gripMult = 1.15f;
                // Soft and long — but suspLength stays 0: the shocks and arms are
                // authored INTO the body mesh, a procedural strut would double them.
                w.suspStiffness = 200f;
                w.suspDampingRatio = 0.55f;
                w.suspTravel = 0.05f;
                d.wheels.Add(w);
            }
            d.sensors.Add(new SensorSpec { name = "cam_front", kind = SensorType.Camera, localPos = new Vector3(0f, 0.11f, 0.04f), aimEuler = new Vector3(6f, 0f, 0f) });
            d.sensors.Add(new SensorSpec { name = "tof_front", kind = SensorType.Tof, localPos = new Vector3(0f, 0.04f, 0.21f), range = 4f, coneRays = 3, coneAngle = 6f });
            AddEncoders(d);
            d.sensors.Add(Susp("susp_fl", 0));
            // Roof pod cluster + the rear flag whip, at their authored mounts.
            d.lights.Add(new LightSpec { name = "pods", localPos = new Vector3(0f, 0.0397f, 0.0301f), style = 1 });
            d.antennas.Add(new AntennaSpec { name = "flag", localPos = new Vector3(0.0695f, -0.0124f, -0.1416f), tiltDeg = 0f, antennaStyle = 2 });
            return d;
        }

        /// <summary>TT Patrol — RWD sedan with push bar, strobing light bar and twin whips.</summary>
        private static VehicleDesign TTPatrol()
        {
            var d = new VehicleDesign
            {
                name = "TT Patrol",
                bodyShape = BodyShape.Patrol,
                bodySize = new Vector3(0.20f, 0.10f, 0.42f),
                bodyColor = new Color(0.80f, 0.80f, 0.80f),
                hornStyle = 1,     // police siren, obviously
                mass = 1.8f,
                steerRate = 520f,
            };
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                float x = (i % 2 == 0) ? -0.0805f : 0.0805f;
                float z = front ? 0.1242f : -0.1242f;
                var w = Wheel(front ? (i == 0 ? "wheel_fl" : "wheel_fr") : (i == 2 ? "wheel_rl" : "wheel_rr"),
                              x, z, front, !front);
                w.radius = 0.0388f;
                w.wheelStyle = 5;   // chrome steelie
                w.suspStiffness = 350f;
                w.suspDampingRatio = 0.65f;
                w.suspTravel = 0.03f;
                d.wheels.Add(w);
            }
            d.sensors.Add(new SensorSpec { name = "cam_front", kind = SensorType.Camera, localPos = new Vector3(0f, 0.10f, 0.05f), aimEuler = new Vector3(6f, 0f, 0f) });
            d.sensors.Add(new SensorSpec { name = "tof_front", kind = SensorType.Tof, localPos = new Vector3(0f, 0.03f, 0.22f), range = 4f });
            AddEncoders(d);
            // Roof light bar (strobes at runtime) + the twin trunk whips.
            d.lights.Add(new LightSpec { name = "bar", localPos = new Vector3(0f, 0.0341f, -0.0229f), style = 0 });
            d.antennas.Add(new AntennaSpec { name = "whips", localPos = new Vector3(0f, -0.0008f, -0.161f), tiltDeg = 0f, antennaStyle = 3 });
            return d;
        }

        /// <summary>
        /// Opus Vector — an F1TENTH-class 1/10 autonomous research platform, built
        /// entirely from sourced parts. Every number here traces to a row in
        /// <c>Opus_Car_Spec/sim_mapping.md</c>, which is the specification; if the
        /// two disagree, that document wins and this is the bug.
        ///
        /// Real hardware: Castle Creations 1410-3800Kv brushless on 2S through an
        /// 11.2:1 final drive, Savox SC-1251MG steering servo, 2S 5200 mAh LiPo,
        /// three ST VL53L1X rangers, a Bosch BNO055 IMU, four 4096-count magnetic
        /// wheel encoders, a Pi Camera Module 3, and a Raspberry Pi 5 under a
        /// 3D-printed PETG shell. 2.13 kg all up.
        ///
        /// Two things here are load-bearing for the mission firmware and must not
        /// be "tidied up" — both are explained at their site below: the front
        /// wheels' <c>balloonPct = 0</c>, and the encoders' zero rate/latency.
        /// </summary>
        private static VehicleDesign OpusVector()
        {
            var d = new VehicleDesign
            {
                name = "Opus Vector",
                bodyShape = BodyShape.LowRacer,          // flat-deck F1TENTH silhouette
                bodySize = new Vector3(0.200f, 0.090f, 0.420f),
                bodyColor = new Color(0.85f, 0.45f, 0.10f),
                // Chassis only — rolling chassis, motor, ESC, servo, Pi, shell,
                // deck, wiring. Total mass (2.1315 kg) is assembled by
                // MassProperties from this plus every part's own massKg.
                mass = 1.5685f,
                useCompositeMass = true,
                // Savox SC-1251MG @6 V: 667 °/s no-load, 0.883 N·m stall. The old
                // hand-derated 600 °/s is now emergent — the torque-speed line
                // derates the slew under real cornering load instead.
                steerRate = 667f,
                servoStallNm = 0.883f,
                ackermannPct = 100f,
                imuVibration = 0.10f,
                // The ABI wheel_vel[] channel quantised to the real encoder
                // resolution. The mission firmware never reads it — it uses the
                // encoders' own tick counters — but a controller that does should
                // see the same resolution the hardware has.
                wheelVelQuantCpr = 4096,
                controllerDll = "opus_controller.dll",
            };

            // One real motor drives both rear wheels through a differential; the
            // simulator wants one motor per powered wheel. Extensive quantities are
            // therefore HALVED so the pair reproduces the single real motor's
            // torque, current draw and inertia (Opus_Car_Spec/derived_parameters.md
            // §4 works the algebra). Intensive constants — kt, gear ratio — are not.
            var motor = MotorParams.Default();
            motor.maxVoltage = 7.4f;          // 2S LiPo nominal
            motor.kt = 0.0025130f;            // 60/(2π·3800 Kv) — exact from the published Kv
            motor.resistance = 0.060f;        // 2 × the real 30 mΩ (two parallel paths)
            motor.gearRatio = 11.2f;          // 15T pinion / 62T spur × 2.71 transmission
            motor.noLoadCurrent = 0.9f;       // half of 1.8 A
            motor.maxCurrent = 30f;           // half of a 60 A ESC; never binds (peak demand ≈ 20 A total)
            motor.rotorInertia = 3.22e-6f;    // half of 6.44e-6 — one rotor, not two
            motor.viscousDamping = 1e-6f;
            motor.efficiency = 0.85f;
            motor.coulombScale = 1f;
            motor.escPwmSteps = 1024;
            motor.escDeadbandV = 0.10f;
            motor.escTimeConstMs = 5f;

            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                float x = (i % 2 == 0) ? -0.086f : 0.086f;   // 172 mm track
                float z = front ? 0.150f : -0.150f;          // 300 mm wheelbase
                var w = Wheel(front ? (i == 0 ? "wheel_fl" : "wheel_fr")
                                    : (i == 2 ? "wheel_rl" : "wheel_rr"),
                              x, z, front, !front);          // RWD, steered fronts
                w.radius = 0.033f;                           // 66 mm touring tyre
                w.wheelStyle = 0;                            // slick
                w.massKg = 0.060f;                           // wheel + tyre + hub only:
                // the motor is central, not in the wheel, so the simulator's 190 g
                // powered-wheel default would misplace both mass and yaw inertia.
                w.suspStiffness = 320f;
                w.suspDampingRatio = 0.65f;
                w.suspTravel = 0.030f;
                w.suspLength = 0.030f;                       // visible strut, motion ratio 1
                w.localPos.y += w.suspLength;                // raise the mount, keep the hub put
                w.gripMult = 1f;
                w.loadSensitivity = 0.15f;

                if (front)
                {
                    w.steerAngle = 28f;
                    // DO NOT ENABLE BALLOONING ON THE FRONT WHEELS. They are the
                    // odometry source, and ballooning rewrites WheelCollider.radius
                    // at speed while the encoder integrates rpm — so the odometer's
                    // metres-per-revolution constant would silently drift. At
                    // 4.5 m/s a 3 % tyre grows 0.89 %, which is 129 mm of error over
                    // the mission's 14.5 m leg. The rears keep it; nothing measures
                    // distance from them.
                    w.balloonPct = 0f;
                }
                else
                {
                    w.balloonPct = 3f;
                    w.motor = motor;
                }
                d.wheels.Add(w);
            }

            // ST VL53L1X ×3: 4 m range, 27° field of view, 50 Hz, ±25 mm accuracy
            // bound treated as 3σ → σ = 8 mm. These are polled over I²C, so unlike
            // the encoders they genuinely do carry rate and latency.
            SensorSpec Tof(string name, float x, float z, float yaw) => new SensorSpec
            {
                name = name,
                kind = SensorType.Tof,
                localPos = new Vector3(x, 0.030f, z),
                aimEuler = new Vector3(0f, yaw, 0f),
                range = 4f,
                coneRays = 3,
                coneAngle = 27f,
                noiseStd = 0.008f,
                updateRateHz = 50f,
                latencyMs = 20f,
                massKg = 0.002f,
            };
            d.sensors.Add(Tof("tof_front", 0f, 0.210f, 0f));
            d.sensors.Add(Tof("tof_left", -0.060f, 0.190f, -32f));
            d.sensors.Add(Tof("tof_right", 0.060f, 0.190f, 32f));

            // Raspberry Pi Camera Module 3, downsampled to what a perception loop
            // at this scale would actually consume.
            d.sensors.Add(new SensorSpec
            {
                name = "cam_front",
                kind = SensorType.Camera,
                localPos = new Vector3(0f, 0.090f, 0.055f),
                aimEuler = new Vector3(6f, 0f, 0f),
                camWidth = 64, camHeight = 48, camFov = 66f, camRateHz = 10f,
                massKg = 0.012f,
            });

            // 1024-line magnetic quadrature encoders, ×4 = 4096 counts/rev, direct
            // on the wheel. That is 0.0506 mm of travel per count.
            //
            // updateRateHz and latencyMs stay at ZERO deliberately. It looks like
            // missing realism next to the ToF rows above, but a quadrature encoder
            // is a hardware counter the MCU reads synchronously — there is no
            // sampling delay to model. Giving them the ToF's 50 Hz / 20 ms would
            // inject 90 mm of odometry lag at 4.5 m/s and model hardware that does
            // not exist.
            string[] tag = { "fl", "fr", "rl", "rr" };
            for (int i = 0; i < 4; i++)
                d.sensors.Add(new SensorSpec
                {
                    name = "enc_" + tag[i],
                    kind = SensorType.Encoder,
                    wheelIndex = i,
                    cprTicks = 4096,
                    encoderGearRatio = 1f,
                    updateRateHz = 0f,
                    latencyMs = 0f,
                    noiseStd = 0f,
                    massKg = 0.006f,
                });

            // 2S 5200 mAh LiPo, low and slightly rearward under the deck.
            d.batteries.Add(new BatterySpec
            {
                name = "pack_2s",
                localPos = new Vector3(0f, -0.020f, -0.050f),
                massKg = 0.265f,
                nominalV = 7.4f,
                internalR = 0.020f,
                capacitymAh = 5200f,
            });

            // WiFi whips on the rear deck — cosmetic, but they are on the real car.
            d.antennas.Add(new AntennaSpec { name = "ant_l", localPos = new Vector3(-0.055f, 0.090f, -0.140f), tiltDeg = 14f, massKg = 0.008f, mirrorGroup = 1 });
            d.antennas.Add(new AntennaSpec { name = "ant_r", localPos = new Vector3(0.055f, 0.090f, -0.140f), tiltDeg = 14f, massKg = 0.008f, mirrorGroup = 1 });

            return d;
        }
    }
}
