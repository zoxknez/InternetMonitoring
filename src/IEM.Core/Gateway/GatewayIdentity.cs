using IEM.Core.Probes;

namespace IEM.Core.Gateway;

/// <summary>
/// Stable identity of a default gateway and its network attachment context.
/// Invariant 53: GATEWAY_CAPABILITY_IS_SCOPED_TO_GATEWAY_IDENTITY_AND_NETWORK_CONTEXT.
/// </summary>
public sealed record GatewayIdentity(
    string GatewayId,
    string GatewayAddress,
    TargetAddressFamily AddressFamily,
    string InterfaceId,
    string InterfaceAddress,
    string? RouteContextRef = null)
{
    public string UniqueKey => $"{GatewayId}:{AddressFamily}:{InterfaceId}:{GatewayAddress}";
}
