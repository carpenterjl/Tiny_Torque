using System.Collections.Generic;
using UnityEngine;
using AIHWSim.Core;
using AIHWSim.Garage;

namespace AIHWSim.UI
{
    /// <summary>
    /// Gamepad navigation for the IMGUI menus — the focus concept IMGUI never
    /// had. Menus draw their controls through the wrappers here (Button,
    /// Toggle, Slider01, Cycle); the wrappers behave exactly like the raw
    /// GUILayout calls for the mouse, and additionally track a focus index
    /// that the d-pad / left stick moves, South activates and East backs out
    /// of. The gold focus ring only appears while the pad is the current
    /// input device (<see cref="CursorAutoHide.PadIsLastInput"/>), so mouse
    /// users never see it.
    ///
    /// IMGUI ground rules this is built around (see also SettingsPanel's
    /// capture): every OnGUI event arrives paired with its own fresh Layout
    /// pass, and the control count must match within each Layout/event pair.
    /// So all nav state that could alter WHICH controls exist is mutated only
    /// at the top of a Layout pass (BeginFrame), and pad activation is
    /// delivered during a Layout pass — which means a host whose page/tab
    /// dispatch can change from a handler must draw from a value snapshotted
    /// on Layout, not from the live field:
    ///
    ///     if (Event.current.type == EventType.Layout) _pageDraw = _page;
    ///     switch (_pageDraw) { ... }
    ///
    /// With that pattern a mid-Layout page switch simply draws the old page
    /// for the rest of the frame (Layout and Repaint agree with each other)
    /// and the new page from the next frame — the same timing a mouse click
    /// already has. Do NOT call GUIUtility.ExitGUI for this; its abort
    /// semantics for the paired Repaint are not something to gamble the whole
    /// menu system on.
    ///
    /// One host owns the nav per frame (first BeginFrame claims it); wrappers
    /// drawn by anyone else fall back to plain mouse behaviour. Hosts consume
    /// pad-Back from their Update() via <see cref="ConsumeBack"/> — state
    /// changes in Update are always pass-safe.
    /// </summary>
    public static class MenuNav
    {
        // --- input latches, refreshed once per frame ---
        private static int _pollFrame = -1;
        private static int _moveY;          // -1 up, +1 down (GUI order: down = next control)
        private static int _adjustX;        // -1 left, +1 right, for focused sliders/cycles
        private static bool _submit;
        private static bool _back;

        // hold-to-repeat
        private const float RepeatDelay = 0.4f;
        private const float RepeatRate = 0.12f;
        private static int _heldY, _heldX;
        private static float _repeatAtY, _repeatAtX;

        // --- focus state ---
        private static string _page = "";
        private static int _focus;
        private static int _claimFrame = -1;
        private static string _claimPage = "";
        // True while the OWNING host's OnGUI is between BeginFrame and
        // EndFrame — a non-owning host's wrappers must not touch the shared
        // census. Re-established by every BeginFrame, so interleaving between
        // scripts can't leave it stale.
        private static bool _drawingOwner;

        // Controls registered this pass / last pass (for clamping + skipping
        // disabled entries when the focus moves).
        private static int _count;
        private static int _lastCount;
        private static readonly List<bool> _disabled = new List<bool>(64);
        private static readonly List<bool> _disabledPrev = new List<bool>(64);
        private static int _moveAppliedFrame = -1;

        /// <summary>
        /// Start a navigable region. Call at the top of the host's OnGUI,
        /// before any wrappers. The first host to call this in a frame owns
        /// the pad for that frame.
        /// </summary>
        public static void BeginFrame(string pageId)
        {
            Poll();

            if (_claimFrame != Time.frameCount)
            {
                _claimFrame = Time.frameCount;
                _claimPage = pageId;
            }
            _drawingOwner = _claimPage == pageId;
            if (!_drawingOwner) return;   // wrappers fall back to plain mouse behaviour

            if (_page != pageId)
            {
                _page = pageId;
                _focus = 0;
                _moveY = 0; _adjustX = 0; _submit = false;
            }

            // Focus movement: once per frame, on a Layout pass, before any
            // control has registered — the whole frame then agrees on who is
            // focused. Uses the previous pass's control census.
            if (Event.current.type == EventType.Layout && _moveAppliedFrame != Time.frameCount)
            {
                _moveAppliedFrame = Time.frameCount;
                _disabledPrev.Clear();
                _disabledPrev.AddRange(_disabled);
                if (_moveY != 0 && _lastCount > 0)
                {
                    int start = Mathf.Clamp(_focus, 0, _lastCount - 1);
                    int idx = start;
                    for (int step = 0; step < _lastCount; step++)
                    {
                        idx = (idx + _moveY + _lastCount) % _lastCount;
                        bool dis = idx < _disabledPrev.Count && _disabledPrev[idx];
                        if (!dis) break;
                    }
                    if (idx != start)
                    {
                        _focus = idx;
                        Ui(Audio.ProceduralAudio.UiMove, 0.5f);
                    }
                    _moveY = 0;
                }
                _focus = Mathf.Clamp(_focus, 0, Mathf.Max(0, _lastCount - 1));
            }

            if (Event.current.type == EventType.Layout) _disabled.Clear();
            _count = 0;
        }

