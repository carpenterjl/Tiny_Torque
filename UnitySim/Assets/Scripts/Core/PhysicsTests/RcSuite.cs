using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Sensors;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.PhysicsTests
{
    /// <summary>
    /// <b>[RC] — VAL-01's RC-scale physics suite (R0–R4, R7, R8, R10).</b>
    ///
    /// Every P-series test drives the full-scale Tiguan. These drive an RC
    /// twin — by default the per-wheel FOC "Opus Vector FOC" — and each one
    /// mirrors a bench test that can be run on the real car, so the same
    /// numbers become the sim-vs-real comparison later (SIM_TO_REAL_PLAN §4.8).
    ///
    /// Predictions are worked from the design's own parameters (mass, wheel
    /// and rotor inertia, motor constants, rolling resistance, bearings, aero),
    /// not from another run of the sim, so a pass means the physics does what
    /// the equations say.
    ///
    /// Launched by <c>Editor/RcSuiteRunner</c> through a request file, like
    /// MissionAutorun: an empty scene, physics stepped by hand
    /// (<see cref="SimulationMode.Script"/>), a fresh car per test.
    /// </summary>
    public static class RcSuite
    {
        public static string RequestPath => Path.Combine(Path.GetTempPath(), "tt_rc_suite_request.json");

        [Serializable]
        public class Request
        {
            public string resultPath = "";
            public string vehicle = "Opus Vector FOC";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!File.Exists(RequestPath)) return;
            Request req;
            try
            {
                req = JsonUtility.FromJson<Request>(File.ReadAllText(RequestPath));
                File.Delete(RequestPath);
            }
            catch (Exception e) { Debug.LogError($"[RC] bad request: {e.Message}"); return; }
            var go = new GameObject("RcSuite");
            go.AddComponent<RcSuiteRunner>().Begin(req);
        }
    }

    public sealed class RcSuiteRunner : MonoBehaviour
    {
        private const string Tag = "[RC]";
        private RcSuite.Request _req;
        private readonly StringBuilder _log = new StringBuilder();
        private readonly List<string> _json = new List<string>();
        private int _checks, _failed;

        // live test state
        private VehicleDesign _design;
        private CarVehicle _car;
        private Rigidbody _body;
        private MemsImuSensor _imu;
        private GameObject _carRoot, _ground;
        private readonly float[] _cmd = new float[8];
        private float _dt;
        private double _t;
        private Vector3 _fwd0, _pos0;
        private StringBuilder _tr;      // per-step trace of the current test, when on
        private string _trName;

        public void Begin(RcSuite.Request req) { _req = req; }

        private IEnumerator Start()
        {
            yield return null;
            var oldMode = Physics.simulationMode;
            float oldFixed = Time.fixedDeltaTime;
            Physics.simulationMode = SimulationMode.Script;
            bool ok = true;
            try
            {
                RunAll();
            }
            catch (Exception e)
            {
                ok = false;
                Debug.LogError($"{Tag} suite threw: {e}");
                _failed++;
            }
            finally
            {
                Physics.simulationMode = oldMode;
                Time.fixedDeltaTime = oldFixed;
                Teardown();
            }

            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            Debug.Log(_log.ToString().TrimEnd());
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);
            try
            {
                if (!string.IsNullOrEmpty(_req.resultPath))
                {
                    File.WriteAllText(_req.resultPath, _log + summary + "\n");
                    File.WriteAllText(Path.ChangeExtension(_req.resultPath, ".json"),
                        "{\n" + string.Join(",\n", _json) + "\n}\n");
                }
            }
            catch (Exception e) { Debug.LogError($"{Tag} write failed: {e.Message}"); }
            ok &= _failed == 0;
#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(ok ? 0 : 1);
            else UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // =================================================================== tests

        private void RunAll()
        {
            Line($"vehicle: {_req.vehicle}");
            Identify(400);

            // R1 / R3 / R4a at three physics rates is R8.
            var r1 = new Dictionary<int, float>();
            var r3 = new Dictionary<int, float>();
            var r4 = new Dictionary<int, float>();
            foreach (int hz in new[] { 400, 800, 1600 })
            {
                r1[hz] = R1TorqueStep(hz);
                r3[hz] = R3StopFromSpeed(hz);
                r4[hz] = R4aMotorHold(hz);
            }
            R8Sweep("R1 acceleration", r1, 0.02f);
            R8Sweep("R3 stopping distance / its prediction", r3, 0.02f);
            {
                float worst = 0f;
                foreach (var kv in r4) worst = Mathf.Max(worst, kv.Value);
                Check("R8", "R4a hold: no creep at any rate (worst mm/s)", worst, 0f, 0.1f, abs: true);
            }

            R0FreeRoll(400);
            R2Coastdown(400);
            R4bRollAway(400);
            R4cHoldsOnShallowGrade(400);
            R7RegenCharges(400);
            R7FullPackDerates(400);
            R7FullPackTrips(400);
            foreach (int hz in new[] { 400, 1600 }) R10EncoderCrawl(hz);
            ImuInTheCar(400);
            RealismInTheCar(400);
        }

        /// <summary>SEN-06/09 in the car: on a realistic design the pack
        /// sensor gives no SoC, and the pack and driver readings carry their
        /// converters' errors — close to the truth, never equal to it.</summary>
        private void RealismInTheCar(int hz)
        {
            if (_design.sensorRealism <= 0) { Line("SEN   the design keeps the legacy sensor profile: skipped"); return; }
            Build(hz, 0f);
            SetIq(2f);
            Run(0.3f);
            var batt = _carRoot.GetComponentInChildren<BatterySensor>();
            if (batt == null) { Check("SEN", "pack sensor present", 0f, 1f, 0f, abs: true); return; }
            var b = new float[3];
            batt.Sample(_dt, b, 0);
            Check("SEN", "pack SoC is not measurable (1 = reads NaN)", float.IsNaN(b[2]) ? 1f : 0f, 1f, 0f, abs: true);
            Check("SEN", $"pack voltage vs truth {_car.BatteryTerminalV:0.0000} V (V)", b[0], _car.BatteryTerminalV, 0.01f);
            Check("SEN", "pack voltage is not the truth to the bit (1 = differs)",
                  b[0] != _car.BatteryTerminalV ? 1f : 0f, 1f, 0f, abs: true);
            var mp = _car.Motors.Count > 0 ? _car.Motors[0] : null;
            if (mp != null && mp.IsFoc)
            {
                var f = new float[mp.DataCount];
                mp.Sample(_dt, f, 0);
                Check("SEN", $"driver bus voltage vs truth {mp.BusVoltage:0.000} V (V)", f[3], mp.BusVoltage, 0.03f);
                Check("SEN", $"driver Iq vs commanded 2 A (A)", f[0], 2f, 0.1f);
            }
        }

        /// <summary>
        /// SEN-01 in the car: the twin's IMU (with its datasheet errors) reads
        /// +1 g and no rate at rest, and its forward channel follows the car's
        /// measured acceleration in a torque step. The step is judged as a
        /// change from rest, so the part's turn-on bias cancels; what is left
        /// is noise, scale error and the car's squat (pitch adds g·sinθ).
        /// </summary>
        private void ImuInTheCar(int hz)
        {
            Build(hz, 0f);
            if (_imu == null) { Line("IMU   the vehicle carries no SENSOR_IMU6 part: skipped"); return; }
            var buf = new float[6];
            // tilt = g·sin(pitch) of the chip's x axis: what gravity alone puts
            // on the forward channel.
            Vector3 MeanImu(float seconds, out Vector3 gyro, out float tilt)
            {
                int n = Mathf.RoundToInt(seconds / _dt);
                Vector3 a = Vector3.zero, g = Vector3.zero;
                tilt = 0f;
                for (int i = 0; i < n; i++)
                {
                    Step();
                    _imu.Sample(_dt, buf, 0);
                    g += new Vector3(buf[0], buf[1], buf[2]);
                    a += new Vector3(buf[3], buf[4], buf[5]);
                    tilt += -Physics.gravity.y * Vector3.Dot(_imu.transform.forward, Vector3.up);
                }
                gyro = g / n;
                tilt /= n;
                return a / n;
            }
            Vector3 rest = MeanImu(0.5f, out Vector3 gRest, out float tiltRest);
            Check("IMU", "at rest: specific force up (m/s²)", rest.z, 9.80665f, 0.02f);
            Check("IMU", "at rest: yaw rate, turn-on bias included (rad/s)", gRest.z, 0f, 0.012f, abs: true);

            const float iq = 3f;
            SetIq(iq);
            Run(0.15f);
            Vector3 p0 = _body.position;
            float v0 = Speed();
            const float win = 0.30f;
            Vector3 drive = MeanImu(win, out _, out float tiltDrive);
            float aCar = (Speed() - v0) / win;
            float squat = tiltDrive - tiltRest;
            Check("IMU", $"torque step: forward accel change vs the car's {aCar:0.000} m/s² + squat g·sinθ {squat:0.000} (m/s²)",
                  drive.x - rest.x, aCar + squat, 0.02f);
            Line($"IMU   mount {_imu.transform.localPosition}, spec ODR {_imu.updateRateHz:0} Hz, DLPF {_imu.spec.dlpfHz:0} Hz; " +
                 $"rest a ({rest.x:0.000}, {rest.y:0.000}, {rest.z:0.000}) m/s², step a_x {drive.x:0.000}");
        }

        /// <summary>
        /// ID — the firmware-facing numbers a bench would measure, reported
        /// rather than judged: the static front-axle weight share (corner
        /// scales, S1) and the tyre's longitudinal slip stiffness per unit load
        /// C_κ/F_z (S7/S10). The slip stiffness comes from a tug of war: front
        /// wheels drive, rear wheels brake by the same torque, so the car
        /// rolls at a steady speed while the front tyres carry a known force.
        /// </summary>
        private void Identify(int hz)
        {
            Build(hz, 0f);
            Run(0.5f);
            float front = _car.WheelLoadN(0) + _car.WheelLoadN(1);
            float total = front + _car.WheelLoadN(2) + _car.WheelLoadN(3);
            float frac = total > 0f ? front / total : 0f;
            Line($"ID  static front weight share: {frac:0.0000}  (front_weight_frac)");
            _json.Add($"  \"id_front_weight_frac\": {frac:0.00000}");

            DriveToSpeed(2f, 3f);
            var kap = new List<float>(); var frc = new List<float>(); var fzs = new List<float>();
            foreach (float iq in new[] { 1.5f, 3.0f, 4.5f })
            {
                // Front drives at +iq; the rear's current is trimmed to hold 2 m/s.
                float rear = -iq;
                for (int k = 0; k < (int)(1.2f / _dt); k++)
                {
                    rear = Mathf.Clamp(rear + 20f * (2f - Speed()) * _dt * 10f, -15f, 15f);
                    foreach (var m in _car.Motors)
                        if (m != null && m.ActuatorIndex >= 0)
                            _cmd[m.ActuatorIndex] = m.wheelIndex < 2 ? iq : rear;
                    Step();
                }
                float ka = 0f, fz = 0f; int n = (int)(0.3f / _dt);
                for (int k = 0; k < n; k++)
                {
                    Step();
                    ka += 0.5f * (_car.WheelSlipRatio(0) + _car.WheelSlipRatio(1));
                    fz += 0.5f * (_car.WheelLoadN(0) + _car.WheelLoadN(1));
                }
                ka /= n; fz /= n;
                float omegaM = Speed() / Radius() * Gear();
                // Tyre force on one front wheel: its drive force less its own losses.
                float fTyre = DriveForce(iq, omegaM) / Powered()
                              - (LossForce(Speed(), coasting: false) - 0.5f * AeroDynamics.AirDensity * _car.AeroCdA * Speed() * Speed()) / 4f;
                kap.Add(ka); frc.Add(fTyre); fzs.Add(fz);
            }
            // Slope of force against slip, through the three points, per unit load.
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int np = kap.Count;
            for (int i = 0; i < np; i++) { sx += kap[i]; sy += frc[i]; sxx += kap[i] * kap[i]; sxy += kap[i] * frc[i]; }
            float ck = (float)((np * sxy - sx * sy) / (np * sxx - sx * sx));
            float fzm = 0f; foreach (var f in fzs) fzm += f / np;
            Line($"ID  front tyre slip stiffness C_k = {ck:0.0} N/unit at F_z {fzm:0.00} N -> C_k/F_z = {ck / fzm:0.00}  (slip_stiffness)");
            Line($"ID    points (kappa %, F N): " + string.Join("  ", kap.ConvertAll(k => (k * 100f).ToString("0.000"))) +
                 " | " + string.Join("  ", frc.ConvertAll(f => f.ToString("0.000"))));
            _json.Add($"  \"id_slip_stiffness\": {ck / fzm:0.0000}");
        }

        /// <summary>R0 — free roll at Iq = 0: decelerates at exactly the losses
        /// over the effective mass.</summary>
        private void R0FreeRoll(int hz)
        {
            Build(hz, 0f);
            DriveToSpeed(1.6f, 3f);
            SetIq(0f);
            Run(0.2f);
            FitSlope(0.4f, out float a, out float vMid);
            float pred = -LossForce(vMid, coasting: true) / EffMass();
            {
                float fzSum = 0f;
                for (int i = 0; i < 4; i++) fzSum += _car.WheelLoadN(i);
                float r = Radius();
                var pm = Motor();
                float tc = Mathf.Max(0f, pm.coulombScale) * pm.kt * Mathf.Max(0f, pm.noLoadCurrent);
                Line($"R0    m {_body.mass:0.000} kg, m_eff {EffMass():0.000} kg, ΣF_z {fzSum:0.00} N (m·g {_body.mass * -Physics.gravity.y:0.00}); " +
                     $"losses: rolling {Mathf.Max(0f, _design.wheels[0].rollCrr) * fzSum:0.000} N, bearings {4f * _design.wheels[0].bearingNm / r:0.000} N, " +
                     $"motors {Powered() * tc * Gear() / EtaBack() / r:0.000} N, aero {0.5f * AeroDynamics.AirDensity * _car.AeroCdA * vMid * vMid:0.0000} N; " +
                     $"measured total {-a * EffMass():0.000} N");
                var sb = new StringBuilder("R0    per motor (wheel, torque N·m, engaged, gap, ω_wheel·r): ");
                foreach (var mm in _car.Motors)
                    sb.Append($"[{mm.wheelIndex} {mm.WheelTorqueOut:0.00000} {(mm.LashEngaged ? 1 : 0)} {mm.LashGapRad:0.0000} {_car.WheelOmega(mm.wheelIndex) * r:0.0000}] ");
                Line(sb.ToString());
            }
            Check("R0", $"free-roll decel at {vMid:0.00} m/s (m/s²)", a, pred, 0.05f);
        }

        /// <summary>R1 — torque step from rest: a = (ΣT/r − losses)/m_eff ± 2 %.</summary>
        private float R1TorqueStep(int hz)
        {
            Build(hz, 0f);
            const float iq = 3f;
            SetIq(iq);
            Run(0.15f);
            FitSlope(0.30f, out float a, out float vMid);
            float omegaM = vMid / Radius() * Gear();
            float pred = (DriveForce(iq, omegaM) - LossForce(vMid, coasting: false)) / EffMass();
            Check("R1", $"@{hz} Hz torque-step acceleration, Iq {iq} A (m/s²)", a, pred, 0.02f);
            return a;
        }

        /// <summary>R2 — coast-down from 4 m/s: fit F(v) = c0 + c1·v + c2·v²
        /// and check it against the losses the design states. The fit is what
        /// the firmware's drag feed-forward should carry.</summary>
        private void R2Coastdown(int hz)
        {
            Build(hz, 0f);
            DriveToSpeed(4.0f, 6f);
            SetIq(0f);
            TraceBegin("r2");
            Run(0.2f);
            var vs = new List<float>();
            var fs = new List<float>();
            float mEff = EffMass();
            float vPrev = Speed();
            int every = Mathf.Max(1, hz / 50);   // 20 ms differences
            int n = 0;
            while (Speed() > 0.3f && _t < 60.0)
            {
                Step();
                if (++n % every != 0) continue;
                float v = Speed();
                float a = (v - vPrev) / (every * _dt);
                vs.Add(0.5f * (v + vPrev));
                fs.Add(-mEff * a);
                vPrev = v;
            }
            TraceEnd();
            FitQuadratic(vs, fs, out double c0, out double c1, out double c2);
            float predC0 = LossForce(0f, coasting: true);
            float predAt2 = LossForce(2f, coasting: true);
            float fitAt2 = (float)(c0 + c1 * 2 + c2 * 4);
            Check("R2", "coast-down fit c0 vs stated losses at rest (N)", (float)c0, predC0, 0.10f);
            Check("R2", "coast-down fit F(2 m/s) vs stated losses (N)", fitAt2, predAt2, 0.05f);
            Line($"  R2 fit: F(v) = {c0:0.000} + {c1:0.0000}·v + {c2:0.00000}·v² N  (drag_c0/c1/c2 for the firmware)");
            _json.Add($"  \"r2_drag\": [{c0:0.0000}, {c1:0.00000}, {c2:0.000000}]");
        }

        /// <summary>R3 — stop from 1 m/s on a constant regen current: the
        /// distance matches ∫v dv / a(v), and (R8) does not depend on dt.</summary>
        private float R3StopFromSpeed(int hz)
        {
            Build(hz, 0f);
            DriveToSpeed(1.0f, 3f);
            const float iq = -2f;   // inside the rear tyres' grip under braking
            float v0 = Speed();
            Vector3 p0 = _body.position;
            if (hz == 400) TraceBegin("r3");
            SetIq(iq);
            float vPrevStep = v0;
            Vector3 pPrev = p0;
            while (Speed() > 0f && _t < 30.0)
            {
                pPrev = _body.position;
                vPrevStep = Speed();
                Step();
            }
            // Interpolate to the instant the speed crossed zero.
            float v1 = Speed();
            float frac = vPrevStep / Mathf.Max(1e-6f, vPrevStep - v1);
            Vector3 pStop = Vector3.Lerp(pPrev, _body.position, frac);
            float d = Vector3.Dot(pStop - p0, _fwd0);
            TraceEnd();

            // Prediction: ∫ m_eff·v / F(v) dv from 0 to v0, F = regen force + losses.
            double pred = 0;
            const int N = 400;
            for (int k = 0; k < N; k++)
            {
                float v = (k + 0.5f) * v0 / N;
                float omegaM = v / Radius() * Gear();
                float f = -DriveForce(iq, omegaM) + LossForce(v, coasting: false);
                pred += EffMass() * v / f * (v0 / N);
            }
            // The command reaches the wheel one transport delay late.
            pred += v0 * Motor().cmdLatencyMs * 0.001f;
            Check("R3", $"@{hz} Hz stop from {v0:0.00} m/s at Iq {iq} A (mm)", d * 1000f, (float)pred * 1000f, 0.03f);
            // Each rate reaches a slightly different v0, so R8 compares the
            // measured/predicted ratio rather than raw distances.
            return d / (float)pred;
        }

        /// <summary>R4a — hold on a 10 % grade with motor current alone: no
        /// creep (the legacy tyre crept ~11 mm/s, set by the timestep).</summary>
        private float R4aMotorHold(int hz)
        {
            float grade = 0.10f;
            Build(hz, grade, facingUphill: true);
            float theta = Mathf.Atan(grade);
            float tWheel = _body.mass * -Physics.gravity.y * Mathf.Sin(theta) * Radius() / 4f;
            float iq = tWheel / (Motor().kt * Gear() * Eta());
            SetIq(iq);
            Run(1.5f);
            Vector3 p0 = _body.position;
            Run(5f);
            float creep = Vector3.Distance(_body.position, p0) / 5f * 1000f;   // mm/s
            Check("R4", $"@{hz} Hz 10 % grade held on Iq {iq:0.00} A: creep (mm/s)", creep, 0f, 0.1f, abs: true);
            return creep;
        }

        /// <summary>R4b — on a 10 % grade at Iq = 0 the car rolls away at
        /// (m·g·sinθ − losses)/m_eff.</summary>
        private void R4bRollAway(int hz)
        {
            float grade = 0.10f;
            Build(hz, grade, facingUphill: false);
            SetIq(0f);
            TraceBegin("r4b");
            Run(0.6f);
            FitSlope(0.4f, out float a, out float vMid);
            TraceEnd();
            float theta = Mathf.Atan(grade);
            float pred = (_body.mass * -Physics.gravity.y * Mathf.Sin(theta)
                          - LossForce(vMid, coasting: true, cosTheta: Mathf.Cos(theta))) / EffMass();
            Check("R4", $"10 % grade at Iq = 0 rolls away (m/s²) at {vMid:0.00} m/s", a, pred, 0.05f);
        }

        /// <summary>R4c — on a 1 % grade the losses exceed the pull, so the car
        /// stays put at Iq = 0, held by its tyres alone (no PhysX park-hold).</summary>
        private void R4cHoldsOnShallowGrade(int hz)
        {
            Build(hz, 0.01f, facingUphill: false);
            SetIq(0f);
            Run(1f);
            Vector3 p0 = _body.position;
            Run(3f);
            float moved = Vector3.Distance(_body.position, p0) * 1000f;
            Check("R4", "1 % grade at Iq = 0 stays put: moved in 3 s (mm)", moved, 0f, 1f, abs: true);
        }

        /// <summary>R7a — regen charges a part-used pack. Judged above
        /// ~1.2 m/s: below it the copper loss R·Iq² exceeds what the back-EMF
        /// generates, and braking on current then draws from the pack — which
        /// is the physics, and why the window stops at 1.5 m/s.</summary>
        private void R7RegenCharges(int hz)
        {
            Build(hz, 0f);
            _car.SetBatterySoc(0.5f);
            DriveToSpeed(3f, 4f);
            float soc0 = _car.BatterySoc;
            float iMin = 0f;
            SetIq(-3f);   // inside grip: a locked wheel makes no back-EMF and only burns power
            while (Speed() > 1.5f && _t < 30.0) { Step(); iMin = Mathf.Min(iMin, _car.BatteryCurrent); }
            SetIq(0f);
            Check("R7", "regen: pack current goes negative, net of the aux load (A)", iMin, -1f, 0f, below: true);
            Check("R7", "regen: state of charge rises (ppm)", (_car.BatterySoc - soc0) * 1e6f, 0f, 0f, above: true);
        }

        /// <summary>R7b — on a full pack the drivers' regen derate keeps the bus
        /// below the over-voltage trip, for a regen command ramped in over
        /// 0.2 s as firmware would (a step into a full pack is R7c's trip).</summary>
        private void R7FullPackDerates(int hz)
        {
            Build(hz, 0f);
            DriveToSpeed(4f, 6f);
            _car.SetBatterySoc(1f);
            float vMax = 0f; int faults = 0;
            TraceBegin("r7b");
            double t0 = _t;
            while (Speed() > 0.5f && _t < 30.0)
            {
                SetIq(-12f * Mathf.Clamp01((float)(_t - t0) / 0.2f));
                Step();
                vMax = Mathf.Max(vMax, _car.BatteryTerminalV);
                foreach (var m in _car.Motors) faults |= m.Faults;
            }
            TraceEnd();
            var mp = Motor();
            Check("R7", "full pack: bus stays below the trip (V)", vMax, mp.busOvTripV, 0f, below: true);
            Check("R7", "full pack: regen derate engaged (bus above derate start, V)", vMax, mp.busOvDerateV, 0f, above: true);
            Check("R7", "full pack: no over-voltage fault (bits)", faults & MotorPart.FaultOverVoltage, 0f, 0f, abs: true);
        }

        /// <summary>R7c — with the derate disabled (and the trip set where a
        /// full-pack stop must cross it) the over-voltage fault trips, and a
        /// tripped driver produces no current.</summary>
        private void R7FullPackTrips(int hz)
        {
            Build(hz, 0f, mutate: m => { m.busOvTripV = 8.55f; m.busOvDerateV = m.busOvTripV - 1e-4f; return m; });
            DriveToSpeed(4f, 6f);
            _car.SetBatterySoc(1f);
            bool tripped = false; float iAtTrip = float.NaN;
            SetIq(-12f);
            while (Speed() > 0.5f && _t < 30.0)
            {
                Step();
                foreach (var m in _car.Motors)
                    if ((m.Faults & MotorPart.FaultOverVoltage) != 0 && !tripped)
                    { tripped = true; iAtTrip = m.Current; }
            }
            Check("R7", "derate off: the over-voltage limit trips (1 = yes)", tripped ? 1f : 0f, 1f, 0f, abs: true);
            Check("R7", "tripped driver carries no current (A)", float.IsNaN(iAtTrip) ? 99f : iAtTrip, 0f, 1e-4f, abs: true);
        }

        /// <summary>R10 — encoder at crawl, with direction reversals: the count
        /// equals the analytic count of the physics' own wheel angle to ±1 LSB,
        /// sampled at the control rate, at any physics rate.</summary>
        private void R10EncoderCrawl(int hz)
        {
            Build(hz, 0f);
            WheelEncoderSensor enc = null;
            foreach (var e in _carRoot.GetComponentsInChildren<WheelEncoderSensor>())
                if (e.wheelIndex == 0) { enc = e; break; }
            if (enc == null) { Check("R10", "front-left encoder present", 0f, 1f, 0f, abs: true); return; }

            int decim = Mathf.Max(1, hz / 100);
            float sampleDt = decim * _dt;
            var buf = new float[enc.DataCount];
            enc.Sample(sampleDt, buf, 0);                 // base the counter here
            double theta = 0.0;
            double tick = 2.0 * Math.PI / enc.EffectiveCpr / Mathf.Max(0.01f, enc.gearRatio);
            long prevRaw = (long)buf[1], cum = 0;
            int worst = 0;
            // Crawl at ~5 cm/s forward, then back, with a simple speed loop.
            for (int phase = 0; phase < 2; phase++)
            {
                float vRef = phase == 0 ? 0.05f : -0.05f;
                for (int k = 0; k < (int)(3f / sampleDt); k++)
                {
                    SetIq(Mathf.Clamp(40f * (vRef - Speed()), -3f, 3f));
                    for (int s = 0; s < decim; s++)
                    {
                        Step();
                        theta += _car.WheelOmega(0) * (double)_dt;
                    }
                    enc.Sample(sampleDt, buf, 0);
                    long raw = (long)buf[1];
                    long d = raw - prevRaw;
                    if (d > enc.wrap / 2) d -= enc.wrap; else if (d < -enc.wrap / 2) d += enc.wrap;
                    prevRaw = raw;
                    cum += d;
                    long expect = (long)Math.Floor(theta / tick + 1e-9);
                    worst = Mathf.Max(worst, (int)Math.Abs(cum - expect));
                }
            }
            Check("R10", $"@{hz} Hz crawl ±5 cm/s: |count − analytic| (LSB)", worst, 0f, 1f, abs: true);
        }

        private void R8Sweep(string what, Dictionary<int, float> v, float tol)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var kv in v) { lo = Mathf.Min(lo, kv.Value); hi = Mathf.Max(hi, kv.Value); }
            float spread = Mathf.Abs(hi - lo) / Mathf.Max(1e-9f, Mathf.Abs(0.5f * (hi + lo)));
            Check("R8", $"{what}: spread across 400/800/1600 Hz (fraction)", spread, 0f, tol, abs: true);
        }

        // ================================================================== world

        private void Build(int hz, float grade, bool facingUphill = true,
                           Func<MotorParams, MotorParams> mutate = null)
        {
            Teardown();
            _dt = 1f / hz;
            Time.fixedDeltaTime = _dt;
            _t = 0.0;
            Array.Clear(_cmd, 0, _cmd.Length);

            // Ground: a 400 m slab, tilted about x by the grade. Downhill is +z.
            float theta = Mathf.Atan(grade);
            _ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _ground.name = "rc_ground";
            _ground.transform.localScale = new Vector3(40f, 1f, 400f);
            _ground.transform.rotation = Quaternion.Euler(theta * Mathf.Rad2Deg, 0f, 0f);
            _ground.transform.position = -0.5f * _ground.transform.up;
            Physics.SyncTransforms();

            _design = VehiclePresets.Resolve(_req.vehicle);
            if (mutate != null)
                foreach (var w in _design.wheels)
                    if (w.powered) w.motor = mutate(w.motor);

            Vector3 up = _ground.transform.up;
            Vector3 downhill = _ground.transform.forward;
            Vector3 fwd = facingUphill ? -downhill : downhill;
            if (grade == 0f) fwd = Vector3.forward;
            var rot = Quaternion.LookRotation(fwd, up);
            var built = VehicleFactory.Build(_design, up * 0.08f, rot);
            _carRoot = built.root;
            _car = built.car;
            _body = _carRoot.GetComponent<Rigidbody>();
            built.rig.Initialize(_car, _carRoot.transform);
            _imu = _carRoot.GetComponentInChildren<MemsImuSensor>();
            _fwd0 = fwd;

            // Settle on its suspension, motors off. On a grade, hold it with
            // the motors so it starts the test at rest.
            float tHold = _body.mass * -Physics.gravity.y * Mathf.Sin(theta) * Radius() / 4f;
            SetIq(facingUphill ? tHold / (Motor().kt * Gear() * Eta()) : -tHold / (Motor().kt * Gear() * Eta()));
            Run(1.0f);
            SetIq(0f);
            _pos0 = _body.position;
        }

        private void Teardown()
        {
            if (_carRoot != null) DestroyImmediate(_carRoot);
            if (_ground != null) DestroyImmediate(_ground);
            _carRoot = null; _ground = null; _car = null; _body = null; _imu = null;
        }

        private void SetIq(float iq)
        {
            foreach (var m in _car.Motors)
                if (m != null && m.ActuatorIndex >= 0 && m.ActuatorIndex < _cmd.Length)
                    _cmd[m.ActuatorIndex] = iq;
        }

        private void Step()
        {
            _car.SetCommands(_cmd);
            _car.StepPhysics(_dt);
            Physics.Simulate(_dt);
            _imu?.PhysicsStep(0, _dt);
            _t += _dt;
            if (_tr != null)
            {
                var m = _car.Motors.Count > 0 ? _car.Motors[0] : null;
                _tr.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "{0:0.0000},{1:0.00000},{2:0.00000},{3:0.000},{4:0.00000},{5},{6:0.000000},{7:0.000},{8:0.000},{9:0.00000},{10}\n",
                    _t, Speed(), _car.WheelOmega(m != null ? m.wheelIndex : 0) * Radius(),
                    m != null ? m.Current : 0f, m != null ? m.WheelTorqueOut : 0f,
                    m != null && m.LashEngaged ? 1 : 0, m != null ? m.LashGapRad : 0f,
                    _car.BatteryTerminalV, _car.BatteryCurrent,
                    _car.WheelSlipRatio(m != null ? m.wheelIndex : 0), m != null ? m.Faults : 0);
            }
        }

        private void TraceBegin(string name)
        {
            _trName = name;
            _tr = new StringBuilder("t,v,wheel_rim_v,iq,wheel_torque,engaged,gap,vbus,ibus,kappa,faults\n");
        }

        private void TraceEnd()
        {
            if (_tr == null) return;
            try
            {
                if (!string.IsNullOrEmpty(_req.resultPath))
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(_req.resultPath),
                        Path.GetFileNameWithoutExtension(_req.resultPath) + "_" + _trName + ".csv"), _tr.ToString());
            }
            catch (Exception e) { Debug.LogWarning($"{Tag} trace write failed: {e.Message}"); }
            _tr = null;
        }

        private void Run(float seconds)
        {
            int n = Mathf.RoundToInt(seconds / _dt);
            for (int i = 0; i < n; i++) Step();
        }

        private float Speed() => Vector3.Dot(_body.linearVelocity, _fwd0);

        /// <summary>Accelerate to v within the tyres' grip (no wheelspin), then
        /// coast briefly so every test starts with the wheels rolling true.</summary>
        private void DriveToSpeed(float v, float timeout)
        {
            SetIq(3f);
            double end = _t + timeout;
            while (Speed() < v && _t < end) Step();
            SetIq(0f);
            Run(0.05f);
        }

        /// <summary>Least-squares slope of v(t) over the next window (m/s²).</summary>
        private void FitSlope(float window, out float a, out float vMid)
        {
            int n = Mathf.RoundToInt(window / _dt);
            double st = 0, sv = 0, stt = 0, stv = 0;
            for (int i = 0; i < n; i++)
            {
                Step();
                double t = i * _dt, v = Speed();
                st += t; sv += v; stt += t * t; stv += t * v;
            }
            double den = n * stt - st * st;
            a = (float)((n * stv - st * sv) / den);
            vMid = (float)(sv / n);
        }

        private static void FitQuadratic(List<float> x, List<float> y, out double c0, out double c1, out double c2)
        {
            double s0 = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
            for (int i = 0; i < x.Count; i++)
            {
                double v = x[i], f = y[i], v2 = v * v;
                s0 += 1; s1 += v; s2 += v2; s3 += v2 * v; s4 += v2 * v2;
                t0 += f; t1 += f * v; t2 += f * v2;
            }
            // Normal equations, solved by Cramer's rule.
            double[,] m = { { s0, s1, s2 }, { s1, s2, s3 }, { s2, s3, s4 } };
            double det = Det(m, null, -1);
            double[] t = { t0, t1, t2 };
            c0 = Det(m, t, 0) / det;
            c1 = Det(m, t, 1) / det;
            c2 = Det(m, t, 2) / det;
        }

        private static double Det(double[,] m, double[] col, int replace)
        {
            double A(int r, int c) => c == replace ? col[r] : m[r, c];
            return A(0, 0) * (A(1, 1) * A(2, 2) - A(1, 2) * A(2, 1))
                 - A(0, 1) * (A(1, 0) * A(2, 2) - A(1, 2) * A(2, 0))
                 + A(0, 2) * (A(1, 0) * A(2, 1) - A(1, 1) * A(2, 0));
        }

        // ============================================================ predictions

        private MotorParams Motor()
        {
            foreach (var w in _design.wheels) if (w.powered) return w.motor;
            return MotorParams.Default();
        }
        private float Gear() => Mathf.Max(1e-3f, Motor().gearRatio);
        private float Eta() => Mathf.Clamp(Motor().efficiency <= 0f ? 1f : Motor().efficiency, 0.05f, 1f);
        private float EtaBack() => Motor().etaBack > 0f ? Mathf.Clamp(Motor().etaBack, 0.05f, 1f) : Eta();
        private float Radius() => _car.GetWheel(0).radius;
        private int Powered() { int n = 0; foreach (var w in _design.wheels) if (w.powered) n++; return n; }

        /// <summary>m + Σ J_spin/r² + (rotors with lash, which the wheel does
        /// not carry) J·G²/r².</summary>
        private float EffMass()
        {
            float r = Radius(), j = 0f;
            for (int i = 0; i < _design.wheels.Count; i++)
            {
                j += _car.WheelSpinInertia(i);
                var w = _design.wheels[i];
                if (w.powered && w.motor.lashRad > 0f)
                    j += w.motor.rotorInertia * w.motor.gearRatio * w.motor.gearRatio;
            }
            return _body.mass + j / (r * r);
        }

        /// <summary>Road force from the motors at Iq (all powered wheels), with
        /// the motor's own friction, through the gearbox in the right direction.</summary>
        private float DriveForce(float iq, float omegaM)
        {
            var p = Motor();
            float tc = Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);
            float tm = p.kt * iq - p.viscousDamping * omegaM - tc * Mathf.Sign(omegaM == 0f ? iq : omegaM);
            float tw = tm * omegaM < 0f ? tm * Gear() / EtaBack() : tm * Gear() * Eta();
            // Friction is counted here, so LossForce leaves the motor out when driving.
            return Powered() * tw / Radius();
        }

        /// <summary>Resisting road force at speed v: rolling resistance, bearings,
        /// aero, and — coasting — the back-driven motors' friction.</summary>
        private float LossForce(float v, bool coasting, float cosTheta = 1f)
        {
            float r = Radius();
            float f = 0f;
            float omegaW = v / r;
            foreach (var w in _design.wheels)
                f += (w.bearingNm + w.bearingNmsPerRad * Mathf.Abs(omegaW)) / r;
            // Rolling resistance acts on the load the tyres carry — the
            // suspension forces, which (like the grip) leave out the wheels'
            // own unsprung weight. Read live rather than assumed as m·g.
            float crr = 0f; foreach (var w in _design.wheels) crr = Mathf.Max(crr, w.rollCrr);
            float fzSum = 0f;
            for (int i = 0; i < _design.wheels.Count; i++) fzSum += _car.WheelLoadN(i);
            f += crr * (fzSum > 0f ? fzSum : _body.mass * -Physics.gravity.y * cosTheta);
            f += 0.5f * AeroDynamics.AirDensity * _car.AeroCdA * v * v;
            if (coasting)
            {
                var p = Motor();
                float tc = Mathf.Max(0f, p.coulombScale) * p.kt * Mathf.Max(0f, p.noLoadCurrent);
                float tm = tc + p.viscousDamping * omegaW * Gear();
                f += Powered() * tm * Gear() / EtaBack() / r;
            }
            return f;
        }

        // ================================================================ checks

        private void Check(string id, string what, float got, float want, float tol,
                           bool abs = false, bool below = false, bool above = false)
        {
            bool ok;
            string crit;
            if (below) { ok = got < want; crit = $"< {want:0.###}"; }
            else if (above) { ok = got > want; crit = $"> {want:0.###}"; }
            else if (abs) { ok = Mathf.Abs(got - want) <= tol; crit = $"{want:0.###} ± {tol:0.###}"; }
            else
            {
                ok = Mathf.Abs(got - want) <= tol * Mathf.Abs(want);
                crit = $"{want:0.####} ± {tol * 100f:0.#} %";
            }
            _checks++;
            if (!ok) _failed++;
            Line($"{id,-3} {(ok ? "ok  " : "FAIL")} {what}: {got:0.####}  (want {crit})");
            _json.Add($"  \"{_checks:00}_{id}\": {{\"what\": \"{what.Replace("\"", "'")}\", \"got\": {got:0.######}, \"want\": {want:0.######}, \"ok\": {(ok ? "true" : "false")}}}");
        }

        private void Line(string s) => _log.AppendLine($"{Tag} {s}");
    }
}
