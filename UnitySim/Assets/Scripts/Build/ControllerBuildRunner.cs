using System.Collections.Generic;
using System.IO;
using AIHWSim.Core;
using UnityEngine;

namespace AIHWSim.Build
{
    /// <summary>
    /// The main-thread half of "build my controller without leaving the game".
    /// Lives on the persistent UiRuntime object, so a build survives the scene load
    /// between the menu and the track.
    ///
    /// Everything that touches Unity is here; everything that touches a process or
    /// a file watcher is on a worker thread in <see cref="ControllerBuildService"/>
    /// and <see cref="ControllerWatcher"/>. The seam is a queue drained in Update.
    ///
    /// The reason a rebuild can be dropped on a running game at all is that
    /// <c>NativeControllerLoader</c> loads a shadow copy from %TEMP% rather than the
    /// real DLL — the compiler is free to overwrite the original while we hold a
    /// handle. That was built long before this feature; this only supplies the
    /// missing half, which is something that actually runs the compiler.
    /// </summary>
    public sealed class ControllerBuildRunner : MonoBehaviour
    {
        public static ControllerBuildRunner Instance { get; private set; }

        /// <summary>Kept small enough to stay cheap to join, large enough to hold a
        /// full clean build's diagnostics.</summary>
        private const int MaxLines = 2000;
        private const int DrainPerFrame = 400;
        private const int WatchQuietMs = 750;

        private readonly ControllerBuildService _svc = new ControllerBuildService();
        private readonly List<BuildLine> _lines = new List<BuildLine>();
        private ControllerWatcher _watcher;

        private BuildState _lastState = BuildState.Idle;
        private bool _rebuildPending;      // a save landed mid-build: coalesce to ONE
        private string _startError = "";

        // Reused rather than allocated per build; Preflight clears them itself.
        private readonly List<string> _preflightErrors = new List<string>();
        private readonly List<string> _preflightWarnings = new List<string>();

        /// <summary>Bumped whenever <see cref="Lines"/> changes, so the UI can cache
        /// its joined string instead of rebuilding it several times per frame.</summary>
        public int LineVersion { get; private set; }

        /// <summary>Bumped once per finished build. A UI listing the built DLLs uses
        /// this to know when its list went stale — otherwise a first-time user who
        /// builds from an empty folder still sees "nothing built yet" until they
        /// leave the screen and come back.</summary>
        public int BuildGeneration { get; private set; }

        public IReadOnlyList<BuildLine> Lines => _lines;
        public BuildState State => _svc.State;
        public bool IsRunning => _svc.IsRunning;

        /// <summary>One line describing the loaded controller, for the UI header.</summary>
        public string StatusLine { get; private set; } = "";

        private void Awake()
        {
            Instance = this;
            RefreshWatcher();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _watcher?.Dispose();
            _watcher = null;
            // A live child process would otherwise outlive play mode.
            _svc.Dispose();
        }

        private void OnApplicationQuit() => _svc.Dispose();

        // ---- public API ------------------------------------------------------

        /// <summary>
        /// Build <paramref name="target"/> (a CMake target name; empty = all) and
        /// hot-reload on success.
        /// </summary>
        public void RequestBuild(string target)
        {
            if (_svc.IsRunning) { _rebuildPending = true; return; }

            if (!ControllerWorkspace.IsAvailable)
            {
                _startError = ControllerWorkspace.UnavailableReason;
                Append(BuildLineKind.Err, _startError);
                return;
            }

            _lines.Clear();
            LineVersion++;
            _startError = "";

            // Look at the sources before spending a build on them. Only ever for a
            // player's own script — the game's own controllers are not the ones
            // being edited, and checking them would be noise on every build.
            var script = UserScriptCatalog.Find(target);
            if (script != null)
            {
                UserScriptCatalog.Preflight(script, _preflightErrors, _preflightWarnings);
                foreach (var w in _preflightWarnings) Append(BuildLineKind.Err, "warning: " + w);
                if (_preflightErrors.Count > 0)
                {
                    foreach (var e in _preflightErrors) Append(BuildLineKind.Err, e);
                    _startError = _preflightErrors[0];
                    return;   // nothing to hand the compiler
                }
            }

            if (!_svc.Start(ControllerWorkspace.Root, target,
                            Persistence.SettingsStore.Current.controllerBuildConfig,
                            PluginDir(), out string err))
            {
                _startError = err;
                Append(BuildLineKind.Err, err);
            }
        }

