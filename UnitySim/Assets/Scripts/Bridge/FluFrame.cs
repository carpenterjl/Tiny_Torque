using UnityEngine;

namespace AIHWSim.Bridge
{
    /// <summary>
    /// The one place Unity's frame becomes the firmware's (ABI-04).
    ///
    /// Unity: x right, y up, z forward — LEFT-handed. Firmware (ABI v7): FLU,
    /// x forward, y left, z up — RIGHT-handed (ISO 8855 / ROS REP-103), so a
    /// left turn and a left steer are positive.
    ///
    /// A vector maps by the axis permutation alone. An angular rate is a
    /// pseudo-vector, so it also flips sign with the change of handedness.
    /// <c>Editor/FrameConventionCheck</c> proves both against real motion.
    /// </summary>
    public static class FluFrame
    {
        /// <summary>Body vector (position, velocity, specific force) in FLU.</summary>
        public static Vector3 Vector(Vector3 u) => new Vector3(u.z, -u.x, u.y);

        /// <summary>Body angular rate in FLU: (-w.z, w.x, -w.y).</summary>
        public static Vector3 Rate(Vector3 w) => new Vector3(-w.z, w.x, -w.y);
    }
}
