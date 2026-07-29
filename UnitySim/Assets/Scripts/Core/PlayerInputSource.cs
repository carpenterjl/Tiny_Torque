using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AIHWSim.Core
{
    /// <summary>
    /// Which physical device a player slot reads. Merged is the classic
    /// single-player behavior (keyboard + any gamepad, via InputReader);
    /// Keyboard/Gamepad give split-screen players exclusive devices.
    /// </summary>
    public enum InputDeviceKind { MergedKeyboardGamepad = 0, Keyboard = 1, Gamepad = 2 }

    /// <summary>
    /// Per-player driving input. A future networked player supplies a remote
    /// implementation of this seam; local players use
    /// <see cref="PlayerInputSource"/>.
    /// </summary>
    public interface IDriverInputSource
    {
        float Throttle();
        float Steer();
        float Brake();
        bool Handbrake();
        bool RespawnPressed();
        /// <summary>Edge: fire the held arcade power-up. Consumed by the caller,
        /// so each press reaches exactly one item use. Always false outside
        /// arcade sessions — nothing listens.</summary>
        bool UseItemPressed();
        /// <summary>Held: swing the chase camera round to look behind. Purely a
        /// camera concern — it reaches no physics and no controller — which is
        /// why the network and bot implementations simply return false.</summary>
        bool LookBackHeld();
        /// <summary>Held: sound the horn. Purely audio — no physics, no
        /// controller — but unlike look-back it IS synced over LAN (a horn
        /// nobody else hears is pointless), so the network implementations
        /// latch it from the stream rather than returning false.</summary>
        bool HornHeld();
        /// <summary>Edge: jump / double jump / flip. Only the soccer mode reads
        /// it, and only there does CarVehicle act on it.</summary>
        bool JumpPressed();
        /// <summary>Held: burn the boost tank. Soccer only, same as jump.</summary>
        bool BoostHeld();
        float MouseSteerDelta();
    }

    /// <summary>
    /// Local device-routed input. Merged delegates verbatim to the static
    /// <see cref="InputReader"/> (byte-identical single-player feel); Keyboard
    /// and Gamepad kinds read one exclusive device via the Input System —
    /// gamepads are resolved from <c>Gamepad.all</c> on every call so hot-plugs
    /// are safe (a missing pad just reads zeros). Under a legacy-only input
    /// build the exclusive kinds degrade to the merged behavior.
    ///
    /// Since the rebind layer, the exclusive kinds do not name any key or button
    /// themselves: they call <see cref="InputReader"/>'s device-scoped readers,
    /// which resolve the player's bindings once. There is ONE keyboard binding
    /// set, for the obvious reason that there is one physical keyboard — two
    /// keyboard players already collided before this and remain unsupported.
    /// </summary>
    public sealed class PlayerInputSource : IDriverInputSource
    {
        private readonly InputDeviceKind _kind;
        private readonly int _gamepadIndex;
        private readonly SteerSmoother _kbSteer = new SteerSmoother(); // Keyboard kind only
        private readonly ThrottleSmoother _kbThrottle = new ThrottleSmoother();

        public PlayerInputSource(InputDeviceKind kind, int gamepadIndex = 0)
        {
            _kind = kind;
            _gamepadIndex = gamepadIndex;
        }

#if ENABLE_INPUT_SYSTEM
        private Gamepad Pad =>
            _gamepadIndex >= 0 && _gamepadIndex < Gamepad.all.Count ? Gamepad.all[_gamepadIndex] : null;
#endif

        public float Throttle()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    // The shared raw reader is already keyboard-only; this kind
                    // differs from Merged solely in not folding a gamepad in.
                    return _kbThrottle.Step(InputReader.ThrottleDigitalRaw(), Time.time);
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp == null ? 0f
                        : Mathf.Clamp(gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue(), -1f, 1f);
                }
#endif
                default:
                    return InputReader.Throttle();
            }
        }

        public float Steer()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    // Digital keys get the transmitter-style ramp (see SteerSmoother).
                    return _kbSteer.Step(InputReader.SteerDigitalRaw(), Time.time);
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp == null ? 0f : Mathf.Clamp(gp.leftStick.x.ReadValue(), -1f, 1f);
                }
#endif
                default:
                    return InputReader.Steer();
            }
        }

        public float Brake()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.BrakeKeyHeld() ? 1f : 0f;
                case InputDeviceKind.Gamepad:
                    return InputReader.BrakePadHeld(Pad) ? 1f : 0f;
#endif
                default:
                    return InputReader.Brake();
            }
        }

        public bool Handbrake()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.HandbrakeKeyHeld();
                case InputDeviceKind.Gamepad:
                    return InputReader.HandbrakePadHeld(Pad);
#endif
                default:
                    return InputReader.Handbrake();
            }
        }

        public bool RespawnPressed()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.RespawnKeyPressed();
                case InputDeviceKind.Gamepad:
                    return InputReader.RespawnPadPressed(Pad);
#endif
                default:
                    return InputReader.RespawnPressed();
            }
        }

        public bool UseItemPressed()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.UseItemKeyPressed();
                case InputDeviceKind.Gamepad:
                    return InputReader.UseItemPadPressed(Pad);
#endif
                default:
                    return InputReader.UseItemPressed();
            }
        }

        public bool LookBackHeld()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.LookBackKeyHeld();
                case InputDeviceKind.Gamepad:
                    return InputReader.LookBackPadHeld(Pad);
#endif
                default:
                    return InputReader.LookBackHeld();
            }
        }

        public bool HornHeld()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.HornKeyHeld();
                case InputDeviceKind.Gamepad:
                    return InputReader.HornPadHeld(Pad);
#endif
                default:
                    return InputReader.HornHeld();
            }
        }

        public bool JumpPressed()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.JumpKeyPressed();
                case InputDeviceKind.Gamepad:
                    return InputReader.JumpPadPressed(Pad);
#endif
                default:
                    return InputReader.JumpPressed();
            }
        }

        public bool BoostHeld()
        {
            switch (_kind)
            {
#if ENABLE_INPUT_SYSTEM
                case InputDeviceKind.Keyboard:
                    return InputReader.BoostKeyHeld();
                case InputDeviceKind.Gamepad:
                    return InputReader.BoostPadHeld(Pad);
#endif
                default:
                    return InputReader.BoostHeld();
            }
        }

        public float MouseSteerDelta() =>
            _kind == InputDeviceKind.MergedKeyboardGamepad ? InputReader.MouseSteerDelta() : 0f;
    }
}
