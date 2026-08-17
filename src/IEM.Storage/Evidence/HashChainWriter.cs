using System.Text;

namespace IEM.Storage.Evidence;

/// <summary>
/// Append-only writer for the raw evidence log.
/// <para>
/// This file is the source of truth. The SQLite database alongside it is a derived index
/// that can be rebuilt from this; the reverse is not true.
/// </para>
/// <para>
/// Durability is deliberate rather than incidental. Every entry is pushed to the
/// operating system immediately, and anything that matters - a state change, an incident,
/// the end of a session - is forced all the way to the platter. A monitor whose whole
/// purpose is to survive a machine losing power has no business buffering its findings
/// in user space.
/// </para>
/// </summary>
public sealed class HashChainWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    private long _nextEntryNumber;
    private bool _disposed;

    private HashChainWriter(FileStream stream, string headHash, long nextEntryNumber)
    {
        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };

        HeadHash = headHash;
        _nextEntryNumber = nextEntryNumber;
    }

    /// <summary>Hash of the most recent entry. This is what the next entry chains from.</summary>
    public string HeadHash { get; private set; }

    /// <summary>Entries written by this instance.</summary>
    public long EntriesWritten { get; private set; }

    /// <summary>
    /// Opens a log for appending, continuing an existing chain if there is one.
    /// <para>
    /// A partial final line - the signature of the process being killed mid-write - is
    /// truncated before appending, so a crash costs at most the entry in flight and never
    /// corrupts the chain.
    /// </para>
    /// </summary>
    public static HashChainWriter Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var recovery = ChainVerifier.Recover(path);

        // A break that recovery cannot honestly repair means someone altered the log.
        // Truncating to the first broken entry would destroy the record of the tampering
        // itself - the one thing this product refuses to do everywhere else - so opening
        // for append is refused outright and the caller is left to quarantine the session.
        if (!recovery.CanContinue)
        {
            throw new InvalidOperationException(
                "Sirova evidencija je izmenjena; lanac se ne nastavlja ni ne skraćuje.");
        }

        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

        try
        {
        stream.SetLength(recovery.ValidLength);
        stream.Seek(0, SeekOrigin.End);

        // A log whose final entry is intact but unterminated - a crash after the line was
        // written and before its newline reached the disk. Without this the next entry would
        // be appended onto the end of that line, welding two valid entries into one that
        // hashes to neither.
        EnsureLineTerminated(stream);

        return new HashChainWriter(stream, recovery.HeadHash, recovery.NextEntryNumber);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void EnsureLineTerminated(FileStream stream)
    {
        if (stream.Length == 0)
        {
            return;
        }

        stream.Seek(-1, SeekOrigin.End);
        var last = stream.ReadByte();

        if (last != '\n')
        {
            stream.WriteByte((byte)'\n');
            stream.Flush();
        }
    }

    /// <summary>Appends an entry and returns its number.</summary>
    public long Append(IEvidencePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        lock (_gate)
        {
            var entryNumber = _nextEntryNumber++;
            var body = HashChain.BuildBody(payload.Kind, entryNumber, HeadHash, payload);
            var hash = HashChain.ComputeHash(Encoding.UTF8.GetBytes(body));

            // Splice the hash in as the final field, matching the layout the verifier
            // relies on to recover these exact bytes.
            var line = string.Concat(body.AsSpan(0, body.Length - 1), HashChain.HashFieldPrefix, hash, "\"}");

            _writer.Write(line);
            _writer.Write('\n');
            _writer.Flush();

            HeadHash = hash;
            EntriesWritten++;

            return entryNumber;
        }
    }

    /// <summary>
    /// Forces everything through to the physical disk. Costly, so it is called at the
    /// moments where losing the last second of log would actually matter.
    /// </summary>
    public void FlushToDisk()
    {
        lock (_gate)
        {
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Flushes and closes. Safe to call more than once, because the shutdown path has
    /// several owners and a second call must not turn a completed session into a crash.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _writer.Flush();
            _stream.Flush(flushToDisk: true);
            _writer.Dispose();
        }
    }
}
