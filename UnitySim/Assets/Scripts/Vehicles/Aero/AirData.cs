using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>
    /// The airflow an aircraft sees, in its own body frame. One struct, computed
    /// once per step, read by the panels, the propeller, the HUD and telemetry —
    /// so there is exactly one answer to "how fast are we going through the air"
    /// and nothing can disagree with the number on screen.
    ///
    /// <b>Airspeed is not ground speed, and they are separate from day one.</b>
    /// They are equal only in still air. Publishing one under the other's name
    /// works fine right up until wind exists, at which point every measurement
    /// taken before it becomes unreadable — so <see cref="Tas"/> is derived from
    /// the air-relative velocity even while <see cref="Wind"/> is always zero.
    ///
    /// Body-frame convention, matching Unity and the rest of this project:
    /// +Z forward, +Y up, +X right.
    /// </summary>
    public readonly struct AirData
    {
        /// <summary>Aircraft velocity through the air, body frame (m/s).</summary>
        public readonly Vector3 VelocityBody;
        /// <summary>True airspeed (m/s).</summary>
        public readonly float Tas;
        /// <summary>Angle of attack (rad). Positive = nose above the flight path.</summary>
        public readonly float AlphaRad;
        /// <summary>Sideslip (rad). Positive = airflow arriving from the right.</summary>
        public readonly float BetaRad;
        /// <summary>Dynamic pressure ½·ρ·V² (Pa).</summary>
        public readonly float Q;

        public float AlphaDeg => AlphaRad * Mathf.Rad2Deg;
        public float BetaDeg => BetaRad * Mathf.Rad2Deg;

        private AirData(Vector3 velBody, float tas, float alpha, float beta, float q)
        {
            VelocityBody = velBody;
            Tas = tas;
            AlphaRad = alpha;
            BetaRad = beta;
            Q = q;
        }

        /// <summary>
        /// Wind, world frame (m/s). Always zero today; it exists as a named seam so
        /// that the day it stops being zero, nothing else has to change and every
        /// airspeed channel is already correct.
        /// </summary>
        public static Vector3 Wind => Vector3.zero;

        /// <summary>
        /// Resolve the air data at the body origin.
        /// <paramref name="worldVelocity"/> is the rigidbody's world velocity;
        /// <paramref name="bodyRotation"/> its world rotation.
        /// </summary>
        public static AirData FromBody(Vector3 worldVelocity, Quaternion bodyRotation)
        {
            Vector3 rel = worldVelocity - Wind;
            Vector3 vb = Quaternion.Inverse(bodyRotation) * rel;

            float tas = vb.magnitude;
            if (tas < 1e-4f) return new AirData(vb, 0f, 0f, 0f, 0f);

            // alpha from the flow's vertical component: moving forward and DOWN
            // (vb.y < 0) means the air arrives from below, which is positive AoA.
            float alpha = Mathf.Atan2(-vb.y, vb.z);
            // beta from the lateral component; asin because it is measured against
            // the total speed, not against the longitudinal component alone.
            float beta = Mathf.Asin(Mathf.Clamp(vb.x / tas, -1f, 1f));

            return new AirData(vb, tas, alpha, beta,
                0.5f * AeroDynamics.AirDensity * tas * tas);
        }
    }
}
