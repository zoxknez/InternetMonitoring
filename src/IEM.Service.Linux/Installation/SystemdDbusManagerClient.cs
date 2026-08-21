using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Managed D-Bus client for org.freedesktop.systemd1.Manager on the Linux system bus.
/// Invariant 8E-R1-R2: Absence truth is determined strictly via exact upstream systemd D-Bus ErrorNames:
/// - GetUnit: org.freedesktop.systemd1.NoSuchUnit
/// - GetUnitFileState: org.freedesktop.DBus.Error.FileNotFound (standard sd-bus ENOENT mapping)
/// All other D-Bus and system errors propagate and fail closed to Unknown.
/// </summary>
public sealed class SystemdDbusManagerClient : ISystemdDbusManager
{
    public const string NoSuchUnitError = "org.freedesktop.systemd1.NoSuchUnit";
    public const string UnitFileNotFoundError = "org.freedesktop.DBus.Error.FileNotFound";

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
        catch (DBusErrorReplyException ex) when (ex.ErrorName == NoSuchUnitError)
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
        catch (DBusErrorReplyException ex) when (ex.ErrorName == UnitFileNotFoundError)
        {
            return null;
        }
    }
}
