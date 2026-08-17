using IEM.Storage.Evidence;

namespace IEM.Storage;

/// <param name="RawEntries">Entries read from the chain.</param>
/// <param name="DerivedRecords">Rows the rebuilt index ended up holding.</param>
/// <param name="MissingFromExistingIndex">
/// Records the chain has that the session's own database does not. Non-zero means the index
/// had drifted - usually a crash between the chain write and the database write.
/// </param>
public sealed record IndexRebuild(
    string DatabasePath,
    long RawEntries,
    long DerivedRecords,
    long ExistingIndexRecords,
    long MissingFromExistingIndex)
{
    /// <summary>
    /// Entries the chain carries that this build could not interpret.
    /// <para>
    /// Counted rather than skipped in silence. A log written by a newer build, or one with a
    /// field this version does not understand, would otherwise lose records during the very
    /// operation that is supposed to establish the chain as authoritative - and the report
    /// would come out short with nothing anywhere saying so.
    /// </para>
    /// </summary>
    public long UnreadableEntries { get; init; }

    /// <summary>The database that was found on disk did not match the chain.</summary>
    public bool Reconciled => MissingFromExistingIndex != 0;

    /// <summary>Something in the chain did not survive the rebuild, which must be said out loud.</summary>
    public bool Incomplete => UnreadableEntries > 0;
}

/// <summary>
/// Rebuilds a queryable index from the raw chain alone.
/// <para>
/// The architecture has always claimed the database is a disposable cache over the chain.
/// Until this existed, that claim was untested and, in the one place it mattered, false: the
/// report was built from whatever database happened to be sitting in the folder. Delete the
/// raw log and the report was still produced, still declaring the chain unbroken.
/// </para>
/// <para>
/// So the report is now built from an index reconstructed here, in a temporary file, from
/// entries that were just hash-verified. The database in the session folder is consulted
/// only to be compared against - never trusted.
/// </para>
/// </summary>
public static class EvidenceIndexRebuilder
{
    /// <summary>
    /// Replays the chain into a fresh index and reports how the session's own database
    /// compares. The caller owns the resulting file and should delete it when done.
    /// </summary>
    /// <param name="existingDatabasePath">
    /// The session's own index, consulted only for comparison. Null skips the comparison.
    /// </param>
    public static IndexRebuild Rebuild(string rawLogPath, string targetDatabasePath, string? existingDatabasePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabasePath);

        if (!File.Exists(rawLogPath))
        {
            throw new FileNotFoundException("Sirova evidencija ne postoji.", rawLogPath);
        }

        if (File.Exists(targetDatabasePath))
        {
            File.Delete(targetDatabasePath);
        }

        long entries = 0;
        long derived = 0;
        long unreadable = 0;
        string? sessionId = null;
        var closed = false;
        CheckpointPayload? checkpoint = null;

        using (var store = SqliteSessionStore.Open(targetDatabasePath))
        {
            foreach (var entry in ChainReader.Read(rawLogPath))
            {
                entries++;

                switch (entry.Kind)
                {
                    case EvidenceKind.SessionStart:
                        if (PayloadReader.SessionStart(entry.Payload) is { } start)
                        {
                            // A resumed session writes a second opening entry. The first one
                            // is the session; later ones only restate it.
                            sessionId ??= start.SessionId;
                            store.BeginSession(sessionId, start);
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.Sample:
                        if (sessionId is not null && PayloadReader.Sample(entry.Payload) is { } sample)
                        {
                            store.AddSample(sessionId, sample);
                            derived++;
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.Incident:
                        if (sessionId is not null && PayloadReader.Incident(entry.Payload) is { } incident)
                        {
                            store.AddIncident(sessionId, incident);
                            derived++;
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.Trace:
                        if (sessionId is not null && PayloadReader.Trace(entry.Payload) is { } trace)
                        {
                            store.AddTrace(sessionId, trace);
                            derived++;
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.Gap:
                        if (sessionId is not null && PayloadReader.Gap(entry.Payload) is { } gap)
                        {
                            store.AddGap(sessionId, gap);
                            derived++;
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.SessionEnd:
                        if (sessionId is not null && PayloadReader.SessionEnd(entry.Payload) is { } end)
                        {
                            store.CompleteSession(sessionId, end);
                            closed = true;
                        }
                        else
                        {
                            unreadable++;
                        }

                        break;

                    case EvidenceKind.Checkpoint:
                        // Not indexed as a row, but the last one is the only place a running
                        // session's totals exist: they are the engine's own accumulators,
                        // measured on the monotonic clock, written down every few minutes.
                        checkpoint = PayloadReader.Checkpoint(entry.Payload) ?? checkpoint;
                        break;

                    default:
                        // Traces, clock anomalies and environment records are carried by the
                        // chain and are not part of the queryable index.
                        break;
                }
            }

            store.Flush();

            // A session the chain never closed carries no summary entry, so every total in
            // its row is still the column default. Worked out from the indexed rows instead,
            // or the report prints "Stvarno nadzirano 0,0 s" and "Dostupnost 100 %" over a
            // table of samples and pauses that say otherwise.
            if (!closed && sessionId is not null)
            {
                store.SummariseUnfinishedSession(sessionId, checkpoint);
            }
        }

        var existing = CountIndexRecords(existingDatabasePath);

        return new IndexRebuild(
            targetDatabasePath,
            entries,
            derived,
            existing,
            // Absolute, because the dangerous direction is the one where the chain yields
            // fewer records than the stored index already held: that means the rebuild lost
            // something, and a signed subtraction clamped at zero would report it as no
            // difference at all.
            existing < 0 ? 0 : Math.Abs(derived - existing))
        {
            UnreadableEntries = unreadable,
        };
    }

    /// <summary>
    /// Rebuilds into a temporary file, so an export never writes to the session's own
    /// database - which would make the export a change to the evidence it is exporting.
    /// </summary>
    public static IndexRebuild RebuildForExport(SessionPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var target = Path.Combine(Path.GetTempPath(), $"iem-rebuild-{Guid.NewGuid():N}.db");

        return Rebuild(paths.RawLog, target, paths.Database);
    }

    /// <summary>
    /// Counts the rows in the session's existing index, so any drift can be reported rather
    /// than quietly papered over. Returns -1 when there is no readable database at all.
    /// </summary>
    private static long CountIndexRecords(string? databasePath)
    {
        if (databasePath is null || !File.Exists(databasePath))
        {
            return -1;
        }

        try
        {
            using var reader = SessionReader.Open(databasePath);
            var session = reader.Load();

            return session is null
                ? -1
                : session.SampleCount + session.Incidents.Count + session.Traces.Count + session.Gaps.Count;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // An unreadable index is exactly the case this class exists to survive.
            return -1;
        }
    }
}
