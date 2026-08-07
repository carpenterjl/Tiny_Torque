using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The arithmetic behind a translate/rotate gizmo, with no Unity object in
    /// sight so every bit of it can be benched.
    ///
    /// <b>The near-parallel case is REFUSED, not clamped.</b> When the pointer ray
    /// lies nearly along the axis being dragged, the closest-approach parameter is
    /// the ratio of two vanishing numbers and swings from −∞ to +∞ across a couple
    /// of pixels. Every gizmo that misbehaves does it here, and every one of them
    /// clamps. Refusing instead means the part simply does not move while you are
    /// looking down its own arrow, which is the honest answer to a question with
    /// no stable one — and it is a state the user can always leave by orbiting.
    ///
    /// <b>Snapping quantises the RESULT, in the frame the drag is happening in.</b>
    /// Snapping the delta instead would put a part on the grid only if it started
    /// there; this way a part dragged with snapping on ends up on the grid whatever
    /// it was doing before.
    /// </summary>
    public static class GizmoMath
    {
        /// <summary>Below this |sin| between the ray and the axis, an axis drag is
        /// refused. 0.035 is about two degrees.</summary>
        public const float MinAxisSin = 0.035f;

        /// <summary>Below this |cos| between the ray and a plane's normal, a plane
        /// drag is refused — the same failure one dimension up, where the ray runs
        /// along the plane instead of across it.</summary>
        public const float MinPlaneCos = 0.05f;

        /// <summary>
        /// Distance along <paramref name="axis"/> from <paramref name="origin"/> to
        /// the point on that line closest to <paramref name="ray"/>.
        ///
        /// False when the two are within <see cref="MinAxisSin"/> of parallel, or
        /// when the axis is degenerate. <paramref name="t"/> is then untouched.
        /// </summary>
        public static bool ClosestOnAxis(Ray ray, Vector3 origin, Vector3 axis, out float t)
        {
            t = 0f;
            Vector3 u = axis.normalized;
            Vector3 v = ray.direction.normalized;
            if (u.sqrMagnitude < 0.5f || v.sqrMagnitude < 0.5f) return false;

            float uv = Vector3.Dot(u, v);
            // 1 - (u·v)² is sin² of the angle between them, which is exactly the
            // determinant of the 2×2 system below — so the guard and the divisor
            // are the same quantity rather than two approximations of it.
            float denom = 1f - uv * uv;
            if (denom < MinAxisSin * MinAxisSin) return false;

            // The closest-approach parameter along u, from the standard two-line
            // system with a = c = 1 (both directions unit): t = (b·e − d) / (1 − b²).
            Vector3 w = origin - ray.origin;
            t = (uv * Vector3.Dot(w, v) - Vector3.Dot(w, u)) / denom;
            return true;
        }

        /// <summary>
        /// Where <paramref name="ray"/> crosses the plane through
        /// <paramref name="point"/> with normal <paramref name="normal"/>.
        ///
        /// False when the ray is nearly in the plane, or points away from it — a
        /// drag that would place the part behind the camera is not a drag.
        /// </summary>
        public static bool RayPlane(Ray ray, Vector3 point, Vector3 normal, out Vector3 hit)
        {
            hit = Vector3.zero;
            Vector3 n = normal.normalized;
            Vector3 d = ray.direction.normalized;
            float denom = Vector3.Dot(n, d);
            if (Mathf.Abs(denom) < MinPlaneCos) return false;

            float t = Vector3.Dot(point - ray.origin, n) / denom;
            if (t <= 0f) return false;
            hit = ray.origin + d * t;
            return true;
        }

        /// <summary>
        /// The angle swept about <paramref name="axis"/> between two points on the
        /// plane through <paramref name="pivot"/>, in degrees, signed by the
        /// right-hand rule about the axis.
        ///
        /// Both arms are projected into the plane first, so a hit point picked up
        /// off a thick disc handle still reports the angle its position implies.
        /// Returns 0 when either arm is degenerate — a grab at the exact centre
        /// has no angle to report.
        /// </summary>
        public static float SweepDegrees(Vector3 axis, Vector3 pivot, Vector3 from, Vector3 to)
        {
            Vector3 n = axis.normalized;
            Vector3 a = Vector3.ProjectOnPlane(from - pivot, n);
            Vector3 b = Vector3.ProjectOnPlane(to - pivot, n);
            if (a.sqrMagnitude < 1e-10f || b.sqrMagnitude < 1e-10f) return 0f;
            return Vector3.SignedAngle(a, b, n);
        }

        /// <summary>Round to a grid. A step of 0 or less is "no snapping" and
        /// returns the value untouched — so a caller can pass its step
        /// unconditionally and let the toggle live in one place.</summary>
        public static float Snap(float value, float step) =>
            step > 0f ? Mathf.Round(value / step) * step : value;

        public static Vector3 Snap(Vector3 v, float step) =>
            step > 0f ? new Vector3(Snap(v.x, step), Snap(v.y, step), Snap(v.z, step)) : v;

        /// <summary>
        /// Snap an angle in degrees, keeping it in (−180, 180].
        ///
        /// Wrapped AFTER snapping, not before: snapping 359.9° with a 15° step
        /// gives 360°, and a wrap-first order would leave that as −0.1° short of a
        /// grid position it is in fact exactly on.
        /// </summary>
        public static float SnapAngle(float deg, float step)
        {
            float s = Snap(deg, step);
            s %= 360f;
            if (s > 180f) s -= 360f;
            if (s <= -180f) s += 360f;
            return s;
        }

        public static Vector3 SnapAngles(Vector3 euler, float step) =>
            step > 0f
                ? new Vector3(SnapAngle(euler.x, step), SnapAngle(euler.y, step),
                              SnapAngle(euler.z, step))
                : euler;

        /// <summary>
        /// The world size a gizmo should be drawn at to occupy a constant slice of
        /// the screen — <paramref name="fraction"/> of the viewport height at the
        /// distance the handle actually sits.
        ///
        /// Handles both projections: a perspective camera's scale is proportional
        /// to distance, an orthographic one's is not a function of distance at all.
        /// Clamped below so a gizmo on a part the camera is inside of still exists.
        /// </summary>
        public static float ScreenConstantSize(Camera cam, Vector3 worldPoint, float fraction)
        {
            if (cam == null) return fraction;
            if (cam.orthographic) return Mathf.Max(1e-4f, cam.orthographicSize * 2f * fraction);

            float dist = Vector3.Dot(worldPoint - cam.transform.position, cam.transform.forward);
            dist = Mathf.Max(dist, cam.nearClipPlane);
            float heightAtDist = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return Mathf.Max(1e-4f, heightAtDist * fraction);
        }
    }
}
