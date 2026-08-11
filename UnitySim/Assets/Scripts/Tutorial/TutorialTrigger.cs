using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// A place a tutorial step can ask the player to drive to. Attach to a
    /// trigger volume and point a <see cref="TutorialStep"/> at it.
    ///
    /// The <see cref="Checkpoint"/> shape, and for the same reasons: colliders
    /// live on children of the car so the lookup is
    /// <c>GetComponentInParent</c>, and <c>Reset</c> forces <c>isTrigger</c>
    /// because a solid checkpoint is a wall nobody meant to build.
    ///
    /// It latches rather than raising an event. A step is only listening while
    /// it is the current one, but a player can cross a volume early — the latch
    /// means an objective they happened to already satisfy completes the moment
    /// it is asked for, instead of sending them back to drive through a gate
    /// they are standing in.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class TutorialTrigger : MonoBehaviour
    {
        [Tooltip("Optional label, for reading the scene. Nothing matches on it.")]
        public string note = "";

        [Tooltip("Hide the volume's renderer at play. Leave on: these are " +
                 "authoring aids, and a floating box is not a lesson.")]
        public bool hideVisual = true;

        /// <summary>Has the player's car been in here since the last reset?</summary>
        public bool Entered { get; private set; }

        /// <summary>Is the player's car in here right now? For a step that wants
        /// the car to STAY somewhere rather than pass through it.</summary>
        public bool Inside { get; private set; }

        private CarVehicle _watch;

        /// <summary>
        /// Whose crossings count. Set by the director to the player's car, so a
        /// bot wandering through a gate cannot complete the lesson.
        /// </summary>
        public void Watch(CarVehicle car) => _watch = car;

        public void ResetLatch()
        {
            Entered = false;
            Inside = false;
        }

        private void Reset()
        {
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void Awake()
        {
            if (!hideVisual) return;
            var r = GetComponent<Renderer>();
            if (r != null) r.enabled = false;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!Matches(other)) return;
            Entered = true;
            Inside = true;
        }

        private void OnTriggerExit(Collider other)
        {
            if (!Matches(other)) return;
            Inside = false;
        }

        /// <summary>Null <c>_watch</c> accepts any car: pressing Play directly in
        /// a tutorial scene before the director has bound the rig should still
        /// let the volumes work, and there are no bots in a tutorial to confuse
        /// it with.</summary>
        private bool Matches(Collider other)
        {
            var car = other.GetComponentInParent<CarVehicle>();
            if (car == null) return false;
            return _watch == null || car == _watch;
        }

        private void OnDrawGizmos()
        {
            var col = GetComponent<Collider>();
            if (col == null) return;
            Gizmos.color = new Color(0.30f, 0.80f, 1f, 0.25f);
            Gizmos.matrix = transform.localToWorldMatrix;
            if (col is BoxCollider box) Gizmos.DrawCube(box.center, box.size);
            else Gizmos.DrawWireSphere(Vector3.zero, 1f);
        }
    }
}
