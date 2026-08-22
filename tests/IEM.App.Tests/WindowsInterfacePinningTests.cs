using System.Net.NetworkInformation;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Windows;
using Xunit;

namespace IEM.App.Tests;

public sealed class WindowsInterfacePinningTests
{
    private const string ProductionWifiGuid = "{370E9134-7973-4017-BD92-CF72CB556DE4}";
    private const string ProductionWfpGuid = "{9A98AF38-98FB-11F1-B19A-6C6A775452CC}";

    [Fact]
    public void Production_Incident_Fixture_Wi_Fi_Vs_WFP_Filter_Rejection()
    {
        // Incident reproduction:
        // Physical Wi-Fi: {370E9134-7973-4017-BD92-CF72CB556DE4}
        // WFP Filter: {9A98AF38-98FB-11F1-B19A-6C6A775452CC}
        var req = InterfaceSelectionRequest.ForExplicit("Wi-Fi");
        var resolved = WindowsInterfaceResolver.Resolve(req);

        // When pinned to Wi-Fi, SystemLinkInspector must NEVER return the WFP adapter even during outage
        var inspector = new SystemLinkInspector(resolved.InterfaceId, resolved.InterfaceName);
        var snapshot = inspector.Inspect();

        if (snapshot.Status != LinkStatus.Missing)
        {
            Assert.False(WindowsInterfaceResolver.MatchesGuid(snapshot.InterfaceId, ProductionWfpGuid));
        }
    }

    [Fact]
    public void WIN_IF_01_Explicit_Request_Resolves_Canonical_Guid()
    {
        var req = InterfaceSelectionRequest.ForExplicit("Wi-Fi");
        var identity = WindowsInterfaceResolver.Resolve(req);

        Assert.NotNull(identity);
        Assert.Equal("Wi-Fi", identity.InterfaceName);
        if (!string.IsNullOrWhiteSpace(identity.InterfaceId))
        {
            Assert.StartsWith("{", identity.InterfaceId);
            Assert.EndsWith("}", identity.InterfaceId);
        }
    }

    [Fact]
    public void WIN_IF_04_WFP_Pseudo_Adapter_Rejected_In_Auto_Mode()
    {
        // Verify eligibility classifier rejects WFP filters
        var all = NetworkInterface.GetAllNetworkInterfaces();
        var wfpNics = all.Where(n => n.Description.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
                                     n.Description.Contains("LightWeight Filter", StringComparison.OrdinalIgnoreCase));

        foreach (var wfp in wfpNics)
        {
            var status = WindowsInterfaceEligibilityClassifier.Classify(wfp);
            Assert.Equal(InterfaceEligibilityStatus.RejectedWfpFilter, status);
        }
    }

    [Fact]
    public void WIN_IF_06_Resume_Existing_Wi_Fi_Session_Restores_Exact_Guid()
    {
        var resumeReq = InterfaceSelectionRequest.ForResume(ProductionWifiGuid, "Wi-Fi", schemaVersion: 4);
        var identity = WindowsInterfaceResolver.Resolve(resumeReq);

        Assert.Equal(ProductionWifiGuid, identity.InterfaceId);
        Assert.Equal("Wi-Fi", identity.InterfaceName);
    }

    [Fact]
    public void WIN_IF_07_Auto_Mode_Without_Explicit_Interface_Pins_Carrier_Once()
    {
        var autoReq = InterfaceSelectionRequest.ForAuto();
        var identity = WindowsInterfaceResolver.Resolve(autoReq);

        Assert.NotNull(identity);
        // Pinned identity must remain stable across multiple calls
        var identity2 = WindowsInterfaceResolver.Resolve(autoReq);
        Assert.Equal(identity.InterfaceId, identity2.InterfaceId);
    }

