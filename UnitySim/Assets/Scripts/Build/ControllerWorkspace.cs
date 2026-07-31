using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace AIHWSim.Build
{
    /// <summary>
    /// Finds the <c>Controllers/</c> source tree — the folder holding
    /// <c>build.ps1</c> and <c>CMakeLists.txt</c> — so the game can rebuild the
    /// player's C controller without them leaving it.
    ///
    /// Deliberately NOT editor-only. In the editor the tree is always two levels
    /// above <c>Application.dataPath</c>; in a player build shipped beside the repo
    /// it may still be there, and a learner who has the sources has every reason to
    /// expect the button to work. When it is genuinely absent this reports a
    /// sentence saying so and the UI shows that instead of a button that does
    /// nothing — an unavailable feature that explains itself beats one that is
    /// silently compiled out on some platforms and not others.
    /// </summary>
    public static class ControllerWorkspace
    {
        /// <summary>How far up from the data folder to look. Editor hits on step 2
        /// (Assets -> UnitySim -> repo root); the extra depth covers a player build
        /// nested a folder or two deeper beside the sources.</summary>
        private const int ProbeDepth = 5;

        private static bool _resolved;
        private static string _root;
        private static string _reason = "";
        private static readonly List<string> _targets = new List<string>();

        /// <summary>Absolute path of the Controllers folder, or null.</summary>
        public static string Root { get { Resolve(); return _root; } }

        public static bool IsAvailable { get { Resolve(); return _root != null; } }

        /// <summary>Why <see cref="Root"/> is null — a sentence to show the user,
        /// not a code. Empty when the workspace was found.</summary>
        public static string UnavailableReason { get { Resolve(); return _reason; } }

        /// <summary>CMake target names scraped from CMakeLists.txt, in file order.</summary>
        public static IReadOnlyList<string> Targets { get { Resolve(); return _targets; } }

        public static string BuildScript =>
            Root == null ? null : Path.Combine(Root, "build.ps1");

        /// <summary>
        /// Where a built DLL must land: beside the RUNNING game, not wherever the
        /// sources were last compiled from. In the editor those are the same folder
        /// and this is a no-op — in a player build they are not, and without it the
        /// compiler cheerfully writes into the repo while the game keeps loading the
        /// DLL it shipped with.
        ///
        /// One definition, because two callers need it for opposite reasons: the
        /// build passes it to CMake, and the catalogue reads timestamps out of it.
        /// If they ever disagreed, the game would report a freshly built controller
        /// as stale forever.
        /// </summary>
        public static string PluginDir() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "Plugins", "x86_64"));

        /// <summary>Re-run the search — call after the user edits the path.</summary>
        public static void Invalidate()
        {
            _resolved = false;
            _root = null;
            _reason = "";
            _targets.Clear();
            // The player's own scripts hang off this root, so they go stale with it.
            UserScriptCatalog.Invalidate();
        }

        /// <summary>
        /// Re-derive the target list without re-resolving the workspace. For the
        /// case the whole feature exists to serve: a folder created under
        /// UserScripts/ while the game is running. Cheap enough to call when
        /// something noticed a change, not cheap enough to call every frame.
        ///
        /// Only ever call this from a Layout pass. It changes the CONTENTS of the
        /// build picker's list, which is census-safe (a Cycle is one control however
        /// many entries it holds) — but going from an empty list to a non-empty one
        /// adds a control, and doing that between a Layout pass and its Repaint is
        /// the one thing this UI must never do.
        /// </summary>
        public static void RefreshTargets()
        {
            if (!IsAvailable) return;
            ScrapeTargets();
        }

        /// <summary>
        /// Every folder a saved file should trigger a rebuild from: the game's own
        /// controller sources, and the player's. They are siblings, not nested, so
        /// one watcher cannot cover both — and a rebuild-on-save that silently
        /// ignores the folder the player is actually typing in would be worse than
        /// not offering the feature.
        /// </summary>
        public static string[] WatchRoots()
        {
            var list = new List<string>(2);
            if (Root != null) list.Add(Root);
            string user = UserScriptCatalog.Root;
            if (user != null) list.Add(user);
            return list.ToArray();
        }

        /// <summary>
        /// The CMake target that produces <paramref name="dllFileName"/>, or null if
        /// no such target exists. <c>add_controller</c> sets <c>PREFIX ""</c>, so the
        /// target name and the DLL stem are the same string — but this checks the
        /// scraped list rather than assuming, because "built something, loaded
        /// something else" is the single most confusing way this feature can fail.
        /// </summary>
        public static string TargetForDll(string dllFileName)
        {
            if (string.IsNullOrEmpty(dllFileName)) return null;
            Resolve();
            string stem = Path.GetFileNameWithoutExtension(dllFileName);
            for (int i = 0; i < _targets.Count; i++)
                if (string.Equals(_targets[i], stem, System.StringComparison.OrdinalIgnoreCase))
                    return _targets[i];
            return null;
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            // 1. An explicit override always wins — including when it is wrong, so
            //    that a user who set it and mistyped is told about THAT path rather
            //    than silently falling through to a working one somewhere else.
            string over = Persistence.SettingsStore.Current?.controllerWorkspace;
            if (!string.IsNullOrWhiteSpace(over))
            {
                if (IsWorkspace(over)) { Accept(over); return; }
                _reason = $"The controller folder setting points at \"{over}\", " +
                          "which has no build.ps1 + CMakeLists.txt pair.";
                return;
            }

            // 2. Walk up from the data folder.
            var dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < ProbeDepth && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Controllers");
                if (IsWorkspace(candidate)) { Accept(candidate); return; }
            }

            _reason = "No Controllers folder found near the game (looked for one " +
                      "holding both build.ps1 and CMakeLists.txt). Set the path " +
                      "below if the sources live somewhere else.";
        }

        /// <summary>Both files, because either alone is a plausible stray copy.</summary>
        private static bool IsWorkspace(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                return File.Exists(Path.Combine(dir, "build.ps1"))
                    && File.Exists(Path.Combine(dir, "CMakeLists.txt"));
            }
            catch { return false; }   // malformed path, permissions — same answer
        }

        private static void Accept(string dir)
        {
            _root = Path.GetFullPath(dir);
            _reason = "";
            ScrapeTargets();
        }

        /// <summary>
        /// Read the target list out of CMakeLists.txt rather than keeping a copy
        /// here. A hand-maintained enum rots the moment the user adds their own
        /// controller — which is the exact workflow this whole feature exists for.
        ///
        /// The player's own scripts come FIRST, and are not in CMakeLists.txt at all
        /// — the glob at the bottom of that file generates them, which the regex
        /// below cannot see. First because the build panel's picker starts at index
        /// 0, and on the screen built for writing your own controller, "your own
        /// controller" is the right thing to have already selected.
        ///
        /// Safe to reach into UserScriptCatalog from here even though it reads
        /// <see cref="Root"/>: Resolve set _resolved before calling Accept, and
        /// Accept set _root before calling this, so the re-entrant Resolve() returns
        /// immediately with the answer already in place.
        /// </summary>
        private static void ScrapeTargets()
        {
            _targets.Clear();

            UserScriptCatalog.Invalidate();
            foreach (var s in UserScriptCatalog.All)
                if (!_targets.Contains(s.name)) _targets.Add(s.name);

            try
            {
                string text = File.ReadAllText(Path.Combine(_root, "CMakeLists.txt"));
                foreach (Match m in Regex.Matches(text, @"^\s*add_controller\s*\(\s*([A-Za-z0-9_]+)",
                                                  RegexOptions.Multiline))
                    if (m.Groups[1].Success && !_targets.Contains(m.Groups[1].Value))
                        _targets.Add(m.Groups[1].Value);
            }
            catch (System.Exception e)
            {
                // A readable workspace with an unreadable CMakeLists is still worth
                // keeping: the user can build "everything" even with no target list.
                Debug.LogWarning($"[CtrlBuild] Could not read CMakeLists.txt: {e.Message}");
            }
        }
    }
}
