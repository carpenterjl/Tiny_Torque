using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Debug = UnityEngine.Debug;

namespace AIHWSim.Bridge
{
    /// <summary>
    /// Where the firmware of a linked session runs (HIL-01/04). Parsed from one
    /// string: <c>dll</c> (the vehicle's controller DLL, out of process),
    /// <c>exe</c> (the same firmware built for the PC in external-tick mode —
    /// <c>opus_foc_controller.dll</c> becomes <c>opus_foc_pil.exe</c>),
    /// <c>serial:COM5</c> (a board in external-tick mode); <c>dll:</c> and
    /// <c>exe:</c> also take an explicit path.
    /// </summary>
    public sealed class ControllerLinkOptions
    {
        public string mode = "exe";
        public string target = "";
        /// <summary>How long the bridge waits for the firmware each tick. In
        /// lockstep the sim waits for the firmware, so this only has to catch
        /// one that has stopped.</summary>
        public int timeoutMs = 250;
        /// <summary>Wire capture for tools/tt_replay (exe / serial).</summary>
        public string record = "";
        /// <summary>Folder holding tt_bridge.exe and the PIL images; empty =
        /// &lt;project&gt;/Native, where the controller build puts them.</summary>
        public string toolDir = "";
        /// <summary>This many ticks in a row without an answer and the link is
        /// declared lost: the runner goes open-loop.</summary>
        public int maxMissedTicks = 20;

        public static ControllerLinkOptions Parse(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            var o = new ControllerLinkOptions();
            spec = spec.Trim();
            int colon = spec.IndexOf(':');
            // "exe:C:\x.exe" — the first colon splits; a drive letter's does not
            // matter because the mode comes first.
            o.mode = (colon > 0 ? spec.Substring(0, colon) : spec).ToLowerInvariant();
            o.target = colon > 0 ? spec.Substring(colon + 1) : "";
            if (o.mode != "dll" && o.mode != "exe" && o.mode != "serial")
                throw new ArgumentException($"link '{spec}': mode must be dll, exe or serial");
            if (o.mode == "serial" && o.target.Length == 0)
                throw new ArgumentException("link serial needs a port, e.g. serial:COM5");
            return o;
        }

        public override string ToString() =>
            mode + (target.Length > 0 ? ":" + target : "") + $" timeout={timeoutMs}ms";
    }

    /// <summary>
    /// The sim's end of the lockstep link (HIL-01). Starts the bridge process
    /// (Controllers/tools/tt_bridge.c), speaks its host-link protocol over the
    /// bridge's stdin / stdout, and presents the firmware as the same calls a
    /// controller DLL exports, so <see cref="NativeControllerLoader"/> can hand
    /// them to the runner unchanged.
    ///
    /// <see cref="Step"/> sends the tick's input block and BLOCKS until the
    /// outputs of that same tick come back — the sim waits for the firmware,
    /// however far away it is. If they do not come (the bridge reports that
    /// the firmware missed its deadline, or the bridge itself goes quiet),
    /// <see cref="LastStepMissed"/> is set: the firmware sent no command that
    /// tick, and the runner treats it as it treats a stalled MCU — the drives
    /// hold the last command until their own watchdog acts, and the log holds
    /// its last values. The reply that arrives late is dropped by its tick
    /// number. After <see cref="ControllerLinkOptions.maxMissedTicks"/> in a
    /// row, or if the bridge exits, the link throws, and the runner goes
    /// open-loop as it does for a DLL that faults.
    ///
    /// Bytes are read on a background thread into a queue; the main thread
    /// only waits on the queue, so a stalled pipe can never hang Unity past a
    /// timeout.
    /// </summary>
    public sealed class ControllerLink : IDisposable
    {
        public const uint Proto = 1;

        public string Mode { get; private set; }
        /// <summary>What the firmware says it is (the DLL path, or the INFO name).</summary>
        public string Firmware { get; private set; }
        /// <summary>The binary actually running: the DLL or the PIL image.</summary>
        public string TargetPath { get; private set; }
        public float WantedRate { get; private set; }

        public int Steps, Missed, FirmwareLate, NotReady, MissedInARow;
        public bool Dead { get; private set; }
        /// <summary>The last <see cref="Step"/> got no answer for its tick: no
        /// command frame, the outputs it wrote mean nothing.</summary>
        public bool LastStepMissed { get; private set; }

        private readonly ControllerLinkOptions _opt;
        private Process _proc;
        private Stream _toBridge;
        private Thread _reader;
        private readonly BlockingCollection<byte[]> _replies = new BlockingCollection<byte[]>();
        private readonly List<float> _rttUs = new List<float>();
        private readonly Stopwatch _sw = new Stopwatch();

