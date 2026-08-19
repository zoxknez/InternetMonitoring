using System;
using System.Runtime.InteropServices;
using IEM.Linux.Time;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-6E adjtimex Provenance.
/// Verifies x64 struct timex ABI layout compliance with Linux UAPI,
/// full kernel return state decoding (TIME_OK .. TIME_ERROR), status flags,
/// raw frequency scaled ppm calculations, and read-only modes=0 enforcement.
/// </summary>
public sealed class LinuxAdjtimexTests
{
    [Fact]
    public void LinuxTimex_x64_ABI_size_and_field_offsets_match_Linux_UAPI()
    {
        Assert.Equal(208, Marshal.SizeOf<LinuxTimex>());
        Assert.Equal(0, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Modes)));
        Assert.Equal(8, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Offset)));
        Assert.Equal(16, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Freq)));
        Assert.Equal(24, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Maxerror)));
        Assert.Equal(32, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Esterror)));
        Assert.Equal(40, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Status)));
        Assert.Equal(48, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Constant)));
        Assert.Equal(56, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Precision)));
        Assert.Equal(64, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Tolerance)));
        Assert.Equal(72, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Time)));
        Assert.Equal(88, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Tick)));
        Assert.Equal(96, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Ppsfreq)));
        Assert.Equal(104, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Jitter)));
        Assert.Equal(112, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Shift)));
        Assert.Equal(120, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Stabil)));
        Assert.Equal(128, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Jitcnt)));
        Assert.Equal(136, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Calcnt)));
        Assert.Equal(144, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Errcnt)));
        Assert.Equal(152, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Stbcnt)));
        Assert.Equal(160, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Tai)));
        Assert.Equal(164, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Pad0)));
        Assert.Equal(204, (int)Marshal.OffsetOf<LinuxTimex>(nameof(LinuxTimex.Pad10)));
    }

    [Theory]
    [InlineData(LinuxAdjtimex.TIME_OK, false, false, false, false, false)]
    [InlineData(LinuxAdjtimex.TIME_INS, false, true, false, false, false)]
    [InlineData(LinuxAdjtimex.TIME_DEL, false, false, true, false, false)]
    [InlineData(LinuxAdjtimex.TIME_OOP, false, false, false, true, false)]
    [InlineData(LinuxAdjtimex.TIME_WAIT, false, false, false, false, true)]
    [InlineData(LinuxAdjtimex.TIME_ERROR, true, false, false, false, false)]
    [InlineData(99, false, false, false, false, false)] // Future unknown kernel state
    public void Kernel_states_mapped_correctly(
        int state,
        bool expectedUnsync,
        bool expectedLeapIns,
        bool expectedLeapDel,
        bool expectedLeapOop,
        bool expectedLeapWait)
    {
        var timex = new LinuxTimex();
        var prov = LinuxTimeSyncProvenance.FromTimex(state, in timex);

        Assert.True(prov.Available);
        Assert.Equal(state, prov.RawKernelState);
        Assert.Equal(expectedUnsync, prov.Unsynchronized);
        Assert.Equal(expectedLeapIns, prov.LeapInsertPending);
        Assert.Equal(expectedLeapDel, prov.LeapDeletePending);
        Assert.Equal(expectedLeapOop, prov.LeapInProgress);
        Assert.Equal(expectedLeapWait, prov.LeapCompleted);
    }

    [Fact]
    public void Status_flags_and_raw_facts_decoded_accurately()
    {
        var timex = new LinuxTimex
        {
            Status = LinuxAdjtimex.STA_PLL |
                     LinuxAdjtimex.STA_PPSTIME |
                     LinuxAdjtimex.STA_PPSSIGNAL |
                     LinuxAdjtimex.STA_NANO |
                     LinuxAdjtimex.STA_CLOCKERR,
            Offset = -420,
            Freq = 65536 * 12 + 32768, // 12.5 ppm
            Maxerror = 1500,
            Esterror = 350,
            Precision = 1,
            Tolerance = 32768000,
            Tai = 37,
        };

        var prov = LinuxTimeSyncProvenance.FromTimex(LinuxAdjtimex.TIME_OK, in timex);

        Assert.True(prov.PllEnabled);
        Assert.False(prov.FllEnabled);
        Assert.True(prov.PpsTimeDiscipline);
        Assert.True(prov.PpsSignalPresent);
        Assert.True(prov.NanosecondMode);
        Assert.True(prov.ClockError);
        Assert.False(prov.Unsynchronized); // STA_UNSYNC not set, TIME_OK

        Assert.Equal(-420, prov.OffsetRaw);
        Assert.Equal(timex.Freq, prov.FrequencyRaw);
        Assert.Equal(12.5, prov.FrequencyPpm, precision: 4);
        Assert.Equal(1500, prov.MaximumErrorMicroseconds);
        Assert.Equal(350, prov.EstimatedErrorMicroseconds);
        Assert.Equal(1, prov.PrecisionRaw);
        Assert.Equal(32768000, prov.ToleranceRaw);
        Assert.Equal(37, prov.TaiOffsetSeconds);
        Assert.Equal("adjtimex(modes=0)", prov.Source);
        Assert.Null(prov.FailureReason);
    }

    [Fact]
    public void Leap_state_with_STA_INS_is_not_flagged_as_unsynchronized()
    {
        var timex = new LinuxTimex
        {
            Status = LinuxAdjtimex.STA_PLL | LinuxAdjtimex.STA_INS,
        };

        var prov = LinuxTimeSyncProvenance.FromTimex(LinuxAdjtimex.TIME_INS, in timex);

        Assert.True(prov.LeapInsertPending);
        Assert.False(prov.Unsynchronized);
        Assert.False(prov.ClockError);
    }

    [Fact]
    public void TIME_ERROR_without_STA_UNSYNC_is_marked_unsynchronized_and_retains_raw_state()
    {
        var timex = new LinuxTimex
        {
            Status = LinuxAdjtimex.STA_PLL, // STA_UNSYNC not set
        };

        var prov = LinuxTimeSyncProvenance.FromTimex(LinuxAdjtimex.TIME_ERROR, in timex);

        Assert.Equal(LinuxAdjtimex.TIME_ERROR, prov.RawKernelState);
        Assert.True(prov.Unsynchronized);
    }

    [Fact]
    public void STA_UNSYNC_with_TIME_OK_is_marked_unsynchronized()
    {
        var timex = new LinuxTimex
        {
            Status = LinuxAdjtimex.STA_UNSYNC,
        };

        var prov = LinuxTimeSyncProvenance.FromTimex(LinuxAdjtimex.TIME_OK, in timex);

        Assert.Equal(LinuxAdjtimex.TIME_OK, prov.RawKernelState);
        Assert.True(prov.Unsynchronized);
    }

    [Fact]
    public void Provider_CaptureTimeSyncProvenance_with_failing_native_returns_Unavailable_without_throwing()
    {
        var fakeAdj = new FakeLinuxAdjtimex { ReturnCode = -13 }; // errno 13 (EACCES)
        var provider = new LinuxTimeObservationProvider(
            new FakeLinuxClock(),
            null,
            fakeAdj);

        var prov = provider.CaptureTimeSyncProvenance();

        Assert.False(prov.Available);
        Assert.NotNull(prov.FailureReason);
        Assert.Contains("13", prov.FailureReason);
        Assert.True(prov.Unsynchronized);
    }

    [Fact]
    public void Provider_CaptureTimeSyncProvenance_with_successful_native_returns_live_provenance()
    {
        var fakeAdj = new FakeLinuxAdjtimex
        {
            ReturnCode = LinuxAdjtimex.TIME_OK,
            SampleToReturn = new LinuxTimex
            {
                Status = LinuxAdjtimex.STA_PLL | LinuxAdjtimex.STA_NANO,
                Freq = 65536 * 10, // 10 ppm
                Maxerror = 500,
                Tai = 37,
            },
        };

        var provider = new LinuxTimeObservationProvider(
            new FakeLinuxClock(),
            null,
            fakeAdj);

        var prov = provider.CaptureTimeSyncProvenance();

        Assert.True(prov.Available);
        Assert.Equal(LinuxAdjtimex.TIME_OK, prov.RawKernelState);
        Assert.True(prov.PllEnabled);
        Assert.True(prov.NanosecondMode);
        Assert.Equal(10.0, prov.FrequencyPpm);
        Assert.Equal(37, prov.TaiOffsetSeconds);
    }

    private sealed class FakeLinuxClock : ILinuxNativeClock
    {
        public void GetTime(int clockId, out LinuxTimeSpec timeSpec)
        {
            timeSpec = new LinuxTimeSpec { TvSec = 100, TvNsec = 0 };
        }
    }

    private sealed class FakeLinuxAdjtimex : ILinuxAdjtimex
    {
        public int ReturnCode { get; set; } = LinuxAdjtimex.TIME_OK;
        public LinuxTimex SampleToReturn { get; set; } = new LinuxTimex();

        public int Query(ref LinuxTimex timex)
        {
            timex = SampleToReturn;
            return ReturnCode;
        }
    }
}
