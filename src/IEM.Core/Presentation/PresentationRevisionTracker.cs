namespace IEM.Core.Presentation;

/// <summary>
/// Thread-safe manager ensuring the UI always consumes monotonically increasing presentation snapshots.
/// Invariant 168: STALE_PRESENTATION_SNAPSHOT_NEVER_OVERRIDES_A_NEWER_REVISION.
/// </summary>
public sealed class PresentationRevisionTracker
{
    private readonly object _lock = new();
    private PresentationSnapshot? _currentSnapshot;

    public PresentationSnapshot? CurrentSnapshot
    {
        get
        {
            lock (_lock)
            {
                return _currentSnapshot;
            }
        }
    }

    public long CurrentRevision
    {
        get
        {
            lock (_lock)
            {
                return _currentSnapshot?.AnalysisRevision ?? 0;
            }
        }
    }

    public event Action<PresentationSnapshot>? SnapshotUpdated;

    public bool TryApplySnapshot(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            if (_currentSnapshot != null && snapshot.AnalysisRevision < _currentSnapshot.AnalysisRevision)
            {
                // Discard stale out-of-order snapshot
                return false;
            }

            _currentSnapshot = snapshot;
        }

        SnapshotUpdated?.Invoke(snapshot);
        return true;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _currentSnapshot = null;
        }
    }
}
