using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace AIHWSim.Build
{
    /// <summary>
    /// Watches the controller sources and raises a "something was saved" flag.
    ///
    /// It only ever sets a timestamp. The debounce, the decision to build and every
    /// Unity call live on the main thread in <see cref="ControllerBuildRunner"/>,
    /// because FileSystemWatcher events arrive on ThreadPool threads where touching
    /// the engine is undefined behaviour.
    /// </summary>
    public sealed class ControllerWatcher : IDisposable
    {
        private FileSystemWatcher _fsw;
        private long _dirtyTicks;         // UtcNow.Ticks of the last accepted event
        private readonly string _buildDir;

        /// <summary>Why the watcher is not running, or empty.</summary>
        public string Error { get; private set; } = "";
        public bool Active => _fsw != null;

        public ControllerWatcher(string workspaceRoot)
        {
            _buildDir = Path.Combine(workspaceRoot ?? "", "build")
                            .Replace('/', Path.DirectorySeparatorChar);
            try
            {
                _fsw = new FileSystemWatcher(workspaceRoot)
                {
                    IncludeSubdirectories = true,
                    // One pattern only, so the extension test happens in the handler.
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                 | NotifyFilters.Size,
                };
                _fsw.Changed += OnEvent;
                _fsw.Created += OnEvent;
                _fsw.Renamed += OnEvent;
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception e)
            {
                // A nonexistent path throws here. Report it rather than letting it
                // escape into whatever scene load happened to construct us.
                _fsw = null;
                Error = e.Message;
            }
        }

        /// <summary>
        /// True once <paramref name="quietMs"/> have passed with no further saves.
        /// Consuming it clears the flag.
        ///
        /// The quiet window is doing two jobs: editors emit two to five events for a
        /// single save (write, rename, attribute touch) and this collapses them into
        /// one build; and a user still typing keeps pushing the deadline out, which
        /// is exactly the "don't compile my half-finished edit" behaviour you want.
        /// </summary>
        public bool ConsumeIfSettled(int quietMs)
        {
            long t = Interlocked.Read(ref _dirtyTicks);
            if (t == 0) return false;
            if ((DateTime.UtcNow.Ticks - t) < quietMs * TimeSpan.TicksPerMillisecond)
                return false;
            Interlocked.Exchange(ref _dirtyTicks, 0);
            return true;
        }

        private void OnEvent(object sender, FileSystemEventArgs e)
        {
            if (!Accepts(e.FullPath)) return;
            Interlocked.Exchange(ref _dirtyTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Source files only, and never anything under <c>Controllers/build/</c>.
        /// That exclusion is the load-bearing line in this file: CMake and Ninja
        /// write generated .c/.h in there on every build, so watching it makes each
        /// build trigger the next one, forever.
        /// </summary>
        private bool Accepts(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            if (full.StartsWith(_buildDir, StringComparison.OrdinalIgnoreCase)) return false;

            string name = Path.GetFileName(full);
            if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                return true;

            string ext = Path.GetExtension(full);
            return string.Equals(ext, ".c", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            if (_fsw == null) return;
            try
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Changed -= OnEvent;
                _fsw.Created -= OnEvent;
                _fsw.Renamed -= OnEvent;
                _fsw.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CtrlBuild] Watcher dispose: {e.Message}");
            }
            _fsw = null;
        }
    }
}