        /// <summary>End the navigable region (bottom of the host's OnGUI).</summary>
        public static void EndFrame()
        {
            if (_drawingOwner && Event.current.type == EventType.Layout)
                _lastCount = _count;
        }

        /// <summary>
        /// Pad "back" (East), consumed from the host's Update() — never from
        /// OnGUI. Returns true once per press. Dropped while a rebind capture
        /// owns input.
        /// </summary>
        public static bool ConsumeBack()
        {
            Poll();
            if (!_back) return false;
            _back = false;
            Ui(Audio.ProceduralAudio.UiBack, 0.7f);
            return true;
        }

        /// <summary>Forget focus/page state (e.g. when a menu closes).</summary>
        public static void ResetFocus()
        {
            _focus = 0;
            _page = "";
        }

        // ------------------------------------------------------------------
        // Wrappers
        // ------------------------------------------------------------------

        public static bool Button(string label, params GUILayoutOption[] options)
        {
            int idx = Register();
            bool clicked = GUILayout.Button(label, options);
            DrawRing(idx);
            if (Activated(idx)) clicked = true;
            if (clicked) Ui(Audio.ProceduralAudio.UiSelect, 0.8f);
            return clicked;
        }

        /// <summary>The main-menu button shape (fixed height), replacing
        /// MenuUI.MenuButton.</summary>
        public static bool MenuButton(string label)
            => Button(label, GUILayout.Height(34));

        public static bool Toggle(bool value, string label, params GUILayoutOption[] options)
        {
            int idx = Register();
            bool v = GUILayout.Toggle(value, label, options);
            DrawRing(idx);
            if (Activated(idx)) v = !v;
            if (v != value) Ui(Audio.ProceduralAudio.UiSelect, 0.5f);
            return v;
        }

        /// <summary>GarageSkin.Slider01 with pad focus: left/right nudges the
        /// focused slider by 0.05. Returns true when the value moved.</summary>
        public static bool Slider01(string label, ref float value)
        {
            int idx = Register();
            bool changed = GarageSkin.Slider01(label, ref value);
            DrawRing(idx);
            int adj = Adjusted(idx);
            if (adj != 0)
            {
                value = Mathf.Clamp01(value + 0.05f * adj);
                changed = true;
                Ui(Audio.ProceduralAudio.UiMove, 0.35f);
            }
            return changed;
        }

        /// <summary>
        /// A "◀ value ▶" row (wrapping). Mouse clicks the arrow buttons; the
        /// pad focuses the row and left/right cycles. Returns the (possibly
        /// wrapped) new index.
        /// </summary>
        public static int Cycle(string label, int index, int count, System.Func<int, string> display,
                                float labelWidth = 90f)
        {
            int idx = Register();
            int result = index;
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, GUILayout.Width(labelWidth));
            if (GUILayout.Button("◀", GUILayout.Width(30f)) && count > 0)
                result = (index - 1 + count) % count;
            GUILayout.Label(count > 0 ? display(Mathf.Clamp(index, 0, count - 1)) : "—",
                GarageSkin.Header, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("▶", GUILayout.Width(30f)) && count > 0)
                result = (index + 1) % count;
            GUILayout.EndHorizontal();
            DrawRing(idx);
            int adj = Adjusted(idx);
            if (adj != 0 && count > 0) result = (index + adj + count) % count;
            if (result != index) Ui(Audio.ProceduralAudio.UiSelect, 0.5f);
            return result;
        }

