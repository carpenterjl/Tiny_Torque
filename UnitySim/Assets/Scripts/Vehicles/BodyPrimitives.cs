using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One box of a primitive body compound, as fractions of the design's
    /// <c>bodySize</c>.
    ///
    /// <see cref="pos"/> and <see cref="scale"/> are fractions; <see cref="euler"/>
    /// is absolute degrees. That split is not a style choice — it is what makes a
    /// compound scale-free, which is what lets the drag estimator measure a
    /// silhouette once and evaluate it at any bodySize.
    /// </summary>
    public readonly struct BodyPiece
    {
        public readonly Vector3 pos;    // centre, as a fraction of bodySize
        public readonly Vector3 scale;  // extents, as a fraction of bodySize
        public readonly Vector3 euler;  // degrees, absolute

        public BodyPiece(Vector3 pos, Vector3 scale, Vector3 euler)
        {
            this.pos = pos; this.scale = scale; this.euler = euler;
        }

        public BodyPiece(float px, float py, float pz,
                         float sx, float sy, float sz, float pitchDeg = 0f)
            : this(new Vector3(px, py, pz), new Vector3(sx, sy, sz),
                   pitchDeg == 0f ? Vector3.zero : new Vector3(pitchDeg, 0f, 0f)) { }
    }

    /// <summary>
    /// The five hand-built body compounds, as data.
    ///
    /// These lists were the bodies of <c>CarVehicle.BuildBoxBody</c> /
    /// <c>BuildWedgeBody</c> / <c>BuildBuggyBody</c> / <c>BuildShellBody</c> /
    /// <c>BuildLowRacerBody</c>, transcribed verbatim — same pieces, same order.
    /// They moved here because they acquired a SECOND reader: the drag estimator
    /// projects them to measure a frontal silhouette, and a compound that renders
    /// one shape while estimating another would be a silent lie about what the
    /// player is looking at. One table, both readers.
    ///
    /// <b>Bit-identity of the transcription.</b> Every literal below is the same
    /// literal the builder wrote, and the multiply is commutative:
    /// <c>-s.y * 0.22f</c> and <c>-0.22f * s.y</c> are the same IEEE-754 bits, so
    /// <c>Vector3.Scale(piece.pos, bodySize)</c> reproduces the old argument
    /// exactly. That is why the numbers are fractions here rather than being
    /// re-derived into something tidier.
    ///
    /// <b>The renderer is the reference, not this file.</b> These compounds exist
    /// because someone shaped them until the car looked right; changing a number
    /// to make an estimate come out nicer would be changing the car to fit the
    /// measurement.
    /// </summary>
    public static class BodyPrimitives
    {
        /// <summary>A single box the size of the design. The flat-wall reference.</summary>
        public static readonly BodyPiece[] Box =
        {
            new BodyPiece(0f, 0f, 0f,  1f, 1f, 1f),
        };

        /// <summary>Low slab with a raked upper deck.</summary>
        public static readonly BodyPiece[] Wedge =
        {
            new BodyPiece(0f, -0.22f,  0f,     1f,   0.55f, 1f),
            new BodyPiece(0f,  0.18f, -0.12f,  0.9f, 0.5f,  0.55f, 8f),
        };

        /// <summary>Narrow tub, roll hoop, nose pod — the open frame.</summary>
        public static readonly BodyPiece[] Buggy =
        {
            new BodyPiece(0f, -0.05f,  0f,     0.5f,  0.6f,  0.85f),
            new BodyPiece(0f,  0.45f, -0.05f,  0.55f, 0.14f, 0.3f),
            new BodyPiece(0f, -0.15f,  0.45f,  0.35f, 0.3f,  0.22f),
        };

        /// <summary>Touring-car lexan shell: low slab, tapered cabin, faired nose + tail.</summary>
        public static readonly BodyPiece[] Shell =
        {
            // Lower hull.
            new BodyPiece(0f, -0.25f,  0f,     1f,    0.5f,  0.94f),
            // Cabin: narrower, raked front and rear glass.
            new BodyPiece(0f,  0.12f, -0.06f,  0.82f, 0.42f, 0.44f),
            new BodyPiece(0f,  0.02f,  0.20f,  0.80f, 0.34f, 0.24f,  22f),
            new BodyPiece(0f,  0.02f, -0.30f,  0.80f, 0.34f, 0.22f, -18f),
            // Faired nose.
            new BodyPiece(0f, -0.22f,  0.44f,  0.85f, 0.38f, 0.18f,  10f),
        };

        /// <summary>F1TENTH-style low racer: flat deck, nose wedge, side pods.</summary>
        public static readonly BodyPiece[] LowRacer =
        {
            // Flat main deck, low.
            new BodyPiece( 0f,   -0.28f,  0f,     0.9f,  0.44f, 0.9f),
            // Nose wedge.
            new BodyPiece( 0f,   -0.14f,  0.38f,  0.6f,  0.3f,  0.26f, 14f),
            // Side pods (the builder's e = -1, +1 loop, unrolled in the same order).
            new BodyPiece(-0.42f, -0.24f, -0.05f, 0.18f, 0.36f, 0.5f),
            new BodyPiece( 0.42f, -0.24f, -0.05f, 0.18f, 0.36f, 0.5f),
            // Electronics hump behind the (imagined) driver.
            new BodyPiece( 0f,    0.06f, -0.18f,  0.4f,  0.34f, 0.28f),
        };

        /// <summary>
        /// The compound a shape renders as. The default is <see cref="Box"/> for
        /// the same reason the builder's switch defaulted there: a catalogue body
        /// whose FBX did not ship still has to be something.
        /// </summary>
        public static BodyPiece[] For(BodyShape shape)
        {
            switch (shape)
            {
                case BodyShape.Wedge:    return Wedge;
                case BodyShape.Buggy:    return Buggy;
                case BodyShape.Shell:    return Shell;
                case BodyShape.LowRacer: return LowRacer;
                default:                 return Box;
            }
        }
    }
}
