using System.Net;

namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Parsed kernel FIB lookup response from an RTM_GETROUTE Netlink query.
/// Preserves full kernel routing provenance while strictly adhering to internal adapter bounds.
/// </summary>
public sealed record NetlinkRouteResponse(
    bool IsSuccess,
    IPAddress Destination,
    int? InterfaceIndex = null,
    IPAddress? PreferredSource = null,
    IPAddress? Gateway = null,
    bool IsMultipath = false,
    int? NativeErrorCode = null,
    string? ErrorMessage = null,
    uint Sequence = 0)
{
    public static NetlinkRouteResponse CreateFailure(
        IPAddress destination,
        string errorMessage,
        int? nativeErrorCode = null,
        uint sequence = 0) =>
        new(
            IsSuccess: false,
            Destination: destination,
            NativeErrorCode: nativeErrorCode,
            ErrorMessage: errorMessage,
            Sequence: sequence);
}
