using System;
using System.Runtime.InteropServices;

namespace IEM.Linux.Time;

/// <summary>
/// Native Linux struct timeval layout (seconds + microseconds).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct LinuxTimeval
{
    public long TvSec;
    public long TvUsec;
}

/// <summary>
/// Full native Linux struct timex layout according to Linux UAPI (x86_64).
/// Exactly 208 bytes on x64 platforms.
/// Invariant 113: PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct LinuxTimex
{
    public int Modes;              // 0: mode selector
    public int PadModes;           // 4: 32-bit alignment padding for 64-bit long fields
    public long Offset;            // 8: time offset (usec or nsec)
    public long Freq;              // 16: frequency offset (scaled ppm, 65536 = 1 ppm)
    public long Maxerror;          // 24: maximum error (usec)
    public long Esterror;          // 32: estimated error (usec)
    public int Status;             // 40: clock command/status flags (STA_*)
    public int PadStatus;          // 44: alignment padding
    public long Constant;          // 48: PLL time constant
    public long Precision;         // 56: clock precision (usec, read-only)
    public long Tolerance;         // 64: clock frequency tolerance (ppm, read-only)
    public LinuxTimeval Time;      // 72: current time (tv_sec: 8, tv_usec: 8)
    public long Tick;              // 88: usecs between clock ticks
    public long Ppsfreq;           // 96: PPS frequency (scaled ppm, read-only)
    public long Jitter;            // 104: PPS jitter (usec, read-only)
    public int Shift;              // 112: interval duration (shift, read-only)
    public int PadShift;           // 116: alignment padding
    public long Stabil;            // 120: PPS stability (scaled ppm, read-only)
    public long Jitcnt;            // 128: PPS jitter limit exceeded count
    public long Calcnt;            // 136: PPS calibration intervals count
    public long Errcnt;            // 144: PPS calibration errors count
    public long Stbcnt;            // 152: PPS stability limit exceeded count
    public int Tai;                // 160: TAI offset (seconds, read-only)
    public int Pad0;               // 164: reserved padding
    public int Pad1;
    public int Pad2;
    public int Pad3;
    public int Pad4;
    public int Pad5;
    public int Pad6;
    public int Pad7;
    public int Pad8;
    public int Pad9;
    public int Pad10;              // 204: 11 ints pad total (164 + 44 = 208 bytes)
}

/// <summary>
/// Abstraction for querying Linux kernel time discipline status via adjtimex(2).
/// Enforces read-only querying (modes = 0).
/// </summary>
internal interface ILinuxAdjtimex
{
    int Query(ref LinuxTimex timex);
}

/// <summary>
/// Direct libc P/Invoke implementation of adjtimex(2).
/// Strictly read-only: forces modes = 0 before invoking libc.
/// </summary>
internal sealed class LinuxAdjtimex : ILinuxAdjtimex
{
    // Kernel Time States (return values from adjtimex)
    public const int TIME_OK = 0;     // Clock synchronized, no leap second pending
    public const int TIME_INS = 1;    // Insert leap second
    public const int TIME_DEL = 2;    // Delete leap second
    public const int TIME_OOP = 3;    // Leap second in progress
    public const int TIME_WAIT = 4;   // Leap second has occurred
    public const int TIME_ERROR = 5;  // Clock not synchronized

    // Status bits in timex.Status (STA_*)
    public const int STA_PLL = 0x0001;        // Enable PLL updates
    public const int STA_PPSFREQ = 0x0002;    // Enable PPS freq discipline
    public const int STA_PPSTIME = 0x0004;    // Enable PPS time discipline
    public const int STA_FLL = 0x0008;        // Enable FLL mode
    public const int STA_INS = 0x0010;        // Insert leap second
    public const int STA_DEL = 0x0020;        // Delete leap second
    public const int STA_UNSYNC = 0x0040;     // Clock unsynchronized
    public const int STA_FREQHOLD = 0x0080;   // Hold frequency
    public const int STA_PPSSIGNAL = 0x0100;  // PPS signal present
    public const int STA_PPSJITTER = 0x0200;  // PPS signal jitter exceeded
    public const int STA_PPSWANDER = 0x0400;  // PPS signal wander exceeded
    public const int STA_PPSERROR = 0x0800;   // PPS signal calibration error
    public const int STA_CLOCKERR = 0x1000;   // Clock hardware fault
    public const int STA_NANO = 0x2000;       // Resolution (0=us, 1=ns)
    public const int STA_MODE = 0x4000;       // Mode (0=phase, 1=frequency)
    public const int STA_CLK = 0x8000;        // Clock source (0=A, 1=B)

    [DllImport("libc", EntryPoint = "adjtimex", SetLastError = true)]
    private static extern int adjtimex_native(ref LinuxTimex buf);

    public int Query(ref LinuxTimex timex)
    {
        // Enforce strictly read-only mode (modes = 0)
        timex.Modes = 0;

        var result = adjtimex_native(ref timex);
        if (result < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return -errno; // Return negative errno
        }

        return result;
    }
}
