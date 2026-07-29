using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Turns raw collisions into the three kinds of hit a derby cares about:
    /// a square ram, a side-swipe, and a wall.
    ///
    /// Sits beside <c>VehicleAudio</c> on the car's own GameObject and needs no
    /// cooperation from <see cref="CarVehicle"/> at all, because Unity delivers
    /// collision callbacks to every component on the Rigidbody's object — the
    /// same trick VehicleAudio's impact thud already uses, and the reason this
    /// whole feature costs the physics nothing.
    ///
    /// Classification, not consequence: what a hit COSTS is the director's call,
    /// and only the authority makes it.
    /// </summary>
    [RequireComponent(typeof(CarVehicle))]
    public sealed class CarImpact : MonoBehaviour
    {
        public enum Kind
        {
            /// <summary>Nose-first into another car — the attacker's hit.</summary>
            Ram,
            /// <summary>Anything else car-to-car: side-swipes, T-bones taken on
            /// the flank, two cars arriving at the same corner.</summary>
            Side,
            /// <summary>Into the scenery.</summary>
            Wall,
        }

        public readonly struct Hit
        {
            public readonly Kind kind;
            public readonly CarVehicle self;
            /// <summary>The other car, or null for a wall.</summary>
            public readonly CarVehicle other;
            /// <summary>Closing speed at the contact, m/s.</summary>
            public readonly float speed;
            /// <summary>0..1 severity: closing speed against the reference,
            /// which is what every damage number is scaled by.</summary>
            public readonly float severity;
            public readonly Vector3 point;
            /// <summary>Contact normal, pointing away from the other surface.</summary>
            public readonly Vector3 normal;

            public Hit(Kind kind, CarVehicle self, CarVehicle other,
                float speed, float severity, Vector3 point, Vector3 normal)
            {
                this.kind = kind; this.self = self; this.other = other;
                this.speed = speed; this.severity = severity;
                this.point = point; this.normal = normal;
            }
        }

        /// <summary>Raised once per classified impact, on the car that owns this
        /// component. A car-to-car crash raises one on each car, and each sees
        /// the hit from its own side — which is exactly what decides who rammed
        /// whom.</summary>
        public event System.Action<Hit> Impact;

        private CarVehicle _car;
        private float _lastAt;

        /// <summary>Physics hands out several contacts per shunt (a car is a
        /// root box plus four wheel colliders), so the first one wins and the
        /// rest of that shunt is ignored.</summary>
        private const float Debounce = 0.08f;

        private void Awake() => _car = GetComponent<CarVehicle>();

        private void OnCollisionEnter(Collision c)
        {
            if (Impact == null || Time.timeScale <= 0f) return;
            if (Time.time - _lastAt < Debounce) return;

            float speed = c.relativeVelocity.magnitude;
            if (speed < ModeConfig.ImpactMinSpeed) return;

            _lastAt = Time.time;

            var other = c.collider.GetComponentInParent<CarVehicle>();
            Vector3 point = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Vector3 normal = c.contactCount > 0 ? c.GetContact(0).normal : Vector3.up;
            float severity = Mathf.Clamp01(
                (speed - ModeConfig.ImpactMinSpeed) /
                Mathf.Max(0.01f, ModeConfig.ImpactRefSpeed - ModeConfig.ImpactMinSpeed));

            Kind kind;
            if (other == null || other == _car)
            {
                kind = Kind.Wall;
                other = null;
            }
            else
            {
                // The contact normal points OUT of the surface we hit, i.e. back
                // toward us. Driving forward into someone therefore lines our
                // forward up against -normal; the more squarely, the more this
                // was a deliberate ram rather than two cars brushing.
                float align = Vector3.Dot(Flat(transform.forward), Flat(-normal));
                bool goingForward = _car == null || _car.ForwardSpeed > 0.5f;
                kind = (align >= ModeConfig.RamAlignment && goingForward) ? Kind.Ram : Kind.Side;
            }

            Impact.Invoke(new Hit(kind, _car, other, speed, severity, point, normal));
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