        /// <summary>
        /// A "− value +" row (clamped, non-wrapping) — the numeric steppers
        /// (opponents, laps, countdown). Same nav semantics as Cycle.
        /// </summary>
        public static int Stepper(string label, int value, int min, int max,
                                  System.Func<int, string> display, float labelWidth = 70f)
        {
            int idx = Register();
            int result = value;
            GUILayout.BeginHorizontal();
            if (!string.IsNullOrEmpty(label)) GUILayout.Label(label, GUILayout.Width(labelWidth));
            if (GUILayout.Button("−", GUILayout.Width(30f))) result = Mathf.Max(min, value - 1);
            GUILayout.Label(display(value), GarageSkin.Header);
            if (GUILayout.Button("+", GUILayout.Width(30f))) result = Mathf.Min(max, value + 1);
            GUILayout.EndHorizontal();
            DrawRing(idx);
            int adj = Adjusted(idx);
            if (adj != 0) result = Mathf.Clamp(value + adj, min, max);
            if (result != value) Ui(Audio.ProceduralAudio.UiSelect, 0.5f);
            return result;
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        private static int Register()
        {
            if (!_drawingOwner) return -1;
            int idx = _count++;
            if (Event.current.type == EventType.Layout)
            {
                while (_disabled.Count <= idx) _disabled.Add(false);
                _disabled[idx] = !GUI.enabled;
            }
            return idx;
        }

        /// <summary>Pad activation of the focused control. Consumed on a
        /// Layout pass (each event owns a fresh one — see class doc), never on
        /// Repaint, and never for a disabled control.</summary>
        private static bool Activated(int idx)
        {
            if (!_drawingOwner || idx < 0 || !_submit) return false;
            if (idx != _focus || !GUI.enabled) return false;
            if (Event.current.type != EventType.Layout) return false;
            _submit = false;
            return true;
        }

        /// <summary>Left/right adjust for the focused slider/cycle, same
        /// consumption rules as <see cref="Activated"/>.</summary>
        private static int Adjusted(int idx)
        {
            if (!_drawingOwner || idx < 0 || _adjustX == 0) return 0;
            if (idx != _focus || !GUI.enabled) return 0;
            if (Event.current.type != EventType.Layout) return 0;
            int v = _adjustX;
            _adjustX = 0;
            return v;
        }

        /// <summary>Gold ring over the focused control — Repaint only, drawn
        /// with an explicit rect so it never takes part in layout.</summary>
        private static void DrawRing(int idx)
        {
            if (!_drawingOwner || idx < 0 || idx != _focus) return;
            if (Event.current.type != EventType.Repaint) return;
            if (!CursorAutoHide.PadIsLastInput) return;
            var r = GUILayoutUtility.GetLastRect();
            r.xMin -= 3f; r.xMax += 3f; r.yMin -= 3f; r.yMax += 3f;
            GUI.Box(r, GUIContent.none, GarageSkin.FocusRing);
        }

        /// <summary>Read the pad once per frame into edge latches with
        /// hold-to-repeat, from whichever entry point runs first (a host's
        /// Update via ConsumeBack, or BeginFrame).</summary>
        private static void Poll()
        {
            if (_pollFrame == Time.frameCount) return;
            _pollFrame = Time.frameCount;

            // A rebind capture owns the pad completely.
            if (SettingsPanel.Capturing)
            {
                _moveY = 0; _adjustX = 0; _submit = false; _back = false;
                _heldY = 0; _heldX = 0;
                return;
            }

            var stick = InputReader.LeftStick();
            int dirY = PadTable.HeldAny(PadButton.DpadDown) || stick.y < -0.5f ? 1
                     : PadTable.HeldAny(PadButton.DpadUp) || stick.y > 0.5f ? -1 : 0;
            int dirX = PadTable.HeldAny(PadButton.DpadRight) || stick.x > 0.5f ? 1
                     : PadTable.HeldAny(PadButton.DpadLeft) || stick.x < -0.5f ? -1 : 0;

            _moveY = Repeat(dirY, ref _heldY, ref _repeatAtY);
            _adjustX = Repeat(dirX, ref _heldX, ref _repeatAtX);
            if (PadTable.PressedAny(PadButton.South)) _submit = true;
            if (PadTable.PressedAny(PadButton.East)) _back = true;
        }

        /// <summary>First press fires immediately, then repeats after
        /// RepeatDelay at RepeatRate while held.</summary>
        private static int Repeat(int dir, ref int held, ref float nextAt)
        {
            float now = Time.unscaledTime;
            if (dir == 0) { held = 0; return 0; }
            if (dir != held)
            {
                held = dir;
                nextAt = now + RepeatDelay;
                return dir;
            }
            if (now >= nextAt)
            {
                nextAt = now + RepeatRate;
                return dir;
            }
            return 0;
        }

        private static void Ui(string key, float volume)
        {
            Audio.SfxPlayer.Ensure()?.PlayUi(key, volume);
        }
    }
}
