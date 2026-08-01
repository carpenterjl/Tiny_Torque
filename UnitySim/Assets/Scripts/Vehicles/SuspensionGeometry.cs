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

        // ---- linkage kinematics -------------------------------------------
        // Everything below derives from SuspensionLinkage hardpoints. All return
        // 0 for an unauthored linkage, which is the sentinel that keeps every
        // pre-existing design on the code path it already had.

        /// <summary>
        /// Roll centre height above the GROUND (m) for this corner, by the standard
        /// front-view construction: find the suspension's instant centre, draw a
        /// line from the tyre contact patch through it, and read off where that line
        /// crosses the vehicle centreline.
        ///
        /// The instant centre is where the two links' lines meet. For a double
        /// wishbone that is the two arms. For a MacPherson strut there is no upper
        /// arm — the strut resists bending, so its constraint is the line through
        /// the top mount PERPENDICULAR to the strut axis, which is what makes struts
        /// behave like a virtual upper arm of enormous length.
        ///
        /// <b>Why this matters at all:</b> lateral tyre force reaches the sprung mass
        /// through the linkage, which reacts at this height — not at ground level.
        /// Applying it at the contact patch (which is what this engine did) uses the
        /// full CoM height as the roll moment arm and overstates body roll.
        ///
        /// <b>Declared omission: no roll centre migration.</b> This is evaluated once,
        /// at the authored (static) hardpoints. Real roll centres move as the
        /// suspension travels, and MacPherson struts are notorious for moving a lot.
        /// The static value is the standard first-order treatment; it is recorded as
        /// an approximation rather than hidden.
        ///
        /// Returns 0 (= ground level = the old behaviour) if the geometry is missing
        /// or degenerate, e.g. parallel links, whose instant centre is at infinity.
        /// </summary>
        /// <param name="k">Authored hardpoints, chassis-local.</param>
        /// <param name="wheelCentreLocal">Wheel centre, chassis-local.</param>
        /// <param name="loadedRadius">Loaded rolling radius (m) — sets ground level.</param>
        public static float RollCentreHeight(SuspensionLinkage k,
                                             Vector3 wheelCentreLocal,
                                             float loadedRadius)
        {
            if (!k.HasRollCentre || loadedRadius <= 0f) return 0f;

            // Front view: x lateral, y up, measured from the ground plane.
            float groundY = wheelCentreLocal.y - loadedRadius;
            Vector2 lowIn = new Vector2(k.lowerInner.x, k.lowerInner.y - groundY);
            Vector2 lowOut = new Vector2(k.lowerOuter.x, k.lowerOuter.y - groundY);

            Vector2 upPoint, upDir;
            if (k.upperInner != Vector3.zero && k.upperOuter != Vector3.zero)
            {
                upPoint = new Vector2(k.upperInner.x, k.upperInner.y - groundY);
                upDir = new Vector2(k.upperOuter.x, k.upperOuter.y - groundY) - upPoint;
            }
            else
            {
                // Strut: constraint line ⟂ to the strut axis (top mount → ball joint).
                upPoint = new Vector2(k.strutTop.x, k.strutTop.y - groundY);
                Vector2 axis = lowOut - upPoint;
                if (axis.sqrMagnitude < 1e-10f) return 0f;
                upDir = new Vector2(-axis.y, axis.x);   // perpendicular
            }

            if (!LineIntersect(lowIn, lowOut - lowIn, upPoint, upDir, out Vector2 ic))
                return 0f;   // parallel links — instant centre at infinity

            // Contact patch sits under the wheel centre, on the ground (y = 0 here).
            float cx = wheelCentreLocal.x;
            float dx = ic.x - cx;
            if (Mathf.Abs(dx) < 1e-6f) return 0f;   // patch→IC never reaches x = 0

            // Where the patch→IC line crosses the centreline. Symmetric in side:
            // mirroring flips cx and ic.x together and the result is unchanged.
            return -cx * ic.y / dx;
        }

        /// <summary>
        /// Roll steer, as wheel yaw in RADIANS per metre of upward hub travel,
        /// derived from the toe link. Positive is a right-handed rotation about +y;
        /// callers converting to a Unity steer angle must negate (Unity's y-Euler is
        /// left-handed).
        ///
        /// <b>Roll steer is not a coefficient here — it is a consequence.</b> The toe
        /// link is a rigid rod, so as the hub rises its outer end must swing on an arc
        /// about the inner pivot. If the link is not exactly level, that arc has a
        /// horizontal component, and a horizontal nudge to a point offset from the
        /// wheel centre yaws the knuckle. In roll one wheel bumps while the other
        /// droops, so the two toe changes add into an axle steer — which is the whole
        /// mechanism, falling out of where the rod is bolted rather than being fitted.
        ///
        /// First-order in travel, and the knuckle is taken to yaw about a vertical
        /// axis through the wheel centre (exact enough for a rear link; a steered
        /// corner's true axis is the kingpin, which the strut hardpoints could give
        /// but which is not read yet).
        /// </summary>
        public static float ToeSteerPerMetre(SuspensionLinkage k, Vector3 wheelCentreLocal)
        {
            if (!k.HasToeLink) return 0f;

            Vector3 d = k.toeOuter - k.toeInner;
            float duSq = d.x * d.x + d.z * d.z;         // horizontal link length²
            if (duSq < 1e-10f) return 0f;               // vertical link: no toe effect

            // Lever from the yaw axis to the link's outer joint, in plan view.
            float rx = k.toeOuter.x - wheelCentreLocal.x;
            float rz = k.toeOuter.z - wheelCentreLocal.z;
            float rSq = rx * rx + rz * rz;
            if (rSq < 1e-8f) return 0f;                 // joint on the axis: no lever

            // Bump of 1 m moves the outer joint horizontally by -d.y/du along the
            // link's own horizontal direction (length constraint, first order).
            // Yaw of that displacement about the vertical axis: (r × δ)·ŷ / |r|².
            return -d.y * (rz * d.x - rx * d.z) / (duSq * rSq);
        }

        /// <summary>2D line intersection; false when the lines are parallel.</summary>
        private static bool LineIntersect(Vector2 p1, Vector2 d1, Vector2 p2, Vector2 d2,
                                          out Vector2 hit)
        {
            hit = default;
            float den = d1.x * d2.y - d1.y * d2.x;
            if (Mathf.Abs(den) < 1e-9f) return false;
            Vector2 w = p2 - p1;
            float t = (w.x * d2.y - w.y * d2.x) / den;
            hit = p1 + t * d1;
            return true;
        }
    }
}
