using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Runtime half of the design dump: builds every vehicle design the project
    /// can produce and writes back the physics state the engine was actually
    /// handed — Rigidbody, chassis box and every WheelCollider field.
    ///
    /// <b>Why this must run in play mode.</b> Everything a car IS happens in
    /// <see cref="CarVehicle.Awake"/>, and the editor does not call Awake outside
    /// play mode. Built in edit mode the cars come back carrying Unity's stock
    /// defaults — mass 1, angular damping 0.05 — so every design looks identical
    /// and the dump would cheerfully "prove" bit-identity across any change at
    /// all. That is a worse outcome than having no check, so it lives here,
    /// beside <see cref="MissionAutorun"/> and shaped like it: inert unless the
    /// editor half left a request file, and it CONSUMES that request before
    /// acting on it — one reader, one run.
    ///
    /// <b>Why it exists alongside the Opus mission.</b> The mission is the
    /// project's bit-identity gate and a better test of the integrated result
    /// than anything here, but it drives exactly ONE design. A new serialized
    /// field with a wrong initialiser would move the other nine presets, and
    /// every saved design in <c>UnitySim/Vehicles/</c>, without the mission
    /// shifting a micron. This closes that gap, and it is cheap — no physics
    /// steps, just build each car and read back what it was told.
    /// </summary>
    public static class VehicleDesignDumpRun
    {
        /// <summary>Where the editor half leaves the output path. A file rather
        /// than a static field because entering play mode triggers a domain
        /// reload that wipes statics.</summary>
        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "aihwsim_design_dump_request.txt");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!File.Exists(RequestPath)) return;
            string outPath;
            try
            {
                outPath = File.ReadAllText(RequestPath).Trim();
                File.Delete(RequestPath);
            }
            catch { return; }
            if (string.IsNullOrEmpty(outPath)) return;

            int n = 0, failed = 0;
            try { Write(outPath, out n, out failed); }
            catch (System.Exception e)
            {
                Debug.LogError($"[DUMP] {e}");
                failed++;
            }
            Debug.Log($"[DUMP] {n} designs, {failed} failed -> {outPath}");

#if UNITY_EDITOR
            if (Application.isBatchMode)
                UnityEditor.EditorApplication.Exit(failed == 0 ? 0 : 2);
            else
                UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        private static void Write(string outPath, out int n, out int failed)
        {
            var sb = new StringBuilder();
            n = 0; failed = 0;
            foreach (var (label, design) in Designs())
            {
                sb.Append("=== ").Append(label).Append('\n');
                try { Dump(sb, design); n++; }
                catch (System.Exception e)
                {
                    sb.Append("  BUILD FAILED: ").Append(e.Message).Append('\n');
                    Debug.LogError($"[DUMP] {label}: {e.Message}");
                    failed++;
                }
            }
            File.WriteAllText(outPath, sb.ToString());
        }

        private static IEnumerable<(string, VehicleDesign)> Designs()
        {
            // The nine code presets...
            foreach (var (name, build) in VehiclePresets.All)
                yield return ("preset:" + name, build());

            // ...the fallback that is not in All but is reached everywhere...
            yield return ("default:Stock RC", VehicleDesign.Default());

            // ...and every design actually saved to disk. Read the directory
            // rather than VehicleLibrary.List(), which hides anything over 50 kg:
            // the point is to cover what is ON DISK, not what the UI will show.
            string dir = VehicleLibrary.Dir;
            if (!Directory.Exists(dir)) yield break;
            var files = Directory.GetFiles(dir, "*.json");
            System.Array.Sort(files, string.CompareOrdinal);
            foreach (string f in files)
            {
                VehicleDesign d = null;
                try { d = JsonUtility.FromJson<VehicleDesign>(File.ReadAllText(f)); }
                catch { }
                if (d != null) yield return ("saved:" + Path.GetFileName(f), d);
            }
        }

        private static void Dump(StringBuilder sb, VehicleDesign design)
        {
            var built = VehicleFactory.Build(design, Vector3.zero, Quaternion.identity,
                                             previewKinematic: false);
            var car = built.car;
            if (car == null) { sb.Append("  no car\n"); return; }
            try
            {
                var rb = car.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Row(sb, "rb.mass", rb.mass);
                    Row(sb, "rb.centerOfMass", rb.centerOfMass);
                    Row(sb, "rb.inertiaTensor", rb.inertiaTensor);
                    Row(sb, "rb.inertiaTensorRotation", rb.inertiaTensorRotation);
                    Row(sb, "rb.linearDamping", rb.linearDamping);
                    Row(sb, "rb.angularDamping", rb.angularDamping);
                    Row(sb, "rb.maxAngularVelocity", rb.maxAngularVelocity);
                    Row(sb, "rb.maxDepenetrationVelocity", rb.maxDepenetrationVelocity);
                }

                var box = car.GetComponent<BoxCollider>();
                if (box != null)
                {
                    Row(sb, "box.size", box.size);
                    Row(sb, "box.center", box.center);
                    Row(sb, "box.contactOffset", box.contactOffset);
                }

                var wcs = car.GetComponentsInChildren<WheelCollider>(true);
                for (int i = 0; i < wcs.Length; i++)
                {
                    var w = wcs[i];
                    string p = $"wc[{i}]{w.name}.";
                    Row(sb, p + "localPosition", w.transform.localPosition);
                    Row(sb, p + "localRotation", w.transform.localRotation);
                    Row(sb, p + "mass", w.mass);
                    Row(sb, p + "radius", w.radius);
                    Row(sb, p + "suspensionDistance", w.suspensionDistance);
                    Row(sb, p + "center", w.center);
                    Row(sb, p + "forceAppPointDistance", w.forceAppPointDistance);
                    Row(sb, p + "wheelDampingRate", w.wheelDampingRate);
                    var s = w.suspensionSpring;
                    Row(sb, p + "spring.spring", s.spring);
                    Row(sb, p + "spring.damper", s.damper);
                    Row(sb, p + "spring.targetPosition", s.targetPosition);
                    Curve(sb, p + "fwd", w.forwardFriction);
                    Curve(sb, p + "side", w.sidewaysFriction);
                }

                // Spin inertia is integrated by CarVehicle itself on the brush
                // path, so it appears in no engine field — and it is one of the
                // numbers this dump exists to protect.
                for (int i = 0; i < car.WheelCount; i++)
                    Row(sb, $"car.spinInertia[{i}]", car.WheelSpinInertia(i));
            }
            finally
            {
                // Immediate, not deferred: otherwise all 21 cars coexist until
                // the end of frame and the later ones build inside the earlier
                // ones' colliders.
                if (car != null) Object.DestroyImmediate(car.gameObject);
            }
        }

        private static void Curve(StringBuilder sb, string p, WheelFrictionCurve c)
        {
            Row(sb, p + ".extremumSlip", c.extremumSlip);
            Row(sb, p + ".extremumValue", c.extremumValue);
            Row(sb, p + ".asymptoteSlip", c.asymptoteSlip);
            Row(sb, p + ".asymptoteValue", c.asymptoteValue);
            Row(sb, p + ".stiffness", c.stiffness);
        }

        private static void Row(StringBuilder sb, string k, float v) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

        private static void Row(StringBuilder sb, string k, Vector3 v) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(v.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(v.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(v.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

        private static void Row(StringBuilder sb, string k, Quaternion q) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(q.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.z.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.w.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }
}
