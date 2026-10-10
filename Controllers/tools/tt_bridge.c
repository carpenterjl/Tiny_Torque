/*
 * tt_bridge.c — the lockstep bridge between the sim and a firmware outside
 * Unity's process (HIL-01, HIL-04).
 *
 * Unity starts this as a child process and talks to it over its stdin and
 * stdout (the "host link", below). Every control tick Unity sends the ABI
 * input block and WAITS for the outputs of that same tick, so the firmware
 * is in lockstep with the sim however long it takes. What the bridge does
 * with a tick depends on where the firmware is:
 *
 *   --dll <controller.dll>   load a v7 controller DLL here and call it: the
 *                            DLL's own run, out of Unity's process (a crash
 *                            takes down the bridge, not the editor).
 *   --exe <firmware.exe>     the firmware built for the PC in external-tick
 *                            mode (targets/hil/pil_main.c), as a child
 *                            process; the wire protocol over its pipes.
 *   --serial <COMn>          a board in external-tick mode on a serial port
 *                            (USB CDC): the wire protocol over the port.
 *
 * In the last two the bridge plays the board's HAL with the same adapter the
 * DLL uses (targets/sim/sim_hal.c): ABI block -> TtMeas -> MEAS frame ->
 * firmware -> ACT frame -> TtAct -> actuator slots. Only the wire is
 * between them, so a run through --exe reproduces the DLL's run exactly,
 * and a board that does not is wrong (core/tt_wire.h has the protocol).
 *
 * Options:
 *   --timeout-ms N    how long to wait for the firmware each tick (default
 *                     250). In lockstep the sim waits for the firmware, so
 *                     this only has to catch a firmware that has stopped:
 *                     a PC answers in ~15 us but the OS can hold a process
 *                     off for 25 ms now and then. A tick that runs out is
 *                     answered with zero outputs and counted; its late reply
 *                     is discarded.
 *   --init-timeout-ms N   the same for INIT / CONFIG (default 3000: a board
 *                     may still be enumerating).
 *   --baud N          serial baud (default 2000000; USB CDC ignores it).
 *   --record <file>   write every wire frame, both ways, to <file>: a valid
 *                     wire stream that tools/tt_replay.c replays (VAL-03).
 *   --bench N         no Unity: ping the firmware N times, print the round
 *                     trip (p50 / p99 / max) and exit.
 *
 * Statistics (ticks, timeouts, late replies, round trip p50 / p99 / max,
 * wire errors) go to stderr at exit and to Unity on request.
 *
 * HOST LINK (Unity <-> bridge), little-endian. Every message is
 *     u32 len | u8 type | payload[len - 1]
 * Bridge -> Unity first, unsolicited:
 *   'h'  u32 proto(1), i32 sizeof SensorInfo2, CtrlInputs, CtrlOutputs,
 *        f32 wanted rate, str mode, str firmware, i32 ok
 * Requests (Unity -> bridge) and replies:
 *   'I' f32 rate                         -> 'i' i32 rc, str debug names
 *   'C' i32 n, n x SensorInfo2 (raw)     -> 'c' i32 status
 *   'R'                                  -> 'r' i32 status
 *   'X'                                  -> 'x' i32 status
 *   'S' u32 tick, u32 flags, u64 time_us, f32 time_s, f32 dt_s, f32 gyro[3],
 *       f32 accel[3], f32 wheel_vel[4], f32 setpoint[4], i32 sensor_count,
 *       i32 data_len, f32 data[data_len], u8 has_stamps,
 *       {u32 seq, u32 t_us} x sensor_count (if has_stamps)
 *                                        -> 's' u32 tick, u8 status,
 *                                           f32 actuator[8], f32 debug[16],
 *                                           u16 n_ext, f32 ext[n_ext]
 *   'T'                                  -> 't' str statistics
 * (str = u16 length, bytes.) Step status: 0 ok, 1 the firmware did not
 * answer in time, 2 the firmware is not initialised, 3 the firmware is gone.
 * The camera frame is not forwarded.
 *
 * Windows only for now (Unity runs there); the protocol and everything
 * under it are portable.
 */
#ifndef _WIN32
#error "tt_bridge: Windows only for now"
#endif

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "controller_api.h"
#include "sim_hal.h"
#include "tt_wire.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LINK_PROTO 1u

enum { MODE_NONE = 0, MODE_DLL, MODE_EXE, MODE_SERIAL };
enum { ST_OK = 0, ST_TIMEOUT = 1, ST_NOT_READY = 2, ST_GONE = 3 };

static int         g_mode = MODE_NONE;
static const char *g_target = 0;
static int         g_timeout_ms = 250;
static int         g_init_timeout_ms = 3000;
static long        g_baud = 2000000;
static FILE       *g_rec = 0;

/* ------------------------------------------------------------- timing --- */

static LARGE_INTEGER g_qpf;
static double now_us(void)
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return (double)t.QuadPart * 1e6 / (double)g_qpf.QuadPart;
}

