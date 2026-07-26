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
        float MouseSteerDelta();
    }

    /// <summary>
    /// Local device-routed input. Merged delegates verbatim to the static
    /// <see cref="InputReader"/> (byte-identical single-player feel); Keyboard
    /// and Gamepad kinds read one exclusive device via the Input System —
    /// gamepads are resolved from <c>Gamepad.all</c> on every call so hot-plugs
    /// are safe (a missing pad just reads zeros). Under a legacy-only input
    /// build the exclusive kinds degrade to the merged behavior.
    /// </summary>
    public sealed class PlayerInputSource : IDriverInputSource
    {
        private readonly InputDeviceKind _kind;
        private readonly int _gamepadIndex;
        private readonly SteerSmoother _kbSteer = new SteerSmoother(); // Keyboard kind only

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
                {
                    var kb = Keyboard.current;
                    if (kb == null) return 0f;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) return 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) return -1f;
                    return 0f;
                }
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
                {
                    var kb = Keyboard.current;
                    float raw = 0f;
                    if (kb != null)
                    {
                        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) raw = 1f;
                        else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) raw = -1f;
                    }
                    // Digital keys get the transmitter-style ramp (see SteerSmoother).
                    return _kbSteer.Step(raw, Time.time);
                }
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
                {
                    var kb = Keyboard.current;
                    return kb != null && kb.leftCtrlKey.isPressed ? 1f : 0f;
                }
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp != null && gp.buttonEast.isPressed ? 1f : 0f;
                }
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
                {
                    var kb = Keyboard.current;
                    return kb != null && kb.spaceKey.isPressed;
                }
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp != null && gp.buttonSouth.isPressed;
                }
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
                {
                    var kb = Keyboard.current;
                    return kb != null && kb.rKey.wasPressedThisFrame;
                }
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp != null && gp.buttonNorth.wasPressedThisFrame;
                }
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
                {
                    var kb = Keyboard.current;
                    return kb != null && kb.leftShiftKey.wasPressedThisFrame;
                }
                case InputDeviceKind.Gamepad:
                {
                    var gp = Pad;
                    return gp != null && gp.buttonWest.wasPressedThisFrame;
                }
#endif
                default:
                    return InputReader.UseItemPressed();
            }
        }

        public float MouseSteerDelta() =>
            _kind == InputDeviceKind.MergedKeyboardGamepad ? InputReader.MouseSteerDelta() : 0f;
    }
}
