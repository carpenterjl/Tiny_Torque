using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// The car's whole design, in the Inspector, while it drives.
    ///
    /// Select the car in the hierarchy during Play and every number a
    /// <see cref="VehicleDesign"/> carries is there to drag: spring rates,
    /// damping ratios, travel, per-wheel grip and load sensitivity, motor
    /// windings and gearing, mass and centre of mass, aero parts, batteries.
    /// Nothing here is a new vocabulary — it is the design object itself, drawn
    /// by Unity because every type in it is already <c>[Serializable]</c> for
    /// the save format.
    ///
    /// <b>Two speeds, and the split is not a convenience.</b>
    ///
    /// <list type="bullet">
    /// <item><b>Per-step values land immediately.</b> Brake torque, aero
    /// multiplier, anti-roll, steer rate, the assists and the tyre grip scale
    /// are read fresh by the physics step, so they are copied straight onto the
    /// live <c>CarVehicle</c> and take effect within a tick. Those are also the
    /// ones the pause menu's Tune panel offers, through the same
    /// <c>ITunable</c> list.</item>
    /// <item><b>Build-time values need a new car.</b> A spring rate became a
    /// <c>JointSpring</c> when the wheel was made; mass became rigidbody state;
    /// body size became a collider. Writing them on the live car would move a
    /// number nothing reads again — a slider that appears to do nothing, which
    /// is worse than no slider. So they trigger a debounced rebuild through
    /// <see cref="Core.Boot.CarRebuilder"/>, which keeps the pose and the
    /// momentum.</item>
    /// </list>
    ///
    /// <b>The debounce is 0.15 s, copied from the garage preview</b>, and it is
    /// what makes dragging a slider usable: a drag emits a value per frame, and
    /// rebuilding a car sixty times a second would be a slideshow of exploded
    /// suspension.
    ///
    /// <b>Editor-only, by construction.</b> The change detection is the only
    /// thing that costs anything and it is compiled out of a player build; a
    /// shipped game has no Inspector to drag, and polling a design's JSON every
    /// frame for a UI nobody can open would be pure waste.
    ///
    /// <b>What it refuses.</b> <c>CarRebuilder</c> will not replace a car in a
    /// LAN session, an arcade race, an arena match, a bot rig or under a native
    /// controller — see its note for what would break in each. In those
    /// sessions this component still applies the per-step half; it says so once
    /// rather than failing silently at the moment somebody drags.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiveCarTuner : MonoBehaviour
    {
        [Tooltip("This car's design, live. Per-step values apply on the next physics " +
                 "step; anything consumed when the car was built rebuilds it in place " +
                 "0.15 s after you stop dragging, keeping pose and speed.")]
        public VehicleDesign design;

        [Tooltip("Off freezes this panel: the car keeps whatever it currently has and " +
                 "no edit is applied or rebuilt. Useful while taking a measurement.")]
        public bool applyEdits = true;

        /// <summary>The rig this car belongs to — what a rebuild has to rewire.
        /// Assigned by whoever attached this; without it the component is
        /// inert, because replacing a car whose owner is unknown would leave
        /// every holder of it dangling.</summary>
        [HideInInspector] public Core.PlayerRig rig;

        /// <summary>Optional: the pause menu whose Tune panel points at this
        /// car, so a rebuild can repoint it.</summary>
        [HideInInspector] public Core.PauseMenu pause;

#if UNITY_EDITOR
        private const float DebounceSec = 0.15f;

        private string _lastJson;
        private float _rebuildAt = -1f;
        private bool _refusalLogged;

        private void Start() => _lastJson = design != null ? JsonUtility.ToJson(design) : null;

        private void Update()
        {
            if (!applyEdits || design == null || rig == null) return;

            // Compared as JSON rather than field by field: the design is a tree
            // with arrays of wheels, aero parts and batteries, and a hand-written
            // comparison would be the one place a newly added field is forgotten.
            string now = JsonUtility.ToJson(design);
            if (now != _lastJson)
            {
                _lastJson = now;
                ApplyPerStep();
                _rebuildAt = Time.unscaledTime + DebounceSec;
            }

            if (_rebuildAt >= 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                Rebuild();
            }
        }

        /// <summary>
        /// Copy the values the physics step re-reads onto the live car, so a
        /// drag is visible before the debounce expires — and so the values that
        /// need NO rebuild are already right when one happens.
        ///
        /// Only the scalars that <c>VehicleFactory</c> copies straight across
        /// and <c>CarVehicle</c> then reads every step. Anything that became
        /// collider or rigidbody state is deliberately absent; that is what the
        /// rebuild is for.
        /// </summary>
        private void ApplyPerStep()
        {
            var car = rig.car;
            if (car == null) return;
            car.steerRateDegPerSec = design.steerRate;
            car.servoStallNm = design.servoStallNm;
            car.ackermannPct = design.ackermannPct;
            car.maxBrakeTorque = design.maxBrakeTorque;
            car.handbrakeTorque = design.handbrakeTorque;
            car.brakeProportioning = design.brakeProportioning;
            car.antiRoll = design.antiRoll;
            car.stickyPhantomNm = design.stickyPhantomNm;
            car.dragCdOverride = design.dragCd;
            car.frontalAreaOverride = design.frontalAreaM2;
            car.bodyColor = design.bodyColor;
        }

        private void Rebuild()
        {
            if (!Core.Boot.CarRebuilder.CanRebuild(rig, out string why))
            {
                if (!_refusalLogged)
                {
                    _refusalLogged = true;
                    Debug.LogWarning($"[TUNER] rebuild-on-edit is off in this session: {why}. "
                        + "Per-step values (brakes, aero, anti-roll, steer rate, assists) are "
                        + "still being applied live; suspension, mass, wheels and motors are "
                        + "not, and will not be until the car is next spawned.");
                }
                return;
            }

            var car = Core.Boot.CarRebuilder.RebuildInPlace(rig, design, pause);
            if (car == null) return;

            // The tuner lived on the car that was just destroyed, so it moves to
            // the new one carrying the design that produced it. Attaching before
            // reading _lastJson back would restart the diff against a fresh
            // snapshot and swallow an edit made during the rebuild frame.
            var next = car.gameObject.AddComponent<LiveCarTuner>();
            next.design = design;
            next.applyEdits = applyEdits;
            next.rig = rig;
            next.pause = pause;
            UnityEditor.Selection.activeGameObject = car.gameObject;
        }
#endif
    }
}
