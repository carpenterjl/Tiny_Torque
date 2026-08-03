using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One placeable wheel on a <see cref="CarVehicle"/>. Position + heading (yaw),
    /// plus per-wheel suspension (stiffness / damping ratio / travel / strut tilt)
    /// and a friction scalar. A wheel steers if <see cref="allowsSteering"/>
    /// (optionally inverted), and is driven only if a motor is attached to it
    /// (handled separately via a MotorPart bound to this wheel's index). The strut
    /// tilt inclines the WheelCollider mount, so suspension travel (still along the
    /// mount's up axis) is angled and the wheel carries a camber-like lean.
    /// </summary>
    [System.Serializable]
    public struct CarWheelConfig
    {
        public Vector3 localPos;        // relative to the chassis centre
        public float yaw;               // heading of the wheel (deg about up)
        public float radius;
        public bool allowsSteering;
        public bool reverseSteering;    // invert the steer command for this wheel
        public float steerAngle;        // this wheel's max steer angle (deg)
        public bool powered;            // has an on-board motor (drives the visual can)

        // Suspension
        public float suspStiffness;     // spring rate (N/m)
        public float suspDampingRatio;  // damping ratio ζ; <=0 = use CarVehicle.suspensionDamper
        public float suspTravel;        // suspension distance (m)
        public float suspAngleDeg;      // strut tilt about wheel-local Z (deg)
        public float suspLength;        // visible strut length (m); 0 = rigid mount / no strut
        public float gripMult;          // friction stiffness scalar (fwd+side)

        // Reflected drivetrain spin inertia (rotorInertia·gear², kg·m²) added to
        // this wheel's spin inertia at build time. 0 = none (legacy).
        public float extraSpinInertia;

        // Tire realism (0 = off = legacy behaviour on old JSON)
        public float loadSensitivity;   // grip ∝ (Fz/Fz0)^-s; typical 0.15
        public float balloonPct;        // max radius growth % at high wheel speed

        // Cosmetic wheel/tyre mesh style (0 slick / 1 knobby / 2 rally), and the
        // WheelCatalog key that outranks it. Both copied unresolved off the
        // design; see CarVehicle.bodyKey for why the pair travels together.
        public int wheelStyle;
        public string wheelKey;

        /// <summary>Mesh, author radius and finish for this corner. Never null.</summary>
        public WheelDef Wheel => WheelCatalog.Resolve(wheelKey, wheelStyle);

        // ---- authored scale-dependent constants (see WheelSpec for the why) ----
        // Runtime mirrors of the design fields. A 0 sentinel means "use the
        // legacy expression verbatim", which is what makes every existing
        // design bit-identical rather than approximately unchanged.
        public float unsprungMassKg;    // 0 = the legacy 0.05 kg
        public float spinInertiaKgM2;   // 0 = the legacy 0.5*0.05*r*r
        public float brushEps;          // 0 = the legacy 0.01 curve floor
        public float suspTargetPos;     // 0 = the legacy 0.5 (mid-travel)
        public float brakeScale;        // per-wheel brake bias; 1 = unchanged

        /// <summary>Linkage hardpoints. All-zero (the default) means no linkage is
        /// described: roll centre stays at ground level and the toe link is inert,
        /// which is exactly what every design did before this existed.</summary>
        public SuspensionLinkage linkage;

        /// <summary>Tyre rated load (N) — sets where cornering stiffness peaks.
        /// 0 = the legacy load-independent AlphaPeak.</summary>
        public float ratedLoadN;

        public static CarWheelConfig Default(Vector3 pos, bool steers)
        {
            return new CarWheelConfig
            {
                localPos = pos,
                yaw = 0f,
                radius = 0.033f,
                allowsSteering = steers,
                reverseSteering = false,
                steerAngle = 28f,
                powered = false,
                suspStiffness = 300f,
                suspDampingRatio = 0f,
                suspTravel = 0.03f,
                suspAngleDeg = 0f,
                gripMult = 1f,
            };
        }
    }
}
