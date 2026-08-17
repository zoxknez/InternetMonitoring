using System.Text;

namespace IEM.Storage.Evidence;

/// <param name="FirstBrokenLine">1-based line number where verification failed, if it did.</param>
public sealed record ChainVerification(
    bool Valid,
    long EntriesChecked,
    long? FirstBrokenLine,
    string? Reason,
    string HeadHash)
{
    /// <summary>An existing but empty log: a session that has only just begun.</summary>
    public static ChainVerification Empty => new(true, 0, null, null, HashChain.GenesisHash);

    /// <summary>
    /// No raw log at all.
    /// <para>
    /// Deliberately invalid rather than empty. The raw chain is the evidence; the database
    /// is a derived index that can be rebuilt from it. Treating a missing chain as a valid
    /// empty one meant deleting the inconvenient half of a session and still getting a
    /// report that declared the chain unbroken.
    /// </para>
    /// </summary>
    public static ChainVerification Missing => new(
        false, 0, null, "Sirova evidencija ne postoji", HashChain.GenesisHash);
}

/// <summary>Why a chain stopped verifying, which decides whether it can be written to again.</summary>
public enum ChainBreak
{
    /// <summary>The chain verified end to end.</summary>
    None,

    /// <summary>
    /// The final line is half written - the ordinary signature of a process killed
    /// mid-write. Recoverable: the cost is the one entry that was in flight.
    /// </summary>
    IncompleteFinalEntry,

    /// <summary>An entry's contents no longer hash to what it claims. Someone edited it.</summary>
    ContentsAltered,

    /// <summary>An entry does not chain from its predecessor. A line was inserted or removed.</summary>
    ChainBroken,
}

/// <param name="ValidLength">Byte length of the file up to and including the last intact entry.</param>
/// <param name="TruncatedBytes">Bytes of half-written tail that a crash left behind.</param>
public sealed record ChainRecovery(
    long ValidLength,
    string HeadHash,
    long NextEntryNumber,
    long EntriesRecovered,
    long TruncatedBytes,
    string? BreakReason)
{
    public ChainBreak Break { get; init; }

    /// <summary>
    /// The log can be appended to without extending a falsified record.
    /// <para>
    /// A crash costs the entry that was in flight and nothing else. An altered entry is a
    /// different matter entirely: appending would bury the flaw deeper and lend the doubt
    /// to everything written afterwards.
    /// </para>
    /// </summary>
    public bool CanContinue => Break is ChainBreak.None or ChainBreak.IncompleteFinalEntry;
}

/// <summary>
/// Checks a raw evidence log, and recovers one that a crash cut short.
/// <para>
/// The verifier never parses the payload. It recomputes each line's hash from the bytes
/// on disk and checks that the chain links up, which means a log written by an older
/// build still verifies against a newer one even after the payload schema has moved on.
/// That property is the whole reason the format pins the hash to a fixed-length suffix.
/// </para>
/// </summary>
public static class ChainVerifier
{
    /// <summary>Verifies a whole log and reports where it first fails, if it does.</summary>
    public static ChainVerification Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return ChainVerification.Missing;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Verify(stream);
    }

    public static ChainVerification Verify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var expectedPrevious = HashChain.GenesisHash;
        long lineNumber = 0;
        long checkedEntries = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;

            if (line.Length == 0)
            {
                continue;
            }

            if (!HashChain.TrySplit(line, out var body, out var claimedHash))
            {
                return new ChainVerification(false, checkedEntries, lineNumber, "Line is not a valid chain entry", expectedPrevious);
            }

            var actualHash = HashChain.ComputeHash(Encoding.UTF8.GetBytes(body));
            if (!string.Equals(actualHash, claimedHash, StringComparison.Ordinal))
            {
                return new ChainVerification(false, checkedEntries, lineNumber, "Entry contents do not match its hash", expectedPrevious);
            }

            if (!ContainsPreviousHash(body, expectedPrevious))
            {
                return new ChainVerification(false, checkedEntries, lineNumber, "Entry does not chain from the previous one", expectedPrevious);
            }

            expectedPrevious = claimedHash;
            checkedEntries++;
        }

        return new ChainVerification(true, checkedEntries, null, null, expectedPrevious);
    }

    /// <summary>
    /// Finds how much of a log is intact so writing can resume after a crash.
    /// <para>
    /// Everything after the last intact entry is reported as truncatable. In practice
    /// that is the single line the process was part-way through when it died.
    /// </para>
    /// </summary>
    public static ChainRecovery Recover(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new ChainRecovery(0, HashChain.GenesisHash, 0, 0, 0, null);
        }

        var fileLength = new FileInfo(path).Length;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var expectedPrevious = HashChain.GenesisHash;
        long validLength = 0;
        long entries = 0;
        long nextEntryNumber = 0;
        string? breakReason = null;
        var breakKind = ChainBreak.None;

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                // Blank lines carry nothing but still occupy the file; count them so the
                // truncation point stays byte-accurate.
                validLength += 1;
                continue;
            }

            if (!HashChain.TrySplit(line, out var body, out var claimedHash))
            {
                breakReason = "Incomplete final entry";
                breakKind = ChainBreak.IncompleteFinalEntry;
                break;
            }

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            if (!string.Equals(HashChain.ComputeHash(bodyBytes), claimedHash, StringComparison.Ordinal))
            {
                breakReason = "Entry contents do not match its hash";
                breakKind = ChainBreak.ContentsAltered;
                break;
            }

            if (!ContainsPreviousHash(body, expectedPrevious))
            {
                breakReason = "Entry does not chain from the previous one";
                breakKind = ChainBreak.ChainBroken;
                break;
            }

            expectedPrevious = claimedHash;
            entries++;
            nextEntryNumber = ReadEntryNumber(body) + 1;

            // Line bytes plus the newline. Payload text is UTF-8, so byte length and
            // character length part company as soon as anything Serbian appears.
            validLength += Encoding.UTF8.GetByteCount(line) + 1;
        }

        // Each line was counted as its bytes plus a newline, which the final line need not
        // have. Left uncorrected, a well-formed log with no trailing newline yields a valid
        // length one byte past the end, and SetLength then pads the file with a NUL that
        // breaks the very next entry written.
        validLength = Math.Min(validLength, fileLength);

        return new ChainRecovery(
            validLength,
            expectedPrevious,
            nextEntryNumber,
            entries,
            Math.Max(0, fileLength - validLength),
            breakReason)
        {
            Break = breakKind,
        };
    }

    /// <summary>
    /// Looks for the literal <c>"prev":"…"</c> field. Deliberately a substring check
    /// rather than a parse: the verifier must stay indifferent to the payload schema.
    /// </summary>
    private static bool ContainsPreviousHash(string body, string expected) =>
        body.Contains($"\"prev\":\"{expected}\"", StringComparison.Ordinal);

    private static long ReadEntryNumber(string body)
    {
        const string marker = "\"n\":";

        var start = body.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return -1;
        }

        start += marker.Length;
        var end = start;
        while (end < body.Length && (char.IsDigit(body[end]) || body[end] == '-'))
        {
            end++;
        }

        return long.TryParse(body.AsSpan(start, end - start), out var value) ? value : -1;
    }
}
