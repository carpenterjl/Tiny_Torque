using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>Which pilot axis moves this surface's control hinge.</summary>
    public enum ControlAxis { None = 0, Aileron = 1, Elevator = 2, Rudder = 3 }

    /// <summary>
    /// One lifting surface, described the way a builder would describe it: where
    /// the root quarter-chord sits, how long the span is, what the chords are,
    /// how much dihedral and twist, and where the hinge line falls.
    ///
    /// <b>Nothing here is a coefficient.</b> Area, aspect ratio, mean chord and
    /// control-surface effectiveness are all DERIVED from these dimensions — the
    /// same contract <see cref="SuspensionGeometry"/> holds for the suspension,
    /// where roll centre and roll steer come from bolt positions rather than being
    /// authored as numbers that could disagree with the drawing.
    ///
    /// A surface is either <see cref="mirrored"/> (a wing or tailplane: two halves,
    /// span = 2·semiSpan) or single (a fin: span = its height). Aspect ratio is a
    /// property of the WHOLE surface, so a wing half must never compute its own —
    /// that is what <see cref="AspectRatio"/> is for.
    /// </summary>
    [System.Serializable]
    public struct LiftingSurface
    {
        public string name;

        /// <summary>Quarter-chord of the ROOT rib, body-local (m).</summary>
        public Vector3 rootQuarterChord;

        /// <summary>Half-span for a mirrored surface; full height for a fin (m).</summary>
        public float semiSpan;

        public float rootChord;
        public float tipChord;

        /// <summary>Dihedral (deg), positive = tips up. Ignored on a fin.</summary>
        public float dihedralDeg;
        /// <summary>Quarter-chord sweep (deg), positive = tips aft.</summary>
        public float sweepDeg;
        /// <summary>Fixed mounting incidence (deg), positive = leading edge up.</summary>
        public float incidenceDeg;
        /// <summary>Twist added linearly out to the tip (deg). NEGATIVE is washout,
        /// which is what makes the root stall before the tip and therefore what
        /// keeps a wing drop from becoming a spin.</summary>
        public float washoutDeg;

        /// <summary>Two halves (wing, tailplane) rather than one (fin).</summary>
        public bool mirrored;
        /// <summary>Span runs vertically and the surface's "lift" is a side force.</summary>
        public bool vertical;

        public ControlAxis control;
        /// <summary>Hinge position as a fraction of chord (0.25 = a quarter-chord
        /// flap). Drives effectiveness through thin-airfoil theory, so a bigger
        /// control surface is more powerful because it IS bigger.</summary>
        public float controlChordFrac;
        /// <summary>Hinge extent, as fractions of the semi-span.</summary>
        public float controlSpanStart, controlSpanEnd;
        /// <summary>Maximum hinge deflection (deg) at full stick.</summary>
        public float controlMaxDeg;

        /// <summary>⚠ Effective-aspect-ratio multiplier. 1 everywhere except a fin,
        /// where the fuselage and tailplane act as end plates and raise the
        /// effective AR by roughly half again. That factor cannot be derived
        /// within this model and is not sourced, so every directional-axis result
        /// inherits its uncertainty — which is why the yaw tests report Info.</summary>
        public float aspectRatioScale;

        public int panelsPerSide;
        public Airfoil airfoil;

        // ---- derived: geometry only, no fitted numbers ----

        /// <summary>Planform area (m²), counting both halves when mirrored.</summary>
        public float Area => 0.5f * (rootChord + tipChord) * semiSpan * (mirrored ? 2f : 1f);

        /// <summary>Full span (m) — tip to tip, or root to tip for a fin.</summary>
        public float Span => semiSpan * (mirrored ? 2f : 1f);

        /// <summary>Effective aspect ratio b²/S, scaled by <see cref="aspectRatioScale"/>.</summary>
        public float AspectRatio
        {
            get
            {
                float s = Area;
                if (s <= 1e-6f) return 0f;
                float scale = aspectRatioScale <= 0f ? 1f : aspectRatioScale;
                return Span * Span / s * scale;
            }
        }

        /// <summary>Finite-wing lift slope (per rad) for this surface.</summary>
        public float LiftSlope => Airfoil.LiftSlope(AspectRatio, airfoil.oswald);

        /// <summary>Mean aerodynamic chord (m), the standard taper integral.</summary>
        public float MeanAerodynamicChord
        {
            get
            {
                if (rootChord <= 1e-6f) return 0f;
                float lambda = tipChord / rootChord;
                return (2f / 3f) * rootChord * (1f + lambda + lambda * lambda) / (1f + lambda);
            }
        }

        /// <summary>Chord at a spanwise fraction (0 = root, 1 = tip).</summary>
        public float ChordAt(float t) => Mathf.Lerp(rootChord, tipChord, Mathf.Clamp01(t));

        /// <summary>
        /// Control-surface effectiveness τ: the fraction of a hinge deflection that
        /// acts like a change of the whole section's incidence. Thin-airfoil flap
        /// theory, exact:
        /// <code>
        ///   θ_f = acos(2E − 1),   τ = 1 − (θ_f − sin θ_f)/π
        /// </code>
        /// A quarter-chord flap gives τ = 0.609 — so 15° of aileron is worth 9.1°
        /// of local incidence, and that follows from where the hinge is rather than
        /// from an authored "aileron authority".
        /// </summary>
        public float ControlEffectiveness
        {
            get
            {
                if (control == ControlAxis.None) return 0f;
                float e = Mathf.Clamp(controlChordFrac, 0f, 1f);
                if (e <= 0f) return 0f;
                float theta = Mathf.Acos(Mathf.Clamp(2f * e - 1f, -1f, 1f));
                return 1f - (theta - Mathf.Sin(theta)) / Mathf.PI;
            }
        }

        /// <summary>True when the hinge covers this spanwise fraction.</summary>
        public bool HasControlAt(float t) =>
            control != ControlAxis.None &&
            t >= controlSpanStart && t <= controlSpanEnd;

        /// <summary>Tail volume coefficient against a reference wing — the standard
        /// non-dimensional measure of how much authority a tail has. Horizontal
        /// uses the mean chord, vertical uses the span, which is simply the
        /// convention each one is quoted in.</summary>
        public float VolumeCoefficient(float wingArea, float wingReference, float armMetres)
        {
            if (wingArea <= 1e-6f || wingReference <= 1e-6f) return 0f;
            return Area * armMetres / (wingArea * wingReference);
        }
    }
}
