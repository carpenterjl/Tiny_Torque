using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>Engineer's-panel readout computed from a <see cref="VehicleDesign"/>.</summary>
    public struct StatsResult
    {
        public float totalMass;
        public int wheels;
        public int powered;
        public int steered;
        public float totalStallTorqueNm;   // summed geared stall torque at the wheels
        public float estTopSpeedMs;        // drag-limited (motor thrust = aero drag)
        public float dragAtTopN;           // aero drag at the estimated top speed
        public float downforceAtTopN;      // aero downforce at the estimated top speed
        public float rideFreqHz;           // suspension natural frequency (sprung)
        public float sagPct;               // static compression as % of travel
        public float aeroFrontPct;         // share of straight-line downforce ahead of the CoM
        public bool hasAeroParts;          // any placed aero part (balance readout shown)
        public bool composite;             // composite mass model active
        public Vector3 com;                // chassis-local CoM (composite only)
        public float frontWeightPct;       // static front-axle load share (composite only)
        public float yawInertia;           // kg·m² about vertical (composite only)
    }

    /// <summary>
    /// Derives a live stats summary for the garage. Top speed is the equilibrium
    /// where summed motor thrust (per-wheel DC model at full voltage, falling
    /// with back-EMF) equals aero drag (body Cd·A + parts) — solved by bisection
    /// between 0 and the no-load ground speed. Stall torque is the geared
    /// (ESC-limited) electrical stall.
    /// </summary>
    public static class VehicleStats
    {
        // WheelCollider.mass in CarVehicle.MakeWheel.
        private const float WheelColliderMass = 0.05f;

        public static StatsResult Compute(VehicleDesign d)
        {
            var r = new StatsResult { wheels = d.wheels.Count };
            if (d.useCompositeMass)
            {
                var mp = MassProperties.Compute(d);
                r.composite = true;
                r.totalMass = mp.totalMass;
                r.com = mp.com;
                r.frontWeightPct = mp.frontWeightPct;
                r.yawInertia = mp.yawInertia;
            }
            else
            {
                r.totalMass = d.mass + WheelColliderMass * d.wheels.Count;
            }

            // Suspension: ride frequency and static sag from the sprung corner mass.
            if (d.wheels.Count > 0)
            {
                float kSum = 0f, travelSum = 0f;
                foreach (var w in d.wheels)
                {
                    // Feed the strut motion-ratio effective values so the readout
                    // tracks strut length (legacy length 0 => the raw numbers).
                    float baseK = w.suspStiffness > 0f ? w.suspStiffness : 300f;
                    float baseTravel = w.suspTravel > 0f ? w.suspTravel : 0.03f;
                    kSum += SuspensionGeometry.EffectiveRate(baseK, w.suspLength);
                    travelSum += SuspensionGeometry.EffectiveTravel(baseTravel, w.suspLength);
                }
                float kAvg = kSum / d.wheels.Count;
                float travelAvg = travelSum / d.wheels.Count;
                float mCorner = r.totalMass / d.wheels.Count;
                r.rideFreqHz = Mathf.Sqrt(kAvg / Mathf.Max(1e-4f, mCorner)) / (2f * Mathf.PI);
                r.sagPct = mCorner * 9.81f / (kAvg * Mathf.Max(1e-4f, travelAvg)) * 100f;
            }

            float noLoadTop = float.MaxValue;
            foreach (var w in d.wheels)
            {
                if (w.allowsSteering) r.steered++;
                if (!w.powered) continue;
                r.powered++;

                var m = w.motor;
                float kt = Mathf.Max(1e-4f, m.kt);
                float res = Mathf.Max(1e-3f, m.resistance);
                float gear = Mathf.Max(1e-3f, m.gearRatio);
                float eff = Mathf.Clamp01(m.efficiency <= 0f ? 1f : m.efficiency);

                float stallA = m.maxVoltage / res;
                if (m.maxCurrent > 0f) stallA = Mathf.Min(stallA, m.maxCurrent); // ESC limit
                r.totalStallTorqueNm += kt * stallA * gear * eff;

                // No-load motor speed (rad/s) → wheel speed → ground speed.
                float wheelOmega = (m.maxVoltage / kt) / gear;
                float top = wheelOmega * Mathf.Max(0.01f, w.radius);
                if (top < noLoadTop) noLoadTop = top;
            }

            if (r.powered == 0) { r.estTopSpeedMs = 0f; return r; }

            // Aero drag/downforce areas including parts.
            var parts = new System.Collections.Generic.List<AeroConfig>();
            if (d.aero != null)
                foreach (var a in d.aero)
                    parts.Add(new AeroConfig
                    {
                        kind = a.kind,
                        localPos = a.localPos,
                        angleDeg = a.angleDeg,
                        yawDeg = a.yawDeg,
                        sizeScale = Mathf.Clamp(a.sizeScale <= 0f ? 1f : a.sizeScale, 0.6f, 1.6f),
                    });
            float cdA = AeroDynamics.TotalCdA(d.Body, d.bodySize, parts);
            float clA = AeroDynamics.TotalClA(d.Body, parts);

            // Aero balance: straight-line downforce share ahead of the CoM (the
            // body's built-in downforce acts at the CoM → splits half/half).
            r.hasAeroParts = parts.Count > 0;
            if (r.hasAeroParts)
            {
                float comZ = r.composite ? r.com.z : 0f;
                float bodyClA = AeroDynamics.BodyClA(d.Body);
                float front = bodyClA * 0.5f, total = bodyClA;
                foreach (var p in parts)
                {
                    AeroDynamics.PartCoefficients(p.kind, p.angleDeg, out float pClA, out _);
                    float df = pClA * p.sizeScale * p.sizeScale;
                    total += df;
                    if (p.localPos.z > comZ) front += df;
                }
                r.aeroFrontPct = total > 1e-6f ? front / total * 100f : 50f;
            }

            // Bisect thrust(v) − drag(v) = 0 on (0, noLoadTop].
            float Thrust(float v)
            {
                float sum = 0f;
                foreach (var w in d.wheels)
                {
                    if (!w.powered) continue;
                    float rad = Mathf.Max(0.01f, w.radius);
                    float tq = MotorModel.WheelTorque(w.motor, w.motor.maxVoltage, v / rad,
                        out _, out _);
                    sum += tq / rad;
                }
                return sum;
            }
            float Drag(float v) => 0.5f * AeroDynamics.AirDensity * v * v * cdA;

            float lo = 0f, hi = noLoadTop;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Thrust(mid) > Drag(mid)) lo = mid; else hi = mid;
            }
            r.estTopSpeedMs = (lo + hi) * 0.5f;
            float qTop = 0.5f * AeroDynamics.AirDensity * r.estTopSpeedMs * r.estTopSpeedMs;
            r.dragAtTopN = qTop * cdA;
            r.downforceAtTopN = qTop * clA;
            return r;
        }
    }
}