typedef struct { float *v; size_t n, cap; double max; } Samples;
static Samples g_rtt;

static void sample(Samples *s, double us)
{
    if (s->n == s->cap) {
        size_t cap = s->cap ? s->cap * 2u : 4096u;
        float *nv = (float *)realloc(s->v, cap * sizeof(float));
        if (!nv) return;
        s->v = nv;
        s->cap = cap;
    }
    s->v[s->n++] = (float)us;
    if (us > s->max) s->max = us;
}

static int cmp_f(const void *a, const void *b)
{
    float x = *(const float *)a, y = *(const float *)b;
    return x < y ? -1 : (x > y ? 1 : 0);
}

static double pct(Samples *s, double p)
{
    size_t k;
    if (s->n == 0) return 0.0;
    qsort(s->v, s->n, sizeof(float), cmp_f);
    k = (size_t)(p * (double)(s->n - 1) + 0.5);
    return s->v[k];
}

/* ------------------------------------------------- counters / statistics --- */

static struct {
    unsigned steps, timeouts, late, not_ready, faults, gone;
} g_n;

/* ---------------------------------------------------- the host link (Unity) */

static HANDLE g_in, g_out;

static int up_read_exact(void *p, DWORD n)
{
    uint8_t *b = (uint8_t *)p;
    while (n > 0) {
        DWORD got = 0;
        if (!ReadFile(g_in, b, n, &got, 0) || got == 0) return -1;
        b += got;
        n -= got;
    }
    return 0;
}

typedef struct { uint8_t *p; size_t n, cap; } Buf;

static void b_raw(Buf *b, const void *p, size_t n)
{
    if (b->n + n > b->cap) {
        size_t cap = b->cap ? b->cap : 256u;
        uint8_t *np;
        while (cap < b->n + n) cap *= 2u;
        np = (uint8_t *)realloc(b->p, cap);
        if (!np) { fprintf(stderr, "tt_bridge: out of memory\n"); exit(2); }
        b->p = np;
        b->cap = cap;
    }
    memcpy(b->p + b->n, p, n);
    b->n += n;
}
static void b_u8(Buf *b, uint8_t v) { b_raw(b, &v, 1); }
static void b_u16(Buf *b, uint16_t v) { uint8_t x[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; b_raw(b, x, 2); }
static void b_u32(Buf *b, uint32_t v)
{
    uint8_t x[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) };
    b_raw(b, x, 4);
}
static void b_i32(Buf *b, int32_t v) { b_u32(b, (uint32_t)v); }
static void b_f32(Buf *b, float f) { uint32_t u; memcpy(&u, &f, 4); b_u32(b, u); }
static void b_str(Buf *b, const char *s)
{
    size_t n = s ? strlen(s) : 0u;
    if (n > 65535u) n = 65535u;
    b_u16(b, (uint16_t)n);
    if (n) b_raw(b, s, n);
}

/* Send one host-link message: u32 len | u8 type | payload. */
static void up_send(uint8_t type, const Buf *payload)
{
    static Buf msg;
    size_t n = payload ? payload->n : 0u;
    DWORD w;
    msg.n = 0;
    b_u32(&msg, (uint32_t)(n + 1u));
    b_u8(&msg, type);
    if (n) b_raw(&msg, payload->p, n);
    if (!WriteFile(g_out, msg.p, (DWORD)msg.n, &w, 0) || w != msg.n) {
        fprintf(stderr, "tt_bridge: Unity went away\n");
        exit(0);
    }
}

static void up_status(uint8_t type, int32_t status)
{
    Buf b = { 0, 0, 0 };
    b_i32(&b, status);
    up_send(type, &b);
    free(b.p);
}

/* Reader over a received payload. */
typedef struct { const uint8_t *p; size_t n, i; int err; } Rd;
static const uint8_t *rd_take(Rd *r, size_t n)
{
    const uint8_t *q;
    if (r->i + n > r->n) { r->err = 1; return 0; }
    q = r->p + r->i;
    r->i += n;
    return q;
}
static uint32_t rd_u32(Rd *r)
{
    const uint8_t *q = rd_take(r, 4);
    return q ? (uint32_t)q[0] | ((uint32_t)q[1] << 8) | ((uint32_t)q[2] << 16) | ((uint32_t)q[3] << 24) : 0u;
}
static uint64_t rd_u64(Rd *r) { uint64_t lo = rd_u32(r); return lo | ((uint64_t)rd_u32(r) << 32); }
static float rd_f32(Rd *r) { uint32_t u = rd_u32(r); float f; memcpy(&f, &u, 4); return f; }
static uint8_t rd_u8(Rd *r) { const uint8_t *q = rd_take(r, 1); return q ? q[0] : 0u; }

/* ------------------------------------------------- the DLL (--dll) --- */

typedef int  (*FnAbi)(int *, int *);
typedef int  (*FnInit)(float);
typedef void (*FnStep)(const CtrlInputs *, CtrlOutputs *);
typedef void (*FnVoid)(void);
typedef const char *(*FnNames)(void);
typedef void (*FnConf2)(const SensorInfo2 *, int);
typedef float (*FnRate)(void);
typedef int  (*FnExt)(float *, int);

