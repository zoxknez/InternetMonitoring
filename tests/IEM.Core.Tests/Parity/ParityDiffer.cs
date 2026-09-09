using System.Text;
using System.Text.Json;

namespace IEM.Core.Tests.Parity;

public enum ParityVerdict
{
    Identical,
    Allowed,
    Forbidden,
}

/// <summary>One line of the diff the harness prints (roadmap 25.9).</summary>
public sealed record ParityDiffLine(string Path, string Windows, string Linux, ParityVerdict Verdict, string Reason)
{
    public override string ToString() =>
        $"{Verdict.ToString().ToUpperInvariant(),-11}{Path}{Environment.NewLine}" +
        $"          windows: {Windows}{Environment.NewLine}" +
        $"          linux:   {Linux}{Environment.NewLine}" +
        $"          reason:  {Reason}";
}

public sealed record ParityDiffResult(IReadOnlyList<ParityDiffLine> Lines)
{
    public bool HasForbidden => Lines.Any(line => line.Verdict == ParityVerdict.Forbidden);

    public IEnumerable<ParityDiffLine> Divergences => Lines.Where(line => line.Verdict != ParityVerdict.Identical);

    public string Report()
    {
        var builder = new StringBuilder();
        foreach (var line in Divergences)
        {
            builder.AppendLine(line.ToString());
        }

        return builder.Length == 0 ? "identical" : builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Compares two <see cref="CanonicalParityView"/>s field by field. A difference not covered by
/// the global registry, the fixture's <c>pathWhitelist</c> or its <c>allowedDivergences</c> is
/// <see cref="ParityVerdict.Forbidden"/> and fails the release (invariant 257).
/// </summary>
public static class ParityDiffer
{
    public static ParityDiffResult Diff(
        CanonicalParityView windows,
        CanonicalParityView linux,
        ParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(linux);
        ArgumentNullException.ThrowIfNull(fixture);

        var lines = new List<ParityDiffLine>();
        var count = Math.Max(windows.Samples.Count, linux.Samples.Count);

        for (var index = 0; index < count; index++)
        {
            if (index >= windows.Samples.Count || index >= linux.Samples.Count)
            {
                lines.Add(new ParityDiffLine(
                    $"$.samples[{index}]",
                    index < windows.Samples.Count ? "present" : "absent",
                    index < linux.Samples.Count ? "present" : "absent",
                    ParityVerdict.Forbidden,
                    "sample count differs between platforms"));
                continue;
            }

            CompareSample(index, windows.Samples[index], linux.Samples[index], fixture, lines);
        }

        CompareIncidents(windows.Incidents, linux.Incidents, fixture, lines);
        Compare("$.sessionVerdictKind", windows.SessionVerdictKind, linux.SessionVerdictKind, fixture, lines);
        Compare("$.claims.supportsComplaint", windows.Claims.SupportsComplaint, linux.Claims.SupportsComplaint, fixture, lines);
        Compare("$.claims.namesOperatorAsFault", windows.Claims.NamesOperatorAsFault, linux.Claims.NamesOperatorAsFault, fixture, lines);
        Compare("$.claims.wifiRadioBlamed", windows.Claims.WifiRadioBlamed, linux.Claims.WifiRadioBlamed, fixture, lines);

        return new ParityDiffResult(lines);
    }

    private static void CompareIncidents(
        IReadOnlyList<CanonicalIncidentView> windows,
        IReadOnlyList<CanonicalIncidentView> linux,
        ParityFixture fixture,
        List<ParityDiffLine> lines)
    {
        var count = Math.Max(windows.Count, linux.Count);
        for (var index = 0; index < count; index++)
        {
            if (index >= windows.Count || index >= linux.Count)
            {
                lines.Add(new ParityDiffLine(
                    $"$.incidents[{index}]",
                    index < windows.Count ? "present" : "absent",
                    index < linux.Count ? "present" : "absent",
                    ParityVerdict.Forbidden,
                    "incident count differs between platforms"));
                continue;
            }

            Compare($"$.incidents[{index}].index", windows[index].Index, linux[index].Index, fixture, lines);
            Compare($"$.incidents[{index}].worstState", windows[index].WorstState, linux[index].WorstState, fixture, lines);
            Compare($"$.incidents[{index}].monotonic.firstBadMs", windows[index].Monotonic.FirstBadMs, linux[index].Monotonic.FirstBadMs, fixture, lines);
            Compare($"$.incidents[{index}].monotonic.lastBadMs", windows[index].Monotonic.LastBadMs, linux[index].Monotonic.LastBadMs, fixture, lines);
            CompareNullable($"$.incidents[{index}].monotonic.firstGoodMs", windows[index].Monotonic.FirstGoodMs, linux[index].Monotonic.FirstGoodMs, fixture, lines);
            Compare($"$.incidents[{index}].endedByGap", windows[index].EndedByGap, linux[index].EndedByGap, fixture, lines);
            Compare($"$.incidents[{index}].routeChanged", windows[index].RouteChanged, linux[index].RouteChanged, fixture, lines);
        }
    }

    private static void CompareNullable(
        string path,
        long? windows,
        long? linux,
        ParityFixture fixture,
        List<ParityDiffLine> lines) =>
        Compare(path, windows?.ToString() ?? "null", linux?.ToString() ?? "null", fixture, lines);

    private static void CompareSample(
        int index,
        CanonicalSampleView windows,
        CanonicalSampleView linux,
        ParityFixture fixture,
        List<ParityDiffLine> lines)
    {
        Compare($"$.samples[{index}].networkState", windows.NetworkState, linux.NetworkState, fixture, lines);
        Compare($"$.samples[{index}].isOutage", windows.IsOutage, linux.IsOutage, fixture, lines);
        Compare($"$.samples[{index}].anyExternalReachability", windows.AnyExternalReachability, linux.AnyExternalReachability, fixture, lines);
        Compare($"$.samples[{index}].pathProvesLink", windows.PathProvesLink, linux.PathProvesLink, fixture, lines);
        Compare($"$.samples[{index}].tallies.gateway.attempted", windows.Tallies.Gateway.Attempted, linux.Tallies.Gateway.Attempted, fixture, lines);
        Compare($"$.samples[{index}].tallies.gateway.succeeded", windows.Tallies.Gateway.Succeeded, linux.Tallies.Gateway.Succeeded, fixture, lines);
        Compare($"$.samples[{index}].tallies.externalIcmp.attempted", windows.Tallies.ExternalIcmp.Attempted, linux.Tallies.ExternalIcmp.Attempted, fixture, lines);
        Compare($"$.samples[{index}].tallies.externalIcmp.succeeded", windows.Tallies.ExternalIcmp.Succeeded, linux.Tallies.ExternalIcmp.Succeeded, fixture, lines);
        Compare($"$.samples[{index}].tallies.externalTcp.attempted", windows.Tallies.ExternalTcp.Attempted, linux.Tallies.ExternalTcp.Attempted, fixture, lines);
        Compare($"$.samples[{index}].tallies.externalTcp.succeeded", windows.Tallies.ExternalTcp.Succeeded, linux.Tallies.ExternalTcp.Succeeded, fixture, lines);
    }

    private static void Compare(string path, object windows, object linux, ParityFixture fixture, List<ParityDiffLine> lines)
    {
        var windowsText = Render(windows);
        var linuxText = Render(linux);

        if (string.Equals(windowsText, linuxText, StringComparison.Ordinal))
        {
            lines.Add(new ParityDiffLine(path, windowsText, linuxText, ParityVerdict.Identical, "match"));
            return;
        }

        var allowed = fixture.AllowedDivergences.FirstOrDefault(d => PathMatches(d.Path, path));
        if (allowed is not null && ValueMatches(allowed.Windows, windows) && ValueMatches(allowed.Linux, linux))
        {
            lines.Add(new ParityDiffLine(path, windowsText, linuxText, ParityVerdict.Allowed, allowed.Reason));
            return;
        }

        var whitelisted = fixture.PathWhitelist.FirstOrDefault(w => PathMatches(w.Path, path));
        if (whitelisted is not null)
        {
            lines.Add(new ParityDiffLine(path, windowsText, linuxText, ParityVerdict.Allowed, $"pathWhitelist: {whitelisted.Reason}"));
            return;
        }

        var hint = allowed is null
            ? "not in pathWhitelist or allowedDivergences"
            : "allowedDivergences entry exists but its windows/linux values do not match the observed ones";
        lines.Add(new ParityDiffLine(path, windowsText, linuxText, ParityVerdict.Forbidden, hint));
    }

    private static bool PathMatches(string declared, string actual)
    {
        if (string.Equals(declared, actual, StringComparison.Ordinal))
        {
            return true;
        }

        // Declared "$.samples[*].x" matches actual "$.samples[3].x".
        var declaredParts = declared.Split('.');
        var actualParts = actual.Split('.');
        if (declaredParts.Length != actualParts.Length)
        {
            return false;
        }

        for (var i = 0; i < declaredParts.Length; i++)
        {
            if (declaredParts[i] == actualParts[i])
            {
                continue;
            }

            if (declaredParts[i].EndsWith("[*]", StringComparison.Ordinal) &&
                actualParts[i].StartsWith(declaredParts[i][..^3] + "[", StringComparison.Ordinal))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool ValueMatches(JsonElement declared, object observed) =>
        string.Equals(RenderJson(declared), Render(observed), StringComparison.Ordinal);

    private static string Render(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => s,
        _ => value.ToString() ?? string.Empty,
    };

    private static string RenderJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => element.GetRawText(),
    };
}
