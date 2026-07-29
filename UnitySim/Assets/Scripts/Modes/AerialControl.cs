using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// The aerial move set, per car: jump, double jump, directional flip, free
    /// air roll while airborne, and the boost tank that feeds all of it.
    ///
    /// A separate component rather than more code inside CarVehicle, so the
    /// vehicle keeps only the four primitives (<c>AerialJump</c>,
    /// <c>AerialFlip</c>, <c>AerialTorque</c>, <c>Grounded</c>) and this owns
    /// the state machine that decides which of them a button press means. It is
    /// only added to cars in a mode that wants aerials — everywhere else the
    /// component does not exist, the primitives are switched off, and the car
    /// drives exactly as it did before any of this was written.
    ///
    /// Runs in FixedUpdate: every one of these is a force or an impulse, and
    /// applying them from Update would make the height of a jump depend on the
    /// frame rate.
    /// </summary>
    [RequireComponent(typeof(CarVehicle))]
    public sealed class AerialControl : MonoBehaviour
    {
        private static readonly Dictionary<CarVehicle, AerialControl> _byCar =
            new Dictionary<CarVehicle, AerialControl>();

        /// <summary>The controller on this car, or null when the mode did not
        /// want one. The lookup is how CarInput forwards a press without every
        /// car in a race paying for a component it will never use.</summary>
        public static AerialControl Of(CarVehicle car)
        {
            if (car == null) return null;
            return _byCar.TryGetValue(car, out var a) ? a : null;
        }

        /// <summary>Fit a car with aerials. Idempotent.</summary>
        public static AerialControl Attach(CarVehicle car, CarInput input)
        {
            if (car == null) return null;
            var a = car.GetComponent<AerialControl>() ?? car.gameObject.AddComponent<AerialControl>();
            a._car = car;
            a._input = input;
            car.arcadeAerial = true;
            _byCar[car] = a;
            return a;
        }

        private CarVehicle _car;
        private CarInput _input;

        private bool _jumpLatch;
        private bool _usedDouble;      // the second jump is spent
        private float _lastJumpAt = -99f;
        private bool _airborne;

        /// <summary>Boost remaining, in seconds of burn.</summary>
        public float Boost { get; private set; } = ModeConfig.BoostTankSec;

        public float Boost01 => Mathf.Clamp01(Boost / ModeConfig.BoostTankSec);

        /// <summary>Queue a jump for the next physics step. Called from the
        /// input layer (an edge) and by the bot policy.</summary>
        public void RequestJump() => _jumpLatch = true;

        /// <summary>Top the tank up — a boost pad, a pickup, or a kick-off.</summary>
        public void AddBoost(float seconds) =>
            Boost = Mathf.Clamp(Boost + seconds, 0f, ModeConfig.BoostTankSec);

        private void Awake()
        {
            _car ??= GetComponent<CarVehicle>();
            _input ??= GetComponent<CarInput>();
            _byCar[_car] = this;
        }

        private void OnDestroy()
        {
            if (_car != null) _byCar.Remove(_car);
            if (_car != null) _car.arcadeAerial = false;
        }

        private void FixedUpdate()
        {
            if (_car == null || _car.Frozen) { _jumpLatch = false; return; }

            bool grounded = _car.Grounded;
            if (grounded && _airborne)
            {
                // Landed: both jumps come back.
                _usedDouble = false;
            }
            _airborne = !grounded;

            if (_jumpLatch)
            {
                _jumpLatch = false;
                DoJump(grounded);
            }

            AirRoll();
            BurnBoost();
        }

        private void DoJump(bool grounded)
        {
            if (grounded)
            {
                _car.AerialJump(ModeConfig.JumpImpulse);
                _lastJumpAt = Time.time;
                _usedDouble = false;
                return;
            }
            if (_usedDouble) return;
            _usedDouble = true;

            // Inside the window, with a direction held, the second press is a
            // FLIP rather than a second lift — the dodge that makes this mode
            // feel like the genre it is imitating.
            Vector2 dir = HeldDirection();
            bool inWindow = Time.time - _lastJumpAt <= ModeConfig.FlipWindowSec;
            if (inWindow && dir.sqrMagnitude > 0.15f)
            {
                Vector3 world = _car.transform.forward * dir.y + _car.transform.right * dir.x;
                _car.AerialFlip(world, ModeConfig.FlipImpulse, ModeConfig.FlipTorque);
            }
            else
            {
                _car.AerialJump(ModeConfig.DoubleJumpImpulse);
            }
        }

        /// <summary>
        /// Steering and throttle become pitch and roll while airborne — there is
        /// nothing else for them to do with the wheels off the ground, and
        /// giving the player a second control scheme to learn would be worse
        /// than reusing the one already under their hands.
        /// </summary>
        private void AirRoll()
        {
            if (_car.Grounded || _input == null) return;
            var src = _input.source;
            if (src == null) return;

            float roll = src.Steer();
            float pitch = src.Throttle();
            if (Mathf.Abs(roll) < 0.05f && Mathf.Abs(pitch) < 0.05f) return;

            // Roll about the car's own nose axis and pitch about its right, so
            // the controls stay meaningful however the car is tumbling.
            Vector3 torque = _car.transform.forward * (-roll * ModeConfig.AirTorque)
                           + _car.transform.right * (-pitch * ModeConfig.AirTorque);
            _car.AerialTorque(torque);
        }

        private void BurnBoost()
        {
            bool want = _input != null && _input.BoostHeldNow;
            if (!want || Boost <= 0f)
            {
                _car.arcadeBoostAccel = 0f;
                return;
            }
            Boost = Mathf.Max(0f, Boost - Time.fixedDeltaTime);
            // Rides the existing arcade boost channel rather than a second
            // acceleration path, so boost pads and the tank cannot stack into
            // something neither was tuned for.
            _car.arcadeBoostAccel = Mathf.Max(_car.arcadeBoostAccel, ModeConfig.BoostAccel);
        }

        private Vector2 HeldDirection()
        {
            var src = _input?.source;
            if (src == null) return Vector2.zero;
            return new Vector2(src.Steer(), src.Throttle());
        }
    }
}
