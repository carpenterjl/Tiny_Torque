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

        // Cosmetic wheel/tyre mesh style (0 slick / 1 knobby / 2 rally).
        public int wheelStyle;

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
