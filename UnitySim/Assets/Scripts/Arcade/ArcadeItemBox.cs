using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// A power-up box. Structurally the same as <see cref="Track.Checkpoint"/> —
    /// a trigger volume that resolves the car from the collider's parent and hands
    /// it to a director — with the addition of a deplete/respawn cycle.
    ///
    /// The car root's BoxCollider and every WheelCollider are children of the same
    /// transform, so one pass can fire OnTriggerEnter several times for one car;
    /// the collider is disabled on the first accepted touch rather than relying on
    /// a time debounce.
    ///
    /// On a LAN client the trigger is removed outright at Awake: pickups are
    /// host-authoritative and the client's cars are kinematic ghosts that must
    /// never arm anything (the same reason TrackBootstrap destroys LapTimer and
    /// Checkpoint on clients). The visual stays so the box is still seen.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class ArcadeItemBox : MonoBehaviour
    {
        /// <summary>The spinning/hiding visual. Defaults to this transform.</summary>
        public Transform viz;

        public bool Active { get; private set; } = true;
        public float RespawnAt { get; private set; }

        private Collider _col;
        private float _spin;

        private void Awake()
        {
            _col = GetComponent<Collider>();
            if (_col != null) _col.isTrigger = true;
            if (viz == null) viz = transform;

            var net = Net.NetSession.Instance;
            if (net != null && !net.IsHost && _col != null)
                Destroy(_col);   // clients render boxes, they never grant them
        }

        private void Update()
        {
            if (!Active || viz == null) return;
            // Idle animation: slow spin plus a shallow bob, so a box reads as a
            // pickup rather than scenery.
            _spin += Time.deltaTime;
            viz.localRotation = Quaternion.Euler(0f, _spin * 90f, 0f);
            viz.localPosition = new Vector3(0f, Mathf.Sin(_spin * 2.2f) * 0.015f, 0f);
        }

        /// <summary>Consume this box; it comes back after the respawn delay.</summary>
        public void Deplete(float clock)
        {
            Active = false;
            RespawnAt = clock + ArcadeConfig.BoxRespawnSeconds;
            if (_col != null) _col.enabled = false;
            if (viz != null) viz.gameObject.SetActive(false);
        }

        public void Respawn()
        {
            Active = true;
            RespawnAt = 0f;
            if (_col != null) _col.enabled = true;
            if (viz != null) viz.gameObject.SetActive(true);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!Active) return;
            if (other.isTrigger) return;   // gates, other boxes, projectile volumes
            var dir = ArcadeDirector.Instance;
            if (dir == null || !dir.IsAuthority) return;
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return;
            dir.OnItemBoxTouched(car, this);
        }
    }
}
