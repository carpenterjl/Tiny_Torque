using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// Human pilot input, on a <b>Mode 2</b> transmitter layout: left stick is
    /// throttle (vertical) and rudder (horizontal), right stick is elevator
    /// (vertical) and aileron (horizontal). That is what the overwhelming majority
    /// of RC pilots fly, and matching the hardware is the point of this project.
    ///
    /// <b>The throttle is ratcheted, and that is a physical fact rather than a
    /// convenience.</b> A real Mode 2 throttle stick has no centring spring: you
    /// set it and it stays. A gamepad stick springs back to middle, so tracking it
    /// directly would mean the engine idles the instant you let go — you could
    /// never fly hands-off, and no amount of skill would fix it. Integrating stick
    /// deflection as a RATE reproduces the ratchet exactly. The three control axes
    /// are self-centring on both the real transmitter and the pad, so those map
    /// straight through.
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
        // Provisional keyboard map. Elevator follows STICK sense — pulling back
        // (Down arrow) raises the nose — because that is what a transmitter does.
        private const KeyCode KeyThrottleUp = KeyCode.W;
        private const KeyCode KeyThrottleDown = KeyCode.S;
        private const KeyCode KeyYawLeft = KeyCode.A;
        private const KeyCode KeyYawRight = KeyCode.D;
        private const KeyCode KeyPitchUp = KeyCode.DownArrow;
        private const KeyCode KeyPitchDown = KeyCode.UpArrow;
        private const KeyCode KeyRollLeft = KeyCode.LeftArrow;
        private const KeyCode KeyRollRight = KeyCode.RightArrow;
        private const KeyCode KeyReset = KeyCode.R;
        private const KeyCode KeyView = KeyCode.V;
        private const KeyCode KeyCut = KeyCode.LeftShift;    // + throttle key: idle / full

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

        public float Roll() => Axis(PadStickX(right: true), _kbRoll);
        public float Pitch()
        {
            float v = Axis(PadStickY(right: true), _kbPitch);
            return invertElevator ? -v : v;
        }
        public float Yaw() => Axis(PadStickX(right: false), _kbYaw);

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
            float stick = PadStickY(right: false);
            float cmd = Mathf.Abs(stick) > 0f ? stick : KeyAxis(KeyThrottleUp, KeyThrottleDown);
            _throttle = Mathf.Clamp01(_throttle + cmd * throttleRate * dt);

            // Snap to idle / full: every transmitter has a thumb that can slam the
            // stick to an end stop, and a go-around needs full power NOW.
            bool cut = KeyTable.Held(KeyCut);
            if ((cut && KeyTable.Held(KeyThrottleDown)) || PadTable.HeldAny(PadButton.LeftShoulder))
                _throttle = 0f;
            if ((cut && KeyTable.Held(KeyThrottleUp)) || PadTable.HeldAny(PadButton.RightShoulder))
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
    }
}
