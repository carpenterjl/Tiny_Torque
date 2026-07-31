using UnityEngine;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// The Blender→Unity coordinate conversion. <b>The only one.</b>
    ///
    /// Blender is Z-up right-handed, Unity is Y-up left-handed, and the mapping
    /// is a swap of the last two axes:
    ///
    ///     unity = (blender.x, blender.z, blender.y)
    ///
    /// Swapping two axes — rather than negating one — is what turns a
    /// right-handed frame into a left-handed one <i>without mirroring the
    /// object</i>. That is the whole trick, and it is also why the rotation is
    /// not just the same swap applied to the axis: the swap has determinant −1,
    /// so conjugating a rotation by it reverses the sense of the angle. A
    /// quaternion (w, x, y, z) about Blender axis (x, y, z) becomes a rotation
    /// by the <i>negated</i> angle about (x, z, y), i.e. (w, −x, −z, −y).
    ///
    /// Get that sign wrong and every prop is reflected about its own axis, which
    /// on a symmetric tree is invisible and on a marshal's post is not.
    ///
    /// <b>None of the above is trusted.</b> <c>CircuitAxisTest</c> runs an
    /// L-shaped marker with a different extent on each axis through the real
    /// pipeline — Blender FBX export, Unity import, this conversion — and
    /// asserts it lands on a copy that Blender baked into place itself. Both
    /// halves of the contract (how the FBX importer moves vertices, and how this
    /// file moves transforms) only ever fail relative to each other, so they are
    /// tested as one thing. See UNITY_EXPORT.md §2: a mirrored circuit renders
    /// perfectly and is completely wrong.
    /// </summary>
    public static class CircuitAxis
    {
        public static Vector3 Position(float bx, float by, float bz) =>
            new Vector3(bx, bz, by);

        public static Vector3 Position(float[] p) =>
            p == null || p.Length < 3 ? Vector3.zero : Position(p[0], p[1], p[2]);

        /// <summary>Blender quaternion, stored w x y z as Blender orders it.</summary>
        public static Quaternion Rotation(float w, float x, float y, float z) =>
            new Quaternion(-x, -z, -y, w);

        public static Quaternion Rotation(float[] q) =>
            q == null || q.Length < 4 ? Quaternion.identity
                                      : Rotation(q[0], q[1], q[2], q[3]);

        /// <summary>A heading measured anticlockwise from Blender +X, as a Unity
        /// Y-rotation. Used for grid slots, which carry a heading rather than a
        /// quaternion because that is all a start box needs.</summary>
        public static Quaternion Heading(float headingDeg) =>
            Quaternion.Euler(0f, 90f - headingDeg, 0f);

        /// <summary>Direction vectors convert exactly like positions — the swap
        /// is linear, so there is no separate case. Spelled out because reaching
        /// for a "TransformDirection" that negates something is the obvious
        /// wrong instinct.</summary>
        public static Vector3 Direction(float bx, float by, float bz) =>
            new Vector3(bx, bz, by);
    }
}
