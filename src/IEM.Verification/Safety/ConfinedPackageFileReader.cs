namespace IEM.Verification.Safety;

/// <summary>
/// Centralized, read-only, handle-bound confined package reader ensuring forensic verifier
/// never accesses files outside the package root.
/// Invariant 29: VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT.
/// </summary>
public static class ConfinedPackageFileReader
{
    public enum ReadResultStatus
    {
        Success,
        NotFound,
        Violation
    }

    /// <summary>
    /// Safely opens a read-only FileStream confined strictly within the package root.
    /// Performs lexical and physical containment checks (reparse points, symlinks, junctions).
    /// </summary>
    public static ReadResultStatus TryOpenRead(
        string packageRoot,
        string relativePath,
        out FileStream? stream,
        out string? violationReason)
    {
        stream = null;
        violationReason = null;

        if (!PathSafety.TryResolveSafeRelativePath(packageRoot, relativePath, out var safeFullPath, out violationReason))
        {
            return ReadResultStatus.Violation;
        }

        if (!File.Exists(safeFullPath))
        {
            return ReadResultStatus.NotFound;
        }

        try
        {
            stream = new FileStream(
                safeFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return ReadResultStatus.Success;
        }
        catch (Exception ex)
        {
            violationReason = $"Greška pri otvaranju datoteke '{relativePath}': {ex.Message}";
            return ReadResultStatus.Violation;
        }
    }

    /// <summary>
    /// Safely reads all bytes from a package-owned file strictly confined within the package root.
    /// </summary>
    public static async Task<(ReadResultStatus Status, byte[]? Bytes, string? ViolationReason)> TryReadAllBytesAsync(
        string packageRoot,
        string relativePath,
        CancellationToken ct = default)
    {
        var status = TryOpenRead(packageRoot, relativePath, out var stream, out var violationReason);
        if (status != ReadResultStatus.Success || stream is null)
        {
            return (status, null, violationReason);
        }

        using (stream)
        {
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return (ReadResultStatus.Success, bytes, null);
        }
    }
}
