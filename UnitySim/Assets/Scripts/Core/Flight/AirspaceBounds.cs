using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The flying field's limits: a box you can see in the Scene view and a
    /// banner that says when the aircraft has left it.
    ///
    /// <b>Advisory, and deliberately nothing more.</b> It does not turn the plane
    /// around, cut the throttle or teleport anything. A car that leaves the world
    /// falls, which is a failure with an obvious fix (see <c>KillPlane</c>); an
    /// aircraft that flies past a boundary is just flying, and the only ways to
    /// "correct" it are to seize the controls or to end the flight. Both are
    /// decisions that belong to whoever is authoring the mission, not to a box.
    /// So this tells the pilot, and stops there.
    ///
    /// Sized in metres from its own transform, not from a collider, because the
    /// airspace is not a physical thing and giving it a collider would make it
    /// one — a trigger volume that every projectile, drone and lock-on raycast in
    /// the combat layer would then have to be taught to ignore.
    /// </summary>
    /// <remarks>Its own file, and authored into saved scenes — a MonoBehaviour
    /// whose filename does not match its class serializes as a Missing Script.</remarks>
    [AddComponentMenu("Tiny Torque/Flight/Airspace Bounds")]
    [DisallowMultipleComponent]
    public sealed class AirspaceBounds : MonoBehaviour
    {
        [Tooltip("Half-extents of the flying field, in metres, from this object's " +
                 "position. Y is height: the box has a ceiling and a floor.")]
        public Vector3 halfExtents = new Vector3(200f, 120f, 200f);

        [Tooltip("The aircraft to watch. Left empty, the first PlaneVehicle in the " +
                 "scene is found at Awake — which is the right answer in every " +
                 "single-aircraft scene, and all of them are.")]
        public PlaneVehicle plane;

        [Tooltip("Show the banner when the aircraft is outside. Off leaves the box " +
                 "as a Scene-view guide only.")]
        public bool showBanner = true;

        private void Awake()
        {
            if (plane == null) plane = FindFirstObjectByType<PlaneVehicle>();
        }

        /// <summary>Is this world point inside the field? Rotation-aware and
        /// scale-blind, so the half-extents stay metres whatever the transform
        /// is doing.</summary>
        public bool Contains(Vector3 world)
        {
            Vector3 d = Quaternion.Inverse(transform.rotation) * (world - transform.position);
            return Mathf.Abs(d.x) <= halfExtents.x
                && Mathf.Abs(d.y) <= halfExtents.y
                && Mathf.Abs(d.z) <= halfExtents.z;
        }

        /// <summary>Metres outside the nearest face, or 0 while inside. What the
        /// banner counts up, so "just over the line" and "two kilometres gone"
        /// do not read the same.</summary>
        public float Excursion(Vector3 world)
        {
            Vector3 d = Quaternion.Inverse(transform.rotation) * (world - transform.position);
            float over = Mathf.Max(
                Mathf.Abs(d.x) - halfExtents.x,
                Mathf.Max(Mathf.Abs(d.y) - halfExtents.y, Mathf.Abs(d.z) - halfExtents.z));
            return Mathf.Max(0f, over);
        }

        private void OnGUI()
        {
            if (!showBanner || plane == null) return;
            float over = Excursion(plane.transform.position);
            if (over <= 0f) return;

            UIScale.Begin();
            var prev = GUI.color;
            GUI.color = new Color(1f, 0.72f, 0.25f);
            var style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
            };
            GUI.Box(new Rect(UIScale.W * 0.5f - 130f, 74f, 260f, 26f),
                    $"OUTSIDE AIRSPACE — {over:0} m", style);
            GUI.color = prev;
            UIScale.End();
        }

        private void OnDrawGizmos() => Draw(0.25f);

        private void OnDrawGizmosSelected() => Draw(0.7f);

        private void Draw(float alpha)
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, alpha);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, halfExtents * 2f);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}
