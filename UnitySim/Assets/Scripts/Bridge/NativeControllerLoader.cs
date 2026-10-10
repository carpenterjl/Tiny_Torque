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
    ///
    /// <see cref="LoadLinked"/> runs the firmware outside Unity instead —
    /// out of process, as a host-built image, or on a board — through the
    /// lockstep bridge (<see cref="ControllerLink"/>, HIL-01/04). The same
    /// delegates are bound to the link's calls, so the runner cannot tell
    /// the difference except by <see cref="Link"/>.
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
        private ControllerLink _link;

        /// <summary>The lockstep link, when the firmware runs outside Unity.</summary>
        public ControllerLink Link => _link;

        public CtrlInitDelegate Init { get; private set; }
        public CtrlStepDelegate Step { get; private set; }
        public CtrlShutdownDelegate Shutdown { get; private set; }
        public CtrlGetDebugNamesDelegate GetDebugNames { get; private set; }

        /// <summary>Optional ABI v2 export; null for pre-v2 controllers.</summary>
        public CtrlConfigureDelegate Configure { get; private set; }

        /// <summary>Optional v7 exports; null unless the DLL is a v7 controller.</summary>
        public CtrlConfigure2Delegate Configure2 { get; private set; }
        public CtrlResetDelegate Reset { get; private set; }
        public CtrlGetControlRateDelegate GetControlRate { get; private set; }
        public CtrlGetDebugExtDelegate GetDebugExt { get; private set; }

        /// <summary>
        /// The ABI the loaded DLL was built against: what ctrl_abi_version()
        /// returned, or 6 for a DLL that does not export it (every v2..v6
        /// controller is driven the same way). 7+ switches on the v7 behaviour:
        /// FLU frame, stamps, radians steer, configure2/reset.
        /// </summary>
        public int AbiVersion { get; private set; }
        public bool IsV7 => AbiVersion >= 7;

        public bool IsLoaded => _module != IntPtr.Zero || _link != null;

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

            if (!Handshake(dllPath)) { Unload(); return false; }

            Debug.Log($"[ControllerLoader] Loaded {Path.GetFileName(dllPath)} " +
                      $"(ABI v{AbiVersion}, built {LoadedStamp:HH:mm:ss} UTC)");
            return true;
        }

        /// <summary>
        /// ABI-03: a DLL that exports ctrl_abi_version() states which ABI it
        /// was compiled for and the struct sizes it was compiled with. A newer
        /// ABI than this host, or sizes that differ, would mean reading and
        /// writing memory at the wrong offsets — refuse instead.
        /// </summary>
        private unsafe bool Handshake(string dllPath)
        {
            var version = BindOptional<CtrlAbiVersionDelegate>("ctrl_abi_version");
            if (version == null) { AbiVersion = 6; return true; }

            int inSize = 0, outSize = 0;
            int v = version(&inSize, &outSize);
            int hostIn = Marshal.SizeOf<CtrlInputs>(), hostOut = Marshal.SizeOf<CtrlOutputs>();
            string name = Path.GetFileName(dllPath);
            if (v > ControllerAbi.Version)
            {
                Debug.LogError($"[ControllerLoader] {name} was built for ABI v{v}; this game " +
                               $"implements v{ControllerAbi.Version}. Update the game or rebuild " +
                               "against the controller_api.h it ships with.");
                return false;
            }
            if (v >= 7 && (inSize != hostIn || outSize != hostOut))
            {
                Debug.LogError($"[ControllerLoader] {name}: struct sizes differ from the host's " +
                               $"(CtrlInputs {inSize} vs {hostIn}, CtrlOutputs {outSize} vs {hostOut} " +
                               "bytes). Rebuild it against this game's controller_api.h.");
                return false;
            }
            AbiVersion = v < 7 ? 6 : v;
            if (AbiVersion >= 7)
            {
                Configure2 = BindOptional<CtrlConfigure2Delegate>("ctrl_configure2");
                Reset = BindOptional<CtrlResetDelegate>("ctrl_reset");
                GetControlRate = BindOptional<CtrlGetControlRateDelegate>("ctrl_get_control_rate");
                GetDebugExt = BindOptional<CtrlGetDebugExtDelegate>("ctrl_get_debug_ext");
            }
            return true;
        }

        /// <summary>
        /// Run the firmware that <paramref name="dllPath"/> names through the
        /// lockstep bridge instead of loading it here. <paramref name="projectRoot"/>
        /// is where the default tool folder (Native/) lives.
        /// </summary>
        public unsafe bool LoadLinked(string dllPath, ControllerLinkOptions options, string projectRoot)
        {
            Unload();
            var link = ControllerLink.Start(options, dllPath, projectRoot, out string error);
            if (link == null)
            {
                Debug.LogError($"[ControllerLoader] link {options}: {error}");
                return false;
            }
            _link = link;
            SourcePath = link.TargetPath;
            LoadedStamp = File.Exists(SourcePath) ? File.GetLastWriteTimeUtc(SourcePath) : DateTime.UtcNow;
            AbiVersion = ControllerAbi.Version;
            Init = link.Init;
            Step = link.Step;
            Shutdown = link.Shutdown;
            GetDebugNames = link.GetDebugNames;
            Configure2 = link.Configure2;
            Reset = link.Reset;
            GetControlRate = link.GetControlRate;
            GetDebugExt = link.GetDebugExt;
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
            Configure2 = null;
            Reset = null;
            GetControlRate = null;
            GetDebugExt = null;
            AbiVersion = 0;

            if (_link != null)
            {
                _link.Dispose();
                _link = null;
            }

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
