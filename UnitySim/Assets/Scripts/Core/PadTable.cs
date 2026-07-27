using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace AIHWSim.Core
{
    /// <summary>
    /// The bindable gamepad controls. A curated enum rather than a raw control
    /// path, so a saved binding is a small stable int and the UI has a fixed list
    /// to walk.
    ///
    /// Face buttons are named by POSITION (South/East/West/North), not by letter,
    /// because A/B/X/Y and Cross/Circle/Square/Triangle are the same four
    /// physical buttons and the Input System already reports them positionally.
    /// </summary>
    public enum PadButton
    {
        None = 0,
        South = 1, East = 2, West = 3, North = 4,
        LeftShoulder = 5, RightShoulder = 6,
        LeftTrigger = 7, RightTrigger = 8,
        LeftStickPress = 9, RightStickPress = 10,
        Select = 11, Start = 12,
        DpadUp = 13, DpadDown = 14, DpadLeft = 15, DpadRight = 16,
    }

    /// <summary>
    /// Resolves a <see cref="PadButton"/> against a gamepad, and captures one for
    /// the rebind UI.
    ///
    /// Only the DIGITAL actions are bindable. Throttle and steering stay welded to
    /// the triggers and the left stick: they are analog, and the whole reason the
    /// gamepad path exists is that an axis carries information a button cannot.
    /// Offering to bind steering to a face button would be offering to make the
    /// controller worse.
    /// </summary>
    public static class PadTable
    {
        /// <summary>What the UI offers, in order. None is first so a player can
        /// clear a binding.</summary>
        public static readonly PadButton[] Bindable =
        {
            PadButton.None,
            PadButton.South, PadButton.East, PadButton.West, PadButton.North,
            PadButton.LeftShoulder, PadButton.RightShoulder,
            PadButton.LeftTrigger, PadButton.RightTrigger,
            PadButton.LeftStickPress, PadButton.RightStickPress,
            PadButton.Select, PadButton.Start,
            PadButton.DpadUp, PadButton.DpadDown, PadButton.DpadLeft, PadButton.DpadRight,
        };

#if ENABLE_INPUT_SYSTEM
        private static ButtonControl Control(Gamepad gp, PadButton b)
        {
            if (gp == null) return null;
            switch (b)
            {
                case PadButton.South: return gp.buttonSouth;
                case PadButton.East: return gp.buttonEast;
                case PadButton.West: return gp.buttonWest;
                case PadButton.North: return gp.buttonNorth;
                case PadButton.LeftShoulder: return gp.leftShoulder;
                case PadButton.RightShoulder: return gp.rightShoulder;
                case PadButton.LeftTrigger: return gp.leftTrigger;
                case PadButton.RightTrigger: return gp.rightTrigger;
                case PadButton.LeftStickPress: return gp.leftStickButton;
                case PadButton.RightStickPress: return gp.rightStickButton;
                case PadButton.Select: return gp.selectButton;
                case PadButton.Start: return gp.startButton;
                case PadButton.DpadUp: return gp.dpad.up;
                case PadButton.DpadDown: return gp.dpad.down;
                case PadButton.DpadLeft: return gp.dpad.left;
                case PadButton.DpadRight: return gp.dpad.right;
                default: return null;
            }
        }

        public static bool Held(Gamepad gp, PadButton b)
        {
            var c = Control(gp, b);
            return c != null && c.isPressed;
        }

        public static bool Pressed(Gamepad gp, PadButton b)
        {
            var c = Control(gp, b);
            return c != null && c.wasPressedThisFrame;
        }

        /// <summary>Held on ANY connected pad — for the merged input kind, where a
        /// single player may pick up whichever controller is to hand.</summary>
        public static bool HeldAny(PadButton b)
        {
            for (int i = 0; i < Gamepad.all.Count; i++)
                if (Held(Gamepad.all[i], b)) return true;
            return false;
        }

        public static bool PressedAny(PadButton b)
        {
            for (int i = 0; i < Gamepad.all.Count; i++)
                if (Pressed(Gamepad.all[i], b)) return true;
            return false;
        }

        /// <summary>The one bindable pad control pressed this frame, across every
        /// connected pad — the player rebinding is holding whichever one they
        /// actually use, and asking them which index it is would be absurd.</summary>
        public static PadButton CaptureThisFrame()
        {
            foreach (var b in Bindable)
            {
                if (b == PadButton.None) continue;
                if (PressedAny(b)) return b;
            }
            return PadButton.None;
        }
#else
        // Legacy-only build: there is no gamepad path in this project at all, so
        // every pad binding simply reads as unpressed rather than as an error.
        public static bool HeldAny(PadButton b) => false;
        public static bool PressedAny(PadButton b) => false;
        public static PadButton CaptureThisFrame() => PadButton.None;
#endif

        public static string Label(PadButton b)
        {
            switch (b)
            {
                case PadButton.None: return "—";
                case PadButton.South: return "A / Cross";
                case PadButton.East: return "B / Circle";
                case PadButton.West: return "X / Square";
                case PadButton.North: return "Y / Triangle";
                case PadButton.LeftShoulder: return "LB";
                case PadButton.RightShoulder: return "RB";
                case PadButton.LeftTrigger: return "LT";
                case PadButton.RightTrigger: return "RT";
                case PadButton.LeftStickPress: return "L Stick";
                case PadButton.RightStickPress: return "R Stick";
                case PadButton.Select: return "Select";
                case PadButton.Start: return "Start";
                case PadButton.DpadUp: return "D-Pad Up";
                case PadButton.DpadDown: return "D-Pad Down";
                case PadButton.DpadLeft: return "D-Pad Left";
                case PadButton.DpadRight: return "D-Pad Right";
                default: return b.ToString();
            }
        }
    }
}
