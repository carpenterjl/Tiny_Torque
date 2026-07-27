using System;
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace AIHWSim.Core
{
    /// <summary>
    /// The bindable keyboard, as data.
    ///
    /// Every key in the game was named twice before this existed — once as an
    /// Input System <c>Key</c> and once as a legacy <c>KeyCode</c> — in two files.
    /// A rebind layer cannot live on top of that, so this is the single canonical
    /// list, and both backends resolve through it.
    ///
    /// <see cref="KeyCode"/> is the canonical form because it is what gets saved:
    /// its values are stable across Unity versions, it exists whichever input
    /// backend is compiled in, and <c>ToString()</c> already produces readable
    /// names for the awkward keys ("LeftShift", "PageDown").
    /// </summary>
    public static class KeyTable
    {
        /// <summary>
        /// What a player is allowed to bind, in the order the UI lists it.
        /// Deliberately not "every KeyCode": that enum also contains mouse
        /// buttons, joystick buttons and platform keys, none of which this
        /// resolves, and offering them would produce bindings that silently do
        /// nothing.
        /// </summary>
        public static readonly KeyCode[] Bindable =
        {
            KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F,
            KeyCode.G, KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L,
            KeyCode.M, KeyCode.N, KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R,
            KeyCode.S, KeyCode.T, KeyCode.U, KeyCode.V, KeyCode.W, KeyCode.X,
            KeyCode.Y, KeyCode.Z,

            KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3,
            KeyCode.Alpha4, KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7,
            KeyCode.Alpha8, KeyCode.Alpha9,

            KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,

            KeyCode.Space, KeyCode.Return, KeyCode.Tab, KeyCode.Backspace,
            KeyCode.Escape, KeyCode.CapsLock,
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt,

            KeyCode.Insert, KeyCode.Delete, KeyCode.Home, KeyCode.End,
            KeyCode.PageUp, KeyCode.PageDown,

            KeyCode.Comma, KeyCode.Period, KeyCode.Slash, KeyCode.Semicolon,
            KeyCode.Quote, KeyCode.LeftBracket, KeyCode.RightBracket,
            KeyCode.Backslash, KeyCode.Minus, KeyCode.Equals, KeyCode.BackQuote,

            KeyCode.Keypad0, KeyCode.Keypad1, KeyCode.Keypad2, KeyCode.Keypad3,
            KeyCode.Keypad4, KeyCode.Keypad5, KeyCode.Keypad6, KeyCode.Keypad7,
            KeyCode.Keypad8, KeyCode.Keypad9,
            KeyCode.KeypadPlus, KeyCode.KeypadMinus, KeyCode.KeypadMultiply,
            KeyCode.KeypadDivide, KeyCode.KeypadPeriod, KeyCode.KeypadEnter,

            KeyCode.F1, KeyCode.F2, KeyCode.F3, KeyCode.F4, KeyCode.F5, KeyCode.F6,
            KeyCode.F7, KeyCode.F8, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12,
        };

#if ENABLE_INPUT_SYSTEM
        private static Dictionary<KeyCode, Key> _toKey;
        private static Dictionary<Key, KeyCode> _fromKey;

        /// <summary>
        /// The handful of places where the two enums disagree about a name.
        /// Everything not listed here parses straight across (A..Z, F1..F12,
        /// LeftArrow, PageDown, …), which is why this is a table of exceptions
        /// rather than eighty hand-written cases waiting to be mistyped.
        /// </summary>
        private static string InputSystemName(KeyCode c)
        {
            string n = c.ToString();
            if (n.StartsWith("Alpha", StringComparison.Ordinal)) return "Digit" + n.Substring(5);
            if (n.StartsWith("Keypad", StringComparison.Ordinal)) return "Numpad" + n.Substring(6);
            switch (c)
            {
                case KeyCode.Return: return "Enter";
                case KeyCode.LeftControl: return "LeftCtrl";
                case KeyCode.RightControl: return "RightCtrl";
                case KeyCode.BackQuote: return "Backquote";
                default: return n;
            }
        }

        private static void EnsureMaps()
        {
            if (_toKey != null) return;
            _toKey = new Dictionary<KeyCode, Key>(Bindable.Length);
            _fromKey = new Dictionary<Key, KeyCode>(Bindable.Length);

            string missing = null;
            foreach (var c in Bindable)
            {
                if (Enum.TryParse(InputSystemName(c), out Key k) && k != Key.None)
                {
                    _toKey[c] = k;
                    _fromKey[k] = c;
                }
                else
                {
                    // Not fatal: the legacy backend still resolves it, and this
                    // project builds with Active Input Handling = Both. It is
                    // logged rather than swallowed because on an Input-System-only
                    // build the same gap would be a key that silently does nothing.
                    missing = missing == null ? c.ToString() : missing + ", " + c;
                }
            }
            if (missing != null)
                Debug.LogWarning($"[KeyTable] No Input System Key for: {missing}");
        }

        private static ButtonControl Control(KeyCode c)
        {
            EnsureMaps();
            var kb = Keyboard.current;
            if (kb == null || !_toKey.TryGetValue(c, out var k)) return null;
            return kb[k];
        }
#endif

        /// <summary>Is this key down right now?</summary>
        public static bool Held(KeyCode c)
        {
            if (c == KeyCode.None) return false;
#if ENABLE_INPUT_SYSTEM
            var ctl = Control(c);
            if (ctl != null && ctl.isPressed) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { if (Input.GetKey(c)) return true; } catch { }
#endif
            return false;
        }

        /// <summary>Did this key go down this frame?</summary>
        public static bool Pressed(KeyCode c)
        {
            if (c == KeyCode.None) return false;
#if ENABLE_INPUT_SYSTEM
            var ctl = Control(c);
            if (ctl != null && ctl.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            try { if (Input.GetKeyDown(c)) return true; } catch { }
#endif
            return false;
        }

        /// <summary>
        /// The one bindable key pressed this frame, for the rebind capture flow —
        /// <see cref="KeyCode.None"/> if nothing, or if the player hit something
        /// outside <see cref="Bindable"/>. Returning None for an unbindable key is
        /// what lets the UI say "that key can't be bound" instead of storing a
        /// binding that never fires.
        /// </summary>
        public static KeyCode CaptureThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            EnsureMaps();
            var kb = Keyboard.current;
            if (kb != null)
            {
                foreach (var ctl in kb.allKeys)
                    if (ctl.wasPressedThisFrame && _fromKey.TryGetValue(ctl.keyCode, out var mapped))
                        return mapped;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            foreach (var c in Bindable)
            {
                try { if (Input.GetKeyDown(c)) return c; } catch { break; }
            }
#endif
            return KeyCode.None;
        }

        /// <summary>What to print on the binding row.</summary>
        public static string Label(KeyCode c)
        {
            switch (c)
            {
                case KeyCode.None: return "—";
                case KeyCode.Return: return "Enter";
                case KeyCode.UpArrow: return "Up";
                case KeyCode.DownArrow: return "Down";
                case KeyCode.LeftArrow: return "Left";
                case KeyCode.RightArrow: return "Right";
                case KeyCode.LeftControl: return "L Ctrl";
                case KeyCode.RightControl: return "R Ctrl";
                case KeyCode.LeftShift: return "L Shift";
                case KeyCode.RightShift: return "R Shift";
                case KeyCode.LeftAlt: return "L Alt";
                case KeyCode.RightAlt: return "R Alt";
                case KeyCode.Comma: return ",";
                case KeyCode.Period: return ".";
                case KeyCode.Slash: return "/";
                case KeyCode.Backslash: return "\\";
                case KeyCode.Semicolon: return ";";
                case KeyCode.Quote: return "'";
                case KeyCode.LeftBracket: return "[";
                case KeyCode.RightBracket: return "]";
                case KeyCode.Minus: return "-";
                case KeyCode.Equals: return "=";
                case KeyCode.BackQuote: return "`";
            }
            string n = c.ToString();
            if (n.StartsWith("Alpha", StringComparison.Ordinal)) return n.Substring(5);
            if (n.StartsWith("Keypad", StringComparison.Ordinal)) return "Num " + n.Substring(6);
            return n;
        }
    }
}
