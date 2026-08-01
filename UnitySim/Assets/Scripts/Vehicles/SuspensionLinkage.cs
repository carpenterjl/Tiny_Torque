using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Suspension linkage hardpoints for one corner, in CHASSIS-LOCAL metres — the
    /// same frame as <c>CarWheelConfig.localPos</c>, which is what a CAD hardpoint
    /// table gives you.
    ///
    /// <b>Why hardpoints and not two coefficients.</b> Roll centre height and roll
    /// steer are the two things this engine was missing, and both are *consequences*
    /// of where the arms are bolted, not free parameters. Authoring them directly
    /// would mean two numbers nobody can check; authoring the geometry means the
    /// numbers are derived, and a wrong hardpoint shows up as a roll centre that a
    /// suspension engineer would recognise as wrong. The same hardpoints also carry
    /// camber gain and scrub radius for whenever those are wanted — neither is read
    /// yet.
    ///
    /// <b>All-zero is the sentinel</b>, meaning "no linkage described": the roll
    /// centre is then ground level and the toe link is inert, which is exactly the
    /// behaviour every design had before this struct existed. The ten RC designs
    /// author none of it. See <c>CarWheelConfig</c>'s note on why sentinels rather
    /// than formulas are what make that bit-identical rather than approximate.
    ///
    /// <b>Two layouts, distinguished by what is filled in.</b> If both upper-arm
    /// points are set it is a double wishbone (or a multilink treated as its
    /// equivalent two-arm mechanism, which is the standard front-view reduction);
    /// otherwise, if the strut top mount is set, it is a MacPherson strut, whose
    /// upper constraint is the line perpendicular to the strut axis rather than an
    /// arm. See <see cref="SuspensionGeometry.RollCentreHeight"/>.
    /// </summary>
    [System.Serializable]
    public struct SuspensionLinkage
    {
        /// <summary>Lower arm inner pivot (on the chassis).</summary>
        public Vector3 lowerInner;
        /// <summary>Lower arm outer ball joint (on the knuckle).</summary>
        public Vector3 lowerOuter;

        /// <summary>Upper arm inner pivot; zero = not a wishbone.</summary>
        public Vector3 upperInner;
        /// <summary>Upper arm outer ball joint; zero = not a wishbone.</summary>
        public Vector3 upperOuter;

        /// <summary>Strut upper mount (top of the tower); zero = not a strut. Used
        /// only when the upper arm is absent.</summary>
        public Vector3 strutTop;

        /// <summary>Toe/track-rod link inner pivot (on the chassis); zero = no
        /// roll steer from this corner.</summary>
        public Vector3 toeInner;
        /// <summary>Toe/track-rod link outer joint (on the knuckle).</summary>
        public Vector3 toeOuter;

        /// <summary>True when nothing is authored — the sentinel that keeps every
        /// pre-existing design on its original code path.</summary>
        public bool IsEmpty =>
            lowerInner == Vector3.zero && lowerOuter == Vector3.zero
            && upperInner == Vector3.zero && upperOuter == Vector3.zero
            && strutTop == Vector3.zero
            && toeInner == Vector3.zero && toeOuter == Vector3.zero;

        /// <summary>Has enough geometry for a roll-centre construction.</summary>
        public bool HasRollCentre =>
            lowerInner != lowerOuter
            && ((upperInner != Vector3.zero && upperOuter != Vector3.zero)
                || strutTop != Vector3.zero);

        /// <summary>Has a toe link described.</summary>
        public bool HasToeLink => toeInner != toeOuter;
    }
}
