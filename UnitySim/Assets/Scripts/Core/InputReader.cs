using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AIHWSim.Core
{
    /// <summary>
    /// Backend-agnostic input helper. Reads keyboard, mouse, and gamepad through
    /// the new Input System when it is enabled, and/or the legacy Input Manager,
    /// merging both so keyboard and a controller work at the same time.
    ///
    /// Guarded by ENABLE_INPUT_SYSTEM / ENABLE_LEGACY_INPUT_MANAGER so it
    /// compiles and runs under any "Active Input Handling" setting. If the
    /// project hasn't enabled the Input System yet, gamepad support simply falls
    /// back to the legacy path (keyboard-only on most setups).
    /// </summary>
    public static class InputReader
    {
        // Forward/back throttle in [-1, 1] (negative = reverse).
        public static float Throttle()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null)
                v = gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v = 1f;
                else if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v = -1f;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            v = MaxMag(v, SafeAxis("Vertical"));
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        // Digital keyboard steering is shaped like a transmitter stick (see
        // SteerSmoother); one static instance is correct — one physical keyboard.
        private static readonly SteerSmoother KbSteer = new SteerSmoother();

        // Steering in [-1, 1] (positive = right). Analog sticks are raw;
        // the digital keyboard axis is ramped.
        public static float Steer()
        {
            float v = SteerAnalog();
            v = MaxMag(v, KbSteer.Step(SteerDigitalRaw(), Time.time));
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>Gamepad stick steering only (raw, unshaped).</summary>
        public static float SteerAnalog()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null) v = gp.leftStick.x.ReadValue();
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>Digital steering keys as a raw ±1 step (pre-smoothing).</summary>
        public static float SteerDigitalRaw()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v = 1f;
                else if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v = -1f;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            // Legacy Horizontal already carries the Input Manager's own smoothing.
            v = MaxMag(v, SafeAxis("Horizontal"));
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        // Foot brake in [0, 1].
        public static float Brake()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null && gp.buttonEast.isPressed) v = 1f;
            var kb = Keyboard.current;
            if (kb != null && kb.leftCtrlKey.isPressed) v = 1f;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKey(KeyCode.LeftControl)) v = 1f;
#endif
            return v;
        }

        public static bool Handbrake()
        {
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null && gp.buttonSouth.isPressed) return true;
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.isPressed) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKey(KeyCode.Space)) return true;
#endif
            return false;
        }

        public static bool RespawnPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null && gp.buttonNorth.wasPressedThisFrame) return true;
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKeyDown(KeyCode.R)) return true;
#endif
            return false;
        }

        public static bool ModeTogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null && gp.selectButton.wasPressedThisFrame) return true;
            var kb = Keyboard.current;
            if (kb != null && kb.mKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKeyDown(KeyCode.M)) return true;
#endif
            return false;
        }

        // Pause menu (Escape / any gamepad's Start — either split-screen player can pause).
        public static bool PausePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
            for (int i = 0; i < Gamepad.all.Count; i++)
                if (Gamepad.all[i].startButton.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKeyDown(KeyCode.Escape)) return true;
#endif
            return false;
        }

        // Graph overlay hotkeys (routed here so no code calls legacy Input directly,
        // which keeps the project working under any Active Input Handling setting).
        public static bool GraphTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.gKey,
#endif
            KeyCode.G);

        public static bool MetricsTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.jKey,
#endif
            KeyCode.J);

        public static bool MissionTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.kKey,
#endif
            KeyCode.K);

        public static bool PauseTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.pKey,
#endif
            KeyCode.P);

        public static bool WindowShrinkPressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.leftBracketKey,
#endif
            KeyCode.LeftBracket);

        public static bool WindowGrowPressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.rightBracketKey,
#endif
            KeyCode.RightBracket);

        // Shared edge-detect across both backends.
        private static bool KeyPressed(
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Controls.ButtonControl isKey,
#endif
            KeyCode legacyKey)
        {
#if ENABLE_INPUT_SYSTEM
            if (isKey != null && isKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKeyDown(legacyKey)) return true;
#endif
            return false;
        }

        // Horizontal mouse movement this frame (pixels-ish), for optional mouse steering.
        public static float MouseSteerDelta()
        {
            float d = 0f;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) d = m.delta.x.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            d = MaxMag(d, SafeAxis("Mouse X"));