static struct {
    HMODULE m;
    FnInit init; FnStep step; FnVoid shutdown, reset; FnNames names;
    FnConf2 configure2; FnRate rate; FnExt ext;
} g_dll;

static int dll_open(const char *path, char *why, size_t whyn)
{
    FnAbi abi;
    int si = 0, so = 0, v;
    g_dll.m = LoadLibraryA(path);
    if (!g_dll.m) { snprintf(why, whyn, "LoadLibrary failed (%lu)", GetLastError()); return -1; }
    abi = (FnAbi)(void *)GetProcAddress(g_dll.m, "ctrl_abi_version");
    if (!abi) { snprintf(why, whyn, "not a v7 controller (no ctrl_abi_version)"); return -1; }
    v = abi(&si, &so);
    if (v != CTRL_ABI_VERSION || si != (int)sizeof(CtrlInputs) || so != (int)sizeof(CtrlOutputs)) {
        snprintf(why, whyn, "ABI v%d, sizes %d/%d; the bridge is v%d, %d/%d",
                 v, si, so, CTRL_ABI_VERSION, (int)sizeof(CtrlInputs), (int)sizeof(CtrlOutputs));
        return -1;
    }
    g_dll.init = (FnInit)(void *)GetProcAddress(g_dll.m, "ctrl_init");
    g_dll.step = (FnStep)(void *)GetProcAddress(g_dll.m, "ctrl_step");
    g_dll.shutdown = (FnVoid)(void *)GetProcAddress(g_dll.m, "ctrl_shutdown");
    g_dll.names = (FnNames)(void *)GetProcAddress(g_dll.m, "ctrl_get_debug_names");
    g_dll.reset = (FnVoid)(void *)GetProcAddress(g_dll.m, "ctrl_reset");
    g_dll.configure2 = (FnConf2)(void *)GetProcAddress(g_dll.m, "ctrl_configure2");
    g_dll.rate = (FnRate)(void *)GetProcAddress(g_dll.m, "ctrl_get_control_rate");
    g_dll.ext = (FnExt)(void *)GetProcAddress(g_dll.m, "ctrl_get_debug_ext");
    if (!g_dll.init || !g_dll.step || !g_dll.shutdown || !g_dll.names) {
        snprintf(why, whyn, "a required export is missing");
        return -1;
    }
    return 0;
}

/* -------------------------------------- the device byte stream (wire) --- */

#define RING (1u << 16)

static struct {
    HANDLE rd, wr, proc, thread;
    int serial;
    CRITICAL_SECTION cs;
    CONDITION_VARIABLE cv;
    uint8_t ring[RING];
    size_t head, tail;            /* tail = read position */
    int closed;
    unsigned dropped;
} g_dev;

static DWORD WINAPI dev_reader(LPVOID arg)
{
    uint8_t tmp[4096];
    OVERLAPPED ov;
    (void)arg;
    memset(&ov, 0, sizeof(ov));
    if (g_dev.serial) ov.hEvent = CreateEventA(0, TRUE, FALSE, 0);
    for (;;) {
        DWORD got = 0;
        BOOL ok;
        if (g_dev.serial) {
            ResetEvent(ov.hEvent);
            ok = ReadFile(g_dev.rd, tmp, sizeof(tmp), &got, &ov);
            if (!ok && GetLastError() == ERROR_IO_PENDING)
                ok = GetOverlappedResult(g_dev.rd, &ov, &got, TRUE);
            if (ok && got == 0) continue;            /* serial read timeout */
        } else {
            ok = ReadFile(g_dev.rd, tmp, sizeof(tmp), &got, 0);
            if (ok && got == 0) ok = FALSE;          /* pipe closed */
        }
        EnterCriticalSection(&g_dev.cs);
        if (!ok) {
            g_dev.closed = 1;
        } else {
            DWORD k;
            for (k = 0; k < got; k++) {
                size_t nh = (g_dev.head + 1u) % RING;
                if (nh == g_dev.tail) { g_dev.dropped++; break; }
                g_dev.ring[g_dev.head] = tmp[k];
                g_dev.head = nh;
            }
        }
        WakeAllConditionVariable(&g_dev.cv);
        LeaveCriticalSection(&g_dev.cs);
        if (!ok) return 0;
    }
}

/* Up to cap bytes; > 0 = bytes, 0 = timed out, -1 = the firmware is gone. */
static int dev_read(uint8_t *p, size_t cap, double deadline_us)
{
    int n = 0;
    EnterCriticalSection(&g_dev.cs);
    while (g_dev.head == g_dev.tail && !g_dev.closed) {
        double left = deadline_us - now_us();
        if (left <= 0.0) break;
        SleepConditionVariableCS(&g_dev.cv, &g_dev.cs, (DWORD)(left / 1000.0) + 1u);
    }
    while (g_dev.head != g_dev.tail && (size_t)n < cap) {
        p[n++] = g_dev.ring[g_dev.tail];
        g_dev.tail = (g_dev.tail + 1u) % RING;
    }
    if (n == 0 && g_dev.closed) n = -1;
    LeaveCriticalSection(&g_dev.cs);
    return n;
}

