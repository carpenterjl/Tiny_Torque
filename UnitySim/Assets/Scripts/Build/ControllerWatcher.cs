using System;
using System.Collections.Generic;
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
        private readonly List<FileSystemWatcher> _fsws = new List<FileSystemWatcher>();
        private readonly List<string> _buildDirs = new List<string>();
        private long _dirtyTicks;         // UtcNow.Ticks of the last accepted event

        /// <summary>Why the watcher is not running, or empty.</summary>
        public string Error { get; private set; } = "";
        public bool Active => _fsws.Count > 0;

        /// <summary>
        /// Watch every root given. There are normally two — the game's
        /// <c>Controllers/</c> and the player's <c>UserScripts/</c> — and they are
        /// siblings, so one recursive watcher cannot see both. Watching their common
        /// parent instead would put the whole repo (and Unity's Library folder,
        /// which churns constantly) under a rebuild trigger.
        ///
        /// A root that cannot be watched is skipped, not fatal: rebuild-on-save over
        /// one of the two is still worth having, and <see cref="Error"/> says which
        /// one was lost.
        /// </summary>
        public ControllerWatcher(params string[] roots)
        {
            if (roots == null || roots.Length == 0)
            {
                Error = "nothing to watch";
                return;
            }

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                _buildDirs.Add(Path.Combine(root, "build")
                                   .Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var fsw = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        // One pattern only, so the extension test happens in the handler.
                        Filter = "*.*",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                     | NotifyFilters.Size,
                    };
                    fsw.Changed += OnEvent;
                    fsw.Created += OnEvent;
                    fsw.Renamed += OnEvent;
                    fsw.EnableRaisingEvents = true;
                    _fsws.Add(fsw);
                }
                catch (Exception e)
                {
                    // A nonexistent path throws here. Report it rather than letting
                    // it escape into whatever scene load happened to construct us.
                    Error = $"{root}: {e.Message}";
                }
            }

            if (_fsws.Count > 0) Error = "";
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
            for (int i = 0; i < _buildDirs.Count; i++)
                if (full.StartsWith(_buildDirs[i], StringComparison.OrdinalIgnoreCase))
                    return false;

            string name = Path.GetFileName(full);
            if (string.Equals(name, "CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                return true;

            string ext = Path.GetExtension(full);
            return string.Equals(ext, ".c", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            foreach (var fsw in _fsws)
            {
                try
                {
                    fsw.EnableRaisingEvents = false;
                    fsw.Changed -= OnEvent;
                    fsw.Created -= OnEvent;
                    fsw.Renamed -= OnEvent;
                    fsw.Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CtrlBuild] Watcher dispose: {e.Message}");
                }
            }
            _fsws.Clear();
            _buildDirs.Clear();
        }
    }
}
