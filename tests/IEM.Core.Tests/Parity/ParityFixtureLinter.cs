using System.Text.Json;

namespace IEM.Core.Tests.Parity;

/// <summary>
/// Fixture-consistency checks that go beyond shape (roadmap 25.5, invariant 258): a platform's
/// facts may be <em>weaker</em> than the real event, never <em>stronger</em>. A fixture that
/// makes one adapter assert more than <c>world</c> allows is malformed - it would let a diff
/// pass by comparing two already-wrong projections.
/// </summary>
public static class ParityFixtureLinter
{
    public static IReadOnlyList<string> Lint(ParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var violations = new List<string>();
        var worldByTick = ReadWorldByTick(fixture.SemanticInput);

        CheckPlatform("windows", fixture.PlatformFactsWindows, worldByTick, violations);
        CheckPlatform("linux", fixture.PlatformFactsLinux, worldByTick, violations);

        return violations;
    }

    public static void Validate(ParityFixture fixture)
    {
        var violations = Lint(fixture);
        if (violations.Count > 0)
        {
            throw new ParityFixtureFormatException(
                $"'{fixture.FixtureId}' is malformed:{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", violations));
        }
    }

    private static void CheckPlatform(
        string platform,
        JsonElement facts,
        IReadOnlyDictionary<int, WorldFacts> worldByTick,
        List<string> violations)
    {
        if (!facts.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
        {
            violations.Add($"platformFacts.{platform} requires a \"ticks\" array.");
            return;
        }

        foreach (var tick in ticks.EnumerateArray())
        {
            if (!tick.TryGetProperty("seq", out var seqElement) || !seqElement.TryGetInt32(out var seq))
            {
                violations.Add($"platformFacts.{platform}: a tick is missing an integer \"seq\".");
                continue;
            }

            if (!worldByTick.TryGetValue(seq, out var world))
            {
                violations.Add($"platformFacts.{platform} tick {seq} has no matching semanticInput tick.");
                continue;
            }

            CheckNotStronger(
                $"{platform}.tick[{seq}].radioOn",
                TaggedValue.Parse(tick, "radioOn"),
                world.RadioSwitch,
                onValue: "on",
                offValue: "off",
                violations);

            CheckNotStronger(
                $"{platform}.tick[{seq}].ssidVisibleInScan",
                TaggedValue.Parse(tick, "ssidVisibleInScan"),
                world.SsidOnAir,
                onValue: "true",
                offValue: "false",
                violations);
        }
    }

    /// <summary>
    /// A boolean platform fact must not be more certain than the world fact behind it:
    /// world unknown =&gt; platform may not be a firm true/false; world true =&gt; platform may
    /// not be false; world false =&gt; platform may not be true.
    /// </summary>
    private static void CheckNotStronger(
        string label,
        TaggedValue platformFact,
        TaggedValue worldFact,
        string onValue,
        string offValue,
        List<string> violations)
    {
        if (platformFact.Tag != TaggedValueTag.Value)
        {
            return;
        }

        var worldSaysOn = worldFact.IsValueEqualTo(onValue) || worldFact.IsValueEqualTo(true);
        var worldSaysOff = worldFact.IsValueEqualTo(offValue) || worldFact.IsValueEqualTo(false);
        var worldIsCertain = worldSaysOn || worldSaysOff;

        if (!worldIsCertain)
        {
            violations.Add(
                $"{label}={platformFact.Describe()} is stronger than world ({worldFact.Describe()}): " +
                "an uncertain event cannot yield a firm platform fact.");
            return;
        }

        if (worldSaysOn && platformFact.IsValueEqualTo(false))
        {
            violations.Add($"{label}=false contradicts world={worldFact.Describe()}.");
        }

        if (worldSaysOff && platformFact.IsValueEqualTo(true))
        {
            violations.Add($"{label}=true contradicts world={worldFact.Describe()}.");
        }
    }

    private static IReadOnlyDictionary<int, WorldFacts> ReadWorldByTick(JsonElement semanticInput)
    {
        var result = new Dictionary<int, WorldFacts>();
        if (!semanticInput.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var tick in ticks.EnumerateArray())
        {
            if (!tick.TryGetProperty("seq", out var seqElement) || !seqElement.TryGetInt32(out var seq))
            {
                continue;
            }

            var world = tick.TryGetProperty("world", out var worldElement) &&
                        worldElement.ValueKind == JsonValueKind.Object
                ? worldElement
                : default;

            result[seq] = new WorldFacts(
                world.ValueKind == JsonValueKind.Object
                    ? TaggedValue.Parse(world, "radioSwitch")
                    : new TaggedValue(TaggedValueTag.Absent),
                world.ValueKind == JsonValueKind.Object
                    ? TaggedValue.Parse(world, "ssidOnAir")
                    : new TaggedValue(TaggedValueTag.Absent));
        }

        return result;
    }

    private sealed record WorldFacts(TaggedValue RadioSwitch, TaggedValue SsidOnAir);
}
