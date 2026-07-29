using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// The ball.
    ///
    /// **Dynamic on the authority only.** On a LAN client this is a kinematic
    /// visual copy driven from the host's stream, matching the rule the arcade
    /// projectiles already follow to the letter: a client renders shared objects
    /// and never decides anything about them. On the host, client-owned cars are
    /// kinematic followers that still carry their full colliders, so they do
    /// push a dynamic ball — the owning client sees its own touch resolve about
    /// one round trip late, which is inherent to an owner-authoritative session
    /// rather than a bug to chase.
    ///
    /// The extra kick on contact is deliberate: a 1.6 kg car meeting a 0.35 kg
    /// ball at RC scale barely moves it, and "barely moves it" is not the game.
    /// </summary>
    public sealed class SoccerBall : MonoBehaviour
    {
        public Vector3 Home { get; private set; }

        private Rigidbody _body;
        private bool _authority;

        public static SoccerBall Create(Transform parent, Vector3 home, bool authority)
        {
            var go = new GameObject("SoccerBall");
            go.transform.SetParent(parent, false);
            go.transform.position = home;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "ball_skin";
            vis.transform.SetParent(go.transform, false);
            vis.transform.localScale = Vector3.one * (ModeConfig.BallRadius * 2f);
            Destroy(vis.GetComponent<Collider>());   // the real one is on the root
            var mat = TrackBuilder.StandardMat(new Color(0.94f, 0.95f, 0.97f));
            mat.SetFloat("_Glossiness", 0.55f);
            vis.GetComponent<Renderer>().sharedMaterial = mat;

            var col = go.AddComponent<SphereCollider>();
            col.radius = ModeConfig.BallRadius;
            var phys = new PhysicsMaterial("BallBounce")
            {
                bounciness = 0.62f,
                dynamicFriction = 0.28f,
                staticFriction = 0.3f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
            };
            col.material = phys;

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = ModeConfig.BallMass;
            rb.linearDamping = ModeConfig.BallDrag;
            rb.angularDamping = ModeConfig.BallAngularDrag;
            rb.isKinematic = !authority;   // clients render, they do not simulate
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var ball = go.AddComponent<SoccerBall>();
            ball._body = rb;
            ball._authority = authority;
            ball.Home = home;
            return ball;
        }

        /// <summary>Put it back on the centre spot, dead still.</summary>
        public void Reset()
        {
            transform.position = Home;
            if (_body == null || _body.isKinematic) return;
            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
        }

        /// <summary>Pose from the network (clients only).</summary>
        public void ApplyRemote(Vector3 pos, Quaternion rot)
        {
            transform.SetPositionAndRotation(pos, rot);
        }

        public Vector3 Position => transform.position;
        public Vector3 Velocity => _body != null && !_body.isKinematic
            ? _body.linearVelocity : Vector3.zero;

        private void OnCollisionEnter(Collision c)
        {
            if (!_authority || _body == null || _body.isKinematic) return;
            var car = c.collider.GetComponentInParent<CarVehicle>();
            if (car == null) return;

            // Push it along the car's approach, not along the contact normal:
            // a normal-aligned kick sends the ball sideways off a glancing hit,
            // which reads as the ball ignoring you.
            Vector3 dir = c.relativeVelocity.sqrMagnitude > 0.01f
                ? -c.relativeVelocity.normalized
                : car.transform.forward;
            dir.y = Mathf.Max(dir.y, 0.05f);   // never drive it into the floor
            float speed = c.relativeVelocity.magnitude;
            _body.AddForce(dir.normalized * (speed * ModeConfig.BallHitBoost * _body.mass),
                ForceMode.Impulse);
        }
    }
}
