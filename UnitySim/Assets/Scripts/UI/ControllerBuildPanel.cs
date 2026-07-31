using System.Text;
using AIHWSim.Build;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.UI
{
    /// <summary>
    /// "Build &amp; Reload" — compile the player's C controller and hot-swap it into
    /// the running game, with the compiler's output on screen.
    ///
    /// A static drawn into the caller's layout rather than a MonoBehaviour, for the
    /// same reason as <see cref="Core.SettingsPanel"/>: it has two mutually
    /// exclusive homes (the Simulate Controller menu screen and the in-drive pause
    /// menu), and the scroll position and cached log belong to "the one panel on
    /// screen", not to either host.
    /// </summary>
    public static class ControllerBuildPanel
    {
        private static Vector2 _scroll;
        private static int _targetIdx;
        private static string _pathEdit;

        // Layout-snapshotted twins. Everything here can change from a worker
        // thread's output arriving in Update, i.e. between a Layout pass and its
        // paired Repaint — and IMGUI requires the control census to match across
        // that pair (see MenuNav's class doc).
        private static string _statusDraw = "";
        private static bool _watchDraw;
        private static bool _busyDraw;
        // Whether the workspace was found decides between two completely different
        // control sets. "Use this folder" resolves the workspace from inside a
        // Layout pass (MenuNav only activates there), so without this snapshot that
        // click would hand the paired Repaint a different set of controls than
        // Layout registered — the one IMGUI error this UI never risks.
        private static bool _availDraw;

        // The log is rendered as ONE control over a joined string, rebuilt only when
        // the line count changes. Not one Label per line: a streaming log would then
        // change the control count mid-frame, which is precisely the crash this
        // codebase's whole UI discipline exists to avoid. It also keeps a 2000-line
        // buffer from being re-joined several times every frame.
        private static string _logCache = "";
        private static int _logVersion = -1;
        private static GUIStyle _logStyle;

        public static bool IsAvailable => ControllerWorkspace.IsAvailable;
        public static string UnavailableReason => ControllerWorkspace.UnavailableReason;

        /// <summary>Draw inside the caller's existing layout area. The host owns
        /// UIScale and the skin.</summary>
        public static void Draw(float logHeight = 150f)
        {
            var runner = ControllerBuildRunner.Instance;
            if (runner == null)
            {
                GUILayout.Label("Controller building is not available in this session.",
                                GarageSkin.StatLabel);
                return;
            }

            if (Event.current.type == EventType.Layout)
            {
                // "Busy", not "state == Running": a cancelled build is no longer
                // Running but the child is still dying, and Start refuses until it
                // is gone — so gating on the state would offer a button that
                // silently does nothing for a second or two.
                _busyDraw = runner.IsRunning;
                _statusDraw = runner.StatusLine ?? "";
                _watchDraw = runner.Watching;
                _availDraw = ControllerWorkspace.IsAvailable;
                if (_logVersion != runner.LineVersion)
                {
                    _logVersion = runner.LineVersion;
                    _logCache = JoinLog(runner);
                    _scroll.y = float.MaxValue;   // follow the tail as it streams
                }
            }

            GUILayout.Label("CONTROLLER", GarageSkin.Header);
            GUILayout.Label(_statusDraw, GarageSkin.StatLabel);

            if (!_availDraw)
            {
                DrawWorkspaceFixup(runner);
                return;
            }

            var targets = ControllerWorkspace.Targets;
            if (targets.Count > 0)
            {
                _targetIdx = Mathf.Clamp(_targetIdx, 0, targets.Count - 1);
                _targetIdx = MenuNav.Cycle("Build", _targetIdx, targets.Count,
                                           k => targets[k], 60f);
            }

            // Both buttons always exist; only their enabled state changes. Adding or
            // removing one per build state would change the control census, whereas
            // MenuNav.Register already records the disabled flag and skips greyed
            // rows with the pad.
            bool running = _busyDraw;
            GUILayout.BeginHorizontal();
            GUI.enabled = !running;
            if (MenuNav.Button("Build & Reload"))
                runner.RequestBuild(targets.Count > 0 ? targets[_targetIdx] : "");
            GUI.enabled = running;
            if (MenuNav.Button("Cancel")) runner.Cancel();
            GUI.enabled = !running;
            if (MenuNav.Button("Clear")) runner.ClearLog();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            var s = Persistence.SettingsStore.Current;
            bool watch = MenuNav.Toggle(_watchDraw, " Rebuild when I save a source file");
            if (watch != _watchDraw)
            {
                s.controllerRebuildOnSave = watch;
                Persistence.SettingsStore.Save();
                runner.SetWatch(watch);
            }
            bool reset = MenuNav.Toggle(s.controllerResetOnReload, " Reset the car after a reload");
            if (reset != s.controllerResetOnReload)
            {
                s.controllerResetOnReload = reset;
                Persistence.SettingsStore.Save();
            }

            GUILayout.Label(_logCache.Length > 0 ? "BUILD OUTPUT" : "BUILD OUTPUT — nothing yet",
                            GarageSkin.Header);
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(logHeight));
            GUILayout.Label(_logCache, LogStyle());
            GUILayout.EndScrollView();

            // Permanent, not conditional: this is the one failure the game cannot
            // protect anyone from, and it is worth reading every time.
            GUILayout.Label("A bad pointer in your C code crashes the whole process — " +
                            "in the editor, that means Unity. A managed try/catch cannot " +
                            "catch a native access violation.", GarageSkin.StatLabel);
        }

        /// <summary>No workspace: say why, and offer a path rather than a dead button.</summary>
        private static void DrawWorkspaceFixup(ControllerBuildRunner runner)
        {
            GUILayout.Label(ControllerWorkspace.UnavailableReason, GarageSkin.StatLabel);
            _pathEdit ??= Persistence.SettingsStore.Current.controllerWorkspace ?? "";
            _pathEdit = GUILayout.TextField(_pathEdit);
            if (MenuNav.Button("Use this folder"))
            {
                var s = Persistence.SettingsStore.Current;
                s.controllerWorkspace = _pathEdit.Trim();
                Persistence.SettingsStore.Save();
                runner.RefreshWorkspace();
            }
        }

        private static string JoinLog(ControllerBuildRunner runner)
        {
            var lines = runner.Lines;
            if (lines.Count == 0) return "";
            var sb = new StringBuilder(lines.Count * 48);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                // Rich text rather than per-line colouring, because per-line colour
                // would mean per-line controls — see the field comment above.
                if (l.kind == BuildLineKind.Err) sb.Append("<color=#ff8f8f>");
                else if (l.kind == BuildLineKind.Info) sb.Append("<color=#9fd7ff>");
                sb.Append(Escape(l.text));
                if (l.kind != BuildLineKind.Out) sb.Append("</color>");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Neuter '&lt;' so rich text cannot mistake a compiler diagnostic for
        /// markup — C++ template names and <c>#include &lt;stdio.h&gt;</c> both
        /// contain one, and Unity would swallow the rest of the line as a malformed
        /// tag. Unity's rich text has no entity syntax, so the fix is a zero-width
        /// space after the bracket: invisible, and no longer a tag opener.
        /// </summary>
        private const string ZeroWidthSpace = "\u200B";

        private static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("<", "<" + ZeroWidthSpace);

        private static GUIStyle LogStyle()
        {
            if (_logStyle != null) return _logStyle;
            _logStyle = new GUIStyle(GarageSkin.StatLabel)
            {
                richText = true,
                wordWrap = false,
                alignment = TextAnchor.UpperLeft,
                fontSize = 11,
            };
            return _logStyle;
        }
    }
}
