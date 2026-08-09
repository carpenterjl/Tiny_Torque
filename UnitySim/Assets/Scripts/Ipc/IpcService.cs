using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace AIHWSim.Ipc
{
    /// <summary>
    /// The named-pipe plumbing: two server pipes, four worker threads, and
    /// queues at both ends. Knows nothing about vehicles, messages or JSON — it
    /// moves lines of text in and out of the control pipe and byte frames out of
    /// the telemetry pipe, and that is all.
    ///
    /// Contains NO Unity API on purpose. Everything here runs off the main
    /// thread and the engine is not thread-safe; the same rule
    /// <c>ControllerBuildService</c> and <c>LanDiscovery</c> state in their own
    /// headers. Work reaches the engine when <see cref="IpcRuntime"/> drains
    /// these queues from <c>Update</c>. In particular there is no
    /// <c>Time.unscaledTime</c> in this file — timestamps are
    /// <c>Environment.TickCount</c>, which is thread-safe and only ever used for
    /// connect logging.
    ///
    /// <b>One client.</b> Both pipes are created with a single server instance,
    /// so Windows itself refuses a second connection with ERROR_PIPE_BUSY rather
    /// than this code having to arbitrate. A second control application sees its
    /// connect attempt fail, which is the honest answer: two processes both
    /// holding takeover of the same car has no sensible meaning.
    /// </summary>
    public sealed class IpcService
    {
        /// <summary>Frames queued for the telemetry pipe before the oldest start
        /// getting dropped. At 100 Hz this is ~2.5 s of backlog; a client that
        /// far behind is not going to catch up, and holding the frames would
        /// just turn a stall into unbounded memory growth. Drops are counted and
        /// reported, never silent.</summary>
        private const int MaxQueuedFrames = 256;

        /// <summary>Refuse a control line longer than this. A whole
        /// <c>VehicleDesign</c> is tens of kilobytes, so the cap is generous —
        /// it exists to stop a malformed client (or a desynchronised binary
        /// write into the wrong pipe) from growing the accumulator forever.</summary>
        private const int MaxLineBytes = 8 * 1024 * 1024;

        private struct Frame
        {
            public byte[] buf;
            public int len;
        }

        private readonly ConcurrentQueue<string> _inbound = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outControl = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<Frame> _outFrames = new ConcurrentQueue<Frame>();
        private readonly ConcurrentBag<byte[]> _pool = new ConcurrentBag<byte[]>();

        private readonly AutoResetEvent _controlWake = new AutoResetEvent(false);
        private readonly AutoResetEvent _frameWake = new AutoResetEvent(false);

        private NamedPipeServerStream _control;
        private NamedPipeServerStream _telemetry;
        private Thread _controlThread, _controlWriteThread, _telemetryThread;

        private volatile bool _stop;
        private volatile bool _controlConnected;
        private volatile bool _telemetryConnected;
        private int _connectEpoch;      // Interlocked; bumped on each new control client
        private int _queuedFrames;      // Interlocked
        private long _framesDropped;    // Interlocked
        private volatile string _lastError = "";

        /// <summary>True while a control application is connected. The telemetry
        /// pipe connects a moment later and is tracked separately — frames
        /// enqueued before it lands are dropped, not buffered, because a
        /// subscription's first few samples are worth less than a stale
        /// backlog.</summary>
        public bool ControlConnected => _controlConnected;
        public bool TelemetryConnected => _telemetryConnected;

        /// <summary>Increments every time a NEW control client connects. The main
        /// thread compares it against the epoch it last saw; a change means
        /// "reset everything" — this is how a reconnect is detected without the
        /// worker thread touching any session state.</summary>
        public int ConnectEpoch => Volatile.Read(ref _connectEpoch);

        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        /// <summary>Last pipe fault, for the log. Empty when nothing has gone wrong.</summary>
        public string LastError => _lastError;

        public bool TryDequeueInbound(out string line) => _inbound.TryDequeue(out line);

        // ---- lifecycle -------------------------------------------------------

        public void Start()
        {
            _stop = false;
            _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "IpcControl" };
            _controlWriteThread = new Thread(ControlWriteLoop) { IsBackground = true, Name = "IpcControlWrite" };
            _telemetryThread = new Thread(TelemetryLoop) { IsBackground = true, Name = "IpcTelemetry" };
            _controlThread.Start();
            _controlWriteThread.Start();
            _telemetryThread.Start();
        }

        /// <summary>
        /// Tear down. The reader threads are parked inside a blocking
        /// <c>WaitForConnection</c> or <c>Read</c>, which no flag can interrupt —
        /// disposing the stream out from under them is what unblocks it, and the
        /// loops swallow the resulting exception once <see cref="_stop"/> is set.
        /// That is <c>LanDiscovery</c>'s Close-then-catch shape.
        /// </summary>
        public void Stop()
        {
            _stop = true;
            _controlWake.Set();
            _frameWake.Set();

            try { _control?.Dispose(); } catch { /* racing the reader is the point */ }
            try { _telemetry?.Dispose(); } catch { }
            _control = null;
            _telemetry = null;

            // Bounded joins: a worker stuck in a kernel wait must not hang domain
            // reload or application quit. They are background threads, so a straggler
            // dies with the process either way.
            JoinBriefly(_controlThread);
            JoinBriefly(_controlWriteThread);
            JoinBriefly(_telemetryThread);
            _controlThread = _controlWriteThread = _telemetryThread = null;

            _controlConnected = false;
            _telemetryConnected = false;
            while (_inbound.TryDequeue(out _)) { }
            while (_outControl.TryDequeue(out _)) { }
            while (_outFrames.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queuedFrames, 0);
        }

        private static void JoinBriefly(Thread t)
        {
            try { t?.Join(500); } catch { }
        }

        // ---- sending ---------------------------------------------------------

        /// <summary>Queue one control line. Never blocks and never drops: control
        /// traffic is low-rate and a lost reply would strand a client waiting on
        /// a request id forever.</summary>
        public void SendLine(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            _outControl.Enqueue(json);
            _controlWake.Set();
        }

        /// <summary>
        /// Queue one binary frame. Takes ownership of <paramref name="buf"/> —
        /// it goes back to the pool after the write, so the caller must not
        /// touch it again. Rent buffers with <see cref="RentFrame"/>.
        /// </summary>
        public void SendFrame(byte[] buf, int len)
        {
            if (buf == null || len <= 0) return;
            if (!_telemetryConnected) { ReturnFrame(buf); return; }

            _outFrames.Enqueue(new Frame { buf = buf, len = len });
            if (Interlocked.Increment(ref _queuedFrames) > MaxQueuedFrames
                && _outFrames.TryDequeue(out var old))
            {
                Interlocked.Decrement(ref _queuedFrames);
                Interlocked.Increment(ref _framesDropped);
                ReturnFrame(old.buf);
            }
            _frameWake.Set();
        }

        /// <summary>A scratch buffer at least this big. Pooled because a camera
        /// subscription allocates 16 KB ten times a second and telemetry frames
        /// land every control tick — left to the GC that is a steady drip of
        /// collections underneath the physics loop.</summary>
        public byte[] RentFrame(int minSize)
        {
            while (_pool.TryTake(out var b))
                if (b.Length >= minSize) return b;
            return new byte[Math.Max(minSize, 256)];
        }

        private void ReturnFrame(byte[] buf)
        {
            // Unbounded pooling would hold every size ever asked for; 32 buffers
            // covers the queue depth a healthy client ever reaches.
            if (buf != null && _pool.Count < 32) _pool.Add(buf);
        }

        // ---- control pipe ----------------------------------------------------

        private void ControlLoop()
        {
            while (!_stop)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    // PipeOptions.Asynchronous is REQUIRED, not a tuning choice.
                    // Without it the OS handle is synchronous, and a synchronous
                    // handle serialises every operation on itself — so the writer
                    // thread's Write parks behind the reader thread's blocking
                    // Read, which only returns when the client sends something.
                    // The result is a server that can never answer a request: the
                    // reply sits in the queue until the client happens to send
                    // another message. The [IPC] gate caught exactly this.
                    pipe = new NamedPipeServerStream(
                        IpcProtocol.ControlPipeName, PipeDirection.InOut,
                        maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, inBufferSize: 64 * 1024, outBufferSize: 64 * 1024);
                    _control = pipe;
                    pipe.WaitForConnection();
                    if (_stop) break;

                    Interlocked.Increment(ref _connectEpoch);
                    _controlConnected = true;
                    ReadLines(pipe);
                }
                catch (Exception e)
                {
                    if (!_stop) _lastError = e.Message;
                }
                finally
                {
                    _controlConnected = false;
                    try { pipe?.Dispose(); } catch { }
                    if (ReferenceEquals(_control, pipe)) _control = null;
                }

                // A client that dropped may come straight back. Idle briefly so a
                // pipe that fails to create (name in use by a stale process) does
                // not spin a core.
                if (!_stop) Thread.Sleep(100);
            }
        }

        /// <summary>
        /// Accumulate bytes and cut lines on '\n'. Decoding is deferred until a
        /// whole line is in hand: a UTF-8 sequence can straddle two reads, and
        /// decoding each chunk as it arrives would corrupt any non-ASCII
        /// character that lands on the boundary.
        /// </summary>
        private void ReadLines(NamedPipeServerStream pipe)
        {
            var chunk = new byte[16 * 1024];
            var line = new MemoryStream(4096);

            while (!_stop && pipe.IsConnected)
            {
                int n = pipe.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;                    // clean EOF: client closed

                for (int i = 0; i < n; i++)
                {
                    byte b = chunk[i];
                    if (b == (byte)'\n')
                    {
                        if (line.Length > 0)
                        {
                            var bytes = line.ToArray();
                            // Tolerate CRLF even though the spec says LF: a hand-written
                            // client or a PowerShell StreamWriter emits CR and the
                            // resulting parse failure is baffling to debug.
                            int len = bytes.Length;
                            if (len > 0 && bytes[len - 1] == (byte)'\r') len--;
                            if (len > 0) _inbound.Enqueue(IpcProtocol.Utf8.GetString(bytes, 0, len));
                            line.SetLength(0);
                        }
                        continue;
                    }

                    if (line.Length >= MaxLineBytes)
                    {
                        _lastError = "control line exceeded the size cap; dropping the connection";
                        return;
                    }
                    line.WriteByte(b);
                }
            }
        }

        private void ControlWriteLoop()
        {
            while (!_stop)
            {
                if (!_controlConnected || !_outControl.TryDequeue(out var json))
                {
                    _controlWake.WaitOne(100);
                    continue;
                }

                var pipe = _control;
                if (pipe == null || !pipe.IsConnected) continue;   // queued reply outlived its client

                try
                {
                    var bytes = IpcProtocol.Utf8.GetBytes(json);
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.WriteByte((byte)'\n');
                    pipe.Flush();
                }
                catch (Exception e)
                {
                    if (!_stop) _lastError = e.Message;
                    // The reader thread owns the pipe's lifetime and will see the
                    // same fault; nothing to tear down here.
                }
            }
        }

        // ---- telemetry pipe --------------------------------------------------

        private void TelemetryLoop()
        {
            while (!_stop)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    // Asynchronous here too, and for a second reason on top of the
                    // one on the control pipe: only an overlapped handle's pending
                    // WaitForConnection is actually aborted by Dispose. On a
                    // synchronous handle the accept thread stays parked in the
                    // kernel with nobody ever connecting, Stop() cannot free it,
                    // and the process wedges on shutdown waiting for a thread that
                    // will never return.
                    pipe = new NamedPipeServerStream(
                        IpcProtocol.TelemetryPipeName, PipeDirection.Out,
                        maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 512 * 1024);
                    _telemetry = pipe;
                    pipe.WaitForConnection();
                    if (_stop) break;

                    _telemetryConnected = true;
                    WriteFrames(pipe);
                }
                catch (Exception e)
                {
                    if (!_stop) _lastError = e.Message;
                }
                finally
                {
                    _telemetryConnected = false;
                    try { pipe?.Dispose(); } catch { }
                    if (ReferenceEquals(_telemetry, pipe)) _telemetry = null;
                    DrainFrames();
                }

                if (!_stop) Thread.Sleep(100);
            }
        }

        private void WriteFrames(NamedPipeServerStream pipe)
        {
            while (!_stop && pipe.IsConnected)
            {
                if (!_outFrames.TryDequeue(out var f))
                {
                    _frameWake.WaitOne(50);
                    continue;
                }
                Interlocked.Decrement(ref _queuedFrames);

                try
                {
                    pipe.Write(f.buf, 0, f.len);
                    // No Flush per frame: a named pipe write is already a syscall
                    // that hands the bytes to the kernel, and flushing additionally
                    // blocks until the READER has drained them, which turns a slow
                    // client into a stalled telemetry thread.
                }
                catch (Exception e)
                {
                    if (!_stop) _lastError = e.Message;
                    ReturnFrame(f.buf);
                    return;
                }
                ReturnFrame(f.buf);
            }
        }

        private void DrainFrames()
        {
            while (_outFrames.TryDequeue(out var f))
            {
                Interlocked.Decrement(ref _queuedFrames);
                ReturnFrame(f.buf);
            }
        }
    }
}
