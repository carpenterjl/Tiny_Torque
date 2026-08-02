using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>Placeable aerodynamic part kinds (garage AERO palette).</summary>
    public enum AeroKind { Wing = 0, Splitter = 1, SideDam = 2, Canard = 3 }

    /// <summary>
    /// Runtime config for one aero part on a built car (flattened from the
    /// design's AeroSpec by the VehicleFactory, the same way wheels become
    /// CarWheelConfigs). Forces are applied at <see cref="localPos"/> so a rear
    /// wing plants the rear axle specifically.
    /// </summary>
    public struct AeroConfig
    {
        public AeroKind kind;
        public Vector3 localPos;
        public float angleDeg;    // attack angle (Wing/Canard)
        public float yawDeg;      // mount heading — rotates the working direction
        public float sizeScale;   // scales forces by sizeScale²
    }

    /// <summary>
    /// Quadratic aerodynamics for the 1/10 RC scale: body drag/lift from a
    /// per-shape Cd table and frontal area, plus per-part downforce/drag
    /// coefficients. Everything is expressed as force = q·(C·A) with
    /// q = ½·ρ·v², so coefficient tables carry effective areas (m²).
    ///
    /// Magnitude sanity at 10 m/s (q ≈ 61 Pa): stock LowRacer body drag
    /// ≈ 0.61 N (≈ 3.5% of weight), a 15° wing ≈ 0.73 N downforce — small at
    /// cruise, decisive at top speed and in fast corners, like the real thing.
    /// </summary>
    public static class AeroDynamics
    {
        public const float AirDensity = 1.225f;   // kg/m³

        /// <summary>Effective frontal area of the body box (m²).</summary>
        public static float FrontalArea(Vector3 bodySize) => bodySize.x * bodySize.y * 0.9f;

        /// <summary>
        /// Drag coefficient per body silhouette. The numbers, and the reasoning
        /// behind each one, live in <see cref="BodyCatalog"/> — a body's drag is
        /// a property of the shell, and keeping it in a second switch keyed on
        /// the same shape was how the two could disagree.
        ///
        /// The 0.80 for a missing row is the old <c>default:</c> arm verbatim:
        /// an unknown silhouette has always been priced as a buggy.
        /// </summary>
        public static float BodyCd(BodyDef def) => def == null ? 0.80f : def.cd;

        /// <summary>Built-in body downforce area ClA (m²) — 0 for bluff shapes,
        /// and 0 for a body with no row, which is the old default arm.</summary>
        public static float BodyClA(BodyDef def) => def == null ? 0f : def.clA;

        /// <summary>
        /// Effective areas for one part at sizeScale 1: <paramref name="clA"/> =
        /// downforce area, <paramref name="cdA"/> = drag area. Wing/Canard lift
        /// grows linearly with attack angle up to a soft stall clamp; their drag
        /// carries a small induced term on top of the profile drag.
        /// </summary>
        public static void PartCoefficients(AeroKind kind, float angleDeg, out float clA, out float cdA)
        {
            switch (kind)
            {
                case AeroKind.Wing:
                {
                    float a = Mathf.Clamp(angleDeg, 0f, 15f);        // soft stall at 15°
                    clA = 0.0008f * a;                                // ≤ 0.012 m²
                    cdA = 0.0003f + 0.00002f * a;                     // profile + induced
                    return;
                }
                case AeroKind.Splitter: clA = 0.004f; cdA = 0.0004f; return;
                case AeroKind.SideDam: clA = 0.0015f; cdA = 0.0002f; return;
                case AeroKind.Canard:
                {
                    float a = Mathf.Clamp(angleDeg, 0f, 15f);
                    clA = 0.0002f * a;                                // ≤ 0.003 m²
                    cdA = 0.0002f + 0.00001f * a;
                    return;
                }
                default: clA = 0f; cdA = 0f; return;
            }
        }

        // ---- Directional per-part model (angle-of-attack aware) -------------
        //
        // Wings and canards are modelled as thin flat plates: the aerodynamic
        // force acts along the plate normal, proportional to the flow component
        // through the plate — so effective angle of attack emerges from the part
        // pitch, its mount yaw, AND the airflow direction in body space. A wing
        // mounted backwards (yaw 180°) or a nose-up body over a jump produces
        // LIFT instead of downforce; slides load side dams asymmetrically.
        // Plate constants are calibrated so a yaw-0 part in level straight-line
        // flow reproduces PartCoefficients' small-angle forces exactly.

        private const float WingPlate = 0.0008f * Mathf.Rad2Deg;   // ≈ 0.0458 m² (2π-slope · plate area)
        private const float CanardPlate = 0.0002f * Mathf.Rad2Deg; // ≈ 0.0115 m²
        private const float StallStartDeg = 15f;   // attached-flow limit
        private const float StallEndDeg = 24f;     // fully stalled by here
        private const float StallResidual = 0.45f; // post-stall plate fraction

        /// <summary>
        /// Body-space aero forces for one part. <paramref name="airflowLocal"/> is
        /// the air velocity in body space (= −vehicle velocity, body frame).
        /// <paramref name="liftLocal"/> is the plate/downforce component (scaled by
        /// aeroMult by the caller); <paramref name="dragLocal"/> is profile drag.
        /// </summary>
        public static void PartForces(AeroKind kind, float angleDeg, float yawDeg,
            float sizeScale, Vector3 airflowLocal,
            out Vector3 liftLocal, out Vector3 dragLocal)
        {
            liftLocal = Vector3.zero;
            dragLocal = Vector3.zero;
            float sp2 = airflowLocal.sqrMagnitude;
            if (sp2 < 0.01f) return;

            float q = 0.5f * AirDensity * sp2;
            float s2 = sizeScale * sizeScale;
            Vector3 flowDir = airflowLocal / Mathf.Sqrt(sp2);
            Quaternion yawRot = Quaternion.Euler(0f, yawDeg, 0f);

            switch (kind)
            {
                case AeroKind.Wing:
                case AeroKind.Canard:
                {
                    // Plate normal: +Y pitched by +angleDeg about local X (nose-
                    // down chord = downforce in head-on flow), then the mount yaw.
                    // Sanity: flow (0,0,−1), n (0,cos a,sin a) → force (0,−sin a·cos a,−sin²a)
                    // = downforce + induced drag; reversed flow flips it to lift.
                    Vector3 n = yawRot * (Quaternion.Euler(angleDeg, 0f, 0f) * Vector3.up);
                    float k = (kind == AeroKind.Wing ? WingPlate : CanardPlate) * s2;

                    float through = Vector3.Dot(flowDir, n);   // −sin(effective AoA)
                    float aoaDeg = Mathf.Asin(Mathf.Clamp(through, -1f, 1f)) * Mathf.Rad2Deg;
                    float stall = Mathf.Lerp(1f, StallResidual,
                        Mathf.InverseLerp(StallStartDeg, StallEndDeg, Mathf.Abs(aoaDeg)));

                    // Momentum-transfer flat plate: F = k·q·(flŵ·n)·n — the force
                    // rides the plate normal and flips sign with the flow side.
                    liftLocal = n * (k * q * through * stall);

                    float cd0 = kind == AeroKind.Wing ? 0.0003f : 0.0002f;
                    dragLocal = -flowDir * (q * cd0 * s2);
                    return;
                }
                case AeroKind.Splitter:
                case AeroKind.SideDam:
                {
                    // Ground-effect devices: fixed downforce area, effective only
                    // while air approaches their leading edge (part-local −Z flow).
                    Vector3 partFwd = yawRot * Vector3.forward;
                    float eff = Mathf.Clamp01(Vector3.Dot(flowDir, -partFwd));
                    PartCoefficients(kind, 0f, out float clA, out float cdA);
                    liftLocal = Vector3.down * (q * clA * s2 * eff);
                    dragLocal = -flowDir * (q * cdA * s2 * eff);
                    return;
                }
            }
        }

        /// <summary>Total drag area (m²) of a body + parts set (stats/top-speed solver).</summary>
        public static float TotalCdA(BodyDef def, Vector3 bodySize,
            System.Collections.Generic.IReadOnlyList<AeroConfig> parts)
        {
            float cdA = BodyCd(def) * FrontalArea(bodySize);
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                {
                    PartCoefficients(parts[i].kind, parts[i].angleDeg, out _, out float p);
                    cdA += p * parts[i].sizeScale * parts[i].sizeScale;
                }
            return cdA;
        }

        /// <summary>Total downforce area (m²) of a body + parts set.</summary>
        public static float TotalClA(BodyDef def,
            System.Collections.Generic.IReadOnlyList<AeroConfig> parts)
        {
            float clA = BodyClA(def);
            if (parts != null)
                for (int i = 0; i < parts.Count; i++)
                {
                    PartCoefficients(parts[i].kind, parts[i].angleDeg, out float p, out _);
                    clA += p * parts[i].sizeScale * parts[i].sizeScale;
                }
            return clA;
        }
    }
}
