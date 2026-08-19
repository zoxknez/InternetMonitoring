using System;
using System.Globalization;
using System.IO;

namespace IEM.Linux.Time;

/// <summary>
/// Internal Linux kernel boot facts reader.
/// Reads boot_id from /proc/sys/kernel/random/boot_id and uptime from /proc/uptime.
/// Invariants:
/// 100. BOOT_CONTINUITY_IS_NEVER_ASSUMED_WHEN_IDENTITY_EVIDENCE_IS_AMBIGUOUS
/// 110. SERVICE_RESTART_NEVER_IMPLIES_HOST_REBOOT
/// 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY
/// </summary>
internal static class LinuxBootFacts
{
    public const string BootIdPath = "/proc/sys/kernel/random/boot_id";
    public const string UptimePath = "/proc/uptime";

    public const string ReasonBootIdUnavailable = "BOOT_ID_UNAVAILABLE";
    public const string ReasonBootIdReadFailed = "BOOT_ID_READ_FAILED";
    public const string ReasonBootIdEmpty = "BOOT_ID_EMPTY";
    public const string ReasonBootIdMalformed = "BOOT_ID_MALFORMED";
    public const string ReasonBootIdentityAmbiguous = "BOOT_IDENTITY_AMBIGUOUS";
    public const string ReasonBootUptimeUnavailable = "BOOT_UPTIME_UNAVAILABLE";
    public const string ReasonBootUptimeMalformed = "BOOT_UPTIME_MALFORMED";
    public const string ReasonBootUptimeCorrelationMismatch = "BOOT_UPTIME_CORRELATION_MISMATCH";

    public static bool TryReadBootId(
        out string? bootId,
        out string? reasonCode,
        Func<string, string>? fileReader = null)
    {
        fileReader ??= File.ReadAllText;
        bootId = null;

        string raw;
        try
        {
            raw = fileReader(BootIdPath);
        }
        catch (FileNotFoundException)
        {
            reasonCode = ReasonBootIdUnavailable;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            reasonCode = ReasonBootIdUnavailable;
            return false;
        }
        catch
        {
            reasonCode = ReasonBootIdReadFailed;
            return false;
        }

        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            reasonCode = ReasonBootIdEmpty;
            return false;
        }

        if (!Guid.TryParse(trimmed, out var guid))
        {
            reasonCode = ReasonBootIdMalformed;
            return false;
        }

        bootId = $"linux-boot-{guid:D}";
        reasonCode = null;
        return true;
    }

    public static bool TryReadProcUptime(
        out TimeSpan uptime,
        out string? reasonCode,
        Func<string, string>? fileReader = null)
    {
        fileReader ??= File.ReadAllText;
        uptime = TimeSpan.Zero;

        string raw;
        try
        {
            raw = fileReader(UptimePath);
        }
        catch (FileNotFoundException)
        {
            reasonCode = ReasonBootUptimeUnavailable;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            reasonCode = ReasonBootUptimeUnavailable;
            return false;
        }
        catch
        {
            reasonCode = ReasonBootUptimeUnavailable;
            return false;
        }

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            uptime = TimeSpan.FromSeconds(seconds);
            reasonCode = null;
            return true;
        }

        reasonCode = ReasonBootUptimeMalformed;
        return false;
    }
}
