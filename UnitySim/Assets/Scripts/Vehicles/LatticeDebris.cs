using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One chunk knocked off a crash frame: a lifetime, a global live cap, and
    /// the guarantee that debris NEVER pushes a car — every car's chassis box is
    /// ignore-listed at spawn, so a flying bumper is scenery to the physics
    /// gates and to the race, exactly like drift smoke with a rigidbody.
    /// </summary>
    public sealed class LatticeDebris : MonoBehaviour
    {
        /// <summary>Seconds before a chunk stops existing. Long enough to watch
        /// it tumble, short enough that a demolition match is not a landfill.</summary>
        public const float LifetimeS = 6f;

        /// <summary>Chunks alive at once, scene-wide. Past it the OLDEST dies —
        /// the newest chunk is the one somebody just made happen.</summary>
        public const int MaxLive = 12;

        private static readonly List<LatticeDebris> _live = new List<LatticeDebris>();

        private float _dieAt;
        private Mesh _ownedMesh;

        /// <summary>
        /// Stand up a debris chunk. The mesh is OWNED by the chunk and destroyed
        /// with it; the material is shared with the car (paint survives free).
        /// </summary>
        public static LatticeDebris Spawn(Mesh mesh, Material mat, Vector3 worldPos,
                                          Quaternion worldRot, Vector3 worldScale,
                                          Vector3 velocity)
        {
            var go = new GameObject("LatticeDebris");
            go.transform.SetPositionAndRotation(worldPos, worldRot);
            go.transform.localScale = worldScale;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;

            // A box from the mesh bounds, floored — a mirror shard still needs a
            // collider a solver can keep out of the floor.
            var col = go.AddComponent<BoxCollider>();
            Bounds b = mesh.bounds;
            col.center = b.center;
            col.size = Vector3.Max(b.size, Vector3.one * 0.01f);

            var body = go.AddComponent<Rigidbody>();
            body.mass = 0.03f;
            body.linearVelocity = velocity;
            // Spin derived from the throw, not from Random — nothing in the
            // lattice path consumes RNG state, and a chunk tumbling about the
            // axis perpendicular to its flight looks right anyway.
            body.angularVelocity = Vector3.Cross(velocity, Vector3.up) * 4f;

            // Never a force path back into any car. Cheap: a handful of cars,
            // and chunks are rare events.
            foreach (CarVehicle car in Object.FindObjectsByType<CarVehicle>(
                         FindObjectsSortMode.None))
            {
                var box = car.GetComponent<BoxCollider>();
                if (box != null) Physics.IgnoreCollision(col, box);
            }

            var debris = go.AddComponent<LatticeDebris>();
            debris._ownedMesh = mesh;
            debris._dieAt = Time.time + LifetimeS;

            _live.Add(debris);
            while (_live.Count > MaxLive)
            {
                LatticeDebris oldest = _live[0];
                _live.RemoveAt(0);
                if (oldest != null) Destroy(oldest.gameObject);
            }
            return debris;
        }

        private void Update()
        {
            if (Time.time >= _dieAt) Destroy(gameObject);
        }

        private void OnDestroy()
        {
            _live.Remove(this);
            if (_ownedMesh != null) Destroy(_ownedMesh);
        }
    }
}
