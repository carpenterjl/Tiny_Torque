using System;
using System.Runtime.InteropServices;

namespace AIHWSim.Bridge
{
    // Blittable mirrors of the structs in Controllers/hal/controller_api.h.
    // Field order and counts MUST match that header exactly. v7 appended a tail
    // to CtrlInputs and added SensorInfo2 / SensorStamp; a v6 DLL never reads
    // the tail, and a v7 DLL reports its sizes so a mismatch is refused.
    public static class ControllerAbi
    {
        /// <summary>CTRL_ABI_VERSION this host implements.</summary>
        public const int Version = 7;
        /// <summary>CTRL_IN_FLU: vectors are in the FLU body frame.</summary>
        public const uint InFlu = 0x1u;
    }

    /// <summary>CTRL_UNITS_*: what an actuator slot carries (v7).</summary>
    public enum ActuatorUnits
    {
        None = 0,
        Volts = 1,
        AmpsIq = 2,
        Radians = 3,
    }

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
        Color      = 8,   // v6: surface colour [r,g,b,reflect] 0..1
        Rf         = 9,   // v6: [count, id/rssi/bearing ×3 slots]
        Mag        = 10,  // v6: [heading_deg] 0..360
        Bump       = 11,  // v6: [contact_01, force_n]
        Led        = 12,  // v6: actuator part; readback [r,g,b,lit]
        Imu6       = 13,  // v7: raw 6-axis IMU part, chip frame (MemsImuSensor)
        TofMz      = 14,  // v7 (reserved): multizone ToF
        Flow       = 15,  // v7 (reserved): optical flow
        Uwb        = 16,  // v7 (reserved): UWB ranging
        FocFb      = 17,  // v7 (reserved): FOC driver feedback
        SteerFb    = 18,  // v7: the steering servo; describes actuator[6]
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

    // v7 extended manifest entry (ctrl_configure2). base is the v6 entry.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SensorInfo2
    {
        public SensorInfo @base;
        public int wheel_index;      // 0..3 for a wheel-bound part, else -1
        public int units;            // ActuatorUnits when base.actuator_index >= 0
        public fixed float pos_m[3];   // FLU, from the vehicle origin
        public fixed float rpy_rad[3]; // FLU roll, pitch, yaw (Z-Y-X)
        public float rate_hz;
        public float latency_s;
        public float cpr;
        public float wrap;
        public float gear_ratio;
        public float kt;
        public float resistance_ohm;
        public float efficiency;
        public float wheel_radius_m;
        public int truth_only;
        public fixed float reserved[8];
    }

    // v7: when a sensor's current value was sampled (one per manifest entry).
    [StructLayout(LayoutKind.Sequential)]
    public struct SensorStamp
    {
        public uint seq;
        public uint t_sample_us;
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

        // --- v7 (filled only for a controller that exports ctrl_abi_version) ---
        public uint tick;
        public uint flags;
        public ulong time_us;
        public SensorStamp* stamps;
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

    // v5 optional export. Null when the loaded DLL predates ABI v5.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CtrlGetVehicleDelegate();

    // v7 exports. ctrl_abi_version makes a DLL a v7 controller; the rest are
    // optional on top of it.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate int CtrlAbiVersionDelegate(int* sizeofInputs, int* sizeofOutputs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public unsafe delegate void CtrlConfigure2Delegate(SensorInfo2* sensors, int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CtrlResetDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate float CtrlGetControlRateDelegate();

    /// <summary>
    /// Mirror of the CTRL_VEHICLE_* enum in controller_api.h — the cars a
    /// controller may ask to be loaded into.
    ///
    /// The NUMBERS are the ABI; the names below are only how C# spells them. A
    /// value here is compiled into somebody's DLL, so a row may be added but a
    /// row's number may never be reused for a different car.
    /// </summary>
    public enum ControllerVehicle
    {
        Menu        = 0,   // no override — whatever the menu picked
        Stock       = 1,
        RealTwin    = 2,
        TtCoupe     = 3,
        TtBaja      = 4,
        TtPatrol    = 5,
        TtRattletrap= 6,
        TtRedline   = 7,
        TtHighwing  = 8,
        TtAutopia   = 9,
        OpusVector  = 10,
    }
}
