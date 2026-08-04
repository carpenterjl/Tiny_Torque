using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// The floor under the floor: a trigger volume beneath the world that puts
    /// back any car that has left it.
    ///
    /// A template scene is a slab in empty space, so driving off the edge is one
    /// steering input away and the result — a car falling forever while the
    /// camera follows it down — is the least useful failure a scene can have.
    /// This is the answer, and it is deliberately the SAME answer the respawn key
    /// already gives: <see cref="TrackRespawn.TryPose"/>, which projects onto the
    /// racing line on a circuit and picks the nearest free spawn in an arena, so
    /// a car that fell off arrives facing somewhere useful rather than at the
    /// origin facing north.
    ///
    /// <b>It does not touch the lap timer</b>, unlike the respawn key. The key is
    /// a deliberate act with a cost attached; this is a rescue from geometry, and
    /// there is nothing to price. It cannot become a shortcut either: on a track
    /// with checkpoints the returned car still owes every gate it has not passed,
    /// and on one without, falling off and being put back near where you fell is
    /// not faster than driving.
    ///
    /// <b>Host-authoritative</b>, like every other trigger in this project: on a
    /// LAN client the collider is removed at Awake, because a client's cars are
    /// kinematic ghosts and a ghost that respawns itself is a car in two places.
    /// </summary>
    /// <remarks>Its own file, and a component authored into saved scenes — see
    /// <see cref="TrackMarker"/> for what happens to a MonoBehaviour whose
    /// filename does not match its class.</remarks>
    [AddComponentMenu("Tiny Torque/Track/Kill Plane")]
    [RequireComponent(typeof(Collider))]
    public sealed class KillPlane : MonoBehaviour
    {
        /// <summary>Per-plane search hint into the racing line, so two cars
        /// falling off opposite sides of the same corner do not drag each other's
        /// projection across it.</summary>
        private int _hint;

        private void Awake()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost && col != null) Destroy(col);
        }

        private void Reset()
        {
            // Authoring convenience only: a fresh Kill Plane is a trigger from the
            // moment it is added, so nobody discovers at Play that the box under
            // the world is a wall.
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            // The car root's box and every WheelCollider are children of one
            // transform, so this fires several times per fall; each call is
            // idempotent, and the second one finds a car already back on track.
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;

            if (TrackRespawn.TryPose(car.transform.position, ref _hint,
                    out var pos, out var rot))
                car.ResetVehicleTo(pos, rot);
            else
                car.ResetVehicle();   // no line and no spawn ring: back to the start
        }

        private void OnDrawGizmosSelected()
        {
            var col = GetComponent<Collider>();
            if (col == null) return;
            Gizmos.color = new Color(1f, 0.3f, 0.25f, 0.35f);
            Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
        }
    }
}
