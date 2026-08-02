using UnityEngine;

namespace AIHWSim.Vehicles.Aero
{
    /// <summary>
    /// A vectored-thrust jet engine, described as hardware: how hard it pushes,
    /// how fast it spools, where its nozzles are and how far they swivel, and
    /// where the reaction-control bleed exits. No aerodynamic coefficient lives
    /// here — thrust is a force with a direction, and the airframe's behaviour
    /// still comes entirely from the panel model.
    ///
    /// <b>The all-zero default is a sentinel, not a configuration</b> — the same
    /// contract <c>SuspensionLinkage</c> holds. Unity deserializes a
    /// <c>[Serializable]</c> class field as a non-null default object, so "is
    /// there a jet?" must never be a null check on a spec that has been through
    /// a scene file. <see cref="AircraftSpec.IsJet"/> asks the honest question:
    /// does this engine have a thrust rating at all.
    ///
    /// <b>Puffers.</b> At the hover there is no airflow over the panels, so the
    /// control surfaces are dead — which is physics, not a bug, and exactly why
    /// the Harrier bleeds compressor air to nozzles at its extremities. The
    /// puffer stations here are actuators in the same sense the elevator is: the
    /// pilot's commands drive them, the moment is force × authored arm, and any
    /// stabilisation wrapped around them lives on the input side (SAS), never in
    /// the vehicle.
    /// </summary>
    [System.Serializable]
    public class JetSpec
    {
        /// <summary>Sea-level static thrust (N). Zero = no jet (the sentinel).</summary>
        public float maxThrustN;

        /// <summary>⚠ ESTIMATE. First-order spool time constant (s). Small
        /// turbofans spool idle→full in roughly a second; 0.35 s puts most of the
        /// response inside that. Retired by a spool-up measurement against any
        /// documented engine of the class.</summary>
        public float spoolTau = 0.35f;

        /// <summary>The two nozzle stations, body-local (m). Thrust is split
        /// 50/50 between them, so placing them symmetric about the CG makes a
        /// balanced hover a property of GEOMETRY — no trim constant — and a CoM
        /// shift shows up honestly as a hover pitch couple.</summary>
        public Vector3 nozzleForeLocal;
        public Vector3 nozzleAftLocal;

        /// <summary>Nozzle travel (deg). 0 = full aft (forward flight), 90 =
        /// straight down (hover). CONVENTION: the Harrier's nozzles reach 98.5°,
        /// a few degrees past vertical, which is what makes decelerating
        /// transitions and backing up possible — so a max past 90 buys the
        /// Hydra's party tricks for free.</summary>
        public float nozzleMinDeg = 0f;
        public float nozzleMaxDeg = 98.5f;

        /// <summary>CONVENTION ~50°/s: the Harrier moves its nozzles through
        /// full travel in about two seconds.</summary>
        public float nozzleRateDegPerS = 50f;

        /// <summary>Reaction-control stations, body-local (m): vertical thrusters
        /// at nose and tail (pitch pair, and the tail one doubles as the yaw
        /// station), vertical thrusters at the wingtips (roll pair).</summary>
        public Vector3 pufferNoseLocal;
        public Vector3 pufferTailLocal;
        public Vector3 pufferTipLeftLocal;
        public Vector3 pufferTipRightLocal;

        /// <summary>CONVENTION ~0.08. Fraction of current engine thrust each
        /// axis may bleed at full deflection. The Harrier's documented figure is
        /// of order a tenth of engine mass flow; the bleed is SUBTRACTED from
        /// the lift thrust, so hovering with full stick genuinely costs height —
        /// as it does in the real aircraft.</summary>
        public float pufferBudgetFrac = 0.08f;
    }
}
