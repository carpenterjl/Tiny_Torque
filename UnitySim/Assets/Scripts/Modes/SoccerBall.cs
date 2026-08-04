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
        private SphereCollider _col;
        private Transform _skin;

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
            var phys = new PhysicsMaterial("BallBounce")
            {
                bounciness = 0.62f,
                dynamicFriction = 0.28f,
                staticFriction = 0.3f,
                bounceCombine = PhysicsMaterialCombine.Maximum,
            };
            col.material = phys;

            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = !authority;   // clients render, they do not simulate
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            var ball = go.AddComponent<SoccerBall>();
            ball._body = rb;
            ball._col = col;
            ball._skin = vis.transform;
            ball._authority = authority;
            ball.Home = home;
            ball.ApplyTuning();   // size, mass and damping all come from one place
            return ball;
        }

        /// <summary>
        /// Copy the tuned size, mass and damping onto the Rigidbody and the
        /// collider.
        ///
        /// These are the only mode numbers in the project that are BAKED rather
        /// than read at the point of use — a Rigidbody owns its mass, and asking
        /// for it every step would not make the ball any lighter. So this runs at
        /// creation and again whenever a tuning asset is edited, which is what
        /// makes "ball weight" a slider you can drag during a match rather than
        /// one that takes a restart.
        ///
        /// The visible sphere is resized alongside the collider deliberately: a
        /// gameplay radius that does not match the thing on screen is the one
        /// bug in this area nobody would think to look for.
        /// </summary>
        private void ApplyTuning()
        {
            float r = ModeConfig.BallRadius;
            if (_col != null) _col.radius = r;
            if (_skin != null) _skin.localScale = Vector3.one * (r * 2f);
            if (_body == null) return;
            _body.mass = ModeConfig.BallMass;
            _body.linearDamping = ModeConfig.BallDrag;
            _body.angularDamping = ModeConfig.BallAngularDrag;
        }

        private void OnEnable() => Core.Config.TuningBus.Changed += OnTuningChanged;
        private void OnDisable() => Core.Config.TuningBus.Changed -= OnTuningChanged;

        private void OnTuningChanged(ScriptableObject _) => ApplyTuning();

        /// <summary>
        /// The ball's own gravity, as a multiple of the world's.
        ///
        /// Added as a force rather than by switching <c>useGravity</c> off and
        /// integrating it here, so at the default 1 this method costs one compare
        /// and writes nothing at all — the ball falls under exactly the same
        /// engine gravity it always did, and there is no arrangement of the
        /// slider that quietly changes the shipped behaviour. At 0 the added
        /// force cancels weight precisely; above 1 it adds to it.
        ///
        /// Authority only, like everything else here: a client's ball is
        /// kinematic and is told where it is.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_authority || _body == null || _body.isKinematic) return;
            float scale = ModeConfig.BallGravityScale;
            if (Mathf.Approximately(scale, 1f)) return;
            _body.AddForce(Physics.gravity * ((scale - 1f) * _body.mass), ForceMode.Force);
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
