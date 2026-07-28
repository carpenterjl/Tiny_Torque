using AIHWSim.Bridge;
using AIHWSim.Sensors;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Turns a <see cref="VehicleDesign"/> into a live GameObject hierarchy:
    /// unscaled root + Rigidbody + <see cref="CarVehicle"/> configured from the
    /// design, each <see cref="SensorSpec"/> instantiated as a child
    /// <see cref="SensorComponent"/>, and a <see cref="SensorRig"/>. This is the
    /// single construction path shared by the garage preview and the track spawn,
    /// so a vehicle looks and behaves the same in both.
    /// </summary>
    public static class VehicleFactory
    {
        public struct Built
        {
            public CarVehicle car;
            public SensorRig rig;
            public GameObject root;
            public SensorComponent[] sensors;   // in design order (aligns with design.sensors)
            public GameObject[] aeroVisuals;    // in design order (aligns with design.aero)
            public GameObject[] batteryVisuals; // in design order (aligns with design.batteries)
            public GameObject[] antennaVisuals; // in design order (aligns with design.antennas)
            public GameObject[] lightVisuals;   // in design order (aligns with design.lights)
        }

        /// <summary>
        /// Build a vehicle from a design. When <paramref name="previewKinematic"/>
        /// is true the body is frozen (garage showroom); otherwise it's a normal
        /// dynamic vehicle. The caller is responsible for calling rig.Initialize
        /// (the track's SimulationRunner does this; the garage does it directly).
        /// </summary>
        public static Built Build(VehicleDesign design, Vector3 pos, Quaternion rot,
                                  bool previewKinematic = false)
        {
            design ??= VehicleDesign.Default();

            // Build with the root deactivated so CarVehicle.Awake (which reads the
            // fields below and constructs wheels/body) runs only AFTER we've
            // applied the design.
            var root = new GameObject(string.IsNullOrEmpty(design.name) ? "Car" : design.name);
            root.SetActive(false);
            root.transform.SetPositionAndRotation(pos, rot);

            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = previewKinematic;

            var car = root.AddComponent<CarVehicle>();
            car.bodyShape = design.bodyShape;
            car.bodySize = design.bodySize;
            car.bodyColor = design.bodyColor;
            car.bodyMass = design.mass;

            // Painted livery: decode the design's embedded PNG into a texture the
            // body material uses (CarVehicle owns + destroys it). Corrupt/absent
            // data falls back to the plain bodyColor silently.
            if (!string.IsNullOrEmpty(design.liveryPng))
            {
                try
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (ImageConversion.LoadImage(tex, System.Convert.FromBase64String(design.liveryPng)))
                    {
                        tex.wrapMode = TextureWrapMode.Clamp;
                        car.liveryTex = tex;
                    }
                    else Object.Destroy(tex);
                }
                catch (System.FormatException) { }
            }
            car.steerRateDegPerSec = design.steerRate;
            car.servoStallNm = Mathf.Max(0f, design.servoStallNm);
            car.ackermannPct = design.ackermannPct;
            car.imuVibration = design.imuVibration;
            car.wheelVelNoiseStd = design.wheelVelNoiseStd;
            car.wheelVelQuantCpr = design.wheelVelQuantCpr;

            // Composite mass model: total/CoM/inertia from chassis + parts.
            if (design.useCompositeMass)
            {
                var mp = MassProperties.Compute(design);
                car.useCompositeMass = true;
                car.compositeMass = mp.totalMass;
                car.compositeCoM = mp.com;
                car.compositeInertia = mp.inertiaDiag;
            }

            // Wheels are placeable parts. Convert each to a CarWheelConfig; the
            // WheelCollider is built in CarVehicle.Awake (once we activate below).
            var wheelCfgs = new CarWheelConfig[design.wheels.Count];
            for (int i = 0; i < design.wheels.Count; i++)
            {
                var w = design.wheels[i];
                wheelCfgs[i] = new CarWheelConfig
                {
                    localPos = w.localPos,
                    yaw = w.yaw,
                    radius = w.radius,
                    allowsSteering = w.allowsSteering,
                    reverseSteering = w.reverseSteering,
                    steerAngle = w.steerAngle,
                    powered = w.powered,
                    suspStiffness = w.suspStiffness,
                    suspDampingRatio = w.suspDampingRatio,
                    suspTravel = w.suspTravel,
                    suspAngleDeg = w.suspAngleDeg,
                    suspLength = w.suspLength,
                    gripMult = w.gripMult,
                    loadSensitivity = w.loadSensitivity,
                    balloonPct = w.balloonPct,
                    wheelStyle = w.wheelStyle,
                    // Reflected drivetrain inertia J·gear² for powered wheels
                    // (0 on old JSON / unpowered wheels = legacy spin inertia).
                    extraSpinInertia = w.powered && w.motor.rotorInertia > 0f
                        ? w.motor.rotorInertia * w.motor.gearRatio * w.motor.gearRatio
                        : 0f,
                };
            }
            car.wheels = wheelCfgs;

            // A powered wheel gets a MotorPart child bound to its wheel index; the
            // rig discovers it (SENSOR_MOTOR) and gives it an actuator slot.
            for (int i = 0; i < design.wheels.Count; i++)
            {
                var w = design.wheels[i];
                if (!w.powered) continue;
                var mgo = new GameObject("motor_" + w.name);
                mgo.transform.SetParent(root.transform, false);
                var m = mgo.AddComponent<MotorPart>();
                m.sensorName = "motor_" + w.name;
                m.wheelIndex = i;
                m.motor = w.motor;
            }

            // Sensors (ToF / encoder / camera). Motor-kind specs are ignored now
            // that motors live on wheels.
            var sensorList = new System.Collections.Generic.List<SensorComponent>();
            foreach (var spec in design.sensors)
            {
                if (spec.kind == SensorType.Motor) continue;
                sensorList.Add(CreateSensor(root.transform, spec));
            }

            // Batteries: the first pack powers the motor bus (voltage sag model in
            // CarVehicle); each gets a visual child + a BatterySensor so firmware
            // can read the rail. Empty list (old designs) = stiff infinite supply.
            design.batteries ??= new System.Collections.Generic.List<BatterySpec>();
            var batteryVisuals = new GameObject[design.batteries.Count];
            for (int i = 0; i < design.batteries.Count; i++)
            {
                var b = design.batteries[i];
                if (i == 0)
                {
                    car.batteryNominalV = Mathf.Max(0.01f, b.nominalV);
                    car.batteryInternalR = Mathf.Max(0f, b.internalR);
                    car.batteryCapacitymAh = Mathf.Max(0f, b.capacitymAh); // 0 = ∞
                }
                batteryVisuals[i] = CreateBatteryVisual(root.transform, b, withSensor: true);
            }

            // Antennas: cosmetic children (no physics/sensor).
            design.antennas ??= new System.Collections.Generic.List<AntennaSpec>();
            var antennaVisuals = new GameObject[design.antennas.Count];
            for (int i = 0; i < design.antennas.Count; i++)
                antennaVisuals[i] = CreateAntennaVisual(root.transform, design.antennas[i]);

            // Light clusters: cosmetic children like antennas.
            design.lights ??= new System.Collections.Generic.List<LightSpec>();
            var lightVisuals = new GameObject[design.lights.Count];
            for (int i = 0; i < design.lights.Count; i++)
                lightVisuals[i] = CreateLightVisual(root.transform, design.lights[i]);

            // Aero parts: flatten to runtime configs (forces in CarVehicle) and
            // build each part's visual child.
            design.aero ??= new System.Collections.Generic.List<AeroSpec>();
            var aeroCfgs = new AeroConfig[design.aero.Count];
            var aeroVisuals = new GameObject[design.aero.Count];
            for (int i = 0; i < design.aero.Count; i++)
            {
                var a = design.aero[i];
                aeroCfgs[i] = new AeroConfig
                {
                    kind = a.kind,
                    localPos = a.localPos,
                    angleDeg = a.angleDeg,
                    yawDeg = a.yawDeg,
                    sizeScale = Mathf.Clamp(a.sizeScale <= 0f ? 1f : a.sizeScale, 0.6f, 1.6f),
                };
                aeroVisuals[i] = CreateAeroVisual(root.transform, a);
            }
            car.aeroParts = aeroCfgs;

            var rig = root.AddComponent<SensorRig>();

            root.SetActive(true); // CarVehicle.Awake builds everything now

            var built = new Built
            {
                car = car, rig = rig, root = root,
                sensors = sensorList.ToArray(), aeroVisuals = aeroVisuals,
                batteryVisuals = batteryVisuals, antennaVisuals = antennaVisuals,
                lightVisuals = lightVisuals,
            };

            // Unlockable cosmetics last: they mount off the body shell and the
            // wheel holders, which only exist once Awake has run. Visual only —
            // nothing below this line changes how the car drives.
            CosmeticMounts.Apply(built, design);
            return built;
        }

        /// <summary>Instantiate one antenna's cosmetic visual child from a spec.</summary>
        public static GameObject CreateAntennaVisual(Transform parent, AntennaSpec spec)
        {
            var go = new GameObject(spec.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spec.localPos;
            go.transform.localRotation = Quaternion.Euler(0f, spec.yawDeg, 0f);
            PartVisualFactory.BuildAntennaViz(go.transform, spec.tiltDeg,
                Mathf.Clamp(spec.sizeScale <= 0f ? 1f : spec.sizeScale, 0.6f, 1.6f),
                spec.antennaStyle);
            return go;
        }

        /// <summary>Instantiate one light cluster's cosmetic visual child.</summary>
        public static GameObject CreateLightVisual(Transform parent, LightSpec spec)
        {
            var go = new GameObject(spec.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spec.localPos;
            go.transform.localRotation = Quaternion.Euler(0f, spec.yawDeg, 0f);
            PartVisualFactory.BuildLightViz(go.transform, spec.style,
                Mathf.Clamp(spec.sizeScale <= 0f ? 1f : spec.sizeScale, 0.6f, 1.6f));
            return go;
        }

        /// <summary>Instantiate one battery part (visual + optional bus sensor).</summary>
        public static GameObject CreateBatteryVisual(Transform parent, BatterySpec spec, bool withSensor)
        {
            var go = new GameObject(spec.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spec.localPos;
            if (withSensor)
            {
                var bs = go.AddComponent<BatterySensor>();
                bs.sensorName = spec.name;
            }
            PartVisualFactory.BuildBatteryViz(go.transform);
            return go;
        }

        /// <summary>Instantiate one aero part's visual child from a spec.</summary>
        public static GameObject CreateAeroVisual(Transform parent, AeroSpec spec)
        {
            var go = new GameObject(spec.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spec.localPos;
            go.transform.localRotation = Quaternion.Euler(0f, spec.yawDeg, 0f);
            PartVisualFactory.BuildAeroViz(go.transform, spec.kind, spec.angleDeg,
                Mathf.Clamp(spec.sizeScale <= 0f ? 1f : spec.sizeScale, 0.6f, 1.6f));
            return go;
        }

        /// <summary>Instantiate one sensor child from a spec (used by factory + garage editing).</summary>
        public static SensorComponent CreateSensor(Transform parent, SensorSpec spec)
        {
            var go = new GameObject(spec.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = spec.localPos;
            go.transform.localRotation = Quaternion.Euler(spec.aimEuler);

            SensorComponent sc;
            switch (spec.kind)
            {
                case SensorType.Camera:
                {
                    var c = go.AddComponent<CameraSensor>();
                    c.width = spec.camWidth; c.height = spec.camHeight;
                    c.fieldOfView = spec.camFov; c.frameRateHz = spec.camRateHz;
                    sc = c;
                    PartVisualFactory.BuildCameraViz(go.transform);
                    break;
                }
                case SensorType.Encoder:
                {
                    var e = go.AddComponent<WheelEncoderSensor>();
                    e.wheelIndex = spec.wheelIndex;
                    e.countsPerRev = spec.cprTicks;
                    e.gearRatio = spec.encoderGearRatio;
                    sc = e;
                    PartVisualFactory.BuildEncoderViz(go.transform);
                    break;
                }
                case SensorType.Suspension:
                {
                    var s = go.AddComponent<SuspensionSensor>();
                    s.wheelIndex = spec.wheelIndex;
                    sc = s;
                    PartVisualFactory.BuildSuspensionViz(go.transform);
                    break;
                }
                default: // Tof (motors are wheel parts, not sensors)
                {
                    var t = go.AddComponent<TofSensor>();
                    t.maxRange = spec.range;
                    t.coneRays = Mathf.Max(1, spec.coneRays);
                    t.coneAngleDeg = spec.coneAngle;
                    sc = t;
                    PartVisualFactory.BuildTofViz(go.transform);
                    break;
                }
            }

            sc.sensorName = spec.name;

            // Signal realism from the spec: update rate, latency, and noise
            // (all 0 = clean fresh-sample-every-tick = legacy behaviour).
            sc.updateRateHz = spec.updateRateHz;
            sc.latencyMs = spec.latencyMs;
            NoiseModel nm = sc switch
            {
                TofSensor t => t.noise,
                WheelEncoderSensor e => e.noise,
                SuspensionSensor su => su.noise,
                _ => null,
            };
            if (nm != null)
            {
                nm.noiseStdDev = spec.noiseStd;
                nm.quantizationStep = spec.noiseQuant;
                nm.driftRate = spec.driftRate;
            }
            return sc;
        }
    }
}
