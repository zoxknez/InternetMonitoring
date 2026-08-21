using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Managed D-Bus client for org.freedesktop.systemd1.Manager on the Linux system bus.
/// </summary>
public sealed class SystemdDbusManagerClient : ISystemdDbusManager
{
    private const string SystemdDestination = "org.freedesktop.systemd1";
    private const string SystemdPath = "/org/freedesktop/systemd1";
    private const string SystemdManagerInterface = "org.freedesktop.systemd1.Manager";

    public async Task<string?> GetUnitAsync(string unitName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        var address = DBusAddress.System;
        if (string.IsNullOrEmpty(address))
        {
            throw new PlatformNotSupportedException("System bus address is not available on this platform.");
        }

        using var connection = new DBusConnection(address);
        await connection.ConnectAsync().ConfigureAwait(false);

        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: SystemdDestination,
            path: SystemdPath,
            @interface: SystemdManagerInterface,
            member: "GetUnit",
            signature: "s");
        writer.WriteString(unitName);
        var message = writer.CreateMessage();

        try
        {
            return await connection.CallMethodAsync(
                message,
                (Message msg, object? state) => msg.GetBodyReader().ReadObjectPath()).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoSuchUnitError(ex.Message) || IsNoSuchUnitError(ex.GetType().Name))
        {
            return null;
        }
    }

    public async Task<string?> GetUnitFileStateAsync(string unitName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitName);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        var address = DBusAddress.System;
        if (string.IsNullOrEmpty(address))
        {
            throw new PlatformNotSupportedException("System bus address is not available on this platform.");
        }

        using var connection = new DBusConnection(address);
        await connection.ConnectAsync().ConfigureAwait(false);

        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: SystemdDestination,
            path: SystemdPath,
            @interface: SystemdManagerInterface,
            member: "GetUnitFileState",
            signature: "s");
        writer.WriteString(unitName);
        var message = writer.CreateMessage();

        try
        {
            return await connection.CallMethodAsync(
                message,
                (Message msg, object? state) => msg.GetBodyReader().ReadString()).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoSuchUnitError(ex.Message) || IsNoSuchUnitError(ex.GetType().Name))
        {
            return null;
        }
    }

    public static bool IsNoSuchUnitError(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText)) return false;
        return errorText.Contains("NoSuchUnit", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("NoSuchUnitFile", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("NoSuchFile", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
               errorText.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
    }
}
