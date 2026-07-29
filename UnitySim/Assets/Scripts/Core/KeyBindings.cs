using System;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>One rebindable driving control.</summary>
    public enum DriveAction
    {
        ThrottleUp = 0,
        ThrottleDown = 1,
        SteerLeft = 2,
        SteerRight = 3,
        Brake = 4,
        Handbrake = 5,
        Respawn = 6,
        UseItem = 7,
        LookBack = 8,
        ModeToggle = 9,
        Pause = 10,
        Horn = 11,
        Jump = 12,
        Boost = 13,
    }

    /// <summary>Named starting points. Custom is what any hand-edited binding becomes.</summary>
    public enum KeyLayout { Wasd = 0, Arrows = 1, Custom = 2 }

    /// <summary>
    /// The player's control bindings, saved inside <c>settings.json</c>.
    ///
    /// One int field per action holding a <see cref="KeyCode"/>, rather than a
    /// dictionary: JsonUtility cannot serialize a Dictionary at all, and named
    /// fields give this the same back-compat every other setting in this project
    /// relies on — a settings.json written before this existed simply keeps the
    /// field initializers, which are today's hardcoded bindings.
    ///
    /// The four driving axes carry an ALT binding as well, because W-or-Up and
    /// A-or-Left both worked before this layer existed and quietly taking that
    /// away would read as the rebind feature breaking the controls. The digital
    /// actions have a single binding each, exactly as they did.
    /// </summary>
    [Serializable]
    public sealed class KeyBindings
    {
        public int layout = (int)KeyLayout.Wasd;

        // ---- keyboard ----
        public int throttleUp = (int)KeyCode.W;
        public int throttleUpAlt = (int)KeyCode.UpArrow;
        public int throttleDown = (int)KeyCode.S;
        public int throttleDownAlt = (int)KeyCode.DownArrow;
        public int steerLeft = (int)KeyCode.A;
        public int steerLeftAlt = (int)KeyCode.LeftArrow;
        public int steerRight = (int)KeyCode.D;
        public int steerRightAlt = (int)KeyCode.RightArrow;
        public int brake = (int)KeyCode.LeftControl;
        public int handbrake = (int)KeyCode.Space;
        public int respawn = (int)KeyCode.R;
        public int useItem = (int)KeyCode.LeftShift;
        public int lookBack = (int)KeyCode.C;
        public int modeToggle = (int)KeyCode.M;
        public int pause = (int)KeyCode.Escape;
        public int horn = (int)KeyCode.H;
        // Aerial moves; only the soccer mode enables them. Space and LeftShift
        // would be the natural pair, but handbrake and use-item already hold
        // them and a duplicate default would read as a broken binding table —
        // so these take the two free keys nearest WASD and stay rebindable.
        public int jump = (int)KeyCode.E;
        public int boost = (int)KeyCode.Q;

        // ---- gamepad (digital actions only; see PadTable) ----
        public int padBrake = (int)PadButton.East;
        public int padHandbrake = (int)PadButton.South;
        public int padRespawn = (int)PadButton.North;
        public int padUseItem = (int)PadButton.West;
        public int padLookBack = (int)PadButton.RightStickPress;
        public int padModeToggle = (int)PadButton.Select;
        // L3 — the one face-adjacent button still free (R3 is look-back).
        public int padHorn = (int)PadButton.LeftStickPress;
        // The face buttons are all spoken for, so the aerials take the two free
        // shoulders — which is also where a driving game usually puts them.
        public int padJump = (int)PadButton.LeftShoulder;
        public int padBoost = (int)PadButton.RightShoulder;

        /// <summary>The live bindings. Lives inside <see cref="Persistence.GameSettings"/>
        /// so it is saved, loaded and reset by the machinery that already exists
        /// for every other option.</summary>
        public static KeyBindings Current
        {
            get
            {
                var s = Persistence.SettingsStore.Current;
                return s.keys ??= new KeyBindings();
            }
        }

        // ---- generic access, so the settings UI can loop over the actions
        //      instead of naming each field twice more ----

        public KeyCode Key(DriveAction a)
        {
            switch (a)
            {
                case DriveAction.ThrottleUp: return (KeyCode)throttleUp;
                case DriveAction.ThrottleDown: return (KeyCode)throttleDown;
                case DriveAction.SteerLeft: return (KeyCode)steerLeft;
                case DriveAction.SteerRight: return (KeyCode)steerRight;
                case DriveAction.Brake: return (KeyCode)brake;
                case DriveAction.Handbrake: return (KeyCode)handbrake;
                case DriveAction.Respawn: return (KeyCode)respawn;
                case DriveAction.UseItem: return (KeyCode)useItem;
                case DriveAction.LookBack: return (KeyCode)lookBack;
                case DriveAction.ModeToggle: return (KeyCode)modeToggle;
                case DriveAction.Pause: return (KeyCode)pause;
                case DriveAction.Horn: return (KeyCode)horn;
                case DriveAction.Jump: return (KeyCode)jump;
                case DriveAction.Boost: return (KeyCode)boost;
                default: return KeyCode.None;
            }
        }

        /// <summary>The secondary binding, or None for actions that have none.</summary>
        public KeyCode AltKey(DriveAction a)
        {
            switch (a)
            {
                case DriveAction.ThrottleUp: return (KeyCode)throttleUpAlt;
                case DriveAction.ThrottleDown: return (KeyCode)throttleDownAlt;
                case DriveAction.SteerLeft: return (KeyCode)steerLeftAlt;
                case DriveAction.SteerRight: return (KeyCode)steerRightAlt;
                default: return KeyCode.None;
            }
        }

        public static bool HasAlt(DriveAction a) =>
            a == DriveAction.ThrottleUp || a == DriveAction.ThrottleDown ||
            a == DriveAction.SteerLeft || a == DriveAction.SteerRight;

        /// <summary>Rebind. Any manual change makes the layout Custom — the two
        /// named layouts are shortcuts, not cages.</summary>
        public void SetKey(DriveAction a, KeyCode k, bool alt = false)
        {
            int v = (int)k;
            if (alt)
            {
                switch (a)
                {
                    case DriveAction.ThrottleUp: throttleUpAlt = v; break;
                    case DriveAction.ThrottleDown: throttleDownAlt = v; break;
                    case DriveAction.SteerLeft: steerLeftAlt = v; break;
                    case DriveAction.SteerRight: steerRightAlt = v; break;
                    default: return;      // no alt slot: nothing to write, nothing to dirty
                }
            }
            else
            {
                switch (a)
                {
                    case DriveAction.ThrottleUp: throttleUp = v; break;
                    case DriveAction.ThrottleDown: throttleDown = v; break;
                    case DriveAction.SteerLeft: steerLeft = v; break;
                    case DriveAction.SteerRight: steerRight = v; break;
                    case DriveAction.Brake: brake = v; break;
                    case DriveAction.Handbrake: handbrake = v; break;
                    case DriveAction.Respawn: respawn = v; break;
                    case DriveAction.UseItem: useItem = v; break;
                    case DriveAction.LookBack: lookBack = v; break;
                    case DriveAction.ModeToggle: modeToggle = v; break;
                    case DriveAction.Pause: pause = v; break;
                    case DriveAction.Horn: horn = v; break;
                    case DriveAction.Jump: jump = v; break;
                    case DriveAction.Boost: boost = v; break;
                    default: return;
                }
            }
            layout = (int)KeyLayout.Custom;
        }

        public PadButton Pad(DriveAction a)
        {
            switch (a)
            {
                case DriveAction.Brake: return (PadButton)padBrake;
                case DriveAction.Handbrake: return (PadButton)padHandbrake;
                case DriveAction.Respawn: return (PadButton)padRespawn;
                case DriveAction.UseItem: return (PadButton)padUseItem;
                case DriveAction.LookBack: return (PadButton)padLookBack;
                case DriveAction.ModeToggle: return (PadButton)padModeToggle;
                case DriveAction.Horn: return (PadButton)padHorn;
                case DriveAction.Jump: return (PadButton)padJump;
                case DriveAction.Boost: return (PadButton)padBoost;
                default: return PadButton.None;
            }
        }

        /// <summary>Which actions the gamepad section offers. Throttle and steering
        /// are absent on purpose — they are analog on a pad.</summary>
        public static readonly DriveAction[] PadActions =
        {
            DriveAction.Brake, DriveAction.Handbrake, DriveAction.Respawn,
            DriveAction.UseItem, DriveAction.LookBack, DriveAction.ModeToggle,
            DriveAction.Horn, DriveAction.Jump, DriveAction.Boost,
        };

        public void SetPad(DriveAction a, PadButton b)
        {
            int v = (int)b;
            switch (a)
            {
                case DriveAction.Brake: padBrake = v; break;
                case DriveAction.Handbrake: padHandbrake = v; break;
                case DriveAction.Respawn: padRespawn = v; break;
                case DriveAction.UseItem: padUseItem = v; break;
                case DriveAction.LookBack: padLookBack = v; break;
                case DriveAction.ModeToggle: padModeToggle = v; break;
                case DriveAction.Horn: padHorn = v; break;
                case DriveAction.Jump: padJump = v; break;
                case DriveAction.Boost: padBoost = v; break;
                default: return;
            }
            layout = (int)KeyLayout.Custom;
        }

        public static string Label(DriveAction a)
        {
            switch (a)
            {
                case DriveAction.ThrottleUp: return "Accelerate";
                case DriveAction.ThrottleDown: return "Reverse / brake";
                case DriveAction.SteerLeft: return "Steer left";
                case DriveAction.SteerRight: return "Steer right";
                case DriveAction.Brake: return "Foot brake";
                case DriveAction.Handbrake: return "Handbrake";
                case DriveAction.Respawn: return "Respawn";
                case DriveAction.UseItem: return "Use item";
                case DriveAction.LookBack: return "Look back";
                case DriveAction.ModeToggle: return "Manual / auto";
                case DriveAction.Pause: return "Pause";
                case DriveAction.Horn: return "Horn";
                default: return a.ToString();
            }
        }

        /// <summary>
        /// Overwrite every binding with a named layout.
        ///
        /// The two layouts are genuinely different hand positions, not the same
        /// keys relabelled: WASD is the left-hand default (and keeps the arrows as
        /// its alternates, which is exactly what shipped before rebinding
        /// existed), while Arrows moves the whole control set to the right of the
        /// keyboard and drops the alternates so the letter keys are free.
        /// </summary>
        public void ApplyLayout(KeyLayout l)
        {
            if (l == KeyLayout.Arrows)
            {
                throttleUp = (int)KeyCode.UpArrow; throttleUpAlt = (int)KeyCode.None;
                throttleDown = (int)KeyCode.DownArrow; throttleDownAlt = (int)KeyCode.None;
                steerLeft = (int)KeyCode.LeftArrow; steerLeftAlt = (int)KeyCode.None;
                steerRight = (int)KeyCode.RightArrow; steerRightAlt = (int)KeyCode.None;
                brake = (int)KeyCode.RightControl;
                handbrake = (int)KeyCode.RightShift;
                respawn = (int)KeyCode.Delete;
                useItem = (int)KeyCode.Keypad0;
                lookBack = (int)KeyCode.Keypad1;
                modeToggle = (int)KeyCode.End;
                horn = (int)KeyCode.Keypad2;
            }
            else
            {
                l = KeyLayout.Wasd;
                throttleUp = (int)KeyCode.W; throttleUpAlt = (int)KeyCode.UpArrow;
                throttleDown = (int)KeyCode.S; throttleDownAlt = (int)KeyCode.DownArrow;
                steerLeft = (int)KeyCode.A; steerLeftAlt = (int)KeyCode.LeftArrow;
                steerRight = (int)KeyCode.D; steerRightAlt = (int)KeyCode.RightArrow;
                brake = (int)KeyCode.LeftControl;
                handbrake = (int)KeyCode.Space;
                respawn = (int)KeyCode.R;
                useItem = (int)KeyCode.LeftShift;
                lookBack = (int)KeyCode.C;
                modeToggle = (int)KeyCode.M;
                horn = (int)KeyCode.H;
            }
            // Pause is not part of a layout: Escape is the pause key on every
            // layout, and PausePressed accepts it unconditionally anyway.
            pause = (int)KeyCode.Escape;

            padBrake = (int)PadButton.East;
            padHandbrake = (int)PadButton.South;
            padRespawn = (int)PadButton.North;
            padUseItem = (int)PadButton.West;
            padLookBack = (int)PadButton.RightStickPress;
            padModeToggle = (int)PadButton.Select;
            padHorn = (int)PadButton.LeftStickPress;

            layout = (int)l;
        }

        /// <summary>
        /// Actions sharing a key with this one — reported to the player rather
        /// than silently prevented. A deliberate double-bind (brake and handbrake
        /// on one key) is a legitimate thing to want; a typo is not, and the only
        /// difference between them is whether the player meant it.
        /// </summary>
        public bool IsConflicted(DriveAction a, bool alt = false)
        {
            KeyCode mine = alt ? AltKey(a) : Key(a);
            if (mine == KeyCode.None) return false;
            foreach (DriveAction other in Enum.GetValues(typeof(DriveAction)))
            {
                if (other != a && Key(other) == mine) return true;
                if (HasAlt(other) && !(other == a && alt) && AltKey(other) == mine) return true;
            }
            // The primary and the alt of the SAME action clashing is a conflict
            // too — it looks like two bindings and behaves like one.
            if (HasAlt(a) && Key(a) == AltKey(a)) return true;
            return false;
        }
    }
}
