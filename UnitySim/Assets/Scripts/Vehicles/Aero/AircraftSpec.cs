using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>One lump of mass at a known station — a motor, a battery, a wing bay.</summary>
    [System.Serializable]
    public struct MassComponent
    {
        public string name;
        public Vector3 posLocal;   // body-local (m)
        public float massKg;

        public MassComponent(string name, Vector3 posLocal, float massKg)
        {
            this.name = name;
            this.posLocal = posLocal;
            this.massKg = massKg;
        }
    }

    /// <summary>
    /// A complete aircraft, described as parts rather than as behaviour.
    ///
    /// <b>The centre of gravity is not authored.</b> It is the mass-weighted mean
    /// of <see cref="masses"/>, and the battery is the term you move to place it —
    /// which is exactly how a real model is balanced, with a battery tray and a
    /// finger under each wing. Authoring a CG directly would let it disagree with
    /// the parts list, and then two numbers would describe one aeroplane.
    ///
    /// The inertia tensor comes from the same table by the parallel-axis theorem.
    /// That is why the wing is listed as several lumps along the span instead of
    /// one at the root: roll inertia is dominated by how far the wing mass sits
    /// from the centreline, and a single centred lump would report a wing that
    /// rolls like a broomstick.
    ///
    /// <b>Static margin, and the one number this model is knowingly wrong about.</b>
    /// The CG is placed to give the REAL aeroplane a 15 % static margin. The model
    /// has no fuselage aerodynamics, and a fuselage is destabilising, so the model's
    /// own neutral point sits about 5 % of chord further aft than the real one and
    /// it will therefore MEASURE a static margin nearer 20 %. That gap is the
    /// declared fuselage omission, quantified — the same way the car's roll test
    /// reports an upper bound and names the missing anti-roll bar as the residual.
    /// </summary>
    [System.Serializable]
    public class AircraftSpec
    {
        public string name = "Aircraft";

        public List<MassComponent> masses = new List<MassComponent>();
        public List<LiftingSurface> surfaces = new List<LiftingSurface>();

        public PropellerSpec propeller;
        public MotorParams motor;
        /// <summary>Prop disc centre, body-local. Thrust acts here, so an offset
        /// thrust line produces a pitching moment without anyone asking it to.</summary>
        public Vector3 propPosLocal;

        /// <summary>Vectored-thrust jet, or the all-zero sentinel meaning "this
        /// aircraft has a propeller". See <see cref="IsJet"/> for why the test is
        /// a thrust rating and not a null check.</summary>
        public JetSpec jet;

        /// <summary>Whether this aircraft is jet-propelled. Deliberately NOT
        /// <c>jet != null</c>: Unity deserializes a [Serializable] class field as
        /// a non-null default object, so a trainer that has been through a scene
        /// file comes back with an all-zero <see cref="JetSpec"/> — the sentinel.
        /// A jet is a jet because its engine has a thrust rating.</summary>
        public bool IsJet => jet != null && jet.maxThrustN > 0f;

        /// <summary>
        /// Parasitic drag area (m²) for everything that is NOT a lifting surface:
        /// fuselage, landing gear, interference. The wing's and tail's own profile
        /// drag is applied per strip by <see cref="PanelAero"/>, so including it
        /// here as well would count it twice.
        /// </summary>
        public float parasiticCdA;

        /// <summary>Fuselage box, for the primitive visual and its collider.</summary>
        public Vector3 fuselageSize = new Vector3(0.09f, 0.11f, 1.00f);
        public Vector3 fuselageCentre;

        /// <summary>Landing gear contact points, body-local, and their radius.</summary>
        public List<Vector3> gearLocal = new List<Vector3>();
        public float gearRadius = 0.03f;

        /// <summary>Index into <see cref="surfaces"/> of the main wing — the
        /// reference area every non-dimensional coefficient is quoted against.</summary>
        public int wingIndex;

        // ---- derived ----

        public float TotalMass
        {
            get
            {
                float m = 0f;
                for (int i = 0; i < masses.Count; i++) m += masses[i].massKg;
                return m;
            }
        }

        /// <summary>Centre of gravity, body-local (m).</summary>
        public Vector3 CentreOfMass
        {
            get
            {
                float m = TotalMass;
                if (m <= 1e-6f) return Vector3.zero;
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < masses.Count; i++)
                    sum += masses[i].posLocal * masses[i].massKg;
                return sum / m;
            }
        }

        /// <summary>
        /// Principal moments of inertia about the CG (kg·m²), from the parts list.
        /// Point masses, so this is a LOWER bound — each component's own inertia
        /// about its own centre is not counted. For a model aeroplane, where almost
        /// all of the inertia is separation rather than bulk, the error is small,
        /// and it is stated rather than papered over with a multiplier.
        /// </summary>
        public Vector3 InertiaTensorDiagonal
        {
            get
            {
                Vector3 c = CentreOfMass;
                float ixx = 0f, iyy = 0f, izz = 0f;
                for (int i = 0; i < masses.Count; i++)
                {
                    Vector3 r = masses[i].posLocal - c;
                    float m = masses[i].massKg;
                    ixx += m * (r.y * r.y + r.z * r.z);
                    iyy += m * (r.x * r.x + r.z * r.z);
                    izz += m * (r.x * r.x + r.y * r.y);
                }
                // A degenerate axis would make the solver divide by zero; a model
                // with any span at all never hits this, but a bench spec might.
                const float floor = 1e-4f;
                return new Vector3(Mathf.Max(floor, ixx), Mathf.Max(floor, iyy),
                                   Mathf.Max(floor, izz));
            }
        }

        public LiftingSurface Wing =>
            surfaces.Count > wingIndex ? surfaces[wingIndex] : default;

        public float WingArea => Wing.Area;
        public float WingSpan => Wing.Span;
        public float MeanChord => Wing.MeanAerodynamicChord;

        /// <summary>Wing loading (N/m²) — the single best predictor of how an
        /// aeroplane feels, and the number model flyers quote in oz/ft².</summary>
        public float WingLoading =>
            WingArea > 1e-6f ? TotalMass * 9.80665f / WingArea : 0f;

        /// <summary>
        /// Stall speed (m/s) in 1 g level flight, closed form:
        /// V_s = √(2W / (ρ·S·C_Lmax)). A prediction the stall test measures against
        /// rather than a number the model is told.
        /// </summary>
        public float PredictedStallSpeed
        {
            get
            {
                float s = WingArea;
                float clMax = Wing.airfoil.clMax;
                if (s <= 1e-6f || clMax <= 1e-3f) return 0f;
                return Mathf.Sqrt(2f * TotalMass * 9.80665f
                                  / (AeroDynamics.AirDensity * s * clMax));
            }
        }

        /// <summary>
        /// Best glide ratio, closed form: L/D_max = ½·√(π·AR·e / C_D0), where C_D0
        /// is the whole airframe's zero-lift drag — the parasitic area plus every
        /// surface's profile drag, referenced to wing area.
        /// </summary>
        public float PredictedLiftToDragMax
        {
            get
            {
                float s = WingArea;
                if (s <= 1e-6f) return 0f;
                float cd0 = ZeroLiftDragCoefficient;
                float ar = Wing.AspectRatio;
                float e = Wing.airfoil.oswald <= 0f ? 1f : Wing.airfoil.oswald;
                if (cd0 <= 1e-6f || ar <= 1e-3f) return 0f;
                return 0.5f * Mathf.Sqrt(Mathf.PI * ar * e / cd0);
            }
        }

        /// <summary>Whole-airframe C_D0, referenced to wing area.</summary>
        public float ZeroLiftDragCoefficient
        {
            get
            {
                float s = WingArea;
                if (s <= 1e-6f) return 0f;
                float area = parasiticCdA;
                for (int i = 0; i < surfaces.Count; i++)
                    area += surfaces[i].airfoil.profileCd * surfaces[i].Area;
                return area / s;
            }
        }

        /// <summary>
        /// Phugoid period (s) at a given trim speed: T = π·√2·V/g.
        ///
        /// Lanchester's result, and the most valuable single prediction available
        /// here — it contains no aerodynamic coefficient, no mass, no area and no
        /// inertia. Only the trim speed and gravity. Anything the model gets wrong
        /// about lift direction, energy conservation or where forces are applied
        /// shows up in this number, and nothing else it could be blamed on does.
        /// </summary>
        public static float PhugoidPeriod(float trimSpeed) =>
            Mathf.PI * Mathf.Sqrt(2f) * trimSpeed / 9.80665f;

        /// <summary>Undamped phugoid frequency (rad/s): ω_n = √2·g/V.</summary>
        public static float PhugoidFrequency(float trimSpeed) =>
            trimSpeed > 1e-3f ? Mathf.Sqrt(2f) * 9.80665f / trimSpeed : 0f;

        /// <summary>
        /// <b>Phugoid damping ratio — and the term the textbook form omits.</b>
        ///
        /// The classical result is ζ = 1/(√2·L/D), which follows from
        /// ζ = −X_u/(2ω_n) once you assume thrust does not change with speed. Then
        /// the only speed force left is drag, which at constant angle of attack goes
        /// as V², so ∂D/∂V = 2D/V and everything cancels down to the lift-to-drag
        /// ratio.
        ///
        /// <b>A fixed-pitch propeller on a fixed voltage breaks that assumption
        /// badly.</b> Speed up and the advance ratio rises, C_T falls, and thrust
        /// drops — a restoring force proportional to the speed excursion, which is
        /// exactly what damping is. On this airframe ∂T/∂V is about −0.33 N per m/s
        /// against a drag term of +0.30, so the propeller supplies slightly MORE than
        /// half the total. A model that reproduces this correctly measures roughly
        /// twice the classical ζ, and reads as over-damped only if it is compared
        /// against a formula that does not describe it.
        /// <code>
        ///   X_u   = (∂T/∂V − ∂D/∂V) / m       ∂D/∂V = 2·D/V at constant α
        ///   ω_n   = √2·g/V
        ///   ζ     = −X_u / (2·ω_n)
        /// </code>
        ///
        /// <paramref name="trimDragN"/> is the drag at trim. In level flight that IS
        /// the thrust, so the flight test hands in its own measured mean thrust
        /// rather than an estimate off the drag polar — which keeps the prediction
        /// free of the two ⚠ coefficients (C_D0 and Oswald e) the polar would drag in.
        /// </summary>
        /// <param name="classical">The constant-thrust value, ζ = D/(√2·W), for
        /// comparison. Reporting both is the point: their ratio is how much of this
        /// aeroplane's phugoid damping is propeller rather than airframe.</param>
        public float PhugoidDamping(float trimSpeed, float trimDragN, float volts,
                                    out float classical)
        {
            classical = 0f;
            float wn = PhugoidFrequency(trimSpeed);
            float m = TotalMass;
            if (wn <= 1e-6f || m <= 1e-6f) return 0f;

            float dragSlope = 2f * trimDragN / trimSpeed;
            float thrustSlope = PropellerModel.ThrustSpeedDerivative(propeller, motor,
                                                                     volts, trimSpeed);

            classical = dragSlope / (2f * m * wn);
            return (dragSlope - thrustSlope) / (2f * m * wn);
        }
    }
}
