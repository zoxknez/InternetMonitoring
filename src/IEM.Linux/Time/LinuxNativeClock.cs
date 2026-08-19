using System;
using System.Runtime.InteropServices;

namespace IEM.Linux.Time;

/// <summary>
/// Native Linux timespec representation (seconds + nanoseconds).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LinuxTimeSpec
{
    public long TvSec;
    public long TvNsec;
}

/// <summary>
/// Abstraction for querying Linux POSIX kernel clocks.
/// Decouples LinuxTimeObservationProvider from concrete libc P/Invoke for deterministic testing.
/// </summary>
internal interface ILinuxNativeClock
{
    void GetTime(int clockId, out LinuxTimeSpec timeSpec);
}

/// <summary>
/// Direct libc clock_gettime wrapper for Linux kernel clocks.
/// Supported clocks: CLOCK_REALTIME (0), CLOCK_MONOTONIC (1), CLOCK_BOOTTIME (7).
/// </summary>
internal sealed class LinuxNativeClock : ILinuxNativeClock
{
    public const int CLOCK_REALTIME = 0;
    public const int CLOCK_MONOTONIC = 1;
    public const int CLOCK_BOOTTIME = 7;

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int clock_gettime(int clockId, out LinuxTimeSpec tp);

    public void GetTime(int clockId, out LinuxTimeSpec timeSpec)
    {
        if (clock_gettime(clockId, out timeSpec) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"clock_gettime({clockId}) failed with errno {errno}.");
        }

        if (timeSpec.TvNsec < 0 || timeSpec.TvNsec >= 1_000_000_000)
        {
            throw new InvalidOperationException($"clock_gettime({clockId}) returned invalid nanoseconds: {timeSpec.TvNsec}.");
        }
    }
}
