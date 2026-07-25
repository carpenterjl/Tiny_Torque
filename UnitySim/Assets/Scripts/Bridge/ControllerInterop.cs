using System;
using System.Runtime.InteropServices;

namespace AIHWSim.Bridge
{
    // Blittable mirrors of the structs in Controllers/hal/controller_api.h.
    // Field order and counts MUST match that header exactly (ABI v2).

    // Sensor type tags — mirror of the enum in controller_api.h.
    public enum SensorType
    {
        Tof        = 1,
        Encoder    = 2,
        Motor      = 3,
        Imu        = 4,
        Camera     = 5,
        Suspension = 6,
        Battery    = 7,
    }

    // One manifest entry per configured sensor. char name[32] is an inline
    // fixed byte buffer so the struct stays blittable for the native call.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SensorInfo
    {
        public const int NameLen = 32;

        public fixed byte name[NameLen];
        public int type;
        public int data_offset;
        public int data_count;
        public float range_min;
        public float range_max;
        public int actuator_index; // v3: actuator[] slot a motor reads; -1 otherwise

        /// <summary>Copy an ASCII name into the inline fixed buffer (truncated, NUL-terminated).</summary>
        public void SetName(string s)
        {
            fixed (byte* p = name)
            {
                int n = 0;
                if (!string.IsNullOrEmpty(s))
                {
                    int max = NameLen - 1;
                    for (; n < s.Length && n < max; n++)
                    {
                        char c = s[n];
                        p[n] = c < 128 ? (byte)c : (byte)'?';
                    }
                }
                p[n] = 0;
            }
        }
    }

    // Host -> controller. The v2 pointer fields are filled from pinned managed
    // arrays for the duration of the ctrl_step call (see SimulationRunner).
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CtrlInputs
    {
        public float time_s;
        public float dt_s;
        public fixed float gyro[3];
        public fixed float accel[3];
        public fixed float wheel_vel[4];
        public fixed float setpoint[4];

        // --- v2 ---
        public float* sensor_data;
        public int sensor_count;
        public int sensor_data_len;
        public byte* cam_pixels;
        public int cam_width;
        public int cam_height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct CtrlOutputs
    {
        public fixed float actuator[8];
        public fixed float debug[16];
    }

    // Exported entry points. x86_64 has a single calling convention, but we
    // pin Cdecl for clarity and 32-bit safety.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CtrlInitDelegate(float controlRateHz);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void CtrlStepDelegate(CtrlInputs* input, CtrlOutputs* output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtrlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CtrlGetDebugNamesDelegate();

    // v2 optional export. Null when the loaded DLL predates ABI v2.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void CtrlConfigureDelegate(SensorInfo* sensors, int count);
}
