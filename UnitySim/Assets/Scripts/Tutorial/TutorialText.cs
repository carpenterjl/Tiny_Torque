using System.Text;
using AIHWSim.Core;
using AIHWSim.UI;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// Expands control placeholders in step text: "Hold {throttle} to pull away"
    /// becomes "Hold [W] to pull away" on a keyboard and "Hold (Right Trigger) to
    /// pull away" on a pad.
    ///
    /// It has to be done at DRAW time, not at author time, for two reasons: the
    /// controls are rebindable, so the right key is whatever the player set; and
    /// which DEVICE to name changes mid-session, the moment they pick up a pad.
    /// The device choice follows <c>CursorAutoHide.PadIsLastInput</c> — the same
    /// signal MenuNav uses to decide whether to draw its focus ring, and the same
    /// one <c>StudioHints</c> uses for exactly this job. Naming both devices at
    /// once is how a hint becomes wallpaper.
    ///
    /// Pad face buttons come out of <c>PadTable</c>, which names them
    /// positionally (South, East) rather than as A/B/X/Y, so the label is right
    /// on a controller of either family.
    ///
    /// An unknown placeholder is left exactly as written. A typo should look like
    /// a typo in the tutorial rather than vanish into an empty gap.
    /// </summary>
    public static class TutorialText
    {
        /// <summary>True when prompts should name gamepad buttons.</summary>
        public static bool PadActive => CursorAutoHide.PadIsLastInput;

        /// <summary>The label for one action on whichever device is in hand.</summary>
        public static string Label(DriveAction a)
        {
            var b = KeyBindings.Current;
            if (PadActive)
            {
                var pad = b.Pad(a);
                // Throttle and steering are analog on a pad and have no binding
                // to look up; name the stick or trigger instead.
                if (pad != PadButton.None) return "(" + PadTable.Label(pad) + ")";
                switch (a)
                {
                    case DriveAction.ThrottleUp: return "(Right Trigger)";
                    case DriveAction.ThrottleDown: return "(Left Trigger)";
                    case DriveAction.SteerLeft: return "(Left Stick ←)";
                    case DriveAction.SteerRight: return "(Left Stick →)";
                    case DriveAction.Pause: return "(Start)";
                }
            }
            return "[" + KeyTable.Label(b.Key(a)) + "]";
        }

        /// <summary>Both steering directions as one label — most sentences want
        /// "steer" rather than one side of it.</summary>
        public static string SteerLabel() =>
            PadActive ? "(Left Stick)" : Label(DriveAction.SteerLeft) + Label(DriveAction.SteerRight);

        /// <summary>
        /// Replace every {token} in a line. Single pass, so a label containing
        /// braces cannot be re-expanded.
        /// </summary>
        public static string Expand(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('{') < 0) return s;

            var sb = new StringBuilder(s.Length + 16);
            int i = 0;
            while (i < s.Length)
            {
                int open = s.IndexOf('{', i);
                if (open < 0) { sb.Append(s, i, s.Length - i); break; }
                int close = s.IndexOf('}', open + 1);
                if (close < 0) { sb.Append(s, i, s.Length - i); break; }

                sb.Append(s, i, open - i);
                string token = s.Substring(open + 1, close - open - 1);
                string label = Resolve(token);
                sb.Append(label ?? s.Substring(open, close - open + 1));
                i = close + 1;
            }
            return sb.ToString();
        }

        /// <summary>Null for an unknown token, which leaves it on screen as
        /// written.</summary>
        private static string Resolve(string token) => token switch
        {
            "throttle" => Label(DriveAction.ThrottleUp),
            "reverse" => Label(DriveAction.ThrottleDown),
            "brake" => Label(DriveAction.Brake),
            "steer" => SteerLabel(),
            "left" => Label(DriveAction.SteerLeft),
            "right" => Label(DriveAction.SteerRight),
            "handbrake" => Label(DriveAction.Handbrake),
            "respawn" => Label(DriveAction.Respawn),
            "item" => Label(DriveAction.UseItem),
            "lookback" => Label(DriveAction.LookBack),
            "mode" => Label(DriveAction.ModeToggle),
            "pause" => Label(DriveAction.Pause),
            "horn" => Label(DriveAction.Horn),
            "jump" => Label(DriveAction.Jump),
            "boost" => Label(DriveAction.Boost),
            _ => null,
        };
    }
}
