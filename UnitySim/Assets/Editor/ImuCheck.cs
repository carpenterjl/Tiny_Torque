using System.Collections.Generic;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[IMU] — SEN-01's checks on the raw MEMS IMU, and VAL-01 R9.</b>
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.ImuCheck.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// <list type="bullet">
    /// <item>Kinematics on a real rigid body moved by PhysX: an IMU mounted
    /// off-centre and rotated reads the body's rate in ITS OWN right-handed
    /// frame, the centripetal ω²r and tangential α·r of its lever arm, and
    /// +1 g up at rest (−1 g mounted upside down).</item>
    /// <item>The digital low-pass is −3 dB at its corner and passes DC.</item>
    /// <item>The per-part errors have the configured spread; the output is
    /// clipped at full scale and lands on whole LSBs; a seed fixes the stream.</item>
    /// <item>R9: an Allan-variance analysis of a two-hour static log of the
    /// FOC twin's IMU recovers the configured noise density (ARW) and bias
    /// instability within 10 %.</item>
    /// </list>
    /// </summary>
    public static class ImuCheck
    {
        private const string Tag = "[IMU]";
        private static int _checks, _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [IMU] MEMS IMU Check", priority = 125)]
        public static void RunFromMenu() => Run(false);

        public static void Report() => Run(true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0; _failed = 0; _log = new StringBuilder();
            int savedSeed = NoiseModel.GlobalSeed;
            NoiseModel.GlobalSeed = 4242;
            if (Application.isBatchMode)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var oldMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                FilterResponse();
                Kinematics();
                ErrorStatistics();
                AllanR9();
            }
            finally
            {
                Physics.simulationMode = oldMode;
                NoiseModel.GlobalSeed = savedSeed;
            }

            Debug.Log(_log.ToString().TrimEnd());
            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);
            if (exitWhenDone && Application.isBatchMode) EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        // ---- the digital low-pass -------------------------------------------

        private static void FilterResponse()
        {
            const float fs = 400f, fc = 50f;
            var f = new Biquad();
            f.LowPass(fc, fs);
            float peak = 0f;
            for (int n = 0; n < 4000; n++)
            {
                float y = f.Step(Mathf.Sin(2f * Mathf.PI * fc * n / fs));
                if (n > 2000) peak = Mathf.Max(peak, Mathf.Abs(y));
            }
            Check($"DLPF gain at its {fc:0} Hz corner {peak:0.000} (want 0.707)", Mathf.Abs(peak - 0.7071f) < 0.02f);
            f.LowPass(fc, fs);
            f.Prime(9.81f);
            float y0 = f.Step(9.81f);
            for (int n = 0; n < 50; n++) y0 = f.Step(9.81f);
            Check("DLPF primed at a constant input stays there (no start-up ramp)", Mathf.Abs(y0 - 9.81f) < 1e-4f);
        }

        // ---- kinematics on a PhysX body --------------------------------------

        private static (Rigidbody, MemsImuSensor) MakeRig(Vector3 mountPos, Vector3 mountEuler)
        {
            var go = new GameObject("imu_body");
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.linearDamping = 0f;
            rb.angularDamping = 0f;
            rb.maxAngularVelocity = 100f;
            rb.centerOfMass = Vector3.zero;
            rb.inertiaTensor = Vector3.one;
            rb.inertiaTensorRotation = Quaternion.identity;
            var child = new GameObject("imu");
            child.transform.SetParent(go.transform, false);
            child.transform.localPosition = mountPos;
            child.transform.localRotation = Quaternion.Euler(mountEuler);
            var imu = child.AddComponent<MemsImuSensor>();
            imu.sensorName = "imu_check";
            imu.spec = ImuSpec.Ideal();
            imu.Bind(null, go.transform);
            return (rb, imu);
        }

        private static void Kinematics()
        {
            const float dt = 0.0025f, r = 0.1f;

            // At rest, upright and upside down.
            var (rb, imu) = MakeRig(Vector3.zero, Vector3.zero);
            for (int k = 0; k < 10; k++) { Physics.Simulate(dt); imu.PhysicsStep(0, dt); }
            var buf = new float[6];
            imu.Sample(dt, buf, 0);
            Check($"at rest, upright: accel z {buf[5]:0.000} = +1 g, x/y/gyro 0",
                  Mathf.Abs(buf[5] - 9.81f) < 0.01f && Mathf.Abs(buf[3]) < 1e-4f && Mathf.Abs(buf[4]) < 1e-4f
                  && Mathf.Abs(buf[0]) + Mathf.Abs(buf[1]) + Mathf.Abs(buf[2]) < 1e-5f);
            Object.DestroyImmediate(rb.gameObject);

            (rb, imu) = MakeRig(Vector3.zero, new Vector3(0f, 0f, 180f));
            for (int k = 0; k < 10; k++) { Physics.Simulate(dt); imu.PhysicsStep(0, dt); }
            imu.Sample(dt, buf, 0);
            Check($"at rest, mounted upside down: accel z {buf[5]:0.000} = -1 g", Mathf.Abs(buf[5] + 9.81f) < 0.01f);
            Object.DestroyImmediate(rb.gameObject);

            // Yawing left at 2 rad/s, the chip 0.1 m ahead of the centre and
            // turned to face the body's right: its x = body −y, its y = body +x.
            foreach (float heading in new[] { 0f, 120f })
            {
                (rb, imu) = MakeRig(new Vector3(0f, 0f, r), new Vector3(0f, 90f, 0f));
                rb.transform.rotation = Quaternion.Euler(0f, heading, 0f);
                const float w = 2f;
                rb.angularVelocity = new Vector3(0f, -w, 0f);     // Unity: −y = yaw left
                for (int k = 0; k < 40; k++) { Physics.Simulate(dt); imu.PhysicsStep(0, dt); }
                imu.Sample(dt, buf, 0);
                Check($"heading {heading:0}: yaw-left 2 rad/s reads gyro z {buf[2]:+0.000;-0.000} in the chip frame",
                      Mathf.Abs(buf[2] - w) < 1e-3f && Mathf.Abs(buf[0]) < 1e-3f && Mathf.Abs(buf[1]) < 1e-3f);
                Check($"heading {heading:0}: centripetal {buf[4]:+0.000;-0.000} = -w^2 r on the chip's y (toward the axis)",
                      Mathf.Abs(buf[4] + w * w * r) < 0.01f && Mathf.Abs(buf[3]) < 0.01f);
                Object.DestroyImmediate(rb.gameObject);
            }

            // Spinning up at alpha = 5 rad/s^2: tangential alpha*r to the body's
            // left, which is the chip's −x.
            {
                (rb, imu) = MakeRig(new Vector3(0f, 0f, r), new Vector3(0f, 90f, 0f));
                const float alpha = 5f;
                float wNow = 0f;
                for (int k = 0; k < 80; k++)
                {
                    wNow += alpha * dt;
                    rb.angularVelocity = new Vector3(0f, -wNow, 0f);
                    Physics.Simulate(dt);
                    imu.PhysicsStep(0, dt);
                }
                imu.Sample(dt, buf, 0);
                Check($"spin-up 5 rad/s^2: tangential {buf[3]:+0.000;-0.000} = -alpha r on the chip's x",
                      Mathf.Abs(buf[3] + alpha * r) < 0.02f);
                Object.DestroyImmediate(rb.gameObject);
            }
        }

        // ---- per-part errors, clip, LSB, seeding ----------------------------

        private static void ErrorStatistics()
        {
            var spec = new ImuSpec();
            double sum = 0, sumSq = 0; int n = 0;
            double sfSum = 0, sfSq = 0;
            for (int s = 0; s < 600; s++)
            {
                var g = spec.Gyro(new System.Random(1000 + s));
                Vector3 b = g.Bias;
                Vector3 one = g.Misalign(Vector3.right);
                for (int a = 0; a < 3; a++) { sum += b[a]; sumSq += b[a] * b[a]; n++; }
                sfSum += one.x - 1.0; sfSq += (one.x - 1.0) * (one.x - 1.0);
            }
            double sd = System.Math.Sqrt(sumSq / n - (sum / n) * (sum / n)) / ImuSpec.DegToRad;
            // The turn-on bias is joined by the flicker states (started stationary).
            double flick = spec.gyroBiasInstabDph / 3600.0 / ImuTriad.FlickerFloorPerUnitSigma()
                           * System.Math.Sqrt(ImuTriad.FlickerTaus.Length);
            double want = System.Math.Sqrt(spec.gyroTurnOnBiasDps * spec.gyroTurnOnBiasDps + flick * flick);
            Check($"gyro power-up bias spread {sd:0.000} deg/s vs {want:0.000}", System.Math.Abs(sd / want - 1.0) < 0.1);
            double sfSd = System.Math.Sqrt(sfSq / 600.0) * 100.0;
            Check($"gyro scale-factor error spread {sfSd:0.000} % vs {spec.gyroScaleErrPct:0.000} %",
                  System.Math.Abs(sfSd / spec.gyroScaleErrPct - 1.0) < 0.15);

            var clean = ImuSpec.Ideal();
            clean.gyroFullScaleDps = 500f;
            var t = clean.Gyro(new System.Random(1));
            t.Step(new Vector3(1000f, -1000f, 0.3f) * ImuSpec.DegToRad, 0.0025f, 0f);
            Vector3 o = t.Output();
            float lsb = t.Lsb;
            Check($"clipped at full scale: +{o.x / ImuSpec.DegToRad:0.00} / {o.y / ImuSpec.DegToRad:0.00} deg/s",
                  Mathf.Abs(o.x - (t.FullScale - lsb)) < 1e-6f && Mathf.Abs(o.y + t.FullScale) < 1e-6f);
            Check($"quantized to whole LSBs ({lsb / ImuSpec.DegToRad:0.0000} deg/s)",
                  Mathf.Abs(o.z / lsb - Mathf.Round(o.z / lsb)) < 1e-3f);

            float[] A = Stream(7), B = Stream(7), C = Stream(8);
            bool same = true, diff = false;
            for (int i = 0; i < A.Length; i++) { same &= A[i] == B[i]; diff |= A[i] != C[i]; }
            Check("same seed, same stream; another seed, another stream", same && diff);
        }

        private static float[] Stream(int seed)
        {
            var g = new ImuSpec().Gyro(new System.Random(seed));
            var v = new float[200];
            for (int i = 0; i < v.Length; i++) { g.Step(Vector3.zero, 0.0025f, 50f); v[i] = g.Output().x; }
            return v;
        }

        // ---- R9: Allan variance of a static log ------------------------------

        private static ImuSpec TwinSpec()
        {
            var d = VehiclePresets.Resolve("Opus Vector FOC");
            if (d != null)
                foreach (var s in d.sensors)
                    if (s.kind == Bridge.SensorType.Imu6 && s.imu != null) return s.imu;
            return new ImuSpec();
        }

        private static void AllanR9()
        {
            var spec = TwinSpec();
            const float physHz = 400f, odrHz = 200f;
            const double hours = 2.0;
            float dt = 1f / physHz;
            int decim = Mathf.RoundToInt(physHz / odrHz);
            int nOut = (int)(hours * 3600.0 * odrHz);
            var g = spec.Gyro(new System.Random(9001));
            var a = spec.Accel(new System.Random(9002));
            var gx = new double[3][];
            var ax = new double[3][];
            for (int k = 0; k < 3; k++) { gx[k] = new double[nOut]; ax[k] = new double[nOut]; }
            var still = new Vector3(0f, 0f, 9.80665f);
            for (int i = 0; i < nOut; i++)
            {
                for (int s = 0; s < decim; s++)
                {
                    g.Step(Vector3.zero, dt, spec.dlpfHz);
                    a.Step(still, dt, spec.dlpfHz);
                }
                Vector3 go = g.Output(), ao = a.Output();
                for (int k = 0; k < 3; k++) { gx[k][i] = go[k]; ax[k][i] = ao[k]; }
            }
            AllanTriad("gyro", gx, odrHz, g, 3600.0 / ImuSpec.DegToRad, "deg/h");
            AllanTriad("accel", ax, odrHz, a, 1e6 / ImuSpec.G0, "ug");
        }

        /// <summary>Allan deviation of x at cluster size m (overlapping estimator).</summary>
        private static double Adev(double[] x, int m, double fs)
        {
            int n = x.Length;
            if (3 * m > n) return double.NaN;
            var c = new double[n + 1];
            for (int i = 0; i < n; i++) c[i + 1] = c[i] + x[i];
            double acc = 0; int cnt = 0;
            for (int i = 0; i + 2 * m <= n; i++)
            {
                double d = (c[i + 2 * m] - 2.0 * c[i + m] + c[i]) / m;
                acc += d * d; cnt++;
            }
            return System.Math.Sqrt(acc / (2.0 * cnt));
        }

        private static void AllanTriad(string what, double[][] x, double fs, ImuTriad model,
                                       double toUnit, string unit)
        {
            // The model curve: white (density/sqrt(2 tau)), the quantizer's
            // own white noise, and the flicker part.
            double d = model.Density, q = model.Lsb;
            double Expected(double tau) => System.Math.Sqrt(d * d / (2 * tau)
                + (q * q / 12.0) / (fs * tau) + System.Math.Pow(model.FlickerAdev(tau), 2));

            double At(double tau)
            {
                int m = System.Math.Max(1, (int)System.Math.Round(tau * fs));
                double v = 0;
                for (int k = 0; k < 3; k++) { double s = Adev(x[k], m, fs); v += s * s; }
                return System.Math.Sqrt(v / 3.0);
            }

            // ARW: density recovered from the short-tau end, the other terms removed.
            double tau0 = 0.1, s0 = At(tau0);
            double white2 = s0 * s0 - (q * q / 12.0) / (fs * tau0) - System.Math.Pow(model.FlickerAdev(tau0), 2);
            double dEst = System.Math.Sqrt(System.Math.Max(0, white2) * 2 * tau0);
            Check($"R9 {what}: density from the Allan curve {dEst / d:0.000} x configured",
                  System.Math.Abs(dEst / d - 1.0) < 0.10);

            // Bias instability: the floor of the measured curve.
            double floor = double.MaxValue, floorTau = 0;
            var line = new StringBuilder();
            foreach (double tau in new[] { 0.1, 1.0, 3.0, 10.0, 30.0, 100.0, 300.0, 1000.0 })
            {
                double s = At(tau);
                line.Append($" {tau:0.#}s:{s * toUnit:0.##}/{Expected(tau) * toUnit:0.##}");
                if (tau >= 10 && tau <= 600 && s < floor) { floor = s; floorTau = tau; }
            }
            _log.AppendLine($"{Tag}      {what} Allan dev ({unit}, measured/model):{line}");
            double bi = model.BiasInstab;
            Check($"R9 {what}: Allan floor {floor * toUnit:0.00} {unit} at {floorTau:0} s vs bias instability {bi * toUnit:0.00} {unit}",
                  System.Math.Abs(floor / bi - 1.0) < 0.10);
        }

        private static void Check(string what, bool ok)
        {
            _checks++;
            if (!ok) _failed++;
            _log.AppendLine($"{Tag} {(ok ? "ok  " : "FAIL")} {what}");
        }
    }
}
