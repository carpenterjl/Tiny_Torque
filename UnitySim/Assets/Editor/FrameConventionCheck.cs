using System.Text;
using AIHWSim.Bridge;
using AIHWSim.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// <b>[FRAME] — ABI-04's proof that a v7 controller really gets FLU.</b>
    ///
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;UnitySim&gt; \
    ///   -executeMethod AIHWSim.EditorTools.FrameConventionCheck.Report -logFile &lt;log&gt;
    /// </code>
    ///
    /// It does not re-derive the conversion formula — that would only test the
    /// algebra against itself. It spins a real rigid body about each of its own
    /// axes, lets PhysX move it, and reads the motion off the transform: the nose
    /// swung toward the car's left, the right side dropped, the nose dipped.
    /// Each is a positive rotation in a right-handed FLU frame (yaw left, roll
    /// right-side-down, pitch nose-down), so the FLU gyro the controller would
    /// see — the vehicle's own IMU read through <see cref="FluFrame"/> — must
    /// read positive on exactly that axis. Specific force is checked the same
    /// way: +z at rest, +x accelerating forward, +y accelerating left. Every
    /// case runs with the body yawed to several headings, so a world-vs-body
    /// mix-up cannot pass by coincidence.
    /// </summary>
    public static class FrameConventionCheck
    {
        private const string Tag = "[FRAME]";
        private const float Dt = 0.01f;

        private static int _checks;
        private static int _failed;
        private static StringBuilder _log;

        [MenuItem("Tools/AIHWSim/Physics Tests/Run [FRAME] FLU Frame Check", priority = 122)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            _checks = 0;
            _failed = 0;
            _log = new StringBuilder();

            if (Application.isBatchMode)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var oldMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                foreach (float heading in new[] { 0f, 90f, -135f })
                {
                    Rates(heading);
                    Forces(heading);
                }
            }
            finally
            {
                Physics.simulationMode = oldMode;
            }

            Debug.Log(_log.ToString().TrimEnd());
            string summary = _failed == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {_failed} FAILED of {_checks} checks";
            if (_failed == 0) Debug.Log(summary); else Debug.LogError(summary);

            if (exitWhenDone && Application.isBatchMode)
                EditorApplication.Exit(_failed == 0 ? 0 : 1);
        }

        private static Rigidbody MakeBody(float heading, bool gravity)
        {
            var go = new GameObject("frame_probe");
            go.transform.rotation = Quaternion.Euler(0f, heading, 0f);
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = gravity;
            rb.linearDamping = 0f;
            rb.angularDamping = 0f;
            rb.inertiaTensor = Vector3.one;   // isotropic: no gyroscopic coupling
            rb.inertiaTensorRotation = Quaternion.identity;
            return rb;
        }

        /// <summary>Spin about one body axis, see which way the body actually
        /// went, and check the FLU gyro agrees on sign and axis.</summary>
        private static void Rates(float heading)
        {
            // Body axes (Unity local) to spin about, and how to recognise the
            // right-handed-positive motion from the transform afterwards.
            Spin(heading, "yaw left", Vector3.up, 2,
                 (before, after) => Vector3.Dot(after.forward - before.forward, -before.right));
            Spin(heading, "roll right-side-down", Vector3.forward, 0,
                 (before, after) => -Vector3.Dot(after.right - before.right, before.up));
            Spin(heading, "pitch nose-down", Vector3.right, 1,
                 (before, after) => -Vector3.Dot(after.forward - before.forward, before.up));
        }

        private delegate float Motion(Pose before, Pose after);

        private struct Pose
        {
            public Vector3 forward, right, up;
            public Pose(Transform t) { forward = t.forward; right = t.right; up = t.up; }
        }

        private static void Spin(float heading, string name, Vector3 localAxis, int fluAxis, Motion positive)
        {
            foreach (float sign in new[] { 1f, -1f })
            {
                var rb = MakeBody(heading, gravity: false);
                var imu = new ImuSensor();
                rb.angularVelocity = rb.transform.TransformDirection(localAxis) * (0.5f * sign);
                var before = new Pose(rb.transform);
                Physics.Simulate(Dt);
                var after = new Pose(rb.transform);

                imu.Read(rb, Dt, out Vector3 gyroU, out _);
                Vector3 g = FluFrame.Rate(gyroU);
                float moved = positive(before, after);   // > 0 = the named motion happened

                bool signOk = Mathf.Sign(g[fluAxis]) == Mathf.Sign(moved) && Mathf.Abs(moved) > 1e-5f;
                bool aloneOk = true;
                for (int k = 0; k < 3; k++)
                    if (k != fluAxis && Mathf.Abs(g[k]) > 1e-3f) aloneOk = false;
                Check($"heading {heading,5:0} deg, {name} x{sign:+0;-0}: FLU gyro[{fluAxis}] = {g[fluAxis]:+0.000;-0.000} " +
                      $"(motion {moved:+0.00000;-0.00000})", signOk && aloneOk);
                Object.DestroyImmediate(rb.gameObject);
            }
        }

        /// <summary>Specific force: at rest it is +g up; accelerating forward
        /// or left it is positive along that FLU axis.</summary>
        private static void Forces(float heading)
        {
            // At rest under gravity: reaction to gravity, +z (up).
            {
                var rb = MakeBody(heading, gravity: false);
                var imu = new ImuSensor();
                imu.Read(rb, Dt, out _, out _);   // seed the velocity history
                imu.Read(rb, Dt, out _, out Vector3 accU);
                Vector3 a = FluFrame.Vector(accU);
                Check($"heading {heading,5:0} deg, at rest: FLU accel = {a} (+z = up)",
                      Mathf.Abs(a.z - (-Physics.gravity.y)) < 1e-3f && Mathf.Abs(a.x) < 1e-3f && Mathf.Abs(a.y) < 1e-3f);
                Object.DestroyImmediate(rb.gameObject);
            }
            Accelerate(heading, "forward", Vector3.forward, 0);
            Accelerate(heading, "left", Vector3.left, 1);
        }

        private static void Accelerate(float heading, string name, Vector3 localDir, int fluAxis)
        {
            var rb = MakeBody(heading, gravity: false);
            var imu = new ImuSensor();
            imu.Read(rb, Dt, out _, out _);
            // Instantaneous change of velocity over one step: 2 m/s^2 along the
            // body direction. Gravity reaction is still in the reading (+z).
            rb.linearVelocity = rb.transform.TransformDirection(localDir) * (2f * Dt);
            imu.Read(rb, Dt, out _, out Vector3 accU);
            Vector3 a = FluFrame.Vector(accU);
            Check($"heading {heading,5:0} deg, accelerating {name}: FLU accel[{fluAxis}] = {a[fluAxis]:+0.000;-0.000}",
                  Mathf.Abs(a[fluAxis] - 2f) < 1e-3f);
            Object.DestroyImmediate(rb.gameObject);
        }

        private static void Check(string what, bool ok)
        {
            _checks++;
            if (!ok) _failed++;
            _log.AppendLine($"{Tag} {(ok ? "ok  " : "FAIL")} {what}");
        }
    }
}
