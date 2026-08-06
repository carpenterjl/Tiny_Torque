using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Copies the C controller workspace — <c>Controllers/</c> and
    /// <c>UserScripts/</c> — next to the built exe, so a shipped game can rebuild
    /// a controller instead of only running the DLLs it happened to be built with.
    ///
    /// Unity ships <c>Assets/Plugins/x86_64/*.dll</c> automatically; it ships no
    /// source at all. Without this the "Build &amp; Reload" button in a downloaded
    /// copy reports "No Controllers folder found near the game" and the whole
    /// write-your-own-firmware half of the product is missing. The reason it has
    /// looked fine so far is that <c>Builds/Release/</c> sits INSIDE the repo, so
    /// <see cref="AIHWSim.Build.ControllerWorkspace"/>'s upward probe reaches the
    /// real sources three folders up — an accident of where the build lands, which
    /// does not survive zipping the folder and sending it to someone.
    ///
    /// A post-build hook rather than a step inside <see cref="BuildMenu"/>, because
    /// the Build Settings window's own Build button is a real way to build this
    /// project and it does not go through that menu.
    ///
    /// <b>Not shipped: the compiler.</b> See <c>SHIPPED_README</c> below and the
    /// toolchain lookup at the top of <c>Controllers/build.ps1</c>.
    /// </summary>
    public sealed class ControllerSourceShipper : IPostprocessBuildWithReport
    {
        /// <summary>Late — after anything that might still be writing the folder.</summary>
        public int callbackOrder => 100;

        /// <summary>Folders copied whole, relative to the repo root.</summary>
        private static readonly string[] Payload = { "Controllers", "UserScripts" };

        /// <summary>
        /// Directory names never copied, matched on the leaf name at any depth.
        ///
        /// <c>build</c> is the CMake cache, and it is not merely bulk: a
        /// <c>CMakeCache.txt</c> records the absolute source and binary paths it
        /// was generated for, so shipping one hands every player a cache stamped
        /// with a path from this machine. build.ps1 recovers from that (it wipes
        /// and re-configures), but only after failing once with an error nobody
        /// should have to read.
        /// </summary>
        private static readonly HashSet<string> SkipDirs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build", ".vs", "__pycache__" };

        /// <summary>
        /// Extensions never copied. Build output, not source — and a stale DLL
        /// sitting in the source tree next to the game is exactly the kind of
        /// thing that gets loaded by mistake.
        /// </summary>
        private static readonly HashSet<string> SkipExt =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".dll", ".pdb", ".obj", ".ilk", ".exp", ".lib", ".a" };

        public void OnPostprocessBuild(BuildReport report)
        {
            // Tested for FAILURE, not for success: inside a postprocess callback
            // the result is still BuildResult.Unknown — Unity does not stamp it
            // Succeeded until every callback has run. Gating on Succeeded here
            // compiles, runs, and silently does nothing, every time.
            if (report.summary.result == BuildResult.Failed ||
                report.summary.result == BuildResult.Cancelled) return;
            if (report.summary.platform != BuildTarget.StandaloneWindows64 &&
                report.summary.platform != BuildTarget.StandaloneWindows) return;

            string dest = Path.GetDirectoryName(Path.GetFullPath(report.summary.outputPath));
            string repo = RepoRoot();
            if (dest == null || repo == null)
            {
                Debug.LogWarning("[CtrlShip] Could not locate the repo root — " +
                                 "the build has no controller sources beside it.");
                return;
            }

            int files = 0;
            foreach (var folder in Payload)
            {
                string from = Path.Combine(repo, folder);
                if (!Directory.Exists(from))
                {
                    Debug.LogWarning($"[CtrlShip] {folder}/ not found at {from} — skipped.");
                    continue;
                }
                string to = Path.Combine(dest, folder);
                // Mirror, not merge: a folder the player deleted from the sources
                // must not come back from the last build's copy.
                try { if (Directory.Exists(to)) Directory.Delete(to, true); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CtrlShip] Could not clear {to}: {e.Message}");
                }
                files += CopyTree(from, to);
            }

            File.WriteAllText(Path.Combine(dest, "BUILDING_CONTROLLERS.txt"), ShippedReadme);
            WarnIfNoPlugins(dest, report.summary.outputPath);

            Debug.Log($"[CtrlShip] Shipped {files} controller source files to {dest}");
        }

        /// <summary>
        /// Recursive copy honouring <see cref="SkipDirs"/> / <see cref="SkipExt"/>.
        /// Returns the number of files written.
        /// </summary>
        private static int CopyTree(string from, string to)
        {
            int n = 0;
            Directory.CreateDirectory(to);

            foreach (var f in Directory.GetFiles(from))
            {
                if (SkipExt.Contains(Path.GetExtension(f))) continue;
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
                n++;
            }
            foreach (var d in Directory.GetDirectories(from))
            {
                string leaf = Path.GetFileName(d);
                if (SkipDirs.Contains(leaf)) continue;
                n += CopyTree(d, Path.Combine(to, leaf));
            }
            return n;
        }

        /// <summary>
        /// The folder holding <c>Controllers/</c> and <c>UserScripts/</c>: two above
        /// <c>Assets</c>. Verified by looking for them rather than assumed, so a
        /// re-arranged project produces a warning instead of an empty copy.
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(Application.dataPath);   // <repo>/UnitySim/Assets
            for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "Controllers")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "UserScripts")))
                    return dir.FullName;
            return null;
        }

        /// <summary>
        /// A build whose Plugins folder is empty runs, boots, and offers an empty
        /// controller picker — the failure is entirely silent at build time and
        /// baffling at run time. The DLLs are git-ignored, so a fresh clone that
        /// has never run build.ps1 hits this every time.
        /// </summary>
        private static void WarnIfNoPlugins(string dest, string exePath)
        {
            string data = Path.Combine(dest,
                Path.GetFileNameWithoutExtension(exePath) + "_Data");
            string plugins = Path.Combine(data, "Plugins", "x86_64");
            // Burst emits lib_burst_generated.dll into the same folder, and it is
            // not a controller. Counting it would let a build with zero
            // controllers pass this check silently.
            int dlls = 0;
            if (Directory.Exists(plugins))
                foreach (var f in Directory.GetFiles(plugins, "*.dll"))
                    if (!Path.GetFileName(f).StartsWith("lib_burst",
                            StringComparison.OrdinalIgnoreCase)) dlls++;
            if (dlls == 0)
                Debug.LogWarning(
                    "[CtrlShip] The build contains no controller DLLs (looked in " +
                    $"{plugins}). Run Controllers/build.ps1 and build again, or the " +
                    "shipped game starts with nothing to run.");
            else
                Debug.Log($"[CtrlShip] {dlls} controller DLL(s) shipped in {plugins}");
        }

        /// <summary>
        /// Answers the one question the shipped sources cannot answer themselves:
        /// where the compiler is. It is not in the box, and the reason is licensing
        /// — see the file's own text.
        /// </summary>
        private const string ShippedReadme =
