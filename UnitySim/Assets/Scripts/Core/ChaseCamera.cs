using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Third-person chase camera that trails the target from behind its current
    /// heading, so turning the car swings the view around with it. Uses
    /// yaw-only orientation, so the camera stays upright and doesn't pitch or
    /// roll when the car tilts or launches off a jump.
    /// </summary>
    public sealed class ChaseCamera : MonoBehaviour
    {
        public Transform target;

        [Tooltip("Offset in the target's heading frame: -Z = behind, +Y = above.")]
        public Vector3 offset = new Vector3(0f, 0.7f, -1.5f);
        public float followLerp = 4f;
        public float lookAtHeight = 0.12f;

        /// <summary>Swing round to look behind. Set every frame by
        /// <c>CarInput</c> from the driver's input source. It mirrors the offset
        /// rather than snapping the camera to a second rig, so the existing
        /// follow lerp does the whole transition for free — and because the lerp
        /// is what carries it, releasing the key eases back rather than cutting.</summary>
        public bool lookBack;

        private void LateUpdate()
        {
            if (target == null) return;

            // Heading = the target's forward flattened onto the ground plane.
            Vector3 flatFwd = Vector3.ProjectOnPlane(target.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-4f)                       // car nearly vertical
                flatFwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (flatFwd.sqrMagnitude < 1e-4f) flatFwd = Vector3.forward;
            Quaternion heading = Quaternion.LookRotation(flatFwd.normalized, Vector3.up);

            Vector3 off = offset;
            if (lookBack) off.z = -off.z;              // in front, looking back

            Vector3 desired = target.position + heading * off;
            transform.position = Vector3.Lerp(transform.position, desired, followLerp * Time.deltaTime);
            transform.LookAt(target.position + Vector3.up * lookAtHeight);
        }
    }
}
