using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// A dropped banana peel.
    ///
    /// Deliberately NOT built on TrackFactory's dynamic-prop pattern: those props
    /// get non-trigger Rigidbodies so the car can shove them around, and a banana
    /// you can bump out of the way is not a banana. This is a kinematic trigger —
    /// driving into it spins you out, and nothing moves it.
    ///
    /// The owner is immune only briefly. After that grace it can hit its own
    /// dropper, which is both the Mario Kart behaviour and what stops a player
    /// parking on their own drop and using it as a rear shield.
    /// </summary>
    public sealed class Banana : MonoBehaviour
    {
        public int objId;
        public int ownerSlot = -1;
        public CarVehicle ownerCar;
        public float droppedAt;
        public float expiresAt;

        private void Awake()
        {
            var col = gameObject.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = ArcadeConfig.BananaRadius;

            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;   // required for reliable trigger callbacks
            rb.useGravity = false;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost) Destroy(col);   // visual-only on clients
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.isTrigger) return;
            var dir = ArcadeDirector.Instance;
            if (dir == null || !dir.IsAuthority) return;

            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            if (car == ownerCar && ArcadeDirector.Clock - droppedAt < ArcadeConfig.BananaOwnerGrace)
                return;

            dir.OnBananaHit(this, car);
        }
    }
}
