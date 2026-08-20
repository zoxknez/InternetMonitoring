using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;
using IEM.Linux.Wifi;

namespace IEM.WifiRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("==============================================================================");
        Console.WriteLine("3.1-7B · LINUX BARE-METAL WI-FI ACCEPTANCE RUNNER");
        Console.WriteLine("==============================================================================");

        string? targetInterface = null;
        string? jsonPath = null;
        string? markdownPath = null;
        int trafficDurationSeconds = 2;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--interface" && i + 1 < args.Length)
            {
                targetInterface = args[++i];
            }
            else if (args[i] == "--json" && i + 1 < args.Length)
            {
                jsonPath = args[++i];
            }
            else if (args[i] == "--markdown" && i + 1 < args.Length)
            {
                markdownPath = args[++i];
            }
            else if (args[i] == "--traffic-seconds" && i + 1 < args.Length && int.TryParse(args[++i], out var sec))
            {
                trafficDurationSeconds = sec;
            }
        }

        var report = new WifiAcceptanceReport
        {
            TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            OsDescription = RuntimeInformation.OSDescription,
            RequestedInterface = targetInterface
        };

        // 1. Process and Capability Verification
        string[]? statusLines = null;
        try
        {
            if (File.Exists("/proc/self/status"))
            {
                statusLines = File.ReadAllLines("/proc/self/status");
            }
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"Failed reading /proc/self/status: {ex.Message}");
        }

        var (zeroCapVerdict, capEff, capAmb, capReason) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(statusLines);
        report.CapEff = capEff;
        report.CapAmb = capAmb;
        report.Verdicts["ZeroCapabilities"] = zeroCapVerdict;
        if (capReason != null) report.GateReasons["ZeroCapabilities"] = capReason;

        // 2. Production Composition Root Link Inspection & Direct Kernel Observation
        LinkSnapshot? initialSnapshot = null;
        LinuxComposedAssociationObservation? t0Composed = null;
        LinuxComposedAssociationObservation? t1Composed = null;
        WirelessAccessPoint? resolvedAp = null;

        await using (var scope = await LinuxProbeFactory.Instance.CreateLinkInspectionAsync(targetInterface))
        {
            initialSnapshot = scope.Inspector.Inspect();
            report.InitialSnapshot = new SnapshotDto
            {
                InterfaceName = initialSnapshot.InterfaceName,
                InterfaceId = initialSnapshot.InterfaceId,
                Status = initialSnapshot.Status.ToString(),
                Medium = initialSnapshot.Medium.ToString(),
                IsUp = initialSnapshot.IsUp,
                WirelessSsid = initialSnapshot.Wireless?.Ssid,
                WirelessBssid = initialSnapshot.Wireless?.Bssid,
                SignalQuality = initialSnapshot.Wireless?.SignalQualityPercent,
                Channel = initialSnapshot.Wireless?.Channel,
                RssiDbm = initialSnapshot.Wireless?.MeasuredRssiDbm,
                RadioOn = initialSnapshot.Wireless?.RadioOn
            };

            if (scope is LinuxLinkInspectionScope linuxScope)
            {
                var radio = linuxScope.WifiInspector.Radio;
                var effectiveInterface = targetInterface ?? initialSnapshot.InterfaceName;
                report.EffectiveInterface = effectiveInterface;

                if (!string.IsNullOrWhiteSpace(effectiveInterface))
                {
                    Console.WriteLine($"\n[1/4] Querying LinuxNl80211Radio for interface: {effectiveInterface}");

                    t0Composed = await radio.ReadComposedAssociationObservationAsync(effectiveInterface);
                    if (t0Composed != null)
                    {
                        report.ObservationT0 = MapObservationDto(t0Composed);

                        // 3. Traffic Monotonicity Check
                        if (t0Composed.State == LinuxWirelessAssociationState.Associated && t0Composed.StationInfo != null && trafficDurationSeconds > 0)
                        {
                            Console.WriteLine($"\n[2/4] Associated to SSID='{t0Composed.Links.FirstOrDefault()?.DisplaySsid}'. Generating traffic ({trafficDurationSeconds}s) to verify counter monotonicity...");
                            await GenerateTestTrafficAsync(trafficDurationSeconds);

                            t1Composed = await radio.ReadComposedAssociationObservationAsync(effectiveInterface);
                            if (t1Composed != null)
                            {
                                report.ObservationT1 = MapObservationDto(t1Composed);
                            }
                        }

                        // 4. Access Point Scan Cache Resolution
                        if (t0Composed.Links.Count > 0 && !string.IsNullOrEmpty(t0Composed.Links[0].DisplaySsid) && !string.IsNullOrEmpty(t0Composed.Links[0].Bssid))
                        {
                            var link0 = t0Composed.Links[0];
                            Console.WriteLine($"\n[3/4] Resolving Access Point evidence for BSSID '{link0.Bssid}' (SSID='{link0.DisplaySsid}')...");
                            resolvedAp = await radio.ReadAccessPointAsync(effectiveInterface, link0.DisplaySsid!, link0.Bssid!);
                            if (resolvedAp != null)
                            {
                                report.AccessPoint = new AccessPointDto
                                {
                                    Bssid = resolvedAp.Bssid,
                                    Channel = resolvedAp.Channel,
                                    Rssi = resolvedAp.Rssi
                                };
                            }
                        }
                    }
                }
            }
        }

        // 5. Evaluate All Gates using LinuxWifiAcceptanceEvaluator
        Console.WriteLine("\n[4/4] Evaluating Acceptance Gates...");
        var expectedIface = report.EffectiveInterface ?? targetInterface ?? string.Empty;

        SetGateResult(report, "InterfaceIdentity", LinuxWifiAcceptanceEvaluator.EvaluateInterfaceIdentity(expectedIface, initialSnapshot, t0Composed));
        SetGateResult(report, "AssociationTruth", LinuxWifiAcceptanceEvaluator.EvaluateAssociationTruth(t0Composed));
        SetGateResult(report, "ContinuityTruth", LinuxWifiAcceptanceEvaluator.EvaluateContinuityTruth(t0Composed));
        SetGateResult(report, "ProductionProjectionTruth", LinuxWifiAcceptanceEvaluator.EvaluateProductionProjectionTruth(initialSnapshot, t0Composed));
        SetGateResult(report, "StationPeerTruth", LinuxWifiAcceptanceEvaluator.EvaluateStationPeerTruth(t0Composed));
        SetGateResult(report, "CachedBssTruth", LinuxWifiAcceptanceEvaluator.EvaluateCachedBssTruth(t0Composed, resolvedAp));
        SetGateResult(report, "AccessPointEvidence", LinuxWifiAcceptanceEvaluator.EvaluateAccessPointEvidence(t0Composed, resolvedAp));
        SetGateResult(report, "NumericFidelity", LinuxWifiAcceptanceEvaluator.EvaluateNumericFidelity(t0Composed, t1Composed));
        SetGateResult(report, "MloHardwareQualification", LinuxWifiAcceptanceEvaluator.EvaluateMloHardwareQualification(t0Composed));

        // 6. Compute Overall Verdict & Exit Code
        var (overallVerdict, exitCode) = LinuxWifiAcceptanceEvaluator.ComputeOverallVerdict(report.Verdicts);
        report.OverallVerdict = overallVerdict;
        report.ExitCode = exitCode;

        // Print Summary to Console
        PrintConsoleSummary(report);

        // Output JSON & Markdown Artifacts
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            var jsonDir = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(jsonDir)) Directory.CreateDirectory(jsonDir);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(jsonPath, json);
            Console.WriteLine($"\nWrote JSON report: {jsonPath}");
        }

        if (!string.IsNullOrWhiteSpace(markdownPath))
        {
            var mdDir = Path.GetDirectoryName(markdownPath);
            if (!string.IsNullOrEmpty(mdDir)) Directory.CreateDirectory(mdDir);
            var md = GenerateMarkdownReport(report);
            await File.WriteAllTextAsync(markdownPath, md);
            Console.WriteLine($"Wrote Markdown report: {markdownPath}");
        }

        return exitCode;
    }

    private static void SetGateResult(WifiAcceptanceReport report, string gate, (string Verdict, string? Reason) result)
    {
        report.Verdicts[gate] = result.Verdict;
        if (!string.IsNullOrEmpty(result.Reason))
        {
            report.GateReasons[gate] = result.Reason;
        }
    }

    private static async Task GenerateTestTrafficAsync(int seconds)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(seconds + 2) };
            var stopAt = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < stopAt)
            {
                try
                {
                    _ = await client.GetByteArrayAsync("http://www.google.com/generate_204");
                }
                catch
                {
                    // Best effort
                }
                await Task.Delay(200);
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static ComposedObservationDto MapObservationDto(LinuxComposedAssociationObservation obs)
    {
        return new ComposedObservationDto
        {
            IfIndex = obs.IfIndex,
            IfName = obs.IfName,
            WiphyIndex = obs.WiphyIndex,
            Wdev = obs.Wdev,
            State = obs.State.ToString(),
            ContinuityVerified = obs.ContinuityVerified,
            Generation = obs.Generation,
            Links = obs.Links.Select(l => new LinkDto
            {
                Bssid = l.Bssid,
                DisplaySsid = l.DisplaySsid,
                FrequencyMhz = l.FrequencyMhz,
                SignalMbm = l.SignalMbm,
                SignalQuality = l.SignalUnspec,
                MloLinkId = l.MloLinkId,
                MldAddress = l.MldAddress
            }).ToList(),
            StationInfo = obs.StationInfo == null ? null : new StationInfoDto
            {
                PeerMac = obs.StationInfo.PeerMacString,
                SignalDbm = obs.StationInfo.SignalDbm,
                SignalAverageDbm = obs.StationInfo.SignalAverageDbm,
                RxBytes = obs.StationInfo.RxBytes,
                TxBytes = obs.StationInfo.TxBytes,
                RxPackets = obs.StationInfo.RxPackets,
                TxPackets = obs.StationInfo.TxPackets,
                ConnectedTimeSeconds = obs.StationInfo.ConnectedTimeSeconds,
                ExpectedThroughputKbps = obs.StationInfo.ExpectedThroughputKbps,
                AssociationBootTimeNs = obs.StationInfo.AssociationBootTimeNs,
                TxBitrateBps = obs.StationInfo.TxRate?.BitrateBps,
                TxMcs = obs.StationInfo.TxRate?.Mcs
            }
        };
    }

    private static void PrintConsoleSummary(WifiAcceptanceReport report)
    {
        Console.WriteLine("\n==============================================================================");
        Console.WriteLine($"OVERALL VERDICT: {report.OverallVerdict} (Exit Code {report.ExitCode})");
        Console.WriteLine("==============================================================================");
        foreach (var (gate, verdict) in report.Verdicts)
        {
            var color = verdict switch
            {
                WifiAcceptanceVerdict.Pass => ConsoleColor.Green,
                WifiAcceptanceVerdict.Fail => ConsoleColor.Red,
                _ => ConsoleColor.Yellow
            };
            Console.ForegroundColor = color;
            var reasonSuffix = report.GateReasons.TryGetValue(gate, out var reason) ? $" ({reason})" : string.Empty;
            Console.WriteLine($"  [{verdict,-14}] {gate}{reasonSuffix}");
            Console.ResetColor();
        }
        Console.WriteLine("==============================================================================");
    }

    private static string GenerateMarkdownReport(WifiAcceptanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 3.1-7B · Linux Bare-Metal Wi-Fi Acceptance Report");
        sb.AppendLine();
        sb.AppendLine($"- **Overall Verdict**: **{report.OverallVerdict}** (Exit Code `{report.ExitCode}`)");
        sb.AppendLine($"- **Timestamp UTC**: `{report.TimestampUtc}`");
        sb.AppendLine($"- **Requested Interface**: `{report.RequestedInterface ?? "auto"}`");
        sb.AppendLine($"- **Effective Interface**: `{report.EffectiveInterface ?? "UNKNOWN"}`");
        sb.AppendLine($"- **Architecture**: `{report.Architecture}`");
        sb.AppendLine($"- **OS Description**: `{report.OsDescription}`");
        sb.AppendLine($"- **Capabilities (CapEff / CapAmb)**: `{report.CapEff ?? "UNKNOWN"}` / `{report.CapAmb ?? "UNKNOWN"}`");
        sb.AppendLine();
        sb.AppendLine("## Gate Verdicts Matrix");
        sb.AppendLine();
        sb.AppendLine("| Gate | Category | Verdict | Reason / Details |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (gate, verdict) in report.Verdicts)
        {
            bool isMandatory = LinuxWifiAcceptanceEvaluator.MandatoryGates.Contains(gate);
            var category = isMandatory ? "Mandatory" : "Optional";
            var reason = report.GateReasons.TryGetValue(gate, out var r) ? r : "-";
            sb.AppendLine($"| `{gate}` | {category} | **{verdict}** | {reason} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Production Link Snapshot");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(report.InitialSnapshot, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Composed Kernel Observation (T0)");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(JsonSerializer.Serialize(report.ObservationT0, new JsonSerializerOptions { WriteIndented = true }));
        sb.AppendLine("```");
        sb.AppendLine();
        if (report.ObservationT1 != null)
        {
            sb.AppendLine("## Composed Kernel Observation (T1 Post-Traffic)");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(JsonSerializer.Serialize(report.ObservationT1, new JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public sealed class WifiAcceptanceReport
{
    public string OverallVerdict { get; set; } = WifiAcceptanceVerdict.NotTested;
    public int ExitCode { get; set; } = 2;
    public string TimestampUtc { get; set; } = string.Empty;
    public string? RequestedInterface { get; set; }
    public string? EffectiveInterface { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string OsDescription { get; set; } = string.Empty;
    public string? CapEff { get; set; }
    public string? CapAmb { get; set; }
    public Dictionary<string, string> Verdicts { get; set; } = new();
    public Dictionary<string, string> GateReasons { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public SnapshotDto? InitialSnapshot { get; set; }
    public ComposedObservationDto? ObservationT0 { get; set; }
    public ComposedObservationDto? ObservationT1 { get; set; }
    public AccessPointDto? AccessPoint { get; set; }
}

public sealed class SnapshotDto
{
    public string? InterfaceName { get; set; }
    public string? InterfaceId { get; set; }
    public string? Status { get; set; }
    public string? Medium { get; set; }
    public bool IsUp { get; set; }
    public string? WirelessSsid { get; set; }
    public string? WirelessBssid { get; set; }
    public int? SignalQuality { get; set; }
    public int? Channel { get; set; }
    public int? RssiDbm { get; set; }
    public bool? RadioOn { get; set; }
}

public sealed class ComposedObservationDto
{
    public int IfIndex { get; set; }
    public string? IfName { get; set; }
    public uint WiphyIndex { get; set; }
    public ulong? Wdev { get; set; }
    public string? State { get; set; }
    public bool ContinuityVerified { get; set; }
    public uint? Generation { get; set; }
    public List<LinkDto> Links { get; set; } = new();
    public StationInfoDto? StationInfo { get; set; }
}

public sealed class LinkDto
{
    public string? Bssid { get; set; }
    public string? DisplaySsid { get; set; }
    public uint? FrequencyMhz { get; set; }
    public int? SignalMbm { get; set; }
    public byte? SignalQuality { get; set; }
    public byte? MloLinkId { get; set; }
    public string? MldAddress { get; set; }
}

public sealed class StationInfoDto
{
    public string? PeerMac { get; set; }
    public int? SignalDbm { get; set; }
    public int? SignalAverageDbm { get; set; }
    public ulong? RxBytes { get; set; }
    public ulong? TxBytes { get; set; }
    public uint? RxPackets { get; set; }
    public uint? TxPackets { get; set; }
    public uint? ConnectedTimeSeconds { get; set; }
    public uint? ExpectedThroughputKbps { get; set; }
    public ulong? AssociationBootTimeNs { get; set; }
    public ulong? TxBitrateBps { get; set; }
    public byte? TxMcs { get; set; }
}

public sealed class AccessPointDto
{
    public string? Bssid { get; set; }
    public int? Channel { get; set; }
    public int? Rssi { get; set; }
}
