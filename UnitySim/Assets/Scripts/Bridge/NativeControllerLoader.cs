using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace AIHWSim.Bridge
{
    /// <summary>
    /// Loads controller.dll manually (LoadLibrary/GetProcAddress) instead of
    /// [DllImport], so the DLL can be rebuilt and hot-reloaded without
    /// restarting the Unity editor.
    ///
    /// The DLL is loaded from a per-load *shadow copy* in a temp folder; that
    /// leaves the original file writable, so build.ps1 can overwrite it while
    /// the editor holds a handle to the shadow.
    /// </summary>
    public sealed class NativeControllerLoader : IDisposable
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private IntPtr _module = IntPtr.Zero;
        private string _shadowPath;

        public CtrlInitDelegate Init { get; private set; }
        public CtrlStepDelegate Step { get; private set; }
        public CtrlShutdownDelegate Shutdown { get; private set; }
        public CtrlGetDebugNamesDelegate GetDebugNames { get; private set; }

        /// <summary>Optional ABI v2 export; null for pre-v2 controllers.</summary>
        public CtrlConfigureDelegate Configure { get; private set; }

        public bool IsLoaded => _module != IntPtr.Zero;

        /// <summary>Source path of the real DLL (in Assets/Plugins/x86_64).</summary>
        public string SourcePath { get; private set; }

        /// <summary>Last-write time of the source DLL when it was loaded.</summary>
        public DateTime LoadedStamp { get; private set; }

        public bool Load(string dllPath)
        {
            Unload();

            if (!File.Exists(dllPath))
            {
                Debug.LogError($"[ControllerLoader] DLL not found: {dllPath}");
                return false;
            }

            SourcePath = dllPath;
            LoadedStamp = File.GetLastWriteTimeUtc(dllPath);

            try
            {
                _shadowPath = Path.Combine(
                    Path.GetTempPath(),
                    $"aihwsim_controller_{Guid.NewGuid():N}.dll");
                File.Copy(dllPath, _shadowPath, overwrite: true);
            }
            catch (Exception e)
            {
                Debug.LogError($"[ControllerLoader] Failed to shadow-copy DLL: {e.Message}");
                return false;
            }

            _module = LoadLibrary(_shadowPath);
            if (_module == IntPtr.Zero)
            {
                Debug.LogError($"[ControllerLoader] LoadLibrary failed (err {Marshal.GetLastWin32Error()}) for {_shadowPath}");
                TryDeleteShadow();
                return false;
            }

            Init = Bind<CtrlInitDelegate>("ctrl_init");
            Step = Bind<CtrlStepDelegate>("ctrl_step");
            Shutdown = Bind<CtrlShutdownDelegate>("ctrl_shutdown");
            GetDebugNames = Bind<CtrlGetDebugNamesDelegate>("ctrl_get_debug_names");
            // Optional ABI v2 export — absent on older controllers, not an error.
            Configure = BindOptional<CtrlConfigureDelegate>("ctrl_configure");

            if (Init == null || Step == null || Shutdown == null || GetDebugNames == null)
            {
                Debug.LogError("[ControllerLoader] One or more exports missing; unloading.");
                Unload();
                return false;
            }

            Debug.Log($"[ControllerLoader] Loaded {Path.GetFileName(dllPath)} (built {LoadedStamp:HH:mm:ss} UTC)");
            return true;
        }

        /// <summary>True if the source DLL on disk is newer than what we loaded.</summary>
        public bool SourceIsNewer()
        {
            if (string.IsNullOrEmpty(SourcePath) || !File.Exists(SourcePath))
                return false;
            return File.GetLastWriteTimeUtc(SourcePath) > LoadedStamp;
        }

        /// <summary>Returns the comma-separated debug channel names, or empty array.</summary>
        public string[] ReadDebugNames()
        {
            if (GetDebugNames == null) return Array.Empty<string>();
            IntPtr p = GetDebugNames();
            if (p == IntPtr.Zero) return Array.Empty<string>();
            string csv = Marshal.PtrToStringAnsi(p) ?? string.Empty;
            if (csv.Length == 0) return Array.Empty<string>();
            return csv.Split(',');
        }

        public void Unload()
        {
            Init = null;
            Step = null;
            Shutdown = null;
            GetDebugNames = null;
            Configure = null;

            if (_module != IntPtr.Zero)
            {
                FreeLibrary(_module);
                _module = IntPtr.Zero;
            }
            TryDeleteShadow();
        }

        private T Bind<T>(string export) where T : Delegate
        {
            IntPtr addr = GetProcAddress(_module, export);
            if (addr == IntPtr.Zero)
            {
                Debug.LogError($"[ControllerLoader] Missing export '{export}'");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<T>(addr);
        }

        /// <summary>Bind an export that may be absent; returns null silently if missing.</summary>
        private T BindOptional<T>(string export) where T : Delegate
        {
            IntPtr addr = GetProcAddress(_module, export);
            return addr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(addr);
        }

        private void TryDeleteShadow()
        {
            if (string.IsNullOrEmpty(_shadowPath)) return;
            try { if (File.Exists(_shadowPath)) File.Delete(_shadowPath); }
            catch { /* temp file cleanup is best-effort */ }
            _shadowPath = null;
        }

        public void Dispose() => Unload();
    }
}
