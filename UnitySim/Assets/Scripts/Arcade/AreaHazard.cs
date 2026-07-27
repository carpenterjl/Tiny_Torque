using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// A dropped area of denial — a smoke cloud or an oil slick. The gameplay
    /// identity only; the look belongs to <see cref="AreaHazardViz"/>, which
    /// shares this GameObject so a drifting cloud carries its effect with it
    /// instead of leaving it behind on the tarmac.
    ///
    /// Unlike <see cref="Banana"/> this carries NO COLLIDER, and that is the
    /// central design decision rather than an omission. Containment is a distance
    /// poll in <c>ArcadeDirector.UpdateAreaHazards</c>, for three reasons:
    ///
    ///   * a car is one transform with a root BoxCollider AND four
    ///     WheelColliders, so a trigger fires several times per car per pass —
    ///     the box dodges that by disabling on first touch and the banana by
    ///     dying, and a PERSISTENT area can do neither;
    ///   * re-arming the effect while you sit inside needs OnTriggerStay, which
    ///     stops firing the moment a stationary car's Rigidbody sleeps;
    ///   * LAN host followers are kinematic and stream-posed, and kinematic
    ///     against kinematic is exactly where trigger callbacks get murky.
    ///
    /// It also means a cloud can never become a wall — there is nothing there to
    /// collide with — which for an item whose whole point is that you drive
    /// through it is worth more than the poll costs (8 racers × 16 hazards of
    /// sqrMagnitude per frame).
    /// </summary>
    public sealed class AreaHazard : MonoBehaviour
    {
        public int objId;
        public int ownerSlot = -1;
        public CarVehicle ownerCar;
        public ItemKind kind = ItemKind.SmokeCloud;
        public float droppedAt;
        public float expiresAt;

        /// <summary>Fully-grown radius, and the ramp that gets there. Copied from
        /// the viz at spawn rather than re-derived, so the area you are caught by
        /// is by construction the area you can see.</summary>
        public float fullRadius = ArcadeConfig.SmokeRadius;
        public float startRadius = ArcadeConfig.SmokeStartRadius;
        public float growSeconds = ArcadeConfig.SmokeGrowSeconds;

        /// <summary>Radius right now. It ramps in because an unexpanded puff that
        /// caught people at full radius would blind from two car-lengths away,
        /// which looks like a bug even when it is a rule.</summary>
        public float Radius
        {
            get
            {
                if (growSeconds <= 0f) return fullRadius;
                float t = Mathf.Clamp01((ArcadeDirector.Clock - droppedAt) / growSeconds);
                return Mathf.Lerp(startRadius, fullRadius, t);
            }
        }

        /// <summary>
        /// Is this world point inside? Flat distance plus a vertical band, not a
        /// sphere: the hazard sits low on the surface while a car's origin is
        /// above it, and a plain 3D test would make the effective radius depend
        /// on ride height.
        /// </summary>
        public bool Contains(Vector3 p, float radius)
        {
            Vector3 d = p - transform.position;
            if (Mathf.Abs(d.y) > ArcadeConfig.HazardVerticalBand) return false;
            d.y = 0f;
            return d.sqrMagnitude <= radius * radius;
        }
    }
}