        private byte[] _buf = new byte[4096];
        private int _n;
        private readonly float[] _ext = new float[64];
        private int _extN;
        private IntPtr _names = IntPtr.Zero;

        private ControllerLink(ControllerLinkOptions o) { _opt = o; }

        private int StepWaitMs => _opt.timeoutMs + 2000;
        private const int ControlWaitMs = 8000;

        // ------------------------------------------------------------ start --

        /// <summary>Start the bridge for the controller DLL at
        /// <paramref name="dllPath"/>. Null and a reason if it cannot run.</summary>
        public static ControllerLink Start(ControllerLinkOptions o, string dllPath, string projectRoot,
                                           out string error)
        {
            error = null;
            var link = new ControllerLink(o) { Mode = o.mode };
            string dir = string.IsNullOrEmpty(o.toolDir) ? Path.Combine(projectRoot, "Native") : o.toolDir;
            string bridge = Path.Combine(dir, "tt_bridge.exe");
            if (!File.Exists(bridge))
            {
                error = $"{bridge} not found: build the controllers (Controllers/build.ps1) first";
                return null;
            }

            string target = o.target;
            if (o.mode == "dll" && target.Length == 0) target = dllPath;
            if (o.mode == "exe" && target.Length == 0) target = Path.Combine(dir, PilName(dllPath));
            if (o.mode != "serial" && !File.Exists(target))
            {
                error = $"{target} not found";
                return null;
            }
            link.TargetPath = o.mode == "serial" ? dllPath : Path.GetFullPath(target);

            var args = new StringBuilder();
            args.Append("--").Append(o.mode).Append(" \"").Append(target).Append('"');
            args.Append(" --timeout-ms ").Append(o.timeoutMs);
            if (!string.IsNullOrEmpty(o.record)) args.Append(" --record \"").Append(o.record).Append('"');

            var psi = new ProcessStartInfo(bridge, args.ToString())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            try
            {
                link._proc = Process.Start(psi);
            }
            catch (Exception e)
            {
                error = $"cannot start {bridge}: {e.Message}";
                return null;
            }
            link._proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Debug.Log("[tt_bridge] " + e.Data); };
            link._proc.BeginErrorReadLine();
            link._toBridge = link._proc.StandardInput.BaseStream;
            link._reader = new Thread(link.ReadLoop) { IsBackground = true, Name = "tt_bridge reader" };
            link._reader.Start();

            // The bridge introduces itself first.
            byte[] hello = link.Wait('h', ControlWaitMs);
            if (hello == null) { error = "the bridge did not start"; link.Dispose(); return null; }
            int p = 1;
            uint proto = U32(hello, ref p);
            int sInfo = (int)U32(hello, ref p), sIn = (int)U32(hello, ref p), sOut = (int)U32(hello, ref p);
            link.WantedRate = F32(hello, ref p);
            string mode = Str(hello, ref p), fw = Str(hello, ref p);
            bool ok = U32(hello, ref p) != 0;
            if (!ok) { error = $"bridge ({mode}): {fw}"; link.Dispose(); return null; }
            if (proto != Proto || sInfo != Marshal.SizeOf<SensorInfo2>() ||
                sIn != Marshal.SizeOf<CtrlInputs>() || sOut != Marshal.SizeOf<CtrlOutputs>())
            {
                error = $"bridge speaks link v{proto} with struct sizes {sInfo}/{sIn}/{sOut}; " +
                        $"this game v{Proto}, {Marshal.SizeOf<SensorInfo2>()}/{Marshal.SizeOf<CtrlInputs>()}/" +
                        $"{Marshal.SizeOf<CtrlOutputs>()}. Rebuild the controllers.";
                link.Dispose();
                return null;
            }
            link.Firmware = fw;
            Debug.Log($"[ControllerLink] {o} -> {fw} ({link.TargetPath})");
            return link;
        }

