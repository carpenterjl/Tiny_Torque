using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Human pilot input, on a <b>Kerbal Space Program</b> layout: <c>WASDQE</c> fly
    /// the aircraft (W/S pitch, A/D yaw, Q/E roll), <c>Shift</c>/<c>Ctrl</c> run the
    /// throttle up and down, and <c>X</c>/<c>Z</c> slam it to idle and full.
    ///
    /// <b>Why this rather than the Mode 2 transmitter map it replaced.</b> Mode 2 is
    /// what RC pilots actually fly and it was the right default while the aeroplane
    /// was a flying test article. It is a poor fit for a keyboard: a transmitter's
    /// value is two proportional sticks, and a key is a switch. KSP's map was
    /// designed for exactly the hardware in front of this scene, and its throttle is
    /// ratcheted for the same reason a transmitter's is. The gamepad keeps
    /// proportional sticks; only the assignment changed.
    ///
    /// <b>The throttle is ratcheted, and that is a physical fact rather than a
    /// convenience.</b> A real throttle stick has no centring spring: you set it and
    /// it stays. A gamepad trigger and a keyboard key both spring back, so tracking
    /// one directly would mean the engine idles the instant you let go — you could
    /// never fly hands-off, and no amount of skill would fix it. Integrating the
    /// input as a RATE reproduces the ratchet exactly. The three control axes are
    /// self-centring on the pad and on a real stick, so those map straight through.
    ///
    /// <b>Nothing here touches <see cref="InputReader"/>'s smoothers.</b> Its
    /// <c>KbThrottle</c> and <c>KbSteer</c> are <c>static readonly</c> singletons
    /// shared by every car in the process; driving them from an aeroplane would
    /// leave a car's throttle ramp part-way up, and that state survives a scene
    /// load. This class owns its own.
    ///
    /// <b>The key table is fixed and provisional, on purpose.</b> These are not
    /// <c>DriveAction</c> entries because <c>KeyBindings.IsConflicted</c> walks that
    /// enum — new members would change the conflict matrix in every player's saved
    /// settings.json — and because the settings panel builds its rows from it, which
    /// would show controls for a vehicle that is meant to be invisible outside debug
    /// scenes. It is the same reason the debug Tiguan is absent from
    /// <c>VehiclePresets.All</c>. When the aircraft stops being debug-only, these
    /// move into <c>DriveAction</c> and <c>KeyBindings.PadActions</c> and this
    /// comment goes away.
    /// </summary>
    public sealed class PilotInputSource : IPilotInputSource
    {
        // Provisional keyboard map, KSP layout. Note the pitch keys are the way
        // round they are because W is FORWARD: forward on a stick is nose down.
        private const KeyCode KeyPitchDown = KeyCode.W;
        private const KeyCode KeyPitchUp = KeyCode.S;
        private const KeyCode KeyYawLeft = KeyCode.A;
        private const KeyCode KeyYawRight = KeyCode.D;
        private const KeyCode KeyRollLeft = KeyCode.Q;
        private const KeyCode KeyRollRight = KeyCode.E;
        private const KeyCode KeyThrottleUp = KeyCode.LeftShift;
        private const KeyCode KeyThrottleDown = KeyCode.LeftControl;
        private const KeyCode KeyThrottleFull = KeyCode.Z;
        private const KeyCode KeyThrottleCut = KeyCode.X;
        private const KeyCode KeyReset = KeyCode.R;
        private const KeyCode KeyView = KeyCode.V;

        /// <summary>Throttle stick travel per second at full deflection. One second
        /// from idle to full is about how fast a thumb moves a real stick.</summary>
        public float throttleRate = 1.0f;

        /// <summary>Deadzone on the pad sticks. Below this a stick that has not
        /// quite centred would creep the throttle, which on a ratchet is a slow
        /// unexplained climb rather than an obvious twitch.</summary>
        public float stickDeadzone = 0.12f;

        public bool invertElevator = false;

        private float _throttle;
        private float _kbRoll, _kbPitch, _kbYaw;

        /// <summary>Keyboard control axes ramp instead of snapping — a real stick
        /// has mass and a thumb has a speed limit, and a step input on a control
        /// surface is not a manoeuvre any pilot can make.</summary>
        private const float KeyAxisRate = 4.0f;

        public float Throttle() => _throttle;

        public float Roll() => Axis(PadStickX(right: false), _kbRoll);

        /// <summary>Positive is nose UP. The left stick is negated because pulling
        /// a stick BACK (−y) raises the nose, on a transmitter and in KSP alike;
        /// <see cref="invertElevator"/> is for pilots who want it the other way.</summary>
        public float Pitch()
        {
            float v = Axis(-PadStickY(right: false), _kbPitch);
            return invertElevator ? -v : v;
        }

        public float Yaw() => Axis(PadStickX(right: true), _kbYaw);

        public bool ResetPressed() =>
            KeyTable.Pressed(KeyReset) || PadTable.PressedAny(PadButton.North);

        public bool ViewTogglePressed() =>
            KeyTable.Pressed(KeyView) || PadTable.PressedAny(PadButton.Select);

        /// <summary>
        /// Advance the held state. Must be called once per frame from
        /// <see cref="PlaneInput"/>'s Update — the throttle ratchet and the keyboard
        /// ramps are integrators, so they need a tick, unlike the car's stateless
        /// axis reads.
        /// </summary>
        public void Tick(float dt)
        {
            // ---- throttle: rate, held ----
            // Triggers are analog, so a light pull trims slowly and a hard one
            // sweeps the range — the same proportional feel the old throttle stick
            // had, and something the keys cannot give.
            float trigger = PadTrigger(right: true) - PadTrigger(right: false);
            float cmd = Mathf.Abs(trigger) > 0f ? trigger : KeyAxis(KeyThrottleUp, KeyThrottleDown);
            _throttle = Mathf.Clamp01(_throttle + cmd * throttleRate * dt);

            // Snap to idle / full. A go-around needs full power NOW, and no ratchet
            // is fast enough. Applied AFTER the integration above, so holding Ctrl
            // and tapping Z gives full power rather than a fight.
            if (KeyTable.Held(KeyThrottleCut) || PadTable.HeldAny(PadButton.LeftShoulder))
                _throttle = 0f;
            if (KeyTable.Held(KeyThrottleFull) || PadTable.HeldAny(PadButton.RightShoulder))
                _throttle = 1f;

            // ---- keyboard control axes: ramp toward the key state ----
            _kbRoll = Ramp(_kbRoll, KeyAxis(KeyRollRight, KeyRollLeft), dt);
            _kbPitch = Ramp(_kbPitch, KeyAxis(KeyPitchUp, KeyPitchDown), dt);
            _kbYaw = Ramp(_kbYaw, KeyAxis(KeyYawRight, KeyYawLeft), dt);
        }

        /// <summary>Reset to a cold cockpit — throttle closed, sticks centred.</summary>
        public void ResetState()
        {
            _throttle = 0f;
            _kbRoll = _kbPitch = _kbYaw = 0f;
        }

        // ---- plumbing ----

        /// <summary>Pad wins when it is being moved; otherwise the keyboard. The
        /// same merged behaviour single-player cars have had all along.</summary>
        private static float Axis(float pad, float keyboard) =>
            Mathf.Abs(pad) > 0f ? pad : keyboard;

        private static float Ramp(float current, float target, float dt) =>
            Mathf.MoveTowards(current, target, KeyAxisRate * dt);

        private static float KeyAxis(KeyCode positive, KeyCode negative)
        {
            float v = 0f;
            if (KeyTable.Held(positive)) v += 1f;
            if (KeyTable.Held(negative)) v -= 1f;
            return v;
        }

        private float Deadzone(float v) =>
            Mathf.Abs(v) < stickDeadzone
                ? 0f
                // Rescale so the axis still reaches 1 at full deflection rather
                // than topping out at 1 − deadzone.
                : Mathf.Sign(v) * (Mathf.Abs(v) - stickDeadzone) / (1f - stickDeadzone);

        private float PadStickX(bool right)
        {
#if ENABLE_INPUT_SYSTEM
            // Resolved per call, never cached: a pad unplugged mid-flight must read
            // zero rather than throw, which is the hot-plug behaviour
            // PlayerInputSource documents for the car.
            var gp = Gamepad.current;
            if (gp != null)
                return Deadzone((right ? gp.rightStick : gp.leftStick).x.ReadValue());
#endif
            return 0f;
        }

        private float PadStickY(bool right)
        {
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null)
                return Deadzone((right ? gp.rightStick : gp.leftStick).y.ReadValue());
#endif
            return 0f;
        }

        /// <summary>Analog trigger pull, [0,1]. Read raw off the device rather than
        /// through <see cref="PadTable"/>, which reports triggers as buttons — a
        /// press threshold would throw away exactly the proportional information
        /// that makes a trigger better than a shoulder button for this.</summary>
        private float PadTrigger(bool right)
        {
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null)
                return Deadzone((right ? gp.rightTrigger : gp.leftTrigger).ReadValue());
#endif
            return 0f;
        }
    }
}
