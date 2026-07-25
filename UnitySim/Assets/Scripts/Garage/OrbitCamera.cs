using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Showroom orbit camera: hold right mouse to swing around the pivot, scroll
    /// to zoom. Used by the garage so the player can inspect the vehicle they're
    /// assembling from any angle. Skips its own drag while the pointer is over the
    /// garage UI panels (see <see cref="blockDrag"/>).
    /// </summary>
    public sealed class OrbitCamera : MonoBehaviour
    {
        public Vector3 pivot = new Vector3(0f, 0.06f, 0f);
        public float distance = 1.3f;
        public float minDistance = 0.35f;
        public float maxDistance = 6f;
        public float yaw = 35f;
        public float pitch = 18f;
        public float rotateSpeed = 0.22f;
        // Proportional zoom: fraction of the current distance per scroll unit
        // (~±1.2/notch), so zooming feels identical at 0.5 m and 50 m out.
        public float zoomSpeed = 0.2f;
        public float maxPitch = 80f; // track builder raises this for its top-down view

        /// <summary>Set by the UI each frame to suppress orbit/pan while over a panel.</summary>
        public bool blockDrag;

        /// <summary>
        /// Set by the UI each frame to suppress scroll zoom independently of
        /// <see cref="blockDrag"/> — the garage keeps orbit/pan live during a part
        /// drag but hands the scroll wheel to ghost-yaw unless Ctrl is held.
        /// Callers that only ever set blockDrag get the old coupled behaviour.
        /// </summary>
        public bool blockZoom;

        /// <summary>Frame a world point: recentre the pivot (and optionally zoom in).</summary>
        public void FocusOn(Vector3 worldPoint, float newDistance = -1f)
        {
            pivot = worldPoint;
            if (newDistance > 0f) distance = Mathf.Clamp(newDistance, minDistance, maxDistance);
        }

        private void LateUpdate()
        {
            if (!blockDrag && InputReader.RightMouseHeld())
            {
                Vector2 d = InputReader.MouseDelta();
                yaw += d.x * rotateSpeed;
                pitch = Mathf.Clamp(pitch - d.y * rotateSpeed, -5f, maxPitch);
            }

            // Middle-mouse pan: slide the pivot in the camera's screen plane.
            if (!blockDrag && InputReader.MiddleMouseHeld())
            {
                Vector2 d = InputReader.MouseDelta();
                pivot -= (transform.right * d.x + transform.up * d.y) * (0.0016f * distance);
            }

            float scroll = InputReader.ScrollDelta();
            if (!blockDrag && !blockZoom && Mathf.Abs(scroll) > 0.0001f)
                distance = Mathf.Clamp(
                    distance * (1f - Mathf.Clamp(scroll * zoomSpeed, -0.6f, 0.6f)),
                    minDistance, maxDistance);

            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.position = pivot + rot * new Vector3(0f, 0f, -distance);
            transform.LookAt(pivot);
        }
    }
}