@"Writing your own controller
===========================

This game runs firmware you write in C. The sources are beside this file:

  Controllers/    the build (CMakeLists.txt + build.ps1), the shared libraries,
                  and hal/controller_api.h -- the API contract between your code
                  and the game. Worth reading once.
  UserScripts/    where YOUR controllers live. Start with guide.html (the game
                  can open it: Single Player -> Simulate Controller).

One folder under UserScripts/ = one controller = one DLL named after the folder.
Copy a folder to make a second one. There is no build file to edit.

In the game: Single Player -> Simulate Controller -> Build & Reload -> Run.


You need a C compiler. It is not bundled.
-----------------------------------------

The game shells out to a compiler you install; it does not ship one, because
neither of the usual choices can simply be put in this folder:

  * Microsoft's Visual C++ toolchain is free to download but its licence does
    not permit redistributing it inside another application.
  * GCC (mingw-w64) is GPLv3. Redistributing it is allowed, but only together
    with its licence texts and a corresponding-source offer, and it is about a
    gigabyte -- far more than the game itself.

Install either one. The smallest option, which brings gcc, cmake and ninja in
one package:

    winget install --id BrechtSanders.WinLibs.POSIX.UCRT --exact

Or Visual Studio Build Tools with the ""Desktop development with C++"" workload.
Either way it must be 64-bit; a 32-bit compiler produces a DLL the game cannot
load, and build.ps1 refuses it rather than letting that happen quietly.

If you would rather not install anything system-wide, unpack a WinLibs release
into a folder named Toolchain next to the game, so that this path exists:

    Toolchain\mingw64\bin\gcc.exe

build.ps1 looks there before it looks at your PATH.
";
    }
}
