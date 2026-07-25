/* =====================================================================
   tt-schema.js — the Tiny Torque VehicleDesign schema as data.

   Mirrors UnitySim/Assets/Scripts/Garage/VehicleDesign.cs (+ MotorModel.cs,
   MassProperties.cs, AeroDynamics.cs) so the browser tools can build JSON
   that Unity's JsonUtility parses into exactly the design the user meant.

   THE THREE RULES THAT MATTER (learned from JsonUtility's behaviour):
     1. VehicleDesign / WheelSpec / SensorSpec / AeroSpec / AntennaSpec /
        BatterySpec are [Serializable] CLASSES. A key absent from the JSON
        keeps the C# field initializer — so omitting a field is safe and
        means "default", not "zero".
     2. MotorParams and MotorDatasheet are STRUCTS. Absent sub-keys become 0,
        NOT the Default(). So a "motor" object must be emitted COMPLETE or
        omitted entirely. This generator always emits it complete.
     3. Enums serialize as ints; Color needs its alpha; a design with
        mass > 50 is hidden by VehicleLibrary.List() (legacy-scale filter).
   ===================================================================== */
(function (global) {
    'use strict';

    // ---- Enums (integer values are the serialized form) ----------------

    const BodyShape = { Box: 0, Wedge: 1, Buggy: 2, Shell: 3, LowRacer: 4 };
    const BodyShapeNames = ['Box', 'Wedge', 'Buggy', 'Shell', 'LowRacer'];
    const BodyShapeInfo = [
        { id: 0, name: 'Box', blurb: 'Bluff crate. Highest drag, no built-in downforce — the utility shape.', cd: 0.90, clA: 0 },
        { id: 1, name: 'Wedge', blurb: 'Simple ramped nose. Mild drag win and a little inherent downforce.', cd: 0.65, clA: 0.002 },
        { id: 2, name: 'Buggy', blurb: 'Tall off-road cab with flared arches. Draggy but the right look for dirt.', cd: 0.80, clA: 0 },
        { id: 3, name: 'Shell', blurb: 'Touring-car lexan shell. Low drag, moderate downforce — the Tamiya look.', cd: 0.45, clA: 0.004 },
        { id: 4, name: 'LowRacer', blurb: 'F1TENTH-style flat deck. Lowest practical drag, most downforce. Best for a research car.', cd: 0.55, clA: 0.006 }
    ];

    // NOTE: there is deliberately no sensor type 0.
    const SensorType = { Tof: 1, Encoder: 2, Motor: 3, Imu: 4, Camera: 5, Suspension: 6, Battery: 7 };
    const SensorTypeNames = { 1: 'ToF ranger', 2: 'Wheel encoder', 3: 'Motor feedback', 4: 'IMU', 5: 'Camera', 6: 'Suspension', 7: 'Battery' };
    /* Placeable-from-JSON sensor kinds. Motor feedback comes from a powered
       wheel's motor (not a placed part); IMU is built into the chassis. */
    const PlaceableSensorKinds = [1, 2, 5, 6, 7];

    const AeroKind = { Wing: 0, Splitter: 1, SideDam: 2, Canard: 3 };
    const AeroKindNames = ['Wing', 'Splitter', 'SideDam', 'Canard'];

    const WheelStyleNames = ['Slick', 'Knobby', 'Rally'];
    const MotorEntryMode = { Constants: 0, Datasheet: 1 };

    // ---- Defaults (field initializers, verbatim from the C#) ------------

    const MOTOR_DEFAULT = {
        maxVoltage: 7.4,
        kt: 0.003,
        resistance: 0.09,
        gearRatio: 8,
        noLoadCurrent: 1.2,
        viscousDamping: 1e-6,
        efficiency: 0.85,
        maxCurrent: 40,
        coulombScale: 1,
        rotorInertia: 5e-6,
        escPwmSteps: 1024,
        escDeadbandV: 0.10,
        escTimeConstMs: 5,
        escSlewVPerS: 0,
        escDragBrakePct: 0,
        escBrakeStrengthPct: 100,
        escReverseLockMs: 150
    };
    // Serialization order — must match MotorParams field declaration order.
    const MOTOR_FIELDS = [
        'maxVoltage', 'kt', 'resistance', 'gearRatio', 'noLoadCurrent',
        'viscousDamping', 'efficiency', 'maxCurrent', 'coulombScale',
        'rotorInertia', 'escPwmSteps', 'escDeadbandV', 'escTimeConstMs',
        'escSlewVPerS', 'escDragBrakePct', 'escBrakeStrengthPct', 'escReverseLockMs'
    ];
    const MOTOR_INT_FIELDS = ['escPwmSteps'];

    const DATASHEET_DEFAULT = { nominalVoltage: 0, stallTorque: 0, noLoadRpm: 0, noLoadCurrent: 0 };
    const DATASHEET_FIELDS = ['nominalVoltage', 'stallTorque', 'noLoadRpm', 'noLoadCurrent'];

    const WHEEL_DEFAULT = {
        name: 'wheel',
        localPos: { x: 0.083, y: -0.045, z: 0.152 },
        yaw: 0,
        radius: 0.033,
        wheelStyle: 0,
        mirrorGroup: -1,
        allowsSteering: false,
        reverseSteering: false,
        steerAngle: 28,
        suspStiffness: 300,
        suspDampingRatio: 0,
        suspTravel: 0.03,
        suspAngleDeg: 0,
        suspLength: 0,
        gripMult: 1,
        loadSensitivity: 0,
        balloonPct: 0,
        massKg: 0,
        powered: false,
        motor: null,            // filled from MOTOR_DEFAULT on create
        motorDatasheet: null,
        motorEntryMode: 0
    };

    const SENSOR_DEFAULT = {
        name: 'sensor',
        kind: 1,
        localPos: { x: 0, y: 0.05, z: 0.18 },
        aimEuler: { x: 0, y: 0, z: 0 },
        mirrorGroup: -1,
        range: 4,
        coneRays: 1,
        coneAngle: 8,
        wheelIndex: 0,
        cprTicks: 360,
        encoderGearRatio: 1,
        massKg: 0,
        noiseStd: 0,
        noiseQuant: 0,
        driftRate: 0,
        updateRateHz: 0,
        latencyMs: 0,
        camWidth: 64,
        camHeight: 48,
        camFov: 60,
        camRateHz: 10
    };

    const AERO_DEFAULT = {
        name: 'wing', kind: 0,
        localPos: { x: 0, y: 0.08, z: -0.20 },
        yawDeg: 0, mirrorGroup: -1, angleDeg: 8, sizeScale: 1, massKg: 0
    };

    const ANTENNA_DEFAULT = {
        name: 'antenna',
        localPos: { x: 0, y: 0.09, z: -0.14 },
        yawDeg: 0, tiltDeg: 15, sizeScale: 1, mirrorGroup: -1, massKg: 0
    };

    const BATTERY_DEFAULT = {
        name: 'battery',
        localPos: { x: 0, y: -0.02, z: -0.05 },
        mirrorGroup: -1, massKg: 0.18, nominalV: 7.4, internalR: 0.03, capacitymAh: 0
    };

    const DESIGN_DEFAULT = {
        name: 'New Vehicle',
        bodyShape: 0,
        bodySize: { x: 0.20, y: 0.10, z: 0.42 },
        bodyColor: { r: 0.20, g: 0.55, b: 0.95, a: 1 },
        liveryPng: '',
        mass: 1.6,
        useCompositeMass: false,
        steerRate: 480,
        servoStallNm: 0,
        ackermannPct: 0,
        controllerDll: '',
        imuVibration: 0,
        wheelVelNoiseStd: 0,
        wheelVelQuantCpr: 0,
        wheels: [], sensors: [], aero: [], batteries: [], antennas: []
    };

    // ---- Auto part masses (MassProperties.cs) ---------------------------

    const AutoMass = {
        Tof: 0.005, Camera: 0.015, Encoder: 0.008, SuspSensor: 0.006,
        Wheel: 0.030, PoweredWheel: 0.190, Wing: 0.010, SmallAero: 0.008,
        Antenna: 0.008
    };

    // ---- Slider ranges (GarageUI.cs) — used for validation --------------
    // [min, max, unit, label]
    const Range = {
        // vehicle
        'bodySize.x': [0.12, 0.35, 'm', 'Body width'],
        'bodySize.y': [0.04, 0.18, 'm', 'Body height'],
        'bodySize.z': [0.25, 0.60, 'm', 'Body length'],
        'mass': [0.3, 5, 'kg', 'Mass'],
        'steerRate': [60, 1200, '°/s', 'Servo speed'],
        'servoStallNm': [0, 2, 'N·m', 'Servo stall torque'],
        'ackermannPct': [0, 100, '%', 'Ackermann'],
        'imuVibration': [0, 0.5, '', 'IMU vibration'],
        'wheelVelNoiseStd': [0, 2, 'rad/s', 'wheel_vel noise σ'],
        'wheelVelQuantCpr': [0, 2048, 'cpr', 'wheel_vel quantization'],
        // wheel
        'wheel.localPos.x': [-0.20, 0.20, 'm', 'Wheel X'],
        'wheel.localPos.y': [-0.09, 0.03, 'm', 'Wheel Y'],
        'wheel.localPos.z': [-0.32, 0.32, 'm', 'Wheel Z'],
        'wheel.yaw': [-180, 180, '°', 'Wheel heading'],
        'wheel.radius': [0.02, 0.07, 'm', 'Wheel radius'],
        'wheel.steerAngle': [5, 45, '°', 'Max steer angle'],
        'wheel.suspStiffness': [50, 2000, 'N/m', 'Spring rate'],
        'wheel.suspDampingRatio': [0.1, 2, 'ζ', 'Damping ratio'],
        'wheel.suspTravel': [0.01, 0.08, 'm', 'Suspension travel'],
        'wheel.suspAngleDeg': [-30, 30, '°', 'Strut angle'],
        'wheel.suspLength': [0, 0.060, 'm', 'Strut length'],
        'wheel.gripMult': [0.3, 2, '×', 'Grip multiplier'],
        'wheel.loadSensitivity': [0, 0.4, '', 'Load sensitivity'],
        'wheel.balloonPct': [0, 12, '%', 'Tyre ballooning'],
        'wheel.massKg': [0, 0.400, 'kg', 'Wheel mass'],
        // motor constants
        'motor.maxVoltage': [3.7, 12, 'V', 'Supply rail'],
        'motor.kt': [0.001, 0.02, 'N·m/A', 'Torque constant Kt'],
        'motor.resistance': [0.02, 1, 'Ω', 'Winding resistance R'],
        'motor.gearRatio': [1, 30, ':1', 'Gear ratio'],
        'motor.efficiency': [0.3, 1, '', 'Drivetrain efficiency'],
        'motor.noLoadCurrent': [0, 5, 'A', 'No-load current I₀'],
        'motor.viscousDamping': [0, 5e-4, 'N·m·s/rad', 'Viscous damping'],
        'motor.maxCurrent': [0, 100, 'A', 'ESC current limit'],
        'motor.coulombScale': [0, 2, '×', 'Coulomb friction scale'],
        'motor.rotorInertia': [0, 20e-6, 'kg·m²', 'Rotor inertia J'],
        'motor.escDeadbandV': [0, 0.5, 'V', 'ESC deadband'],
        'motor.escTimeConstMs': [0, 20, 'ms', 'ESC lag'],
        'motor.escSlewVPerS': [0, 500, 'V/s', 'ESC slew limit'],
        'motor.escDragBrakePct': [0, 30, '%', 'Drag brake'],
        'motor.escBrakeStrengthPct': [0, 100, '%', 'Brake strength'],
        'motor.escReverseLockMs': [0, 500, 'ms', 'Reverse lockout'],
        // datasheet
        'ds.nominalVoltage': [3.7, 12, 'V', 'Nominal voltage'],
        'ds.stallTorque': [0.02, 1.5, 'N·m', 'Stall torque'],
        'ds.noLoadRpm': [5000, 40000, 'rpm', 'No-load speed'],
        'ds.noLoadCurrent': [0, 5, 'A', 'No-load current'],
        // sensor
        'sensor.localPos.x': [-0.18, 0.18, 'm', 'Sensor X'],
        'sensor.localPos.y': [-0.05, 0.25, 'm', 'Sensor Y'],
        'sensor.localPos.z': [-0.30, 0.30, 'm', 'Sensor Z'],
        'sensor.aimEuler.y': [-180, 180, '°', 'Aim yaw'],
        'sensor.aimEuler.x': [-90, 90, '°', 'Aim pitch'],
        'sensor.range': [0.2, 8, 'm', 'Max range'],
        'sensor.coneRays': [1, 7, '', 'Cone rays'],
        'sensor.coneAngle': [0, 30, '°', 'Cone angle'],
        'sensor.cprTicks': [16, 2048, 'cpr', 'Counts per rev'],
        'sensor.encoderGearRatio': [1, 50, ':1', 'Encoder gear ratio'],
        'sensor.camWidth': [16, 128, 'px', 'Camera width'],
        'sensor.camHeight': [16, 96, 'px', 'Camera height'],
        'sensor.camFov': [20, 110, '°', 'Camera FOV'],
        'sensor.camRateHz': [1, 30, 'Hz', 'Camera rate'],
        'sensor.massKg': [0, 0.100, 'kg', 'Sensor mass'],
        'sensor.noiseStd': [0, 0.5, '', 'Noise σ'],
        'sensor.noiseQuant': [0, 0.1, '', 'Quantization'],
        'sensor.driftRate': [0, 0.05, '/√s', 'Drift rate'],
        'sensor.updateRateHz': [0, 100, 'Hz', 'Update rate'],
        'sensor.latencyMs': [0, 100, 'ms', 'Latency'],
        // aero / antenna / battery
        'aero.angleDeg': [0, 20, '°', 'Attack angle'],
        'aero.sizeScale': [0.6, 1.6, '×', 'Size scale'],
        'aero.massKg': [0, 0.100, 'kg', 'Aero mass'],
        'antenna.tiltDeg': [0, 45, '°', 'Antenna tilt'],
        'antenna.sizeScale': [0.6, 1.6, '×', 'Size scale'],
        'antenna.massKg': [0, 0.060, 'kg', 'Antenna mass'],
        'battery.localPos.x': [-0.15, 0.15, 'm', 'Battery X'],
        'battery.localPos.y': [-0.06, 0.15, 'm', 'Battery Y'],
        'battery.localPos.z': [-0.30, 0.30, 'm', 'Battery Z'],
        'battery.massKg': [0.080, 0.350, 'kg', 'Pack mass'],
        'battery.internalR': [0.005, 0.1, 'Ω', 'Internal resistance'],
        'battery.capacitymAh': [0, 8000, 'mAh', 'Capacity']
    };

    // ---- Helpers --------------------------------------------------------

    function clone(o) { return JSON.parse(JSON.stringify(o)); }
    function v3(x, y, z) { return { x: x, y: y, z: z }; }

    function newMotor(over) { return Object.assign(clone(MOTOR_DEFAULT), over || {}); }
    function newDatasheet(over) { return Object.assign(clone(DATASHEET_DEFAULT), over || {}); }

    function newWheel(over) {
        const w = clone(WHEEL_DEFAULT);
        w.motor = newMotor();
        w.motorDatasheet = newDatasheet();
        return Object.assign(w, over || {});
    }
    function newSensor(over) { return Object.assign(clone(SENSOR_DEFAULT), over || {}); }
    function newAero(over) { return Object.assign(clone(AERO_DEFAULT), over || {}); }
    function newAntenna(over) { return Object.assign(clone(ANTENNA_DEFAULT), over || {}); }
    function newBattery(over) { return Object.assign(clone(BATTERY_DEFAULT), over || {}); }

    function newDesign(over) {
        const d = clone(DESIGN_DEFAULT);
        return Object.assign(d, over || {});
    }

    /* The in-game stock car (VehicleDesign.Default()), used as the starting
       point of the wizard and the bundled example for the other tools. */
    function stockDesign() {
        const d = newDesign({
            name: 'Stock RC',
            bodyShape: BodyShape.LowRacer,
            bodySize: v3(0.20, 0.09, 0.42),
            ackermannPct: 100,
            useCompositeMass: true,
            mass: 1.0,
            imuVibration: 0.1
        });
        d.wheels = [
            newWheel({ name: 'wheel_fl', localPos: v3(-0.083, -0.015, 0.152), suspLength: 0.03, allowsSteering: true }),
            newWheel({ name: 'wheel_fr', localPos: v3(0.083, -0.015, 0.152), suspLength: 0.03, allowsSteering: true }),
            newWheel({ name: 'wheel_rl', localPos: v3(-0.083, -0.015, -0.152), suspLength: 0.03, powered: true }),
            newWheel({ name: 'wheel_rr', localPos: v3(0.083, -0.015, -0.152), suspLength: 0.03, powered: true })
        ];
        d.sensors = [
            newSensor({ name: 'cam_front', kind: SensorType.Camera, localPos: v3(0, 0.09, 0.05), aimEuler: v3(8, 0, 0), camWidth: 64, camHeight: 48, camFov: 62, camRateHz: 10 }),
            newSensor({ name: 'tof_front', kind: SensorType.Tof, localPos: v3(0, 0.03, 0.21), range: 4, coneRays: 3, coneAngle: 6 }),
            newSensor({ name: 'tof_left', kind: SensorType.Tof, localPos: v3(-0.06, 0.03, 0.19), aimEuler: v3(0, -32, 0), range: 4 }),
            newSensor({ name: 'tof_right', kind: SensorType.Tof, localPos: v3(0.06, 0.03, 0.19), aimEuler: v3(0, 32, 0), range: 4 }),
            newSensor({ name: 'enc_fl', kind: SensorType.Encoder, wheelIndex: 0 }),
            newSensor({ name: 'enc_fr', kind: SensorType.Encoder, wheelIndex: 1 }),
            newSensor({ name: 'enc_rl', kind: SensorType.Encoder, wheelIndex: 2 }),
            newSensor({ name: 'enc_rr', kind: SensorType.Encoder, wheelIndex: 3 })
        ];
        d.batteries = [newBattery()];
        d.antennas = [
            newAntenna({ name: 'ant_l', localPos: v3(-0.05, 0.09, -0.15), yawDeg: -12, tiltDeg: 16, mirrorGroup: 1 }),
            newAntenna({ name: 'ant_r', localPos: v3(0.05, 0.09, -0.15), yawDeg: 12, tiltDeg: 16, mirrorGroup: 1 })
        ];
        return d;
    }

    // ---- Reading a design loaded from disk ------------------------------

    /* Fill every absent key with its C# default so the tools can edit a
       partially-specified file (which is what most saved vehicles are).
       Struct members are filled whole, mirroring rule 2 in reverse: if the
       file HAS a motor object we keep its values but complete the shape. */
    function normalize(raw) {
        const d = Object.assign(clone(DESIGN_DEFAULT), raw || {});
        d.bodySize = Object.assign(clone(DESIGN_DEFAULT.bodySize), raw && raw.bodySize);
        d.bodyColor = Object.assign(clone(DESIGN_DEFAULT.bodyColor), raw && raw.bodyColor);
        d.wheels = (raw && raw.wheels || []).map(function (w) {
            const o = Object.assign(clone(WHEEL_DEFAULT), w);
            o.localPos = Object.assign(clone(WHEEL_DEFAULT.localPos), w.localPos);
            // A file that carries a motor object carries it complete (Unity wrote
            // it); one that doesn't gets Default() — matching JsonUtility's rule.
            o.motor = w.motor ? Object.assign(clone(MOTOR_DEFAULT), w.motor) : newMotor();
            o.motorDatasheet = Object.assign(clone(DATASHEET_DEFAULT), w.motorDatasheet);
            return o;
        });
        d.sensors = (raw && raw.sensors || []).map(function (s) {
            const o = Object.assign(clone(SENSOR_DEFAULT), s);
            o.localPos = Object.assign(clone(SENSOR_DEFAULT.localPos), s.localPos);
            o.aimEuler = Object.assign(clone(SENSOR_DEFAULT.aimEuler), s.aimEuler);
            return o;
        });
        d.aero = (raw && raw.aero || []).map(function (a) {
            const o = Object.assign(clone(AERO_DEFAULT), a);
            o.localPos = Object.assign(clone(AERO_DEFAULT.localPos), a.localPos);
            return o;
        });
        d.batteries = (raw && raw.batteries || []).map(function (b) {
            const o = Object.assign(clone(BATTERY_DEFAULT), b);
            o.localPos = Object.assign(clone(BATTERY_DEFAULT.localPos), b.localPos);
            return o;
        });
        d.antennas = (raw && raw.antennas || []).map(function (a) {
            const o = Object.assign(clone(ANTENNA_DEFAULT), a);
            o.localPos = Object.assign(clone(ANTENNA_DEFAULT.localPos), a.localPos);
            return o;
        });
        return d;
    }

    // ---- Emitting JSON Unity will parse ---------------------------------

    /* Unity writes floats with a trailing ".0" for whole numbers; JsonUtility
       parses either form, but matching the convention makes diffs readable. */
    function num(v, isInt) {
        if (v === undefined || v === null || !isFinite(v)) v = 0;
        if (isInt) return String(Math.round(v));
        if (Number.isInteger(v)) return v.toFixed(1);
        // Trim float noise (0.1+0.2 artefacts) without losing small constants.
        let s = Number(v.toPrecision(9)).toString();
        if (s.indexOf('e') >= 0) return s;           // 5e-6 etc. — JsonUtility reads it
        if (s.indexOf('.') < 0) s += '.0';
        return s;
    }

    function emitVec(v, ind) {
        const p = ' '.repeat(ind), p2 = ' '.repeat(ind + 4);
        return '{\n' +
            p2 + '"x": ' + num(v.x) + ',\n' +
            p2 + '"y": ' + num(v.y) + ',\n' +
            p2 + '"z": ' + num(v.z) + '\n' + p + '}';
    }
    function emitColor(c, ind) {
        const p = ' '.repeat(ind), p2 = ' '.repeat(ind + 4);
        return '{\n' +
            p2 + '"r": ' + num(c.r) + ',\n' +
            p2 + '"g": ' + num(c.g) + ',\n' +
            p2 + '"b": ' + num(c.b) + ',\n' +
            p2 + '"a": ' + num(c.a === undefined ? 1 : c.a) + '\n' + p + '}';
    }
    function emitStr(s) { return JSON.stringify(s === undefined || s === null ? '' : String(s)); }

    function emitObject(pairs, ind) {
        const p = ' '.repeat(ind), p2 = ' '.repeat(ind + 4);
        return '{\n' + pairs.map(function (kv) { return p2 + '"' + kv[0] + '": ' + kv[1]; }).join(',\n') + '\n' + p + '}';
    }
    function emitArray(items, ind) {
        if (!items.length) return '[]';
        const p = ' '.repeat(ind);
        return '[\n' + items.map(function (s) { return ' '.repeat(ind + 4) + s; }).join(',\n') + '\n' + p + ']';
    }

    function emitMotor(m, ind) {
        return emitObject(MOTOR_FIELDS.map(function (f) {
            return [f, num(m[f], MOTOR_INT_FIELDS.indexOf(f) >= 0)];
        }), ind);
    }
    function emitDatasheet(ds, ind) {
        return emitObject(DATASHEET_FIELDS.map(function (f) { return [f, num(ds[f])]; }), ind);
    }

    function emitWheel(w, ind) {
        const i2 = ind + 4;
        return emitObject([
            ['name', emitStr(w.name)],
            ['localPos', emitVec(w.localPos, i2)],
            ['yaw', num(w.yaw)],
            ['radius', num(w.radius)],
            ['wheelStyle', num(w.wheelStyle, true)],
            ['mirrorGroup', num(w.mirrorGroup, true)],
            ['allowsSteering', w.allowsSteering ? 'true' : 'false'],
            ['reverseSteering', w.reverseSteering ? 'true' : 'false'],
            ['steerAngle', num(w.steerAngle)],
            ['suspStiffness', num(w.suspStiffness)],
            ['suspDampingRatio', num(w.suspDampingRatio)],
            ['suspTravel', num(w.suspTravel)],
            ['suspAngleDeg', num(w.suspAngleDeg)],
            ['suspLength', num(w.suspLength)],
            ['gripMult', num(w.gripMult)],
            ['loadSensitivity', num(w.loadSensitivity)],
            ['balloonPct', num(w.balloonPct)],
            ['massKg', num(w.massKg)],
            ['powered', w.powered ? 'true' : 'false'],
            ['motor', emitMotor(w.motor || MOTOR_DEFAULT, i2)],
            ['motorDatasheet', emitDatasheet(w.motorDatasheet || DATASHEET_DEFAULT, i2)],
            ['motorEntryMode', num(w.motorEntryMode, true)]
        ], ind);
    }

    function emitSensor(s, ind) {
        const i2 = ind + 4;
        return emitObject([
            ['name', emitStr(s.name)],
            ['kind', num(s.kind, true)],
            ['localPos', emitVec(s.localPos, i2)],
            ['aimEuler', emitVec(s.aimEuler, i2)],
            ['mirrorGroup', num(s.mirrorGroup, true)],
            ['range', num(s.range)],
            ['coneRays', num(s.coneRays, true)],
            ['coneAngle', num(s.coneAngle)],
            ['wheelIndex', num(s.wheelIndex, true)],
            ['cprTicks', num(s.cprTicks, true)],
            ['encoderGearRatio', num(s.encoderGearRatio)],
            ['massKg', num(s.massKg)],
            ['noiseStd', num(s.noiseStd)],
            ['noiseQuant', num(s.noiseQuant)],
            ['driftRate', num(s.driftRate)],
            ['updateRateHz', num(s.updateRateHz)],
            ['latencyMs', num(s.latencyMs)],
            ['camWidth', num(s.camWidth, true)],
            ['camHeight', num(s.camHeight, true)],
            ['camFov', num(s.camFov)],
            ['camRateHz', num(s.camRateHz)]
        ], ind);
    }

    function emitAero(a, ind) {
        return emitObject([
            ['name', emitStr(a.name)],
            ['kind', num(a.kind, true)],
            ['localPos', emitVec(a.localPos, ind + 4)],
            ['yawDeg', num(a.yawDeg)],
            ['mirrorGroup', num(a.mirrorGroup, true)],
            ['angleDeg', num(a.angleDeg)],
            ['sizeScale', num(a.sizeScale)],
            ['massKg', num(a.massKg)]
        ], ind);
    }
    function emitAntenna(a, ind) {
        return emitObject([
            ['name', emitStr(a.name)],
            ['localPos', emitVec(a.localPos, ind + 4)],
            ['yawDeg', num(a.yawDeg)],
            ['tiltDeg', num(a.tiltDeg)],
            ['sizeScale', num(a.sizeScale)],
            ['mirrorGroup', num(a.mirrorGroup, true)],
            ['massKg', num(a.massKg)]
        ], ind);
    }
    function emitBattery(b, ind) {
        return emitObject([
            ['name', emitStr(b.name)],
            ['localPos', emitVec(b.localPos, ind + 4)],
            ['mirrorGroup', num(b.mirrorGroup, true)],
            ['massKg', num(b.massKg)],
            ['nominalV', num(b.nominalV)],
            ['internalR', num(b.internalR)],
            ['capacitymAh', num(b.capacitymAh)]
        ], ind);
    }

    /* Serialize a design to the JSON text that goes in the Vehicles folder.
       Field order follows the C# declaration order (what JsonUtility itself
       writes), so a file round-tripped through the game diffs cleanly. */
    function toJson(design) {
        const d = normalize(design);
        const parts = [
            ['name', emitStr(d.name)],
            ['bodyShape', num(d.bodyShape, true)],
            ['bodySize', emitVec(d.bodySize, 4)],
            ['bodyColor', emitColor(d.bodyColor, 4)],
            ['liveryPng', emitStr(d.liveryPng)],
            ['mass', num(d.mass)],
            ['useCompositeMass', d.useCompositeMass ? 'true' : 'false'],
            ['steerRate', num(d.steerRate)],
            ['servoStallNm', num(d.servoStallNm)],
            ['ackermannPct', num(d.ackermannPct)],
            ['controllerDll', emitStr(d.controllerDll)],
            ['imuVibration', num(d.imuVibration)],
            ['wheelVelNoiseStd', num(d.wheelVelNoiseStd)],
            ['wheelVelQuantCpr', num(d.wheelVelQuantCpr, true)],
            ['wheels', emitArray(d.wheels.map(function (w) { return emitWheel(w, 8); }), 4)],
            ['sensors', emitArray(d.sensors.map(function (s) { return emitSensor(s, 8); }), 4)],
            ['aero', emitArray(d.aero.map(function (a) { return emitAero(a, 8); }), 4)],
            ['batteries', emitArray(d.batteries.map(function (b) { return emitBattery(b, 8); }), 4)],
            ['antennas', emitArray(d.antennas.map(function (a) { return emitAntenna(a, 8); }), 4)]
        ];
        return emitObject(parts, 0) + '\n';
    }

    /* VehicleLibrary.Sanitize: invalid filename chars → '_', trimmed;
       empty → "vehicle". The file name must match the design name or the
       garage list and the file drift apart. */
    function fileNameFor(design) {
        let n = String((design && design.name) || '').replace(/[<>:"/\\|?*\x00-\x1f]/g, '_').trim();
        if (!n) n = 'vehicle';
        return n + '.json';
    }

    // ---- Validation -----------------------------------------------------

    function rangeCheck(out, key, value, where) {
        const r = Range[key];
        if (!r) return;
        if (value < r[0] || value > r[1]) {
            out.push({
                level: 'warn',
                msg: where + ': ' + r[3] + ' = ' + value + (r[2] ? ' ' + r[2] : '') +
                     ' is outside the garage slider range ' + r[0] + '…' + r[1] +
                     '. The game will still load it, but it is untested territory.'
            });
        }
    }

    /* Returns [{level:'error'|'warn'|'info', msg}]. Errors mean the design
       will not work as intended in game; warnings are worth a second look. */
    function validate(design) {
        const d = normalize(design);
        const out = [];

        if (!String(d.name || '').trim()) out.push({ level: 'error', msg: 'The design has no name — it drives both the file name and the garage list entry.' });
        if (d.mass > 50) out.push({ level: 'error', msg: 'Mass ' + d.mass + ' kg exceeds 50 kg, so VehicleLibrary hides this design from every picker (the legacy full-scale filter). Keep an RC car under 5 kg.' });

        rangeCheck(out, 'bodySize.x', d.bodySize.x, 'Body');
        rangeCheck(out, 'bodySize.y', d.bodySize.y, 'Body');
        rangeCheck(out, 'bodySize.z', d.bodySize.z, 'Body');
        rangeCheck(out, 'mass', d.mass, 'Body');
        rangeCheck(out, 'steerRate', d.steerRate, 'Steering');
        rangeCheck(out, 'servoStallNm', d.servoStallNm, 'Steering');
        rangeCheck(out, 'ackermannPct', d.ackermannPct, 'Steering');

        if (!d.wheels.length) out.push({ level: 'error', msg: 'No wheels: the car has nothing to stand on.' });

        let powered = 0, steered = 0;
        d.wheels.forEach(function (w, i) {
            const where = 'Wheel ' + i + ' (' + w.name + ')';
            if (w.powered) powered++;
            if (w.allowsSteering) steered++;
            rangeCheck(out, 'wheel.localPos.x', w.localPos.x, where);
            rangeCheck(out, 'wheel.localPos.y', w.localPos.y, where);
            rangeCheck(out, 'wheel.localPos.z', w.localPos.z, where);
            rangeCheck(out, 'wheel.radius', w.radius, where);
            rangeCheck(out, 'wheel.suspStiffness', w.suspStiffness, where);
            rangeCheck(out, 'wheel.suspTravel', w.suspTravel, where);
            rangeCheck(out, 'wheel.gripMult', w.gripMult, where);
            if (w.suspDampingRatio !== 0) rangeCheck(out, 'wheel.suspDampingRatio', w.suspDampingRatio, where);
            if (w.powered) {
                const m = w.motor;
                rangeCheck(out, 'motor.kt', m.kt, where + ' motor');
                rangeCheck(out, 'motor.resistance', m.resistance, where + ' motor');
                rangeCheck(out, 'motor.gearRatio', m.gearRatio, where + ' motor');
                rangeCheck(out, 'motor.maxVoltage', m.maxVoltage, where + ' motor');
                if (m.kt <= 0) out.push({ level: 'error', msg: where + ': motor Kt must be > 0.' });
                if (m.resistance <= 0) out.push({ level: 'error', msg: where + ': motor resistance must be > 0.' });
                if (w.motorEntryMode === 1) {
                    const ds = w.motorDatasheet;
                    if (!(ds.nominalVoltage > 0 && ds.stallTorque > 0 && ds.noLoadRpm > 0)) {
                        out.push({ level: 'error', msg: where + ': motorEntryMode is Datasheet but the datasheet figures are incomplete. Fill nominal voltage, stall torque and no-load rpm, or switch back to Constants.' });
                    }
                }
            }
        });
        if (!powered) out.push({ level: 'error', msg: 'No powered wheel: the car cannot move. Give at least one wheel a motor.' });
        if (!steered && d.ackermannPct > 0) out.push({ level: 'warn', msg: 'Ackermann is set but no wheel steers — the setting has no effect.' });
        if (!steered) out.push({ level: 'warn', msg: 'No steering wheel: you can only drive in a straight line (fine for a differential-steer layout with independent motor voltages).' });

        const names = {};
        d.sensors.forEach(function (s, i) {
            const where = 'Sensor ' + i + ' (' + s.name + ')';
            if (names[s.name]) out.push({ level: 'warn', msg: where + ': duplicate sensor name. Firmware binds sensors by name in ctrl_configure, so duplicates make one of them unreachable.' });
            names[s.name] = true;
            if (!/^[A-Za-z0-9_]{1,31}$/.test(s.name || '')) {
                out.push({ level: 'warn', msg: where + ': the ABI carries names in a char[32]; stick to letters, digits and underscores under 32 chars.' });
            }
            rangeCheck(out, 'sensor.localPos.x', s.localPos.x, where);
            rangeCheck(out, 'sensor.localPos.y', s.localPos.y, where);
            rangeCheck(out, 'sensor.localPos.z', s.localPos.z, where);
            if (s.kind === SensorType.Tof) {
                rangeCheck(out, 'sensor.range', s.range, where);
                rangeCheck(out, 'sensor.coneRays', s.coneRays, where);
            }
            if (s.kind === SensorType.Encoder || s.kind === SensorType.Suspension) {
                if (s.wheelIndex < 0 || s.wheelIndex >= d.wheels.length) {
                    out.push({ level: 'error', msg: where + ': wheelIndex ' + s.wheelIndex + ' has no matching wheel (there ' + (d.wheels.length === 1 ? 'is 1 wheel' : 'are ' + d.wheels.length + ' wheels') + ').' });
                }
            }
            if (s.kind === SensorType.Encoder) {
                rangeCheck(out, 'sensor.cprTicks', s.cprTicks, where);
                if (s.updateRateHz > 0 || s.latencyMs > 0) {
                    out.push({ level: 'warn', msg: where + ': a real quadrature counter is read synchronously. Update rate and latency above 0 inject odometry lag that hardware would not have.' });
                }
            }
            if (s.kind === SensorType.Camera) {
                rangeCheck(out, 'sensor.camWidth', s.camWidth, where);
                rangeCheck(out, 'sensor.camHeight', s.camHeight, where);
                rangeCheck(out, 'sensor.camFov', s.camFov, where);
            }
        });

        if (!d.batteries.length) {
            out.push({ level: 'info', msg: 'No battery part: the motors run off a stiff, infinite supply (legacy behaviour). Add a pack to get voltage sag and state-of-charge.' });
        } else {
            const b = d.batteries[0];
            rangeCheck(out, 'battery.massKg', b.massKg, 'Battery');
            rangeCheck(out, 'battery.internalR', b.internalR, 'Battery');
            rangeCheck(out, 'battery.capacitymAh', b.capacitymAh, 'Battery');
            const maxV = Math.max.apply(null, d.wheels.filter(function (w) { return w.powered; })
                .map(function (w) { return w.motor.maxVoltage; }).concat([0]));
            if (maxV > b.nominalV + 0.05) {
                out.push({ level: 'warn', msg: 'A motor rail of ' + maxV + ' V exceeds the pack\'s ' + b.nominalV + ' V. The bus clamps to the battery terminal voltage, so the extra rail is unreachable.' });
            }
        }

        if (d.controllerDll) {
            if (/[\\/:]|\.\./.test(d.controllerDll)) {
                out.push({ level: 'error', msg: 'controllerDll "' + d.controllerDll + '" contains a path separator; TrackBootstrap.SafeDllName rejects it and falls back to car_controller.dll.' });
            } else if (!/\.dll$/i.test(d.controllerDll)) {
                out.push({ level: 'info', msg: 'controllerDll has no .dll suffix — the game appends one automatically.' });
            }
        }
        return out;
    }

    // ---- Derived readouts (ports of the in-game maths) ------------------

    function suspClampLength(len) { return len <= 0 ? 0 : Math.min(0.06, Math.max(0.015, len)); }
    function suspMotionRatio(len) { const l = suspClampLength(len); return l <= 0 ? 1 : 0.03 / l; }
    function suspEffectiveRate(k, len) {
        if (len <= 0) return k;
        const mr = suspMotionRatio(len);
        return Math.min(4000, Math.max(50, k * mr * mr));
    }
    function suspEffectiveTravel(travel, len) {
        if (len <= 0) return travel;
        return Math.min(0.12, Math.max(0.005, travel * (suspClampLength(len) / 0.03)));
    }
    function hubOffset(localPosX, angleDeg, length) {
        const len = suspClampLength(length);
        if (len <= 0) return { x: 0, y: 0, z: 0 };
        const sign = localPosX >= 0 ? -1 : 1;
        const tilt = sign * Math.min(30, Math.max(-30, angleDeg)) * Math.PI / 180;
        // Rotate (0,-len,0) about Z by `tilt`.
        return { x: Math.sin(tilt) * len, y: -Math.cos(tilt) * len, z: 0 };
    }

    function sensorAutoMass(s) {
        if (s.massKg > 0) return s.massKg;
        if (s.kind === SensorType.Camera) return AutoMass.Camera;
        if (s.kind === SensorType.Encoder) return AutoMass.Encoder;
        if (s.kind === SensorType.Suspension) return AutoMass.SuspSensor;
        return AutoMass.Tof;
    }
    function wheelAutoMass(w) { return w.massKg > 0 ? w.massKg : (w.powered ? AutoMass.PoweredWheel : AutoMass.Wheel); }
    function aeroAutoMass(a) {
        if (a.massKg > 0) return a.massKg;
        const s = Math.min(1.6, Math.max(0.6, a.sizeScale <= 0 ? 1 : a.sizeScale));
        return a.kind === AeroKind.Wing ? AutoMass.Wing * s * s : AutoMass.SmallAero;
    }

    /* Port of MassProperties.Compute — total mass, CoM, yaw inertia and
       front-axle share, using the same point-mass approximation. */
    function massProperties(design) {
        const d = normalize(design);
        const masses = [{ m: Math.max(0.05, d.mass), p: { x: 0, y: -0.03, z: 0 } }];
        d.wheels.forEach(function (w) {
            const off = hubOffset(w.localPos.x, w.suspAngleDeg, w.suspLength);
            masses.push({ m: wheelAutoMass(w), p: { x: w.localPos.x + off.x, y: w.localPos.y + off.y, z: w.localPos.z + off.z } });
        });
        d.sensors.forEach(function (s) { masses.push({ m: sensorAutoMass(s), p: s.localPos }); });
        d.aero.forEach(function (a) { masses.push({ m: aeroAutoMass(a), p: a.localPos }); });
        d.batteries.forEach(function (b) { masses.push({ m: Math.max(0.01, b.massKg), p: b.localPos }); });
        d.antennas.forEach(function (a) { masses.push({ m: a.massKg > 0 ? a.massKg : AutoMass.Antenna, p: a.localPos }); });

        let total = 0, cx = 0, cy = 0, cz = 0;
        masses.forEach(function (e) { total += e.m; cx += e.m * e.p.x; cy += e.m * e.p.y; cz += e.m * e.p.z; });
        const denom = Math.max(1e-4, total);
        const com = { x: cx / denom, y: cy / denom, z: cz / denom };

        const mc = Math.max(0.05, d.mass), s3 = d.bodySize;
        const inertia = {
            x: mc / 12 * (s3.y * s3.y + s3.z * s3.z),
            y: mc / 12 * (s3.x * s3.x + s3.z * s3.z),
            z: mc / 12 * (s3.x * s3.x + s3.y * s3.y)
        };
        masses.forEach(function (e) {
            const dx = e.p.x - com.x, dy = e.p.y - com.y, dz = e.p.z - com.z;
            inertia.x += e.m * (dy * dy + dz * dz);
            inertia.y += e.m * (dx * dx + dz * dz);
            inertia.z += e.m * (dx * dx + dy * dy);
        });

        let zF = -Infinity, zR = Infinity;
        d.wheels.forEach(function (w) { zF = Math.max(zF, w.localPos.z); zR = Math.min(zR, w.localPos.z); });
        const frontWeightPct = (zF - zR > 1e-3)
            ? Math.min(1, Math.max(0, (com.z - zR) / (zF - zR))) * 100 : 50;

        return { totalMass: total, com: com, inertiaDiag: inertia, yawInertia: inertia.y, frontWeightPct: frontWeightPct };
    }

    // ---- Aerodynamics (AeroDynamics.cs) ---------------------------------

    const AIR_DENSITY = 1.225;
    function bodyCd(shape) { return BodyShapeInfo[shape] ? BodyShapeInfo[shape].cd : 0.80; }
    function bodyClA(shape) { return BodyShapeInfo[shape] ? BodyShapeInfo[shape].clA : 0; }
    function frontalArea(bodySize) { return bodySize.x * bodySize.y * 0.9; }

    function partCoefficients(kind, angleDeg) {
        switch (kind) {
            case AeroKind.Wing: {
                const a = Math.min(15, Math.max(0, angleDeg));
                return { clA: 0.0008 * a, cdA: 0.0003 + 0.00002 * a };
            }
            case AeroKind.Splitter: return { clA: 0.004, cdA: 0.0004 };
            case AeroKind.SideDam: return { clA: 0.0015, cdA: 0.0002 };
            case AeroKind.Canard: {
                const a = Math.min(15, Math.max(0, angleDeg));
                return { clA: 0.0002 * a, cdA: 0.0002 + 0.00001 * a };
            }
            default: return { clA: 0, cdA: 0 };
        }
    }
    function totalCdA(design) {
        const d = normalize(design);
        let cdA = bodyCd(d.bodyShape) * frontalArea(d.bodySize);
        d.aero.forEach(function (a) {
            const s = Math.min(1.6, Math.max(0.6, a.sizeScale <= 0 ? 1 : a.sizeScale));
            cdA += partCoefficients(a.kind, a.angleDeg).cdA * s * s;
        });
        return cdA;
    }
    function totalClA(design) {
        const d = normalize(design);
        let clA = bodyClA(d.bodyShape);
        d.aero.forEach(function (a) {
            const s = Math.min(1.6, Math.max(0.6, a.sizeScale <= 0 ? 1 : a.sizeScale));
            clA += partCoefficients(a.kind, a.angleDeg).clA * s * s;
        });
        return clA;
    }

    // ---- Stats (VehicleStats.cs) ----------------------------------------

    /* Geared wheel torque for a commanded voltage at a wheel speed — the
       same equation the sim solves every physics tick (MotorModel.WheelTorque),
       including the ESC current clamp and Coulomb friction branch. */
    function wheelTorque(m, commandedVoltage, wheelOmega) {
        const vmax = Math.max(0.01, m.maxVoltage);
        const voltage = Math.min(vmax, Math.max(-vmax, commandedVoltage));
        const r = Math.max(1e-3, m.resistance);
        const gear = Math.max(1e-3, m.gearRatio);
        const omegaMotor = wheelOmega * gear;
        let stall = vmax / r;
        if (m.maxCurrent > 0) stall = Math.min(stall, m.maxCurrent);
        const current = Math.min(stall, Math.max(-stall, (voltage - m.kt * omegaMotor) / r));
        const tauEm = m.kt * current;
        const tauVisc = m.viscousDamping * omegaMotor;
        const tc = Math.max(0, m.coulombScale) * m.kt * Math.max(0, m.noLoadCurrent);
        let torqueMotor;
        if (Math.abs(omegaMotor) > 0.5) {
            torqueMotor = tauEm - tauVisc - tc * Math.sign(omegaMotor);
        } else {
            const net = tauEm - tauVisc;
            torqueMotor = Math.sign(net) * Math.max(0, Math.abs(net) - tc);
        }
        const eff = Math.min(1, Math.max(0, m.efficiency <= 0 ? 1 : m.efficiency));
        return { torque: torqueMotor * gear * eff, current: current, voltage: voltage };
    }

    function stats(design) {
        const d = normalize(design);
        const r = { wheels: d.wheels.length, powered: 0, steered: 0, totalStallTorqueNm: 0, estTopSpeedMs: 0 };

        if (d.useCompositeMass) {
            const mp = massProperties(d);
            r.composite = true;
            r.totalMass = mp.totalMass;
            r.com = mp.com;
            r.frontWeightPct = mp.frontWeightPct;
            r.yawInertia = mp.yawInertia;
        } else {
            r.composite = false;
            r.totalMass = d.mass + 0.05 * d.wheels.length;   // WheelCollider mass
        }

        if (d.wheels.length) {
            let kSum = 0, travelSum = 0;
            d.wheels.forEach(function (w) {
                kSum += suspEffectiveRate(w.suspStiffness > 0 ? w.suspStiffness : 300, w.suspLength);
                travelSum += suspEffectiveTravel(w.suspTravel > 0 ? w.suspTravel : 0.03, w.suspLength);
            });
            const kAvg = kSum / d.wheels.length;
            const travelAvg = travelSum / d.wheels.length;
            const mCorner = r.totalMass / d.wheels.length;
            r.rideFreqHz = Math.sqrt(kAvg / Math.max(1e-4, mCorner)) / (2 * Math.PI);
            r.sagPct = mCorner * 9.81 / (kAvg * Math.max(1e-4, travelAvg)) * 100;
        }

        let noLoadTop = Infinity;
        d.wheels.forEach(function (w) {
            if (w.allowsSteering) r.steered++;
            if (!w.powered) return;
            r.powered++;
            const m = w.motor;
            const kt = Math.max(1e-4, m.kt), res = Math.max(1e-3, m.resistance);
            const gear = Math.max(1e-3, m.gearRatio);
            const eff = Math.min(1, Math.max(0, m.efficiency <= 0 ? 1 : m.efficiency));
            let stallA = m.maxVoltage / res;
            if (m.maxCurrent > 0) stallA = Math.min(stallA, m.maxCurrent);
            r.totalStallTorqueNm += kt * stallA * gear * eff;
            const top = ((m.maxVoltage / kt) / gear) * Math.max(0.01, w.radius);
            if (top < noLoadTop) noLoadTop = top;
        });
        if (!r.powered) return r;

        const cdA = totalCdA(d), clA = totalClA(d);
        function thrust(v) {
            let sum = 0;
            d.wheels.forEach(function (w) {
                if (!w.powered) return;
                const rad = Math.max(0.01, w.radius);
                sum += wheelTorque(w.motor, w.motor.maxVoltage, v / rad).torque / rad;
            });
            return sum;
        }
        function drag(v) { return 0.5 * AIR_DENSITY * v * v * cdA; }

        let lo = 0, hi = noLoadTop;
        for (let i = 0; i < 24; i++) {
            const mid = (lo + hi) * 0.5;
            if (thrust(mid) > drag(mid)) lo = mid; else hi = mid;
        }
        r.estTopSpeedMs = (lo + hi) * 0.5;
        const qTop = 0.5 * AIR_DENSITY * r.estTopSpeedMs * r.estTopSpeedMs;
        r.dragAtTopN = qTop * cdA;
        r.downforceAtTopN = qTop * clA;
        r.launchAccelMs2 = r.totalStallTorqueNm > 0 && d.wheels.length
            ? (thrust(0) / Math.max(0.05, r.totalMass)) : 0;
        return r;
    }

    global.TT = global.TT || {};
    global.TT.Schema = {
        BodyShape: BodyShape, BodyShapeNames: BodyShapeNames, BodyShapeInfo: BodyShapeInfo,
        SensorType: SensorType, SensorTypeNames: SensorTypeNames, PlaceableSensorKinds: PlaceableSensorKinds,
        AeroKind: AeroKind, AeroKindNames: AeroKindNames,
        WheelStyleNames: WheelStyleNames, MotorEntryMode: MotorEntryMode,
        MOTOR_DEFAULT: MOTOR_DEFAULT, MOTOR_FIELDS: MOTOR_FIELDS, DATASHEET_FIELDS: DATASHEET_FIELDS,
        AutoMass: AutoMass, Range: Range, AIR_DENSITY: AIR_DENSITY,
        v3: v3, clone: clone,
        newDesign: newDesign, newWheel: newWheel, newSensor: newSensor,
        newAero: newAero, newAntenna: newAntenna, newBattery: newBattery, newMotor: newMotor,
        stockDesign: stockDesign, normalize: normalize,
        toJson: toJson, fileNameFor: fileNameFor, validate: validate,
        massProperties: massProperties, stats: stats, wheelTorque: wheelTorque,
        totalCdA: totalCdA, totalClA: totalClA, partCoefficients: partCoefficients,
        bodyCd: bodyCd, bodyClA: bodyClA, frontalArea: frontalArea,
        suspEffectiveRate: suspEffectiveRate, suspEffectiveTravel: suspEffectiveTravel,
        suspMotionRatio: suspMotionRatio, hubOffset: hubOffset,
        wheelAutoMass: wheelAutoMass, sensorAutoMass: sensorAutoMass, aeroAutoMass: aeroAutoMass
    };
})(typeof window !== 'undefined' ? window : globalThis);
