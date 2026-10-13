using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIHWSim.Bridge;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Domain randomization for robustness, not training (SIM_TO_REAL_PLAN
    /// §9.2): the same firmware drives the Opus mission N times, each run on
    /// a car whose physical parameters are drawn from ranges around the
    /// nominal design — the firmware's own parameter file stays nominal, as
    /// the car's will be — and the campaign is judged on worst-case and
    /// percentile metrics, not on one lucky run.
    ///
    /// Every run is reproducible from (campaign seed, run index) alone: the
    /// sampled values and the sensor-noise seed both come from it, and both
    /// are written into the result. <c>-opusCampaignOnly k</c> reruns run k by
    /// itself. Corner cases (<c>-opusCampaignCorners 1</c>) set one parameter
    /// at a time to each end of its range, everything else nominal.
    ///
    /// Each run rebuilds the car: the next design goes into the session and
    /// the track scene reloads, exactly as the first run loaded it, so a run
    /// inside a campaign is the same run as one started on its own.
    /// </summary>
    public static class Campaign
    {
        /// <summary>One randomized parameter. <see cref="apply"/> gets the
        /// design and a normalized draw u in [-1, 1] (0 = nominal; the range
        /// ends at ±1, already scaled by the campaign's width).</summary>
        public sealed class Knob
        {
            public string name;      // short, CSV-safe
            public string unit;      // "x" (a factor), or the unit of an offset
            public float lo, nom, hi;
            public bool perWheel;    // one draw per wheel (name_0..3)
            /// <summary>The range runs from nom to hi only (a delay that can
            /// only add, a pack that can only be emptier): u = -1 is nominal.</summary>
            public bool oneSided;
            public Action<VehicleDesign, int, float> apply;   // design, wheel (-1 = whole car), value

            /// <summary>The value draw u in [-1, 1] maps to at this width. u = 0
            /// (two-sided) is exactly nominal; each half of the range gets half
            /// the draws, so an asymmetric range stays centred on nominal.</summary>
            public float Value(float u, float width)
            {
                if (oneSided) return nom + (hi - nom) * width * 0.5f * (u + 1f);
                float x = Mathf.Clamp(u * width, -1f, 1f);
                return x < 0f ? nom + (nom - lo) * x : nom + (hi - nom) * x;
            }

            public string Range =>
                string.Format(CultureInfo.InvariantCulture, oneSided ? "{1:0.###} -> {2:0.###} {3}" : "{0:0.###} .. {1:0.###} .. {2:0.###} {3}",
                              lo, nom, hi, unit == "x" ? "(factor)" : unit);
        }

        /// <summary>One run of a campaign: its label, noise seed and draws.</summary>
        [Serializable]
        public sealed class RunSpec
        {
            public int index;
            public string label = "";        // "mc 17", "corner mass lo", ...
            public int noiseSeed;
            public string[] names = new string[0];
            public float[] u = new float[0];  // normalized draws, same order as names
        }

        // ------------------------------------------------------------ RNG --
        // SplitMix64: tiny, portable and documented, so a run's draws can be
        // recomputed anywhere from (seed, index, slot).
        private static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform in [-1, 1) for (seed, run, slot).</summary>
        public static float Draw(int seed, int run, int slot)
        {
            ulong h = Mix(Mix(Mix((ulong)(uint)seed) ^ (ulong)(uint)run) ^ (ulong)(uint)slot);
            return (float)((h >> 11) * (1.0 / (1UL << 53)) * 2.0 - 1.0);
        }

        /// <summary>A non-zero sensor-noise seed for (seed, run).</summary>
        public static int NoiseSeed(int seed, int run)
        {
            int s = (int)(Mix(Mix((ulong)(uint)seed ^ 0xA5A5A5A5UL) ^ (ulong)(uint)run) & 0x7FFFFFFFUL);
            return s == 0 ? 1 : s;
        }

        // ---------------------------------------------------------- plan --

        /// <summary>The runs of a campaign, in order. Monte-Carlo runs first
        /// (indices 0..runs-1), then the corners when asked for.</summary>
        public static List<RunSpec> Plan(int runs, int seed, bool corners, string only, int cornerNoiseSeed = 0)
        {
            int cSeed = cornerNoiseSeed != 0 ? cornerNoiseSeed : NoiseSeed(seed, -1);
            var knobs = Knobs();
            var names = new List<string>();
            var nominal = new List<float>();     // the draw that is nominal: 0, or -1 one-sided
            foreach (var k in knobs)
            {
                int n = k.perWheel ? 4 : 1;
                for (int w = 0; w < n; w++)
                {
                    names.Add(k.perWheel ? k.name + "_" + w : k.name);
                    nominal.Add(k.oneSided ? -1f : 0f);
                }
            }
            var plan = new List<RunSpec>();
            for (int i = 0; i < runs; i++)
            {
                var r = new RunSpec { index = i, label = "mc " + i, noiseSeed = NoiseSeed(seed, i),
                                      names = names.ToArray(), u = new float[names.Count] };
                for (int j = 0; j < names.Count; j++) r.u[j] = Draw(seed, i, j);
                plan.Add(r);
            }
            if (corners)
            {
                // The nominal car first: the corners' reference, and with
                // -opusSeed the same run as a plain mission. A one-sided knob's
                // low end is its nominal, so it has a high corner only.
                int idx = runs;
                plan.Add(new RunSpec { index = idx++, label = "corner nominal", noiseSeed = cSeed,
                                       names = names.ToArray(), u = nominal.ToArray() });
                // A per-wheel knob also gets a split corner: left wheels at the
                // low end, right at the high (wheel order FL, FR, RL, RR) — the
                // mismatch, not the shift, is what a differential notices.
                const float Split = 2f;
                foreach (var k in knobs)
                    foreach (float end in k.oneSided ? new[] { 1f } : k.perWheel ? new[] { -1f, 1f, Split } : new[] { -1f, 1f })
                    {
                        string tag = end == Split ? "split" : end < 0 ? "lo" : "hi";
                        var r = new RunSpec { index = idx++, label = $"corner {k.name} {tag}",
                                              noiseSeed = cSeed, names = names.ToArray(), u = nominal.ToArray() };
                        for (int j = 0; j < names.Count; j++)
                        {
                            string n = names[j];
                            if (n == k.name) r.u[j] = end;
                            else if (k.perWheel && n.StartsWith(k.name + "_"))
                                r.u[j] = end != Split ? end : (n.EndsWith("_0") || n.EndsWith("_2") ? -1f : 1f);
                        }
                        plan.Add(r);
                    }
            }
            if (!string.IsNullOrEmpty(only))
            {
                // In the order given, repeats included: "0,0" runs run 0 twice,
                // which is how a reload is shown to change nothing.
                var byIndex = plan.ToDictionary(r => r.index);
                plan = only.Split(',').Select(t => int.Parse(t.Trim(), CultureInfo.InvariantCulture))
                           .Where(byIndex.ContainsKey).Select(i => byIndex[i]).ToList();
            }
            return plan;
        }

        /// <summary>The nominal design with this run's draws applied, each
        /// scaled by <paramref name="width"/> (1 = the §9.2 ranges).</summary>
        public static VehicleDesign DesignFor(string vehicle, RunSpec run, float width)
        {
            var d = VehiclePresets.Resolve(vehicle);
            if (d == null) return null;
            var knobs = Knobs();
            int j = 0;
            foreach (var k in knobs)
            {
                // A knob at exactly nominal is not applied at all, so the
                // nominal car is the preset bit for bit.
                if (k.perWheel)
                {
                    for (int w = 0; w < 4 && w < d.wheels.Count; w++, j++)
                    {
                        float v = k.Value(run.u[j], width);
                        if (v != k.nom) k.apply(d, w, v);
                    }
                }
                else
                {
                    float v = k.Value(run.u[j], width);
                    if (v != k.nom) k.apply(d, -1, v);
                    j++;
                }
            }
            return d;
        }

        /// <summary>Column name -> draw -> physical value, for the CSV.</summary>
        public static Dictionary<string, Func<float, float>> Describe(float width)
        {
            var map = new Dictionary<string, Func<float, float>>();
            foreach (var k in Knobs())
            {
                var kk = k;
                if (k.perWheel) for (int w = 0; w < 4; w++) map[k.name + "_" + w] = u => kk.Value(u, width);
                else map[k.name] = u => kk.Value(u, width);
            }
            return map;
        }

        /// <summary>§9.2 rows not sampled yet, for the report (none since
        /// VAL-13's close-out: every row of the table has a knob).</summary>
        public const string NotYet = "";

        // ---------------------------------------------------------- knobs --

        /// <summary>
        /// The §9.2 table, every row. Each range is centred on the nominal
        /// design; draws are uniform. New knobs go at the END: a run's draws
        /// are (seed, run, slot), so an insertion would change every run.
        /// </summary>
        public static List<Knob> Knobs()
        {
            const float Deg = Mathf.Deg2Rad;
            var list = new List<Knob>
            {
                // Tyre-road friction, all four (the floor), then each tyre.
                new Knob { name = "grip", unit = "x", lo = 0.6f, nom = 1f, hi = 1.2f,
                           apply = (d, w, v) => { foreach (var ws in d.wheels) ws.gripMult = (ws.gripMult > 0f ? ws.gripMult : 1f) * v; } },
                new Knob { name = "grip_wheel", unit = "x", lo = 0.95f, nom = 1f, hi = 1.05f, perWheel = true,
                           apply = (d, w, v) => { var ws = d.wheels[w]; ws.gripMult = (ws.gripMult > 0f ? ws.gripMult : 1f) * v; } },
                // Lateral peak and slope together (µy/µx); a stand-in for C_alpha.
                new Knob { name = "lat_grip", unit = "x", lo = 0.7f, nom = 1f, hi = 1.3f,
                           apply = (d, w, v) => { foreach (var ws in d.wheels) ws.muLatRatio = (ws.muLatRatio > 0f ? ws.muLatRatio : 1f) * v; } },
                // Mass, then CoM and yaw inertia of the car that results.
                new Knob { name = "mass", unit = "x", lo = 0.9f, nom = 1f, hi = 1.1f,
                           apply = (d, w, v) => d.mass += (v - 1f) * MassProperties.Compute(d).totalMass },
                new Knob { name = "com_fwd", unit = "mm", lo = -10f, nom = 0f, hi = 10f,
                           apply = (d, w, v) =>
                           {
                               var c = d.hasComOverride ? d.comOverride : MassProperties.Compute(d).com;
                               d.hasComOverride = true;
                               d.comOverride = c + new Vector3(0f, 0f, v * 0.001f);
                           } },
                new Knob { name = "yaw_inertia", unit = "x", lo = 0.8f, nom = 1f, hi = 1.2f,
                           apply = (d, w, v) =>
                           {
                               float izz = d.inertiaOverride.z > 0f ? d.inertiaOverride.z : MassProperties.Compute(d).inertiaDiag.y;
                               d.inertiaOverride = new Vector3(d.inertiaOverride.x, d.inertiaOverride.y, izz * v);
                           } },
                // Motors: each its own kt (mismatched); every driver's current loop.
                new Knob { name = "kt", unit = "x", lo = 0.925f, nom = 1f, hi = 1.075f, perWheel = true,
                           apply = (d, w, v) => { var m = d.wheels[w].motor; m.kt *= v; d.wheels[w].motor = m; } },
                new Knob { name = "current_loop", unit = "x", lo = 0.5f, nom = 1f, hi = 2f,
                           apply = (d, w, v) => { foreach (var ws in d.wheels) { var m = ws.motor; m.currentLoopHz *= v; ws.motor = m; } } },
                // Rolling radius: the tyre rolls on another radius than the
                // spec sheet (and the firmware) says.
                new Knob { name = "radius", unit = "x", lo = 0.98f, nom = 1f, hi = 1.02f, perWheel = true,
                           apply = (d, w, v) =>
                           {
                               var ws = d.wheels[w];
                               if (ws.specRadius <= 0f) ws.specRadius = ws.radius;
                               ws.radius *= v;
                           } },
                // Steering: linkage gain and trim offset (steerMap), servo speed.
                new Knob { name = "steer_gain", unit = "x", lo = 0.9f, nom = 1f, hi = 1.1f,
                           apply = (d, w, v) => MapSteer(d, v, 0f) },
                new Knob { name = "steer_offset", unit = "deg", lo = -2f, nom = 0f, hi = 2f,
                           apply = (d, w, v) => MapSteer(d, 1f, v / Mathf.Max(1f, MaxSteerDeg(d))) },
                new Knob { name = "servo_bw", unit = "x", lo = 0.7f, nom = 1f, hi = 1.3f,
                           apply = (d, w, v) => d.servoBandwidthHz *= v },
                // Timing: compute + bus delay the firmware is NOT told about.
                new Knob { name = "delay_extra", unit = "us", lo = 0f, nom = 0f, hi = 20000f, oneSided = true,
                           apply = (d, w, v) => d.computeLatencyUs += Mathf.RoundToInt(v) },
                // Pack: a part-used pack (lower voltage, earlier sag).
                new Knob { name = "start_soc", unit = "", lo = 1f, nom = 1f, hi = 0.3f, oneSided = true,
                           apply = (d, w, v) => { if (d.batteries.Count > 0) d.batteries[0].startSoc = v; } },
                // IMU and UWB.
                new Knob { name = "gyro_bias", unit = "deg/s", lo = -0.5f, nom = 0f, hi = 0.5f,
                           apply = (d, w, v) => d.gyroBias = v * Deg },
                new Knob { name = "uwb_dropout", unit = "", lo = 0f, nom = 0f, hi = 0.3f, oneSided = true,
                           apply = (d, w, v) => { foreach (var s in d.sensors) if (s.kind == SensorType.Uwb) s.uwbExtraDropout = v; } },
                new Knob { name = "uwb_outlier", unit = "", lo = 0f, nom = 0f, hi = 0.3f, oneSided = true,
                           apply = (d, w, v) => { foreach (var s in d.sensors) if (s.kind == SensorType.Uwb) s.uwbExtraOutlier = v; } },
                // The rows added at VAL-13's close-out (2026-10-12):
                // the IMU's mount (roll puts gravity into the lateral
                // accelerometer, pitch into the forward one, and the yaw rate
                // into the other gyros), the steering's dead time, the tyre's
                // cornering stiffness on its own, the floor the flow sensor
                // sees, the drive bus's frame loss.
                new Knob { name = "imu_roll", unit = "deg", lo = -2f, nom = 0f, hi = 2f,
                           apply = (d, w, v) => d.imuMisalignDeg = new Vector3(v, d.imuMisalignDeg.y, d.imuMisalignDeg.z) },
                new Knob { name = "imu_pitch", unit = "deg", lo = -2f, nom = 0f, hi = 2f,
                           apply = (d, w, v) => d.imuMisalignDeg = new Vector3(d.imuMisalignDeg.x, v, d.imuMisalignDeg.z) },
                new Knob { name = "steer_dead", unit = "ms", lo = 0f, nom = 0f, hi = 20f, oneSided = true,
                           apply = (d, w, v) => d.servoDeadTimeS += v * 0.001f },
                new Knob { name = "c_alpha", unit = "x", lo = 0.7f, nom = 1f, hi = 1.3f,
                           apply = (d, w, v) => { foreach (var ws in d.wheels) ws.corneringStiffMult = (ws.corneringStiffMult > 0f ? ws.corneringStiffMult : 1f) * v; } },
                new Knob { name = "flow_texture", unit = "x", lo = 1f, nom = 1f, hi = 0.5f, oneSided = true,
                           apply = (d, w, v) => { foreach (var s in d.sensors) if (s.kind == SensorType.Flow) s.flowTexture = (s.flowTexture > 0f ? s.flowTexture : 0.8f) * v; } },
                new Knob { name = "bus_loss", unit = "", lo = 0f, nom = 0f, hi = 0.05f, oneSided = true,
                           apply = (d, w, v) => d.busLossFrac = v },
            };
            return list;
        }

        /// <summary>Scale the steering linkage by <paramref name="gain"/> and shift
        /// it by <paramref name="offsetFrac"/> of full lock: every point of the
        /// map (an empty map is the identity, -1 .. +1).</summary>
        private static void MapSteer(VehicleDesign d, float gain, float offsetFrac)
        {
            var m = d.steerMap != null && d.steerMap.Length >= 2 ? (float[])d.steerMap.Clone() : new[] { -1f, 1f };
            for (int i = 0; i < m.Length; i++) m[i] = m[i] * gain + offsetFrac;
            d.steerMap = m;
        }

        private static float MaxSteerDeg(VehicleDesign d)
        {
            float a = 0f;
            foreach (var w in d.wheels) if (w.allowsSteering) a = Mathf.Max(a, Mathf.Abs(w.steerAngle));
            return a;
        }
    }
}
