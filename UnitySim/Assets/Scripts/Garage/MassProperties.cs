using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Composite mass properties of a <see cref="VehicleDesign"/>: total mass,
    /// centre of mass, and a diagonal inertia tensor built from the chassis box
    /// plus every part treated as a point mass at its mounting position (wheels
    /// with their motors, sensors, aero parts, battery packs). Shared by the
    /// factory (physics) and the stats panel (readout) so they can never drift.
    /// Products of inertia from asymmetric layouts are dropped — a documented
    /// approximation that is &lt;5 % for near-symmetric RC layouts.
    /// </summary>
    public struct MassResult
    {
        public float totalMass;      // kg
        public Vector3 com;          // chassis-local centre of mass
        public Vector3 inertiaDiag;  // Ixx, Iyy (yaw), Izz about the CoM (kg·m²)
        public float frontWeightPct; // static front-axle load share (0..100)
        public float yawInertia;     // = inertiaDiag.y
    }

    public static class MassProperties
    {
        // Legacy chassis CoM offset (battery/electronics ride low in the tray).
        private static readonly Vector3 ChassisCoM = new Vector3(0f, -0.03f, 0f);

        // Auto (default) part masses in kg, used when a spec's massKg is 0.
        public const float TofMass = 0.005f;
        public const float CameraMass = 0.015f;
        public const float EncoderMass = 0.008f;
        public const float SuspSensorMass = 0.006f;
        public const float WheelMass = 0.030f;          // unpowered wheel + hub
        public const float PoweredWheelMass = 0.190f;   // wheel + 540 motor + pinion
        public const float WingMass = 0.010f;           // × sizeScale²
        public const float SmallAeroMass = 0.008f;
        public const float AntennaMass = 0.008f;         // SMA + rubber whip
        public const float LightMass = 0.012f;           // light bar / pod cluster

        public static float SensorMass(SensorSpec s)
        {
            if (s.massKg > 0f) return s.massKg;
            return s.kind switch
            {
                Bridge.SensorType.Camera => CameraMass,
                Bridge.SensorType.Encoder => EncoderMass,
                Bridge.SensorType.Suspension => SuspSensorMass,
                _ => TofMass,
            };
        }

        public static float WheelMassOf(WheelSpec w) =>
            w.massKg > 0f ? w.massKg : (w.powered ? PoweredWheelMass : WheelMass);

        public static float AeroMass(AeroSpec a)
        {
            if (a.massKg > 0f) return a.massKg;
            float s = Mathf.Clamp(a.sizeScale <= 0f ? 1f : a.sizeScale, 0.6f, 1.6f);
            return a.kind == Vehicles.AeroKind.Wing ? WingMass * s * s : SmallAeroMass;
        }

        public static MassResult Compute(VehicleDesign d)
        {
            var r = new MassResult();

            // Gather point masses: chassis + every part at its mount point.
            var masses = new System.Collections.Generic.List<(float m, Vector3 p)>
            {
                // The design's own chassis CoM. The legacy default IS ChassisCoM,
                // so every existing design lands on the same vector — but a 30 mm
                // drop describes a 40 cm RC tray and means nothing on a 4.5 m car,
                // where CoM height is the number that sets load transfer and roll.
                (Mathf.Max(0.05f, d.mass), d.chassisCoM),
            };
            // Wheel mass sits at the hub (mount + strut drop), not the mount, so a
            // long strut lowers the composite CoM. Legacy length 0 => hub == mount.
            foreach (var w in d.wheels)
                masses.Add((WheelMassOf(w),
                    w.localPos + AIHWSim.Vehicles.SuspensionGeometry.HubOffsetLocal(
                        w.localPos.x, w.suspAngleDeg, w.suspLength)));
            foreach (var s in d.sensors) masses.Add((SensorMass(s), s.localPos));
            if (d.aero != null)
                foreach (var a in d.aero) masses.Add((AeroMass(a), a.localPos));
            if (d.batteries != null)
                foreach (var b in d.batteries) masses.Add((Mathf.Max(0.01f, b.massKg), b.localPos));
            if (d.antennas != null)
                foreach (var a in d.antennas)
                    masses.Add((a.massKg > 0f ? a.massKg : AntennaMass, a.localPos));
            if (d.lights != null)
                foreach (var l in d.lights)
                    masses.Add((l.massKg > 0f ? l.massKg : LightMass, l.localPos));

            float total = 0f;
            Vector3 com = Vector3.zero;
            foreach (var (m, p) in masses) { total += m; com += m * p; }
            com /= Mathf.Max(1e-4f, total);
            r.totalMass = total;
            r.com = com;

            // Chassis box inertia about its own centre; every mass then shifts to
            // the composite CoM via the parallel-axis theorem (diagonal terms).
            float mc = Mathf.Max(0.05f, d.mass);
            Vector3 s3 = d.bodySize;
            var inertia = new Vector3(
                mc / 12f * (s3.y * s3.y + s3.z * s3.z),
                mc / 12f * (s3.x * s3.x + s3.z * s3.z),
                mc / 12f * (s3.x * s3.x + s3.y * s3.y));
            foreach (var (m, p) in masses)
            {
                Vector3 dpos = p - com;
                inertia.x += m * (dpos.y * dpos.y + dpos.z * dpos.z);
                inertia.y += m * (dpos.x * dpos.x + dpos.z * dpos.z);
                inertia.z += m * (dpos.x * dpos.x + dpos.y * dpos.y);
            }
            // Floor to keep PhysX happy with tiny/degenerate designs.
            r.inertiaDiag = Vector3.Max(inertia, Vector3.one * 1e-5f);
            r.yawInertia = r.inertiaDiag.y;

            // Static front-axle load share from the CoM position between axles.
            float zF = float.MinValue, zR = float.MaxValue;
            foreach (var w in d.wheels)
            {
                if (w.localPos.z > zF) zF = w.localPos.z;
                if (w.localPos.z < zR) zR = w.localPos.z;
            }
            r.frontWeightPct = zF - zR > 1e-3f
                ? Mathf.Clamp01((com.z - zR) / (zF - zR)) * 100f
                : 50f;
            return r;
        }
    }
}