#endif
            return d;
        }

        // --- Pointer helpers (used by the garage orbit camera + part placement) ---

        /// <summary>Mouse position in screen pixels (origin bottom-left).</summary>
        public static Vector2 PointerPosition()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.position.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.mousePosition; } catch { }
#endif
            return Vector2.zero;
        }

        /// <summary>Frame mouse delta (pixels).</summary>
        public static Vector2 MouseDelta()
        {
            Vector2 d = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) d = m.delta.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (d == Vector2.zero) d = new Vector2(SafeAxis("Mouse X"), SafeAxis("Mouse Y"));
#endif
            return d;
        }

        /// <summary>Scroll wheel delta this frame (positive = up/zoom-in).</summary>
        public static float ScrollDelta()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.scroll.ReadValue().y * 0.01f;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.mouseScrollDelta.y; } catch { }
#endif
            return 0f;
        }

        public static bool RightMouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.rightButton.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.GetMouseButton(1); } catch { }
#endif
            return false;
        }

        public static bool LeftMousePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.leftButton.wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.GetMouseButtonDown(0); } catch { }
#endif
            return false;
        }

        public static bool LeftMouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.leftButton.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.GetMouseButton(0); } catch { }
#endif
            return false;
        }

        public static bool LeftMouseReleased()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.leftButton.wasReleasedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.GetMouseButtonUp(0); } catch { }
#endif
            return false;
        }

        public static bool MiddleMouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) return m.middleButton.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { return Input.GetMouseButton(2); } catch { }
#endif
            return false;
        }

        // --- Garage editor hotkeys ---

        /// <summary>Focus/frame the selected part (F).</summary>
        public static bool FocusPressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.fKey,
#endif
            KeyCode.F);

        /// <summary>Cancel the in-progress drag (Escape).</summary>
        public static bool CancelPressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.escapeKey,
#endif
            KeyCode.Escape);

        /// <summary>Toggle mirror-symmetry placement (X).</summary>
        public static bool MirrorTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.xKey,
#endif
            KeyCode.X);

        /// <summary>Toggle the track builder's top-down map view (T).</summary>
        public static bool TopDownTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.tKey,
#endif
            KeyCode.T);

        /// <summary>Delete the selected item (Delete).</summary>
        public static bool DeletePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.deleteKey,
#endif
            KeyCode.Delete);

        /// <summary>Toggle grid-snap placement in the garage (N).</summary>
        public static bool SnapTogglePressed() => KeyPressed(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.nKey,
#endif
            KeyCode.N);

        /// <summary>Either Ctrl key held (modifier for zoom-while-dragging etc.).</summary>
        public static bool CtrlHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKey(KeyCode.LeftControl) || SafeKey(KeyCode.RightControl)) return true;
#endif
            return false;
        }

        /// <summary>Either Alt key held (garage paint eyedropper).</summary>
        public static bool AltHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKey(KeyCode.LeftAlt) || SafeKey(KeyCode.RightAlt)) return true;
#endif
            return false;
        }

        /// <summary>Ctrl+Z.</summary>
        public static bool UndoPressed() => CtrlChord(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.zKey,
#endif
            KeyCode.Z);

        /// <summary>Ctrl+Y.</summary>
        public static bool RedoPressed() => CtrlChord(
#if ENABLE_INPUT_SYSTEM
            Keyboard.current?.yKey,
#endif
            KeyCode.Y);

        // Ctrl held + key edge this frame, across both backends.
        private static bool CtrlChord(
#if ENABLE_INPUT_SYSTEM
            UnityEngine.InputSystem.Controls.ButtonControl isKey,
#endif
            KeyCode legacyKey)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed)
                && isKey != null && isKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if ((SafeKey(KeyCode.LeftControl) || SafeKey(KeyCode.RightControl)) && SafeKeyDown(legacyKey)) return true;
#endif
            return false;
        }

        private static float MaxMag(float a, float b) => Mathf.Abs(a) >= Mathf.Abs(b) ? a : b;

#if ENABLE_LEGACY_INPUT_MANAGER
        // Wrap legacy calls so a missing axis definition can't spam exceptions.
        private static float SafeAxis(string name)
        {
            try { return Input.GetAxisRaw(name); }
            catch { return 0f; }
        }
        private static bool SafeKey(KeyCode k)
        {
            try { return Input.GetKey(k); }
            catch { return false; }
        }
        private static bool SafeKeyDown(KeyCode k)
        {
            try { return Input.GetKeyDown(k); }
            catch { return false; }
        }
#endif
    }
}
