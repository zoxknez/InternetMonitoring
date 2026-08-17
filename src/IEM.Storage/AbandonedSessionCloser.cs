using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Storage.Evidence;

namespace IEM.Storage;

/// <summary>
/// Closes a session that was interrupted and whose planned window then ran out.
/// <para>
/// Without this, a test cut short by a power cut on its last evening would be left
/// permanently open: no closing entry, no totals, and the report generator refusing to
/// treat it as finished. The evidence is all there and it deserves to end up as a
/// finished package rather than being quietly stranded.
/// </para>
/// <para>
/// The closing entry is written from totals reconstructed out of the chain itself, so a
/// session closed this way asserts nothing that was not already recorded.
/// </para>
/// </summary>
public static class AbandonedSessionCloser
{
    /// <summary>
    /// Appends a closing entry using totals reconstructed from the log.
    /// Does nothing if the session is already closed.
    /// </summary>
    /// <returns>True if a closing entry was written.</returns>
    public static bool Close(SessionPaths paths, DateTimeOffset endedUtc)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!File.Exists(paths.RawLog) || ChainReader.IsClosed(paths.RawLog))
        {
            return false;
        }

        // Analysing at the recorded end rather than at "now" keeps the closing totals to
        // what was actually observed. Using the current time would fold the entire span
        // since the machine died into the session as though it had been watched.
        var analysis = SessionResumeAnalyzer.Analyze(paths, endedUtc);
        var prior = analysis.Context?.Prior ?? PriorTotals.None;

        // An outage still in progress when the process died. Its failing samples are in the
        // chain, but the segment that would summarise them is only written when it closes -
        // and nothing was left running to close it. Rebuilt here and cut at the last observed
        // sample, exactly where the engine cuts one on resume: the outage reaches the totals,
        // the incident table and the report, instead of sitting in the log uncounted.
        IncidentRecord? trailing = null;

        if (analysis.Context?.OpenIncident is { } open)
        {
            // All states in the open run are outages; the worst is the most severe, ties
            // broken by first appearance so the choice is reproducible from the chain.
            var worst = open.StatesSeen.OrderByDescending(state => state.SeverityOf()).First();

            trailing = new IncidentRecord
            {
                Number = prior.IncidentCount + 1,
                CorrelationId = Guid.NewGuid(),
                LastGood = open.LastGood,
                FirstBad = open.FirstBad,
                LastBad = open.LastBad,

                // Observation stopped here, so nothing after this moment may be claimed.
                GapStartedAt = open.LastBad.Monotonic,

                WorstState = worst,
                StatesSeen = open.StatesSeen,
                SampleCount = open.SampleCount,
                TechnicalDetail = open.TechnicalDetail,

                // No live probes ran to gather evidence around this segment, and the score
                // says so: an empty evidence set yields a low-coverage verdict rather than a
                // confidence nothing observed supports.
                Confidence = ConfidenceScorer.Score(worst, new IncidentEvidence()),
            };
        }

        var statistics = new SessionStatistics(prior);

        if (trailing is not null)
        {
            statistics.RecordIncident(trailing);
        }

        var payload = SessionEndPayload.From(statistics, endedUtc);

        using (var chain = HashChainWriter.Open(paths.RawLog))
        {
            if (trailing is not null)
            {
                chain.Append(IncidentPayload.From(trailing));
            }

            chain.Append(payload);
            chain.FlushToDisk();
        }

        if (File.Exists(paths.Database))
        {
            using var store = SqliteSessionStore.Open(paths.Database);
            var sessionId = ChainReader.ReadSessionStart(paths.RawLog)?.SessionId;

            if (!string.IsNullOrEmpty(sessionId))
            {
                if (trailing is not null)
                {
                    store.AddIncident(sessionId, IncidentPayload.From(trailing));
                }

                store.CompleteSession(sessionId, payload);
            }
        }

        return true;
    }
}
