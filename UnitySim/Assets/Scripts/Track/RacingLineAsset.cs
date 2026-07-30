using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// A baked ideal line: where to drive, how fast, where the apexes are and
    /// where the braking happens.
    ///
    /// <b>Nothing in the shipped game reads this yet, on purpose.</b> Bots still run
    /// their own out-in-out heuristic, so race feel, lap times and difficulty tiers
    /// are exactly what they were. This is a design and analysis artefact first —
    /// look at the line, check the sector targets, then decide whether bots should
    /// follow it. Wiring it into BotDriver is a gameplay change and gets made
    /// deliberately, not as a side effect of baking one.
    ///
    /// Lives in Scripts/ rather than an Editor/ folder: the calibration run reads it
    /// in play mode, and a player build would have to be able to load it the day
    /// anything does consume it.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Racing Line", fileName = "RacingLine")]
    public sealed class RacingLineAsset : ScriptableObject
    {
        [System.Serializable]
        public struct BrakeZone
        {
            public float sStart, sEnd;    // arc length along the line, metres
            public float vEntry, vExit;   // m/s
        }

        [System.Serializable]
        public struct Calibration
        {
            /// <summary>False until a headless run has actually measured this car
            /// on this track. An uncalibrated profile is a physics model's opinion,
            /// and the validator refuses to let one masquerade as a measurement.</summary>
            public bool valid;

            public float muScale;        // measured grip / catalog frictionMult
            public float accelA0;        // m/s^2 at a standstill
            public float vMax;           // m/s, the drive model's asymptote
            public float brakeUse;       // fraction of the friction circle braking uses

            public float measuredLapSec;
            public float predictedLapSec;
            public float residualPct;    // |predicted - measured| / measured * 100

            /// <summary>Laps 2 and 3 are the same lap driven twice. Surface roughness
            /// is a deterministic positional field, so they must agree; a gap means
            /// something non-deterministic is in the loop and the fit is noise.</summary>
            public float lapRepeatDeltaSec;

            /// <summary>Fraction of the lap actually driven at the grip limit. A fit
            /// from a car that never reached the limit is extrapolation.</summary>
            public float limitFraction;

            public string vehicle;       // which car this was measured with
        }

        [Header("Source")]
        [Tooltip("Scene this line was baked from. A line pointed at the wrong scene " +
                 "is worse than none.")]
        public string sceneName = "";

        /// <summary>Hash of the corridor the line was solved against. The stale-bake
        /// case — edit the spline, forget to re-bake, ship a line that cuts the new
        /// corner — is the most likely real failure of this whole tool, and this is
        /// what lets the validator catch it.</summary>
        public string bakeHash = "";

        [Header("Line")]
        public Vector3[] points = System.Array.Empty<Vector3>();

        /// <summary>Signed curvature per node, rad/m, positive = right turn.</summary>
        public float[] curvature = System.Array.Empty<float>();

        /// <summary>Friction-limited target speed per node, m/s.</summary>
        public float[] speed = System.Array.Empty<float>();

        /// <summary>Lateral offset from the corridor centreline per node, metres.
        /// Kept so the validator can prove every node is still inside the corridor
        /// without re-deriving the solve.</summary>
        public float[] lateral = System.Array.Empty<float>();

        public bool closed = true;

        [Header("Features")]
        public int[] apexIndices = System.Array.Empty<int>();
        public BrakeZone[] brakeZones = System.Array.Empty<BrakeZone>();

        [Header("Timing")]
        public float predictedLapSec;
        public float lineLength;

        public Calibration calibration;

        /// <summary>Usable when the parallel arrays agree and there is a line at all.
        /// A half-written asset is the one thing a consumer must not act on.</summary>
        public bool IsUsable =>
            points != null && points.Length >= 2 &&
            speed != null && speed.Length == points.Length &&
            curvature != null && curvature.Length == points.Length;
    }
}
