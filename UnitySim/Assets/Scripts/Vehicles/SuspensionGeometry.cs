using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Single source of truth for the per-wheel strut geometry and its motion-ratio
    /// effect on the spring. CarVehicle, MassProperties, VehicleStats and the garage
    /// inspector all read these so the visible strut, the physics, and the readouts
    /// agree.
    ///
    /// The body-side mount is the anchor (fixed); a longer strut drops the wheel hub
    /// further below/outboard of it and, through a rocker-style motion ratio, lowers
    /// the effective wheel rate and increases wheel travel. The strut tilt is about
    /// the wheel-local Z axis, so the hub moves only in x/y — z (which Ackermann,
    /// wheelbase and anti-roll pairing key off) is invariant.
    ///
    /// Back-compat: <c>suspLength &lt;= 0</c> (old-JSON default) => zero hub offset,
    /// motion ratio 1, and today's spring/travel verbatim. At <see cref="NominalArm"/>
    /// the ratio is also 1, so presets can opt into visible struts with identical
    /// numbers by raising the mount by the same offset.
    /// </summary>
    public static class SuspensionGeometry
    {
        /// <summary>Reference strut length: motion ratio == 1 here (legacy rate/travel).</summary>
        public const float NominalArm = 0.03f;

        private const float MinLen = 0.015f;
        private const float MaxLen = 0.06f;

        /// <summary>Clamp a requested strut length into the usable band (0 stays 0).</summary>
        public static float ClampLength(float length) =>
            length <= 0f ? 0f : Mathf.Clamp(length, MinLen, MaxLen);

        /// <summary>Strut tilt (deg about wheel-local Z). Sign is side-relative — a
        /// positive <paramref name="angleDeg"/> leans both strut tops inboard — and
        /// the magnitude is clamped so the WheelCollider's raycast stays sane.</summary>
        public static float TiltZ(float localPosX, float angleDeg)
        {
            float sign = localPosX >= 0f ? -1f : 1f;
            return sign * Mathf.Clamp(angleDeg, -30f, 30f);
        }

        /// <summary>Offset from the body mount to the wheel hub (chassis-local).
        /// Zero when there is no strut; otherwise the clamped length pointed down,
        /// rotated by the strut tilt (so only x/y move; z stays put).</summary>
        public static Vector3 HubOffsetLocal(float localPosX, float angleDeg, float length)
        {
            float len = ClampLength(length);
            if (len <= 0f) return Vector3.zero;
            return Quaternion.Euler(0f, 0f, TiltZ(localPosX, angleDeg)) * (Vector3.down * len);
        }

        /// <summary>Rocker motion ratio (wheel travel : spring travel). Longer arm =>
        /// ratio &lt; 1 => softer wheel rate, more wheel travel. 0/legacy => 1.</summary>
        public static float MotionRatio(float length)
        {
            float len = ClampLength(length);
            return len <= 0f ? 1f : NominalArm / len;
        }

        /// <summary>Effective wheel rate (N/m) after the motion ratio, clamped for
        /// WheelCollider stability. Legacy length => the raw spring <paramref name="k"/>.</summary>
        public static float EffectiveRate(float k, float length)
        {
            if (length <= 0f) return k;
            float mr = MotionRatio(length);
            return Mathf.Clamp(k * mr * mr, 50f, 4000f);
        }

        /// <summary>Effective wheel travel (m) after the motion ratio, clamped.
        /// Legacy length => the raw <paramref name="travel"/>.</summary>
        public static float EffectiveTravel(float travel, float length)
        {
            if (length <= 0f) return travel;
            float len = ClampLength(length);
            return Mathf.Clamp(travel * (len / NominalArm), 0.005f, 0.12f);
        }
    }
}
