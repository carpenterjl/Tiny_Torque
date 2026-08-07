using AIHWSim.Core;
using AIHWSim.UI;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The prompt strip: what the tool in your hand does, and which button does it.
    ///
    /// <b>It follows the device you are actually using.</b> The pad line is shown
    /// while the gamepad was the last thing touched (<c>UiRuntime.PadIsLastInput</c>,
    /// the same signal <c>MenuNav</c> uses to decide whether to draw its focus
    /// ring), and the keyboard line otherwise. Showing both at once is how a hint
    /// strip becomes wallpaper.
    ///
    /// <b>The pad names come from <c>PadTable</c>.</b> Face buttons are named
    /// positionally there — South, East — because A/B/X/Y and
    /// Cross/Circle/Square/Triangle are the same four physical buttons; the label
    /// this shows is therefore right on a controller of either family, which a
    /// hard-coded "A" would not be.
    ///
    /// The tool keys themselves are constants rather than bindings. The rebindable
    /// set is the DRIVING actions (<c>KeyBindings</c>), and inventing an editor
    /// action for every one of these would be eleven more rows in the options page
    /// for a screen most players will open a handful of times.
    /// </summary>
    public static class StudioHints
    {
        // The editor's own keys, in one place so the hint text and the code that
        // reads them cannot disagree.
        public const UnityEngine.KeyCode Move = UnityEngine.KeyCode.G;
        public const UnityEngine.KeyCode Rotate = UnityEngine.KeyCode.R;
        public const UnityEngine.KeyCode Scale = UnityEngine.KeyCode.T;
        public const UnityEngine.KeyCode ToggleSnap = UnityEngine.KeyCode.X;
        public const UnityEngine.KeyCode ToggleSpace = UnityEngine.KeyCode.Q;
        public const UnityEngine.KeyCode Duplicate = UnityEngine.KeyCode.D;
        public const UnityEngine.KeyCode Mirror = UnityEngine.KeyCode.M;
        public const UnityEngine.KeyCode Delete = UnityEngine.KeyCode.Delete;
        public const UnityEngine.KeyCode Focus = UnityEngine.KeyCode.F;
        public const UnityEngine.KeyCode Cancel = UnityEngine.KeyCode.Escape;

        private static string K(UnityEngine.KeyCode c) => "[" + KeyTable.Label(c) + "]";
        private static string P(PadButton b) => "(" + PadTable.Label(b) + ")";

        /// <summary>True when the prompts should name gamepad buttons.</summary>
        public static bool PadActive => CursorAutoHide.PadIsLastInput;

        /// <summary>The line under the viewport for the tab that is open.</summary>
        public static string For(StudioTab tab, bool hasSelection)
        {
            if (PadActive) return Pad(tab, hasSelection);
            switch (tab)
            {
                case StudioTab.Body:
                    return "Drag a slider to morph  ·  hold LMB on the body to sculpt  ·  " +
                           "RMB orbit  ·  MMB pan  ·  " + K(Focus) + " frame the car";
                case StudioTab.Parts:
                    return hasSelection
                        ? K(Move) + " move  " + K(Rotate) + " turn  " + K(Scale) + " size  ·  " +
                          K(ToggleSnap) + " snap  " + K(ToggleSpace) + " axes  ·  " +
                          K(Duplicate) + " copy  " + K(Mirror) + " mirror  " +
                          K(Delete) + " remove  ·  " + K(Cancel) + " cancel a drag"
                        : "Pick a part from the palette, then click the car to place it  ·  " +
                          "click a placed part to select it";
                case StudioTab.Paint:
                    return "Pick a feature, then a colour and a finish  ·  " +
                           "Hide takes a feature off the car entirely";
                default:
                    return "Test drive takes this car to the track  ·  " +
                           "Pause ▸ Garage brings it back here, edits and all";
            }
        }

        private static string Pad(StudioTab tab, bool hasSelection)
        {
            switch (tab)
            {
                case StudioTab.Body:
                    return P(PadButton.South) + " adjust  ·  " + P(PadButton.East) + " back  ·  " +
                           P(PadButton.LeftShoulder) + P(PadButton.RightShoulder) + " change tab";
                case StudioTab.Parts:
                    return hasSelection
                        ? P(PadButton.South) + " place  ·  " + P(PadButton.West) + " next tool  ·  " +
                          P(PadButton.North) + " snap  ·  " + P(PadButton.East) + " deselect"
                        : P(PadButton.South) + " choose a part  ·  " +
                          P(PadButton.LeftShoulder) + P(PadButton.RightShoulder) + " change tab";
                case StudioTab.Paint:
                    return P(PadButton.South) + " choose  ·  " + P(PadButton.East) + " back";
                default:
                    return P(PadButton.South) + " choose  ·  " + P(PadButton.East) + " back";
            }
        }

        /// <summary>The one-line tooltip for a gizmo mode, for the tool row.</summary>
        public static string ToolTip(GizmoMode mode) => mode switch
        {
            GizmoMode.Move => "Drag an arrow to slide along one axis, or a square to " +
                              "slide in that plane.",
            GizmoMode.Rotate => "Drag a disc to turn about its axis.",
            _ => "Drag a stalked cube to stretch one axis, or the centre cube to " +
                 "resize the whole part.",
        };
    }
}
