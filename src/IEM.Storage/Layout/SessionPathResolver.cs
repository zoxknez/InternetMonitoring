using System.Diagnostics.CodeAnalysis;

namespace IEM.Storage.Layout;

public enum SessionStorageLifecycle
{
    Provisioning,
    Active,
    Sealing,
    Sealed,
}

/// <summary>
/// Centralized path resolver and access boundary enforcement for a session directory.
/// Invariants:
/// 70. POST_SIGNATURE_WRITES_ARE_LIMITED_TO_EXPLICIT_ENVELOPE_ARTIFACTS
/// 71. AUTHORITATIVE_RAW_AND_DERIVED_ARTIFACTS_ARE_APPEND_ONLY_UNTIL_SEAL
/// 72. SEALED_PROTECTED_ARTIFACTS_ARE_NEVER_MUTATED_IN_PLACE
/// 74. EXPORTS_NEVER_AFFECT_EVIDENCE_INTEGRITY
/// 75. USER_WRITABLE_EXPORTS_ARE_NEVER_TRUSTED_AS_EVIDENCE_INPUT
/// 78. PROTECTED_ARTIFACT_PATH_NEVER_ESCAPES_SESSION_ROOT
/// 79. PUBLISHED_PROTECTED_ARTIFACT_IS_COMPLETE_OR_ABSENT
/// </summary>
public sealed class SessionPathResolver
{
    private readonly string _sessionRoot;
    private readonly SessionLayoutDescriptor _layout;
    private SessionStorageLifecycle _lifecycle = SessionStorageLifecycle.Provisioning;

    public SessionPathResolver(string sessionRoot, SessionLayoutDescriptor? layout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        _sessionRoot = Path.GetFullPath(sessionRoot);
        _layout = layout ?? SessionLayoutDescriptor.CreateStandard(Path.GetFileName(_sessionRoot));
    }

    public string SessionRoot => _sessionRoot;
    public SessionLayoutDescriptor Layout => _layout;
    public SessionStorageLifecycle Lifecycle => _lifecycle;

    public void TransitionTo(SessionStorageLifecycle newLifecycle)
    {
        _lifecycle = newLifecycle;
    }

    public string GetAreaFullPath(StorageAreaPolicy area)
    {
        return Path.Combine(_sessionRoot, area.RelativeRoot.Replace('/', Path.DirectorySeparatorChar));
    }

    public string GetRawFullPath(string relativePath) => ResolveSafePath(Path.Combine(_layout.RawRelativePath, relativePath));
    public string GetDerivedFullPath(string relativePath) => ResolveSafePath(Path.Combine(_layout.DerivedRelativePath, relativePath));
    public string GetEvidenceFullPath(string relativePath) => ResolveSafePath(Path.Combine(_layout.EvidenceRelativePath, relativePath));
    public string GetExportsFullPath(string relativePath) => ResolveSafePath(Path.Combine(_layout.ExportsRelativePath, relativePath));

    /// <summary>
    /// Validates and resolves a relative path safely inside the session root.
    /// Invariant 78: PROTECTED_ARTIFACT_PATH_NEVER_ESCAPES_SESSION_ROOT.
    /// </summary>
    public bool TryResolveSafePath(string relativePath, [NotNullWhen(true)] out string? safeFullPath, out string? violation)
    {
        safeFullPath = null;
        violation = null;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            violation = "Putanja je prazna.";
            return false;
        }

        if (relativePath.Contains('\0'))
        {
            violation = "Putanja sadrži zabranjeni NUL bajt.";
            return false;
        }

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\') || (relativePath.Length >= 2 && relativePath[1] == ':'))
        {
            violation = $"Apsolutne putanje su zabranjene: {relativePath}";
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Any(s => s == ".." || s == "."))
        {
            violation = $"Putanja sadrži '..' ili '.' segmente: {relativePath}";
            return false;
        }

        var combined = Path.Combine(_sessionRoot, Path.Combine(segments));
        var full = Path.GetFullPath(combined);

        var rootWithSep = _sessionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _sessionRoot
            : _sessionRoot + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, _sessionRoot, StringComparison.OrdinalIgnoreCase))
        {
            violation = $"Putanja izlazi van korena sesije: {relativePath}";
            return false;
        }

        safeFullPath = full;
        return true;
    }

    public string ResolveSafePath(string relativePath)
    {
        if (!TryResolveSafePath(relativePath, out var safeFullPath, out var violation))
        {
            throw new InvalidOperationException($"Bezbednosno narušavanje putanje: {violation}");
        }
        return safeFullPath;
    }

    /// <summary>
    /// Validates write permission against current lifecycle and storage area.
    /// </summary>
    public bool CanWrite(StorageAreaPolicy area, string relativeArtifactPath)
    {
        // Exports is always writable (user mutable)
        if (area.Role == ArtifactRole.Export)
        {
            return true;
        }

        return _lifecycle switch
        {
            SessionStorageLifecycle.Provisioning => true,
            SessionStorageLifecycle.Active => area.Role is ArtifactRole.RawEvidence or ArtifactRole.DerivedEvidence,
            SessionStorageLifecycle.Sealing => area.Role == ArtifactRole.IntegrityEnvelope,
            // Invariant 70: Post-signature writes limited to timestamp envelope artifacts
            SessionStorageLifecycle.Sealed => area.Role == ArtifactRole.IntegrityEnvelope &&
                                              (relativeArtifactPath.Contains("timestamp.tsr", StringComparison.OrdinalIgnoreCase) ||
                                               relativeArtifactPath.Contains("timestamp.tsq", StringComparison.OrdinalIgnoreCase) ||
                                               relativeArtifactPath.Contains("validation", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }
}
