using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIHWSim.Build
{
    /// <summary>One folder under <c>UserScripts/</c> — a controller the player wrote.</summary>
    public sealed class UserScript
    {
        /// <summary>Folder name. Also the CMake target name and the DLL stem: the
        /// glob in Controllers/CMakeLists.txt names the target after the folder and
        /// <c>add_controller</c> sets <c>PREFIX ""</c>, so all three are one string.
        /// That identity is the whole reason "copy a folder" is the way to make a
        /// new controller.</summary>
        public string name;

        /// <summary>Absolute path of the folder.</summary>
        public string folder;

        /// <summary>The .c files CMake will compile — the same glob, evaluated here
        /// so the game can say "this folder has no source in it" before spending
        /// twenty seconds finding out from the compiler.</summary>
        public string[] sources = Array.Empty<string>();

        public string DllName => name + ".dll";
    }

    /// <summary>
    /// The player's own controllers: what is in <c>UserScripts/</c>, whether each
    /// one has been built, and whether what was built is still current.
    ///
    /// This deliberately re-implements the folder rules from
    /// <c>Controllers/CMakeLists.txt</c> rather than asking CMake, because the game
    /// has to answer "what can I offer to build?" before any build has ever run —
    /// on a fresh clone there is no CMake cache to interrogate. The two rule sets
    /// must agree: a folder this offers and CMake declines is a button that does
    /// nothing. They are cross-referenced in comments at both ends.
    /// </summary>
    public static class UserScriptCatalog
    {
        /// <summary>Sibling of <c>Controllers/</c>.</summary>
        public const string FolderName = "UserScripts";

        /// <summary>Shared helper header, not a controller — skipped by CMake too.</summary>
        public const string LibFolder = "lib";

        public const string GuideFile = "guide.html";

        /// <summary>The four the game looks up with GetProcAddress. A DLL missing
        /// one of these compiles and loads perfectly and then does nothing, which is
        /// the single most baffling way a first controller fails.</summary>
        private static readonly string[] RequiredExports =
            { "ctrl_init", "ctrl_step", "ctrl_shutdown", "ctrl_get_debug_names" };

        private static readonly List<UserScript> _all = new List<UserScript>();
        private static bool _scanned;

        public static IReadOnlyList<UserScript> All { get { Scan(); return _all; } }

        /// <summary>Re-scan on the next access. Call after a build, or after the
        /// workspace path changes.</summary>
        public static void Invalidate() => _scanned = false;

        /// <summary>Absolute path of the UserScripts folder, or null when there is
        /// no controller workspace to hang it off.</summary>
        public static string Root
        {
            get
            {
                string ctrl = ControllerWorkspace.Root;
                if (string.IsNullOrEmpty(ctrl)) return null;
                var parent = Directory.GetParent(ctrl);
                if (parent == null) return null;
                string dir = Path.Combine(parent.FullName, FolderName);
                return Directory.Exists(dir) ? dir : null;
            }
        }

        public static string GuidePath
        {
            get
            {
                string root = Root;
                if (root == null) return null;
                string p = Path.Combine(root, GuideFile);
                return File.Exists(p) ? p : null;
            }
        }

        /// <summary>Accepts a folder name, a target name or a file name with .dll on
        /// it — they are all the same string with different clothes on.</summary>
        public static UserScript Find(string nameOrDll)
        {
            if (string.IsNullOrEmpty(nameOrDll)) return null;
            string stem = Path.GetFileNameWithoutExtension(nameOrDll);
            Scan();
            for (int i = 0; i < _all.Count; i++)
                if (string.Equals(_all[i].name, stem, StringComparison.OrdinalIgnoreCase))
                    return _all[i];
            return null;
        }

        /// <summary>Same rule as the CMake glob's regex: a name that has to work as
        /// a CMake target and a Windows file name at once.</summary>
        public static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        /// <summary>Where the built DLL lands. Same answer the build itself passes to
        /// CMake — see <see cref="ControllerWorkspace.PluginDir"/>.</summary>
        public static string DllPath(UserScript s) =>
            s == null ? null : Path.Combine(ControllerWorkspace.PluginDir(), s.DllName);

        public static bool IsBuilt(UserScript s)
        {
            string p = DllPath(s);
            return p != null && File.Exists(p);
        }

        /// <summary>
        /// True when a source has been edited since the DLL was produced — i.e. what
        /// is about to drive is not what is on screen in the editor. Never built
        /// counts as stale.
        ///
        /// Includes <c>lib/</c>, because editing the shared helper header changes
        /// every controller and the one thing worse than a stale DLL is a stale DLL
        /// the game called current.
        /// </summary>
        public static bool IsStale(UserScript s)
        {
            if (s == null) return false;
            string dll = DllPath(s);
            if (dll == null || !File.Exists(dll)) return true;
            try
            {
                DateTime built = File.GetLastWriteTimeUtc(dll);
                return NewestWrite(s.folder) > built || NewestWrite(LibDir()) > built;
            }
            catch (Exception e)
            {
                // An unreadable timestamp is not worth crying "stale" over every
                // frame; say current and let the compiler be the authority.
                Debug.LogWarning($"[UserScripts] Could not compare timestamps: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Problems worth knowing about before spending a build on them.
        ///
        /// This is NOT a syntax checker — the compiler is, and its diagnostics land
        /// in the build pane. It only covers the two things a compiler cannot tell
        /// you: a folder with nothing in it to compile, and a controller that builds
        /// cleanly into a DLL the game then cannot get a grip on.
        /// </summary>
        /// <returns>false when <paramref name="errors"/> is non-empty, i.e. there is
        /// no point starting a build.</returns>
        public static bool Preflight(UserScript s, List<string> errors, List<string> warnings)
        {
            errors.Clear();
            warnings.Clear();
            if (s == null) return true;      // not a user script; nothing to check

            if (s.sources.Length == 0)
            {
                errors.Add($"{s.name}: no .c file in the folder — there is nothing to compile. " +
                           "(A .cpp will not do: these are compiled as C.)");
                return false;
            }

            string all;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in s.sources) sb.Append(File.ReadAllText(f)).Append('\n');
                all = sb.ToString();
            }
            catch (Exception e)
            {
                errors.Add($"{s.name}: could not read the sources ({e.Message})");
                return false;
            }

            // A plain substring test, and it says so: this catches "you have not
            // written it yet", not "you wrote it wrongly". A false pass costs
            // nothing beyond the message the loader prints anyway; a false FAIL
            // would block a build over a comment, so it stays a warning.
            foreach (var export in RequiredExports)
                if (all.IndexOf(export, StringComparison.Ordinal) < 0)
                    warnings.Add($"{s.name}: nothing named {export}() in the sources. " +
                                 "The DLL will build, but the game looks that name up by " +
                                 "hand — without it the car coasts.");

            foreach (var stray in Directory.GetFiles(s.folder, "*.cpp"))
                warnings.Add($"{s.name}: {Path.GetFileName(stray)} is ignored — " +
                             "only .c files are compiled. Rename it to .c.");

            return errors.Count == 0;
        }

        // ---- scanning --------------------------------------------------------

        private static string LibDir()
        {
            string root = Root;
            return root == null ? null : Path.Combine(root, LibFolder);
        }

        private static DateTime NewestWrite(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return DateTime.MinValue;
            DateTime newest = DateTime.MinValue;
            foreach (var f in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f);
                if (!ext.Equals(".c", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".h", StringComparison.OrdinalIgnoreCase)) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > newest) newest = t;
            }
            return newest;
        }

        private static void Scan()
        {
            if (_scanned) return;
            _scanned = true;
            _all.Clear();

            string root = Root;
            if (root == null) return;

            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (string.Equals(name, LibFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsValidName(name)) continue;      // CMake skips it too

                    var srcs = Directory.GetFiles(dir, "*.c");
                    // Listed even with no sources: the folder exists, the player made
                    // it, and Preflight has a much better sentence about why it will
                    // not build than a picker that silently omits it.
                    _all.Add(new UserScript { name = name, folder = dir, sources = srcs });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[UserScripts] Could not scan {root}: {e.Message}");
            }

            _all.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
