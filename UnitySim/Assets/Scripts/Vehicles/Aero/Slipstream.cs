using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>The propeller wake at one instant, resolved from thrust alone.</summary>
    public readonly struct SlipstreamState
    {
        /// <summary>False when there is no accelerated wake to speak of — the motor
        /// is off, the prop has stopped, or it is windmilling. See
        /// <see cref="Slipstream.Solve"/> for why the windmilling case is declared
        /// out rather than extrapolated into.</summary>
        public readonly bool Active;

        /// <summary>Disc centre, body-local (m).</summary>
        public readonly Vector3 DiscLocal;
        /// <summary>Disc radius R (m).</summary>
        public readonly float DiscRadius;
        public readonly float DiameterM;
        /// <summary>Free-stream velocity along the shaft (m/s), never negative.</summary>
        public readonly float FreeStreamAxial;
        /// <summary>Induced velocity at the disc, v_i (m/s). Half the far-wake
        /// increment — that factor of two is momentum theory's one real result.</summary>
        public readonly float InducedVelocity;

        public SlipstreamState(bool active, Vector3 discLocal, float discRadius,
                               float diameterM, float freeStreamAxial, float inducedVelocity)
        {
            Active = active;
            DiscLocal = discLocal;
            DiscRadius = discRadius;
            DiameterM = diameterM;
            FreeStreamAxial = freeStreamAxial;
            InducedVelocity = inducedVelocity;
        }

        /// <summary>Fully developed velocity increment, Δv = 2·v_i (m/s).</summary>
        public float FarWakeIncrement => 2f * InducedVelocity;

        /// <summary>Fully contracted tube radius (m). r∞ = R·√((V+v_i)/(V+2v_i)),
        /// which is continuity and nothing else — at the hover it is exactly
        /// R/√2.</summary>
        public float FarWakeRadius
        {
            get
            {
                float den = FreeStreamAxial + 2f * InducedVelocity;
                if (!Active || den <= 1e-6f) return DiscRadius;
                return DiscRadius * Mathf.Sqrt((FreeStreamAxial + InducedVelocity) / den);
            }
        }
    }

    /// <summary>
    /// <b>The propeller slipstream, by momentum theory. No free parameters.</b>
    ///
    /// A propeller makes thrust by throwing air backwards, so behind it there is a
    /// tube of air moving faster than the free stream. Everything inside that tube
    /// — most of the tailplane, a third of the fin, the inboard wing — is flying in
    /// air the propeller has already accelerated.
    ///
    /// <b>Omitting this is not neutral, and the two consequences are the reason it
    /// is here rather than on a wish list.</b>
    /// <list type="number">
    /// <item><b>Without it the aircraft has no elevator or rudder authority at zero
    ///       airspeed at all.</b> Control force goes as q, and q is zero when the
    ///       aeroplane is not moving — so a model standing still with the motor
    ///       screaming could not raise its own tail. It plainly can, and the 19 m/s
    ///       of static slipstream over the tail is the entire reason.</item>
    /// <item>It makes the tail's dynamic-pressure ratio η_h a function of THROTTLE
    ///       rather than a number somebody authored. The neutral point then moves
    ///       aft as power comes on, which is exactly the throttle-dependent pitch
    ///       trim every tractor-prop aeroplane has, and it falls out free.</item>
    /// </list>
    ///
    /// <b>The theory, in full.</b> Treat the disc as an actuator that raises the
    /// static pressure across itself. Conservation of mass, momentum and energy
    /// through the streamtube give
    /// <code>
    ///   T = 2·ρ·A·v_i·(V + v_i)                    A = πD²/4
    ///   ⇒ v_i = ½·(−V + √(V² + 2T/(ρA)))
    ///   Δv_∞ = 2·v_i                               far-wake increment
    ///   r_∞  = R·√((V + v_i)/(V + 2v_i))           continuity ⇒ contraction
    /// </code>
    /// Every one of those is exact given the actuator-disc idealisation. There is
    /// nothing to tune: feed it a thrust and it returns a velocity field.
    ///
    /// <b>The one thing momentum theory does not give is the RATE.</b> It knows the
    /// disc (v_i, radius R) and the far wake (2·v_i, radius r_∞) and says nothing
    /// about what happens between them. So a shape function is needed, and it is
    /// marked ⚠ accordingly — but it is constrained rather than free: whatever
    /// increment is chosen at a station, the radius there follows from continuity
    /// exactly, so the model can never carry more or less mass flow than the disc
    /// passed. The shape used is a smoothstep completing one diameter aft, which is
    /// the usual engineering statement of how quickly a wake develops.
    ///
    /// That estimate barely matters here, and it is worth saying why: on this
    /// airframe the wing sits 2.0 diameters behind the disc and the tail 4.3, so
    /// both are past the transition and both see the far-wake values regardless of
    /// what shape is used between. A canard, or a pusher with a close-coupled tail,
    /// would be a different story and would need this sourced.
    ///
    /// <b>Declared out.</b>
    /// <list type="bullet">
    /// <item><b>Swirl.</b> The wake rotates as well as accelerating, which is one of
    ///       the sources of a single-engine aeroplane's left-yaw tendency. It needs
    ///       a blade-element propeller rather than a C_T(J) curve, the same
    ///       prerequisite P-factor has.</item>
    /// <item><b>The windmilling and stopped cases.</b> With T ≤ 0 momentum theory
    ///       runs into the vortex-ring and windmill-brake states, where the
    ///       streamtube assumption fails outright and the closed form returns
    ///       nonsense (a negative radicand, or an "induced velocity" describing a
    ///       flow that does not exist). Rather than extrapolate into a regime the
    ///       theory disclaims, the wake is simply switched off there. That makes the
    ///       power-off glide slightly optimistic about tail effectiveness, which is
    ///       stated rather than hidden — and it is a small error, because a stopped
    ///       prop's wake deficit is a fraction of a running one's excess.</item>
    /// <item>The fuselage and gear are not given their share of the wake. Their drag
    ///       is one ⚠-estimated C_D·A bucket already, and putting a correction on
    ///       top of an estimate does not make it a measurement.</item>
    /// </list>
    /// </summary>
    public static class Slipstream
    {
        /// <summary>Below this the wake is not worth modelling and momentum theory
        /// is not applicable anyway (N).</summary>
        private const float MinThrust = 1e-3f;

        /// <summary>
        /// Solve the wake for a thrust and an axial inflow. Returns an inactive
        /// state when the propeller is not accelerating the air — see the class
        /// note on why that case is declared out rather than extrapolated.
        /// </summary>
        public static SlipstreamState Solve(in PropellerSpec p, Vector3 discLocal,
                                            float thrustN, float vAxial)
        {
            float d = Mathf.Max(1e-4f, p.diameterM);
            float r = 0.5f * d;

            if (thrustN <= MinThrust) return default;

            // A rearward axial inflow (flying backwards) is outside the theory too;
            // clamping at zero makes the static case the worst it can report.
            float v = Mathf.Max(0f, vAxial);

            float discFactor = 2f * AeroDynamics.AirDensity * Mathf.PI * r * r;   // 2ρA
            float vi = 0.5f * (-v + Mathf.Sqrt(v * v + 4f * thrustN / discFactor));
            if (vi <= 1e-4f) return default;

            return new SlipstreamState(true, discLocal, r, d, v, vi);
        }

        /// <summary>
        /// Velocity increment (m/s) at a station a given distance aft of the disc,
        /// and the tube radius there.
        ///
        /// The increment follows the ⚠ shape function; the radius then follows from
        /// continuity <c>π·r²·(V+Δv) = π·R²·(V+v_i)</c> exactly, so the two can
        /// never disagree about how much air is in the tube.
        /// </summary>
        public static float IncrementAt(in SlipstreamState s, float distanceAft,
                                        out float radius)
        {
            if (!s.Active || distanceAft <= 0f)
            {
                radius = s.DiscRadius;
                return 0f;
            }

            float t = Mathf.Clamp01(distanceAft / Mathf.Max(1e-4f, s.DiameterM));
            float shape = t * t * (3f - 2f * t);          // ⚠ smoothstep over one D
            float dv = s.InducedVelocity * (1f + shape);

            float num = s.FreeStreamAxial + s.InducedVelocity;
            float den = s.FreeStreamAxial + dv;
            radius = den > 1e-6f ? s.DiscRadius * Mathf.Sqrt(num / den) : s.DiscRadius;
            return dv;
        }

        /// <summary>
        /// What fraction of one spanwise strip lies inside the tube.
        ///
        /// <b>Geometry, not a coefficient.</b> The strip is the straight segment
        /// from <c>centre − spanDir·w/2</c> to <c>centre + spanDir·w/2</c>; the tube
        /// is a circle of the given radius about the thrust axis. Intersecting a
        /// segment with a circle is one quadratic, and the answer is exact — so a
        /// tailplane that is 36 % immersed is 36 % immersed because of where it is,
        /// not because anyone decided a tail should be "mostly in the wash".
        ///
        /// The strip is treated as a line at its quarter-chord: chordwise extent is
        /// ignored, which is consistent with the rest of the panel model treating a
        /// strip as a single station.
        /// </summary>
        public static float Coverage(Vector3 discLocal, Vector3 panelLocal,
                                     Vector3 spanDir, float spanWidth, float radius)
        {
            if (radius <= 1e-6f) return 0f;

            // The thrust axis is body +Z, so "distance from the axis" is measured in
            // the x–y plane. PlaneVehicle applies thrust along transform.forward, so
            // this is the same axis and not an independent assumption.
            var a = new Vector2(panelLocal.x - discLocal.x, panelLocal.y - discLocal.y);
            var b = new Vector2(spanDir.x, spanDir.y);

            float h = 0.5f * Mathf.Max(0f, spanWidth);
            float r2 = radius * radius;
            float bb = Vector2.Dot(b, b);

            // A zero-width strip, or one whose span runs along the shaft, degenerates
            // to a point test.
            if (h <= 1e-9f || bb <= 1e-9f)
                return Vector2.Dot(a, a) <= r2 ? 1f : 0f;

            float ab = Vector2.Dot(a, b);
            float c = Vector2.Dot(a, a) - r2;
            float disc = ab * ab - bb * c;
            if (disc <= 0f) return 0f;                     // the line misses the tube

            float root = Mathf.Sqrt(disc);
            float lo = Mathf.Max((-ab - root) / bb, -h);
            float hi = Mathf.Min((-ab + root) / bb, h);
            return hi > lo ? (hi - lo) / (2f * h) : 0f;
        }
    }
}