        /// <summary>opus_foc_controller.dll -> opus_foc_pil.exe.</summary>
        public static string PilName(string dllPath)
        {
            string n = Path.GetFileNameWithoutExtension(dllPath ?? "");
            if (n.EndsWith("_controller", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - "_controller".Length);
            return n + "_pil.exe";
        }

        // ------------------------------------------------------------- calls --

        public int Init(float rateHz)
        {
            Begin('I');
            PutF32(rateHz);
            byte[] r = Request('i', ControlWaitMs);
            if (r == null) return -1;
            int p = 1;
            int rc = (int)U32(r, ref p);
            string names = Str(r, ref p);
            if (_names != IntPtr.Zero) Marshal.FreeHGlobal(_names);
            _names = Marshal.StringToHGlobalAnsi(names);
            return rc;
        }

        public IntPtr GetDebugNames() => _names;

        public float GetControlRate() => WantedRate;

        public unsafe void Configure2(SensorInfo2* sensors, int count)
        {
            Begin('C');
            PutU32((uint)count);
            int size = Marshal.SizeOf<SensorInfo2>();
            Ensure(count * size);
            for (int i = 0; i < count; i++)
                fixed (byte* dst = &_buf[_n])
                {
                    Buffer.MemoryCopy(&sensors[i], dst, size, size);
                    _n += size;
                }
            Status('c');
        }

        public void Reset() { Begin('R'); Status('r'); }

        public void Shutdown()
        {
            if (Dead) return;
            Begin('X');
            Status('x');
        }

        private void Status(char reply)
        {
            byte[] r = Request(reply, ControlWaitMs);
            int p = 1;
            int st = r != null ? (int)U32(r, ref p) : -1;
            if (st != 0) Debug.LogWarning($"[ControllerLink] '{reply}' returned {st}");
        }

        public unsafe void Step(CtrlInputs* input, CtrlOutputs* output)
        {
            *output = default;
            LastStepMissed = false;
            if (Dead) throw new InvalidOperationException("the controller link is lost");

            uint tick = input->tick;
            Begin('S');
            PutU32(tick);
            PutU32(input->flags);
            PutU32((uint)(input->time_us & 0xFFFFFFFFu));
            PutU32((uint)(input->time_us >> 32));
            PutF32(input->time_s);
            PutF32(input->dt_s);
            for (int i = 0; i < 3; i++) PutF32(input->gyro[i]);
            for (int i = 0; i < 3; i++) PutF32(input->accel[i]);
            for (int i = 0; i < 4; i++) PutF32(input->wheel_vel[i]);
            for (int i = 0; i < 4; i++) PutF32(input->setpoint[i]);
            int n = input->sensor_data != null ? input->sensor_data_len : 0;
            PutU32((uint)input->sensor_count);
            PutU32((uint)n);
            Ensure(n * 4 + 1 + input->sensor_count * 8);
            for (int i = 0; i < n; i++) PutF32(input->sensor_data[i]);
            bool stamps = input->stamps != null;
            _buf[_n++] = (byte)(stamps ? 1 : 0);
            if (stamps)
                for (int i = 0; i < input->sensor_count; i++)
                {
                    PutU32(input->stamps[i].seq);
                    PutU32(input->stamps[i].t_sample_us);
                }

            _sw.Restart();
            Send();
            Steps++;

            // The answer for THIS tick; an older one that arrives late is dropped.
            byte[] r;
            for (;;)
            {
                r = Wait('s', StepWaitMs);
                if (r == null) break;
                int q = 1;
                if (U32(r, ref q) == tick) break;
            }
            double us = _sw.Elapsed.TotalMilliseconds * 1000.0;

            if (r == null)
            {
                Miss($"no answer from the bridge in {StepWaitMs} ms");
                return;
            }
            int p = 5;
            byte status = r[p++];
            if (status != 0)
            {
                if (status == 2) NotReady++;
                if (status == 1) FirmwareLate++;
                if (status == 3) { Dead = true; throw new InvalidOperationException("the firmware is gone"); }
                Miss(status == 1 ? $"the firmware missed tick {tick}" : $"the firmware was not ready at tick {tick}");
                return;
            }
            MissedInARow = 0;
            _rttUs.Add((float)us);
            _extN = 0;
            for (int i = 0; i < 8; i++) output->actuator[i] = F32(r, ref p);
            for (int i = 0; i < 16; i++) output->debug[i] = F32(r, ref p);
            int ne = r[p] | (r[p + 1] << 8);
            p += 2;
            _extN = Math.Min(ne, _ext.Length);
            for (int i = 0; i < _extN; i++) _ext[i] = F32(r, ref p);
        }

        private void Miss(string why)
        {
            LastStepMissed = true;
            Missed++;
            MissedInARow++;
            if (Missed <= 5) Debug.LogWarning($"[ControllerLink] {why}: no command this tick");
            if (MissedInARow >= _opt.maxMissedTicks)
            {
                Dead = true;
                throw new InvalidOperationException($"the controller link is lost ({MissedInARow} ticks without an answer)");
            }
        }

        public unsafe int GetDebugExt(float* dst, int max)
        {
            int k = Math.Min(max, _extN);
            for (int i = 0; i < k; i++) dst[i] = _ext[i];
            return k;
        }

        /// <summary>One line: this side's round trip and counters, then the
        /// bridge's own (the firmware's round trip, wire errors).</summary>
        public string Stats()
        {
            string bridge = "";
            if (!Dead && _proc != null && !_proc.HasExited)
            {
                Begin('T');
                byte[] r = Request('t', ControlWaitMs);
                if (r != null) { int p = 1; bridge = Str(r, ref p); }
            }
            var s = new List<float>(_rttUs);
            s.Sort();
            float P(double q) => s.Count == 0 ? 0f : s[(int)Math.Round(q * (s.Count - 1))];
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(ci,
                "link={0} steps={1} missed={2} late={3} not_ready={4} sim_rtt_p50_us={5:0.0} " +
                "sim_rtt_p99_us={6:0.0} sim_rtt_max_us={7:0.0} | {8}",
                _opt, Steps, Missed, FirmwareLate, NotReady, P(0.5), P(0.99),
                s.Count > 0 ? s[s.Count - 1] : 0f, bridge);
        }

        // ----------------------------------------------------- the host link --

        private void ReadLoop()
        {
            try
            {
                var s = _proc.StandardOutput.BaseStream;
                var head = new byte[4];
                for (;;)
                {
                    if (!ReadExact(s, head, 4)) break;
                    int len = head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24);
                    if (len <= 0 || len > (64 << 20)) break;
                    var msg = new byte[len];
                    if (!ReadExact(s, msg, len)) break;
                    _replies.Add(msg);
                }
            }
            catch (Exception) { /* the process went away */ }
            Dead = true;
            _replies.CompleteAdding();
        }

