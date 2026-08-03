using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The driving assists' numbers, as an asset you can edit while the game
    /// runs.
    ///
    /// <see cref="AssistTuning"/> keeps every one of these as its shipped
    /// default and reads this object only when one is installed, so:
    ///
    /// <list type="bullet">
    /// <item><b>No asset assigned means the literals, verbatim.</b> Not
    /// "approximately", not "the same numbers copied into a second place" —
    /// the field initialisers below ARE the defaults, and the accessor returns
    /// the default when <c>AssistTuning.Override</c> is null. That is what lets
    /// the physics gates prove this milestone changed nothing.</item>
    /// <item><b>Edits land on the next physics step.</b> Nothing caches these;
    /// <c>CarVehicle</c>'s FixedUpdate reads them through the accessor each
    /// time, so dragging a slider in the Inspector during Play is visible
    /// immediately, on every car in the session.</item>
    /// </list>
    ///
    /// <b>Every car at once, and that is the point.</b> These are the assists
    /// themselves, not one car's settings — a player's own 0-to-1 assist levels
    /// still live on their <c>PlayerSlot</c>. This asset decides what "traction
    /// control at 0.6" DOES.
    ///
    /// <b>Not here, deliberately: the ramp shapes.</b> Each of the four top-end
    /// functions is the identity below its anchor (steer .80 / stability .70 /
    /// traction .90 / abs .90), which is what keeps the Standard preset at the
    /// feel it shipped with. That is a correctness property enforced by the
    /// shape of a <c>Mathf.InverseLerp</c>, not a taste knob, so it stays in
    /// code where it can be read alongside the reasoning.
    /// </summary>
    [CreateAssetMenu(menuName = "Tiny Torque/Assist Tuning", fileName = "AssistTuning")]
    public sealed class AssistTuningOverride : ScriptableObject
    {
        [Header("Steering assist")]
        [Tooltip("Reference speed for the lock limiter (m/s): available lock scales by " +
                 "ref/speed above it, so LOWER means a tighter limit at any given speed.")]
        [Min(0.01f)] public float steerLimitRefSpeed = 4f;

        [Tooltip("Floor on the speed the limiter divides by, so a standstill cannot hand " +
                 "back infinite lock.")]
        [Min(0.001f)] public float steerLimitMinSpeed = 0.5f;

        [Tooltip("Longitudinal speed below which countersteer stays out of it — slip " +
                 "angle means nothing when the car is barely moving.")]
        [Min(0f)] public float counterSteerMinLongSpeed = 1f;

        [Tooltip("Slip angle (rad) to steering correction.")]
        [Min(0f)] public float counterSteerGain = 0.5f;

        [Tooltip("Cap on that correction, so the assist nudges rather than takes the " +
                 "wheel off you.")]
        [Min(0f)] public float counterSteerClamp = 0.35f;

        [Header("Stability (ESC)")]
        [Tooltip("Yaw-rate error to corrective torque.")]
        [Min(0f)] public float stabilityGain = 0.08f;

        [Tooltip("Hard cap on that torque (N·m), and the REAL ceiling on the whole " +
                 "assist: against roughly 2 N·m of tyre resisting moment, 0.30 is barely " +
                 "a nudge. Also the low end of the top-end ramp.")]
        [Min(0f)] public float stabilityTorqueClamp = 0.3f;

        [Header("Traction control")]
        [Tooltip("Slip ratio at which drive starts being cut.")]
        [Min(0f)] public float tractionOnset = 0.25f;

        [Tooltip("Slip range over which the cut reaches full authority.")]
        [Min(0.001f)] public float tractionBand = 0.35f;

        [Header("ABS")]
        [Tooltip("Negative slip ratio at which the brake starts being released.")]
        [Min(0f)] public float absOnset = 0.3f;

        [Tooltip("Slip range over which the release reaches full authority.")]
        [Min(0.001f)] public float absBand = 0.4f;

        [Header("Launch control")]
        [Tooltip("Slip ratio the governor holds the worst powered wheel at. The shipped " +
                 "0.12 is 1.2x TyreModel.KappaPeak — just PAST the force peak, so a " +
                 "governed launch leaves at maximum force and the integrator converges " +
                 "from the spin side rather than hunting across the peak.")]
        [Min(0f)] public float launchSlipTarget = 0.12f;

        [Tooltip("Integrator gain: voltage-scale units per second per unit of excess " +
                 "slip. At a floored-launch excess of ~0.5 this cuts about 2/s.")]
        [Min(0f)] public float launchGain = 4f;

        [Tooltip("The governor never cuts below this — a launch should feel managed, " +
                 "not confiscated.")]
        [Range(0f, 1f)] public float launchFloor = 0.30f;

        [Tooltip("Below this speed (m/s) the governor is armed.")]
        [Min(0f)] public float launchEngageSpeed = 3.0f;

        [Tooltip("Above this speed the scale is handed back at 4x the release rate — the " +
                 "launch is over and the motor belongs to the driver.")]
        [Min(0f)] public float launchReleaseSpeed = 4.0f;

        [Tooltip("Recovery rate (scale units per second) while not cutting.")]
        [Min(0f)] public float launchReleaseRate = 2f;
    }
}
