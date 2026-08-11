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
        /// <summary>
        /// The player's bindings. Every driving read below goes through this and
        /// through <see cref="KeyTable"/> / <see cref="PadTable"/> — no driving
        /// control names a KeyCode or a pad button directly any more, because a
        /// single missed call site is a control that ignores its rebind and gives
        /// no sign of having done so.
        ///
        /// The DEVELOPER overlays below (graph, metrics, mission, window size)
        /// deliberately stay hardcoded. They are tools rather than controls, and
        /// pinning them keeps the documented hotkeys true whatever a player has
        /// done to their bindings.
        /// </summary>
        private static KeyBindings B => KeyBindings.Current;

        // Digital keyboard throttle is shaped like a transmitter trigger (see
        // ThrottleSmoother); one static instance, for one physical keyboard —
        // exactly the same reasoning as KbSteer below.
        private static readonly ThrottleSmoother KbThrottle = new ThrottleSmoother();

        // Forward/back throttle in [-1, 1] (negative = reverse). Split the same
        // way Steer() is: triggers are analog and stay raw, the keys are ramped.
        public static float Throttle()
        {
            float v = ThrottleAnalog();
            v = MaxMag(v, KbThrottle.Step(ThrottleDigitalRaw(), Time.time));
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>Gamepad triggers only (raw, unshaped) — shaping a real analog
        /// trigger would only throw away the fidelity it already has.</summary>
        public static float ThrottleAnalog()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            var gp = Gamepad.current;
            if (gp != null)
                v = gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue();
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>
        /// Digital throttle keys as a raw ±1 step (pre-smoothing), honouring the
        /// player's bindings.
        ///
        /// The legacy <c>Vertical</c> axis used to be maxed in here and is gone on
        /// purpose: an Input Manager axis has its own hardcoded key list, so it
        /// would have kept driving the car from W and the up arrow no matter what
        /// the player rebound — the exact silent-stale-binding failure this layer
        /// exists to make impossible. <see cref="KeyTable"/>'s legacy path reads
        /// the bound keys directly instead.
        /// </summary>
        public static float ThrottleDigitalRaw()
        {
            float v = 0f;
            if (Held(DriveAction.ThrottleUp)) v = 1f;
            else if (Held(DriveAction.ThrottleDown)) v = -1f;
            return v;
        }

        /// <summary>Either binding for an action — primary or alternate.</summary>
        private static bool Held(DriveAction a) =>
            KeyTable.Held(B.Key(a)) || KeyTable.Held(B.AltKey(a));

        private static bool Pressed(DriveAction a) =>
            KeyTable.Pressed(B.Key(a)) || KeyTable.Pressed(B.AltKey(a));

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

        /// <summary>Digital steering keys as a raw ±1 step (pre-smoothing),
        /// honouring the player's bindings. The legacy <c>Horizontal</c> axis is
        /// gone for the same reason the Vertical one is — see
        /// <see cref="ThrottleDigitalRaw"/>.</summary>
        public static float SteerDigitalRaw()
        {
            float v = 0f;
            if (Held(DriveAction.SteerRight)) v = 1f;
            else if (Held(DriveAction.SteerLeft)) v = -1f;
            return v;
        }

        // ---- keyboard-only reads -------------------------------------------
        // These exist so PlayerInputSource's exclusive-Keyboard kind can call the
        // same code the merged kind uses rather than naming the keys a second
        // time. Two copies of a binding read is exactly how one of them ends up
        // stale.

        public static bool BrakeKeyHeld() => KeyTable.Held(B.Key(DriveAction.Brake));
        public static bool HandbrakeKeyHeld() => KeyTable.Held(B.Key(DriveAction.Handbrake));
        public static bool RespawnKeyPressed() => KeyTable.Pressed(B.Key(DriveAction.Respawn));
        public static bool UseItemKeyPressed() => KeyTable.Pressed(B.Key(DriveAction.UseItem));
        public static bool LookBackKeyHeld() => KeyTable.Held(B.Key(DriveAction.LookBack));
        public static bool HornKeyHeld() => KeyTable.Held(B.Key(DriveAction.Horn));
        public static bool JumpKeyPressed() => KeyTable.Pressed(B.Key(DriveAction.Jump));
        public static bool BoostKeyHeld() => KeyTable.Held(B.Key(DriveAction.Boost));
        public static bool InteractKeyPressed() => KeyTable.Pressed(B.Key(DriveAction.Interact));

#if ENABLE_INPUT_SYSTEM
        // ---- pad-only reads, for one specific pad (split-screen) ----
        public static bool BrakePadHeld(Gamepad gp) => PadTable.Held(gp, B.Pad(DriveAction.Brake));
        public static bool HandbrakePadHeld(Gamepad gp) => PadTable.Held(gp, B.Pad(DriveAction.Handbrake));
        public static bool RespawnPadPressed(Gamepad gp) => PadTable.Pressed(gp, B.Pad(DriveAction.Respawn));
        public static bool UseItemPadPressed(Gamepad gp) => PadTable.Pressed(gp, B.Pad(DriveAction.UseItem));
        public static bool LookBackPadHeld(Gamepad gp) => PadTable.Held(gp, B.Pad(DriveAction.LookBack));
        public static bool HornPadHeld(Gamepad gp) => PadTable.Held(gp, B.Pad(DriveAction.Horn));
        public static bool JumpPadPressed(Gamepad gp) => PadTable.Pressed(gp, B.Pad(DriveAction.Jump));
        public static bool BoostPadHeld(Gamepad gp) => PadTable.Held(gp, B.Pad(DriveAction.Boost));
        public static bool InteractPadPressed(Gamepad gp) => PadTable.Pressed(gp, B.Pad(DriveAction.Interact));
#endif

        // Foot brake in [0, 1].
        public static float Brake() =>
            BrakeKeyHeld() || PadTable.HeldAny(B.Pad(DriveAction.Brake)) ? 1f : 0f;

        public static bool Handbrake() =>
            HandbrakeKeyHeld() || PadTable.HeldAny(B.Pad(DriveAction.Handbrake));

        public static bool RespawnPressed() =>
            RespawnKeyPressed() || PadTable.PressedAny(B.Pad(DriveAction.Respawn));

        /// <summary>Arcade: fire the held power-up.</summary>
        public static bool UseItemPressed() =>
            UseItemKeyPressed() || PadTable.PressedAny(B.Pad(DriveAction.UseItem));

        /// <summary>World props: toggle/operate the prop the car is alongside.
        /// Non-consuming like every edge check here — PropInteraction is what
        /// keeps one press from reaching two props.</summary>
        public static bool InteractPressed() =>
            InteractKeyPressed() || PadTable.PressedAny(B.Pad(DriveAction.Interact));

        /// <summary>Held: look behind. Held rather than toggled so you cannot
        /// leave the camera facing backwards.</summary>
        /// <summary>Hold-to-sound — a horn is a Held, not a Pressed.</summary>
        public static bool HornHeld() =>
            HornKeyHeld() || PadTable.HeldAny(B.Pad(DriveAction.Horn));

        public static bool LookBackHeld() =>
            LookBackKeyHeld() || PadTable.HeldAny(B.Pad(DriveAction.LookBack));

        /// <summary>Edge: jump, double-jump or flip. An edge rather than a hold
        /// because the second press inside the flip window is a different move,
        /// and a held button cannot say "again".</summary>
        public static bool JumpPressed() =>
            JumpKeyPressed() || PadTable.PressedAny(B.Pad(DriveAction.Jump));

        /// <summary>Held: burn the boost tank.</summary>
        public static bool BoostHeld() =>
            BoostKeyHeld() || PadTable.HeldAny(B.Pad(DriveAction.Boost));

        public static bool ModeTogglePressed() =>
            KeyTable.Pressed(B.Key(DriveAction.ModeToggle)) ||
            PadTable.PressedAny(B.Pad(DriveAction.ModeToggle));

        /// <summary>
        /// Pause menu. The bound key, ANY pad's Start (either split-screen player
        /// can pause), and Escape unconditionally.
        ///
        /// Escape is accepted whatever the binding says because pause is the way
        /// back out to the settings that contain the bindings: a player who binds
        /// it to something unreachable would otherwise have locked themselves out
        /// of the only screen that could undo it.
        /// </summary>
        public static bool PausePressed()
        {
            if (KeyTable.Pressed(B.Key(DriveAction.Pause))) return true;
            if (KeyTable.Pressed(KeyCode.Escape)) return true;
#if ENABLE_INPUT_SYSTEM
            for (int i = 0; i < Gamepad.all.Count; i++)
                if (Gamepad.all[i].startButton.wasPressedThisFrame) return true;
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

        // --- Raw pad axis helpers (menu navigation + editor manipulation) ---
        //
        // Merged across every connected pad (max magnitude per axis), the same
        // any-pad philosophy as PadTable.HeldAny: menus and editors should
        // answer to whichever controller the player happens to pick up, unlike
        // driving, which binds a specific pad per player slot. Like PadTable,
        // these need the new Input System; without it they read zero.

        /// <summary>Left stick, any pad, deadzoned per the system settings.</summary>
        public static Vector2 LeftStick()
        {
            Vector2 v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            foreach (var gp in Gamepad.all)
            {
                var s = gp.leftStick.ReadValue();
                if (Mathf.Abs(s.x) > Mathf.Abs(v.x)) v.x = s.x;
                if (Mathf.Abs(s.y) > Mathf.Abs(v.y)) v.y = s.y;
            }
#endif
            return v;
        }

        /// <summary>Right stick, any pad.</summary>
        public static Vector2 RightStick()
        {
            Vector2 v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
            foreach (var gp in Gamepad.all)
            {
                var s = gp.rightStick.ReadValue();
                if (Mathf.Abs(s.x) > Mathf.Abs(v.x)) v.x = s.x;
                if (Mathf.Abs(s.y) > Mathf.Abs(v.y)) v.y = s.y;
            }
#endif
            return v;
        }

        /// <summary>Right trigger minus left trigger in [-1, 1], any pad.</summary>
        public static float TriggerAxis()
        {
            float v = 0f;
#if ENABLE_INPUT_SYSTEM
            foreach (var gp in Gamepad.all)
                v = MaxMag(v, gp.rightTrigger.ReadValue() - gp.leftTrigger.ReadValue());
#endif
            return Mathf.Clamp(v, -1f, 1f);
        }

        /// <summary>True when any gamepad control was actuated this frame —
        /// the "player is on the pad" signal for cursor auto-hide and the menu
        /// focus ring. Buttons via PadTable's capture, sticks/triggers here.</summary>
        public static bool AnyPadActivity()
        {
            if (PadTable.CaptureThisFrame() != PadButton.None) return true;
            var l = LeftStick(); var r = RightStick();
            return l.sqrMagnitude > 0.09f || r.sqrMagnitude > 0.09f || Mathf.Abs(TriggerAxis()) > 0.25f;
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

        /// <summary>Either Shift key held (track builder: scroll resizes the
        /// item being placed instead of rotating it).</summary>
        public static bool ShiftHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            if (SafeKey(KeyCode.LeftShift) || SafeKey(KeyCode.RightShift)) return true;
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