        public void Cancel() => _svc.Cancel();

        public void ClearLog()
        {
            _lines.Clear();
            LineVersion++;
        }

        /// <summary>Turn the save-watcher on or off. Persisting the choice is the
        /// caller's job; this only owns the watcher object.</summary>
        public void SetWatch(bool on)
        {
            if (on == (_watcher != null)) return;
            if (!on) { _watcher?.Dispose(); _watcher = null; return; }
            RefreshWatcher();
        }

        public bool Watching => _watcher != null && _watcher.Active;

        /// <summary>Re-run workspace resolution after the user edits the path.</summary>
        public void RefreshWorkspace()
        {
            ControllerWorkspace.Invalidate();
            bool wasWatching = _watcher != null;
            _watcher?.Dispose();
            _watcher = null;
            if (wasWatching) RefreshWatcher();
        }

        // ---- frame loop ------------------------------------------------------

        private void Update()
        {
            DrainOutput();
            PumpState();
            PumpWatcher();
            RefreshStatus();
        }

        private void DrainOutput()
        {
            int n = 0;
            while (n++ < DrainPerFrame && _svc.TryDequeue(out var line))
                Append(line.kind, line.text);
        }

        private void Append(BuildLineKind kind, string text)
        {
            _lines.Add(new BuildLine { kind = kind, text = text });
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            LineVersion++;
        }

        /// <summary>Fire the reload on the Running -> finished edge, exactly once.</summary>
        private void PumpState()
        {
            var now = _svc.State;
            if (now == _lastState) return;
            bool finished = _lastState == BuildState.Running && now != BuildState.Running;
            _lastState = now;
            if (!finished) return;
            BuildGeneration++;
            // A build changes what is on disk: which scripts have a DLL, and how old
            // it is. Both are what the menu reads to say "your edits are not in this
            // one yet", so the answer has to be re-derived rather than remembered.
            UserScriptCatalog.Invalidate();
            if (now == BuildState.Succeeded) ReloadAll();
        }

        private void PumpWatcher()
        {
            if (_watcher == null || _svc.IsRunning) return;

            // A save that landed during a build becomes exactly one more build, not
            // a backlog the user cannot stop.
            if (_rebuildPending)
            {
                _rebuildPending = false;
                RequestBuild(WatchTarget());
                return;
            }
            if (_watcher.ConsumeIfSettled(WatchQuietMs))
                RequestBuild(WatchTarget());
        }

        /// <summary>What a watcher-triggered build should compile: the target behind
        /// the DLL the live car is actually running, so a save does not rebuild
        /// three controllers nobody is driving.</summary>
        private string WatchTarget()
        {
            var runner = ActiveControllerRunner();
            string dll = runner != null ? Path.GetFileName(runner.dllRelativePath)
                                        : Persistence.SettingsStore.Current.simControllerDll;
            return ControllerWorkspace.TargetForDll(dll) ?? "";
        }

        // ---- reload ----------------------------------------------------------