        private static bool ReadExact(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(b, got, n - got);
                if (k <= 0) return false;
                got += k;
            }
            return true;
        }

        private byte[] Request(char reply, int ms)
        {
            if (Dead) return null;
            Send();
            return Wait(reply, ms);
        }

        /// <summary>The next message of type <paramref name="type"/>; others are dropped.</summary>
        private byte[] Wait(char type, int ms)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ms);
            for (;;)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                byte[] m;
                try
                {
                    if (!_replies.TryTake(out m, left)) return null;
                }
                catch (InvalidOperationException) { return null; }   // completed: the bridge exited
                if (m.Length > 0 && m[0] == (byte)type) return m;
            }
        }

        private void Begin(char type)
        {
            _n = 4;                 // length, filled in by Send
            Ensure(1);
            _buf[_n++] = (byte)type;
        }

        private void Send()
        {
            int len = _n - 4;
            _buf[0] = (byte)len; _buf[1] = (byte)(len >> 8); _buf[2] = (byte)(len >> 16); _buf[3] = (byte)(len >> 24);
            try
            {
                _toBridge.Write(_buf, 0, _n);
                _toBridge.Flush();
            }
            catch (Exception e)
            {
                Dead = true;
                throw new InvalidOperationException("the bridge went away: " + e.Message);
            }
        }

        private void Ensure(int more)
        {
            if (_n + more <= _buf.Length) return;
            int cap = _buf.Length;
            while (cap < _n + more) cap *= 2;
            Array.Resize(ref _buf, cap);
        }

        private void PutU32(uint v)
        {
            Ensure(4);
            _buf[_n++] = (byte)v; _buf[_n++] = (byte)(v >> 8); _buf[_n++] = (byte)(v >> 16); _buf[_n++] = (byte)(v >> 24);
        }

        private unsafe void PutF32(float f) => PutU32(*(uint*)&f);

        private static uint U32(byte[] b, ref int p)
        {
            uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
            p += 4;
            return v;
        }

        private static unsafe float F32(byte[] b, ref int p)
        {
            uint u = U32(b, ref p);
            return *(float*)&u;
        }

        private static string Str(byte[] b, ref int p)
        {
            int n = b[p] | (b[p + 1] << 8);
            p += 2;
            string s = Encoding.ASCII.GetString(b, p, n);
            p += n;
            return s;
        }

        public void Dispose()
        {
            Dead = true;
            try { _toBridge?.Close(); } catch { /* already gone */ }
            if (_proc != null)
            {
                try { if (!_proc.WaitForExit(3000)) _proc.Kill(); } catch { /* already gone */ }
                _proc.Dispose();
                _proc = null;
            }
            if (_names != IntPtr.Zero) { Marshal.FreeHGlobal(_names); _names = IntPtr.Zero; }
        }
    }
}