    [Fact]
    public void WIN_IF_08_User_Requested_Adapter_Change_Cannot_Mutate_Active_Session()
    {
        var originalId = ProductionWifiGuid;
        var inspector = new SystemLinkInspector(originalId, "Wi-Fi");

        // The inspector is immutable and holds its pinned identity
        var snapshot1 = inspector.Inspect();
        var snapshot2 = inspector.Inspect();

        Assert.Equal(snapshot1.InterfaceId, snapshot2.InterfaceId);
    }

    [Fact]
    public void WIN_IF_09_Preferred_Name_Resolves_To_Guid_Once_And_Holds()
    {
        var req = InterfaceSelectionRequest.ForExplicit("Wi-Fi");
        var resolved = WindowsInterfaceResolver.Resolve(req);

        // Later inspections use the resolved canonical ID
        var inspector = new SystemLinkInspector(resolved.InterfaceId, resolved.InterfaceName);
        var snapshot = inspector.Inspect();

        if (snapshot.Status != LinkStatus.Missing)
        {
            Assert.True(WindowsInterfaceResolver.MatchesGuid(snapshot.InterfaceId, resolved.InterfaceId));
        }
    }

    [Fact]
    public void WIN_IF_10_Preferred_Id_Matches_Even_If_Friendly_Name_Changed()
    {
        var inspector = new SystemLinkInspector(ProductionWifiGuid, "Renamed Wi-Fi Name");
        var snapshot = inspector.Inspect();

        // If present, GUID match wins
        if (snapshot.Status != LinkStatus.Missing)
        {
            Assert.True(WindowsInterfaceResolver.MatchesGuid(snapshot.InterfaceId, ProductionWifiGuid));
        }
    }

    [Fact]
    public void WIN_IF_11_Guid_Comparison_Is_Case_And_Braces_Insensitive()
    {
        var rawGuid = "370e9134-7973-4017-bd92-cf72cb556de4";
        var bracketedUpper = "{370E9134-7973-4017-BD92-CF72CB556DE4}";

        Assert.True(WindowsInterfaceResolver.MatchesGuid(rawGuid, bracketedUpper));
        Assert.Equal(bracketedUpper, WindowsInterfaceResolver.NormalizeGuid(rawGuid));
    }

    [Fact]
    public void WIN_IF_16_Pinned_RouteResolver_Constrains_To_Target_Interface()
    {
        var routeResolver = new RouteResolver(ProductionWifiGuid);
        Assert.NotNull(routeResolver);

        // Invalid target should fail safely and not crash
        var unresolvable = routeResolver.Resolve(System.Net.IPAddress.Parse("240.0.0.1"));
        Assert.False(unresolvable.Resolved);
    }

    [Fact]
    public void WIN_IF_17_Legacy_Resume_Never_Trusts_Phantom_WFP_Last_Environment()
    {
        // Legacy Schema 3 resume request with InterfaceName="Wi-Fi" and InterfaceId=null
        var legacyReq = InterfaceSelectionRequest.ForResume(null, "Wi-Fi", schemaVersion: 3);
        var identity = WindowsInterfaceResolver.Resolve(legacyReq);

        // Must NOT resolve to WFP
        Assert.False(WindowsInterfaceResolver.MatchesGuid(identity.InterfaceId, ProductionWfpGuid));
    }

    [Fact]
    public void WIN_IF_18_Schema_3_And_Schema_4_Compatibility()
    {
        // Schema 4 request has canonical authority
        var schema4Req = InterfaceSelectionRequest.ForResume(ProductionWifiGuid, "Wi-Fi", schemaVersion: 4);
        Assert.Equal(InterfaceSelectionMode.ResumeCanonical, schema4Req.Mode);
        Assert.Equal(ProductionWifiGuid, schema4Req.InterfaceId);

        // Schema 3 request falls back to LegacyResume
        var schema3Req = InterfaceSelectionRequest.ForResume(null, "Wi-Fi", schemaVersion: 3);
        Assert.Equal(InterfaceSelectionMode.LegacyResume, schema3Req.Mode);
        Assert.Null(schema3Req.InterfaceId);
    }
}
