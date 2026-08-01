using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>One spanwise strip of a lifting surface, resolved once at build time.</summary>
    public struct AeroPanel
    {
        public Vector3 posLocal;      // quarter-chord of this strip, body-local
        public Vector3 chordDir;      // body-local, points forward
        public Vector3 normal;        // body-local, the strip's "up"
        public Vector3 spanDir;       // body-local, along the span
        public float area;            // m²
        public float twistRad;        // incidence + washout at this station
        public float controlTau;      // 0 when no hinge here
        public float controlSign;     // maps a pilot axis to a hinge sign
        public float controlMaxRad;
        public ControlAxis control;
        public int surface;           // index into the surface table
    }

    /// <summary>
    /// <b>Spanwise panel aerodynamics — the whole model.</b>
    ///
    /// Every lifting surface is cut into strips, and each strip is asked one
    /// question every step: what is the air doing HERE? The answer comes from
    /// <c>v_cm + ω × r</c>, so a strip on a rolling wing sees a different vertical
    /// velocity than the one on the other side, and therefore a different angle of
    /// attack, and therefore produces a different force.
    ///
    /// <b>That is the entire design.</b> Roll damping, pitch damping, yaw damping,
    /// weathercock stability, dihedral effect, adverse yaw, tip stall and spin
    /// departure are not modelled — they are consequences of where the strips are
    /// and which way they point. <b>There is no authored damping coefficient in
    /// this file, and there must never be one.</b> The moment a rate-damping term
    /// is added by hand, every test that measures a rate becomes a test of that
    /// number instead of a test of the aircraft.
    ///
    /// <b>Forces are accumulated, then applied once.</b> Summing ΣF and Σ(r × F)
    /// in managed code and issuing a single AddForce/AddTorque is exactly
    /// equivalent to thirty AddForceAtPosition calls in rigid-body mechanics — the
    /// same reason a free body diagram may be summed before it is solved — and it
    /// crosses the native boundary twice per step instead of sixty times.
    ///
    /// <b>Induced drag is a surface property, not a strip property.</b> It comes
    /// from the trailing vortex sheet the WHOLE surface sheds, so
    /// C_Di = C_L²/(π·AR·e) is evaluated once per surface from that surface's total
    /// lift and then distributed across its strips. Computing it per strip from the
    /// surface's aspect ratio would count it once per panel — roughly an
    /// eight-fold over-estimate on the wing, which would show up as a glide ratio
    /// far worse than any real airframe and would be very easy to mistake for the
    /// model simply being draggy.
    ///
    /// Declared omissions, in addition to <see cref="Airfoil"/>'s: the induced
    /// increment is spread by area rather than by local loading (exact only for an
    /// elliptic distribution); there is no downwash from the wing onto the
    /// tailplane beyond what the tail's own aspect ratio implies; the spanwise flow
    /// component is dropped (the independence principle — exact for an infinite
    /// yawed wing, good here); and there is no wake, so an aircraft does not fly
    /// through its own.
    /// </summary>
    public sealed class PanelAero
    {
        /// <summary>Per-surface constants, resolved once so the step loop does no geometry.</summary>
        private struct SurfaceInfo
        {
            public float area;
            public float liftSlope;
            public float aspectRatio;
            public float oswald;
            public Airfoil airfoil;
        }

        /// <summary>What one solve produced — forces for the body, numbers for the HUD.</summary>
        public struct Result
        {
            public Vector3 forceWorld;
            public Vector3 torqueWorld;
            public float lift;            // N, perpendicular to the free-stream
            public float drag;            // N, along it
            public int stalledPanels;
            public int totalPanels;
            /// <summary>Smallest (α_stall − |α|)/α_stall across all panels. Negative
            /// once anything is stalled; this is the honest stall warning, because
            /// it reports the worst strip rather than an average that hides it.</summary>
            public float stallMargin;
        }

        private readonly AeroPanel[] _panels;
        private readonly SurfaceInfo[] _surfaces;

        // Per-step scratch, preallocated: this runs at the physics rate and must
        // not allocate.
        private readonly Vector3[] _liftDir;
        private readonly Vector3[] _dragDir;
        private readonly float[] _liftN;
        private readonly float[] _profileDragN;
        private readonly float[] _surfaceLift;      // N, per surface, from pass 1
        private readonly float[] _surfaceInduced;   // N, per surface, drag from that lift

        public IReadOnlyList<AeroPanel> Panels => _panels;
        public int PanelCount => _panels.Length;

        private PanelAero(AeroPanel[] panels, SurfaceInfo[] surfaces)
        {
            _panels = panels;
            _surfaces = surfaces;
            _liftDir = new Vector3[panels.Length];
            _dragDir = new Vector3[panels.Length];
            _liftN = new float[panels.Length];
            _profileDragN = new float[panels.Length];
            _surfaceLift = new float[surfaces.Length];
            _surfaceInduced = new float[surfaces.Length];
        }

        // ---- build -------------------------------------------------------

        /// <summary>
        /// Cut every surface into strips. Stations are half-cosine spaced, which
        /// clusters them toward the tip: that is where the span loading falls off
        /// fastest and where a uniform spacing converges slowest, so it buys
        /// accuracy for free. Panel edges are placed on the cosine and the strip
        /// area uses the midpoint chord, which is EXACT for a linearly tapered
        /// planform rather than merely close.
        /// </summary>
        public static PanelAero Build(IReadOnlyList<LiftingSurface> surfaces)
        {
            var panels = new List<AeroPanel>(64);
            var info = new SurfaceInfo[surfaces.Count];

            for (int s = 0; s < surfaces.Count; s++)
            {
                LiftingSurface surf = surfaces[s];
                info[s] = new SurfaceInfo
                {
                    area = surf.Area,
                    liftSlope = surf.LiftSlope,
                    aspectRatio = surf.AspectRatio,
                    oswald = surf.airfoil.oswald <= 0f ? 1f : surf.airfoil.oswald,
                    airfoil = surf.airfoil,
                };

                int n = Mathf.Max(1, surf.panelsPerSide);
                if (surf.vertical) AddSide(panels, surf, s, +1, n, verticalFin: true);
                else if (surf.mirrored)
                {
                    AddSide(panels, surf, s, +1, n, verticalFin: false);
                    AddSide(panels, surf, s, -1, n, verticalFin: false);
                }
                else AddSide(panels, surf, s, +1, n, verticalFin: false);
            }

            return new PanelAero(panels.ToArray(), info);
        }

        private static void AddSide(List<AeroPanel> outPanels, LiftingSurface surf,
                                    int surfaceIndex, int side, int n, bool verticalFin)
        {
            Vector3 chordDir = Vector3.forward;
            Vector3 normal, spanDir;

            if (verticalFin)
            {
                // A fin's "lift" is a side force: its normal points right, so a
                // positive local alpha is an airflow arriving from the left. The
                // identical maths then produces weathercock stability with no
                // special case — which is the reason the fin is not its own model.
                normal = Vector3.right;
                spanDir = Vector3.up;
            }
            else
            {
                // Dihedral rotates the strip's up-vector about the chord line, so
                // both tips rise. That tilt is the whole of the dihedral effect:
                // in sideslip the windward strips gain alpha and the leeward ones
                // lose it, and the roll falls out.
                normal = Quaternion.AngleAxis(side * surf.dihedralDeg, chordDir) * Vector3.up;
                spanDir = side * Vector3.Cross(normal, chordDir);
            }

            float tanSweep = Mathf.Tan(surf.sweepDeg * Mathf.Deg2Rad);
            float tau = surf.ControlEffectiveness;
            float controlMaxRad = surf.controlMaxDeg * Mathf.Deg2Rad;

            // Half-cosine edges: y_i = semiSpan·sin(i·(π/2)/n), i = 0..n.
            float prevY = 0f;
            for (int i = 0; i < n; i++)
            {
                float thetaHi = (i + 1) * (Mathf.PI * 0.5f) / n;
                float y = surf.semiSpan * Mathf.Sin(thetaHi);
                float width = y - prevY;
                float mid = 0.5f * (prevY + y);
                prevY = y;
                if (width <= 1e-6f) continue;

                float t = surf.semiSpan > 1e-6f ? mid / surf.semiSpan : 0f;
                float chord = surf.ChordAt(t);

                Vector3 pos = surf.rootQuarterChord
                              + spanDir * mid
                              - chordDir * (mid * tanSweep);

                bool hinged = surf.HasControlAt(t);
                float sign = 0f;
                if (hinged)
                {
                    // A positive hinge deflection is trailing edge toward -normal,
                    // which raises the strip's angle of attack. From there each
                    // axis only needs its sign:
                    //   aileron  — antisymmetric, and a roll-right command must
                    //              REDUCE lift on the right wing
                    //   elevator — nose up needs the tailplane to push down
                    //   rudder   — nose right needs a leftward force on the fin
                    sign = surf.control == ControlAxis.Aileron ? -side : -1f;
                }

                outPanels.Add(new AeroPanel
                {
                    posLocal = pos,
                    chordDir = chordDir,
                    normal = normal,
                    spanDir = spanDir,
                    area = width * chord,
                    twistRad = (surf.incidenceDeg + surf.washoutDeg * t) * Mathf.Deg2Rad,
                    controlTau = hinged ? tau : 0f,
                    controlSign = sign,
                    controlMaxRad = controlMaxRad,
                    control = hinged ? surf.control : ControlAxis.None,
                    surface = surfaceIndex,
                });
            }
        }

        // ---- solve -------------------------------------------------------

        /// <summary>
        /// Aerodynamic force and torque about the centre of mass, for the current
        /// rigidbody state and control positions. Commands are normalised [-1,1]
        /// (throttle is not an aerodynamic input and is handled elsewhere).
        /// </summary>
        public Result Solve(Rigidbody body, Transform tr, in AirData air,
                            float aileron, float elevator, float rudder)
        {
            var result = new Result { totalPanels = _panels.Length, stallMargin = 1f };
            if (_panels.Length == 0) return result;

            // Read the body state ONCE. v_i = v_cm + ω × r_i is bit-for-bit what
            // Rigidbody.GetPointVelocity returns, without the interop crossing.
            Vector3 vCm = body.linearVelocity - AirData.Wind;
            Vector3 omega = body.angularVelocity;
            Vector3 cm = body.worldCenterOfMass;
            Quaternion rot = tr.rotation;
            Quaternion invRot = Quaternion.Inverse(rot);

            for (int i = 0; i < _surfaces.Length; i++)
            {
                _surfaceLift[i] = 0f;
                _surfaceInduced[i] = 0f;
            }

            // ---- pass 1: local flow, alpha, section coefficients ----
            for (int i = 0; i < _panels.Length; i++)
            {
                ref AeroPanel p = ref _panels[i];
                _liftN[i] = 0f;
                _profileDragN[i] = 0f;
                _liftDir[i] = Vector3.zero;
                _dragDir[i] = Vector3.zero;

                Vector3 rWorld = rot * p.posLocal + (tr.position - cm);
                Vector3 vWorld = vCm + Vector3.Cross(omega, rWorld);
                Vector3 v = invRot * vWorld;               // body frame

                // Drop the spanwise component: a strip only responds to the flow
                // in its own chordwise plane (the independence principle).
                Vector3 vc = v - p.spanDir * Vector3.Dot(v, p.spanDir);
                float sp2 = vc.sqrMagnitude;
                if (sp2 < 1e-4f) continue;

                float speed = Mathf.Sqrt(sp2);
                Vector3 flowDir = vc / speed;

                float alpha = Mathf.Atan2(-Vector3.Dot(vc, p.normal), Vector3.Dot(vc, p.chordDir));

                float deflect = 0f;
                switch (p.control)
                {
                    case ControlAxis.Aileron: deflect = aileron; break;
                    case ControlAxis.Elevator: deflect = elevator; break;
                    case ControlAxis.Rudder: deflect = rudder; break;
                }
                float alphaEff = alpha + p.twistRad
                                 + p.controlTau * p.controlSign
                                   * Mathf.Clamp(deflect, -1f, 1f) * p.controlMaxRad;

                SurfaceInfo si = _surfaces[p.surface];
                si.airfoil.Coefficients(alphaEff, si.liftSlope, out float cl, out float cd);

                // Lift is perpendicular to the LOCAL flow, on the normal's side —
                // not along the body up-axis. Getting this wrong is the classic
                // flight-model bug: it silently injects energy in a turn and the
                // phugoid test is what catches it.
                Vector3 ld = p.normal - flowDir * Vector3.Dot(p.normal, flowDir);
                float ldMag = ld.magnitude;
                if (ldMag < 1e-5f) continue;

                float q = 0.5f * AeroDynamics.AirDensity * sp2;
                _liftDir[i] = ld / ldMag;
                _dragDir[i] = -flowDir;
                _liftN[i] = q * cl * p.area;
                _profileDragN[i] = q * cd * p.area;
                _surfaceLift[p.surface] += _liftN[i];

                // Measured from the section's own zero-lift angle — a cambered
                // section stalls at +11.9° and −18.9°, not symmetrically about zero.
                float excess = si.airfoil.StallExcess(alphaEff, si.liftSlope);
                if (excess > 0f) result.stalledPanels++;
                float margin = -excess;
                if (margin < result.stallMargin) result.stallMargin = margin;
            }

            // ---- induced drag: once per surface, from its total lift ----
            // Uses the free-stream q as the reference so a surface's C_L means the
            // same thing the textbook formula assumes.
            float qRef = air.Q;
            for (int s = 0; s < _surfaces.Length; s++)
            {
                SurfaceInfo si = _surfaces[s];
                if (qRef < 1e-4f || si.area <= 1e-6f || si.aspectRatio <= 1e-3f) continue;

                float clSurf = _surfaceLift[s] / (qRef * si.area);
                float cdi = clSurf * clSurf / (Mathf.PI * si.aspectRatio * si.oswald);
                // The induced drag FORCE for this surface; pass 2 shares it across
                // the surface's strips by area.
                _surfaceInduced[s] = qRef * si.area * cdi;
            }

            // ---- pass 2: assemble ----
            Vector3 force = Vector3.zero;
            Vector3 torque = Vector3.zero;
            for (int i = 0; i < _panels.Length; i++)
            {
                if (_liftDir[i] == Vector3.zero) continue;
                ref AeroPanel p = ref _panels[i];

                float areaFrac = _surfaces[p.surface].area > 1e-6f
                    ? p.area / _surfaces[p.surface].area : 0f;
                float dragN = _profileDragN[i] + _surfaceInduced[p.surface] * areaFrac;

                Vector3 fBody = _liftDir[i] * _liftN[i] + _dragDir[i] * dragN;
                Vector3 fWorld = rot * fBody;
                Vector3 rWorld = rot * p.posLocal + (tr.position - cm);

                force += fWorld;
                torque += Vector3.Cross(rWorld, fWorld);
            }

            result.forceWorld = force;
            result.torqueWorld = torque;

            // Report lift/drag against the free-stream, which is what the HUD and
            // the glide-ratio test both mean by those words.
            if (air.Tas > 1e-3f)
            {
                Vector3 fBodyTotal = invRot * force;
                Vector3 flow = air.VelocityBody / air.Tas;
                result.drag = -Vector3.Dot(fBodyTotal, flow);
                result.lift = (fBodyTotal - flow * Vector3.Dot(fBodyTotal, flow)).magnitude
                              * Mathf.Sign(Vector3.Dot(fBodyTotal, Vector3.up));
            }

            return result;
        }
    }
}
