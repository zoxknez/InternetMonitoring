using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using IEM.Core.Model;
using ManagedNativeWifi;

namespace IEM.Windows;

/// <summary>
/// Authoritative Windows interface resolver.
/// Invariants:
/// WIN_SESSION_INTERFACE_IMMUTABLE: GUID is resolved ONCE and pinned.
/// WIN_AUTO_SELECTION_OCCURS_ONLY_BEFORE_SESSION_START: Auto-selection occurs only before session.
/// WIN_RESUME_RESTORES_CANONICAL_INTERFACE_IDENTITY: Resumption restores canonical identity.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsInterfaceResolver
{
    public static MonitoredInterfaceIdentity Resolve(InterfaceSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var all = NetworkInterface.GetAllNetworkInterfaces();

        return request.Mode switch
        {
            InterfaceSelectionMode.ResumeCanonical => ResolveResumeCanonical(request, all),
            InterfaceSelectionMode.LegacyResume => ResolveLegacyResume(request, all),
            InterfaceSelectionMode.Explicit => ResolveExplicit(request, all),
            InterfaceSelectionMode.Auto => ResolveAuto(all),
            _ => ResolveAuto(all),
        };
    }

    private static MonitoredInterfaceIdentity ResolveResumeCanonical(
        InterfaceSelectionRequest request,
        IReadOnlyList<NetworkInterface> all)
    {
        var targetId = request.InterfaceId ?? string.Empty;
        var matching = all.FirstOrDefault(n => MatchesGuid(n.Id, targetId));

        if (matching is not null)
        {
            return new MonitoredInterfaceIdentity(matching.Id, matching.Name);
        }

        // Schema 4 resume: canonical GUID is known even if adapter is absent from current enumeration.
        // Pinned identity remains the canonical GUID so inspector reports Missing and does not fail over.
        return new MonitoredInterfaceIdentity(NormalizeGuid(targetId), request.InterfaceName ?? targetId);
    }

    private static MonitoredInterfaceIdentity ResolveLegacyResume(
        InterfaceSelectionRequest request,
        IReadOnlyList<NetworkInterface> all)
    {
        var name = request.InterfaceName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ResolveAuto(all);
        }

        var matching = all
            .Where(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 1)
        {
            return new MonitoredInterfaceIdentity(matching[0].Id, matching[0].Name);
        }

        if (matching.Count > 1)
        {
            var eligible = matching
                .Where(n => WindowsInterfaceEligibilityClassifier.Classify(n) == InterfaceEligibilityStatus.EligiblePhysicalCarrier)
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();

            if (eligible.Count == 1)
            {
                return new MonitoredInterfaceIdentity(eligible[0].Id, eligible[0].Name);
            }

            throw new InvalidOperationException($"Legacy resume cannot unambiguously resolve interface name '{name}'.");
        }

        // Check native Wi-Fi descriptions
        var wlanGuid = TryResolveNativeWifiGuid(name);
        if (wlanGuid is { } guid)
        {
            return new MonitoredInterfaceIdentity(NormalizeGuid(guid.ToString()), name);
        }

        return new MonitoredInterfaceIdentity(string.Empty, name);
    }

    private static MonitoredInterfaceIdentity ResolveExplicit(
        InterfaceSelectionRequest request,
        IReadOnlyList<NetworkInterface> all)
    {
        var input = request.InterfaceId ?? request.InterfaceName ?? string.Empty;

        if (Guid.TryParse(input.Trim().Trim('{', '}'), out var inputGuid))
        {
            var matchingByGuid = all.FirstOrDefault(n => MatchesGuid(n.Id, inputGuid));
            if (matchingByGuid is not null)
            {
                return new MonitoredInterfaceIdentity(matchingByGuid.Id, matchingByGuid.Name);
            }

            return new MonitoredInterfaceIdentity(NormalizeGuid(inputGuid), request.InterfaceName ?? input);
        }

        // Friendly name
        var matchingByName = all
            .Where(n => string.Equals(n.Name, input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingByName.Count == 1)
        {
            return new MonitoredInterfaceIdentity(matchingByName[0].Id, matchingByName[0].Name);
        }

        if (matchingByName.Count > 1)
        {
            var eligible = matchingByName
                .Where(n => WindowsInterfaceEligibilityClassifier.Classify(n) == InterfaceEligibilityStatus.EligiblePhysicalCarrier)
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
                .ToList();

            if (eligible.Count == 1)
            {
                return new MonitoredInterfaceIdentity(eligible[0].Id, eligible[0].Name);
            }

            throw new InvalidOperationException($"Ambiguous interface name '{input}'. Multiple matches found.");
        }

        var wifiGuid = TryResolveNativeWifiGuid(input);
        if (wifiGuid is { } wlan)
        {
            return new MonitoredInterfaceIdentity(NormalizeGuid(wlan), input);
        }

        return new MonitoredInterfaceIdentity(string.Empty, input);
    }

    private static MonitoredInterfaceIdentity ResolveAuto(IReadOnlyList<NetworkInterface> all)
    {
        var eligible = all
            .Where(n => WindowsInterfaceEligibilityClassifier.Classify(n) == InterfaceEligibilityStatus.EligiblePhysicalCarrier)
            .ToList();

        var selected = eligible
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderByDescending(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .ThenByDescending(n => n.GetIPProperties().DnsAddresses.Count > 0)
            .FirstOrDefault()
            ?? eligible.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up)
            ?? eligible.FirstOrDefault()
            ?? all.FirstOrDefault();

        return selected is null
            ? new MonitoredInterfaceIdentity(string.Empty, string.Empty)
            : new MonitoredInterfaceIdentity(selected.Id, selected.Name);
    }

    public static bool MatchesGuid(string? id, string? target)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        if (Guid.TryParse(id.Trim().Trim('{', '}'), out var idGuid) &&
            Guid.TryParse(target.Trim().Trim('{', '}'), out var targetGuid))
        {
            return idGuid == targetGuid;
        }

        return string.Equals(id, target, StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesGuid(string? id, Guid targetGuid)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (Guid.TryParse(id.Trim().Trim('{', '}'), out var idGuid))
        {
            return idGuid == targetGuid;
        }

        return false;
    }

    public static string NormalizeGuid(Guid guid) => $"{{{guid.ToString().ToUpperInvariant()}}}";

    public static string NormalizeGuid(string? input) =>
        Guid.TryParse(input?.Trim().Trim('{', '}'), out var guid)
            ? NormalizeGuid(guid)
            : input ?? string.Empty;

    private static Guid? TryResolveNativeWifiGuid(string name)
    {
        try
        {
            return NativeWifi.EnumerateInterfaces()
                .FirstOrDefault(i => string.Equals(i.Description, name, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(i.Id.ToString(), name, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
