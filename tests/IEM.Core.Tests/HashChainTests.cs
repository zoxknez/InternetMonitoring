using System.Text;
using IEM.Core.Model;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// Tests for the tamper-evidence of the raw log. If these are wrong, every other number
/// the tool produces is worth nothing, because nothing would stop the file being edited
/// after the fact.
/// </summary>
public sealed class HashChainTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "iem-tests", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_directory, "SirovaEvidencija.jsonl");

    public HashChainTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static GapPayload Gap(int seconds) => new(
        new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero),
        TimeSpan.FromSeconds(seconds),
        GapCause.Unknown);

    private void WriteEntries(int count)
    {
        using var writer = HashChainWriter.Open(LogPath);
        for (var i = 1; i <= count; i++)
        {
            writer.Append(Gap(i));
        }
    }

    [Fact]
    public void An_existing_but_empty_log_verifies()
    {
        // A session that has only just started. Nothing to check, nothing wrong.
        File.WriteAllText(LogPath, string.Empty);

        var result = ChainVerifier.Verify(LogPath);

        Assert.True(result.Valid);
        Assert.Equal(0, result.EntriesChecked);
    }

    /// <summary>
    /// A missing chain is a failure, not an empty success.
    /// <para>
    /// It used to verify as valid, which meant deleting the raw log and keeping the database
    /// produced a report stating the chain was unbroken - the single most useful thing
    /// anyone wanting to inflate a claim could do.
    /// </para>
    /// </summary>
    [Fact]
    public void A_missing_log_does_not_verify()
    {
        var result = ChainVerifier.Verify(LogPath);

        Assert.False(result.Valid);
        Assert.Equal(0, result.EntriesChecked);
    }

    /// <summary>
    /// A well-formed last line with no trailing newline used to yield a valid length one
    /// byte past the end of the file. <c>SetLength</c> then padded it with a NUL, and the
    /// next entry written chained from a line that no longer hashed to what it claimed.
    /// </summary>
    [Fact]
    public void A_log_whose_last_line_has_no_newline_survives_recovery_and_further_writes()
    {
        WriteEntries(3);

        var text = File.ReadAllText(LogPath);
        File.WriteAllText(LogPath, text.TrimEnd('\r', '\n'));

        var recovery = ChainVerifier.Recover(LogPath);

        Assert.Null(recovery.BreakReason);
        Assert.Equal(3, recovery.EntriesRecovered);
        Assert.Equal(new FileInfo(LogPath).Length, recovery.ValidLength);

        using (var writer = HashChainWriter.Open(LogPath))
        {
            writer.Append(Gap(30));
        }

        var result = ChainVerifier.Verify(LogPath);

        Assert.True(result.Valid, result.Reason);
        Assert.Equal(4, result.EntriesChecked);
    }

    [Fact]
    public void A_written_chain_verifies()
    {
        WriteEntries(20);

        var result = ChainVerifier.Verify(LogPath);

        Assert.True(result.Valid);
        Assert.Equal(20, result.EntriesChecked);
        Assert.Null(result.FirstBrokenLine);
    }

    [Fact]
    public void Entries_are_numbered_from_zero_and_chain_from_genesis()
    {
        using (var writer = HashChainWriter.Open(LogPath))
        {
            Assert.Equal(HashChain.GenesisHash, writer.HeadHash);
            Assert.Equal(0, writer.Append(Gap(1)));
            Assert.Equal(1, writer.Append(Gap(2)));
        }

        Assert.Contains($"\"prev\":\"{HashChain.GenesisHash}\"", File.ReadLines(LogPath).First(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The headline property: editing a recorded fact is detected, and the report can
    /// say exactly which entry was touched.
    /// </summary>
    [Fact]
    public void Editing_an_entry_is_detected_and_localised()
    {
        WriteEntries(10);

        var lines = File.ReadAllLines(LogPath);
        lines[4] = lines[4].Replace("\"durationMs\":5000", "\"durationMs\":900000", StringComparison.Ordinal);
        File.WriteAllLines(LogPath, lines);

        var result = ChainVerifier.Verify(LogPath);

        Assert.False(result.Valid);
        Assert.Equal(5, result.FirstBrokenLine);
        Assert.Equal("Entry contents do not match its hash", result.Reason);
    }

    [Fact]
    public void Deleting_an_entry_breaks_the_chain()
    {
        // Removing an inconvenient outage would otherwise leave a perfectly plausible file.
        WriteEntries(10);

        var lines = File.ReadAllLines(LogPath).ToList();
        lines.RemoveAt(4);
        File.WriteAllLines(LogPath, lines);

        var result = ChainVerifier.Verify(LogPath);

        Assert.False(result.Valid);
        Assert.Equal(5, result.FirstBrokenLine);
        Assert.Equal("Entry does not chain from the previous one", result.Reason);
    }

    [Fact]
    public void Reordering_entries_breaks_the_chain()
    {
        WriteEntries(10);

        var lines = File.ReadAllLines(LogPath);
        (lines[3], lines[6]) = (lines[6], lines[3]);
        File.WriteAllLines(LogPath, lines);

        Assert.False(ChainVerifier.Verify(LogPath).Valid);
    }

    [Fact]
    public void Appending_a_forged_entry_at_the_end_is_detected()
    {
        WriteEntries(5);

        File.AppendAllText(
            LogPath,
            "{\"k\":\"Gap\",\"n\":5,\"prev\":\"" + new string('a', 64) + "\",\"p\":{}," +
            "\"h\":\"" + new string('b', 64) + "\"}\n");

        var result = ChainVerifier.Verify(LogPath);

        Assert.False(result.Valid);
        Assert.Equal(6, result.FirstBrokenLine);
    }

    // ---- Crash recovery ---------------------------------------------------

    [Fact]
    public void A_half_written_final_line_is_truncated_and_writing_resumes()
    {
        // What a machine losing power mid-append actually leaves behind. The entry in
        // flight is lost; nothing before it may be.
        WriteEntries(10);

        var lengthBefore = new FileInfo(LogPath).Length;
        File.AppendAllText(LogPath, "{\"k\":\"Sample\",\"n\":10,\"prev\":\"abc");

        var recovery = ChainVerifier.Recover(LogPath);

        Assert.Equal(10, recovery.EntriesRecovered);
        Assert.Equal(lengthBefore, recovery.ValidLength);
        Assert.True(recovery.TruncatedBytes > 0);
        Assert.Equal("Incomplete final entry", recovery.BreakReason);

        using (var writer = HashChainWriter.Open(LogPath))
        {
            writer.Append(Gap(99));
        }

        var verification = ChainVerifier.Verify(LogPath);
        Assert.True(verification.Valid);
        Assert.Equal(11, verification.EntriesChecked);
    }

    [Fact]
    public void Reopening_continues_the_existing_chain()
    {
        WriteEntries(5);

        using (var writer = HashChainWriter.Open(LogPath))
        {
            Assert.NotEqual(HashChain.GenesisHash, writer.HeadHash);
            Assert.Equal(5, writer.Append(Gap(6)));
        }

        var result = ChainVerifier.Verify(LogPath);
        Assert.True(result.Valid);
        Assert.Equal(6, result.EntriesChecked);
    }

    [Fact]
    public void Recovery_of_a_missing_file_starts_from_genesis()
    {
        var recovery = ChainVerifier.Recover(LogPath);

        Assert.Equal(0, recovery.ValidLength);
        Assert.Equal(HashChain.GenesisHash, recovery.HeadHash);
        Assert.Equal(0, recovery.NextEntryNumber);
    }

    /// <summary>
    /// Serbian text is multi-byte in UTF-8, so a truncation point computed from character
    /// counts would land mid-character and corrupt the very file it was protecting.
    /// </summary>
    [Fact]
    public void Recovery_measures_bytes_not_characters()
    {
        using (var writer = HashChainWriter.Open(LogPath))
        {
            writer.Append(new SessionStartPayload(
                "s1", "2.1.0", DateTimeOffset.UtcNow, TimeSpan.FromHours(48),
                "RAČUNAR-ŠĐŽ", "Bežična veza", LinkMedium.Wireless, 1_000_000_000, "192.168.1.1"));
        }

        var recovery = ChainVerifier.Recover(LogPath);

        Assert.Equal(new FileInfo(LogPath).Length, recovery.ValidLength);
        Assert.Equal(0, recovery.TruncatedBytes);
    }

    // ---- Format contract --------------------------------------------------

    [Fact]
    public void Every_payload_type_round_trips_through_the_chain()
    {
        using (var writer = HashChainWriter.Open(LogPath))
        {
            writer.Append(new SessionStartPayload(
                "s1", "2.1.0", DateTimeOffset.UtcNow, TimeSpan.FromHours(48),
                "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1"));

            writer.Append(new SamplePayload(
                1, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), NetworkState.Ok, Severity.Ok,
                "ok", "Stable", LinkStatus.Up, TimeSpan.FromMilliseconds(18),
                new ProbeTally(1, 1), new ProbeTally(3, 3), new ProbeTally(2, 2), new ProbeTally(1, 1),
                new ProbeTally(1, 1), new ProbeTally(1, 1), new ProbeTally(1, 1), new ProbeTally(1, 1),
                false, 80, "AA:BB:CC:DD:EE:FF"));

            writer.Append(new IncidentPayload(
                1, Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(8),
                NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream,
                TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(9),
                80, false, false, false, false, "{TEST}", "{TEST}", "{TEST}",
                [NetworkState.CpeUpstreamUnreachable], "detail"));

            writer.Append(Gap(300));

            writer.Append(new ClockAnomalyPayload(
                DateTimeOffset.UtcNow, IEM.Core.Time.ClockAnomaly.WallClockJump,
                TimeSpan.FromHours(2), TimeSpan.FromSeconds(1)));

            writer.Append(new SessionEndPayload(
                DateTimeOffset.UtcNow, TimeSpan.FromHours(48), TimeSpan.Zero,
                TimeSpan.FromSeconds(8), TimeSpan.Zero, 99.99d, 99.99d, 1, 1));
        }

        var result = ChainVerifier.Verify(LogPath);

        Assert.True(result.Valid, result.Reason);
        Assert.Equal(6, result.EntriesChecked);
    }

    [Fact]
    public void Every_line_is_valid_standalone_json()
    {
        // The raw log has to stay readable by ordinary tooling, otherwise nobody the
        // customer sends it to can do anything with it.
        WriteEntries(5);

        foreach (var line in File.ReadLines(LogPath))
        {
            using var document = System.Text.Json.JsonDocument.Parse(line);
            Assert.Equal("Gap", document.RootElement.GetProperty("k").GetString());
            Assert.Equal(64, document.RootElement.GetProperty("h").GetString()!.Length);
        }
    }

    [Fact]
    public void Identical_payloads_hash_identically()
    {
        // Reproducibility is what makes the chain checkable by someone else at all.
        var body1 = HashChain.BuildBody(EvidenceKind.Gap, 7, HashChain.GenesisHash, Gap(42));
        var body2 = HashChain.BuildBody(EvidenceKind.Gap, 7, HashChain.GenesisHash, Gap(42));

        Assert.Equal(
            HashChain.ComputeHash(Encoding.UTF8.GetBytes(body1)),
            HashChain.ComputeHash(Encoding.UTF8.GetBytes(body2)));
    }
}
