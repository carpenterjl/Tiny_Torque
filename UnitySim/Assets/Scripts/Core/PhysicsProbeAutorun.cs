using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using AIHWSim.Vehicles;

namespace AIHWSim.Core
{
    /// <summary>
    /// Settles the physics-debug car and reports what it measures — the static
    /// half of the verification suite (P2 and P0b), which is the part that has
    /// to pass before any dynamic number means anything.
    ///
    /// <b>P2 is the largest unverified assumption in this whole vehicle.</b>
    /// Every spring rate, every <c>suspTargetPos</c> and the wheel Y offset were
    /// derived from a model of where a WheelCollider comes to rest:
    /// <c>p_eq = targetPosition − W/(k·D)</c>. That is inferred from the worked
    /// example in CarVehicle's own class comment, not from any documented Unity
    /// force law. If the measured compressions do not land at 0.50 the model is
    /// wrong and the spring set has to be re-derived from what is measured here
    /// rather than from what was predicted.
    ///
    /// Inert unless a request file exists, and it consumes that file before
    /// acting — one reader, one run — the same shape as MissionAutorun.
    /// </summary>
    public sealed class PhysicsProbeAutorun : MonoBehaviour
    {
        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "aihwsim_phys_probe_request.txt");

        /// <summary>Settle time before reading. Long enough for a 1.3 Hz heave
        /// mode at ζ≈0.3 to be dead: that decays ~e-folding per cycle, so five
        /// seconds is six or seven cycles.</summary>
        private const float SettleSec = 5f;

        private string _outPath;
        private float _t;
        private CarVehicle _car;
        private float _minY = float.MaxValue, _maxY = float.MinValue;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Attach()
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

            var go = new GameObject("PhysicsProbeAutorun");
            DontDestroyOnLoad(go);
            go.AddComponent<PhysicsProbeAutorun>()._outPath = outPath;
        }

        private void FixedUpdate()
        {
            if (_car == null)
            {
                _car = FindFirstObjectByType<CarVehicle>();
                if (_car == null) return;
            }
            _t += Time.fixedDeltaTime;

            // Track the ride-height envelope over the last second, so "settled"
            // is something measured rather than assumed.
            if (_t > SettleSec - 1f)
            {
                float y = _car.transform.position.y;
                if (y < _minY) _minY = y;
                if (y > _maxY) _maxY = y;
            }
            if (_t < SettleSec) return;

            Report();
            enabled = false;
#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(0);
            else UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        private void Report()
        {
            var sb = new StringBuilder();
            var rb = _car.GetComponent<Rigidbody>();
            var inv = CultureInfo.InvariantCulture;

            int n = _car.WheelCount;
            float total = 0f, front = 0f;
            for (int i = 0; i < n; i++)
            {
                float fz = _car.GetSuspensionForce(i);
                total += fz;
                if (_car.GetWheelLocalPos(i).z > 0f) front += fz;
            }

            float mass = rb != null ? rb.mass : 0f;
            float weight = mass * Mathf.Abs(Physics.gravity.y);

            // The wheel CENTRE's height above ground, which is what "ride height"
            // means and what the loaded radius (0.349) is comparable against.
            // Not the collider origin: that is the TOP of travel, and the hub
            // hangs GetSuspensionCompression * suspensionDistance below it. The
            // first version of this line reported the origin and made a car
            // sitting 150 mm high look like it was only 20 mm out.
            float rideH = 0f;
            for (int i = 0; i < n; i++)
                rideH += _car.GetWheelHubWorldY(i);
            if (n > 0) rideH /= n;
            var e = _car.transform.rotation.eulerAngles;

            void L(string k, object v) => sb.Append(k).Append(' ').Append(v).Append('\n');

            L("P2.mass_kg", mass.ToString("0.###", inv));
            L("P2.weight_N", weight.ToString("0.##", inv));
            L("P2.sumFz_N", total.ToString("0.##", inv));
            L("P2.sumFz_over_weight", (weight > 0f ? total / weight : 0f).ToString("0.####", inv));
            L("P2.front_load_pct", (total > 1f ? 100f * front / total : 0f).ToString("0.##", inv));
            for (int i = 0; i < n; i++)
            {
                L($"P2.susp_{i}", _car.GetSuspensionCompression(i).ToString("0.####", inv));
                L($"P2.fz_{i}_N", _car.GetSuspensionForce(i).ToString("0.##", inv));
                L($"P2.grounded_{i}", _car.WheelGrounded(i) ? "1" : "0");
                L($"P2.spinInertia_{i}", _car.WheelSpinInertia(i).ToString("0.####", inv));
            }
            L("P0b.ride_height_m", rideH.ToString("0.####", inv));
            L("P0b.ride_band_mm", ((_maxY - _minY) * 1000f).ToString("0.###", inv));
            L("P2.pitch_deg", Wrap180(e.x).ToString("0.###", inv));
            L("P2.roll_deg", Wrap180(e.z).ToString("0.###", inv));
            L("P2.pos_y", _car.transform.position.y.ToString("0.####", inv));

            File.WriteAllText(_outPath, sb.ToString());
            Debug.Log("[PHYS] static probe ->\n" + sb);
        }

        private static float Wrap180(float d) => d > 180f ? d - 360f : d;
    }
}