        /// <summary>
        /// Reload every runner that uses a DLL. Found by search rather than held as
        /// a reference because split-screen and bots each get their own runner, and
        /// because the menu screen that starts a build has none at all.
        /// </summary>
        private void ReloadAll()
        {
            var runners = Object.FindObjectsByType<SimulationRunner>(FindObjectsSortMode.None);
            int touched = 0, ok = 0;

            // Mirror the loader's own [ControllerLoader]/[SimRunner] messages into
            // the pane for the duration of the reload — "which export is missing" is
            // the answer people need, and it is only ever written to the Unity log.
            Application.logMessageReceived += MirrorLog;
            try
            {
                foreach (var r in runners)
                {
                    if (r == null || !r.loadControllerDll) continue;
                    touched++;

                    // Save first. Freshly compiled C is exactly when a bad pointer
                    // takes the whole process down, and a native access violation is
                    // not something a C# try/catch can save you from — so anything
                    // still only in memory is gone with it.
                    if (r.HasUnsavedTelemetry) r.SaveTelemetry();

                    if (r.ReloadController())
                    {
                        ok++;
                        Append(BuildLineKind.Info,
                               $"reloaded {r.LoadedControllerName} " +
                               $"(built {r.LoadedControllerStamp:HH:mm:ss} UTC)");
                        if (Persistence.SettingsStore.Current.controllerResetOnReload)
                            r.RestartRun();
                    }
                    else
                    {
                        Append(BuildLineKind.Err,
                               "reload failed — running open-loop (the car will coast)");
                    }
                }
            }
            finally
            {
                Application.logMessageReceived -= MirrorLog;
            }

            if (touched == 0)
            {
                Append(BuildLineKind.Info,
                       "no car is running a controller right now — the new DLL loads " +
                       "when you next start a Simulate Controller drive");
                return;
            }

            // The most confusing failure this feature has: the build succeeded, and
            // it built something other than what is driving.
            var active = ActiveControllerRunner();
            string want = active != null ? Path.GetFileName(active.dllRelativePath) : null;
            string built = _svc.LastTarget;
            if (ok > 0 && want != null && !string.IsNullOrEmpty(built)
                && !string.Equals(ControllerWorkspace.TargetForDll(want), built,
                                  System.StringComparison.OrdinalIgnoreCase))
            {
                Append(BuildLineKind.Err,
                       $"note: built '{built}', but this car runs '{want}' — " +
                       "nothing you just compiled is driving");
            }
        }

        private void MirrorLog(string condition, string stack, LogType type)
        {
            if (string.IsNullOrEmpty(condition)) return;
            if (!condition.StartsWith("[ControllerLoader]") && !condition.StartsWith("[SimRunner]"))
                return;
            Append(type == LogType.Log ? BuildLineKind.Info : BuildLineKind.Err, condition);
        }

        // ---- helpers ---------------------------------------------------------

        /// <summary>The one runner whose controller the player is watching, or null.</summary>
        private static SimulationRunner ActiveControllerRunner()
        {
            var runners = Object.FindObjectsByType<SimulationRunner>(FindObjectsSortMode.None);
            foreach (var r in runners)
                if (r != null && r.loadControllerDll) return r;
            return null;
        }

        /// <summary>See <see cref="ControllerWorkspace.PluginDir"/> — it lives there
        /// because the catalogue needs the same answer to date the built DLLs.</summary>
        private static string PluginDir() => ControllerWorkspace.PluginDir();

        private void RefreshWatcher()
        {
            var s = Persistence.SettingsStore.Current;
            if (s == null || !s.controllerRebuildOnSave) return;
            if (!ControllerWorkspace.IsAvailable) return;
            // Both roots: the game's controllers and the player's UserScripts. The
            // second is the one people actually edit, and it is a sibling of the
            // first rather than inside it.
            _watcher = new ControllerWatcher(ControllerWorkspace.WatchRoots());
            if (!string.IsNullOrEmpty(_watcher.Error))
            {
                Append(BuildLineKind.Err, $"rebuild-on-save unavailable: {_watcher.Error}");
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void RefreshStatus()
        {
            var r = ActiveControllerRunner();
            if (r == null)
            {
                StatusLine = ControllerWorkspace.IsAvailable
                    ? "no controller running"
                    : ControllerWorkspace.UnavailableReason;
                return;
            }
            StatusLine = r.ControllerReady
                ? $"LOADED {r.LoadedControllerName} · built {r.LoadedControllerStamp:HH:mm:ss} UTC"
                : $"OPEN-LOOP — {Path.GetFileName(r.dllRelativePath)} is not loaded";
        }

        /// <summary>Reason the last build could not be started, or empty.</summary>
        public string StartError => _startError;
    }
}