static int dev_write(const uint8_t *p, size_t n)
{
    while (n > 0) {
        DWORD w = 0;
        BOOL ok;
        if (g_dev.serial) {
            OVERLAPPED ov;
            memset(&ov, 0, sizeof(ov));
            ov.hEvent = CreateEventA(0, TRUE, FALSE, 0);
            ok = WriteFile(g_dev.wr, p, (DWORD)n, &w, &ov);
            if (!ok && GetLastError() == ERROR_IO_PENDING) ok = GetOverlappedResult(g_dev.wr, &ov, &w, TRUE);
            CloseHandle(ov.hEvent);
        } else {
            ok = WriteFile(g_dev.wr, p, (DWORD)n, &w, 0);
        }
        if (!ok || w == 0) return -1;
        p += w;
        n -= w;
    }
    return 0;
}

static int dev_start_thread(void)
{
    InitializeCriticalSection(&g_dev.cs);
    InitializeConditionVariable(&g_dev.cv);
    g_dev.thread = CreateThread(0, 0, dev_reader, 0, 0, 0);
    return g_dev.thread ? 0 : -1;
}

static int dev_open_exe(const char *path, char *why, size_t whyn)
{
    SECURITY_ATTRIBUTES sa;
    HANDLE in_r, in_w, out_r, out_w;
    STARTUPINFOA si;
    PROCESS_INFORMATION pi;
    char cmd[1024];

    memset(&sa, 0, sizeof(sa));
    sa.nLength = sizeof(sa);
    sa.bInheritHandle = TRUE;
    if (!CreatePipe(&in_r, &in_w, &sa, 1u << 16) || !CreatePipe(&out_r, &out_w, &sa, 1u << 16)) {
        snprintf(why, whyn, "CreatePipe failed (%lu)", GetLastError());
        return -1;
    }
    SetHandleInformation(in_w, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(out_r, HANDLE_FLAG_INHERIT, 0);

    memset(&si, 0, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdInput = in_r;
    si.hStdOutput = out_w;
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    snprintf(cmd, sizeof(cmd), "\"%s\"", path);
    if (!CreateProcessA(0, cmd, 0, 0, TRUE, CREATE_NO_WINDOW, 0, 0, &si, &pi)) {
        snprintf(why, whyn, "cannot start %s (%lu)", path, GetLastError());
        return -1;
    }
    CloseHandle(pi.hThread);
    CloseHandle(in_r);
    CloseHandle(out_w);
    g_dev.proc = pi.hProcess;
    g_dev.rd = out_r;
    g_dev.wr = in_w;
    return dev_start_thread();
}

static int dev_open_serial(const char *port, char *why, size_t whyn)
{
    char name[64];
    DCB dcb;
    COMMTIMEOUTS to;
    HANDLE h;

    snprintf(name, sizeof(name), "\\\\.\\%s", port);
    h = CreateFileA(name, GENERIC_READ | GENERIC_WRITE, 0, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
    if (h == INVALID_HANDLE_VALUE) {
        snprintf(why, whyn, "cannot open %s (%lu)", port, GetLastError());
        return -1;
    }
    memset(&dcb, 0, sizeof(dcb));
    dcb.DCBlength = sizeof(dcb);
    GetCommState(h, &dcb);
    dcb.BaudRate = (DWORD)g_baud;
    dcb.ByteSize = 8;
    dcb.Parity = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    dcb.fBinary = TRUE;
    dcb.fDtrControl = DTR_CONTROL_ENABLE;      /* many CDC stacks wait for DTR */
    dcb.fRtsControl = RTS_CONTROL_ENABLE;
    dcb.fOutxCtsFlow = FALSE;
    dcb.fOutX = dcb.fInX = FALSE;
    SetCommState(h, &dcb);
    /* Return as soon as anything has arrived, or after 100 ms of nothing. */
    memset(&to, 0, sizeof(to));
    to.ReadIntervalTimeout = MAXDWORD;
    to.ReadTotalTimeoutMultiplier = MAXDWORD;
    to.ReadTotalTimeoutConstant = 100;
    SetCommTimeouts(h, &to);
    PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR);
    g_dev.serial = 1;
    g_dev.rd = g_dev.wr = h;
    return dev_start_thread();
}

/* --------------------------------------------------- the wire, host side --- */

static TtWireRx g_rx;
static uint16_t g_seq;
static uint8_t  g_pend[8192];
static size_t   g_pend_n, g_pend_off;
static uint8_t  g_frame[TT_WIRE_MAX_FRAME];

static void record(const TtWireHdr *h, const uint8_t *p, size_t n)
{
    size_t len;
    if (!g_rec) return;
    len = tt_wire_frame(h, p, n, g_frame, sizeof(g_frame));
    if (len) fwrite(g_frame, 1, len, g_rec);
}

static int wire_send(uint8_t type, uint32_t tick, uint64_t time_us, const uint8_t *p, size_t n)
{
    TtWireHdr h;
    size_t len;
    h.type = type;
    h.flags = 0;
    h.seq = g_seq++;
    h.tick = tick;
    h.time_us = time_us;
    len = tt_wire_frame(&h, p, n, g_frame, sizeof(g_frame));
    if (!len) return -1;
    if (g_rec) fwrite(g_frame, 1, len, g_rec);
    return dev_write(g_frame, len);
}

/* Wait for a reply of `type` to the request for `tick`. Other frames are
 * counted (a late reply to an earlier tick, an unsolicited FAULT) and
 * dropped. 1 = got it (copied out), 0 = timed out, -1 = the firmware is gone. */
static int wire_wait(uint8_t type, uint32_t tick, int timeout_ms,
                     TtWireHdr *h, uint8_t *pl, size_t *pn)
{
    double deadline = now_us() + 1000.0 * timeout_ms;
    for (;;) {
        while (g_pend_off < g_pend_n) {
            const uint8_t *p;
            size_t plen;
            int got;
            size_t used = tt_wire_rx_feed(&g_rx, g_pend + g_pend_off, g_pend_n - g_pend_off,
                                          &got, h, &p, &plen);
            g_pend_off += used;
            if (!got) continue;
            record(h, p, plen);
            if (h->type == TT_WIRE_FAULT) {
                g_n.faults++;
                if (plen >= 6)
                    fprintf(stderr, "tt_bridge: firmware fault %u (detail %lu)\n",
                            (unsigned)(p[0] | (p[1] << 8)),
                            (unsigned long)(p[2] | (p[3] << 8) | ((unsigned long)p[4] << 16) |
                                            ((unsigned long)p[5] << 24)));
                continue;
            }
            if (h->type == type && h->tick == tick) {
                memcpy(pl, p, plen);
                *pn = plen;
                return 1;
            }
            g_n.late++;
        }
        {
            int n = dev_read(g_pend, sizeof(g_pend), deadline);
            if (n < 0) return -1;
            if (n == 0) return 0;
            g_pend_n = (size_t)n;
            g_pend_off = 0;
        }
    }
}

/* -------------------------------------------------- the device modes --- */

static SimHal   g_hal;
static int      g_hal_up;
static uint32_t g_ctl_tick;       /* request tick for INIT / CONFIG / ... */
static uint8_t  g_pl[TT_WIRE_MAX_PAYLOAD];
static char     g_names[1024];
static float    g_log[TT_WIRE_MAX_PAYLOAD / 4];
static uint32_t g_n_log;
static int      g_dev_ready;

static int dev_request(uint8_t type, uint8_t reply, const uint8_t *p, size_t n,
                       TtWireHdr *h, size_t *pn, int timeout_ms)
{
    uint32_t tick = ++g_ctl_tick | 0x80000000u;    /* never a control tick */
    if (wire_send(type, tick, 0, p, n) != 0) return -1;
    return wire_wait(reply, tick, timeout_ms, h, g_pl, pn);
}

static int dev_ack_status(int r, size_t pn)
{
    if (r <= 0) return -1;
    if (pn < 5) return -1;
    return (int32_t)((uint32_t)g_pl[1] | ((uint32_t)g_pl[2] << 8) |
                     ((uint32_t)g_pl[3] << 16) | ((uint32_t)g_pl[4] << 24));
}

/* Ask who the firmware is: INIT with rate 0 identifies without initialising. */
static int dev_identify(TtWireInfo *info)
{
    TtWireHdr h;
    size_t pn = 0;
    uint8_t b[4] = { 0, 0, 0, 0 };
    int r = dev_request(TT_WIRE_INIT, TT_WIRE_INFO, b, 4, &h, &pn, g_init_timeout_ms);
    if (r <= 0) return -1;
    return tt_wire_get_info(g_pl, pn, info);
}

/* ------------------------------------------------------------ handlers --- */

static void on_init(Rd *r)
{
    float rate = rd_f32(r);
    Buf b = { 0, 0, 0 };
    int32_t rc = -1;
    const char *names = "";

    if (g_mode == MODE_DLL) {
        rc = g_dll.init(rate);
        names = g_dll.names ? g_dll.names() : "";
    } else {
        TtWireHdr h;
        size_t pn = 0;
        uint8_t p[4];
        uint32_t u;
        TtWireInfo info;
        if (!g_hal_up) { sim_hal_init(&g_hal); g_hal_up = 1; }
        sim_hal_restart_clock(&g_hal);
        memcpy(&u, &rate, 4);
        p[0] = (uint8_t)u; p[1] = (uint8_t)(u >> 8); p[2] = (uint8_t)(u >> 16); p[3] = (uint8_t)(u >> 24);
        if (dev_request(TT_WIRE_INIT, TT_WIRE_INFO, p, 4, &h, &pn, g_init_timeout_ms) > 0 &&
            tt_wire_get_info(g_pl, pn, &info) == 0) {
            rc = (h.flags & TT_WF_NOT_READY) ? -2 : 0;
            g_dev_ready = rc == 0;
            strncpy(g_names, info.log_names, sizeof(g_names) - 1u);
            names = g_names;
        } else {
            fprintf(stderr, "tt_bridge: the firmware did not answer INIT\n");
        }
    }
    b_i32(&b, rc);
    b_str(&b, names);
    up_send('i', &b);
    free(b.p);
}

static void on_configure(Rd *r)
{
    int32_t n = (int32_t)rd_u32(r), status = 0;
    const uint8_t *raw = 0;
    SensorInfo2 *s = 0;
    if (n > 0) raw = rd_take(r, (size_t)n * sizeof(SensorInfo2));
    if (r->err || n < 0) { up_status('c', -1); return; }
    /* The manifest sits at an arbitrary offset in the message: copy it out
     * before anything reads floats from it. */
    if (n > 0) {
        s = (SensorInfo2 *)malloc((size_t)n * sizeof(SensorInfo2));
        if (!s) { up_status('c', -1); return; }
        memcpy(s, raw, (size_t)n * sizeof(SensorInfo2));
    }

    if (g_mode == MODE_DLL) {
        if (g_dll.configure2) g_dll.configure2(s, n);
        else status = -1;
    } else {
        TtCarDesc car;
        TtWireHdr h;
        size_t pn = 0, len;
        sim_hal_configure(&g_hal, s, n, &car);
        len = tt_wire_put_car(g_pl, sizeof(g_pl), &car);
        status = dev_ack_status(dev_request(TT_WIRE_CONFIG, TT_WIRE_ACK, g_pl, len, &h, &pn,
                                            g_init_timeout_ms), pn);
    }
    free(s);
    up_status('c', status);
}

static void on_simple(uint8_t type)
{
    int32_t status = 0;
    if (g_mode == MODE_DLL) {
        if (type == 'R') { if (g_dll.reset) g_dll.reset(); else status = -1; }
        else g_dll.shutdown();
    } else {
        TtWireHdr h;
        size_t pn = 0;
        if (type == 'R') sim_hal_restart_clock(&g_hal);
        status = dev_ack_status(dev_request(type == 'R' ? TT_WIRE_RESET : TT_WIRE_SHUTDOWN,
                                            TT_WIRE_ACK, 0, 0, &h, &pn, g_init_timeout_ms), pn);
        if (type == 'X') g_dev_ready = 0;
    }
    up_status(type == 'R' ? 'r' : 'x', status);
}

static void on_step(Rd *r)
{
    static float *data;
    static size_t data_cap;
    static SensorStamp *stamps;
    static size_t stamps_cap;
    CtrlInputs in;
    CtrlOutputs out;
    float ext[64];
    int n_ext = 0, k;
    uint8_t status = ST_OK;
    int32_t dlen;
    Buf b = { 0, 0, 0 };

    memset(&in, 0, sizeof(in));
    memset(&out, 0, sizeof(out));
    in.tick = rd_u32(r);
    in.flags = rd_u32(r);
    in.time_us = rd_u64(r);
    in.time_s = rd_f32(r);
    in.dt_s = rd_f32(r);
    for (k = 0; k < 3; k++) in.gyro[k] = rd_f32(r);
    for (k = 0; k < 3; k++) in.accel[k] = rd_f32(r);
    for (k = 0; k < 4; k++) in.wheel_vel[k] = rd_f32(r);
    for (k = 0; k < 4; k++) in.setpoint[k] = rd_f32(r);
    in.sensor_count = (int)rd_u32(r);
    dlen = (int32_t)rd_u32(r);
    if (dlen < 0 || in.sensor_count < 0) r->err = 1;
    if (!r->err && (size_t)dlen > data_cap) {
        data_cap = (size_t)dlen + 64u;
        data = (float *)realloc(data, data_cap * sizeof(float));
    }
    for (k = 0; !r->err && k < dlen; k++) data[k] = rd_f32(r);
    in.sensor_data = dlen > 0 ? data : 0;
    in.sensor_data_len = dlen;
    if (!r->err && rd_u8(r)) {
        if ((size_t)in.sensor_count > stamps_cap) {
            stamps_cap = (size_t)in.sensor_count + 16u;
            stamps = (SensorStamp *)realloc(stamps, stamps_cap * sizeof(SensorStamp));
        }
        for (k = 0; !r->err && k < in.sensor_count; k++) {
            stamps[k].seq = rd_u32(r);
            stamps[k].t_sample_us = rd_u32(r);
        }
        in.stamps = in.sensor_count > 0 ? stamps : 0;
    }

    g_n.steps++;
    if (r->err) {
        status = ST_GONE;
    } else if (g_mode == MODE_DLL) {
        g_dll.step(&in, &out);
        if (g_dll.ext) n_ext = g_dll.ext(ext, 64);
    } else {
        TtMeas m;
        TtAct act;
        TtWireHdr h;
        size_t len, pn = 0;
        int w;
        double t0;
        sim_hal_read(&g_hal, &in, &m);
        len = tt_wire_put_meas(g_pl, sizeof(g_pl), &m);
        t0 = now_us();
        if (len == 0 || wire_send(TT_WIRE_MEAS, in.tick, in.time_us, g_pl, len) != 0) {
            w = -1;
        } else {
            w = wire_wait(TT_WIRE_ACT, in.tick, g_timeout_ms, &h, g_pl, &pn);
        }
        if (w > 0) {
            sample(&g_rtt, now_us() - t0);
            if (tt_wire_get_act(g_pl, pn, &act, g_log, (uint32_t)(sizeof(g_log) / 4u), &g_n_log) != 0) {
                status = ST_NOT_READY;
            } else if (h.flags & TT_WF_NOT_READY) {
                status = ST_NOT_READY;
                g_n.not_ready++;
            } else {
                uint32_t i;
                sim_hal_write(&g_hal, &act, &out);
                for (i = 0; i < g_n_log && i < 16u; i++) out.debug[i] = g_log[i];
                for (i = 16; i < g_n_log && n_ext < 64; i++) ext[n_ext++] = g_log[i];
            }
        } else if (w == 0) {
            status = ST_TIMEOUT;
            g_n.timeouts++;
        } else {
            status = ST_GONE;
            g_n.gone++;
        }
    }

    b_u32(&b, in.tick);
    b_u8(&b, status);
    for (k = 0; k < 8; k++) b_f32(&b, out.actuator[k]);
    for (k = 0; k < 16; k++) b_f32(&b, out.debug[k]);
    if (n_ext < 0) n_ext = 0;
    b_u16(&b, (uint16_t)n_ext);
    for (k = 0; k < n_ext; k++) b_f32(&b, ext[k]);
    up_send('s', &b);
    free(b.p);
}

static void stats_text(char *s, size_t n)
{
    double p50 = pct(&g_rtt, 0.5), p99 = pct(&g_rtt, 0.99);
    snprintf(s, n,
             "mode=%s steps=%u timeouts=%u late=%u not_ready=%u gone=%u faults=%u "
             "rtt_n=%u rtt_p50_us=%.1f rtt_p99_us=%.1f rtt_max_us=%.1f "
             "rx_frames=%u crc_errors=%u cobs_errors=%u seq_gaps=%u dropped=%u",
             g_mode == MODE_DLL ? "dll" : g_mode == MODE_EXE ? "exe" : "serial",
             g_n.steps, g_n.timeouts, g_n.late, g_n.not_ready, g_n.gone, g_n.faults,
             (unsigned)g_rtt.n, p50, p99, g_rtt.max,
             (unsigned)g_rx.frames, (unsigned)g_rx.crc_errors, (unsigned)g_rx.cobs_errors,
             (unsigned)g_rx.seq_gaps, g_dev.dropped);
}

static void on_stats(void)
{
    char s[512];
    Buf b = { 0, 0, 0 };
    stats_text(s, sizeof(s));
    b_str(&b, s);
    up_send('t', &b);
    free(b.p);
}

/* -------------------------------------------------------------- bench --- */

static int bench(int n)
{
    int i, lost = 0;
    for (i = 0; i < n; i++) {
        TtWireHdr h;
        size_t pn = 0;
        uint8_t nonce[4] = { (uint8_t)i, (uint8_t)(i >> 8), (uint8_t)(i >> 16), (uint8_t)(i >> 24) };
        double t0 = now_us();
        uint32_t tick = (uint32_t)i;
        if (wire_send(TT_WIRE_PING, tick, 0, nonce, 4) != 0) { fprintf(stderr, "write failed\n"); return 1; }
        if (wire_wait(TT_WIRE_PONG, tick, g_timeout_ms, &h, g_pl, &pn) > 0) sample(&g_rtt, now_us() - t0);
        else lost++;
    }
    printf("ping x%d: lost %d, round trip p50 %.1f us, p99 %.1f us, max %.1f us\n",
           n, lost, pct(&g_rtt, 0.5), pct(&g_rtt, 0.99), g_rtt.max);
    return lost ? 1 : 0;
}

/* --------------------------------------------------------------- main --- */

static void usage(void)
{
    fprintf(stderr,
            "usage: tt_bridge (--dll <controller.dll> | --exe <firmware.exe> | --serial <COMn>)\n"
            "                 [--timeout-ms N] [--init-timeout-ms N] [--baud N]\n"
            "                 [--record <file.ttw>] [--bench N]\n");
}

int main(int argc, char **argv)
{
    int i, bench_n = 0, ok = 1;
    char why[256] = "";
    float want = 0.0f;
    const char *fw = "";
    TtWireInfo info;
    Buf hello = { 0, 0, 0 };

    QueryPerformanceFrequency(&g_qpf);
    tt_wire_rx_init(&g_rx);
    for (i = 1; i < argc; i++) {
        const char *a = argv[i], *v = i + 1 < argc ? argv[i + 1] : 0;
        if (!strcmp(a, "--dll") && v)            { g_mode = MODE_DLL; g_target = v; i++; }
        else if (!strcmp(a, "--exe") && v)       { g_mode = MODE_EXE; g_target = v; i++; }
        else if (!strcmp(a, "--serial") && v)    { g_mode = MODE_SERIAL; g_target = v; i++; }
        else if (!strcmp(a, "--timeout-ms") && v)      { g_timeout_ms = atoi(v); i++; }
        else if (!strcmp(a, "--init-timeout-ms") && v) { g_init_timeout_ms = atoi(v); i++; }
        else if (!strcmp(a, "--baud") && v)      { g_baud = atol(v); i++; }
        else if (!strcmp(a, "--record") && v)    { g_rec = fopen(v, "wb"); i++; }
        else if (!strcmp(a, "--bench") && v)     { bench_n = atoi(v); i++; }
        else { usage(); return 2; }
    }
    if (g_mode == MODE_NONE) { usage(); return 2; }
    if (g_timeout_ms < 1) g_timeout_ms = 1;

    if (g_mode == MODE_DLL) {
        if (dll_open(g_target, why, sizeof(why)) != 0) ok = 0;
        else { want = g_dll.rate ? g_dll.rate() : 0.0f; fw = g_target; }
    } else {
        int r = g_mode == MODE_EXE ? dev_open_exe(g_target, why, sizeof(why))
                                   : dev_open_serial(g_target, why, sizeof(why));
        if (r != 0) ok = 0;
        else {
            /* A lone delimiter ends any half frame the board was holding. */
            uint8_t z = 0;
            dev_write(&z, 1);
            if (bench_n > 0) return bench(bench_n);
            if (dev_identify(&info) != 0) {
                ok = 0;
                snprintf(why, sizeof(why), "no answer from %s", g_target);
            } else if (info.version != TT_WIRE_VERSION) {
                ok = 0;
                snprintf(why, sizeof(why), "%s speaks wire v%u; the bridge v%u",
                         g_target, (unsigned)info.version, (unsigned)TT_WIRE_VERSION);
            } else {
                want = info.rate_hz;
                fw = info.name;
            }
        }
    }
    if (!ok) fprintf(stderr, "tt_bridge: %s\n", why);

    g_in = GetStdHandle(STD_INPUT_HANDLE);
    g_out = GetStdHandle(STD_OUTPUT_HANDLE);
    b_u32(&hello, LINK_PROTO);
    b_i32(&hello, (int32_t)sizeof(SensorInfo2));
    b_i32(&hello, (int32_t)sizeof(CtrlInputs));
    b_i32(&hello, (int32_t)sizeof(CtrlOutputs));
    b_f32(&hello, want);
    b_str(&hello, g_mode == MODE_DLL ? "dll" : g_mode == MODE_EXE ? "exe" : "serial");
    b_str(&hello, ok ? fw : why);
    b_i32(&hello, ok ? 1 : 0);
    up_send('h', &hello);
    free(hello.p);
    if (!ok) return 1;

    for (;;) {
        static uint8_t *msg;
        static size_t msg_cap;
        uint8_t hdr[4];
        uint32_t len;
        Rd r;
        if (up_read_exact(hdr, 4) != 0) break;
        len = (uint32_t)hdr[0] | ((uint32_t)hdr[1] << 8) | ((uint32_t)hdr[2] << 16) | ((uint32_t)hdr[3] << 24);
        if (len == 0 || len > (64u << 20)) { fprintf(stderr, "tt_bridge: bad message length %u\n", len); break; }
        if (len > msg_cap) {
            msg_cap = len;
            msg = (uint8_t *)realloc(msg, msg_cap);
            if (!msg) break;
        }
        if (up_read_exact(msg, len) != 0) break;
        r.p = msg + 1;
        r.n = len - 1u;
        r.i = 0;
        r.err = 0;
        switch (msg[0]) {
        case 'I': on_init(&r); break;
        case 'C': on_configure(&r); break;
        case 'R': case 'X': on_simple(msg[0]); break;
        case 'S': on_step(&r); break;
        case 'T': on_stats(); break;
        default:
            fprintf(stderr, "tt_bridge: unknown request '%c'\n", msg[0]);
            up_status('?', -1);
            break;
        }
        if (g_rec) fflush(g_rec);
    }

    {
        char s[512];
        stats_text(s, sizeof(s));
        fprintf(stderr, "tt_bridge: %s\n", s);
    }
    if (g_rec) fclose(g_rec);
    if (g_mode == MODE_EXE && g_dev.proc) {
        CloseHandle(g_dev.wr);                     /* the firmware sees EOF */
        WaitForSingleObject(g_dev.proc, 2000);
    }
    return 0;
}
