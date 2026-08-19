using System;

namespace IEM.Linux.Time;

/// <summary>
/// Authoritative factual snapshot of Linux kernel time synchronization discipline from adjtimex(2).
/// Invariants:
/// 97. SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME
/// 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
/// 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY
/// 113. PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS
/// </summary>
public sealed record LinuxTimeSyncProvenance(
    bool Available,
    int RawKernelState,
    int RawStatusFlags,

    bool Unsynchronized,
    bool ClockError,

    bool LeapInsertPending,
    bool LeapDeletePending,
    bool LeapInProgress,
    bool LeapCompleted,

    bool PllEnabled,
    bool FllEnabled,

    bool PpsFrequencyDiscipline,
    bool PpsTimeDiscipline,
    bool PpsSignalPresent,
    bool PpsJitterExceeded,
    bool PpsWanderExceeded,
    bool PpsCalibrationError,

    bool NanosecondMode,

    long OffsetRaw,
    long FrequencyRaw,
    long MaximumErrorMicroseconds,
    long EstimatedErrorMicroseconds,
    long PrecisionRaw,
    long ToleranceRaw,
    int TaiOffsetSeconds,

    string Source,
    string? FailureReason)
{
    /// <summary>
    /// Calculated frequency offset in parts-per-million (ppm).
    /// Raw kernel freq has 16-bit fractional part (65536 units = 1 ppm).
    /// </summary>
    public double FrequencyPpm => (double)FrequencyRaw / 65536.0;

    public static LinuxTimeSyncProvenance Unavailable(string failureReason) =>
        new(
            Available: false,
            RawKernelState: -1,
            RawStatusFlags: 0,
            Unsynchronized: true,
            ClockError: false,
            LeapInsertPending: false,
            LeapDeletePending: false,
            LeapInProgress: false,
            LeapCompleted: false,
            PllEnabled: false,
            FllEnabled: false,
            PpsFrequencyDiscipline: false,
            PpsTimeDiscipline: false,
            PpsSignalPresent: false,
            PpsJitterExceeded: false,
            PpsWanderExceeded: false,
            PpsCalibrationError: false,
            NanosecondMode: false,
            OffsetRaw: 0,
            FrequencyRaw: 0,
            MaximumErrorMicroseconds: 0,
            EstimatedErrorMicroseconds: 0,
            PrecisionRaw: 0,
            ToleranceRaw: 0,
            TaiOffsetSeconds: 0,
            Source: "adjtimex(modes=0)",
            FailureReason: failureReason);

    public static LinuxTimeSyncProvenance FromTimex(int kernelState, in LinuxTimex timex)
    {
        var status = timex.Status;
        return new LinuxTimeSyncProvenance(
            Available: true,
            RawKernelState: kernelState,
            RawStatusFlags: status,

            Unsynchronized: (status & LinuxAdjtimex.STA_UNSYNC) != 0 || kernelState == LinuxAdjtimex.TIME_ERROR,
            ClockError: (status & LinuxAdjtimex.STA_CLOCKERR) != 0,

            LeapInsertPending: kernelState == LinuxAdjtimex.TIME_INS || (status & LinuxAdjtimex.STA_INS) != 0,
            LeapDeletePending: kernelState == LinuxAdjtimex.TIME_DEL || (status & LinuxAdjtimex.STA_DEL) != 0,
            LeapInProgress: kernelState == LinuxAdjtimex.TIME_OOP,
            LeapCompleted: kernelState == LinuxAdjtimex.TIME_WAIT,

            PllEnabled: (status & LinuxAdjtimex.STA_PLL) != 0,
            FllEnabled: (status & LinuxAdjtimex.STA_FLL) != 0,

            PpsFrequencyDiscipline: (status & LinuxAdjtimex.STA_PPSFREQ) != 0,
            PpsTimeDiscipline: (status & LinuxAdjtimex.STA_PPSTIME) != 0,
            PpsSignalPresent: (status & LinuxAdjtimex.STA_PPSSIGNAL) != 0,
            PpsJitterExceeded: (status & LinuxAdjtimex.STA_PPSJITTER) != 0,
            PpsWanderExceeded: (status & LinuxAdjtimex.STA_PPSWANDER) != 0,
            PpsCalibrationError: (status & LinuxAdjtimex.STA_PPSERROR) != 0,

            NanosecondMode: (status & LinuxAdjtimex.STA_NANO) != 0,

            OffsetRaw: timex.Offset,
            FrequencyRaw: timex.Freq,
            MaximumErrorMicroseconds: timex.Maxerror,
            EstimatedErrorMicroseconds: timex.Esterror,
            PrecisionRaw: timex.Precision,
            ToleranceRaw: timex.Tolerance,
            TaiOffsetSeconds: timex.Tai,

            Source: "adjtimex(modes=0)",
            FailureReason: null);
    }
}
