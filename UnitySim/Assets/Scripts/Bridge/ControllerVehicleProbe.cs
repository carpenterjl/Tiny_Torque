using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AIHWSim.Bridge
{
    /// <summary>
    /// Asks a controller DLL which car it wants, without starting it.
    ///
    /// <c>ctrl_get_vehicle()</c> (ABI v5, optional) has to be answered BEFORE the
    /// car exists, because its answer is what the car is built from — so this
    /// cannot go through <see cref="NativeControllerLoader"/>, which exists to
    /// load a controller and run it. Nothing here calls <c>ctrl_init</c>,
    /// <c>ctrl_configure</c> or <c>ctrl_step</c>: the library is loaded, one
    /// constant is read out of it, and it is freed again. That is also why the
    /// header says the export must answer from a constant — at this point in the
    /// sequence there is genuinely nothing for it to consult.
    ///
    /// The three kernel32 imports are duplicated from
    /// <see cref="NativeControllerLoader"/> rather than shared, deliberately:
    /// sharing them would mean sharing the class, and a probe that can reach the
    /// loader's lifecycle is a probe that will eventually be asked to start
    /// something.
    /// </summary>
    public static class ControllerVehicleProbe
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private struct Cached
        {
            public DateTime stamp;
            public ControllerVehicle value;
        }

        // Keyed by full path. The menu asks this every time the DLL picker moves,
        // and loading a native library to answer a label is not something to do
        // per frame. Keyed on the build timestamp too, so a Build & Reload that
        // changes the answer is picked up without anyone having to remember to
        // invalidate anything.
        private static readonly Dictionary<string, Cached> _cache =
            new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What <paramref name="dllPath"/> asks for, or
        /// <see cref="ControllerVehicle.Menu"/> for every kind of "it did not
        /// say": no such file, a DLL that will not load, no such export (every
        /// controller written before ABI v5), or an export that returned 0.
        ///
        /// A number the host does not recognise comes back as-is rather than as
        /// Menu — the caller is the one that can name the DLL in the warning.
        /// </summary>
        public static ControllerVehicle Read(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
                return ControllerVehicle.Menu;

            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(dllPath); }
            catch { return ControllerVehicle.Menu; }

            if (_cache.TryGetValue(dllPath, out var hit) && hit.stamp == stamp)
                return hit.value;

            var v = Probe(dllPath);
            _cache[dllPath] = new Cached { stamp = stamp, value = v };
            return v;
        }

        private static ControllerVehicle Probe(string dllPath)
        {
            // Shadow-copied for the same reason the loader shadow-copies: holding
            // a handle on the real file makes the next Build & Reload fail to
            // overwrite it, and a probe has no business breaking a build.
            string shadow = null;
            IntPtr module = IntPtr.Zero;
            try
            {
                shadow = Path.Combine(Path.GetTempPath(),
                                      $"aihwsim_probe_{Guid.NewGuid():N}.dll");
                File.Copy(dllPath, shadow, overwrite: true);

                module = LoadLibrary(shadow);
                if (module == IntPtr.Zero)
                {
                    // Not an error here. The DLL is about to be loaded properly by
                    // the runner, which reports the failure with the context to
                    // explain it; saying it twice from a probe helps nobody.
                    return ControllerVehicle.Menu;
                }

                IntPtr addr = GetProcAddress(module, "ctrl_get_vehicle");
                if (addr == IntPtr.Zero) return ControllerVehicle.Menu;   // pre-v5

                var fn = Marshal.GetDelegateForFunctionPointer<CtrlGetVehicleDelegate>(addr);
                return (ControllerVehicle)fn();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ControllerVehicle] Could not read " +
                                 $"{Path.GetFileName(dllPath)}: {e.Message}");
                return ControllerVehicle.Menu;
            }
            finally
            {
                if (module != IntPtr.Zero) FreeLibrary(module);
                if (shadow != null)
                {
                    try { if (File.Exists(shadow)) File.Delete(shadow); }
                    catch { /* temp cleanup is best-effort */ }
                }
            }
        }
    }
}
