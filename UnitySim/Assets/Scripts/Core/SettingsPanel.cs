using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// The in-race settings panel, drawn by whichever Esc menu is on screen.
    ///
    /// A static drawn into the caller's layout rather than a MonoBehaviour of its
    /// own, because it has two homes and they are mutually exclusive: LAN scenes
    /// replace <see cref="PauseMenu"/> with <c>LanSessionMenu</c> entirely (a
    /// network session cannot pause), so a panel that lived inside PauseMenu would
    /// be invisible to exactly the players who cannot reach the main menu to
    /// change anything.
    ///
    /// What is here and what is not follows one rule: a setting belongs here if
    /// changing it mid-session is both meaningful and safe. Volumes, control feel,
    /// assists and bindings qualify. Quality/fullscreen/vsync do not (they want a
    /// reload), and neither do the noise seed and actuation delay — those are sim
    /// parameters, and moving one mid-run would silently invalidate the telemetry
    /// the run is producing.
    /// </summary>
    public static class SettingsPanel
    {
        private static Vector2 _scroll;
        private static bool _showKeys;

        // Rebind capture. One panel is ever on screen, so this is static state
        // rather than an instance field for the same reason the panel itself is.
        private static DriveAction _capture;
        private static bool _captureAlt;
        private static bool _capturePad;
        private static bool _capturing;

        /// <summary>
        /// True while the panel is waiting for a key. The host menu must consult
        /// this before acting on its own Esc: pressing Escape to cancel a capture
        /// would otherwise also close the menu, which makes cancelling impossible
        /// to do without losing your place.
        /// </summary>
        public static bool Capturing => _capturing;

        /// <summary>Drop any half-finished capture — called when the menu closes.</summary>
        public static void Reset()
        {
            _capturing = false;
        }

        /// <summary>
        /// Draw the panel inside the caller's existing layout area.
        /// </summary>
        /// <param name="rigs">Live rigs, so an assist moved here reaches the car
        /// being driven on the next physics step. Null is fine — the settings
        /// still save, they just apply at the next scene load.</param>
        /// <param name="height">Height for the scroll region.</param>
        public static void Draw(IReadOnlyList<PlayerRig> rigs, float height = 300f)
        {
            var s = Persistence.SettingsStore.Current;
            bool changed = false;

            // Capture is polled on Layout only. OnGUI runs several times per frame
            // and IMGUI requires the control count to match between the Layout and
            // Repaint passes; doing the state change once, in the pass that is
            // allowed to change layout, keeps that true.
            if (_capturing && Event.current.type == EventType.Layout)
                changed |= PollCapture();

            GUILayout.Label("SETTINGS", GarageSkin.Header);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(height));

            // ---- audio ----
            GUILayout.Label("AUDIO", GarageSkin.Header);
            changed |= GarageSkin.Slider01("Master", ref s.masterVolume);
            changed |= GarageSkin.Slider01("Sound effects", ref s.sfxVolume);
            changed |= GarageSkin.Slider01("Engine + tyres", ref s.engineVolume);

            // ---- control feel ----
            GUILayout.Space(4);
            GUILayout.Label("CONTROL FEEL", GarageSkin.Header);
            changed |= GarageSkin.Slider01("KB steer smoothing", ref s.kbSteerSmoothing);
            changed |= GarageSkin.Slider01("KB throttle smoothing", ref s.kbThrottleSmoothing);
            GUILayout.Label("0% is the old instant step. Gamepad sticks are never shaped.",
                GarageSkin.StatLabel);

            // ---- assists ----
            GUILayout.Space(4);
            GUILayout.Label("ASSISTS — P1", GarageSkin.Header);
            changed |= DrawPresetRow(s);

            bool moved = false;
            moved |= GarageSkin.Slider01("Steering help", ref s.p1AssistSteer);
            moved |= GarageSkin.Slider01("Stability", ref s.p1AssistStability);
            moved |= GarageSkin.Slider01("Traction ctrl", ref s.p1AssistTraction);
            moved |= GarageSkin.Slider01("ABS", ref s.p1AssistAbs);

            if (LocalHumans(rigs) > 1)
            {
                GUILayout.Space(4);
                GUILayout.Label("ASSISTS — P2", GarageSkin.Header);
                moved |= GarageSkin.Slider01("Steering help", ref s.p2AssistSteer);
                moved |= GarageSkin.Slider01("Stability", ref s.p2AssistStability);
                moved |= GarageSkin.Slider01("Traction ctrl", ref s.p2AssistTraction);
                moved |= GarageSkin.Slider01("ABS", ref s.p2AssistAbs);
            }
            if (moved)
            {
                s.assistPreset = (int)SessionConfig.AssistPreset.Custom;
                changed = true;
            }

            // Arcade handling is READ-ONLY here: it is consumed once at rig build
            // to set every car's grip baseline and assist floor, so a toggle would
            // do nothing until the next session and read as broken.
            if (SessionConfig.Arcade || !SessionConfig.ArcadeHandling)
                GUILayout.Label(SessionConfig.ArcadeHandling
                        ? "Handling: Arcade (set when the session started)."
                        : "Handling: Sim (set when the session started).",
                    GarageSkin.StatLabel);

            // ---- bindings ----
            GUILayout.Space(4);
            if (GUILayout.Button(_showKeys ? "Controls ▲" : "Controls ▼", GUILayout.Height(26)))
            {
                _showKeys = !_showKeys;
                _capturing = false;
            }
            if (_showKeys) changed |= DrawKeys();

            // ---- telemetry ----
            GUILayout.Space(4);
            GUILayout.Label("TELEMETRY", GarageSkin.Header);
            bool lg = GUILayout.Toggle(s.logTelemetry, " Log sensor/telemetry data (CSV)");
            if (lg != s.logTelemetry) { s.logTelemetry = lg; changed = true; }

            GUILayout.EndScrollView();

            if (!changed) return;
            Persistence.SettingsStore.Apply();
            Persistence.SettingsStore.Save();
            // The one thing Apply() does not do. Assists are per-rig scene state,
            // not engine state, so pushing them is this panel's job — without it a
            // slider does nothing until the next scene load, which reads as a
            // broken control rather than a deferred one.
            AssistApplier.ApplyLive(rigs);
        }

        private static bool DrawPresetRow(Persistence.GameSettings s)
        {
            bool changed = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset", GUILayout.Width(60));
            string[] names = { "Off", "Standard", "Full", "Custom" };
            int preset = Mathf.Clamp(s.assistPreset, 0, 3);
            for (int i = 0; i < names.Length; i++)
            {
                bool on = preset == i;
                if (GUILayout.Toggle(on, names[i], GarageSkin.Skin.button,
                        GUILayout.Width(62)) != on && !on)
                {
                    s.assistPreset = i;
                    changed = true;
                }
            }
            GUILayout.EndHorizontal();
            return changed;
        }

        // ---- bindings ---------------------------------------------------------

        private static readonly DriveAction[] KeyActions =
        {
            DriveAction.ThrottleUp, DriveAction.ThrottleDown,
            DriveAction.SteerLeft, DriveAction.SteerRight,
            DriveAction.Brake, DriveAction.Handbrake,
            DriveAction.Respawn, DriveAction.UseItem, DriveAction.LookBack,
            DriveAction.ModeToggle, DriveAction.Pause,
        };

        private static bool DrawKeys()
        {
            var b = KeyBindings.Current;
            bool changed = false;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Layout", GUILayout.Width(60));
            var layout = (KeyLayout)Mathf.Clamp(b.layout, 0, 2);
            changed |= LayoutButton(b, layout, KeyLayout.Wasd, "WASD");
            changed |= LayoutButton(b, layout, KeyLayout.Arrows, "Arrows");
            GUI.enabled = false;
            GUILayout.Toggle(layout == KeyLayout.Custom, "Custom",
                GarageSkin.Skin.button, GUILayout.Width(62));
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label("KEYBOARD", GarageSkin.Header);
            foreach (var a in KeyActions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(KeyBindings.Label(a), GUILayout.Width(120));
                DrawBindButton(b, a, false);
                if (KeyBindings.HasAlt(a)) DrawBindButton(b, a, true);
                GUILayout.EndHorizontal();
                // Reported, not prevented: deliberately putting brake and
                // handbrake on one key is a legitimate thing to want, and the only
                // difference between that and a typo is whether it was meant.
                if (b.IsConflicted(a) || (KeyBindings.HasAlt(a) && b.IsConflicted(a, true)))
                    GUILayout.Label("   ↑ shares a key with another control", GarageSkin.StatLabel);
            }

            GUILayout.Label("GAMEPAD", GarageSkin.Header);
            GUILayout.Label("Throttle and steering stay on the triggers and left stick — " +
                            "they are analog.", GarageSkin.StatLabel);
            foreach (var a in KeyBindings.PadActions)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(KeyBindings.Label(a), GUILayout.Width(120));
                bool waiting = _capturing && _capturePad && _capture == a;
                if (GUILayout.Button(waiting ? "press a button…" : PadTable.Label(b.Pad(a)),
                        GUILayout.Width(120)))
                    BeginCapture(a, false, pad: true);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Reset to defaults", GUILayout.Height(24)))
            {
                b.ApplyLayout(KeyLayout.Wasd);
                _capturing = false;
                changed = true;
            }
            if (_capturing)
                GUILayout.Label(_capturePad
                        ? "Press a gamepad button (Esc cancels)."
                        : "Press a key (Esc cancels).", GarageSkin.StatLabel);

            // The overlay hotkeys are pinned on purpose — they are developer tools,
            // not controls, and documenting a fixed key is only honest if it is one.
            GUILayout.Label("Fixed: G graph · J metrics · K mission · P pause-graph · [ ] window",
                GarageSkin.StatLabel);
            return changed;
        }

        private static bool LayoutButton(KeyBindings b, KeyLayout current, KeyLayout which, string label)
        {
            bool on = current == which;
            if (GUILayout.Toggle(on, label, GarageSkin.Skin.button, GUILayout.Width(62)) == on || on)
                return false;
            b.ApplyLayout(which);
            _capturing = false;
            return true;
        }

        private static void DrawBindButton(KeyBindings b, DriveAction a, bool alt)
        {
            bool waiting = _capturing && !_capturePad && _capture == a && _captureAlt == alt;
            var key = alt ? b.AltKey(a) : b.Key(a);
            string text = waiting ? "press a key…" : KeyTable.Label(key);
            if (GUILayout.Button(text, GUILayout.Width(alt ? 76 : 96)))
                BeginCapture(a, alt, pad: false);
        }

        private static void BeginCapture(DriveAction a, bool alt, bool pad)
        {
            _capture = a;
            _captureAlt = alt;
            _capturePad = pad;
            _capturing = true;
        }

        /// <summary>Take the key or button pressed this frame. Returns true when a
        /// binding actually changed.</summary>
        private static bool PollCapture()
        {
            var b = KeyBindings.Current;

            // Escape cancels, never binds. It is the one key that has to keep
            // meaning "get me out of here" — which is also why PausePressed
            // honours it whatever the bindings say.
            if (KeyTable.Pressed(KeyCode.Escape)) { _capturing = false; return false; }

            if (_capturePad)
            {
                var pb = PadTable.CaptureThisFrame();
                if (pb == PadButton.None) return false;
                b.SetPad(_capture, pb);
                _capturing = false;
                return true;
            }

            var k = KeyTable.CaptureThisFrame();
            if (k == KeyCode.None) return false;
            b.SetKey(_capture, k, _captureAlt);
            _capturing = false;
            return true;
        }

        /// <summary>Local human rigs — what decides whether the P2 block is shown.
        /// Counting the whole list would show P2's assists in a bot race.</summary>
        private static int LocalHumans(IReadOnlyList<PlayerRig> rigs)
        {
            if (rigs == null) return 0;
            int n = 0;
            for (int i = 0; i < rigs.Count; i++)
            {
                var slot = rigs[i]?.slot;
                if (slot == null || slot.isBot) continue;
                if (slot.control != DriveControl.Human) continue;
                n++;
            }
            return n;
        }
    }
}
