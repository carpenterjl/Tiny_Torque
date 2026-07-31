using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AIHWSim.Build
{
    public enum BuildState { Idle, Running, Succeeded, Failed, Cancelled }

    public enum BuildLineKind { Out, Err, Info }

    public struct BuildLine
    {
        public BuildLineKind kind;
        public string text;
    }

    /// <summary>
    /// Runs <c>Controllers/build.ps1</c> as a child process and streams its output.
    ///
    /// Contains NO Unity API on purpose: everything here runs on a worker thread,
    /// and the engine is not thread-safe. Output is enqueued and drained on the
    /// main thread by <see cref="ControllerBuildRunner"/> — the same
    /// thread + ConcurrentQueue + Drain shape <c>LanDiscovery</c> already uses for
    /// its listener.
    /// </summary>
    public sealed class ControllerBuildService
    {
        private readonly ConcurrentQueue<BuildLine> _lines = new ConcurrentQueue<BuildLine>();
        private Process _proc;
        private Thread _thread;
        private volatile int _state = (int)BuildState.Idle;
        private volatile int _exitCode;

        /// <summary>
        /// True from Start until the waiter thread has fully finished with the
        /// process. Separate from <see cref="State"/> on purpose: Cancel flips the
        /// state to Cancelled the instant it is asked, while the child takes a
        /// moment to actually die. Gating Start on the state instead would let a
        /// second build begin while the first one's thread was still holding — and
        /// about to Dispose — a Process field the new build had already replaced.
        /// </summary>
        private volatile bool _busy;

        public BuildState State => (BuildState)_state;
        public int ExitCode => _exitCode;
        public bool IsRunning => _busy;

        /// <summary>The target the in-flight (or last) build asked for. Empty means
        /// "everything" — build.ps1 omits --target and CMake builds all of them.</summary>
        public string LastTarget { get; private set; } = "";

        public bool TryDequeue(out BuildLine line) => _lines.TryDequeue(out line);

        /// <summary>
        /// Kick off a build. Returns false (with a reason) if one is already
        /// running or PowerShell could not be started — the caller shows the reason
        /// rather than leaving a button that appears to do nothing.
        /// </summary>
        /// <param name="pluginDir">Where the built DLL must land. Forwarded as
        /// -PluginDir so a player build copies next to the running game rather than
        /// into the repo it was compiled from. Null keeps build.ps1's own default.</param>
        public bool Start(string workspaceRoot, string target, string config,
                          string pluginDir, out string error)
        {
            error = "";
            if (IsRunning) { error = "A build is already running."; return false; }

            string script = Path.Combine(workspaceRoot ?? "", "build.ps1");
            if (!File.Exists(script))
            {
                error = $"build.ps1 not found at {script}";
                _state = (int)BuildState.Failed;
                return false;
            }

            string shell = FindPowerShell();
            if (shell == null)
            {
                error = "Could not find powershell.exe or pwsh.exe.";
                _state = (int)BuildState.Failed;
                return false;
            }

            LastTarget = target ?? "";
            while (_lines.TryDequeue(out _)) { }   // a fresh build gets a fresh log

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,          // required for redirection
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workspaceRoot,
            };

            // Every token added separately. ArgumentList applies Win32
            // CommandLineToArgvW quoting, which is the only thing that survives a
            // project path containing a space ("EE Projects") — hand-concatenating
            // these and hoping is how that breaks. Never also set psi.Arguments;
            // setting both throws.
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-Config");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(config) ? "Release" : config);
            if (!string.IsNullOrWhiteSpace(target))
            {
                psi.ArgumentList.Add("-Target");
                psi.ArgumentList.Add(target);
            }
            if (!string.IsNullOrWhiteSpace(pluginDir))
            {
                psi.ArgumentList.Add("-PluginDir");
                psi.ArgumentList.Add(pluginDir);
            }

            // Compilers emit ANSI colour when they think they are on a terminal;
            // an IMGUI label renders the escapes literally.
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["CLICOLOR"] = "0";

            try
            {
                _proc = new Process { StartInfo = psi, EnableRaisingEvents = false };
                _proc.OutputDataReceived += (_, e) => Enqueue(BuildLineKind.Out, e.Data);
                _proc.ErrorDataReceived += (_, e) => Enqueue(BuildLineKind.Err, e.Data);
                _state = (int)BuildState.Running;
                _busy = true;
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
            }
            catch (Exception e)
            {
                error = $"Could not start PowerShell: {e.Message}";
                _state = (int)BuildState.Failed;
                _busy = false;
                _proc = null;
                return false;
            }

            Emit(BuildLineKind.Info,
                 $"> build.ps1 -Config {config}" +
                 (string.IsNullOrWhiteSpace(target) ? "" : $" -Target {target}"));

            // Both pipes must be drained while the child runs or a full buffer
            // deadlocks it. The async readers do that; this thread exists purely to
            // wait without blocking the game.
            _thread = new Thread(WaitForExit) { IsBackground = true, Name = "ControllerBuild" };
            _thread.Start();
            return true;
        }

        private void WaitForExit()
        {
            // Local handle: _proc is nulled below, and the field must not be read
            // again after that point.
            var proc = _proc;
            try
            {
                // The PARAMETERLESS overload — it is the one that also waits for the
                // async output readers to flush. The int overload returns as soon as
                // the process dies and can truncate the tail of the log.
                proc.WaitForExit();
                _exitCode = proc.ExitCode;
            }
            catch (Exception e)
            {
                Emit(BuildLineKind.Err, $"build wait failed: {e.Message}");
                _exitCode = -1;
            }

            bool cancelled = _state == (int)BuildState.Cancelled;
            if (cancelled)
            {
                Emit(BuildLineKind.Info,
                     "-- cancelled (a compiler process may still be finishing)");
            }
            else
            {
                _state = _exitCode == 0 ? (int)BuildState.Succeeded : (int)BuildState.Failed;
                Emit(BuildLineKind.Info, _exitCode == 0
                    ? "-- build ok"
                    : $"-- build FAILED (exit {_exitCode})");
            }

            try { proc?.Dispose(); } catch { /* already gone */ }
            _proc = null;
            // Released LAST, so the next Start cannot race the teardown above.
            _busy = false;
        }

        /// <summary>
        /// Ask the build to stop. Only the PowerShell process is killed — Mono has
        /// no process-tree kill, so cmake/cl children may run on for a moment. The
        /// next build is refused until this one's thread finishes, so that cannot
        /// turn into two compilers writing the same DLL.
        /// </summary>
        public void Cancel()
        {
            if (!IsRunning) return;
            _state = (int)BuildState.Cancelled;
            try { _proc?.Kill(); } catch { /* raced with a normal exit */ }
        }

        /// <summary>Kill without reporting — for teardown.</summary>
        public void Dispose()
        {
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
            catch { /* teardown is best-effort */ }
        }

        private void Enqueue(BuildLineKind kind, string data)
        {
            if (data == null) return;   // end-of-stream marker
            // Ninja overwrites its progress counter with \r rather than \n; without
            // this the whole build arrives as one unreadable line.
            foreach (var part in data.Split('\r', '\n'))
                if (part.Length > 0) Emit(kind, part);
        }

        private void Emit(BuildLineKind kind, string text) =>
            _lines.Enqueue(new BuildLine { kind = kind, text = text });

        /// <summary>
        /// pwsh if it is on PATH, else the absolute Windows PowerShell path. Probing
        /// PATH ourselves rather than shelling out to where.exe: that would be a
        /// whole extra process to answer a question we can answer with File.Exists.
        /// </summary>
        private static string FindPowerShell()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string p = Path.Combine(dir.Trim(), "pwsh.exe");
                    if (File.Exists(p)) return p;
                }
                catch { /* a malformed PATH entry is not worth failing over */ }
            }

            // Absolute, present on every Windows install, and immune to PATH games.
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string ps = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (File.Exists(ps)) return ps;
            }
            catch { }

            return null;
        }
    }
}
